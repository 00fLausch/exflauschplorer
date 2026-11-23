using System.Security;
using System.Runtime.InteropServices;

namespace Explorer.Core.Security;

/// <summary>Normalizes Windows paths and enforces configured roots on every access.</summary>
public sealed class PathAccessPolicy
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private IReadOnlyList<ShareRoot> _roots = Array.Empty<ShareRoot>();
    private readonly IReadOnlyList<string> _blockedPaths;

    public PathAccessPolicy(IEnumerable<ShareRoot> roots, IEnumerable<string>? blockedPaths = null)
    {
        _blockedPaths = (blockedPaths ?? []).Select(NormalizeAbsolute).Distinct(PathComparer).ToArray();
        ReplaceRoots(roots);
    }

    public IReadOnlyList<ShareRoot> Roots => Volatile.Read(ref _roots);

    public void ReplaceRoots(IEnumerable<ShareRoot> roots)
    {
        var normalizedRoots = roots
            .Select(root => root with { Path = NormalizeAbsolute(root.Path) })
            .OrderByDescending(root => root.Path.Length)
            .ToArray();
        Volatile.Write(ref _roots, Array.AsReadOnly(normalizedRoots));
    }

    public string Authorize(string path, bool write = false, bool resolveLinks = true, bool allowInternal = false)
    {
        var roots = Roots;
        var fullPath = NormalizeAbsolute(path);
        RejectBlocked(fullPath, allowInternal);
        var root = FindRoot(roots, fullPath) ?? throw new UnauthorizedAccessException("Der Pfad liegt außerhalb der freigegebenen Ordner.");
        if (write && root.ReadOnly)
            throw new UnauthorizedAccessException("Dieser Ordner ist schreibgeschützt freigegeben.");

        if (!resolveLinks)
            return fullPath;

        var resolved = ResolveExistingLinks(fullPath, roots, allowInternal);
        RejectBlocked(resolved, allowInternal);
        var resolvedRoot = FindRoot(roots, resolved) ?? throw new UnauthorizedAccessException("Der Pfad führt über eine Verknüpfung aus den freigegebenen Ordnern heraus.");
        if (write && resolvedRoot.ReadOnly)
            throw new UnauthorizedAccessException("Das Ziel dieser Verknüpfung ist schreibgeschützt.");
        return resolved;
    }

    // Entry mutations act on the final name. Only its parent may be resolved.
    public string AuthorizeEntryMutation(string path, bool allowInternal = false)
    {
        var lexical = Authorize(path, write: true, resolveLinks: false, allowInternal: allowInternal);
        EnsureMutableEntry(lexical);
        var parent = Authorize(Path.GetDirectoryName(lexical)!, write: true);
        var entry = Authorize(Path.Combine(parent, Path.GetFileName(lexical)), write: true, resolveLinks: false, allowInternal: allowInternal);
        EnsureMutableEntry(entry);
        return entry;
    }

    private void EnsureMutableEntry(string path)
    {
        if (PathComparer.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!)) ||
            Roots.Any(root => IsWithin(path, root.Path)) ||
            _blockedPaths.Any(blocked => IsWithin(path, blocked)))
            throw new UnauthorizedAccessException("Laufwerke, Freigabewurzeln und übergeordnete Ordner geschützter Bereiche sind gesperrt.");
    }

    private void RejectBlocked(string path, bool allowInternal)
    {
        if (_blockedPaths.Any(blocked => IsWithin(blocked, path)) ||
            (!allowInternal && path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Any(IsInternalEntryName)))
            throw new UnauthorizedAccessException("Dieser private Windows-Bereich ist für den Datei-Explorer gesperrt.");
    }

    public static bool IsInternalEntryName(string name) => name.StartsWith(".wme-", StringComparison.OrdinalIgnoreCase);

    public static string ValidateEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(['\\', '/', ':', '"', '<', '>', '|', '?', '*']) >= 0 || name.Any(char.IsControl) ||
            IsInternalEntryName(name) || ReservedNames.Contains(name.Split('.')[0].TrimEnd(' ')))
            throw new ArgumentException("Der Name muss ein einzelner gültiger Windows-Datei- oder Ordnername sein.");
        return name;
    }

    public bool IsAllowed(string path, bool write = false)
    {
        try
        {
            _ = Authorize(path, write);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public string ToLongPath(string absolutePath)
    {
        var fullPath = NormalizeAbsolute(absolutePath);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            return "\\\\?\\UNC\\" + fullPath[2..];
        return fullPath.Length >= 248 ? "\\\\?\\" + fullPath : fullPath;
    }

    public static string NormalizeAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            throw new ArgumentException("Bitte gib einen gültigen Pfad ein.", nameof(path));

        var candidate = path;
        if (candidate.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            candidate = "\\\\" + candidate[8..];
        else if (candidate.StartsWith("\\\\?\\", StringComparison.Ordinal))
            candidate = candidate[4..];

        if (!Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Bitte gib einen absoluten Windows-Pfad ein.", nameof(path));
        ValidateSegments(candidate);
        var fullPath = ExpandShortPathNames(Path.GetFullPath(candidate));
        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Windows-Gerätepfade sind gesperrt.", nameof(path));
        ValidateSegments(fullPath);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string ExpandShortPathNames(string fullPath)
    {
        if (!OperatingSystem.IsWindows() || !fullPath.Contains('~')) return fullPath;

        var input = fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
        var buffer = new char[32768];
        var length = GetLongPathNameW(input, buffer, (uint)buffer.Length);
        if (length == 0) return fullPath;
        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            length = GetLongPathNameW(input, buffer, (uint)buffer.Length);
            if (length == 0 || length >= buffer.Length) return fullPath;
        }

        var result = new string(buffer, 0, (int)length);
        if (result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\" + result[8..];
        return result.StartsWith("\\\\?\\", StringComparison.Ordinal) ? result[4..] : result;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);

    private static ShareRoot? FindRoot(IReadOnlyList<ShareRoot> roots, string path) =>
        roots.FirstOrDefault(root => IsWithin(root.Path, path));

    private static bool IsWithin(string root, string path)
    {
        if (PathComparer.Equals(root, path))
            return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveExistingLinks(string fullPath, IReadOnlyList<ShareRoot> roots, bool allowInternal)
    {
        // Walk from the drive/share root so a configured root cannot hide a junction.
        var current = Path.GetPathRoot(fullPath)!;
        var segments = fullPath[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                current = Path.Combine(current, Path.Combine(segments.Skip(index + 1).ToArray()));
                break;
            }
            catch (DirectoryNotFoundException)
            {
                current = Path.Combine(current, Path.Combine(segments.Skip(index + 1).ToArray()));
                break;
            }

            // Expand intermediate 8.3 names even if the final entry does not exist yet.
            current = NormalizeAbsolute(current);
            RejectBlocked(current, allowInternal);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                continue;

            FileSystemInfo linkInfo = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            // Cloud placeholders (including iCloud/OneDrive folders) are reparse
            // points too, but do not redirect the path. ResolveLinkTarget returns
            // null for these; only symbolic links and junctions have a target.
            var target = linkInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
                continue;
            var targetPath = NormalizeAbsolute(target.FullName);
            RejectBlocked(targetPath, allowInternal);
            if (FindRoot(roots, targetPath) is null)
                throw new UnauthorizedAccessException("Der Pfad führt über eine Verknüpfung aus den freigegebenen Ordnern heraus.");

            current = targetPath;
        }

        var resolved = NormalizeAbsolute(current);
        if (FindRoot(roots, resolved) is null)
            throw new UnauthorizedAccessException("Der Pfad führt aus den freigegebenen Ordnern heraus.");
        return resolved;
    }

    private static void ValidateSegments(string fullPath)
    {
        foreach (var segment in fullPath[Path.GetPathRoot(fullPath)!.Length..]
                     .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") continue;
            var stem = segment.Split('.')[0].TrimEnd(' ', '.');
            if (segment.Contains(':') || segment.EndsWith(' ') || segment.EndsWith('.') || ReservedNames.Contains(stem))
                throw new ArgumentException($"„{stem}“ ist ein reservierter Windows-Name.", nameof(fullPath));
        }
    }
}
