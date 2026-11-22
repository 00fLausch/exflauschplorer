using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Explorer.Core.Models;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

/// <summary>Decodes the complete selected track into bounded, normalized RMS bins.</summary>
public sealed class AudioWaveformService : IAsyncDisposable
{
    public const int PeakCount = 1200;
    private const int SampleRate = 8000;
    private const string InputFormats = "aac,ac3,aiff,ape,asf,au,avi,flac,flv,matroska,webm,mov,mp4,m4a,3gp,3g2,mj2,mp3,mpeg,mpegts,ogg,wav,wv";
    private readonly PathAccessPolicy _policy;
    private readonly string? _ffmpeg;
    private readonly string? _ffprobe;
    private readonly int _capacity;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _workers = new(2, 2);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _pendingJobs = [];
    private bool _disposed;

    public AudioWaveformService(PathAccessPolicy policy, string? ffmpegPath = null, string? ffprobePath = null,
        TimeSpan? analysisTimeout = null, int cacheCapacity = 128)
    {
        _policy = policy;
        _ffmpeg = ffmpegPath ?? FindExecutable("ffmpeg");
        var adjacentProbe = _ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(_ffmpeg)!, "ffprobe.exe");
        _ffprobe = ffprobePath ?? (File.Exists(adjacentProbe) ? adjacentProbe : FindExecutable("ffprobe"));
        _timeout = analysisTimeout ?? TimeSpan.FromMinutes(20);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(analysisTimeout));
        if (cacheCapacity < 1) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        _capacity = cacheCapacity;
    }

    /// <returns>Null while a bounded background job is queued/running; the cached result when ready.</returns>
    public AudioWaveformDto? GetOrStart(string path)
    {
        // Reauthorize every poll, including cached results after share settings change.
        var absolute = _policy.Authorize(path);
        var info = new FileInfo(absolute);
        if (!info.Exists) throw new FileNotFoundException("Die Audiodatei wurde nicht gefunden.", absolute);
        if (_ffmpeg is null || _ffprobe is null)
            throw new WaveformUnavailableException("Für die Wellenform werden FFmpeg und FFprobe benötigt.");
        var fingerprint = new Fingerprint(info.Length, info.LastWriteTimeUtc.Ticks);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(absolute, out var existing))
            {
                if (existing.Fingerprint == fingerprint)
                {
                    existing.LastUsed = DateTime.UtcNow;
                    return existing.Work.IsCompleted ? existing.Work.GetAwaiter().GetResult() : null;
                }
                Remove(absolute, existing);
            }
            while (_cache.Count >= _capacity)
            {
                // Prefer finished jobs; all pending requests are also bounded by capacity.
                var oldest = _cache.OrderBy(item => !item.Value.Work.IsCompleted)
                    .ThenBy(item => item.Value.LastUsed).First();
                Remove(oldest.Key, oldest.Value);
            }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var work = Task.Run(() => AnalyzeAsync(absolute, cancellation.Token));
            _pendingJobs.Add(work);
            _ = work.ContinueWith(completed =>
            {
                _ = completed.Exception;
                lock (_gate) _pendingJobs.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _cache.Add(absolute, new CacheEntry(fingerprint, work, cancellation));
            return null;
        }
    }

    private async Task<AudioWaveformDto> AnalyzeAsync(string path, CancellationToken cancellationToken)
    {
        await _workers.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                path = _policy.Authorize(path);
                var duration = await ProbeDurationAsync(path, timeout.Token);
                return await DecodeAsync(path, duration, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Die Wellenform-Berechnung hat zu lange gedauert. Bitte versuche es erneut.");
            }
        }
        finally { _workers.Release(); }
    }

    private async Task<double> ProbeDurationAsync(string path, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(_ffprobe!);
        Add(start, "-v", "error", "-protocol_whitelist", "file,pipe", "-format_whitelist", InputFormats,
            "-select_streams", "a:0", "-show_entries", "stream=duration:format=duration", "-of", "json", path);
        using var process = Start(start);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = DrainErrorsAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var json = await stdout;
            _ = await stderr;
            if (process.ExitCode != 0) throw DecodeFailure();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                throw new InvalidDataException("Diese Datei enthält keine abspielbare Audiospur.");
            if (root.TryGetProperty("format", out var format) && TryDuration(format, out var duration)) return duration;
            if (TryDuration(streams[0], out duration)) return duration;
            throw new InvalidDataException("Die Länge dieser Audiodatei konnte nicht ermittelt werden.");
        }
        catch
        {
            await KillAsync(process);
            throw;
        }
    }

    private async Task<AudioWaveformDto> DecodeAsync(string path, double duration, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(_ffmpeg!);
        Add(start, "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1",
            "-protocol_whitelist", "file,pipe", "-format_whitelist", InputFormats, "-i", path,
            "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le", "pipe:1");
        using var process = Start(start);
        var stderr = DrainErrorsAsync(process.StandardError, cancellationToken);
        try
        {
            var energies = new double[PeakCount];
            var counts = new long[PeakCount];
            var binsPerSample = PeakCount / (duration * SampleRate);
            var buffer = new byte[32 * 1024];
            long sampleIndex = 0;
            var carry = 0;
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(carry), cancellationToken)) > 0)
            {
                var available = carry + read;
                var complete = available - available % sizeof(float);
                for (var offset = 0; offset < complete; offset += sizeof(float))
                {
                    var sample = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset, sizeof(float)));
                    var bin = (int)Math.Min(PeakCount - 1, sampleIndex * binsPerSample);
                    if (float.IsFinite(sample)) energies[bin] += (double)sample * sample;
                    counts[bin]++;
                    sampleIndex++;
                }
                carry = available - complete;
                if (carry != 0) Buffer.BlockCopy(buffer, complete, buffer, 0, carry);
            }
            await process.WaitForExitAsync(cancellationToken);
            _ = await stderr;
            if (process.ExitCode != 0 || sampleIndex == 0) throw DecodeFailure();
            var peaks = new double[PeakCount];
            double maximum = 0;
            for (var index = 0; index < PeakCount; index++)
            {
                peaks[index] = counts[index] == 0 ? 0 : Math.Sqrt(energies[index] / counts[index]);
                maximum = Math.Max(maximum, peaks[index]);
            }
            if (maximum > 0)
                for (var index = 0; index < PeakCount; index++) peaks[index] = Math.Clamp(peaks[index] / maximum, 0, 1);
            return new AudioWaveformDto(peaks, duration);
        }
        catch
        {
            await KillAsync(process);
            throw;
        }
    }

    private static bool TryDuration(JsonElement element, out double duration)
    {
        duration = 0;
        return element.TryGetProperty("duration", out var value) &&
            double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration) &&
            double.IsFinite(duration) && duration > 0;
    }

    private static InvalidDataException DecodeFailure() => new("Für diese Datei konnte keine Audio-Wellenform berechnet werden.");

    private static ProcessStartInfo CreateStartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static void Add(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }

    private static Process Start(ProcessStartInfo start) => Process.Start(start)
        ?? throw new WaveformUnavailableException("FFmpeg konnte für die Wellenform nicht gestartet werden.");

    private static async Task<string> DrainErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        // Drain even malformed-file diagnostics without retaining unbounded stderr.
        var result = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            if (result.Length < 4096) result.Append(buffer, 0, Math.Min(read, 4096 - result.Length));
        return result.ToString();
    }

    private static async Task KillAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
    }

    public static string? FindExecutable(string name)
    {
        var overridePath = Environment.GetEnvironmentVariable("WINDOWS_MOBILE_EXPLORER_" + name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return Path.GetFullPath(overridePath);
        var executable = name + ".exe";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", executable),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", executable),
            Path.Combine(AppContext.BaseDirectory, executable),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", executable)
        };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory.Trim().Trim('"'), executable));
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages)) return null;
        try
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*", SearchOption.TopDirectoryOnly))
                foreach (var version in Directory.EnumerateDirectories(package, "ffmpeg*", SearchOption.TopDirectoryOnly))
                {
                    var candidate = Path.Combine(version, "bin", executable);
                    if (File.Exists(candidate)) return candidate;
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private void Remove(string path, CacheEntry entry)
    {
        _cache.Remove(path);
        entry.Cancellation.Cancel();
        _ = entry.Work.ContinueWith(_ => entry.Cancellation.Dispose(), TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            pending = _pendingJobs.ToArray();
        }
        try { await Task.WhenAll(pending); } catch (Exception) { /* Completed/failed jobs are already observed. */ }
        lock (_gate)
        {
            foreach (var entry in _cache.Values) entry.Cancellation.Dispose();
            _cache.Clear();
        }
        _workers.Dispose();
        _shutdown.Dispose();
    }

    private readonly record struct Fingerprint(long Length, long ModifiedTicks);
    private sealed class CacheEntry(Fingerprint fingerprint, Task<AudioWaveformDto> work, CancellationTokenSource cancellation)
    {
        public Fingerprint Fingerprint { get; } = fingerprint;
        public Task<AudioWaveformDto> Work { get; } = work;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }
}

public sealed class WaveformUnavailableException(string message) : InvalidOperationException(message);
