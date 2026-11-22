using Explorer.Core.Security;
using Explorer.Host.Configuration;

namespace Explorer.Host.Tests;

public sealed class ExplorerSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-Freigabe Ä- {Guid.NewGuid():N}");
    private readonly ExplorerSettings _settings;
    private readonly PathAccessPolicy _policy;
    private readonly ExplorerSettingsService _service;
    private int _saveCount;

    public ExplorerSettingsServiceTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new ExplorerSettings
        {
            ShareAllDrives = false,
            ProtectSystemFolders = false,
            Roots = [new ConfiguredRoot { Path = _root, Label = "Testordner" }],
            FavoritesByLogin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["owner@example.ts.net"] = [Path.Combine(_root, "Favorit")]
            }
        };
        _policy = SettingsStore.CreatePolicy(_settings);
        _service = new ExplorerSettingsService(_settings, _policy, _ => _saveCount++);
    }

    [Fact]
    public void UpdatingSettingsPersistsAndImmediatelyReplacesTheActivePolicy()
    {
        var target = Path.Combine(_root, "datei.txt");

        var result = _service.Update(new UpdateExplorerShareSettings(
            ShareAllDrives: false,
            ReadOnlyMode: true,
            ProtectSystemFolders: true,
            Roots: [new ShareRootSetting(_root, ReadOnly: false, "Testordner")]));

        Assert.Equal(1, _saveCount);
        Assert.True(result.ReadOnlyMode);
        Assert.True(_settings.ReadOnlyMode);
        Assert.Equal([Path.Combine(_root, "Favorit")], _settings.FavoritesByLogin["owner@example.ts.net"]);
        Assert.Throws<UnauthorizedAccessException>(() => _policy.Authorize(target, write: true));
        Assert.Equal(target, _policy.Authorize(target));
    }

    [Fact]
    public void RejectsDisablingAllDrivesWithoutAtLeastOneSharedFolder()
    {
        Assert.Throws<ArgumentException>(() => _service.Update(new UpdateExplorerShareSettings(
            ShareAllDrives: false,
            ReadOnlyMode: false,
            ProtectSystemFolders: true,
            Roots: [])));

        Assert.Equal(0, _saveCount);
        Assert.True(_policy.IsAllowed(_root));
    }

    [Fact]
    public void RejectsInvalidAndDuplicateRootsWithoutChangingTheLivePolicy()
    {
        Assert.Throws<ArgumentException>(() => _service.Update(new UpdateExplorerShareSettings(
            ShareAllDrives: false,
            ReadOnlyMode: false,
            ProtectSystemFolders: false,
            Roots: [new ShareRootSetting(Path.Combine(_root, "NUL"), false)])));

        Assert.Throws<ArgumentException>(() => _service.Update(new UpdateExplorerShareSettings(
            ShareAllDrives: false,
            ReadOnlyMode: false,
            ProtectSystemFolders: false,
            Roots: [new ShareRootSetting(_root, false), new ShareRootSetting(_root + "\\.", true)])));

        Assert.Equal(0, _saveCount);
        Assert.True(_policy.IsAllowed(_root));
    }

    [Fact]
    public void ProtectsCriticalWindowsFoldersByDefaultAndHonorsTheSetting()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows) || !Directory.Exists(windows)) return;

        var target = Path.Combine(windows, $"WME-policy-check-{Guid.NewGuid():N}.txt");
        var protectedPolicy = SettingsStore.CreatePolicy(new ExplorerSettings
        {
            ShareAllDrives = true,
            ProtectSystemFolders = true
        });
        var unrestrictedPolicy = SettingsStore.CreatePolicy(new ExplorerSettings
        {
            ShareAllDrives = true,
            ProtectSystemFolders = false
        });

        Assert.Throws<UnauthorizedAccessException>(() => protectedPolicy.Authorize(target, write: true));
        Assert.Equal(target, unrestrictedPolicy.Authorize(target, write: true));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
