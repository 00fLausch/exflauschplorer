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
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<string, UploadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly PathAccessPolicy policy;
    private readonly IRecycleBinService recycleBin;
    private readonly ILogger<UploadSessionService> logger;
    private readonly string _metadataDirectory;
    private readonly string? _privateStageDirectory;
    private readonly SemaphoreSlim _createGate = new(1, 1);

    public UploadSessionService(PathAccessPolicy policy, IRecycleBinService recycleBin,
        ILogger<UploadSessionService> logger, string? metadataDirectory = null)
    {
        this.policy = policy;
        this.recycleBin = recycleBin;
        this.logger = logger;
        _metadataDirectory = metadataDirectory ?? Path.Combine(SettingsStore.DataDirectory, "uploads");
        _privateStageDirectory = metadataDirectory is null
            ? PathAccessPolicy.NormalizeAbsolute(Path.Combine(SettingsStore.DataDirectory, "upload-stage"))
            : null;
        LoadExisting();
    }

    public IReadOnlyList<UploadSessionDto> List(string owner) => _sessions.Values
        .Where(session => session.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))
        .Select(session => session.Snapshot())
        .OrderByDescending(session => session.CreatedAt)
        .ToArray();

    internal int PendingCount => _sessions.Values.Count(session => session.State is "uploading" or "ready");

    public async Task<UploadSessionDto> CreateAsync(UploadCreateRequest request, string owner, CancellationToken cancellationToken)
    {
        if (request.Size < 0 || request.Size > MaximumUploadBytes) throw new ArgumentException("Die Datei darf höchstens 20 GB groß sein.");
        var name = PathAccessPolicy.ValidateEntryName(request.FileName);
        var destination = policy.Authorize(request.Destination, write: true);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException();
        var target = policy.AuthorizeEntryMutation(Path.Combine(destination, name));
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
                RepairStage(existing);
                await PersistAsync(existing, cancellationToken);
                logger.LogInformation("Upload {UploadId} von {Owner} wird für {FileName} fortgesetzt ({Offset}/{Size} Bytes)",
                    existing.Id, owner, existing.FileName, existing.Offset, existing.Size);
                return existing.Snapshot();
            }
            finally { existing.Gate.Release(); }
        }

        await _createGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.Count >= 128 || _sessions.Values.Count(upload => upload.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase)) >= 32)
                throw new ArgumentException("Zu viele offene Uploads. Beende oder entferne zuerst eine bestehende Sitzung.");
            var stageDirectory = ResolvePrivateStaging(destination);
            RequireSpace(stageDirectory ?? destination, request.Size);
            var id = Guid.NewGuid().ToString("N");
            var session = new UploadSession(id, name, destination, target, request.Size, 0,
                request.Size == 0 ? "ready" : "uploading", DateTimeOffset.UtcNow, owner, stageDirectory);
            try
            {
                await using (var stage = new FileStream(session.StagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    File.SetAttributes(session.StagePath, FileAttributes.Hidden);
                    await stage.FlushAsync(cancellationToken);
                }
                await PersistAsync(session, cancellationToken);
            }
            catch
            {
                try { DeleteStage(session); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning(ex, "Die temporäre Datei für Upload {UploadId} konnte nicht entfernt werden", id); }
                throw;
            }
            _sessions[id] = session;
            logger.LogInformation("Upload {UploadId} von {Owner} für {FileName} nach {Destination} angelegt ({Size} Bytes)",
                id, owner, name, destination, request.Size);
            return session.Snapshot();
        }
        finally { _createGate.Release(); }
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
            RequireSpace(Path.GetDirectoryName(session.StagePath)!, expectedLength);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            try
            {
                await using (var output = new FileStream(session.StagePath, FileMode.Open, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (output.Length < offset) throw new IOException("Die temporäre Datei ist unvollständig. Setze den Upload fort.");
                    output.SetLength(offset);
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
            try { await PersistAsync(session, cancellationToken); }
            catch
            {
                session.Offset = offset;
                session.State = "uploading";
                throw;
            }
            logger.LogInformation("Upload-Teil {UploadId} von {Owner} geprüft und gespeichert ({Offset}/{Size} Bytes)",
                id, owner, session.Offset, session.Size);
            return new UploadChunkDto(session.Id, session.Offset, session.State == "ready");
        }
        finally { session.Gate.Release(); }
    }

    public async Task<UploadCommitDto> CommitAsync(string id, string owner, string? conflictMode, CancellationToken cancellationToken, string? expectedSha256 = null)
    {
        var session = GetOwned(id, owner);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.State != "ready" || session.Offset != session.Size) throw new InvalidOperationException("Der Upload ist noch nicht vollständig.");
            RepairStage(session);
            if (session.Offset != session.Size) throw new InvalidOperationException("Die temporäre Datei ist unvollständig. Setze den Upload fort.");
            if (expectedSha256 is not null && (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)))
                throw new ArgumentException("Die Prüfsumme der vollständigen Datei ist ungültig.");
            var digest = await ComputeSha256Async(session.StagePath, cancellationToken);
            var expected = expectedSha256 ?? session.ExpectedSha256;
            if (expected is not null && !string.Equals(expected, digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die Gesamtprüfsumme stimmt nicht. Keine Datei wurde ersetzt. Starte den Upload mit der Originaldatei neu.");
            if (expected is not null && session.ExpectedSha256 is null)
            {
                session.ExpectedSha256 = expected;
                await PersistAsync(session, cancellationToken);
            }
            var target = policy.AuthorizeEntryMutation(session.TargetPath);
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
                if (mode == "keep-both") target = policy.AuthorizeEntryMutation(NextAvailablePath(target));
                else if ((File.GetAttributes(target) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new UploadConflictException(Path.GetFileName(target));
                else
                {
                    await recycleBin.DeleteAsync(target, cancellationToken);
                    logger.LogInformation("Bestehende Datei {Path} vor Upload-Ersetzung {UploadId} in den Papierkorb verschoben", target, id);
                }
            }

            File.Move(session.StagePath, target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.Hidden);
            var result = new UploadCommitDto(true, false, target, digest);
            _sessions.TryRemove(session.Id, out _);
            try { await RemoveMetadataAsync(session, CancellationToken.None); }
            catch (IOException ex) { logger.LogWarning(ex, "Upload-Metadaten für {UploadId} konnten nach dem Abschluss nicht entfernt werden", id); }
            logger.LogInformation("Upload {UploadId} von {Owner} nach {Path} abgeschlossen, SHA-256 {Sha256}", id, owner, target, digest);
            return result;
        }
        finally { session.Gate.Release(); }
    }

    public async Task CancelAsync(string id, string owner, CancellationToken cancellationToken)
    {
        var session = GetOwned(id, owner, authorizeWrite: false);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            await RemoveSessionAsync(session, cancellationToken);
            logger.LogInformation("Upload {UploadId} von {Owner} abgebrochen und temporäre Daten entfernt", id, owner);
        }
        finally { session.Gate.Release(); }
    }

    private UploadSession GetOwned(string id, string owner, bool authorizeWrite = true)
    {
        if (!Guid.TryParseExact(id, "N", out _) || !_sessions.TryGetValue(id, out var session) ||
            !session.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Der Upload wurde nicht gefunden.");
        if (!authorizeWrite) return session;
        _ = policy.Authorize(session.Destination, write: true);
        ValidateStage(session, requireShared: !session.PrivateStage);
        return session;
    }

    private async Task RemoveSessionAsync(UploadSession session, CancellationToken cancellationToken)
    {
        try { DeleteStage(session); }
        catch (IOException exception) { throw new IOException("Die temporäre Upload-Datei kann nicht entfernt werden.", exception); }
        await RemoveMetadataAsync(session, cancellationToken);
        _sessions.TryRemove(session.Id, out _);
    }

    private void DeleteStage(UploadSession session)
    {
        var stage = ValidateStage(session, requireShared: false);
        if (!File.Exists(stage)) return;
        File.Delete(stage);
    }

    private string ValidateStage(UploadSession session, bool requireShared)
    {
        var root = Path.GetPathRoot(session.StagePath) ?? throw new IOException("Ungültiger Upload-Ordner.");
        var pathCheck = requireShared ? policy : new PathAccessPolicy([new ShareRoot(root)]);
        var stage = pathCheck.AuthorizeEntryMutation(session.StagePath, allowInternal: true);
        if (!stage.Equals(session.StagePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Der Upload-Ordner hat sich geändert. Die Zwischendatei bleibt erhalten.");
        if (File.Exists(stage) && (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Die Upload-Zwischendatei ist eine Verknüpfung.");
        return stage;
    }

    private string? ResolvePrivateStaging(string destination)
    {
        if (_privateStageDirectory is null ||
            !string.Equals(Path.GetPathRoot(_privateStageDirectory), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            return null;
        Directory.CreateDirectory(_privateStageDirectory);
        return _privateStageDirectory;
    }

    internal async Task ExpireSessionsAsync(CancellationToken token)
    {
        var cutoff = DateTimeOffset.UtcNow - SessionLifetime;
        foreach (var session in _sessions.Values)
        {
            if (session.LastActivityAt > cutoff || !await session.Gate.WaitAsync(0, token)) continue;
            try
            {
                if (session.LastActivityAt <= cutoff && _sessions.ContainsKey(session.Id))
                    await RemoveSessionAsync(session, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { logger.LogWarning(ex, "Abgelaufener Upload {UploadId} konnte nicht entfernt werden", session.Id); }
            finally { session.Gate.Release(); }
        }
    }

    private static void RequireSpace(string destination, long bytes)
    {
        const long reserve = 512L * 1024 * 1024;
        var root = Path.GetPathRoot(destination);
        if (root is not { Length: 3 }) return; // A network share may not report the caller's quota.
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (bytes > Math.Max(0, available - reserve))
            throw new ArgumentException("Auf dem Ziellaufwerk ist nicht genug freier Speicherplatz für diesen Upload.");
    }

    private async Task RemoveMetadataAsync(UploadSession session, CancellationToken cancellationToken)
    {
        var path = MetadataPath(session.Id);
        if (File.Exists(path)) await Task.Run(() => File.Delete(path), cancellationToken);
    }

    private async Task PersistAsync(UploadSession session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_metadataDirectory);
        session.LastActivityAt = DateTimeOffset.UtcNow;
        var record = new UploadRecord(session.Id, session.FileName, session.Destination, session.TargetPath,
            session.Size, session.Offset, session.State, session.CreatedAt, session.Owner, session.LastActivityAt,
            session.ExpectedSha256, session.PrivateStage);
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
                    if (record is null || !Guid.TryParseExact(record.Id, "N", out _) ||
                        record.Size < 0 || record.Size > MaximumUploadBytes || record.Offset < 0 || record.Offset > record.Size) continue;
                    if (record.PrivateStage && _privateStageDirectory is null) continue;
                    var session = new UploadSession(record.Id, record.FileName, record.Destination, record.TargetPath,
                        record.Size, record.Offset, record.State, record.CreatedAt, record.Owner,
                        record.PrivateStage ? _privateStageDirectory : null)
                    {
                        LastActivityAt = record.LastActivityAt ?? File.GetLastWriteTimeUtc(metadata),
                        ExpectedSha256 = record.ExpectedSha256
                    };
                    if (!File.Exists(session.StagePath)) session.State = "missing";
                    else if (policy.IsAllowed(session.Destination, write: true))
                    {
                        ValidateStage(session, requireShared: !session.PrivateStage);
                        var offset = session.Offset;
                        RepairStage(session);
                        if (offset != session.Offset) PersistAsync(session, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    _sessions[session.Id] = session;
                }
                catch (Exception exception) { logger.LogWarning(exception, "Upload-Metadaten {File} konnten nicht geladen werden", Path.GetFileName(metadata)); }
            }
        }
        catch (IOException exception) { logger.LogWarning(exception, "Upload-Sitzungen konnten nicht geladen werden"); }
    }

    private static void RepairStage(UploadSession session)
    {
        using var stage = new FileStream(session.StagePath, FileMode.Open, FileAccess.Write, FileShare.None);
        var verifiedLength = Math.Min(stage.Length, session.Offset);
        if (stage.Length != verifiedLength)
        {
            stage.SetLength(verifiedLength);
            stage.Flush(flushToDisk: true);
        }
        session.Offset = verifiedLength;
        session.State = session.Offset == session.Size ? "ready" : "uploading";
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
        long Offset, string State, DateTimeOffset CreatedAt, string Owner, DateTimeOffset? LastActivityAt = null,
        string? ExpectedSha256 = null, bool PrivateStage = false);

    private sealed class UploadSession(string id, string fileName, string destination, string targetPath, long size,
        long offset, string state, DateTimeOffset createdAt, string owner, string? stageDirectory = null)
    {
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public string Destination { get; } = destination;
        public string TargetPath { get; } = targetPath;
        public long Size { get; } = size;
        public long Offset { get; set; } = offset;
        public string State { get; set; } = state;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset LastActivityAt { get; set; } = createdAt;
        public string? ExpectedSha256 { get; set; }
        public string Owner { get; } = owner;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool PrivateStage => stageDirectory is not null;
        public string StagePath => Path.Combine(stageDirectory ?? Destination, $".wme-upload-{Id}.partial");
        public UploadSessionDto Snapshot() => new(Id, FileName, Destination, Size, Offset, State, CreatedAt);
    }
}

public sealed class UploadOffsetException(long expectedOffset) : IOException($"Der Upload wird ab Byte {expectedOffset} fortgesetzt.")
{
    public long ExpectedOffset { get; } = expectedOffset;
}
