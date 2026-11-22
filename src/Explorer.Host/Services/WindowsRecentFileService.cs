using System.Runtime.InteropServices;
using Explorer.Core.Models;

namespace Explorer.Host.Services;

/// <summary>Reads the current Windows user's Recent shortcuts and exposes only targets inside configured shares.</summary>
public sealed class WindowsRecentFileService(ShortcutService shortcuts, string? recentDirectory = null)
{
    private const int MaximumShortcutScan = 500;
    private const int MaximumResults = 100;

    public async Task<IReadOnlyList<FileEntryDto>> GetAsync(CancellationToken cancellationToken)
    {
        var directory = string.IsNullOrWhiteSpace(recentDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Recent)
            : recentDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return [];

        string[] candidates;
        try
        {
            candidates = Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Modified = File.GetLastWriteTimeUtc(path) })
                .OrderByDescending(item => item.Modified)
                .Take(MaximumShortcutScan)
                .Select(item => item.Path)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        var results = new List<FileEntryDto>(Math.Min(candidates.Length, MaximumResults));
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var target = await shortcuts.ResolveRecentAsync(candidate, directory, cancellationToken);
                if (!seenPaths.Add(target.Path)) continue;
                results.Add(new FileEntryDto(target.Name, target.Path, target.IsDirectory, target.Size,
                    target.Modified, target.Created, target.Extension, target.ContentType, target.IsHidden,
                    target.IsReadOnly, target.IsLink, target.IsShortcut));
                if (results.Count == MaximumResults) break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                                               InvalidOperationException or NotSupportedException or COMException)
            {
                // Broken links and targets outside the configured shares are intentionally omitted.
            }
        }

        return results;
    }
}
