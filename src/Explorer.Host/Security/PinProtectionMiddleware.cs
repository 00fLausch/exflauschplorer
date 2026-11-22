using Explorer.Core.Models;

namespace Explorer.Host.Security;

public sealed class PinProtectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PinProtectionService pinProtection)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/v1") || IsExempt(path) || !pinProtection.IsConfigured)
        {
            await next(context);
            return;
        }

        var identity = context.Items["Explorer.User"]?.ToString() ?? "";
        if (pinProtection.IsUnlocked(identity, PinEndpoints.ReadSession(context.Request)))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status423Locked;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(
            new ApiErrorDto("Der PIN-Schutz ist gesperrt. Entsperre die App, um fortzufahren.", "pin_locked"),
            context.RequestAborted);
    }

    private static bool IsExempt(PathString path) =>
        path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/security/pin/status", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/security/pin/configure", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/security/pin/unlock", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/security/pin/lock", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/security/pin/disable", StringComparison.OrdinalIgnoreCase);
}
