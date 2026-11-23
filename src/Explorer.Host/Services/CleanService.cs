using System.Diagnostics;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

public sealed record CleanLocation(string Id, string Title, string Description, string Root, string Mode);
public sealed record CleanItem(string Id, string Category, string Path, long Bytes, DateTime LastWriteUtc);
public sealed record CleanCategory(string Id, string Title, string Description, bool Exists, bool Truncated, IReadOnlyList<CleanItem> Items);
public sealed record CleanScanDto(string Id, DateTime CreatedUtc, IReadOnlyList<CleanCategory> Categories);
public sealed record CleanDeleteRequest(string ScanId, IReadOnlyList<string> ItemIds);
public sealed record CleanDeleteResult(int Deleted, int Skipped, long BytesFreed);

/// <summary>Explicit, expiring file-level cleanup previews. Never follows reparse points or deletes directories.</summary>
public sealed class CleanService
{
    private const int MaxItemsPerCategory = 1000;
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(48);
    private static readonly TimeSpan ScanLifetime = TimeSpan.FromMinutes(20);
    private readonly PathAccessPolicy _policy;
    private readonly IReadOnlyList<CleanLocation> _locations;
    private readonly IRecycleBinService? _recycleBin;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Owner, CleanScanDto Scan)> _scans = new();

    public CleanService(PathAccessPolicy policy, IReadOnlyList<CleanLocation>? locations = null, IRecycleBinService? recycleBin = null)
    {
        _policy = policy;
        _locations = locations ?? DefaultLocations();
        _recycleBin = recycleBin;
    }

    public async Task<CleanScanDto> ScanAsync(string owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new UnauthorizedAccessException();
        var scan = await Task.Run(() => Scan(cancellationToken), cancellationToken);
        lock (_gate)
        {
            foreach (var expired in _scans.Where(x => DateTime.UtcNow - x.Value.Scan.CreatedUtc > ScanLifetime).Select(x => x.Key).ToArray()) _scans.Remove(expired);
            while (_scans.Count >= 8) _scans.Remove(_scans.MinBy(x => x.Value.Scan.CreatedUtc).Key);
            _scans[scan.Id] = (owner, scan);
        }
        return scan;
    }

    private CleanScanDto Scan(CancellationToken token)
    {
        var categories = new List<CleanCategory>();
        foreach (var location in _locations)
        {
            token.ThrowIfCancellationRequested();
            var items = new List<CleanItem>();
            var truncated = false;
            var exists = Directory.Exists(location.Root) && _policy.IsAllowed(location.Root, write: true);
            var timer = Stopwatch.StartNew();
            if (exists)
            {
                try
                {
                    if (IsReparse(location.Root)) truncated = true;
                    else if (location.Mode == "hf")
                    {
                        foreach (var model in Directory.EnumerateDirectories(location.Root, "models--*"))
                        {
                            token.ThrowIfCancellationRequested();
                            if (timer.Elapsed > TimeSpan.FromSeconds(5) || items.Count >= MaxItemsPerCategory) { truncated = true; break; }
                            if (IsReparse(model)) continue;
                            var blobs = Path.Combine(model, "blobs");
                            if (Directory.Exists(blobs) && !IsReparse(blobs)) ScanFiles(blobs, location, items, token, timer, ref truncated, false);
                        }
                    }
                    else ScanFiles(location.Root, location, items, token, timer, ref truncated, location.Mode != "downloads");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { truncated = true; }
            }
            categories.Add(new CleanCategory(location.Id, location.Title, location.Description, exists, truncated,
                items.OrderByDescending(item => item.Bytes).ToArray()));
        }
        return new CleanScanDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, categories);
    }

    private void ScanFiles(string root, CleanLocation location, List<CleanItem> items, CancellationToken token, Stopwatch timer, ref bool truncated, bool recurse)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(5) || items.Count >= MaxItemsPerCategory) { truncated = true; return; }
            try
            {
                if (IsReparse(item.Path)) { truncated = true; continue; }
                foreach (var path in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (timer.Elapsed > TimeSpan.FromSeconds(5) || items.Count >= MaxItemsPerCategory) { truncated = true; return; }
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var name = Path.GetFileName(path);
                            if (recurse && (location.Mode != "pycache" || item.Depth < 3) &&
                                !(location.Mode == "pycache" && name is "node_modules" or ".git" or ".venv" or "venv")) pending.Push((path, item.Depth + 1));
                            continue;
                        }
                        if (location.Mode == "hf" && !path.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase)) continue;
                        if (location.Mode == "downloads" && !new[] { ".crdownload", ".part", ".partial" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                        if (location.Mode == "pycache" && (!Path.GetDirectoryName(path)!.EndsWith("__pycache__", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))) continue;
                        if (Path.GetFileName(path).StartsWith(".wme-", StringComparison.OrdinalIgnoreCase)) continue;
                        var info = new FileInfo(path);
                        if (info.LastWriteTimeUtc > DateTime.UtcNow - MinimumAge || !_policy.IsAllowed(path, write: true)) continue;
                        items.Add(new CleanItem(Guid.NewGuid().ToString("N"), location.Id, info.FullName, info.Length, info.LastWriteTimeUtc));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { truncated = true; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { truncated = true; }
        }
    }

    internal CleanDeleteResult Delete(string owner, CleanDeleteRequest request) =>
        DeleteAsync(owner, request, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<CleanDeleteResult> DeleteAsync(string owner, CleanDeleteRequest request, CancellationToken token)
    {
        if (request.ItemIds is null || request.ItemIds.Count == 0 || request.ItemIds.Count > 9000) throw new ArgumentException("Bitte bis zu 9000 Dateien auswählen.");
        CleanScanDto scan;
        lock (_gate)
        {
            if (!_scans.TryGetValue(request.ScanId, out var stored) || stored.Owner != owner || DateTime.UtcNow - stored.Scan.CreatedUtc > ScanLifetime)
                throw new ArgumentException("Die Vorschau ist abgelaufen. Bitte erneut prüfen.");
            scan = stored.Scan;
            var scanIds = scan.Categories.SelectMany(category => category.Items).Select(item => item.Id).ToHashSet();
            if (request.ItemIds.Any(id => !scanIds.Contains(id))) throw new ArgumentException("Die Auswahl gehört nicht zu dieser Vorschau.");
            _scans.Remove(request.ScanId);
        }
        var allowed = scan.Categories.SelectMany(category => category.Items).ToDictionary(item => item.Id);
        var ids = request.ItemIds.Distinct().ToArray();
        var deleted = 0; var skipped = 0; long freed = 0;
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            var candidate = allowed[id];
            var location = _locations.First(x => x.Id == candidate.Category);
            try
            {
                var path = _policy.Authorize(candidate.Path, write: true);
                if (Path.GetFileName(path).StartsWith(".wme-", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                if (!string.Equals(path, candidate.Path, StringComparison.OrdinalIgnoreCase) || !IsWithin(location.Root, path) || HasReparseParent(location.Root, path)) { skipped++; continue; }
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.Length != candidate.Bytes || info.LastWriteTimeUtc != candidate.LastWriteUtc || info.LastWriteTimeUtc > DateTime.UtcNow - MinimumAge) { skipped++; continue; }
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    if (stream.Length != candidate.Bytes) { skipped++; continue; }
                if (location.Mode is "downloads" or "temp")
                {
                    if (_recycleBin is null) { skipped++; continue; }
                    await _recycleBin.DeleteAsync(path, token);
                }
                else
                {
                    File.Delete(path);
                    freed += candidate.Bytes;
                }
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { skipped++; }
        }
        return new CleanDeleteResult(deleted, skipped, freed);
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool HasReparseParent(string root, string path)
    {
        var current = Path.GetDirectoryName(path);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null && current.Length >= normalizedRoot.Length)
        {
            if (IsReparse(current)) return true;
            if (string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return false;
            current = Path.GetDirectoryName(current);
        }
        return true;
    }

    private static IReadOnlyList<CleanLocation> DefaultLocations()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var locations = new List<CleanLocation> {
            new CleanLocation("temp", "Temporäre Dateien", "Ältere temporäre Dateien; laufende Programme können sie noch benötigen.", Path.GetTempPath(), "temp"),
            new CleanLocation("pip", "Python pip Cache", "Heruntergeladene Python-Pakete; pip lädt sie bei Bedarf erneut.", Path.Combine(local, "pip", "Cache"), "cache"),
            new CleanLocation("pip-user", "Python pip Cache (Benutzer)", "Zusätzlicher pip-Downloadcache im Benutzerprofil.", Path.Combine(user, ".cache", "pip"), "cache"),
            new CleanLocation("npm", "npm Cache", "Zwischengespeicherte npm-Pakete; werden bei Bedarf erneut geladen.", Path.Combine(local, "npm-cache"), "cache"),
            new CleanLocation("uv", "Python uv Cache", "Zwischengespeicherte Python-Pakete von uv.", Path.Combine(local, "uv", "cache"), "cache"),
            new CleanLocation("downloads", "Unvollständige Downloads", "Alte .part- und .crdownload-Dateien; prüfe, ob ein Download fortgesetzt werden soll.", Path.Combine(user, "Downloads"), "downloads"),
        };
        var hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        var hfHub = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
        if (string.IsNullOrWhiteSpace(hfHub))
            hfHub = Path.Combine(string.IsNullOrWhiteSpace(hfHome) ? Path.Combine(user, ".cache", "huggingface") : hfHome, "hub");
        if (Path.IsPathFullyQualified(hfHub))
            locations.Add(new("hf-user", "Hugging Face Downloads", "Unvollständige Downloads über 48 Stunden; fertige Modelle bleiben erhalten.", hfHub, "hf"));
        var workspace = Environment.GetEnvironmentVariable("WINDOWS_MOBILE_EXPLORER_PYTHON_WORKSPACE");
        if (workspace is not null && Path.IsPathFullyQualified(workspace))
            locations.Add(new("pycache", "Python Bytecode", "Generierte .pyc-Dateien in __pycache__; Python erstellt sie erneut.", workspace, "pycache"));
        return locations;
    }
}
