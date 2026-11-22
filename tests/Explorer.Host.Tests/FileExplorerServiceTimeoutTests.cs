using Explorer.Core.Security;
using Explorer.Host.Services;

namespace Explorer.Host.Tests;

public sealed class FileExplorerServiceTimeoutTests
{
    [Fact]
    public async Task ListAsyncTimesOutAndLimitsAbandonedWorkerPerDrive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WME-slow-drive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerationCalls = 0;

        IEnumerable<string> EnumerateBlockedDirectory(string _)
        {
            Interlocked.Increment(ref enumerationCalls);
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            finished.TrySetResult(true);
            yield break;
        }

        var policy = new PathAccessPolicy([new ShareRoot(root)]);
        var service = new FileExplorerService(policy, TimeSpan.FromMilliseconds(250), EnumerateBlockedDirectory);
        try
        {
            var request = service.ListAsync(root, 0, 100, "name", false, null, false, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var exception = await Assert.ThrowsAsync<TimeoutException>(async () => await request);
            Assert.Contains("Laufwerk", exception.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<TimeoutException>(() => service.ListAsync(
                root, 0, 100, "name", false, null, false, CancellationToken.None));
            Assert.Equal(1, Volatile.Read(ref enumerationCalls));
        }
        finally
        {
            release.TrySetResult(true);
            try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            Directory.Delete(root, recursive: true);
        }
    }
}
