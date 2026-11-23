using Explorer.Host.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace Explorer.Host.Security;

public sealed class ExplorerRequestTrust(ExplorerSettings settings, bool isolatedTest)
{
    public bool IsolatedTest { get; } = isolatedTest;

    public bool AllowsHost(HostString host)
    {
        if (!host.HasValue) return false;
        if (IsolatedTest) return host.Port == settings.Port &&
            host.Host is "127.0.0.1" or "localhost";

        var expected = settings.TailscaleHostName?.TrimEnd('.');
        return expected is not null &&
            Uri.CheckHostName(expected) == UriHostNameType.Dns &&
            expected.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(host.Host, expected, StringComparison.OrdinalIgnoreCase) &&
            (host.Port ?? 443) == settings.TailscaleHttpsPort;
    }

    public bool AllowsOrigin(string? value, HostString host)
    {
        return AllowsHost(host) && Uri.TryCreate(value, UriKind.Absolute, out var origin) &&
            origin.Scheme == (IsolatedTest ? "http" : "https") &&
            origin.Host.Equals(host.Host, StringComparison.OrdinalIgnoreCase) &&
            origin.Port == (IsolatedTest ? settings.Port : settings.TailscaleHttpsPort) &&
            origin.AbsolutePath == "/" && origin.UserInfo.Length == 0 &&
            origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    public bool AllowsLocalSetupToken(string? value) =>
        value is { Length: 64 } && settings.LocalSetupToken is { Length: 64 } token &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(value));
}
