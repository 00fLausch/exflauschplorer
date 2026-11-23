using Explorer.Core.Models;

namespace Explorer.Host.Security;

public sealed class ExplorerLiveStreams(PinProtectionService pins) : IMiddleware
{
    private readonly object _gate = new();
    private readonly HashSet<StreamRequest> _active = [];

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
        var limit = path switch
        {
            "/api/v1/search" => 2,
            "/api/v1/watch" or "/api/v1/jobs/events" => 6,
            _ => 0
        };
        if (limit == 0) { await next(context); return; }

        var identity = context.Items["Explorer.User"]?.ToString() ?? "";
        var session = PinEndpoints.ReadSession(context.Request);
        var request = new StreamRequest(context, path!, identity, session);
        var status = Register(request, limit);
        if (status != 0)
        {
            context.Response.StatusCode = status;
            context.Response.Headers.CacheControl = "no-store";
            var error = status == StatusCodes.Status423Locked
                ? new ApiErrorDto("Entsperre die App, um fortzufahren.", "pin_locked")
                : new ApiErrorDto("Es sind bereits zu viele parallele Verbindungen geöffnet.", "stream_limit");
            await context.Response.WriteAsJsonAsync(error, context.RequestAborted);
            return;
        }
        var remaining = pins.SessionRemaining(identity, session);
        using var expiration = remaining is { } duration ? new CancellationTokenSource(duration) : null;
        using var registration = expiration?.Token.Register(context.Abort);
        try { await next(context); }
        finally { lock (_gate) _active.Remove(request); }
    }

    private int Register(StreamRequest request, int limit)
    {
        lock (_gate)
        {
            if (pins.IsConfigured && !pins.IsUnlocked(request.Identity, request.Session)) return StatusCodes.Status423Locked;
            if (_active.Count(stream => stream.Path == request.Path &&
                    stream.Identity.Equals(request.Identity, StringComparison.OrdinalIgnoreCase)) >= limit)
                return StatusCodes.Status429TooManyRequests;
            _active.Add(request);
            return 0;
        }
    }

    public void Revoke(string? session = null, bool all = false)
    {
        if (!all && session is null) return;
        StreamRequest[] active;
        lock (_gate) active = _active.Where(request => all || request.Session == session).ToArray();
        foreach (var request in active) request.Context.Abort();
    }

    private sealed record StreamRequest(HttpContext Context, string Path, string Identity, string? Session);
}
