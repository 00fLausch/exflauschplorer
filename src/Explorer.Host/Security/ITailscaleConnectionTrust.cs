namespace Explorer.Host.Security;

/// <summary>Decides whether a loopback connection came from the local Tailscale service.</summary>
public interface ITailscaleConnectionTrust
{
    bool IsTrusted(HttpContext context);
}
