using System.Reflection;
using System.Runtime.InteropServices;
using Explorer.Core.Models;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

public interface IQuickAccessFolderSource
{
    IReadOnlyList<string> GetFolderPaths(CancellationToken cancellationToken);
}

/// <summary>Reads filesystem folders from the current user's Windows Explorer Quick Access shell namespace.</summary>
public sealed class WindowsShellQuickAccessFolderSource : IQuickAccessFolderSource
{
    // This Shell namespace identifier is used by Windows Explorer for Quick Access/Home.
    private const string QuickAccessNamespace = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
    private const int MaximumItems = 256;

    public IReadOnlyList<string> GetFolderPaths(CancellationToken cancellationToken)
    {
        object? shell = null;
        object? folder = null;
        object? items = null;
        var paths = new List<string>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: false)
                ?? throw new InvalidOperationException("Der Windows-Schnellzugriff ist nicht verfügbar.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Der Windows-Schnellzugriff konnte nicht geöffnet werden.");
            folder = Invoke(shell, "NameSpace", QuickAccessNamespace)
                ?? throw new InvalidOperationException("Der Windows-Schnellzugriff ist nicht verfügbar.");
            items = Invoke(folder, "Items")
                ?? throw new InvalidOperationException("Die Schnellzugriff-Ordner konnten nicht gelesen werden.");
            var count = Convert.ToInt32(Get(items, "Count"), System.Globalization.CultureInfo.InvariantCulture);
            for (var index = 0; index < Math.Min(count, MaximumItems); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = Invoke(items, "Item", index);
                    if (item is null || !Convert.ToBoolean(Get(item, "IsFolder"), System.Globalization.CultureInfo.InvariantCulture) ||
                        !Convert.ToBoolean(Get(item, "IsFileSystem"), System.Globalization.CultureInfo.InvariantCulture)) continue;
                    var path = Get(item, "Path") as string;
                    if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                }
                finally { Release(item); }
            }
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new IOException("Windows konnte den Schnellzugriff nicht lesen.", exception.InnerException);
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }

        return paths;
    }

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            binder: null, target, arguments, System.Globalization.CultureInfo.InvariantCulture);

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            binder: null, target, args: null, System.Globalization.CultureInfo.InvariantCulture);

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}

/// <summary>Applies the configured share policy before exposing Windows Quick Access folders.</summary>
public sealed class WindowsQuickAccessService(PathAccessPolicy policy, IQuickAccessFolderSource source)
{
    private const int MaximumResults = 128;

    public Task<IReadOnlyList<FileEntryDto>> GetAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        IReadOnlyList<string> candidates;
        try { candidates = source.GetFolderPaths(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                                           InvalidOperationException or NotSupportedException or COMException)
        {
            return (IReadOnlyList<FileEntryDto>)[];
        }

        var result = new List<FileEntryDto>(Math.Min(candidates.Count, MaximumResults));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var path = policy.Authorize(candidate);
                if (!Directory.Exists(path) || !seen.Add(path)) continue;
                var attributes = File.GetAttributes(path);
                var info = new DirectoryInfo(path);
                var name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)) name = path;
                result.Add(new FileEntryDto(name, path, true, null,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                    "", "inode/directory", (attributes & FileAttributes.Hidden) != 0,
                    !policy.IsAllowed(path, write: true),
                    (attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null));
                if (result.Count == MaximumResults) break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                                               NotSupportedException)
            {
                // Unavailable and unshared folders are omitted from the mobile view.
            }
        }

        return (IReadOnlyList<FileEntryDto>)result;
    }, cancellationToken);
}
