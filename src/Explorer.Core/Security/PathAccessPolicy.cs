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

    public PathAccessPolicy(IEnumerable<ShareRoot> roots)
    {
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

    public string Authorize(string path, bool write = false, bool resolveLinks = true)
    {
        var roots = Roots;
        var fullPath = NormalizeAbsolute(path);
        var root = FindRoot(roots, fullPath) ?? throw new UnauthorizedAccessException("Der Pfad liegt außerhalb der freigegebenen Ordner.");
        if (write && root.ReadOnly)
            throw new UnauthorizedAccessException("Dieser Ordner ist schreibgeschützt freigegeben.");

        if (!resolveLinks)
            return fullPath;

        var resolved = ResolveExistingLinks(fullPath, root, roots);
        var resolvedRoot = FindRoot(roots, resolved) ?? throw new UnauthorizedAccessException("Der Pfad führt über eine Verknüpfung aus den freigegebenen Ordnern heraus.");
        if (write && resolvedRoot.ReadOnly)
            throw new UnauthorizedAccessException("Das Ziel dieser Verknüpfung ist schreibgeschützt.");
        return resolved;
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

        var candidate = path.Trim();
        if (candidate.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            candidate = "\\\\" + candidate[8..];
        else if (candidate.StartsWith("\\\\?\\", StringComparison.Ordinal))
            candidate = candidate[4..];

        var fullPath = ExpandShortPathNames(Path.GetFullPath(candidate));
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

    private string ResolveExistingLinks(string fullPath, ShareRoot lexicalRoot, IReadOnlyList<ShareRoot> roots)
    {
        var relative = Path.GetRelativePath(lexicalRoot.Path, fullPath);
        if (relative == ".")
            return fullPath;

        var current = lexicalRoot.Path;
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
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
        foreach (var segment in fullPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = segment.Split('.')[0].TrimEnd(' ', '.');
            if (ReservedNames.Contains(stem))
                throw new ArgumentException($"„{stem}“ ist ein reservierter Windows-Name.", nameof(fullPath));
        }
    }
}
