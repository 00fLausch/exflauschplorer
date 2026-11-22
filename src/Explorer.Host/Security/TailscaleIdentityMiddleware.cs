using System.Text;
using System.Text.Json;
using Explorer.Core.Models;
using Explorer.Host.Configuration;

namespace Explorer.Host.Security;

public sealed class TailscaleIdentityMiddleware(RequestDelegate next, ILogger<TailscaleIdentityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ExplorerSettings settings, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            context.Items["Explorer.User"] = "development-local";
            await next(context);
            return;
        }

        var login = DecodeHeader(context.Request.Headers["Tailscale-User-Login"].FirstOrDefault());
        if (string.IsNullOrWhiteSpace(login))
        {
            await Reject(context, StatusCodes.Status401Unauthorized,
                "Diese App ist nur über Tailscale erreichbar. Es wurde keine Tailscale-Identität übermittelt.", "tailscale_identity_missing");
            return;
        }

        if (settings.AllowedLogins.Count == 0)
        {
            logger.LogWarning("Tailscale-Anmeldung {Login} wurde abgelehnt, weil noch kein Konto freigegeben ist.", login);
            await Reject(context, StatusCodes.Status403Forbidden,
                "Noch ist kein Tailscale-Konto freigegeben. Ergänze deine Anmeldeadresse in der Datei settings.json und starte die App neu.", "allowlist_empty");
            return;
        }

        if (!settings.AllowedLogins.Contains(login, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("Nicht freigegebene Tailscale-Anmeldung abgelehnt: {Login}", login);
            await Reject(context, StatusCodes.Status403Forbidden,
                "Dieses Tailscale-Konto ist für den Datei-Explorer nicht freigegeben.", "identity_not_allowed");
            return;
        }

        context.Items["Explorer.User"] = login;
        await next(context);
    }

    private static async Task Reject(HttpContext context, int status, string message, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, new ApiErrorDto(message, code), cancellationToken: context.RequestAborted);
    }

    private static string? DecodeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith("=?", StringComparison.Ordinal) || !value.EndsWith("?=", StringComparison.Ordinal)) return value.Trim();
        try
        {
            var content = value[2..^2];
            var first = content.IndexOf('?');
            var second = first < 0 ? -1 : content.IndexOf('?', first + 1);
            if (first < 0 || second < 0) return value;
            var encoding = Encoding.GetEncoding(content[..first]);
            var kind = content[(first + 1)..second];
            var encoded = content[(second + 1)..];
            if (kind.Equals("Q", StringComparison.OrdinalIgnoreCase))
                return encoding.GetString(DecodeQ(encoded));
            if (kind.Equals("B", StringComparison.OrdinalIgnoreCase))
                return encoding.GetString(Convert.FromBase64String(encoded));
        }
        catch (Exception) { }
        return value;
    }

    private static byte[] DecodeQ(string value)
    {
        using var output = new MemoryStream();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '_') output.WriteByte((byte)' ');
            else if (value[index] == '=' && index + 2 < value.Length &&
                     byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var decoded))
            {
                output.WriteByte(decoded);
                index += 2;
            }
            else output.WriteByte((byte)value[index]);
        }
        return output.ToArray();
    }
}
