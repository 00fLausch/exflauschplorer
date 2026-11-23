using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Explorer.Host.Services;

public sealed record SevenZipArchiveEntry(string Path, long Size, bool IsDirectory, bool IsLink);

/// <summary>Reads and extracts 7-Zip entries without asking 7-Zip to choose output paths.</summary>
public sealed class SevenZipArchiveService
{
    private const int MaximumEntries = 10_000;
    private const int MaximumListingCharacters = 8 * 1024 * 1024;
    private const long MaximumExpandedBytes = 20L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromMinutes(10);
    private readonly string? _executablePath;

    public SevenZipArchiveService() => _executablePath = FindExecutable();

    public bool IsAvailable => _executablePath is not null;

    public static string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<IReadOnlyList<SevenZipArchiveEntry>> ListEntriesAsync(string archivePath, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo("l");
        start.ArgumentList.Add("-slt");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(archivePath);

        var listing = await RunAsync(start, ListingTimeout,
            (process, token) => ReadListingAsync(process.StandardOutput, token),
            "Das 7-Zip-Archiv konnte nicht gelesen werden. Es ist möglicherweise beschädigt oder kennwortgeschützt.",
            cancellationToken);
        return ParseListing(listing, cancellationToken);
    }

    public async Task<long> ExtractEntryAsync(
        string archivePath,
        string entryName,
        string targetPath,
        Action<long> onBytesRead,
        CancellationToken cancellationToken,
        long? expectedSize = null)
    {
        if (expectedSize is < 0 or > MaximumExpandedBytes)
            throw new InvalidDataException("Die angegebene Dateigröße überschreitet das 7-Zip-Limit.");

        var start = CreateStartInfo("x");
        start.ArgumentList.Add("-so");
        start.ArgumentList.Add("-spd");
        start.ArgumentList.Add("-ssc");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add(entryName);

        var created = false;
        try
        {
            return await RunAsync(start, ExtractionTimeout, async (process, token) =>
            {
                await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                created = true;
                return await CopyEntryAsync(process.StandardOutput.BaseStream, output, expectedSize, onBytesRead, token);
            }, "Die Datei aus dem 7-Zip-Archiv konnte nicht gelesen werden. Kennwortgeschützte Archive werden nicht unterstützt.",
                cancellationToken);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(targetPath); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    public async Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var start = CreateStartInfo("x");
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-snld");
        start.ArgumentList.Add("-spd");
        start.ArgumentList.Add($"-o{outputDirectory}");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(archivePath);

        await RunAsync(start, ExtractionTimeout,
            async (process, token) =>
            {
                await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
                return true;
            },
            "Das 7-Zip-Archiv konnte nicht vollständig entpackt werden. Es ist möglicherweise beschädigt oder kennwortgeschützt.",
            cancellationToken);
    }

    private static async Task<long> CopyEntryAsync(Stream input, Stream output, long? expectedSize,
        Action<long> onBytesRead, CancellationToken token)
    {
        var limit = expectedSize ?? MaximumExpandedBytes;
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (read > limit - copied)
                throw new InvalidDataException("Eine entpackte Datei überschreitet die im Archiv angegebene Größe oder das Entpacklimit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            copied += read;
            onBytesRead(read);
        }
        token.ThrowIfCancellationRequested();
        if (expectedSize.HasValue && copied != expectedSize.Value)
            throw new InvalidDataException("Die entpackte Dateigröße stimmt nicht mit dem 7-Zip-Inhaltsverzeichnis überein.");
        return copied;
    }

    private static async Task<string> ReadListingAsync(StreamReader reader, CancellationToken token)
    {
        var listing = new StringBuilder();
        var buffer = new char[8 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (read > MaximumListingCharacters - listing.Length)
                throw new InvalidDataException("Das Inhaltsverzeichnis des 7-Zip-Archivs ist zu groß.");
            listing.Append(buffer, 0, read);
        }
        return listing.ToString();
    }

    private static async Task<T> RunAsync<T>(ProcessStartInfo start, TimeSpan timeout,
        Func<Process, CancellationToken, Task<T>> readOutput, string errorMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        using var process = Start(start);
        using var stop = lifetime.Token.Register(() => TryKill(process));

        Task drainDiagnostics = Task.CompletedTask;
        try
        {
            process.StandardInput.Close();
            drainDiagnostics = process.StandardError.BaseStream.CopyToAsync(Stream.Null, lifetime.Token);
            var result = await readOutput(process, lifetime.Token);
            await process.WaitForExitAsync(lifetime.Token);
            await drainDiagnostics;
            lifetime.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException(errorMessage);
            return result;
        }
        catch (Exception error) when (lifetime.IsCancellationRequested &&
            error is OperationCanceledException or IOException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("7-Zip hat die maximale Laufzeit überschritten.", error);
        }
        finally
        {
            if (!process.HasExited || !drainDiagnostics.IsCompleted)
            {
                lifetime.Cancel();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(cleanup.Token); }
                catch (OperationCanceledException) { }
            }
            try { await drainDiagnostics.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception cleanup) when (cleanup is OperationCanceledException or IOException or InvalidOperationException or TimeoutException) { }
        }
    }

