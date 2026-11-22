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
    private readonly ConcurrentDictionary<Guid, Channel<JobSnapshot>> _subscribers = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private readonly SemaphoreSlim _undoScheduleGate = new(1, 1);
    private readonly object _undoLock = new();
    private readonly List<UndoRecord> _undo = [];
    private readonly string _storePath;

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

    public IReadOnlyList<JobSnapshot> GetJobs() => _jobs.Values
        .Select(job => job.Snapshot())
        .OrderByDescending(job => job.CreatedAt)
        .ToArray();

    public JobSnapshot? GetJob(string id) => _jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;

    public Task<JobSnapshot> EnqueueAsync(OperationRequest request, CancellationToken cancellationToken) =>
        EnqueueInternalAsync(request, null, cancellationToken);

    private async Task<JobSnapshot> EnqueueInternalAsync(OperationRequest request, string? undoSourceJobId,
        CancellationToken cancellationToken)
    {
        if (request.Sources is null || request.Sources.Count > 10000)
            throw new ArgumentException("Die Auswahl ist ungültig oder enthält zu viele Einträge.");
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Wähle zuerst einen Dateivorgang aus.");
        var id = Guid.NewGuid().ToString("N");
        var state = new JobState(id, kind, request with { Kind = kind }) { UndoSourceJobId = undoSourceJobId };
        if (!_jobs.TryAdd(id, state)) throw new InvalidOperationException("Der Auftrag konnte nicht angelegt werden.");
        _logger.LogInformation("Dateioperation {JobId} ({Kind}) von {SourceCount} Quelle(n) zu {Destination} angelegt: {Sources}",
            id, kind, request.Sources.Count, request.Destination, string.Join(" | ", request.Sources));
        await PersistAsync(cancellationToken);
        _queue.Writer.TryWrite(id);
        Publish(state);
        return state.Snapshot();
    }

    public bool Pause(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        lock (job.Sync)
        {
            if (job.Status != "running") return false;
            job.PauseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            job.Status = "paused";
            job.Message = "Pausiert";
        }
        Publish(job);
        _ = PersistAsync(CancellationToken.None);
        return true;
    }

    public bool Resume(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        lock (job.Sync)
        {
            if (job.Status != "paused") return false;
            job.Status = "running";
            job.Message = "Wird fortgesetzt";
            job.PauseCompletion?.TrySetResult();
            job.PauseCompletion = null;
        }
        Publish(job);
        _ = PersistAsync(CancellationToken.None);
        return true;
    }

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
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
        if (cancelledBeforeStart) _ = PersistAsync(CancellationToken.None);
        return true;
    }

    public bool ResolveConflict(string id, ConflictResolution resolution)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        var choice = resolution.Choice.Trim().ToLowerInvariant();
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

    public async IAsyncEnumerable<JobSnapshot> Watch([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<JobSnapshot>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        try
        {
            foreach (var job in GetJobs()) yield return job;
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken)) yield return update;
        }
        finally { _subscribers.TryRemove(id, out _); }
    }

    public async Task<JobSnapshot?> UndoLastAsync(CancellationToken cancellationToken)
    {
        await _undoScheduleGate.WaitAsync(cancellationToken);
        try
        {
            UndoRecord[] records;
            lock (_undoLock)
            {
                var last = _undo.LastOrDefault();
                if (last is null) return null;
                var pending = _jobs.Values
                    .Select(job => job.Snapshot())
                    .FirstOrDefault(job => job.Status is not ("failed" or "cancelled") &&
                        _jobs[job.Id].UndoSourceJobId == last.JobId);
                if (pending is not null) return pending;
                records = _undo.Where(record => record.JobId == last.JobId).ToArray();
            }
            var actions = records.Reverse().Select(record => record.Kind switch
            {
                "delete" => new OperationUndoAction("restore", record.OriginalPath, ""),
                "move" or "rename" => new OperationUndoAction("move", record.ResultPath, record.OriginalPath),
                "restore-folder" => new OperationUndoAction("create-folder", record.OriginalPath, ""),
                "copy" or "create-folder" or "create-file" or "zip" or "unzip" => new OperationUndoAction("delete", record.ResultPath, ""),
                _ => null
            }).Where(action => action is not null).Cast<OperationUndoAction>().ToArray();
            if (actions.Length == 0) return null;

            return await EnqueueInternalAsync(
                new OperationRequest("undo-batch", [], UndoActions: actions), records[0].JobId, cancellationToken);
        }
        finally { _undoScheduleGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, 2).Select(_ => WorkerAsync(stoppingToken)).ToArray();
        foreach (var job in _jobs.Values.Where(job => !IsTerminal(job.Status)))
        {
            lock (job.Sync)
            {
                job.Status = "queued";
                job.Conflict = null;
                job.ConflictCompletion = null;
                job.PauseCompletion = null;
                job.Message = "Wartet auf Verarbeitung";
                job.CompletedBytes = 0;
                job.CompletedItems = 0;
                job.SkippedItems = 0;
            }
            _queue.Writer.TryWrite(job.Id);
        }
        await PersistAsync(stoppingToken);
        await Task.WhenAll(workers);
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
                await PersistAsync(stoppingToken);
                try
                {
                    await ExecuteOperationAsync(job, cancellation.Token);
                    lock (job.Sync)
                    {
                        job.Status = job.SkippedItems > 0 ? "completed-with-skips" : "completed";
                        job.Message = job.SkippedItems > 0
                            ? $"Abgeschlossen · {job.SkippedItems} Elemente übersprungen"
                            : "Abgeschlossen";
                        job.Conflict = null;
                    }
                    if (job.Kind == "undo-batch") ConsumeUndoFor(job);
                    else AddUndoFor(job);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    lock (job.Sync)
                    {
                        job.Status = stoppingToken.IsCancellationRequested ? "queued" : "cancelled";
                        job.Message = stoppingToken.IsCancellationRequested
                            ? "Wird beim nächsten Start fortgesetzt"
                            : $"Abgebrochen · {job.CompletedItems} abgeschlossene Elemente bleiben erhalten";
                        job.Conflict = null;
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Dateioperation {JobId} ist fehlgeschlagen", job.Id);
                    lock (job.Sync)
                    {
                        job.Status = "failed";
                        job.Error = GermanErrors.For(exception);
                        job.Message = job.Error;
                        job.Conflict = null;
                    }
                }
                finally
                {
                    lock (job.Sync)
                    {
                        job.Cancellation = null;
                        job.ConflictCompletion = null;
                        job.PauseCompletion?.TrySetResult();
                        job.PauseCompletion = null;
                    }
                    Publish(job);
                    await PersistAsync(CancellationToken.None);
                    _logger.LogInformation("Dateioperation {JobId} ({Kind}) beendet: {Status}. Quellen: {Sources}; Ziel: {Destination}",
                        job.Id, job.Kind, job.Status, string.Join(" | ", job.Request.Sources), job.Request.Destination);
                }
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
        var sources = job.Request.Sources.Select(path => _policy.Authorize(path, write: move)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var plan = new List<TransferItem>();
        var folderMoveCopies = new List<PendingFolderMoveCopy>();
        foreach (var source in sources)
        {
            if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException();
            var rootName = sources.Length == 1 && !string.IsNullOrWhiteSpace(job.Request.NewName)
                ? job.Request.NewName
                : Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            var targetRoot = Path.Combine(destination, rootName);
            if (Directory.Exists(source)) EnumerateTree(source, targetRoot, plan, cancellationToken);
            else
            {
                var info = new FileInfo(source);
                plan.Add(new TransferItem(source, targetRoot, false, info.Length, source, info.LastWriteTimeUtc));
            }
        }
        SetTotals(job, plan.Count, plan.Sum(item => item.Size));

        if (move && sources.Length == 1 && Directory.Exists(sources[0]) && _sameVolume(sources[0], destination))
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
                    await _recycleBin.DeleteAsync(target, cancellationToken);
                    AddUndo(job, "delete", target, "");
                }
            }
            _policy.Authorize(target, write: true);
            Directory.Move(sources[0], target);
            AddUndo(job, "move", sources[0], target);
            SetProgress(job, job.TotalBytes ?? 0, job.TotalItems ?? 0, "Ordner verschoben");
            return;
        }

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(job, cancellationToken);
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

            var targetFile = _policy.Authorize(item.TargetPath, write: true);
            var sourceFile = _policy.Authorize(item.SourcePath);
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
                        await _recycleBin.DeleteAsync(targetFile, cancellationToken);
                        AddUndo(job, "delete", targetFile, "");
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
            foreach (var source in sources.Where(Directory.Exists))
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

        target = _policy.Authorize(target, write: true);
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
                if (Directory.Exists(target)) throw new IOException($"Am Ziel liegt ein Ordner mit dem Namen „{Path.GetFileName(target)}“.");
                await _recycleBin.DeleteAsync(target, cancellationToken);
                AddUndo(job, "delete", target, "");
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
            var source = _policy.Authorize(job.Request.Sources[index], write: true);
            if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException();
            var originalName = Path.GetFileName(source);
            var extension = Path.GetExtension(originalName);
            var stem = Path.GetFileNameWithoutExtension(originalName);
            var pattern = job.Request.NewName!;
            var name = pattern.Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
                .Replace("{ext}", extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                .Replace("{n}", (index + 1).ToString("D2"), StringComparison.OrdinalIgnoreCase);
            if (job.Request.Sources.Count > 1 && !pattern.Contains("{n}", StringComparison.OrdinalIgnoreCase) && index > 0)
                name = $"{Path.GetFileNameWithoutExtension(name)} ({index + 1}){Path.GetExtension(name)}";
            if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || name is "." or "..")
                throw new ArgumentException("Der Name darf keinen Pfad enthalten.");
            var target = _policy.Authorize(Path.Combine(Path.GetDirectoryName(source)!, name), write: true);
            if (PathEquals(source, target)) { job.CompletedItems++; continue; }
            if (File.Exists(target) || Directory.Exists(target))
            {
                var choice = await ResolveConflictAsync(job, source, target, cancellationToken);
                if (choice == "skip") { job.SkippedItems++; job.CompletedItems++; continue; }
                if (choice == "keep-both") target = GetAvailablePath(target);
                else
                {
                    await _recycleBin.DeleteAsync(target, cancellationToken);
                    AddUndo(job, "delete", target, "");
                }
            }
            if (Directory.Exists(source)) Directory.Move(source, target);
            else File.Move(source, target);
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
            var path = _policy.Authorize(rawPath, write: true);
            if (!File.Exists(path) && !Directory.Exists(path)) { job.CompletedItems++; continue; }
            if (job.Request.Permanent)
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else File.Delete(path);
            }
            else await _recycleBin.DeleteAsync(path, cancellationToken);
            if (!job.Request.Permanent) AddUndo(job, "delete", path, "");
            job.CompletedItems++;
            PublishIfDue(job);
        }
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
                await _recycleBin.RestoreOriginalPathAsync(action.SourcePath, cancellationToken);
            }
            else if (action.Kind == "move")
            {
                var source = _policy.Authorize(action.SourcePath, write: true);
                var target = _policy.Authorize(action.TargetPath, write: true);
                if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException();
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException($"„{Path.GetFileName(target)}“ ist am ursprünglichen Ort bereits vorhanden.");
                var parent = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(parent)) throw new IOException("Der ursprüngliche Ordner ist nicht mehr erreichbar.");
                Directory.CreateDirectory(parent);
                if (Directory.Exists(source)) Directory.Move(source, target);
                else File.Move(source, target);
            }
            else if (action.Kind == "create-folder")
            {
                var path = _policy.Authorize(action.SourcePath, write: true);
                Directory.CreateDirectory(path);
            }
            else if (action.Kind == "delete")
            {
                var path = _policy.Authorize(action.SourcePath, write: true);
                if (Directory.Exists(path) || File.Exists(path)) await _recycleBin.DeleteAsync(path, cancellationToken);
            }
            else throw new ArgumentException("Die Rückgängig-Aktion ist ungültig.");

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
        var target = _policy.Authorize(Path.Combine(parent, job.Request.NewName), write: true);
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
        var target = _policy.Authorize(Path.Combine(parent, job.Request.NewName), write: true);
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
        var target = _policy.Authorize(Path.Combine(destination, requestedName), write: true);
        if (File.Exists(target) || Directory.Exists(target))
        {
            var choice = await ResolveConflictAsync(job, source, target, cancellationToken);
            if (choice == "skip") { job.SkippedItems = 1; return; }
            if (choice == "keep-both") target = GetAvailablePath(target);
        }
        var files = EnumerateFiles(source, cancellationToken);
        var totalBytes = files.Sum(file => new FileInfo(file).Length);
        SetTotals(job, files.Count + 1, totalBytes);
        var stage = Path.Combine(destination, $".wme-{job.Id}-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                File.SetAttributes(stage, FileAttributes.Hidden);
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var root = Directory.Exists(source) ? source : Path.GetDirectoryName(source)!;
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(job, cancellationToken);
                    var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (Directory.Exists(source)) relative = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)) + "/" + relative;
                    var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var entryStream = entry.Open();
                    var buffer = new byte[128 * 1024];
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
            if (File.Exists(target))
            {
                await _recycleBin.DeleteAsync(target, cancellationToken);
                AddUndo(job, "delete", target, "");
            }
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
        var target = _policy.Authorize(Path.Combine(destination, folderName), write: true);
        if (Directory.Exists(target) || File.Exists(target))
        {
            var choice = await ResolveConflictAsync(job, source, target, cancellationToken);
            if (choice == "skip") { job.SkippedItems = 1; return; }
            if (choice == "keep-both") target = GetAvailablePath(target);
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
            SetTotals(job, archive.Entries.Count, archive.Entries.Sum(entry => entry.Length));
            var stageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging));
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(job, cancellationToken);
                if (string.IsNullOrEmpty(entry.FullName)) continue;
                var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(entryPath)) throw new InvalidDataException("Das ZIP enthält einen ungültigen absoluten Pfad.");
                var outputPath = Path.GetFullPath(Path.Combine(stageRoot, entryPath));
                var relative = Path.GetRelativePath(stageRoot, outputPath);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    throw new InvalidDataException("Das ZIP versucht, Dateien außerhalb des Zielordners abzulegen.");
                _ = PathAccessPolicy.NormalizeAbsolute(outputPath);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
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
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        job.CompletedBytes += read;
                        PublishIfDue(job);
                    }
                    job.CompletedItems++;
                    }
                }
            }
            if (Directory.Exists(target) || File.Exists(target))
            {
                await _recycleBin.DeleteAsync(target, cancellationToken);
                AddUndo(job, "delete", target, "");
            }
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
        var destinations = new List<(SevenZipArchiveEntry Entry, string OutputPath)>();
        var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsLink)
                throw new InvalidDataException("Das 7-Zip-Archiv enthält eine Verknüpfung. Verknüpfungen werden aus Sicherheitsgründen nicht entpackt.");

            var normalizedEntry = entry.Path.Replace('\\', '/');
            if (normalizedEntry.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(normalizedEntry) || normalizedEntry.Contains(':'))
                throw new InvalidDataException("Das 7-Zip-Archiv enthält einen ungültigen absoluten Pfad.");
            var segments = normalizedEntry.Split('/', StringSplitOptions.None);
            if (segments.Length == 0 || segments.Any(segment =>
                    segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0))
                throw new InvalidDataException("Das 7-Zip-Archiv enthält einen ungültigen oder unsicheren Dateinamen.");

            var outputPath = Path.GetFullPath(Path.Combine(stageRoot, Path.Combine(segments)));
            var relative = Path.GetRelativePath(stageRoot, outputPath);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new InvalidDataException("Das 7-Zip-Archiv versucht, Dateien außerhalb des Zielordners abzulegen.");
            _ = PathAccessPolicy.NormalizeAbsolute(outputPath);

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
        foreach (var (entry, outputPath) in destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(job, cancellationToken);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(outputPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var extracted = await _sevenZip.ExtractEntryAsync(archivePath, entry.Path, outputPath, read =>
                {
                    job.CompletedBytes += read;
                    PublishIfDue(job);
                }, cancellationToken);
                if (extracted != entry.Size)
                    throw new InvalidDataException("Die Dateigröße im 7-Zip-Archiv stimmt nicht mit dem gelesenen Inhalt überein.");
            }
            job.CompletedItems++;
            PublishIfDue(job);
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
        _ = PersistAsync(CancellationToken.None);
    }

    private void Publish(JobState job)
    {
        var snapshot = job.Snapshot();
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(snapshot);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            PersistedJob[] jobs = _jobs.Values.Select(job => job.ToPersisted()).ToArray();
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
            foreach (var record in document.Jobs.TakeLast(200))
            {
                var state = JobState.FromPersisted(record);
                _jobs[state.Id] = state;
            }
            lock (_undoLock) _undo.AddRange(document.Undo.TakeLast(50));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Gespeicherte Aufträge konnten nicht geladen werden.");
            try { File.Move(_storePath, _storePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
        }
    }

    private void AddUndoFor(JobState job)
    {
        lock (job.Sync)
        {
            foreach (var undo in job.UndoActions) AddUndoRecord(undo);
        }
    }

    private void ConsumeUndoFor(JobState job)
    {
        string? sourceJobId;
        lock (job.Sync) sourceJobId = job.UndoSourceJobId;
        if (sourceJobId is null) return;
        lock (_undoLock) _undo.RemoveAll(record => record.JobId == sourceJobId);
    }

    private void AddUndo(JobState job, string kind, string original, string result)
    {
        lock (job.Sync) job.UndoActions.Add(new UndoRecord(job.Id, kind, original, result, DateTimeOffset.UtcNow));
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

    private static void UpdateUndoKind(JobState job, string resultPath, string kind)
    {
        lock (job.Sync)
        {
            for (var index = job.UndoActions.Count - 1; index >= 0; index--)
            {
                var record = job.UndoActions[index];
                if (record.Kind != "copy" || !string.Equals(record.ResultPath, resultPath, StringComparison.OrdinalIgnoreCase)) continue;
                job.UndoActions[index] = record with { Kind = kind };
                return;
            }
        }
    }

    private void AddUndoRecord(UndoRecord record)
    {
        lock (_undoLock)
        {
            _undo.Add(record);
            if (_undo.Count > 50) _undo.RemoveRange(0, _undo.Count - 50);
        }
    }

    private List<string> EnumerateFiles(string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source)) return [source];
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                else result.Add(child);
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

    private sealed class JobState(string id, string kind, OperationRequest request)
    {
        public object Sync { get; } = new();
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public OperationRequest Request { get; } = request;
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
            lock (Sync) return new PersistedJob(Id, Kind, Request, Status, Message, Error, CompletedBytes, TotalBytes,
                CompletedItems, TotalItems, SkippedItems, CreatedAt, StartedAt, ConflictMode, UndoActions.ToArray(),
                UndoSourceJobId);
        }

        public static JobState FromPersisted(PersistedJob record)
        {
            var job = new JobState(record.Id, record.Kind, record.Request)
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
            job.UndoActions.AddRange(record.UndoActions);
            return job;
        }
    }

    private sealed record TransferItem(string SourcePath, string TargetPath, bool IsDirectory, long Size, string SourceRoot, DateTime LastWriteTimeUtc);
    private sealed record CopyResult(string SourcePath, string TargetPath, long SourceLength, DateTime SourceLastWriteTimeUtc);
    private sealed record PendingFolderMoveCopy(string SourceRoot, string SourcePath, CopyResult Copy);
    private sealed record UndoRecord(string JobId, string Kind, string OriginalPath, string ResultPath, DateTimeOffset CreatedAt);
    private sealed record JobStoreDocument(PersistedJob[] Jobs, UndoRecord[] Undo);
    private sealed record PersistedJob(string Id, string Kind, OperationRequest Request, string Status, string Message,
        string? Error, long CompletedBytes, long? TotalBytes, int CompletedItems, int? TotalItems, int SkippedItems,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, string? ConflictMode, UndoRecord[] UndoActions,
        string? UndoSourceJobId = null);
}
