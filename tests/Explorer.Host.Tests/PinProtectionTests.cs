using System.Text.Json;
using Explorer.Host.Configuration;
using Explorer.Host.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task LockEndpointAbortsOnlyStreamsOfTheCurrentSession()
    {
        const string owner = "owner@example.com";
        const string other = "other@example.com";
        const string pin = "849201";
        var settings = new ExplorerSettings();
        var pins = new PinProtectionService(settings, _ => { });
        var streams = new ExplorerLiveStreams(pins);
        var trust = new ExplorerRequestTrust(settings, isolatedTest: true);
        var first = pins.Configure(owner, pin, null);
        var second = pins.Unlock(owner, pin);
        var otherSession = pins.Unlock(other, pin);
        Assert.True(first.Success && second.Success && otherSession.Success);

        var firstStream = new TrackedStream();
        var secondStream = new TrackedStream();
        var otherStream = new TrackedStream();
        var firstTask = TrackStreamAsync(streams, firstStream, owner, first.SessionToken);
        var secondTask = TrackStreamAsync(streams, secondStream, owner, second.SessionToken);
        var otherTask = TrackStreamAsync(streams, otherStream, other, otherSession.SessionToken);

        var context = CreateContext("/api/v1/security/pin/lock", owner, first.SessionToken);
        _ = PinEndpoints.Lock(context, pins, streams, trust, NullLogger.Instance);

        Assert.True(await CompletesWithin(firstStream.Aborted.Task));
        Assert.False(secondStream.Aborted.Task.IsCompleted);
        Assert.False(otherStream.Aborted.Task.IsCompleted);

        streams.Revoke(all: true);
        await Task.WhenAll(firstTask, secondTask, otherTask);
    }

    [Fact]
    public async Task ConfigureAndDisableEndpointsAbortAllStreams()
    {
        const string owner = "owner@example.com";
        var settings = new ExplorerSettings();
        var pins = new PinProtectionService(settings, _ => { });
        var streams = new ExplorerLiveStreams(pins);
        var trust = new ExplorerRequestTrust(settings, isolatedTest: true);
        var first = pins.Configure(owner, "849201", null);
        var second = pins.Unlock(owner, "849201");
        Assert.True(first.Success && second.Success);

        var firstStream = new TrackedStream();
        var secondStream = new TrackedStream();
        var firstTask = TrackStreamAsync(streams, firstStream, owner, first.SessionToken);
        var secondTask = TrackStreamAsync(streams, secondStream, owner, second.SessionToken);

        var changeContext = CreateContext("/api/v1/security/pin/configure", owner, first.SessionToken);
        _ = PinEndpoints.Configure(changeContext, new PinRequest("123789"), pins, streams, trust, NullLogger.Instance);

        Assert.True(await CompletesWithin(firstStream.Aborted.Task));
        Assert.True(await CompletesWithin(secondStream.Aborted.Task));
        await Task.WhenAll(firstTask, secondTask);

        var third = pins.Unlock(owner, "123789");
        var fourth = pins.Unlock(owner, "123789");
        Assert.True(third.Success && fourth.Success);
        var thirdStream = new TrackedStream();
        var fourthStream = new TrackedStream();
        var thirdTask = TrackStreamAsync(streams, thirdStream, owner, third.SessionToken);
        var fourthTask = TrackStreamAsync(streams, fourthStream, owner, fourth.SessionToken);

        var disableContext = CreateContext("/api/v1/security/pin/disable", owner, third.SessionToken);
        _ = PinEndpoints.Disable(disableContext, pins, streams, trust, NullLogger.Instance);

        Assert.True(await CompletesWithin(thirdStream.Aborted.Task));
        Assert.True(await CompletesWithin(fourthStream.Aborted.Task));
        await Task.WhenAll(thirdTask, fourthTask);
        Assert.Null(settings.PinHash);
    }

    private static async Task<bool> CompletesWithin(Task task) =>
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))) == task;

    private static async Task TrackStreamAsync(ExplorerLiveStreams streams, TrackedStream tracked,
        string identity, string? session)
    {
        var context = CreateContext("/api/v1/watch", identity, session);
        context.Features.Set<IHttpRequestLifetimeFeature>(new AbortFeature(tracked.Aborted));
        await streams.InvokeAsync(context, async _ =>
        {
            await tracked.Aborted.Task;
            tracked.Finished.TrySetResult(true);
        });
    }

    private static DefaultHttpContext CreateContext(string path) =>
        CreateContext(path, "owner@example.com", null);

    private static DefaultHttpContext CreateContext(string path, string identity, string? session)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Items["Explorer.User"] = identity;
        context.Response.Body = new MemoryStream();
        if (session is not null) context.Request.Headers.Cookie = $"Explorer.PinSession={session}";
        return context;
    }

    private sealed class TrackedStream
    {
        public TaskCompletionSource<bool> Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AbortFeature(TaskCompletionSource<bool> signal) : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource _source = new();
        public CancellationToken RequestAborted
        {
            get => _source.Token;
            set { }
        }
        public void Abort()
        {
            signal.TrySetResult(true);
            _source.Cancel();
        }
    }

    private sealed record PinError(string Message, string? Code);
}