    private ProcessStartInfo CreateStartInfo(string command)
    {
        if (_executablePath is null)
            throw new InvalidOperationException("Zum Entpacken von 7z-Dateien muss 7-Zip installiert sein.");

        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add("-t7z");
        start.ArgumentList.Add("-sccUTF-8");
        start.ArgumentList.Add("-bb0");
        start.ArgumentList.Add("-bd");
        // A nonempty password prevents the CLI from prompting even when the archive is encrypted.
        start.ArgumentList.Add($"-p{Guid.NewGuid():N}");
        return start;
    }

    private static Process Start(ProcessStartInfo start)
    {
        try
        {
            return Process.Start(start) ?? throw new IOException("7-Zip konnte nicht gestartet werden.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException("7-Zip konnte nicht gestartet werden. Prüfe die Installation.");
        }
    }

    private static IReadOnlyList<SevenZipArchiveEntry> ParseListing(string output, CancellationToken token)
    {
        var entries = new List<SevenZipArchiveEntry>();
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        var readingEntries = false;
        var link = false;
        long totalBytes = 0;

        void AddEntry()
        {
            if (!readingEntries || !fields.TryGetValue("Path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                fields.Clear();
                link = false;
                return;
            }

            if (!long.TryParse(fields.GetValueOrDefault("Size", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new InvalidDataException("Das 7-Zip-Archiv enthält eine ungültige Dateigröße.");
            if (entries.Count >= MaximumEntries || size > MaximumExpandedBytes - totalBytes)
                throw new InvalidDataException("Das 7-Zip-Archiv enthält mehr als 10.000 Einträge oder mehr als 20 GB entpackte Daten.");
            if (fields.GetValueOrDefault("Encrypted", "-") == "+")
                throw new InvalidDataException("Kennwortgeschützte 7-Zip-Archive werden nicht unterstützt.");
            totalBytes += size;
            var folder = fields.GetValueOrDefault("Folder", "-").Equals("+", StringComparison.Ordinal) ||
                fields.GetValueOrDefault("Attributes", "").Contains('D');
            entries.Add(new SevenZipArchiveEntry(path, size, folder, link));
            fields.Clear();
            link = false;
        }

        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (line.StartsWith("----------", StringComparison.Ordinal))
            {
                AddEntry();
                readingEntries = true;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                AddEntry();
                continue;
            }
            if (!readingEntries) continue;

            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator <= 0) continue;
            var key = line[..separator];
            var value = line[(separator + 3)..];
            if (key is "Path" or "Size" or "Folder" or "Attributes" or "Encrypted")
                fields[key] = value;
            else if ((key.Contains("Link", StringComparison.OrdinalIgnoreCase) ||
                      key.Contains("Reparse", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(value))
                link = true;
        }
        AddEntry();
        if (!readingEntries || entries.Count == 0)
            throw new InvalidDataException("Das 7-Zip-Archiv ist leer oder hat ein ungültiges Inhaltsverzeichnis.");
        return entries;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
