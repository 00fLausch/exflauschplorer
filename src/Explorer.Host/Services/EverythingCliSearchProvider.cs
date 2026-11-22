using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Explorer.Host.Services;

public interface IEverythingSearchProvider
{
    bool IsAvailable { get; }
    IAsyncEnumerable<string> SearchPathsAsync(string root, string query, int offset, int count,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional adapter for voidtools ES. It never invokes a shell and only reads exported file paths.
/// The ES executable must be installed alongside Everything or the Explorer host.
/// </summary>
public sealed class EverythingCliSearchProvider : IEverythingSearchProvider
{
    private const int PageSize = 10_000;
    private const int DatabaseWaitMilliseconds = 5_000;
    private readonly string? _executablePath;

    public EverythingCliSearchProvider() : this(FindExecutable()) { }

    public EverythingCliSearchProvider(string? executablePath)
    {
        _executablePath = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
            ? Path.GetFullPath(executablePath)
            : null;
    }

    public bool IsAvailable => _executablePath is not null && File.Exists(_executablePath);

    public async IAsyncEnumerable<string> SearchPathsAsync(string root, string query, int offset, int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsAvailable) yield break;
        if (offset < 0 || count < 1) throw new ArgumentOutOfRangeException(nameof(offset));

        var requested = Math.Min(count, PageSize);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "WindowsMobileExplorer", "EverythingSearch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var exportPath = Path.Combine(temporaryDirectory, "results.csv");
        try
        {
            var start = new ProcessStartInfo(_executablePath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_executablePath!)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("-path");
            start.ArgumentList.Add(root);
            start.ArgumentList.Add("-count");
            start.ArgumentList.Add(requested.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-offset");
            start.ArgumentList.Add(offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-timeout");
            start.ArgumentList.Add(DatabaseWaitMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-full-path");
            start.ArgumentList.Add("-no-header");
            start.ArgumentList.Add("-csv");
            start.ArgumentList.Add("-utf8-bom");
            start.ArgumentList.Add("-export-csv");
            start.ArgumentList.Add(exportPath);
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(query);

            using var process = Process.Start(start) ?? throw new IOException("Everything ES konnte nicht gestartet werden.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var killOnCancellation = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Die optionale Everything-Suche hat länger als 30 Sekunden gedauert.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            _ = await standardOutput;
            var errorText = await standardError;
            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(errorText)
                    ? $"Everything ES wurde mit Status {process.ExitCode} beendet."
                    : $"Everything ES ist nicht verfügbar: {errorText.Trim()}");

            if (!File.Exists(exportPath)) yield break;
            await using var stream = new FileStream(exportPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var emitted = 0;
            await foreach (var path in ReadCsvFirstColumnAsync(reader, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    emitted++;
                    yield return path;
                }
            }
            if (emitted < requested) yield break;
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async IAsyncEnumerable<string> ReadCsvFirstColumnAsync(StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var field = new StringBuilder();
        string? firstField = null;
        var fieldIndex = 0;
        var inQuotes = false;
        var pendingQuote = false;
        var previousWasCarriageReturn = false;
        var buffer = new char[8192];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = buffer[index];
                if (previousWasCarriageReturn)
                {
                    previousWasCarriageReturn = false;
                    if (value == '\n') continue;
                }

                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (value == '"') { field.Append('"'); continue; }
                    inQuotes = false;
                }

                if (inQuotes)
                {
                    if (value == '"') pendingQuote = true;
                    else field.Append(value);
                    continue;
                }

                if (value == '"' && field.Length == 0) { inQuotes = true; continue; }
                if (value == ',')
                {
                    if (fieldIndex == 0) firstField = field.ToString();
                    field.Clear();
                    fieldIndex++;
                    continue;
                }
                if (value is '\r' or '\n')
                {
                    var path = fieldIndex == 0 ? field.ToString() : firstField;
                    if (!string.IsNullOrWhiteSpace(path)) yield return path;
                    firstField = null;
                    fieldIndex = 0;
                    field.Clear();
                    previousWasCarriageReturn = value == '\r';
                    continue;
                }
                field.Append(value);
            }
        }

        if (pendingQuote) inQuotes = false;
        var lastPath = fieldIndex == 0 ? field.ToString() : firstField;
        if (!string.IsNullOrWhiteSpace(lastPath)) yield return lastPath;
    }

    private static string? FindExecutable()
    {
        var directories = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything 1.5a"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything 1.5a"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Everything")
        };
        return directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.Combine(directory, "es.exe"))
            .FirstOrDefault(File.Exists);
    }
}
