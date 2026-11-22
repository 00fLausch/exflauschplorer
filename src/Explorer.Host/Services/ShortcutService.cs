using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Explorer.Core.Models;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

/// <summary>Resolves Windows Shell shortcuts without executing their targets.</summary>
public sealed class ShortcutService(PathAccessPolicy policy)
{
    private const int MaximumShortcutDepth = 16;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public Task<ShortcutTargetDto> ResolveAsync(string rawPath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var shortcut = policy.Authorize(rawPath);
        EnsureShortcut(shortcut);
        return ResolveChain(shortcut, cancellationToken);
    }, cancellationToken);

    /// <summary>Reads a link from Windows' own Recent folder, then applies normal share checks to every target.</summary>
    internal Task<ShortcutTargetDto> ResolveRecentAsync(string rawPath, string recentDirectory, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var shortcut = PathAccessPolicy.NormalizeAbsolute(rawPath);
        var recent = PathAccessPolicy.NormalizeAbsolute(recentDirectory);
        if (!IsWithin(recent, shortcut)) throw new UnauthorizedAccessException("Die Verknüpfung liegt nicht im Windows-Ordner „Zuletzt verwendet“.");
        EnsureShortcut(shortcut);
        return ResolveChain(shortcut, cancellationToken, skipInitialAuthorization: true);
    }, cancellationToken);

    private static void EnsureShortcut(string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Die ausgewählte Datei ist keine Windows-Verknüpfung.");
        if (!File.Exists(path)) throw new FileNotFoundException();
    }

    private ShortcutTargetDto ResolveChain(string shortcut, CancellationToken cancellationToken, bool skipInitialAuthorization = false)
    {
        var current = shortcut;
        var trustedInitialShortcut = skipInitialAuthorization;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < MaximumShortcutDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!trustedInitialShortcut) current = policy.Authorize(current);
            trustedInitialShortcut = false;

            if (!Path.GetExtension(current).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return Describe(policy.Authorize(current));
            if (!visited.Add(current)) throw new IOException("Die Verknüpfung enthält einen Verweis auf sich selbst.");

            var target = ReadTarget(current);
            if (string.IsNullOrWhiteSpace(target)) throw new FileNotFoundException("Das Ziel der Verknüpfung ist leer.");
            target = Environment.ExpandEnvironmentVariables(target.Trim());
            if (!Path.IsPathFullyQualified(target))
                target = Path.GetFullPath(target, Path.GetDirectoryName(current)!);
            current = policy.Authorize(target);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new FileNotFoundException("Das Ziel der Verknüpfung wurde nicht gefunden.");
        }

        throw new IOException("Die Verknüpfungskette ist zu lang.");
    }

    private static bool IsWithin(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTarget(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false)
                ?? throw new InvalidOperationException("Die Windows Shell-Verknüpfungsfunktion ist nicht verfügbar.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Die Windows Shell-Verknüpfungsfunktion konnte nicht gestartet werden.");
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            if (shortcut is null) throw new IOException("Die Windows-Verknüpfung konnte nicht gelesen werden.");
            var value = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
            return value as string ?? "";
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new IOException("Die Windows-Verknüpfung konnte nicht gelesen werden.", exception.InnerException);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static ShortcutTargetDto Describe(string path)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        var extension = isDirectory ? "" : Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!ContentTypes.TryGetContentType(path, out var contentType)) contentType = "application/octet-stream";
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;
        return new ShortcutTargetDto(name, path, isDirectory, isDirectory ? null : ((FileInfo)info).Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            extension, contentType, (attributes & FileAttributes.Hidden) != 0, (attributes & FileAttributes.ReadOnly) != 0,
            (attributes & FileAttributes.ReparsePoint) != 0);
    }
}
