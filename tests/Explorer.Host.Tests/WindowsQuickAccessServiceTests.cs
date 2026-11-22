using Explorer.Core.Security;
using Explorer.Host.Services;

namespace Explorer.Host.Tests;

public sealed class WindowsQuickAccessServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-QuickAccess-{Guid.NewGuid():N}");
    private readonly string _shared;

    public WindowsQuickAccessServiceTests()
    {
        _shared = Path.Combine(_root, "Freigegeben");
        Directory.CreateDirectory(_shared);
    }

    [Fact]
    public async Task QuickAccessExposesOnlyExistingUniqueFoldersInsideConfiguredShares()
    {
        var pinned = Path.Combine(_shared, "Angeheftet", "Projekte");
        var outside = Path.Combine(_root, "Privat");
        Directory.CreateDirectory(pinned);
        Directory.CreateDirectory(outside);
        var policy = new PathAccessPolicy([new ShareRoot(_shared)]);
        var source = new TestQuickAccessSource([pinned, outside, pinned, Path.Combine(_root, "fehlt")]);
        var service = new WindowsQuickAccessService(policy, source);

        var result = await service.GetAsync(CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal("Projekte", entry.Name);
        Assert.Equal(pinned, entry.Path);
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public async Task QuickAccessReadHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new WindowsQuickAccessService(
            new PathAccessPolicy([new ShareRoot(_shared)]), new TestQuickAccessSource([_shared]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(cancellation.Token));
    }

    [Fact]
    public async Task DirectoryReadOnlyAttributeDoesNotMakeQuickAccessFolderReadOnly()
    {
        var pinned = Path.Combine(_shared, "Desktop");
        Directory.CreateDirectory(pinned);
        File.SetAttributes(pinned, File.GetAttributes(pinned) | FileAttributes.ReadOnly);
        try
        {
            var service = new WindowsQuickAccessService(
                new PathAccessPolicy([new ShareRoot(_shared)]), new TestQuickAccessSource([pinned]));

            var entry = Assert.Single(await service.GetAsync(CancellationToken.None));

            Assert.False(entry.IsReadOnly);
        }
        finally { File.SetAttributes(pinned, FileAttributes.Directory); }
    }

    [Fact]
    public async Task QuickAccessReadOnlyStateFollowsConfiguredSharePolicy()
    {
        var service = new WindowsQuickAccessService(
            new PathAccessPolicy([new ShareRoot(_shared, ReadOnly: true)]), new TestQuickAccessSource([_shared]));

        var entry = Assert.Single(await service.GetAsync(CancellationToken.None));

        Assert.True(entry.IsReadOnly);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class TestQuickAccessSource(IReadOnlyList<string> paths) : IQuickAccessFolderSource
    {
        public IReadOnlyList<string> GetFolderPaths(CancellationToken cancellationToken) => paths;
    }
}
