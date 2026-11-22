using Explorer.Core.Security;

namespace Explorer.Host.Services;

public sealed record FolderSizeValue(long Bytes, bool Incomplete);
public sealed record FolderSizesResult(IReadOnlyDictionary<string, FolderSizeValue> Folders);

/// <summary>Calculates requested directory totals in the background without reading file content or following links.</summary>
public sealed class FolderSizeService(PathAccessPolicy policy)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public FolderSizesResult? GetOrStart(string path, bool refresh = false)
    {
        var folder = policy.Authorize(path);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
        lock (_gate)
        {
            if (_jobs.TryGetValue(folder, out var existing))
            {
                if (!refresh && (!existing.Work.IsCompleted || DateTime.UtcNow - existing.Started < CacheLifetime))
                    return existing.Work.IsCompleted ? existing.Work.GetAwaiter().GetResult() : null;
                existing.Cancel.Cancel();
                _jobs.Remove(folder);
            }
            while (_jobs.Count >= 4)
            {
                var oldest = _jobs.MinBy(item => item.Value.Started);
                oldest.Value.Cancel.Cancel();
                _jobs.Remove(oldest.Key);
            }
            var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var work = Task.Run(async () =>
            {
                await _worker.WaitAsync(cancel.Token);
                try { return Calculate(folder, cancel.Token); }
                finally { _worker.Release(); }
            });
            _ = work.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _jobs[folder] = new Job(DateTime.UtcNow, work, cancel);
            return null;
        }
    }

    public FolderSizesResult? GetReady(string path)
    {
        var folder = policy.Authorize(path);
        lock (_gate)
            return _jobs.TryGetValue(folder, out var job) && job.Work.IsCompletedSuccessfully
                ? job.Work.Result : null;
    }

    internal static FolderSizesResult Calculate(string folder, CancellationToken token)
    {
        var result = new Dictionary<string, FolderSizeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateDirectories(folder))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    result[path] = new FolderSizeValue(0, true);
                    continue;
                }
                long total = 0;
                var incomplete = false;
                var pending = new Stack<string>();
                pending.Push(path);
                while (pending.TryPop(out var directory))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                var attributes = File.GetAttributes(child);
                                if ((attributes & FileAttributes.ReparsePoint) != 0) { incomplete = true; continue; }
                                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                                else total = checked(total + new FileInfo(child).Length);
                            }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or OverflowException) { incomplete = true; }
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { incomplete = true; }
                }
                result[path] = new FolderSizeValue(total, incomplete);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { result[path] = new FolderSizeValue(0, true); }
        }
        return new FolderSizesResult(result);
    }

    private sealed record Job(DateTime Started, Task<FolderSizesResult> Work, CancellationTokenSource Cancel);
}
