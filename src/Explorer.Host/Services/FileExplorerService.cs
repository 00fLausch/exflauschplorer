using Microsoft.AspNetCore.StaticFiles;
using System.Collections.Concurrent;
using Explorer.Core.Models;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

public sealed class FileExplorerService
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultListingTimeout = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ListingGates = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumSnapshots = 8;
    private readonly PathAccessPolicy _policy;
    private readonly TimeSpan _listingTimeout;
    private readonly Func<string, IEnumerable<string>> _enumerateEntries;
    private readonly FolderSizeService? _folderSizes;
    private readonly object _snapshotGate = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);

    public FileExplorerService(
        PathAccessPolicy policy,
        TimeSpan? listingTimeout = null,
        Func<string, IEnumerable<string>>? enumerateEntries = null,
        FolderSizeService? folderSizes = null)
    {
        _policy = policy;
        _listingTimeout = listingTimeout ?? DefaultListingTimeout;
        if (_listingTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(listingTimeout));
        _enumerateEntries = enumerateEntries ?? (folder => Directory.EnumerateFileSystemEntries(folder));
        _folderSizes = folderSizes;
    }

    public async Task<FolderPageDto> ListAsync(
        string path,
        int skip,
        int pageSize,
        string sort,
        bool descending,
        string? query,
        bool showHidden,
        CancellationToken cancellationToken,
        bool showSystem = false,
        string? extension = null,
        long? minSize = null,
        long? maxSize = null,
        DateTimeOffset? modifiedAfter = null,
        DateTimeOffset? modifiedBefore = null,
        bool refresh = false,
        string kind = "all")
    {
        using var timedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timedCancellation.CancelAfter(_listingTimeout);
        var gate = ListingGates.GetOrAdd(GetListingGateKey(path), static _ => new SemaphoreSlim(1, 1));
        try { await gate.WaitAsync(timedCancellation.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ListingTimeout();
        }

        Task<FolderPageDto> operation;
        try
        {
            operation = Task.Factory.StartNew(() =>
            {
                try
                {
                    return ListCore(path, skip, pageSize, sort, descending, query, showHidden,
                        timedCancellation.Token, showSystem, extension, minSize, maxSize, modifiedAfter, modifiedBefore, refresh, kind);
                }
                finally { gate.Release(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            gate.Release();
            throw;
        }
        try { return await operation.WaitAsync(timedCancellation.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ObserveIfAbandoned(operation);
            throw ListingTimeout();
        }
        catch (OperationCanceledException)
        {
            ObserveIfAbandoned(operation);
            throw;
        }
    }

    private static TimeoutException ListingTimeout() =>
        new("Das Laufwerk antwortet nicht rechtzeitig. Prüfe die Verbindung und versuche es erneut.");

    private static string GetListingGateKey(string path)
    {
        var candidate = path.Trim();
        if (candidate.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            candidate = "\\\\" + candidate[8..];
        else if (candidate.StartsWith("\\\\?\\", StringComparison.Ordinal))
            candidate = candidate[4..];
        try { return Path.GetPathRoot(candidate) ?? string.Empty; }
        catch (ArgumentException) { return string.Empty; }
    }

    private FolderPageDto ListCore(
        string path,
        int skip,
        int pageSize,
        string sort,
        bool descending,
        string? query,
        bool showHidden,
        CancellationToken cancellationToken,
        bool showSystem,
        string? extension,
        long? minSize,
        long? maxSize,
        DateTimeOffset? modifiedAfter,
        DateTimeOffset? modifiedBefore,
        bool refresh,
        string kind)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = _policy.Authorize(path);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();

        skip = Math.Max(0, skip);
        pageSize = Math.Clamp(pageSize, 1, 500);
        sort = string.IsNullOrWhiteSpace(sort) ? "name" : sort.Trim().ToLowerInvariant();
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        extension = string.IsNullOrWhiteSpace(extension) ? null : extension.Trim().TrimStart('.');
        kind = string.IsNullOrWhiteSpace(kind) ? "all" : kind.Trim().ToLowerInvariant();

        if (refresh) Invalidate(folder);
        var key = MakeCacheKey(folder, sort, descending, query, showHidden, showSystem,
            extension, minSize, maxSize, modifiedAfter, modifiedBefore, kind);
        var entries = GetSnapshot(key);
        if (entries is null)
        {
            entries = BuildSnapshot(folder, sort, descending, query, showHidden, showSystem,
                extension, minSize, maxSize, modifiedAfter, modifiedBefore, kind, _enumerateEntries, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StoreSnapshot(key, folder, entries);
        }

        var page = entries.Skip(skip).Take(pageSize).ToArray();
        return new FolderPageDto(folder, page, skip, pageSize, entries.Count, skip + page.Length < entries.Count);
    }

    private static void ObserveIfAbandoned(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }
        _ = task.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Invalidates every cached view of a folder after a watcher event or explicit refresh.</summary>
    public void Invalidate(string path)
    {
        string folder;
        try { folder = PathAccessPolicy.NormalizeAbsolute(path); }
        catch (ArgumentException) { return; }

        lock (_snapshotGate)
        {
            foreach (var key in _snapshots.Where(item => string.Equals(item.Value.Folder, folder, StringComparison.OrdinalIgnoreCase))
                         .Select(item => item.Key).ToArray())
                _snapshots.Remove(key);
        }
    }

    private IReadOnlyList<FileEntryDto> BuildSnapshot(string folder, string sort, bool descending, string? query,
        bool showHidden, bool showSystem, string? extension, long? minSize, long? maxSize,
        DateTimeOffset? modifiedAfter, DateTimeOffset? modifiedBefore, string kind,
        Func<string, IEnumerable<string>> enumerateEntries, CancellationToken cancellationToken)
    {
        var entries = new List<FileEntryDto>();
        foreach (var itemPath in enumerateEntries(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(itemPath);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(itemPath); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isShortcut = !isDirectory && Path.GetExtension(name).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            var hidden = (attributes & FileAttributes.Hidden) != 0;
            var system = (attributes & FileAttributes.System) != 0;
            if (!showHidden && hidden || !showSystem && system) continue;
            if (kind == "folders" && !isDirectory || kind != "all" && kind != "folders" && (isDirectory || !MatchesKind(kind, name))) continue;
            if (query is not null && !name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;

            long? size = null;
            DateTimeOffset modified;
            DateTimeOffset created;
            try
            {
                if (isDirectory)
                {
                    var info = new DirectoryInfo(itemPath);
                    modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    created = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
                }
                else
                {
                    var info = new FileInfo(itemPath);
                    size = info.Length;
                    modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    created = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
                }
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (modifiedAfter.HasValue && modified < modifiedAfter.Value || modifiedBefore.HasValue && modified > modifiedBefore.Value) continue;
            if (!isDirectory && extension is not null && !Path.GetExtension(name).Equals("." + extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (!isDirectory && minSize.HasValue && size < minSize || !isDirectory && maxSize.HasValue && size > maxSize) continue;

            var fileExtension = isDirectory ? "" : Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            if (!ContentTypes.TryGetContentType(name, out var contentType)) contentType = "application/octet-stream";
            // Windows also uses the directory ReadOnly bit for Explorer folder
            // customization (for example Desktop); it does not deny writes.
            var readOnly = isDirectory ? !_policy.IsAllowed(itemPath, write: true) : (attributes & FileAttributes.ReadOnly) != 0;
            entries.Add(new FileEntryDto(name, itemPath, isDirectory, size, modified, created, fileExtension, contentType,
                hidden, readOnly, isShortcut || (attributes & FileAttributes.ReparsePoint) != 0, isShortcut));
        }

        if (sort == "folder-size" && _folderSizes?.GetReady(folder) is { } totals)
            entries = entries.Select(entry => entry.IsDirectory && totals.Folders.TryGetValue(entry.Path, out var value)
                ? entry with { Size = value.Bytes, SizeIncomplete = value.Incomplete } : entry).ToList();
        return Order(entries, sort, descending).ToArray();
    }

    private IReadOnlyList<FileEntryDto>? GetSnapshot(string key)
    {
        lock (_snapshotGate)
        {
            if (!_snapshots.TryGetValue(key, out var snapshot)) return null;
            if (DateTimeOffset.UtcNow - snapshot.CreatedAt > SnapshotLifetime)
            {
                _snapshots.Remove(key);
                return null;
            }
            snapshot.LastAccess = DateTimeOffset.UtcNow;
            return snapshot.Entries;
        }
    }

    private void StoreSnapshot(string key, string folder, IReadOnlyList<FileEntryDto> entries)
    {
        lock (_snapshotGate)
        {
            var now = DateTimeOffset.UtcNow;
            _snapshots[key] = new Snapshot(folder, entries, now, now);
            foreach (var expired in _snapshots.Where(item => now - item.Value.CreatedAt > SnapshotLifetime)
                         .Select(item => item.Key).ToArray())
                _snapshots.Remove(expired);
            while (_snapshots.Count > MaximumSnapshots)
            {
                var oldest = _snapshots.MinBy(item => item.Value.LastAccess).Key;
                _snapshots.Remove(oldest);
            }
        }
    }

    private static string MakeCacheKey(string folder, string sort, bool descending, string? query,
        bool showHidden, bool showSystem, string? extension, long? minSize, long? maxSize,
        DateTimeOffset? modifiedAfter, DateTimeOffset? modifiedBefore, string kind)
    {
        var values = new string?[]
        {
            folder.ToUpperInvariant(), sort, descending.ToString(), query, showHidden.ToString(), showSystem.ToString(),
            extension?.ToUpperInvariant(), minSize?.ToString(), maxSize?.ToString(),
            modifiedAfter?.UtcTicks.ToString(), modifiedBefore?.UtcTicks.ToString(), kind
        };
        return string.Concat(values.Select(value => $"{value?.Length ?? -1}:{value}"));
    }

    private static bool MatchesKind(string kind, string name)
    {
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return kind switch
        {
            "images" => IsOneOf(extension, "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "heic", "heif", "tif", "tiff", "avif"),
            "videos" => IsOneOf(extension, "mp4", "mov", "m4v", "mkv", "avi", "webm", "wmv", "mpeg", "mpg"),
            "audio" => IsOneOf(extension, "mp3", "m4a", "wav", "flac", "aac", "ogg", "wma", "opus"),
            "archives" => IsOneOf(extension, "zip", "7z", "rar", "tar", "gz", "bz2"),
            "code" => IsOneOf(extension, "js", "jsx", "ts", "tsx", "css", "html", "htm", "json", "xml", "yml", "yaml", "py", "cs", "go", "rs", "java", "sh", "ps1", "md", "txt", "log", "ini", "toml", "sql"),
            "documents" => IsOneOf(extension, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "rtf"),
            _ => true
        };
    }

    private static bool IsOneOf(string value, params string[] candidates) => candidates.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<FileEntryDto> Order(List<FileEntryDto> entries, string sort, bool descending)
    {
        var directories = entries.Where(item => item.IsDirectory);
        var files = entries.Where(item => !item.IsDirectory);
        IOrderedEnumerable<FileEntryDto> OrderGroup(IEnumerable<FileEntryDto> group) => sort switch
        {
            "date" or "modified" => descending
                ? group.OrderByDescending(item => item.Modified).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                : group.OrderBy(item => item.Modified).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "folder-size" => descending
                ? group.OrderBy(item => item.IsDirectory && item.SizeIncomplete != false).ThenByDescending(item => item.Size ?? long.MinValue).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                : group.OrderBy(item => item.IsDirectory && item.SizeIncomplete != false).ThenBy(item => item.Size ?? long.MaxValue).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "size" => descending
                ? group.OrderByDescending(item => item.Size ?? long.MinValue).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                : group.OrderBy(item => item.Size ?? long.MinValue).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "type" => descending
                ? group.OrderByDescending(item => item.Extension, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                : group.OrderBy(item => item.Extension, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => descending
                ? group.OrderByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                : group.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        return OrderGroup(directories).Concat(OrderGroup(files));
    }

    private sealed class Snapshot(string folder, IReadOnlyList<FileEntryDto> entries, DateTimeOffset createdAt, DateTimeOffset lastAccess)
    {
        public string Folder { get; } = folder;
        public IReadOnlyList<FileEntryDto> Entries { get; } = entries;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset LastAccess { get; set; } = lastAccess;
    }

    public string AuthorizeFile(string path) => _policy.Authorize(path);
    public string AuthorizeWrite(string path) => _policy.Authorize(path, write: true);
    public PathAccessPolicy Policy => _policy;
}
