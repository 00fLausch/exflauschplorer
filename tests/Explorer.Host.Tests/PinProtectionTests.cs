using System.Text.Json;
using Explorer.Host.Configuration;
using Explorer.Host.Security;
using Microsoft.AspNetCore.Http;

namespace Explorer.Host.Tests;

public sealed class PinProtectionTests
{
    [Fact]
    public void PinIsPersistedAsPbkdf2HashAndUnlockIsBoundToTailnetIdentity()
    {
        var settings = new ExplorerSettings();
        var saves = 0;
        var pins = new PinProtectionService(settings, _ => saves++);

        var configured = pins.Configure("owner@example.com", "123456", null);

        Assert.True(configured.Success);
        Assert.NotNull(configured.SessionToken);
        Assert.StartsWith("pbkdf2-sha256$310000$", settings.PinHash);
        Assert.DoesNotContain("123456", settings.PinHash);
        Assert.Equal(1, saves);
        Assert.True(pins.GetStatus("owner@example.com", configured.SessionToken).IsUnlocked);
        Assert.False(pins.GetStatus("other@example.com", configured.SessionToken).IsUnlocked);
    }

    [Fact]
    public void InvalidPinCannotBeConfiguredAndWrongPinAttemptsAreRateLimited()
    {
        var settings = new ExplorerSettings();
        var pins = new PinProtectionService(settings, _ => { });
        Assert.Equal("pin_invalid_format", pins.Configure("owner@example.com", "12ab", null).ErrorCode);
        Assert.Null(settings.PinHash);
        Assert.True(pins.Configure("owner@example.com", "849201", null).Success);

        for (var attempt = 0; attempt < 4; attempt++)
            Assert.Equal("pin_invalid", pins.Unlock("owner@example.com", "000000").ErrorCode);
        var limited = pins.Unlock("owner@example.com", "000000");

        Assert.Equal("pin_rate_limited", limited.ErrorCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
        Assert.Equal(900, pins.GetStatus("owner@example.com", null).RetryAfterSeconds);
    }

    [Fact]
    public void SessionCanBeRevokedAndPinDisabledOnlyFromAnUnlockedSession()
    {
        var settings = new ExplorerSettings();
        var pins = new PinProtectionService(settings, _ => { });
        var configured = pins.Configure("owner@example.com", "849201", null);
        Assert.True(configured.Success);

        var locked = pins.Lock("owner@example.com", configured.SessionToken);
        Assert.True(locked);
        Assert.False(pins.GetStatus("owner@example.com", configured.SessionToken).IsUnlocked);
        Assert.Equal("pin_locked", pins.Disable("owner@example.com", configured.SessionToken).ErrorCode);

        var unlocked = pins.Unlock("owner@example.com", "849201");
        Assert.True(unlocked.Success);
        Assert.True(pins.Disable("owner@example.com", unlocked.SessionToken).Success);
        Assert.Null(settings.PinHash);
    }

    [Fact]
    public void ChangingPinRevokesPreviousDeviceSessions()
    {
        var settings = new ExplorerSettings();
        var pins = new PinProtectionService(settings, _ => { });
        var firstDevice = pins.Configure("owner@example.com", "849201", null);
        Assert.True(firstDevice.Success);

        var replacement = pins.Configure("owner@example.com", "123789", firstDevice.SessionToken);

        Assert.True(replacement.Success);
        Assert.False(pins.GetStatus("owner@example.com", firstDevice.SessionToken).IsUnlocked);
        Assert.True(pins.GetStatus("owner@example.com", replacement.SessionToken).IsUnlocked);
        Assert.Equal("pin_invalid", pins.Unlock("owner@example.com", "849201").ErrorCode);
    }

    [Fact]
    public async Task MiddlewareBlocksProtectedApiButAllowsStatusAndUnlockEndpoints()
    {
        var settings = new ExplorerSettings();
        var pins = new PinProtectionService(settings, _ => { });
        Assert.True(pins.Configure("owner@example.com", "849201", null).Success);
        var reachedNext = false;
        var middleware = new PinProtectionMiddleware(_ => { reachedNext = true; return Task.CompletedTask; });
        var blockedContext = CreateContext("/api/v1/entries");

        await middleware.InvokeAsync(blockedContext, pins);

        Assert.False(reachedNext);
        Assert.Equal(StatusCodes.Status423Locked, blockedContext.Response.StatusCode);
        blockedContext.Response.Body.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<PinError>(blockedContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("pin_locked", body?.Code);

        reachedNext = false;
        var statusContext = CreateContext("/api/v1/security/pin/status");
        await middleware.InvokeAsync(statusContext, pins);
        Assert.True(reachedNext);

        reachedNext = false;
        var unlockContext = CreateContext("/api/v1/security/pin/unlock");
        await middleware.InvokeAsync(unlockContext, pins);
        Assert.True(reachedNext);
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Items["Explorer.User"] = "owner@example.com";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed record PinError(string Message, string? Code);
}
