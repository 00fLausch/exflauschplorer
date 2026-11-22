using Explorer.Core.Security;
using Explorer.Host.Configuration;
using Explorer.Host.Services;

namespace Explorer.Host.Tests;

public sealed class ExplorerFavoritesServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-Favoriten Ä- {Guid.NewGuid():N}");
    private readonly string _folderA;
    private readonly string _folderB;
    private readonly string _outside;
    private readonly ExplorerSettings _settings;
    private readonly ExplorerFavoritesService _service;
    private int _saveCount;

    public ExplorerFavoritesServiceTests()
    {
        _folderA = Path.Combine(_root, "Dokumente Ä");
        _folderB = Path.Combine(_root, "Musik");
        _outside = Path.Combine(_root + "-nicht-freigegeben", "Privat");
        Directory.CreateDirectory(_folderA);
        Directory.CreateDirectory(_folderB);
        Directory.CreateDirectory(_outside);
        _settings = new ExplorerSettings
        {
            ShareAllDrives = false,
            ProtectSystemFolders = false,
            Roots = [new ConfiguredRoot { Path = _root }]
        };
        var policy = SettingsStore.CreatePolicy(_settings);
        _service = new ExplorerFavoritesService(_settings, policy, _ => _saveCount++);
    }

    [Fact]
    public void FavoritesArePersistedAndScopedToTheTailscaleLogin()
    {
        var saved = _service.Update("owner@example.ts.net", [_folderA, _folderB, _folderA]);

        Assert.Equal(1, _saveCount);
        Assert.Equal(2, saved.Count);
        Assert.Equal(saved, _service.Get("OWNER@example.ts.net"));
        Assert.Empty(_service.Get("other@example.ts.net"));
    }

    [Fact]
    public void RejectsPathsOutsideTheCurrentShareAndFiles()
    {
        var file = Path.Combine(_root, "keine-datei.txt");
        File.WriteAllText(file, "test");

        Assert.Throws<UnauthorizedAccessException>(() => _service.Update("owner@example.ts.net", [_outside]));
        Assert.Throws<ArgumentException>(() => _service.Update("owner@example.ts.net", [file]));
        Assert.Equal(0, _saveCount);
        Assert.Empty(_service.Get("owner@example.ts.net"));
    }

    [Fact]
    public void EnforcesTheMaximumWithoutChangingSavedFavorites()
    {
        _service.Update("owner@example.ts.net", [_folderA]);
        var tooMany = Enumerable.Repeat(_folderA, ExplorerFavoritesService.MaximumFavorites + 1).ToArray();

        Assert.Throws<ArgumentException>(() => _service.Update("owner@example.ts.net", tooMany));
        Assert.Equal(1, _saveCount);
        Assert.Equal([_folderA], _service.Get("owner@example.ts.net"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try { Directory.Delete(_outside, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
