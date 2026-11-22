using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Explorer.Host.Configuration;

namespace Explorer.Host.Security;

public sealed record PinStatus(bool IsConfigured, bool IsUnlocked, int RetryAfterSeconds);
public sealed record PinResult(bool Success, string? SessionToken = null, string? ErrorCode = null,
    string? Message = null, int StatusCode = StatusCodes.Status200OK, int RetryAfterSeconds = 0);

public sealed class PinProtectionService(
    ExplorerSettings settings,
    Action<ExplorerSettings>? saveSettings = null,
    TimeProvider? timeProvider = null)
{
    private const int Iterations = 310_000;
    private const int MaxFailures = 5;
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(8);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, PinSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<ExplorerSettings> _saveSettings = saveSettings ?? SettingsStore.Save;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public PinStatus GetStatus(string identity, string? sessionToken)
    {
        var retry = GetRetryAfter(identity);
        return new PinStatus(IsConfigured, IsUnlocked(identity, sessionToken), retry);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.PinHash);

    public bool IsUnlocked(string identity, string? sessionToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(sessionToken)) return false;
        if (!_sessions.TryGetValue(sessionToken, out var session)) return false;
        if (session.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            _sessions.TryRemove(sessionToken, out _);
            return false;
        }
        return string.Equals(session.Identity, identity, StringComparison.OrdinalIgnoreCase);
    }

    public PinResult Configure(string identity, string? pin, string? existingToken)
    {
        if (string.IsNullOrWhiteSpace(identity)) return Failure("identity_missing", "Die Tailscale-Anmeldung konnte nicht geprüft werden.", StatusCodes.Status401Unauthorized);
        if (!IsValidPin(pin)) return Failure("pin_invalid_format", "Der PIN muss aus 6 bis 12 Ziffern bestehen.", StatusCodes.Status400BadRequest);

        lock (SettingsStore.SyncRoot)
        {
            if (IsConfigured && !IsUnlocked(identity, existingToken))
                return Failure("pin_locked", "Bitte entsperre den PIN-Schutz zuerst.", StatusCodes.Status423Locked);

            var replacingPin = IsConfigured;
            var previousHash = settings.PinHash;
            settings.PinHash = HashPin(pin!);
            try { _saveSettings(settings); }
            catch { settings.PinHash = previousHash; throw; }
            if (replacingPin) _sessions.Clear();
            return Success(CreateSession(identity));
        }
    }

    public PinResult Unlock(string identity, string? pin)
    {
        if (!IsConfigured) return Failure("pin_not_configured", "Für diese App ist kein PIN eingerichtet.", StatusCodes.Status409Conflict);
        if (string.IsNullOrWhiteSpace(identity)) return Failure("identity_missing", "Die Tailscale-Anmeldung konnte nicht geprüft werden.", StatusCodes.Status401Unauthorized);

        var state = _attempts.GetOrAdd(identity, _ => new AttemptState());
        lock (state)
        {
            var now = _timeProvider.GetUtcNow();
            if (state.LockedUntil > now)
            {
                var retry = Math.Max(1, (int)Math.Ceiling((state.LockedUntil - now).TotalSeconds));
                return new PinResult(false, ErrorCode: "pin_rate_limited", Message: $"Zu viele Fehlversuche. Bitte warte {retry} Sekunden.",
                    StatusCode: StatusCodes.Status429TooManyRequests, RetryAfterSeconds: retry);
            }

            if (state.LockedUntil != default) { state.Failures = 0; state.LockedUntil = default; }
            if (IsValidPin(pin) && VerifyPin(pin!, settings.PinHash!))
            {
                state.Failures = 0;
                state.LockedUntil = default;
                return Success(CreateSession(identity));
            }

            state.Failures++;
            if (state.Failures >= MaxFailures)
            {
                state.LockedUntil = now + LockoutDuration;
                var retry = (int)LockoutDuration.TotalSeconds;
                return new PinResult(false, ErrorCode: "pin_rate_limited", Message: "Zu viele Fehlversuche. Der PIN ist 15 Minuten gesperrt.",
                    StatusCode: StatusCodes.Status429TooManyRequests, RetryAfterSeconds: retry);
            }
            return Failure("pin_invalid", "Der PIN ist nicht korrekt.", StatusCodes.Status401Unauthorized);
        }
    }

    public bool Lock(string identity, string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken) || !_sessions.TryGetValue(sessionToken, out var session) ||
            !string.Equals(session.Identity, identity, StringComparison.OrdinalIgnoreCase)) return false;
        return _sessions.TryRemove(sessionToken, out _);
    }

    public PinResult Disable(string identity, string? sessionToken)
    {
        lock (SettingsStore.SyncRoot)
        {
            if (!IsConfigured) return Failure("pin_not_configured", "Es ist kein PIN eingerichtet.", StatusCodes.Status409Conflict);
            if (!IsUnlocked(identity, sessionToken)) return Failure("pin_locked", "Bitte entsperre den PIN-Schutz zuerst.", StatusCodes.Status423Locked);
            var previousHash = settings.PinHash;
            settings.PinHash = null;
            try { _saveSettings(settings); }
            catch { settings.PinHash = previousHash; throw; }
            if (!string.IsNullOrWhiteSpace(sessionToken)) _sessions.TryRemove(sessionToken, out _);
            return Success();
        }
    }

    private int GetRetryAfter(string identity)
    {
        if (!_attempts.TryGetValue(identity, out var state)) return 0;
        lock (state)
        {
            var remaining = state.LockedUntil - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                state.LockedUntil = default;
                state.Failures = 0;
                return 0;
            }
            return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        }
    }

    private string CreateSession(string identity)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _sessions[token] = new PinSession(identity, _timeProvider.GetUtcNow() + SessionDuration);
        return token;
    }

    private static PinResult Success(string? token = null) => new(true, token);
    private static PinResult Failure(string code, string message, int status) =>
        new(false, ErrorCode: code, Message: message, StatusCode: status);

    private static bool IsValidPin(string? pin) => pin is { Length: >= 6 and <= 12 } && pin.All(char.IsAsciiDigit);

    private static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPin(string pin, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
                !int.TryParse(parts[1], out var iterations) || iterations is < 100_000 or > 1_000_000) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private sealed record PinSession(string Identity, DateTimeOffset ExpiresAt);
    private sealed class AttemptState
    {
        public int Failures { get; set; }
        public DateTimeOffset LockedUntil { get; set; }
    }
}
