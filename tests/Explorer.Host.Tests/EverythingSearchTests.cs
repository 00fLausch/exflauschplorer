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

    private static async Task<List<FileEntryDto>> CollectAsync(IAsyncEnumerable<FileEntryDto> entries)
    {
        var result = new List<FileEntryDto>();
        await foreach (var entry in entries) result.Add(entry);
        return result;
    }

    private sealed class FakeEverythingSearchProvider(params string[] paths) : IEverythingSearchProvider
    {
        public bool IsAvailable => true;

        public async IAsyncEnumerable<string> SearchPathsAsync(string root, string query, int offset, int count,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
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
