using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using System.IO.Enumeration;
using System.Threading.Channels;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.AspNetCore.StaticFiles;
using Explorer.Core.Models;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

public sealed class RecursiveSearchService
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".markdown", ".log", ".csv", ".json", ".xml", ".html", ".htm", ".css", ".js", ".jsx", ".ts", ".tsx", ".cs", ".py", ".ps1", ".sql", ".yaml", ".yml", ".toml", ".ini", ".config" };
    private const int EverythingPageSize = 10_000;
    private readonly PathAccessPolicy policy;
    private readonly IEverythingSearchProvider? everything;

    public RecursiveSearchService(PathAccessPolicy policy, IEverythingSearchProvider? everything = null)
    {
        this.policy = policy;
        this.everything = everything;
    }

    public async IAsyncEnumerable<FileEntryDto> SearchAsync(string rawRoot, string query, bool showHidden, bool searchContent,
        string? extension, long? minSize, long? maxSize, DateTimeOffset? modifiedAfter, DateTimeOffset? modifiedBefore,
        [EnumeratorCancellation] CancellationToken cancellationToken, bool showSystem = false, bool useWindowsSearchIndex = true)
    {
        var root = policy.Authorize(rawRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        var text = query.Trim();
        if (text.Length < 1 || text.Length > 200) throw new ArgumentException("Gib einen Suchbegriff mit 1 bis 200 Zeichen ein.");
        var searchQuery = new SearchQuery(text);
        using var stopSearch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<FileEntryDto>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var worker = Task.Run(async () =>
        {
            try
            {
                await SearchWorkerAsync(root, searchQuery, showHidden, searchContent, extension, minSize, maxSize,
                    modifiedAfter, modifiedBefore, channel.Writer, stopSearch.Token, showSystem, useWindowsSearchIndex);
                channel.Writer.TryComplete();
            }
            catch (Exception exception) { channel.Writer.TryComplete(exception); }
        });

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }
        finally
        {
            await stopSearch.CancelAsync();
            await worker;
        }
    }

    private async Task SearchWorkerAsync(string root, SearchQuery query, bool showHidden, bool searchContent,
        string? extension, long? minSize, long? maxSize, DateTimeOffset? modifiedAfter, DateTimeOffset? modifiedBefore,
        ChannelWriter<FileEntryDto> output, CancellationToken cancellationToken, bool showSystem, bool useWindowsSearchIndex)
    {
        // Explicit file name patterns do not request document-content matches.
        searchContent &= !query.IsPattern;
        if (!searchContent && await TrySearchEverythingAsync(root, query, showHidden, showSystem, extension,
                minSize, maxSize, modifiedAfter, modifiedBefore, output, cancellationToken)) return;
        if (!query.IsPattern && useWindowsSearchIndex && await TrySearchWindowsIndexAsync(root, query.Text, showHidden, showSystem, searchContent, extension, minSize,
                maxSize, modifiedAfter, modifiedBefore, output, cancellationToken)) return;

        var pending = new Stack<string>();
        pending.Push(root);
        var wantedExtension = string.IsNullOrWhiteSpace(extension) ? null : extension.StartsWith('.') ? extension : "." + extension;
        while (pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(folder); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var path in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetSearchablePath(root, path, out _)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (!showHidden && (attributes & FileAttributes.Hidden) != 0 || !showSystem && (attributes & FileAttributes.System) != 0) continue;
                if (isDirectory)
                {
                    if (query.MatchesName(Path.GetFileName(path), isDirectory: true))
                        await output.WriteAsync(CreateEntry(path, true, attributes), cancellationToken);
                    pending.Push(path);
                    continue;
                }

                var name = Path.GetFileName(path);
                var ext = Path.GetExtension(path);
                if (wantedExtension is not null && !ext.Equals(wantedExtension, StringComparison.OrdinalIgnoreCase)) continue;
                FileInfo info;
                try { info = new FileInfo(path); _ = info.Length; }
                catch (IOException) { continue; }
                if (minSize.HasValue && info.Length < minSize || maxSize.HasValue && info.Length > maxSize) continue;
                if (modifiedAfter.HasValue && info.LastWriteTimeUtc < modifiedAfter.Value.UtcDateTime || modifiedBefore.HasValue && info.LastWriteTimeUtc > modifiedBefore.Value.UtcDateTime) continue;
                var matchesName = query.MatchesName(name, isDirectory: false);
                if (!matchesName && searchContent && info.Length <= 4 * 1024 * 1024 && TextExtensions.Contains(ext))
                {
                    try
                    {
                        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (input.Length <= 4 * 1024 * 1024)
                        {
                            using var reader = new StreamReader(input);
                            var content = await reader.ReadToEndAsync(cancellationToken);
                            matchesName = content.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                if (matchesName) await output.WriteAsync(CreateEntry(path, false, attributes), cancellationToken);
            }
        }
    }

    private async Task<bool> TrySearchEverythingAsync(string root, SearchQuery query, bool showHidden, bool showSystem,
        string? extension, long? minSize, long? maxSize, DateTimeOffset? modifiedAfter, DateTimeOffset? modifiedBefore,
        ChannelWriter<FileEntryDto> output, CancellationToken cancellationToken)
    {
        var indexQuery = query.EverythingText;
        if (indexQuery is null || everything?.IsAvailable != true) return false;
        var wantedExtension = string.IsNullOrWhiteSpace(extension) ? null : extension.StartsWith('.') ? extension : "." + extension;
        var offset = 0;
        var emittedAny = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageCount = 0;
                await foreach (var indexedPath in everything.SearchPathsAsync(root, indexQuery, offset, EverythingPageSize, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pageCount++;
                    try
                    {
                        if (!TryGetSearchablePath(root, indexedPath, out var candidate)) continue;
                        var attributes = File.GetAttributes(candidate);
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                            !showHidden && (attributes & FileAttributes.Hidden) != 0 ||
                            !showSystem && (attributes & FileAttributes.System) != 0) continue;

                        var name = Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (!query.MatchesName(name, isDirectory)) continue;
                        if (isDirectory)
                        {
                            await output.WriteAsync(CreateEntry(candidate, true, attributes), cancellationToken);
                            emittedAny = true;
                            continue;
                        }

                        if (wantedExtension is not null && !Path.GetExtension(candidate).Equals(wantedExtension, StringComparison.OrdinalIgnoreCase)) continue;
                        var info = new FileInfo(candidate);
                        if (minSize.HasValue && info.Length < minSize.Value || maxSize.HasValue && info.Length > maxSize.Value ||
                            modifiedAfter.HasValue && info.LastWriteTimeUtc < modifiedAfter.Value.UtcDateTime ||
                            modifiedBefore.HasValue && info.LastWriteTimeUtc > modifiedBefore.Value.UtcDateTime) continue;
                        await output.WriteAsync(CreateEntry(candidate, false, attributes), cancellationToken);
                        emittedAny = true;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
                }

                if (pageCount < EverythingPageSize || offset > int.MaxValue - pageCount) break;
                offset += pageCount;
            }
            return emittedAny;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            // Everything is optional; if its CLI or database is unavailable, keep the filesystem search path.
            return emittedAny;
        }
    }

    private async Task<bool> TrySearchWindowsIndexAsync(string root, string query, bool showHidden, bool showSystem,
        bool searchContent, string? extension, long? minSize, long? maxSize, DateTimeOffset? modifiedAfter,
        DateTimeOffset? modifiedBefore, ChannelWriter<FileEntryDto> output, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var connectionType = Type.GetTypeFromProgID("ADODB.Connection", throwOnError: false);
        if (connectionType is null) return false;

        object? connectionObject = null;
        object? recordsetObject = null;
        var queryExecuted = false;
        var emittedAny = false;
        try
        {
            connectionObject = Activator.CreateInstance(connectionType);
            if (connectionObject is null) return false;
            dynamic connection = connectionObject;
            connection.ConnectionTimeout = 5;
            connection.CommandTimeout = 15;
            connection.Open("Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows';", "", "", 0);

            var scope = EscapeSqlLiteral(new Uri(RemoveLongPathPrefix(Path.GetFullPath(root))).AbsoluteUri);
            recordsetObject = connection.Execute($"SELECT TOP 1 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='{scope}'");
            dynamic probe = recordsetObject!;
            if ((bool)probe.EOF) return false;
            probe.Close();
            ReleaseComObject(recordsetObject);
            recordsetObject = null;

            var namePattern = EscapeLikePattern(query);
            var namePredicate = $"System.ItemNameDisplay LIKE '%{namePattern}%'";
            var predicate = searchContent
                ? $"({namePredicate} OR FREETEXT(System.Search.Contents, '{EscapeSqlLiteral(query)}'))"
                : namePredicate;
            var sql = $"SELECT System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='{scope}' AND {predicate}";
            recordsetObject = connection.Execute(sql);
            queryExecuted = true;
            dynamic results = recordsetObject!;

            var wantedExtension = string.IsNullOrWhiteSpace(extension) ? null : extension.StartsWith('.') ? extension : "." + extension;
            while (!(bool)results.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? itemPath = results.Fields.Item(0).Value;
                if (itemPath is string indexedPath && !string.IsNullOrWhiteSpace(indexedPath))
                {
                    try
                    {
                        if (TryGetSearchablePath(root, indexedPath, out var candidate))
                        {
                            var attributes = File.GetAttributes(candidate);
                            var isDirectory = (attributes & FileAttributes.Directory) != 0;
                            if ((attributes & FileAttributes.ReparsePoint) == 0 &&
                                (showHidden || (attributes & FileAttributes.Hidden) == 0) &&
                                (showSystem || (attributes & FileAttributes.System) == 0))
                            {
                                var name = Path.GetFileName(candidate);
                                var matchesName = name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                                if (isDirectory)
                                {
                                    if (matchesName)
                                    {
                                        await output.WriteAsync(CreateEntry(candidate, true, attributes), cancellationToken);
                                        emittedAny = true;
                                    }
                                }
                                else if (matchesName || searchContent)
                                {
                                    var info = new FileInfo(candidate);
                                    if ((wantedExtension is null || Path.GetExtension(candidate).Equals(wantedExtension, StringComparison.OrdinalIgnoreCase)) &&
                                        (!minSize.HasValue || info.Length >= minSize.Value) &&
                                        (!maxSize.HasValue || info.Length <= maxSize.Value) &&
                                        (!modifiedAfter.HasValue || info.LastWriteTimeUtc >= modifiedAfter.Value.UtcDateTime) &&
                                        (!modifiedBefore.HasValue || info.LastWriteTimeUtc <= modifiedBefore.Value.UtcDateTime))
                                    {
                                        await output.WriteAsync(CreateEntry(candidate, false, attributes), cancellationToken);
                                        emittedAny = true;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
                }
                results.MoveNext();
            }
            // A zero-result index query may simply be racing the Windows indexer for new files.
            return emittedAny;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or TargetInvocationException or InvalidCastException or ArgumentException or InvalidOperationException)
        {
            // If the index query could not be completed, retain the cancellable filesystem fallback.
            return queryExecuted && emittedAny;
        }
        finally
        {
            CloseRecordset(recordsetObject);
            CloseConnection(connectionObject);
        }
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private bool TryGetSearchablePath(string root, string path, out string lexical)
    {
        lexical = "";
        try
        {
            lexical = policy.Authorize(path, resolveLinks: false);
            return IsWithinRoot(root, lexical) && !lexical.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                policy.Authorize(lexical).Equals(lexical, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class SearchQuery
    {
        private readonly string[]? _extensions;
        private readonly bool _wildcard;

        public SearchQuery(string text)
        {
            Text = text;
            if (text.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            {
                var extensions = text[4..].Split(';');
                if (extensions.All(IsExtension)) _extensions = extensions;
            }
            else if (text.StartsWith("*.", StringComparison.Ordinal) && IsExtension(text[2..]))
                _extensions = [text[2..]];
            var safeForIndex = text.All(character =>
                !char.IsControl(character) && character is not ('\\' or '/' or ':' or '"' or '|' or '<' or '>' or '!' or '&'));
            _wildcard = text.IndexOfAny(['*', '?']) >= 0 && safeForIndex;
            if (IsPattern || safeForIndex)
                EverythingText = text.Contains(' ') ? '"' + text + '"' : text;
        }

        public string Text { get; }
        public bool IsPattern => _wildcard || _extensions is not null;
        public string? EverythingText { get; }

        private static bool IsExtension(string value) => value.Length > 0 &&
            value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');

        public bool MatchesName(string name, bool isDirectory)
        {
            if (_extensions is not null)
                return !isDirectory && _extensions.Contains(Path.GetExtension(name).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
            if (_wildcard) return FileSystemName.MatchesSimpleExpression(Text, name);
            return name.Contains(Text, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("]", "[]]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);

    private static bool IsWithinRoot(string root, string candidate)
    {
        var fullRoot = RemoveLongPathPrefix(Path.GetFullPath(root));
        var fullCandidate = RemoveLongPathPrefix(Path.GetFullPath(candidate));
        if (fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveLongPathPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
        path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;

    private static void CloseRecordset(object? value)
    {
        if (value is null) return;
        try { ((dynamic)value).Close(); } catch { }
        ReleaseComObject(value);
    }

    private static void CloseConnection(object? value)
    {
        if (value is null) return;
        try { ((dynamic)value).Close(); } catch { }
        ReleaseComObject(value);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch (InvalidComObjectException) { }
        }
    }

    private static FileEntryDto CreateEntry(string path, bool isDirectory, FileAttributes attributes)
    {
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        var extension = isDirectory ? "" : Path.GetExtension(info.Name).TrimStart('.').ToLowerInvariant();
        if (!ContentTypes.TryGetContentType(info.Name, out var contentType)) contentType = "application/octet-stream";
        return new FileEntryDto(info.Name, path, isDirectory, isDirectory ? null : ((FileInfo)info).Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            extension, contentType, (attributes & FileAttributes.Hidden) != 0, (attributes & FileAttributes.ReadOnly) != 0, false);
    }
}
