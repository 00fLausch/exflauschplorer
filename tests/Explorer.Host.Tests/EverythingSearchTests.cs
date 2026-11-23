using System.Runtime.CompilerServices;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Services;

namespace Explorer.Host.Tests;

public sealed class EverythingSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-Everything-Ä-{Guid.NewGuid():N}");
    private readonly PathAccessPolicy _policy;

    public EverythingSearchTests()
    {
        Directory.CreateDirectory(_root);
        _policy = new PathAccessPolicy([new ShareRoot(_root)]);
    }

    [Fact]
    public async Task IndexedPathsAreRecheckedAgainstShareRootAndFilters()
    {
        var matching = Path.Combine(_root, "match-item.txt");
        var wrongExtension = Path.Combine(_root, "match-item.log");
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, $"match-item-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(matching, "match");
        await File.WriteAllTextAsync(wrongExtension, "match");
        await File.WriteAllTextAsync(outside, "match");
        try
        {
            var service = new RecursiveSearchService(_policy, new FakeEverythingSearchProvider(matching, wrongExtension, outside));
            var results = await CollectAsync(service.SearchAsync(_root, "match", false, false, "txt", null, null,
                null, null, CancellationToken.None, useWindowsSearchIndex: false));

            var result = Assert.Single(results);
            Assert.Equal(matching, result.Path);
            Assert.Equal("match-item.txt", result.Name);
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task EmptyEverythingResultFallsBackToFilesystemSearch()
    {
        var matching = Path.Combine(_root, "unindexed-match.txt");
        await File.WriteAllTextAsync(matching, "match");
        var service = new RecursiveSearchService(_policy, new FakeEverythingSearchProvider());

        var results = await CollectAsync(service.SearchAsync(_root, "match", false, false, null, null, null,
            null, null, CancellationToken.None, useWindowsSearchIndex: false));

        Assert.Contains(results, entry => entry.Path == matching);
    }

    [Fact]
    public async Task LiteralSearchDoesNotExpandEverythingOperators()
    {
        var matching = Path.Combine(_root, "!demo.txt");
        await File.WriteAllTextAsync(matching, "demo");
        await File.WriteAllTextAsync(Path.Combine(_root, "unrelated.txt"), "demo");
        var provider = new FakeEverythingSearchProvider();
        var service = new RecursiveSearchService(_policy, provider);
        var results = await CollectAsync(service.SearchAsync(_root, "!demo", false, false, null, null, null,
            null, null, CancellationToken.None, useWindowsSearchIndex: false));

        Assert.Null(provider.LastQuery);
        Assert.Equal(matching, Assert.Single(results).Path);
    }

    [Theory]
    [InlineData("*.mp3", "mp3")]
    [InlineData("ext:flac", "flac")]
    public async Task ExplicitFilePatternsUseIndexedResultsWithinTheRequestedFolder(string query, string extension)
    {
        var scope = Directory.CreateDirectory(Path.Combine(_root, "scope")).FullName;
        var matching = Path.Combine(scope, "track." + extension);
        var unindexed = Path.Combine(scope, "unindexed." + extension);
        var wrongExtension = Path.Combine(scope, "track.txt");
        var outsideScope = Path.Combine(_root, "outside." + extension);
        var matchingFolder = Directory.CreateDirectory(Path.Combine(scope, "folder." + extension)).FullName;
        foreach (var file in new[] { matching, unindexed, wrongExtension, outsideScope })
            await File.WriteAllTextAsync(file, "demo");

        var provider = new FakeEverythingSearchProvider(matching, wrongExtension, outsideScope, matchingFolder);
        var service = new RecursiveSearchService(_policy, provider);
        var results = await CollectAsync(service.SearchAsync(scope, query, false, false, extension, 1, null,
            null, null, CancellationToken.None, useWindowsSearchIndex: false));

        Assert.Equal(query, provider.LastQuery);
        Assert.Equal(matching, Assert.Single(results).Path);
    }

    [Theory]
    [InlineData("*.mp3", "mp3")]
    [InlineData("ext:flac", "flac")]
    public async Task ExplicitFilePatternsWorkWithoutEverythingAndRespectBlockedPaths(string query, string extension)
    {
        var blocked = Directory.CreateDirectory(Path.Combine(_root, "private")).FullName;
        var matching = Path.Combine(_root, "track." + extension);
        await File.WriteAllTextAsync(matching, "demo");
        await File.WriteAllTextAsync(Path.Combine(blocked, "private." + extension), "demo");
        await File.WriteAllTextAsync(Path.Combine(_root, "wrong.txt"), "demo");
        var service = new RecursiveSearchService(new PathAccessPolicy([new ShareRoot(_root)], [blocked]));

        var results = await CollectAsync(service.SearchAsync(_root, query, false, false, null, null, null,
            null, null, CancellationToken.None, useWindowsSearchIndex: false));

        Assert.Equal(matching, Assert.Single(results).Path);
    }

    private static async Task<List<FileEntryDto>> CollectAsync(IAsyncEnumerable<FileEntryDto> entries)
    {
        var result = new List<FileEntryDto>();
        await foreach (var entry in entries) result.Add(entry);
        return result;
    }

    private sealed class FakeEverythingSearchProvider(params string[] paths) : IEverythingSearchProvider
    {
        public bool IsAvailable => true;
        public string? LastQuery { get; private set; }

        public async IAsyncEnumerable<string> SearchPathsAsync(string root, string query, int offset, int count,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastQuery = query;
            await Task.Yield();
            foreach (var path in paths.Skip(offset).Take(count))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return path;
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
