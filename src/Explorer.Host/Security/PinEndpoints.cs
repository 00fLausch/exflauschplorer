using Explorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Explorer.Host.Security;

public sealed record PinRequest(string Pin);

public static class PinEndpoints
{
    private const string CookieName = "Explorer.PinSession";

    public static void MapRoutes(WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/security/pin");
        routes.MapGet("/status", (HttpContext context, PinProtectionService pins) =>
            Results.Ok(pins.GetStatus(Identity(context), ReadSession(context.Request))));
        routes.MapPost("/configure", (HttpContext context, PinRequest request, PinProtectionService pins, ExplorerLiveStreams streams, ExplorerRequestTrust trust) =>
            Configure(context, request, pins, streams, trust, app.Logger));
        routes.MapPost("/unlock", (HttpContext context, PinRequest request, PinProtectionService pins, ExplorerRequestTrust trust) =>
        {
            var result = pins.Unlock(Identity(context), request.Pin);
            if (result.Success) app.Logger.LogInformation("PIN-Sitzung von {User} entsperrt", Identity(context));
            return Complete(context, result, trust);
        });
        routes.MapPost("/lock", (HttpContext context, PinProtectionService pins, ExplorerLiveStreams streams, ExplorerRequestTrust trust) =>
            Lock(context, pins, streams, trust, app.Logger));
        routes.MapPost("/disable", (HttpContext context, PinProtectionService pins, ExplorerLiveStreams streams, ExplorerRequestTrust trust) =>
            Disable(context, pins, streams, trust, app.Logger));
    }

    internal static IResult Configure(HttpContext context, PinRequest request, PinProtectionService pins,
        ExplorerLiveStreams streams, ExplorerRequestTrust trust, ILogger logger)
    {
        var result = pins.Configure(Identity(context), request.Pin, ReadSession(context.Request));
        if (result.Success)
        {
            streams.Revoke(all: true);
            logger.LogInformation("Zusätzlicher PIN von {User} eingerichtet oder geändert", Identity(context));
        }
        return Complete(context, result, trust);
    }

    internal static IResult Lock(HttpContext context, PinProtectionService pins, ExplorerLiveStreams streams,
        ExplorerRequestTrust trust, ILogger logger)
    {
        var session = ReadSession(context.Request);
        if (pins.Lock(Identity(context), session)) streams.Revoke(session);
        DeleteCookie(context, trust);
        logger.LogInformation("PIN-Sitzung von {User} gesperrt", Identity(context));
        return Results.NoContent();
    }

    internal static IResult Disable(HttpContext context, PinProtectionService pins, ExplorerLiveStreams streams,
        ExplorerRequestTrust trust, ILogger logger)
    {
        var result = pins.Disable(Identity(context), ReadSession(context.Request));
        if (result.Success)
        {
            streams.Revoke(all: true);
            logger.LogWarning("Zusätzlicher PIN von {User} ausgeschaltet", Identity(context));
        }
        return Complete(context, result, trust);
    }

    internal static string? ReadSession(HttpRequest request) => request.Cookies[CookieName];

    private static IResult Complete(HttpContext context, PinResult result, ExplorerRequestTrust trust)
    {
        if (!result.Success)
        {
            if (result.RetryAfterSeconds > 0) context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
            return Results.Json(new ApiErrorDto(result.Message ?? "Der PIN-Vorgang ist fehlgeschlagen.", result.ErrorCode), statusCode: result.StatusCode);
        }

        if (result.SessionToken is not null) SetCookie(context, result.SessionToken, trust);
        else if (result.ErrorCode is null && !string.IsNullOrWhiteSpace(context.Request.Cookies[CookieName])) DeleteCookie(context, trust);
        return Results.Ok(new { ok = true });
    }

    private static void SetCookie(HttpContext context, string token, ExplorerRequestTrust trust) => context.Response.Cookies.Append(CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = !trust.IsolatedTest || context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        MaxAge = TimeSpan.FromHours(8)
    });

    private static void DeleteCookie(HttpContext context, ExplorerRequestTrust trust) => context.Response.Cookies.Delete(CookieName, new CookieOptions
    {
        HttpOnly = true,
        Secure = !trust.IsolatedTest || context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true
    });

    private static string Identity(HttpContext context) => context.Items["Explorer.User"]?.ToString() ?? "";
}
