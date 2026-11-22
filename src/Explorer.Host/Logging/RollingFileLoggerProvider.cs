using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Explorer.Host.Logging;

public sealed class RollingFileLoggerProvider(string directory, long maximumBytes = 10 * 1024 * 1024, int retainedFiles = 8) : ILoggerProvider
{
    private readonly object _sync = new();
    private bool _disposed;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, EventId eventId, string message, IReadOnlyDictionary<string, object?>? properties, Exception? exception)
    {
        if (_disposed || level < LogLevel.Information) return;
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= maximumBytes)
                {
                    var index = 1;
                    string rotated;
                    do { rotated = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}-{index++:00}.jsonl"); }
                    while (File.Exists(rotated));
                    File.Move(path, rotated);
                }

                var record = new LogRecord(DateTimeOffset.Now, level.ToString(), category, eventId.Id, message, properties,
                    exception is null ? null : $"{exception.GetType().Name}: {exception.Message}");
                File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
                var files = new DirectoryInfo(directory).GetFiles("app-*.jsonl").OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
                foreach (var stale in files.Skip(Math.Max(1, retainedFiles))) stale.Delete();
            }
        }
        catch { }
    }

    public void Dispose() => _disposed = true;

    private sealed record LogRecord(DateTimeOffset Timestamp, string Level, string Category, int EventId, string Message,
        IReadOnlyDictionary<string, object?>? Properties, string? Exception);

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, eventId, formatter(state, exception), ExtractProperties(state), exception);
        }

        private static IReadOnlyDictionary<string, object?>? ExtractProperties<TState>(TState state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return null;
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                if (key == "{OriginalFormat}") continue;
                properties[key] = value switch
                {
                    null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                        float or double or decimal or DateTime or DateTimeOffset or Guid => value,
                    Enum enumValue => enumValue.ToString(),
                    _ => value.ToString()
                };
            }
            return properties.Count == 0 ? null : properties;
        }
    }
}
