using Explorer.Core.Security;
using Explorer.Host.Services;

namespace Explorer.Host.Tests;

public sealed class CleanServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-clean-test-{Guid.NewGuid():N}");
    public CleanServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PreviewDeletesOnlySelectedUnchangedFilesAndCannotBeReplayed()
    {
        var selected = Path.Combine(_root, "selected.cache");
        var changed = Path.Combine(_root, "changed.cache");
        var untouched = Path.Combine(_root, "untouched.cache");
        var fresh = Path.Combine(_root, "fresh.cache");
        foreach (var path in new[] { selected, changed, untouched, fresh }) await File.WriteAllBytesAsync(path, new byte[128]);
        foreach (var path in new[] { selected, changed, untouched }) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        var service = new CleanService(new PathAccessPolicy([new ShareRoot(_root)]), [new CleanLocation("test", "Test", "Test", _root, "cache")]);
        var scan = await service.ScanAsync("owner", CancellationToken.None);
        var items = scan.Categories.Single().Items;
        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, item => item.Path == fresh);
        var selectedId = items.Single(item => item.Path == selected).Id;
        var changedId = items.Single(item => item.Path == changed).Id;
        Assert.Throws<ArgumentException>(() => service.Delete("other", new(scan.Id, [selectedId])));
        Assert.Throws<ArgumentException>(() => service.Delete("owner", new(scan.Id, ["not-in-preview"])));
        await File.WriteAllBytesAsync(changed, new byte[256]);
        var result = service.Delete("owner", new(scan.Id, [selectedId, changedId]));
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(128, result.BytesFreed);
        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(changed));
        Assert.True(File.Exists(untouched));
        Assert.True(File.Exists(fresh));
        Assert.Throws<ArgumentException>(() => service.Delete("owner", new(scan.Id, [selectedId])));
    }

    [Fact]
    public async Task HuggingFaceScanOnlyShowsOldIncompleteBlobDownloads()
    {
        var blobs = Path.Combine(_root, "models--example--model", "blobs");
        Directory.CreateDirectory(blobs);
        var incomplete = Path.Combine(blobs, "hash.incomplete");
        var completed = Path.Combine(blobs, "finished-hash");
        var recent = Path.Combine(blobs, "recent.incomplete");
        foreach (var path in new[] { incomplete, completed, recent }) await File.WriteAllBytesAsync(path, new byte[256]);
        File.SetLastWriteTimeUtc(incomplete, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(completed, DateTime.UtcNow.AddDays(-3));
        var service = new CleanService(new PathAccessPolicy([new ShareRoot(_root)]), [new CleanLocation("hf", "HF", "Test", _root, "hf")]);
        var scan = await service.ScanAsync("owner", CancellationToken.None);
        Assert.Equal(incomplete, Assert.Single(scan.Categories.Single().Items).Path);
    }

    [Fact]
    public async Task PreviewDoesNotFollowDirectoryLinks()
    {
        var scanRoot = Path.Combine(_root, "cache");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(scanRoot);
        Directory.CreateDirectory(elsewhere);
        var protectedFile = Path.Combine(elsewhere, "keep.cache");
        await File.WriteAllTextAsync(protectedFile, "keep");
        File.SetLastWriteTimeUtc(protectedFile, DateTime.UtcNow.AddDays(-3));
        try { Directory.CreateSymbolicLink(Path.Combine(scanRoot, "link"), elsewhere); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        var service = new CleanService(new PathAccessPolicy([new ShareRoot(_root)]), [new CleanLocation("test", "Test", "Test", scanRoot, "cache")]);
        var scan = await service.ScanAsync("owner", CancellationToken.None);
        Assert.Empty(scan.Categories.Single().Items);
        Assert.True(File.Exists(protectedFile));
    }

    [Fact]
    public async Task ReplacedDirectoryLinkIsNotFollowedDuringDeletion()
    {
        var scanRoot = Path.Combine(_root, "cache");
        var working = Path.Combine(scanRoot, "working");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(elsewhere);
        var original = Path.Combine(working, "same.cache");
        var protectedFile = Path.Combine(elsewhere, "same.cache");
        await File.WriteAllBytesAsync(original, new byte[128]);
        await File.WriteAllBytesAsync(protectedFile, new byte[128]);
        var old = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(original, old);
        File.SetLastWriteTimeUtc(protectedFile, old);
        var service = new CleanService(new PathAccessPolicy([new ShareRoot(_root)]), [new CleanLocation("test", "Test", "Test", scanRoot, "cache")]);
        var scan = await service.ScanAsync("owner", CancellationToken.None);
        var item = Assert.Single(scan.Categories.Single().Items);
        var held = Path.Combine(_root, "held");
        Directory.Move(working, held);
        try { Directory.CreateSymbolicLink(working, elsewhere); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { Directory.Move(held, working); return; }
        var result = service.Delete("owner", new(scan.Id, [item.Id]));
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(protectedFile));
        Assert.True(File.Exists(Path.Combine(held, "same.cache")));
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("WME-clean-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid test cleanup path.");
        Directory.Delete(root, recursive: true);
    }
}
