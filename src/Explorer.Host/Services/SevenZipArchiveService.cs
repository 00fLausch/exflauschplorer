using System.Diagnostics;
using System.Globalization;

namespace Explorer.Host.Services;

public sealed record SevenZipArchiveEntry(string Path, long Size, bool IsDirectory, bool IsLink);

/// <summary>Reads and extracts 7-Zip entries without asking 7-Zip to choose output paths.</summary>
public sealed class SevenZipArchiveService
{
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
        start.ArgumentList.Add("-sccUTF-8");
        start.ArgumentList.Add("-bb0");
        start.ArgumentList.Add("-bd");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(archivePath);

        using var process = Start(start);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidDataException("Das 7-Zip-Archiv konnte nicht gelesen werden. Es ist möglicherweise beschädigt oder kennwortgeschützt.");

            return ParseListing(stdout);
        }
        catch
        {
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    public async Task<long> ExtractEntryAsync(
        string archivePath,
        string entryName,
        string targetPath,
        Action<long> onBytesRead,
        CancellationToken cancellationToken)
    {
        var start = CreateStartInfo("x");
        start.ArgumentList.Add("-so");
        start.ArgumentList.Add("-spd");
        start.ArgumentList.Add("-bb0");
        start.ArgumentList.Add("-bd");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add(entryName);

        using var process = Start(start);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        long copied = 0;
        try
        {
            await using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    onBytesRead(read);
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            _ = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidDataException("Eine Datei aus dem 7-Zip-Archiv konnte nicht gelesen werden.");
            return copied;
        }
        catch
        {
            try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw;
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
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add(command);
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

    private static IReadOnlyList<SevenZipArchiveEntry> ParseListing(string output)
    {
        var entries = new List<SevenZipArchiveEntry>();
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        var readingEntries = false;

        void AddEntry()
        {
            if (!readingEntries || !fields.TryGetValue("Path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                fields.Clear();
                return;
            }

            if (!long.TryParse(fields.GetValueOrDefault("Size", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new InvalidDataException("Das 7-Zip-Archiv enthält eine ungültige Dateigröße.");
            var folder = fields.GetValueOrDefault("Folder", "-").Equals("+", StringComparison.Ordinal) ||
                fields.GetValueOrDefault("Attributes", "").Contains('D');
            var link = fields.Any(field =>
                (field.Key.Contains("Link", StringComparison.OrdinalIgnoreCase) || field.Key.Contains("Reparse", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(field.Value));
            entries.Add(new SevenZipArchiveEntry(path, size, folder, link));
            fields.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
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
            if (separator > 0)
                fields[line[..separator]] = line[(separator + 3)..];
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
