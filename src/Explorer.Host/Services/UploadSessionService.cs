using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Configuration;

namespace Explorer.Host.Services;

public sealed class UploadConflictException(string name) : IOException($"„{name}“ ist im Zielordner bereits vorhanden. Wähle Ersetzen, Überspringen oder Beide behalten.");

/// <summary>Resumable, checksummed uploads. Each chunk is verified before its committed offset is persisted.</summary>
public sealed class UploadSessionService
{
    private const long MaximumUploadBytes = 20L * 1024 * 1024 * 1024;
    private const int MaximumChunkBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<string, UploadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly PathAccessPolicy policy;
    private readonly IRecycleBinService recycleBin;
    private readonly ILogger<UploadSessionService> logger;
    private readonly string _metadataDirectory;

    public UploadSessionService(PathAccessPolicy policy, IRecycleBinService recycleBin,
        ILogger<UploadSessionService> logger, string? metadataDirectory = null)
    {
        this.policy = policy;
        this.recycleBin = recycleBin;
        this.logger = logger;
        _metadataDirectory = metadataDirectory ?? Path.Combine(SettingsStore.DataDirectory, "uploads");
        LoadExisting();
    }

    public IReadOnlyList<UploadSessionDto> List(string owner) => _sessions.Values
        .Where(session => session.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))
        .Select(session => session.Snapshot())
        .OrderByDescending(session => session.CreatedAt)
        .ToArray();

    public async Task<UploadSessionDto> CreateAsync(UploadCreateRequest request, string owner, CancellationToken cancellationToken)
    {
        if (request.Size < 0 || request.Size > MaximumUploadBytes) throw new ArgumentException("Die Datei darf höchstens 20 GB groß sein.");
        var name = ValidateFileName(request.FileName);
        var destination = policy.Authorize(request.Destination, write: true);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException();
        var target = policy.Authorize(Path.Combine(destination, name), write: true);
        if (!string.Equals(Path.GetDirectoryName(target), destination, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Der Dateiname ist ungültig.");

        if (!string.IsNullOrWhiteSpace(request.ResumeId))
        {
            var existing = GetOwned(request.ResumeId, owner);
            await existing.Gate.WaitAsync(cancellationToken);
            try
            {
                if (!string.Equals(existing.FileName, name, StringComparison.OrdinalIgnoreCase) || existing.Size != request.Size ||
                    !string.Equals(existing.Destination, destination, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Die ausgewählte Datei passt nicht zum gespeicherten Upload. Starte ihn neu.");
                if (existing.State is not ("uploading" or "ready")) throw new InvalidOperationException("Dieser Upload kann nicht fortgesetzt werden.");
                if (!File.Exists(existing.StagePath)) throw new FileNotFoundException("Der zwischengespeicherte Upload ist nicht mehr vorhanden.");
                var length = new FileInfo(existing.StagePath).Length;
                if (length > existing.Size) length = existing.Size;
                if (length != existing.Offset)
                {
                    using var stage = new FileStream(existing.StagePath, FileMode.Open, FileAccess.Write, FileShare.None);
                    stage.SetLength(length);
                    existing.Offset = length;
                    existing.State = length == existing.Size ? "ready" : "uploading";
                    await PersistAsync(existing, cancellationToken);
                }
                logger.LogInformation("Upload {UploadId} von {Owner} wird für {FileName} fortgesetzt ({Offset}/{Size} Bytes)",
                    existing.Id, owner, existing.FileName, existing.Offset, existing.Size);
                return existing.Snapshot();
            }
            finally { existing.Gate.Release(); }
        }

        var id = Guid.NewGuid().ToString("N");
        var session = new UploadSession(id, name, destination, target, request.Size, 0, request.Size == 0 ? "ready" : "uploading", DateTimeOffset.UtcNow, owner);
        await using (var stage = new FileStream(session.StagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            File.SetAttributes(session.StagePath, FileAttributes.Hidden);
            await stage.FlushAsync(cancellationToken);
        }
        _sessions[id] = session;
        await PersistAsync(session, cancellationToken);
        logger.LogInformation("Upload {UploadId} von {Owner} für {FileName} nach {Destination} angelegt ({Size} Bytes)",
            id, owner, name, destination, request.Size);
        return session.Snapshot();
    }

    public async Task<UploadChunkDto> WriteChunkAsync(string id, string owner, long offset, int expectedLength,
        string expectedSha256, Stream input, CancellationToken cancellationToken)
    {
        if (expectedLength < 1 || expectedLength > MaximumChunkBytes) throw new ArgumentException("Ein Upload-Teil darf höchstens 16 MB groß sein.");
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)) throw new ArgumentException("Die Prüfsumme des Upload-Teils ist ungültig.");
        var session = GetOwned(id, owner);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.State != "uploading") throw new InvalidOperationException("Dieser Upload wartet bereits auf den Abschluss oder ist abgeschlossen.");
            if (offset != session.Offset) throw new UploadOffsetException(session.Offset);
            if (expectedLength > session.Size - offset) throw new ArgumentException("Der Upload-Teil ist größer als die verbleibende Datei.");
            var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            try
            {
                await using (var output = new FileStream(session.StagePath, FileMode.Open, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    output.Position = offset;
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        written += read;
                        if (written > expectedLength) throw new InvalidDataException("Der Upload-Teil ist länger als angegeben.");
                        hash.AppendData(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    if (written != expectedLength) throw new EndOfStreamException("Die Verbindung wurde während des Uploads unterbrochen.");
                    var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(expectedSha256)))
                        throw new InvalidDataException("Die Prüfsumme eines Upload-Teils stimmt nicht überein.");
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
            }
            catch
            {
                try { using var repair = new FileStream(session.StagePath, FileMode.Open, FileAccess.Write, FileShare.None); repair.SetLength(offset); } catch (Exception cleanup) { logger.LogWarning(cleanup, "Upload-Teil {UploadId} konnte nicht zurückgesetzt werden", id); }
                throw;
            }

            session.Offset += written;
            if (session.Offset == session.Size) session.State = "ready";
            await PersistAsync(session, cancellationToken);
            logger.LogInformation("Upload-Teil {UploadId} von {Owner} geprüft und gespeichert ({Offset}/{Size} Bytes)",
                id, owner, session.Offset, session.Size);
            return new UploadChunkDto(session.Id, session.Offset, session.State == "ready");
        }
        finally { session.Gate.Release(); }
    }

    public async Task<UploadCommitDto> CommitAsync(string id, string owner, string? conflictMode, CancellationToken cancellationToken)
    {
        var session = GetOwned(id, owner);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.State != "ready" || session.Offset != session.Size) throw new InvalidOperationException("Der Upload ist noch nicht vollständig.");
            var digest = await ComputeSha256Async(session.StagePath, cancellationToken);
            var target = policy.Authorize(session.TargetPath, write: true);
            var mode = conflictMode?.Trim().ToLowerInvariant();
            if (File.Exists(target) || Directory.Exists(target))
            {
                if (mode is not ("replace" or "skip" or "keep-both")) throw new UploadConflictException(Path.GetFileName(target));
                if (mode == "skip")
                {
                    await RemoveSessionAsync(session, cancellationToken);
                    logger.LogInformation("Upload {UploadId} von {Owner} übersprungen; Zieldatei {Path} blieb bestehen", id, owner, target);
                    return new UploadCommitDto(false, true, null, digest);
                }
                if (mode == "keep-both") target = NextAvailablePath(target);
                else await recycleBin.DeleteAsync(target, cancellationToken);
            }

            File.Move(session.StagePath, target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.Hidden);
            var result = new UploadCommitDto(true, false, target, digest);
            await RemoveMetadataAsync(session, cancellationToken);
            _sessions.TryRemove(session.Id, out _);
            logger.LogInformation("Upload {UploadId} von {Owner} nach {Path} abgeschlossen, SHA-256 {Sha256}", id, owner, target, digest);
            return result;
        }
        finally { session.Gate.Release(); }
    }

    public async Task CancelAsync(string id, string owner, CancellationToken cancellationToken)
    {
        var session = GetOwned(id, owner);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            await RemoveSessionAsync(session, cancellationToken);
            logger.LogInformation("Upload {UploadId} von {Owner} abgebrochen und temporäre Daten entfernt", id, owner);
        }
        finally { session.Gate.Release(); }
    }

    private UploadSession GetOwned(string id, string owner)
    {
        if (!Guid.TryParseExact(id, "N", out _) || !_sessions.TryGetValue(id, out var session) ||
            !session.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Der Upload wurde nicht gefunden.");
        _ = policy.Authorize(session.Destination, write: true);
        _ = policy.Authorize(session.StagePath, write: true);
        return session;
    }

    private async Task RemoveSessionAsync(UploadSession session, CancellationToken cancellationToken)
    {
        try { if (File.Exists(session.StagePath)) File.Delete(session.StagePath); }
        catch (IOException exception) { throw new IOException("Die temporäre Upload-Datei kann nicht entfernt werden.", exception); }
        await RemoveMetadataAsync(session, cancellationToken);
        _sessions.TryRemove(session.Id, out _);
    }

    private async Task RemoveMetadataAsync(UploadSession session, CancellationToken cancellationToken)
    {
        var path = MetadataPath(session.Id);
        if (File.Exists(path)) await Task.Run(() => File.Delete(path), cancellationToken);
    }

    private async Task PersistAsync(UploadSession session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_metadataDirectory);
        var record = new UploadRecord(session.Id, session.FileName, session.Destination, session.TargetPath,
            session.Size, session.Offset, session.State, session.CreatedAt, session.Owner);
        var temporary = MetadataPath(session.Id) + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, JsonOptions), cancellationToken);
        File.Move(temporary, MetadataPath(session.Id), overwrite: true);
    }

    private void LoadExisting()
    {
        try
        {
            Directory.CreateDirectory(_metadataDirectory);
            foreach (var metadata in Directory.EnumerateFiles(_metadataDirectory, "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<UploadRecord>(File.ReadAllText(metadata), JsonOptions);
                    if (record is null || !Guid.TryParseExact(record.Id, "N", out _)) continue;
                    var session = new UploadSession(record.Id, record.FileName, record.Destination, record.TargetPath,
                        record.Size, record.Offset, record.State, record.CreatedAt, record.Owner);
                    if (File.Exists(session.StagePath))
                    {
                        var length = new FileInfo(session.StagePath).Length;
                        session.Offset = Math.Min(length, session.Size);
                        session.State = session.Offset == session.Size ? "ready" : "uploading";
                    }
                    else session.State = "missing";
                    _sessions[session.Id] = session;
                }
                catch (Exception exception) { logger.LogWarning(exception, "Upload-Metadaten {File} konnten nicht geladen werden", Path.GetFileName(metadata)); }
            }
        }
        catch (IOException exception) { logger.LogWarning(exception, "Upload-Sitzungen konnten nicht geladen werden"); }
    }

    private static string ValidateFileName(string value)
    {
        var name = value.Trim();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['\\', '/', ':']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Der Dateiname darf keinen Pfad enthalten.");
        return name;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            cancellationToken.ThrowIfCancellationRequested();
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string NextAvailablePath(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Für den Upload konnte kein freier Dateiname gefunden werden.");
    }

    private string MetadataPath(string id) => Path.Combine(_metadataDirectory, id + ".json");

    private sealed record UploadRecord(string Id, string FileName, string Destination, string TargetPath, long Size,
        long Offset, string State, DateTimeOffset CreatedAt, string Owner);

    private sealed class UploadSession(string id, string fileName, string destination, string targetPath, long size,
        long offset, string state, DateTimeOffset createdAt, string owner)
    {
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public string Destination { get; } = destination;
        public string TargetPath { get; } = targetPath;
        public long Size { get; } = size;
        public long Offset { get; set; } = offset;
        public string State { get; set; } = state;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public string Owner { get; } = owner;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string StagePath => Path.Combine(Destination, $".wme-upload-{Id}.partial");
        public UploadSessionDto Snapshot() => new(Id, FileName, Destination, Size, Offset, State, CreatedAt);
    }
}

public sealed class UploadOffsetException(long expectedOffset) : IOException($"Der Upload wird ab Byte {expectedOffset} fortgesetzt.")
{
    public long ExpectedOffset { get; } = expectedOffset;
}
