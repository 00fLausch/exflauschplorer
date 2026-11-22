using System.Diagnostics;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Services;

namespace Explorer.Host.Tests;

public sealed class AudioWaveformServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-Waveform-{Guid.NewGuid():N}");
    private readonly PathAccessPolicy _policy;

    public AudioWaveformServiceTests()
    {
        Directory.CreateDirectory(_root);
        _policy = new PathAccessPolicy([new ShareRoot(_root)]);
    }

    [Fact]
    public async Task RealDecodedWaveformCoversTheEntireTrackAndKeepsRelativeLoudness()
    {
        if (!ToolsAvailable()) return;
        var path = Path.Combine(_root, "DJ Set ä.wav");
        WriteWave(path, 8);
        await using var service = new AudioWaveformService(_policy);

        Assert.Null(service.GetOrStart(path));
        var waveform = await WaitAsync(service, path);

        Assert.Equal(1200, waveform.Peaks.Count);
        Assert.InRange(waveform.Duration, 7.99, 8.01);
        Assert.All(waveform.Peaks, value => Assert.InRange(value, 0, 1));
        var quietStart = waveform.Peaks.Take(300).Average();
        var loudEnd = waveform.Peaks.TakeLast(300).Average();
        Assert.InRange(quietStart, 0.08, 0.18);
        Assert.InRange(loudEnd, 0.8, 1);
        Assert.Same(waveform, service.GetOrStart(path));
    }

    [Fact]
    public async Task CacheInvalidatesWhenTheFileChangesAndReauthorizesCachedResults()
    {
        if (!ToolsAvailable()) return;
        var path = Path.Combine(_root, "changing.wav");
        WriteWave(path, 2);
        await using var service = new AudioWaveformService(_policy, cacheCapacity: 1);
        _ = await WaitAsync(service, path);

        WriteWave(path, 4);
        Assert.Null(service.GetOrStart(path));
        var updated = await WaitAsync(service, path);
        Assert.InRange(updated.Duration, 3.99, 4.01);

        _policy.ReplaceRoots([new ShareRoot(Path.Combine(_root, "different-share"))]);
        Assert.Throws<UnauthorizedAccessException>(() => service.GetOrStart(path));
    }

    [Fact]
    public async Task InvalidAudioProducesATerminalFailureInsteadOfEndlessProcessing()
    {
        if (!ToolsAvailable()) return;
        var path = Path.Combine(_root, "not-audio.mp3");
        await File.WriteAllTextAsync(path, "This is not an MP3.");
        await using var service = new AudioWaveformService(_policy);

        await Assert.ThrowsAsync<InvalidDataException>(() => WaitAsync(service, path));
        Assert.Throws<InvalidDataException>(() => service.GetOrStart(path));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetOrStart(Path.Combine(_root, "..", "outside.wav")));
    }

    [Fact]
    public async Task AnalysisTimeoutTerminatesTheBackgroundProcess()
    {
        if (!ToolsAvailable()) return;
        var path = Path.Combine(_root, "timeout.wav");
        WriteWave(path, 2);
        await using var service = new AudioWaveformService(_policy, analysisTimeout: TimeSpan.FromTicks(1));

        await Assert.ThrowsAsync<TimeoutException>(() => WaitAsync(service, path));
    }

    private static bool ToolsAvailable() => AudioWaveformService.FindExecutable("ffmpeg") is not null &&
        AudioWaveformService.FindExecutable("ffprobe") is not null;

    private static async Task<AudioWaveformDto> WaitAsync(AudioWaveformService service, string path)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            var result = service.GetOrStart(path);
            if (result is not null) return result;
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("Die echte FFmpeg-Wellenform wurde nicht rechtzeitig fertig.");
    }

    private static void WriteWave(string path, int seconds)
    {
        const int sampleRate = 8000;
        var samples = seconds * sampleRate;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var index = 0; index < samples; index++)
        {
            var amplitude = index < samples / 2 ? 0.1 : 0.8;
            writer.Write((short)(Math.Sin(index * 440 * Math.Tau / sampleRate) * amplitude * short.MaxValue));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
