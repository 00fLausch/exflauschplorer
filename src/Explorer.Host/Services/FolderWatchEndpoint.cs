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
        var channel = Channel.CreateBounded<object>(new BoundedChannelOptions(128)
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
        watcher.Created += (_, e) => { files.Invalidate(folder); channel.Writer.TryWrite(new { type = "created", name = e.Name }); };
        watcher.Changed += (_, e) => { files.Invalidate(folder); channel.Writer.TryWrite(new { type = "changed", name = e.Name }); };
        watcher.Deleted += (_, e) => { files.Invalidate(folder); channel.Writer.TryWrite(new { type = "deleted", name = e.Name }); };
        watcher.Renamed += (_, e) => { files.Invalidate(folder); channel.Writer.TryWrite(new { type = "renamed", name = e.Name, oldName = e.OldName }); };
        watcher.Error += (_, _) => { files.Invalidate(folder); channel.Writer.TryWrite(new { type = "refresh" }); };

        await WriteEvent(context, "ready", new { path = folder });
        while (!context.RequestAborted.IsCancellationRequested)
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var dataReady = channel.Reader.WaitToReadAsync(pending.Token).AsTask();
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(18), context.RequestAborted);
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
            while (channel.Reader.TryRead(out var message))
                await WriteEvent(context, "change", message);
        }
    }

    private static async Task WriteEvent(HttpContext context, string eventName, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
