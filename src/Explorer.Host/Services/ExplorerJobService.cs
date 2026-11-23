using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Configuration;

namespace Explorer.Host.Services;

/// <summary>Persistent background file jobs. A disconnected browser never owns the worker lifetime.</summary>
public sealed class ExplorerJobService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PathAccessPolicy _policy;
    private readonly IRecycleBinService _recycleBin;
    private readonly SevenZipArchiveService _sevenZip = new();
    private readonly ILogger<ExplorerJobService> _logger;
    private readonly Func<string, string, bool> _sameVolume;
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, JobSubscriber> _subscribers = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private readonly SemaphoreSlim _undoScheduleGate = new(1, 1);
    private readonly object _undoLock = new();
    private readonly List<UndoRecord> _undo = [];
    private readonly string _storePath;
    private int _checkpointNeeded;

    public ExplorerJobService(PathAccessPolicy policy, IRecycleBinService recycleBin, ILogger<ExplorerJobService> logger,
        string? storePath = null, Func<string, string, bool>? sameVolumeResolver = null)
    {
        _policy = policy;
        _recycleBin = recycleBin;
        _logger = logger;
        _sameVolume = sameVolumeResolver ?? SameVolume;
        _storePath = storePath ?? Path.Combine(SettingsStore.DataDirectory, "jobs.json");
        LoadStore();
    }

    public IReadOnlyList<JobSnapshot> GetJobs(string? owner = null) => _jobs.Values
        .Where(job => SameOwner(job.Owner, owner))
        .Select(job => job.Snapshot())
        .OrderByDescending(job => job.CreatedAt)
        .ToArray();

    public int CountActiveJobs() => _jobs.Values.Count(job => !job.IsIdle);

    public JobSnapshot? GetJob(string id, string? owner = null) => FindOwnedJob(id, owner)?.Snapshot();

    public Task<JobSnapshot> EnqueueAsync(OperationRequest request, CancellationToken cancellationToken, string? owner = null)
    {
        if (request.UndoActions is not null || string.Equals(request.Kind?.Trim(), "undo-batch", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Rückgängig-Aufträge können nur über den Rückgängig-Befehl erstellt werden.");
        return EnqueueInternalAsync(request, null, cancellationToken, owner);
    }

    private async Task<JobSnapshot> EnqueueInternalAsync(OperationRequest request, string? undoSourceJobId,
        CancellationToken cancellationToken, string? owner)
    {
        if (request.Sources is null || request.Sources.Count > 10000)
            throw new ArgumentException("Die Auswahl ist ungültig oder enthält zu viele Einträge.");
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Wähle zuerst einen Dateivorgang aus.");
        var id = Guid.NewGuid().ToString("N");
        var state = new JobState(id, kind, request with { Kind = kind }, owner) { UndoSourceJobId = undoSourceJobId };
        if (!_jobs.TryAdd(id, state)) throw new InvalidOperationException("Der Auftrag konnte nicht angelegt werden.");
        try { await PersistAsync(cancellationToken); }
        catch
        {
            _jobs.TryRemove(id, out _);
            RequestCheckpoint();
            throw;
        }
        _logger.LogInformation("Dateioperation {JobId} ({Kind}) mit {SourceCount} Quellen angelegt", id, kind, request.Sources.Count);
        _queue.Writer.TryWrite(id);
        Publish(state);
        return state.Snapshot();
    }

    public bool Pause(string id, string? owner = null)
    {
        var job = FindOwnedJob(id, owner);
        if (job is null) return false;
        lock (job.Sync)
        {
            if (job.Status != "running") return false;
            job.PauseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            job.Status = "paused";
            job.Message = "Pausiert";
        }
        Publish(job);
        RequestCheckpoint();
        return true;
    }

    public bool Resume(string id, string? owner = null)
    {
        var job = FindOwnedJob(id, owner);
        if (job is null) return false;
        lock (job.Sync)
        {
            if (job.Status != "paused") return false;
            job.Status = "running";
            job.Message = "Wird fortgesetzt";
            job.PauseCompletion?.TrySetResult();
            job.PauseCompletion = null;
        }
        Publish(job);
        RequestCheckpoint();
        return true;
    }

    public bool Cancel(string id, string? owner = null)
    {
        var job = FindOwnedJob(id, owner);
        if (job is null) return false;
        var cancelledBeforeStart = false;
        lock (job.Sync)
        {
            if (IsTerminal(job.Status)) return false;
            job.CancelRequested = true;
            if (job.Status == "queued")
            {
                job.Status = "cancelled";
                job.Message = "Abgebrochen · 0 abgeschlossene Elemente bleiben erhalten";
                cancelledBeforeStart = true;
            }
            else
            {
                job.Message = "Wird abgebrochen …";
            }
            job.Cancellation?.Cancel();
            job.PauseCompletion?.TrySetResult();
        }
        Publish(job);
        if (cancelledBeforeStart) RequestCheckpoint();
        return true;
    }

    public bool ResolveConflict(string id, ConflictResolution resolution, string? owner = null)
    {
        var job = FindOwnedJob(id, owner);
        if (job is null) return false;
        var choice = resolution.Choice?.Trim().ToLowerInvariant();
        if (choice is not ("replace" or "skip" or "keep-both")) return false;
        lock (job.Sync)
        {
            if (job.Status != "conflict" || job.ConflictCompletion is null) return false;
            if (resolution.ApplyToAll) job.ConflictMode = choice;
            job.ConflictCompletion.TrySetResult(new ConflictResolution(choice, resolution.ApplyToAll));
        }
        Publish(job);
        return true;
    }

    public async IAsyncEnumerable<JobSnapshot> Watch(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, string? owner = null)
    {
        var channel = Channel.CreateBounded<JobSnapshot>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        _subscribers[id] = new JobSubscriber(owner, channel);
        try
        {
            foreach (var job in GetJobs(owner)) yield return job;
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken)) yield return update;
        }
        finally { _subscribers.TryRemove(id, out _); }
    }

    public async Task<JobSnapshot?> UndoLastAsync(CancellationToken cancellationToken, string? owner = null)
    {
        await _undoScheduleGate.WaitAsync(cancellationToken);
        try
        {
            var pending = _jobs.Values.Where(job => job.UndoSourceJobId is not null && SameOwner(job.Owner, owner))
                .Select(job => job.Snapshot()).FirstOrDefault(job => !IsTerminal(job.Status));
            if (pending is not null) return pending;

            UndoRecord[] records;
            lock (_undoLock)
            {
                var last = _undo.LastOrDefault(record => SameOwner(record.Owner, owner));
                if (last is null) return null;
                records = _undo.Where(record => record.JobId == last.JobId && SameOwner(record.Owner, owner)).ToArray();
            }
            var actions = records.Reverse().Select(ToUndoAction).OfType<OperationUndoAction>().ToArray();
            if (actions.Length == 0) return null;

            return await EnqueueInternalAsync(
                new OperationRequest("undo-batch", [], UndoActions: actions), records[0].JobId, cancellationToken, owner);
        }
        finally { _undoScheduleGate.Release(); }
    }

    private static OperationUndoAction? ToUndoAction(UndoRecord record) => record.Kind switch
    {
        "delete" => new OperationUndoAction("restore", record.OriginalPath, ""),
        "move" or "rename" => new OperationUndoAction("move", record.ResultPath, record.OriginalPath),
        "restore-folder" => new OperationUndoAction("create-folder", record.OriginalPath, ""),
        "create-folder" => new OperationUndoAction("delete-empty-folder", record.ResultPath, ""),
        "copy" or "create-file" or "zip" or "unzip" => new OperationUndoAction("delete", record.ResultPath, ""),
        _ => null
    };

    private JobState? FindOwnedJob(string id, string? owner) =>
        _jobs.TryGetValue(id, out var job) && SameOwner(job.Owner, owner) ? job : null;

    private static bool SameOwner(string? storedOwner, string? owner) =>
        string.Equals(storedOwner, owner, StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PersistCheckpointAsync();
        await Task.WhenAll(WorkerAsync(stoppingToken), WorkerAsync(stoppingToken), CheckpointLoopAsync(stoppingToken));
    }

    private async Task WorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_jobs.TryGetValue(id, out var job)) continue;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (job.Sync)
                {
                    if (job.Status != "queued") continue;
                    job.Cancellation = cancellation;
                    job.CancelRequested = false;
                    job.Status = "running";
                    job.Message = JobMessage(job.Kind);
                    job.StartedAt = DateTimeOffset.UtcNow;
                }
                Publish(job);
                string status;
                string message;
                string? error = null;
                try
                {
                    // Save the transition before making any filesystem changes.
                    await PersistAsync(cancellation.Token);
                    await ExecuteOperationAsync(job, cancellation.Token);
                    status = job.SkippedItems > 0 ? "completed-with-skips" : "completed";
                    message = job.SkippedItems > 0
                        ? $"Abgeschlossen · {job.SkippedItems} Elemente übersprungen"
                        : "Abgeschlossen";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    status = "cancelled";
                    message = stoppingToken.IsCancellationRequested
                        ? "Beim Beenden unterbrochen. Bereits abgeschlossene Änderungen bleiben erhalten."
                        : $"Abgebrochen · {job.CompletedItems} abgeschlossene Elemente bleiben erhalten";
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Dateioperation {JobId} ist fehlgeschlagen", job.Id);
                    status = "failed";
                    error = GermanErrors.For(exception);
                    message = error;
                }

                if (job.Kind != "undo-batch") AddUndoFor(job);
                lock (job.Sync)
                {
                    job.Status = status;
                    job.Message = message;
                    job.Error = error;
                    job.Conflict = null;
                    job.ConflictCompletion = null;
                    job.PauseCompletion?.TrySetResult();
                    job.PauseCompletion = null;
                }
                await PersistCheckpointAsync();
                lock (job.Sync) job.Cancellation = null;
                Publish(job);
                _logger.LogInformation("Dateioperation {JobId} ({Kind}) mit Status {Status} beendet. Bearbeitete Einträge: {CompletedItems}",
                    job.Id, job.Kind, job.Status, job.CompletedItems);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ExecuteOperationAsync(JobState job, CancellationToken cancellationToken)
    {
        switch (job.Kind)
        {
            case "copy":
            case "move":
                await CopyOrMoveAsync(job, cancellationToken);
                return;
            case "rename":
            case "rename-series":
                await RenameAsync(job, cancellationToken);
                return;
            case "delete":
                await DeleteAsync(job, cancellationToken);
                return;
            case "new-folder":
                await CreateFolderAsync(job, cancellationToken);
                return;
            case "new-file":
                await CreateFileAsync(job, cancellationToken);
                return;
            case "zip":
                await ZipAsync(job, cancellationToken);
                return;
            case "unzip":
                await UnzipAsync(job, cancellationToken);
                return;
            case "undo-batch":
                await UndoBatchAsync(job, cancellationToken);
                return;
            default:
                throw new ArgumentException("Dieser Dateivorgang wird nicht unterstützt.");
        }
    }

    private async Task CopyOrMoveAsync(JobState job, CancellationToken cancellationToken)
    {
        if (job.Request.Sources.Count == 0) throw new ArgumentException("Wähle mindestens eine Datei aus.");
        if (string.IsNullOrWhiteSpace(job.Request.Destination)) throw new ArgumentException("Wähle einen Zielordner aus.");
        var destination = _policy.Authorize(job.Request.Destination, write: true);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException();
        var move = job.Kind == "move";
        var sources = job.Request.Sources.Select(path => move ? _policy.AuthorizeEntryMutation(path) : _policy.Authorize(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var linkMoves = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<TransferItem>();
        var folderMoveCopies = new List<PendingFolderMoveCopy>();
        foreach (var source in sources)
        {
            var attributes = File.GetAttributes(source);
            var rootName = sources.Length == 1 && !string.IsNullOrWhiteSpace(job.Request.NewName)
                ? PathAccessPolicy.ValidateEntryName(job.Request.NewName)
                : Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            var targetRoot = Path.Combine(destination, rootName);
            if (move && IsRedirectingLink(source, attributes))
            {
                if (!_sameVolume(source, destination))
                    throw new IOException("Verknüpfungen können nur innerhalb desselben Laufwerks verschoben werden.");
                linkMoves.Add(source, attributes);
                plan.Add(new TransferItem(source, targetRoot, false, 0, source, File.GetLastWriteTimeUtc(source)));
                continue;
            }
            if (Directory.Exists(source)) EnumerateTree(source, targetRoot, plan, cancellationToken);
            else
            {
                var info = new FileInfo(source);
                plan.Add(new TransferItem(source, targetRoot, false, info.Length, source, info.LastWriteTimeUtc));
            }
        }
        SetTotals(job, plan.Count, plan.Sum(item => item.Size));

        if (move && sources.Length == 1 && !linkMoves.ContainsKey(sources[0]) && Directory.Exists(sources[0]) && _sameVolume(sources[0], destination))
        {
            var target = plan.First(item => item.SourcePath == sources[0]).TargetPath;
            if (PathEquals(target, sources[0]))
            {
                SetProgress(job, job.TotalBytes ?? 0, job.TotalItems ?? 0, "Der Ordner liegt bereits im Zielordner.");
                return;
            }
            if (Directory.Exists(target) || File.Exists(target))
            {
                var choice = await ResolveConflictAsync(job, sources[0], target, cancellationToken);
                if (choice == "skip") { job.SkippedItems = job.TotalItems ?? 1; return; }
                if (choice == "keep-both") target = GetAvailablePath(target);
                else
                {
                    throw new IOException("Vorhandene Ordner werden beim Verschieben nicht ersetzt. Wähle „Beide behalten“ oder „Überspringen“.");
                }
            }
            target = _policy.AuthorizeEntryMutation(target);
            Directory.Move(sources[0], target);
            AddUndo(job, "move", sources[0], target);
            SetProgress(job, job.TotalBytes ?? 0, job.TotalItems ?? 0, "Ordner verschoben");
            return;
        }

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(job, cancellationToken);
            if (linkMoves.TryGetValue(item.SourcePath, out var linkAttributes))
            {
                var targetLink = _policy.AuthorizeEntryMutation(item.TargetPath);
                if (!PathEquals(item.SourcePath, targetLink))
                {
                    if (EntryExists(targetLink))
                    {
                        var choice = await ResolveConflictAsync(job, item.SourcePath, targetLink, cancellationToken);
                        if (choice == "skip") { job.SkippedItems++; job.CompletedItems++; continue; }
                        if (choice != "keep-both") throw new IOException("Beim Verschieben von Verknüpfungen wird kein vorhandener Eintrag ersetzt.");
                        targetLink = _policy.AuthorizeEntryMutation(GetAvailablePath(targetLink));
                    }
                    MoveEntry(item.SourcePath, targetLink, linkAttributes);
                    AddUndo(job, "move", item.SourcePath, targetLink);
                }
                job.CompletedItems++;
                PublishIfDue(job);
                continue;
            }
            if (item.IsDirectory)
            {
                var targetDirectory = _policy.Authorize(item.TargetPath, write: true);
                if (File.Exists(targetDirectory))
                    throw new IOException($"Am Ziel liegt eine Datei mit dem Namen „{Path.GetFileName(targetDirectory)}“.");
                var targetExisted = Directory.Exists(targetDirectory);
                Directory.CreateDirectory(targetDirectory);
                if (!targetExisted) AddUndo(job, "create-folder", "", targetDirectory);
                job.CompletedItems++;
                PublishIfDue(job);
                continue;
            }

            var targetFile = _policy.AuthorizeEntryMutation(item.TargetPath);
            var sourceFile = move ? _policy.AuthorizeEntryMutation(item.SourcePath) : _policy.Authorize(item.SourcePath);
            if (PathEquals(sourceFile, targetFile))
            {
                job.CompletedBytes += item.Size;
                job.CompletedItems++;
                PublishIfDue(job);
                continue;
            }

            if (move && _sameVolume(sourceFile, targetFile))
            {
                if (File.Exists(targetFile) || Directory.Exists(targetFile))
                {
                    var choice = await ResolveConflictAsync(job, sourceFile, targetFile, cancellationToken);
                    if (choice == "skip") { job.SkippedItems++; job.CompletedItems++; continue; }
                    if (choice == "keep-both") targetFile = GetAvailablePath(targetFile);
                    else
                    {
                        await RecycleReplaceableFileAsync(job, targetFile, cancellationToken);
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Move(sourceFile, targetFile);
                AddUndo(job, "move", sourceFile, targetFile);
                job.CompletedBytes += item.Size;
                job.CompletedItems++;
                PublishIfDue(job);
                continue;
            }

            var folderMove = move && Directory.Exists(item.SourceRoot) && !_sameVolume(item.SourceRoot, destination);
            var copied = await CopyFileAtomicallyAsync(job, item, sourceFile, targetFile, move, cancellationToken);
            if (copied is null)
            {
                job.SkippedItems++;
                job.CompletedItems++;
                continue;
            }
            if (move && folderMove)
            {
                AddUndo(job, "copy", sourceFile, copied.TargetPath);
                folderMoveCopies.Add(new PendingFolderMoveCopy(item.SourceRoot, sourceFile, copied));
            }
            else if (move)
            {
                if (TryDeleteMovedSource(copied)) AddUndo(job, "move", sourceFile, copied.TargetPath);
                else
                {
                    AddUndo(job, "copy", sourceFile, copied.TargetPath);
                    job.SkippedItems++;
                }
            }
            else AddUndo(job, "copy", sourceFile, copied.TargetPath);
            job.CompletedItems++;
            PublishIfDue(job);
        }

        if (move && job.SkippedItems == 0)
        {
            foreach (var source in sources.Where(source => !linkMoves.ContainsKey(source) && Directory.Exists(source)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var pending in folderMoveCopies.Where(item => PathEquals(item.SourceRoot, source)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryDeleteMovedSource(pending.Copy))
                        UpdateUndoKind(job, pending.Copy.TargetPath, "move");
                    else job.SkippedItems++;
                }

                if (job.SkippedItems > 0) continue;
                var incompleteCleanup = false;
                foreach (var directory in plan.Where(item => item.IsDirectory && PathEquals(item.SourceRoot, source))
                             .OrderByDescending(item => item.SourcePath.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(directory.SourcePath)) continue;
                    try
                    {
                        Directory.Delete(directory.SourcePath, recursive: false);
                        AddUndo(job, "restore-folder", directory.SourcePath, "");
                    }
                    catch (IOException) { incompleteCleanup = true; }
                    catch (UnauthorizedAccessException) { incompleteCleanup = true; }
                }
                if (incompleteCleanup) job.SkippedItems++;
            }
        }
        await PersistAsync(cancellationToken);
    }

    private void EnumerateTree(string root, string targetRoot, List<TransferItem> plan, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Source, string Target)>();
        pending.Push((root, targetRoot));
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryInfo = new DirectoryInfo(directory.Source);
            plan.Add(new TransferItem(directory.Source, directory.Target, true, 0, root, directoryInfo.LastWriteTimeUtc));
            foreach (var child in Directory.EnumerateFileSystemEntries(directory.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_policy.IsAllowed(child)) continue;
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var childTarget = Path.Combine(directory.Target, Path.GetFileName(child));
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((child, childTarget));
                else
                {
                    var fileInfo = new FileInfo(child);
                    plan.Add(new TransferItem(child, childTarget, false, fileInfo.Length, root, fileInfo.LastWriteTimeUtc));
                }
            }
        }
    }

    private async Task<CopyResult?> CopyFileAtomicallyAsync(JobState job, TransferItem item, string source,
        string destination, bool moving, CancellationToken cancellationToken)
    {
        var target = destination;
        var conflict = File.Exists(target) || Directory.Exists(target);
        if (conflict)
        {
            var choice = await ResolveConflictAsync(job, source, target, cancellationToken);
            if (choice == "skip") return null;
            if (choice == "keep-both") target = GetAvailablePath(target);
        }

        target = _policy.AuthorizeEntryMutation(target);
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Der Zielordner ist ungültig.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".wme-{job.Id}-{Guid.NewGuid():N}.partial");
        var sourceInfo = new FileInfo(source);
        var initialLength = sourceInfo.Length;
        var initialLastWriteTimeUtc = sourceInfo.LastWriteTimeUtc;
        if (moving && (initialLength != item.Size || initialLastWriteTimeUtc != item.LastWriteTimeUtc))
            throw new IOException("Die Quelldatei hat sich während des Verschiebeplans geändert und wurde am Ursprungsort belassen.");
        try
        {
            var sourceSharing = moving ? FileShare.Read | FileShare.Delete : FileShare.ReadWrite | FileShare.Delete;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, sourceSharing,
                             256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                File.SetAttributes(staging, FileAttributes.Hidden);
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await WaitIfPausedAsync(job, cancellationToken);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    job.CompletedBytes += read;
                    PublishIfDue(job);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
                if (input.Length != initialLength || moving && File.GetLastWriteTimeUtc(source) != initialLastWriteTimeUtc)
                    throw new IOException("Die Quelldatei hat sich während des Kopierens geändert.");
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                await RecycleReplaceableFileAsync(job, target, cancellationToken);
            }
            File.Move(staging, target);
            try
            {
                File.SetLastWriteTimeUtc(target, sourceInfo.LastWriteTimeUtc);
                var sourceAttributes = File.GetAttributes(source) & ~(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Normal);
                if (sourceAttributes == 0) sourceAttributes = FileAttributes.Normal;
                if ((File.GetAttributes(source) & FileAttributes.Hidden) != 0) sourceAttributes |= FileAttributes.Hidden;
                if ((File.GetAttributes(source) & FileAttributes.ReadOnly) != 0) sourceAttributes |= FileAttributes.ReadOnly;
                File.SetAttributes(target, sourceAttributes);
            }
            catch (IOException) { }
            return new CopyResult(source, target, initialLength, initialLastWriteTimeUtc);
        }
        catch
        {
            try
            {
                if (File.Exists(staging))
                {
                    File.SetAttributes(staging, FileAttributes.Normal);
                    File.Delete(staging);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(cleanupException, "Temporäre Kopierdatei {StagingPath} konnte nach einem Fehler nicht entfernt werden", staging);
            }
            throw;
        }
    }

    private async Task RenameAsync(JobState job, CancellationToken cancellationToken)
    {
        if (job.Request.Sources.Count == 0 || string.IsNullOrWhiteSpace(job.Request.NewName))
            throw new ArgumentException("Gib einen neuen Namen ein.");
        SetTotals(job, job.Request.Sources.Count, 0);
        for (var index = 0; index < job.Request.Sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(job, cancellationToken);
            var source = _policy.AuthorizeEntryMutation(job.Request.Sources[index]);
            var sourceAttributes = File.GetAttributes(source);
            var originalName = Path.GetFileName(source);
            var extension = Path.GetExtension(originalName);
            var stem = Path.GetFileNameWithoutExtension(originalName);
            var pattern = job.Request.NewName!;
            var name = pattern.Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
                .Replace("{ext}", extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                .Replace("{n}", (index + 1).ToString("D2"), StringComparison.OrdinalIgnoreCase);
            if (job.Request.Sources.Count > 1 && !pattern.Contains("{n}", StringComparison.OrdinalIgnoreCase) && index > 0)
                name = $"{Path.GetFileNameWithoutExtension(name)} ({index + 1}){Path.GetExtension(name)}";
            var target = _policy.AuthorizeEntryMutation(Path.Combine(Path.GetDirectoryName(source)!, PathAccessPolicy.ValidateEntryName(name)));
            if (PathEquals(source, target)) { job.CompletedItems++; continue; }
            if (EntryExists(target))
            {
                var choice = await ResolveConflictAsync(job, source, target, cancellationToken);
                if (choice == "skip") { job.SkippedItems++; job.CompletedItems++; continue; }
                if (choice == "keep-both") target = GetAvailablePath(target);
                else
                {
                    if ((sourceAttributes & FileAttributes.Directory) != 0 || IsRedirectingLink(source, sourceAttributes))
                        throw new IOException("Beim Umbenennen eines Ordners oder einer Verknüpfung wird kein vorhandener Eintrag ersetzt.");
                    await RecycleReplaceableFileAsync(job, target, cancellationToken);
                }
            }
            MoveEntry(source, target, sourceAttributes);
            AddUndo(job, "rename", source, target);
            job.CompletedItems++;
            PublishIfDue(job);
        }
    }

    private async Task DeleteAsync(JobState job, CancellationToken cancellationToken)
    {
        if (job.Request.Sources.Count == 0) throw new ArgumentException("Wähle mindestens einen Eintrag aus.");
        SetTotals(job, job.Request.Sources.Count, 0);
        foreach (var rawPath in job.Request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(job, cancellationToken);
            var path = _policy.AuthorizeEntryMutation(rawPath);
            if (!EntryExists(path)) { job.CompletedItems++; continue; }
            var attributes = File.GetAttributes(path);
            if (job.Request.Permanent)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(path, recursive: !IsRedirectingLink(path, attributes));
                else File.Delete(path);
            }
            else await _recycleBin.DeleteAsync(path, cancellationToken);
            if (!job.Request.Permanent) AddUndo(job, "delete", path, "");
            job.CompletedItems++;
            PublishIfDue(job);
        }
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsRedirectingLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        return entry.LinkTarget is not null;
    }

    private static void MoveEntry(string source, string target, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0) Directory.Move(source, target);
        else File.Move(source, target);
    }

    private async Task RecycleReplaceableFileAsync(JobState job, string path, CancellationToken cancellationToken)
    {
        path = _policy.AuthorizeEntryMutation(path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Ein vorhandener Ordner oder eine Verknüpfung kann nicht durch eine Datei ersetzt werden.");
        await _recycleBin.DeleteAsync(path, cancellationToken);
        AddUndo(job, "delete", path, "");
    }

    private async Task UndoBatchAsync(JobState job, CancellationToken cancellationToken)
    {
        var actions = job.Request.UndoActions ?? [];
        SetTotals(job, actions.Count, 0);
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(job, cancellationToken);
            if (action.Kind == "restore")
            {
                await _recycleBin.RestoreOriginalPathAsync(_policy.AuthorizeEntryMutation(action.SourcePath), cancellationToken);
            }
            else if (action.Kind == "move")
            {
                var source = _policy.AuthorizeEntryMutation(action.SourcePath);
                var target = _policy.AuthorizeEntryMutation(action.TargetPath);
                var attributes = File.GetAttributes(source);
                if (EntryExists(target)) throw new IOException($"„{Path.GetFileName(target)}“ ist am ursprünglichen Ort bereits vorhanden.");
                var parent = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(parent)) throw new IOException("Der ursprüngliche Ordner ist nicht mehr erreichbar.");
                Directory.CreateDirectory(parent);
                MoveEntry(source, target, attributes);
            }
            else if (action.Kind == "create-folder")
            {
                var path = _policy.Authorize(action.SourcePath, write: true);
                Directory.CreateDirectory(path);
            }
            else if (action.Kind == "delete")
            {
                var path = _policy.AuthorizeEntryMutation(action.SourcePath);
                if (EntryExists(path)) await _recycleBin.DeleteAsync(path, cancellationToken);
            }
            else if (action.Kind == "delete-empty-folder")
            {
                var path = _policy.AuthorizeEntryMutation(action.SourcePath);
                if (EntryExists(path))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Directory) == 0 || IsRedirectingLink(path, attributes))
                        throw new IOException("Der beim Auftrag angelegte Ordner wurde zwischenzeitlich ersetzt.");
                    Directory.Delete(path, recursive: false);
                }
            }
            else throw new ArgumentException("Die Rückgängig-Aktion ist ungültig.");

            ConsumeUndoStep(job, action);
            job.CompletedItems++;
            PublishIfDue(job);
        }
    }

    private async Task CreateFolderAsync(JobState job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.Request.Destination) || string.IsNullOrWhiteSpace(job.Request.NewName))
            throw new ArgumentException("Wähle einen Ordner und gib einen Namen ein.");
        var parent = _policy.Authorize(job.Request.Destination, write: true);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException();
        var target = _policy.AuthorizeEntryMutation(Path.Combine(parent, PathAccessPolicy.ValidateEntryName(job.Request.NewName)));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Ein Element mit diesem Namen existiert bereits.");
        await WaitIfPausedAsync(job, cancellationToken);
        Directory.CreateDirectory(target);
        SetTotals(job, 1, 0);
        job.CompletedItems = 1;
        AddUndo(job, "create-folder", "", target);
    }

    private async Task CreateFileAsync(JobState job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.Request.Destination) || string.IsNullOrWhiteSpace(job.Request.NewName))
            throw new ArgumentException("Wähle einen Ordner und gib einen Dateinamen ein.");
        var parent = _policy.Authorize(job.Request.Destination, write: true);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException();
        var target = _policy.AuthorizeEntryMutation(Path.Combine(parent, PathAccessPolicy.ValidateEntryName(job.Request.NewName)));
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Ein Element mit diesem Namen existiert bereits.");
        await WaitIfPausedAsync(job, cancellationToken);
        var contents = job.Request.Template?.Trim().ToLowerInvariant() switch
        {
            "markdown" => "# Titel\n\n",
            "json" => "{\n  \n}\n",
            "csv" => "Spalte 1;Spalte 2\r\n",
            "html" => "<!doctype html>\n<html lang=\"de\">\n<head>\n  <meta charset=\"utf-8\">\n  <title>Titel</title>\n</head>\n<body>\n</body>\n</html>\n",
            "javascript" => "'use strict';\n\n",
            _ => ""
        };
        await File.WriteAllTextAsync(target, contents, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        SetTotals(job, 1, 0);
        job.CompletedItems = 1;
        AddUndo(job, "create-file", "", target);
    }

    private async Task ZipAsync(JobState job, CancellationToken cancellationToken)
    {
        if (job.Request.Sources.Count != 1 || string.IsNullOrWhiteSpace(job.Request.Destination))
            throw new ArgumentException("Wähle einen Ordner oder eine Datei und einen Zielordner aus.");
        var source = _policy.Authorize(job.Request.Sources[0]);
        var destination = _policy.Authorize(job.Request.Destination, write: true);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException();
        var baseName = Directory.Exists(source)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(source))
            : Path.GetFileNameWithoutExtension(source);
        var requestedName = string.IsNullOrWhiteSpace(job.Request.NewName) ? baseName + ".zip" : job.Request.NewName;
        if (!requestedName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) requestedName += ".zip";
        var target = _policy.AuthorizeEntryMutation(Path.Combine(destination, PathAccessPolicy.ValidateEntryName(requestedName)));
        if (File.Exists(target) || Directory.Exists(target))
        {
            var choice = await ResolveConflictAsync(job, source, target, cancellationToken);
            if (choice == "skip") { job.SkippedItems = 1; return; }
            if (choice == "keep-both") target = GetAvailablePath(target);
        }
        var items = EnumerateArchiveItems(source, cancellationToken);
        var totalBytes = items.Where(item => !item.Directory).Sum(item => new FileInfo(item.Path).Length);
        SetTotals(job, items.Count + 1, totalBytes);
        var stage = Path.Combine(destination, $".wme-{job.Id}-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                File.SetAttributes(stage, FileAttributes.Hidden);
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var root = Path.GetDirectoryName(source) ?? throw new ArgumentException("Ein ganzes Laufwerk kann nicht archiviert werden.");
                var buffer = new byte[128 * 1024];
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(job, cancellationToken);
                    var file = _policy.Authorize(item.Path);
                    var relative = Path.GetRelativePath(root, item.Path).Replace(Path.DirectorySeparatorChar, '/');
                    if (item.Directory)
                    {
                        archive.CreateEntry(relative + "/");
                        job.CompletedItems++;
                        continue;
                    }
                    var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var entryStream = entry.Open();
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        job.CompletedBytes += read;
                        PublishIfDue(job);
                    }
                    job.CompletedItems++;
                }
            }
            if (EntryExists(target)) await RecycleReplaceableFileAsync(job, target, cancellationToken);
            File.Move(stage, target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.Hidden);
            AddUndo(job, "zip", source, target);
            job.CompletedItems++;
        }
        catch
        {
            try { if (File.Exists(stage)) File.Delete(stage); } catch { }
            throw;
        }
    }

    private async Task UnzipAsync(JobState job, CancellationToken cancellationToken)
    {
        if (job.Request.Sources.Count != 1 || string.IsNullOrWhiteSpace(job.Request.Destination))
            throw new ArgumentException("Wähle eine Archivdatei und einen Zielordner aus.");
        var source = _policy.Authorize(job.Request.Sources[0]);
        if (!File.Exists(source)) throw new FileNotFoundException();
        var destination = _policy.Authorize(job.Request.Destination, write: true);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException();
        var defaultName = Path.GetFileNameWithoutExtension(source);
        var folderName = string.IsNullOrWhiteSpace(job.Request.NewName) ? defaultName : job.Request.NewName;
        var target = _policy.AuthorizeEntryMutation(Path.Combine(destination, PathAccessPolicy.ValidateEntryName(folderName)));
        if (EntryExists(target))
        {
            var choice = await ResolveConflictAsync(job, source, target, cancellationToken);
            if (choice == "skip") { job.SkippedItems = 1; return; }
            if (choice == "keep-both") target = _policy.AuthorizeEntryMutation(GetAvailablePath(target));
            else throw new IOException("Beim Entpacken wird kein vorhandener Eintrag ersetzt. Wähle „Beide behalten“ oder „Überspringen“.");
        }
        var staging = Path.Combine(destination, $".wme-{job.Id}-{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(staging);
        File.SetAttributes(staging, File.GetAttributes(staging) | FileAttributes.Hidden);
        try
        {
            if (Path.GetExtension(source).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractSevenZipAsync(job, source, staging, cancellationToken);
            }
            else
            {
            using var archive = ZipFile.OpenRead(source);
            var budget = ExtractionBudget(source, destination);
            ValidateExtractionSize(archive.Entries.Count, archive.Entries.Select(entry => entry.Length), budget);
            SetTotals(job, archive.Entries.Count, archive.Entries.Sum(entry => entry.Length));
            var stageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging));
            var buffer = new byte[128 * 1024];
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(job, cancellationToken);
                var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Verknüpfungen werden aus Archiven nicht entpackt.");
                var outputPath = ResolveArchiveEntryPath(stageRoot, entry.FullName, directory);
                if (directory)
                {
                    Directory.CreateDirectory(outputPath);
                    job.CompletedItems++;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    long fileBytes = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        fileBytes += read;
                        if (fileBytes > entry.Length || job.CompletedBytes + read > budget)
                            throw new InvalidDataException("Die tatsächlich entpackte Datenmenge überschreitet das Archivlimit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        job.CompletedBytes += read;
                        PublishIfDue(job);
                    }
                    if (fileBytes != entry.Length) throw new InvalidDataException("Die Dateigröße im ZIP stimmt nicht mit dem gelesenen Inhalt überein.");
                    job.CompletedItems++;
                    }
                }
            }
            target = _policy.AuthorizeEntryMutation(target);
            Directory.Move(staging, target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.Hidden);
            AddUndo(job, "unzip", source, target);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    private async Task ExtractSevenZipAsync(JobState job, string archivePath, string staging, CancellationToken cancellationToken)
    {
        var entries = await _sevenZip.ListEntriesAsync(archivePath, cancellationToken);
        var stageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging));
        var budget = ExtractionBudget(archivePath, staging);
        ValidateExtractionSize(entries.Count, entries.Select(entry => entry.Size), budget);
        var destinations = new List<(SevenZipArchiveEntry Entry, string OutputPath)>();
        var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsLink)
                throw new InvalidDataException("Das 7-Zip-Archiv enthält eine Verknüpfung. Verknüpfungen werden aus Sicherheitsgründen nicht entpackt.");

            var outputPath = ResolveArchiveEntryPath(stageRoot, entry.Path, entry.IsDirectory);

            if (seen.TryGetValue(outputPath, out var existingIsDirectory))
            {
                if (existingIsDirectory && entry.IsDirectory) continue;
                throw new InvalidDataException("Das 7-Zip-Archiv enthält doppelte Dateinamen, die unter Windows kollidieren.");
            }
            seen.Add(outputPath, entry.IsDirectory);
            destinations.Add((entry, outputPath));
        }

        foreach (var (_, outputPath) in destinations)
        {
            var parent = Path.GetDirectoryName(outputPath);
            while (parent is not null && parent.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (seen.TryGetValue(parent, out var isDirectory) && !isDirectory)
                    throw new InvalidDataException("Das 7-Zip-Archiv enthält kollidierende Datei- und Ordnernamen.");
                if (string.Equals(parent, stageRoot, StringComparison.OrdinalIgnoreCase)) break;
                parent = Path.GetDirectoryName(parent);
            }
        }

        SetTotals(job, destinations.Count, destinations.Where(item => !item.Entry.IsDirectory).Sum(item => item.Entry.Size));
        await WaitIfPausedAsync(job, cancellationToken);
        await _sevenZip.ExtractAllAsync(archivePath, staging, cancellationToken);

        // 7-Zip runs once for a solid archive; validate the resulting tree before it is moved into place.
        var expectedPaths = new HashSet<string>(destinations.Select(item => item.OutputPath), StringComparer.OrdinalIgnoreCase);
        foreach (var (_, outputPath) in destinations)
        {
            var parent = Path.GetDirectoryName(outputPath);
            while (parent is { Length: > 0 } && parent.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(parent, stageRoot, StringComparison.OrdinalIgnoreCase))
            {
                expectedPaths.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        var actualPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long actualBytes = 0;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
        foreach (var path in Directory.EnumerateFileSystemEntries(staging, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Das 7-Zip-Archiv enthält eine Verknüpfung. Verknüpfungen werden aus Sicherheitsgründen nicht entpackt.");
            if (!expectedPaths.Contains(path))
                throw new InvalidDataException("Das 7-Zip-Archiv enthält unerwartete Einträge.");
            actualPaths.Add(path);
            if ((attributes & FileAttributes.Directory) == 0) actualBytes += new FileInfo(path).Length;
        }
        if (actualBytes > budget)
            throw new InvalidDataException("Das Archiv überschreitet das Größenlimit, das Kompressionsverhältnis oder den verfügbaren Speicherplatz.");
        foreach (var expected in expectedPaths)
            if (!actualPaths.Contains(expected))
                throw new InvalidDataException("Das 7-Zip-Archiv wurde nicht vollständig entpackt.");
        foreach (var (entry, outputPath) in destinations.Where(item => !item.Entry.IsDirectory))
        {
            if (new FileInfo(outputPath).Length != entry.Size)
                throw new InvalidDataException("Die Dateigröße im 7-Zip-Archiv stimmt nicht mit dem gelesenen Inhalt überein.");
        }
        job.CompletedBytes = actualBytes;
        job.CompletedItems = destinations.Count;
        PublishIfDue(job);
    }

    private static string ResolveArchiveEntryPath(string root, string name, bool directory)
    {
        var normalized = name.Replace('\\', '/');
        if (directory && normalized.EndsWith('/')) normalized = normalized[..^1];
        var segments = normalized.Split('/');
        try
        {
            foreach (var segment in segments) PathAccessPolicy.ValidateEntryName(segment);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Das Archiv enthält einen ungültigen oder unsicheren Dateinamen.", exception);
        }
        return Path.Combine(root, Path.Combine(segments));
    }

    private static long ExtractionBudget(string archivePath, string destination)
    {
        const long limit = 20L * 1024 * 1024 * 1024;
        const long reserve = 512L * 1024 * 1024;
        var compressed = new FileInfo(archivePath).Length;
        var ratioBudget = Math.Max(128L * 1024 * 1024, Math.Min(compressed, limit) * 300);
        var drive = Path.GetPathRoot(destination);
        var available = drive is { Length: 3 } ? Math.Max(0, new DriveInfo(drive).AvailableFreeSpace - reserve) : limit;
        return Math.Min(limit, Math.Min(ratioBudget, available));
    }

    private static void ValidateExtractionSize(int entries, IEnumerable<long> lengths, long budget)
    {
        if (entries > 10_000) throw new InvalidDataException("Archive mit mehr als 10.000 Einträgen werden nicht entpackt.");
        long total = 0;
        foreach (var length in lengths)
        {
            if (length < 0 || length > budget - total)
                throw new InvalidDataException("Das Archiv überschreitet das Größenlimit, das Kompressionsverhältnis oder den verfügbaren Speicherplatz.");
            total += length;
        }
    }

    private async Task<string> ResolveConflictAsync(JobState job, string source, string destination, CancellationToken cancellationToken)
    {
        lock (job.Sync)
        {
            if (job.ConflictMode is "replace" or "skip" or "keep-both") return job.ConflictMode;
            job.Status = "conflict";
            job.Message = "Datei vorhanden · Entscheidung erforderlich";
            job.Conflict = new ConflictPrompt(Path.GetFileName(source), Path.GetFileName(destination), destination);
            job.ConflictCompletion = new TaskCompletionSource<ConflictResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Publish(job);
        await PersistAsync(cancellationToken);
        ConflictResolution resolution;
        try { resolution = await job.ConflictCompletion.Task.WaitAsync(cancellationToken); }
        finally
        {
            lock (job.Sync)
            {
                job.Conflict = null;
                job.ConflictCompletion = null;
                if (job.Status == "conflict") job.Status = "running";
                job.Message = JobMessage(job.Kind);
            }
        }
        return resolution.Choice;
    }

    private async Task WaitIfPausedAsync(JobState job, CancellationToken cancellationToken)
    {
        Task? wait;
        lock (job.Sync) wait = job.PauseCompletion?.Task;
        if (wait is not null) await wait.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void SetTotals(JobState job, int items, long bytes)
    {
        lock (job.Sync) { job.TotalItems = items; job.TotalBytes = bytes; }
        Publish(job);
    }

    private void SetProgress(JobState job, long bytes, int items, string message)
    {
        lock (job.Sync)
        {
            job.CompletedBytes = bytes;
            job.CompletedItems = items;
            job.Message = message;
            job.LastPublish = DateTimeOffset.UtcNow;
        }
        Publish(job);
    }

    private void PublishIfDue(JobState job)
    {
        if (DateTimeOffset.UtcNow - job.LastPublish < TimeSpan.FromMilliseconds(180)) return;
        job.LastPublish = DateTimeOffset.UtcNow;
        Publish(job);
        RequestCheckpoint();
    }

    private void Publish(JobState job)
    {
        var snapshot = job.Snapshot();
        foreach (var subscriber in _subscribers.Values)
            if (SameOwner(job.Owner, subscriber.Owner)) subscriber.Channel.Writer.TryWrite(snapshot);
    }

    private void RequestCheckpoint() => Interlocked.Exchange(ref _checkpointNeeded, 1);

    private async Task CheckpointLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                if (Interlocked.Exchange(ref _checkpointNeeded, 0) != 0) await PersistCheckpointAsync();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (Interlocked.Exchange(ref _checkpointNeeded, 0) != 0) await PersistCheckpointAsync();
        }
    }

    private async Task PersistCheckpointAsync()
    {
        try { await PersistAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Auftragsfortschritt konnte nicht gespeichert werden. Der Speichervorgang wird wiederholt.");
            RequestCheckpoint();
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_storePath))!);
            var snapshots = _jobs.Values.Select(job => job.ToPersisted()).ToArray();
            var jobs = snapshots.Where(job => IsTerminal(job.Status)).OrderBy(job => job.CreatedAt).TakeLast(200)
                .Concat(snapshots.Where(job => !IsTerminal(job.Status))).ToArray();
            UndoRecord[] undo;
            lock (_undoLock) undo = _undo.ToArray();
            var document = new JobStoreDocument(jobs, undo);
            var temporary = _storePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
            File.Move(temporary, _storePath, overwrite: true);
        }
        finally { _storeLock.Release(); }
    }

    private void LoadStore()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var document = JsonSerializer.Deserialize<JobStoreDocument>(File.ReadAllText(_storePath), JsonOptions);
            if (document is null) return;
            lock (_undoLock)
            {
                _undo.AddRange(document.Undo);
                TrimUndoHistory();
            }
            foreach (var record in document.Jobs)
            {
                var state = JobState.FromPersisted(record);
                if (!IsTerminal(state.Status))
                {
                    state.Status = "cancelled";
                    state.Message = "Beim Neustart unterbrochen. Starte den Auftrag bei Bedarf neu.";
                    if (state.Kind != "undo-batch") AddUndoFor(state);
                }
                _jobs[state.Id] = state;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Gespeicherte Aufträge konnten nicht geladen werden.");
            try { File.Move(_storePath, _storePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
        }
    }

    private void AddUndoFor(JobState job)
    {
        UndoRecord[] records;
        lock (job.Sync) records = job.UndoActions.ToArray();
        if (records.Length == 0) return;
        lock (_undoLock)
        {
            if (_undo.Any(record => record.JobId == job.Id)) return;
            _undo.AddRange(records);
            TrimUndoHistory();
        }
    }

    private void ConsumeUndoStep(JobState job, OperationUndoAction action)
    {
        if (job.UndoSourceJobId is not { } sourceJobId) return;
        lock (_undoLock)
        {
            var index = _undo.FindLastIndex(record => record.JobId == sourceJobId &&
                SameOwner(record.Owner, job.Owner) && ToUndoAction(record) == action);
            if (index >= 0) _undo.RemoveAt(index);
        }
        RequestCheckpoint();
    }

    private void AddUndo(JobState job, string kind, string original, string result)
    {
        lock (job.Sync) job.UndoActions.Add(new UndoRecord(job.Id, kind, original, result, DateTimeOffset.UtcNow, job.Owner));
        RequestCheckpoint();
    }

    private static bool TryDeleteMovedSource(CopyResult copy)
    {
        try
        {
            using var source = new FileStream(copy.SourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 1, FileOptions.None);
            if (source.Length != copy.SourceLength || File.GetLastWriteTimeUtc(copy.SourcePath) != copy.SourceLastWriteTimeUtc)
                return false;
            File.Delete(copy.SourcePath);
            return !File.Exists(copy.SourcePath);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void UpdateUndoKind(JobState job, string resultPath, string kind)
    {
        lock (job.Sync)
        {
            for (var index = job.UndoActions.Count - 1; index >= 0; index--)
            {
                var record = job.UndoActions[index];
                if (record.Kind != "copy" || !string.Equals(record.ResultPath, resultPath, StringComparison.OrdinalIgnoreCase)) continue;
                job.UndoActions[index] = record with { Kind = kind };
                RequestCheckpoint();
                return;
            }
        }
    }

    private void TrimUndoHistory()
    {
        var groups = _undo.GroupBy(record => record.JobId).ToArray();
        var retainedSteps = _undo.Count;
        var retainedJobs = groups.Length;
        var discard = new HashSet<string>();
        foreach (var group in groups)
        {
            if (retainedJobs == 1 || (retainedJobs <= 50 && retainedSteps <= 50_000)) break;
            if (_jobs.Values.Any(job => job.UndoSourceJobId == group.Key && !job.IsIdle)) continue;
            discard.Add(group.Key);
            retainedSteps -= group.Count();
            retainedJobs--;
        }
        if (discard.Count != 0) _undo.RemoveAll(record => discard.Contains(record.JobId));
    }

    private List<(string Path, bool Directory)> EnumerateArchiveItems(string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source)) return [(source, false)];
        var result = new List<(string Path, bool Directory)> { (source, true) };
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!_policy.IsAllowed(child)) continue;
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                result.Add((child, (attributes & FileAttributes.Directory) != 0));
            }
        }
        return result;
    }

    private static string GetAvailablePath(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var extension = Path.GetExtension(path);
        var name = Path.GetFileNameWithoutExtension(path);
        for (var index = 2; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Für die Datei konnte kein freier Name gefunden werden.");
    }

    private static bool SameVolume(string first, string second) =>
        string.Equals(Path.GetPathRoot(first), Path.GetPathRoot(second), StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private static string JobMessage(string kind) => kind switch
    {
        "copy" => "Wird kopiert",
        "move" => "Wird verschoben",
        "rename" or "rename-series" => "Wird umbenannt",
        "delete" => "Wird in den Papierkorb verschoben",
        "zip" => "ZIP-Archiv wird erstellt",
        "unzip" => "Archiv wird entpackt",
        "new-folder" => "Ordner wird erstellt",
        "new-file" => "Datei wird erstellt",
        _ => "Wird verarbeitet"
    };

    private static bool IsTerminal(string status) => status is "completed" or "completed-with-skips" or "failed" or "cancelled";

    private sealed class JobState(string id, string kind, OperationRequest request, string? owner)
    {
        public object Sync { get; } = new();
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public OperationRequest Request { get; } = request;
        public string? Owner { get; } = owner;
        public string Status { get; set; } = "queued";
        public string Message { get; set; } = "Wartet auf Verarbeitung";
        public string? Error { get; set; }
        public long CompletedBytes { get; set; }
        public long? TotalBytes { get; set; }
        public int CompletedItems { get; set; }
        public int? TotalItems { get; set; }
        public int SkippedItems { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset LastPublish { get; set; } = DateTimeOffset.MinValue;
        public string? ConflictMode { get; set; }
        public ConflictPrompt? Conflict { get; set; }
        public TaskCompletionSource<ConflictResolution>? ConflictCompletion { get; set; }
        public TaskCompletionSource? PauseCompletion { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public bool CancelRequested { get; set; }
        public string? UndoSourceJobId { get; set; }
        public List<UndoRecord> UndoActions { get; } = [];
        public bool IsIdle { get { lock (Sync) return IsTerminal(Status) && Cancellation is null; } }

        public JobSnapshot Snapshot()
        {
            lock (Sync)
            {
                double? speed = StartedAt is null ? null : CompletedBytes / Math.Max(.001, (DateTimeOffset.UtcNow - StartedAt.Value).TotalSeconds);
                var remaining = speed.GetValueOrDefault() > 0 && TotalBytes.HasValue
                    ? Math.Max(0, TotalBytes.Value - CompletedBytes) / speed
                    : (double?)null;
                return new JobSnapshot(Id, Kind, Status, Message, CompletedBytes, TotalBytes, CompletedItems, TotalItems,
                    speed, CreatedAt, Error, Kind, Conflict, remaining, SkippedItems);
            }
        }

        public PersistedJob ToPersisted()
        {
            lock (Sync)
            {
                var terminal = IsTerminal(Status);
                var request = terminal ? new OperationRequest(Kind, []) : Request;
                return new PersistedJob(Id, Kind, request, Status, Message, Error, CompletedBytes, TotalBytes,
                    CompletedItems, TotalItems, SkippedItems, CreatedAt, StartedAt, ConflictMode,
                    terminal ? [] : UndoActions.ToArray(), UndoSourceJobId, Owner);
            }
        }

        public static JobState FromPersisted(PersistedJob record)
        {
            var job = new JobState(record.Id, record.Kind, record.Request, record.Owner)
            {
                Status = record.Status,
                Message = record.Message,
                Error = record.Error,
                CompletedBytes = record.CompletedBytes,
                TotalBytes = record.TotalBytes,
                CompletedItems = record.CompletedItems,
                TotalItems = record.TotalItems,
                SkippedItems = record.SkippedItems,
                CreatedAt = record.CreatedAt,
                StartedAt = record.StartedAt,
                ConflictMode = record.ConflictMode,
                UndoSourceJobId = record.UndoSourceJobId
            };
            job.UndoActions.AddRange(record.UndoActions ?? []);
            return job;
        }
    }

    private sealed record TransferItem(string SourcePath, string TargetPath, bool IsDirectory, long Size, string SourceRoot, DateTime LastWriteTimeUtc);
    private sealed record CopyResult(string SourcePath, string TargetPath, long SourceLength, DateTime SourceLastWriteTimeUtc);
    private sealed record PendingFolderMoveCopy(string SourceRoot, string SourcePath, CopyResult Copy);
    private sealed record JobSubscriber(string? Owner, Channel<JobSnapshot> Channel);
    private sealed record UndoRecord(string JobId, string Kind, string OriginalPath, string ResultPath, DateTimeOffset CreatedAt,
        string? Owner = null);
    private sealed record JobStoreDocument(PersistedJob[] Jobs, UndoRecord[] Undo);
    private sealed record PersistedJob(string Id, string Kind, OperationRequest Request, string Status, string Message,
        string? Error, long CompletedBytes, long? TotalBytes, int CompletedItems, int? TotalItems, int SkippedItems,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, string? ConflictMode, UndoRecord[] UndoActions,
        string? UndoSourceJobId = null, string? Owner = null);
}
