using System.Reflection;
using System.Runtime.InteropServices;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Services;

namespace Explorer.Host.Tests;

public sealed class WindowsRecentFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-Recent-{Guid.NewGuid():N}");
    private readonly string _shared;
    private readonly string _recent;

    public WindowsRecentFileServiceTests()
    {
        _shared = Path.Combine(_root, "Freigegeben");
        _recent = Path.Combine(_root, "WindowsRecent");
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_recent);
    }

    [Fact]
    public async Task RecentLinksAreResolvedDeduplicatedAndFilteredBySharePolicy()
    {
        var allowedTarget = Path.Combine(_shared, "Datei mit Umlaut ä.txt");
        var outsideTarget = Path.Combine(_root, "nicht-freigegeben.txt");
        await File.WriteAllTextAsync(allowedTarget, "freigegeben");
        await File.WriteAllTextAsync(outsideTarget, "nicht freigegeben");
        CreateShortcut(Path.Combine(_recent, "Erster Link.lnk"), allowedTarget);
        CreateShortcut(Path.Combine(_recent, "Doppelter Link.lnk"), allowedTarget);
        CreateShortcut(Path.Combine(_recent, "Externer Link.lnk"), outsideTarget);

        var policy = new PathAccessPolicy([new ShareRoot(_shared)]);
        var service = new WindowsRecentFileService(new ShortcutService(policy), _recent);

        var result = await service.GetAsync(CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(allowedTarget, entry.Path);
        Assert.Equal("Datei mit Umlaut ä.txt", entry.Name);
        Assert.Equal("txt", entry.Extension);
    }

    [Fact]
    public async Task RecentReadHonorsCancellation()
    {
        var target = Path.Combine(_shared, "datei.txt");
        await File.WriteAllTextAsync(target, "inhalt");
        CreateShortcut(Path.Combine(_recent, "Link.lnk"), target);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new WindowsRecentFileService(new ShortcutService(new PathAccessPolicy([new ShareRoot(_shared)])), _recent);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(cancellation.Token));
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Die Windows Shell-Verknüpfungsfunktion ist nicht verfügbar.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Die Windows Shell-Verknüpfungsfunktion konnte nicht gestartet werden.");
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            if (shortcut is null) throw new InvalidOperationException("Die Testverknüpfung konnte nicht angelegt werden.");
            shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
