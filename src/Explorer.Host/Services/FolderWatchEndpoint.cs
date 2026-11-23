using System.Text.Json;
using System.Threading.Channels;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

public static class FolderWatchEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task StreamAsync(HttpContext context, string path, FileExplorerService files)
    {
        var folder = files.Policy.Authorize(path);
        if (!Directory.Exists(folder))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        using var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => channel.Writer.TryWrite(true);
        watcher.Changed += (_, _) => channel.Writer.TryWrite(true);
        watcher.Deleted += (_, _) => channel.Writer.TryWrite(true);
        watcher.Renamed += (_, _) => channel.Writer.TryWrite(true);
        watcher.Error += (_, _) => channel.Writer.TryWrite(true);

        await WriteEvent(context, "ready", new { path = folder });
        while (!context.RequestAborted.IsCancellationRequested)
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var dataReady = channel.Reader.WaitToReadAsync(pending.Token).AsTask();
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(18), pending.Token);
            var finished = await Task.WhenAny(dataReady, heartbeat);
            if (finished == heartbeat)
            {
                pending.Cancel();
                try { await dataReady; } catch (OperationCanceledException) { }
                await context.Response.WriteAsync(": keep-alive\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                continue;
            }

            if (!await dataReady) break;
            pending.Cancel();
            await Task.Delay(TimeSpan.FromMilliseconds(350), context.RequestAborted);
            _ = channel.Reader.TryRead(out _);
            if (!files.Policy.IsAllowed(path)) return;
            files.Invalidate(folder);
            await WriteEvent(context, "change", new { type = "refresh" });
        }
    }

    private static async Task WriteEvent(HttpContext context, string eventName, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
