using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Explorer.Host.Tests;

public sealed class FilesystemIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-Integration-Ä- {Guid.NewGuid():N}");
    private readonly PathAccessPolicy _policy;
    private readonly ITestOutputHelper _output;

    public FilesystemIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
        _policy = new PathAccessPolicy([new ShareRoot(_root)]);
    }

    [Fact]
    public async Task FolderSizesIncludeNestedFilesAndSortAcrossPages()
    {
        var small = Path.Combine(_root, "A-klein");
        var large = Path.Combine(_root, "Z-gross");
        Directory.CreateDirectory(small);
        Directory.CreateDirectory(Path.Combine(large, "Unterordner"));
        await File.WriteAllBytesAsync(Path.Combine(small, "klein.bin"), new byte[1024]);
        await File.WriteAllBytesAsync(Path.Combine(large, "Unterordner", "gross.bin"), new byte[8192]);
        var sizes = new FolderSizeService(_policy);
        var listing = new FileExplorerService(_policy, folderSizes: sizes);
        FolderSizesResult? result = null;
        for (var attempt = 0; attempt < 100 && result is null; attempt++)
        {
            result = sizes.GetOrStart(_root);
            if (result is null) await Task.Delay(25);
        }
        Assert.NotNull(result);
        Assert.Equal(1024, result.Folders[small].Bytes);
        Assert.Equal(8192, result.Folders[large].Bytes);
        Assert.False(result.Folders[large].Incomplete);
        var first = await listing.ListAsync(_root, 0, 1, "folder-size", true, null, false, CancellationToken.None, refresh: true);
        var second = await listing.ListAsync(_root, 1, 1, "folder-size", true, null, false, CancellationToken.None);
        Assert.Equal("Z-gross", first.Entries[0].Name);
        Assert.Equal(8192, first.Entries[0].Size);
        Assert.False(first.Entries[0].SizeIncomplete);
        Assert.Equal("A-klein", second.Entries[0].Name);
        var ascending = await listing.ListAsync(_root, 0, 2, "folder-size", false, null, false, CancellationToken.None, refresh: true);
        Assert.Equal(new[] { "A-klein", "Z-gross" }, ascending.Entries.Select(entry => entry.Name));
        await File.WriteAllBytesAsync(Path.Combine(large, "Unterordner", "gross.bin"), new byte[256]);
        Assert.Null(sizes.GetOrStart(_root, refresh: true));
        result = null;
        for (var attempt = 0; attempt < 100 && result is null; attempt++)
        {
            result = sizes.GetOrStart(_root);
            if (result is null) await Task.Delay(25);
        }
        Assert.NotNull(result);
        Assert.Equal(256, result.Folders[large].Bytes);
        var refreshed = await listing.ListAsync(_root, 0, 2, "folder-size", true, null, false, CancellationToken.None, refresh: true);
        Assert.Equal(new[] { "A-klein", "Z-gross" }, refreshed.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task JobsCreateCopyZipAndUnzipInsideTheSharedTestRoot()
    {
        var source = Path.Combine(_root, "Quelle mit Leerzeichen");
        var nested = Path.Combine(source, "Unterordner");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(source, "Grüße.txt"), "Hallo Windows", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(nested, "Werte.csv"), "ä,ö,ü", Encoding.UTF8);
        using var service = CreateJobService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var folder = await CompleteJobAsync(service, new OperationRequest("new-folder", [], _root, "Neu"));
            Assert.Equal("completed", folder.Status);
            Assert.True(Directory.Exists(Path.Combine(_root, "Neu")));

            var markdown = await CompleteJobAsync(service, new OperationRequest("new-file", [], source, "Notiz.md", Template: "markdown"));
            Assert.Equal("completed", markdown.Status);
            Assert.Equal("# Titel\n\n", await File.ReadAllTextAsync(Path.Combine(source, "Notiz.md")));

            var copy = await CompleteJobAsync(service, new OperationRequest("copy", [source], Path.Combine(_root, "Neu")));
            Assert.Equal("completed", copy.Status);
            var copiedFolder = Path.Combine(_root, "Neu", Path.GetFileName(source));
            Assert.Equal("Hallo Windows", await File.ReadAllTextAsync(Path.Combine(copiedFolder, "Grüße.txt"), Encoding.UTF8));

            var renamed = await CompleteJobAsync(service, new OperationRequest("rename", [Path.Combine(copiedFolder, "Grüße.txt")], NewName: "Umlaute äöü.txt"));
            Assert.Equal("completed", renamed.Status);
            Assert.True(File.Exists(Path.Combine(copiedFolder, "Umlaute äöü.txt")));

            var zip = await CompleteJobAsync(service, new OperationRequest("zip", [source], _root));
            Assert.Equal("completed", zip.Status);
            var archive = Path.Combine(_root, "Quelle mit Leerzeichen.zip");
            Assert.True(File.Exists(archive));

            var unzip = await CompleteJobAsync(service, new OperationRequest("unzip", [archive], _root, "Entpackt"));
            Assert.Equal("completed", unzip.Status);
            Assert.Equal("ä,ö,ü", await File.ReadAllTextAsync(Path.Combine(_root, "Entpackt", "Quelle mit Leerzeichen", "Unterordner", "Werte.csv"), Encoding.UTF8));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task LongWindowsPathsCanBeListedCopiedAndRenamedThroughTheJobSystem()
    {
        var longFolder = Path.Combine(_root, new string('L', 96), new string('D', 96));
        Directory.CreateDirectory(longFolder);
        var fileName = new string('f', 48) + ".txt";
        var longFile = Path.Combine(longFolder, fileName);
        Assert.True(longFile.Length > 260, $"Testpfad hat nur {longFile.Length} Zeichen.");
        await File.WriteAllTextAsync(longFile, "Long path works");

        Assert.Equal(longFile, _policy.Authorize(_policy.ToLongPath(longFile)));
        var listing = await new FileExplorerService(_policy).ListAsync(longFolder, 0, 20, "name", false,
            null, false, CancellationToken.None, refresh: true);
        Assert.Contains(listing.Entries, entry => entry.Name == fileName && entry.Size == "Long path works".Length);

        var destination = Path.Combine(_root, "LongPath-Kopie");
        Directory.CreateDirectory(destination);
        using var service = CreateJobService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var copy = await CompleteJobAsync(service, new OperationRequest("copy", [longFile], destination));
            Assert.Equal("completed", copy.Status);
            Assert.Equal("Long path works", await File.ReadAllTextAsync(Path.Combine(destination, fileName)));

            var renamed = await CompleteJobAsync(service, new OperationRequest("rename", [longFile], NewName: "renamed-long-path.txt"));
            Assert.Equal("completed", renamed.Status);
            Assert.False(File.Exists(longFile));
            Assert.Equal("Long path works", await File.ReadAllTextAsync(Path.Combine(longFolder, "renamed-long-path.txt")));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task InstalledSevenZipCanExtractUnicodePathsThroughTheJobSystem()
    {
        var sevenZip = SevenZipArchiveService.FindExecutable();
        if (sevenZip is null) return;

        var archiveSource = Path.Combine(_root, "7z-Quelle");
        var nested = Path.Combine(archiveSource, "Ordner ä");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "Grüße.txt"), "7-Zip funktioniert", Encoding.UTF8);
        var archive = Path.Combine(_root, "Testarchiv.7z");
        var start = new ProcessStartInfo(sevenZip)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = archiveSource
        };
        start.ArgumentList.Add("a");
        start.ArgumentList.Add("-t7z");
        start.ArgumentList.Add("-bd");
        start.ArgumentList.Add(archive);
        start.ArgumentList.Add(".");
        using (var process = Process.Start(start) ?? throw new InvalidOperationException("7-Zip konnte im Integrationstest nicht gestartet werden."))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"7-Zip-Testarchiv konnte nicht angelegt werden: {await stderr}{await stdout}");
        }

        using var service = CreateJobService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var unzip = await CompleteJobAsync(service, new OperationRequest("unzip", [archive], _root, "7z-Entpackt"));
            Assert.True(unzip.Status == "completed", unzip.Error ?? unzip.Message);
            Assert.Equal("7-Zip funktioniert", await File.ReadAllTextAsync(Path.Combine(_root, "7z-Entpackt", "Ordner ä", "Grüße.txt"), Encoding.UTF8));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PropertiesReadPhotoAndMediaShellMetadataWithoutFailingWhenAHandlerIsMissing()
    {
        var imagePath = Path.Combine(_root, "Bild ä.jpg");
        var mediaPath = Path.Combine(_root, "Video.mp4");
        await File.WriteAllTextAsync(imagePath, "not an image");
        await File.WriteAllTextAsync(mediaPath, "not a media file");
        var service = new FilePropertiesService(_policy);

        var image = await service.GetAsync(imagePath, null, CancellationToken.None);
        var media = await service.GetAsync(mediaPath, null, CancellationToken.None);

        Assert.Equal("Bild ä.jpg", image.Name);
        Assert.Equal("Video.mp4", media.Name);
        Assert.Null(image.PhotoTaken);
        Assert.Null(image.ImageDimensions);
        Assert.Null(image.CameraManufacturer);
        Assert.Null(image.CameraModel);
        Assert.Null(media.MediaDurationSeconds);
    }

    [Fact]
    public async Task DirectorySizeIncludesNestedFiles()
    {
        var folder = Path.Combine(_root, "Ordnergröße");
        var nested = Path.Combine(folder, "Unterordner");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(folder, "eins.bin"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(nested, "zwei.bin"), [3, 4, 5]);

        var properties = await new FilePropertiesService(_policy).GetAsync(folder, null, CancellationToken.None);

        Assert.True(properties.IsDirectory);
        Assert.Equal(5, properties.Size);
    }

    [Fact]
    public async Task DirectoryReadOnlyAttributeDoesNotMarkAnEditableFolderAsReadOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(_root, "Desktop-like-folder");
        Directory.CreateDirectory(folder);
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly);
        var file = Path.Combine(_root, "read-only-file.txt");
        await File.WriteAllTextAsync(file, "read-only file");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        try
        {
            var service = new FileExplorerService(_policy);
            var page = await service.ListAsync(_root, 0, 20, "name", false, null, false, CancellationToken.None);

            Assert.False(Assert.Single(page.Entries, entry => entry.Path == folder).IsReadOnly);
            Assert.True(Assert.Single(page.Entries, entry => entry.Path == file).IsReadOnly);
            await File.WriteAllTextAsync(Path.Combine(folder, "editable.txt"), "Windows permits writing here");
            var contents = await service.ListAsync(folder, 0, 20, "name", false, null, false, CancellationToken.None);
            Assert.Single(contents.Entries, entry => entry.Name == "editable.txt");
        }
        finally
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            File.SetAttributes(folder, File.GetAttributes(folder) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public async Task ListingStillMarksFoldersWithReadOnlySharePermissions()
    {
        var folder = Path.Combine(_root, "protected");
        Directory.CreateDirectory(folder);
        _policy.ReplaceRoots([new ShareRoot(_root), new ShareRoot(folder, ReadOnly: true)]);

        var page = await new FileExplorerService(_policy).ListAsync(_root, 0, 20, "name", false, null, false, CancellationToken.None);

        Assert.True(Assert.Single(page.Entries, entry => entry.Path == folder).IsReadOnly);
    }

    [Fact]
    public async Task ListingContinuesWhenOneChildHasUnreadableWindowsDeviceMetadata()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_root, "readable.txt");
        await File.WriteAllTextAsync(file, "ok");
        var service = new FileExplorerService(_policy,
            enumerateEntries: _ => ["\\\\.\\nul", file]);

        var page = await service.ListAsync(_root, 0, 20, "name", false, null, false, CancellationToken.None);

        Assert.Equal(file, Assert.Single(page.Entries).Path);
    }

    [Fact]
    public async Task DirectorySizeTimesOutAndLimitsAStalledWorkerToOnePerDrive()
    {
        var firstFolder = Path.Combine(_root, "Langsame Abfrage");
        var secondFolder = Path.Combine(_root, "Gesperrte Folgeabfrage");
        Directory.CreateDirectory(firstFolder);
        Directory.CreateDirectory(secondFolder);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerations = 0;
        var service = new FilePropertiesService(_policy, TimeSpan.FromSeconds(2), folder =>
        {
            if (Interlocked.Increment(ref enumerations) == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            return Directory.EnumerateFileSystemEntries(folder);
        });

        try
        {
            var firstRequest = service.GetAsync(firstFolder, null, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<TimeoutException>(() => firstRequest);

            var secondRequest = service.GetAsync(secondFolder, null, CancellationToken.None);
            await Task.Delay(50);
            Assert.Equal(1, Volatile.Read(ref enumerations));
            release.TrySetResult();
            var secondProperties = await secondRequest.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, secondProperties.Size);
            Assert.Equal(2, Volatile.Read(ref enumerations));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task UploadChunksVerifyChecksumsAndResumeAfterServiceRestart()
    {
        var destination = Path.Combine(_root, "Upload-Ziel");
        Directory.CreateDirectory(destination);
        var metadata = Path.Combine(_root, "upload-state");
        var sourceBytes = Encoding.UTF8.GetBytes("Grüße aus dem iPhone 🗂 – Upload vollständig");
        var firstChunk = sourceBytes[..11];
        var secondChunk = sourceBytes[11..];
        var firstHash = Convert.ToHexString(SHA256.HashData(firstChunk));
        var secondHash = Convert.ToHexString(SHA256.HashData(secondChunk));
        var request = new UploadCreateRequest("Datei ä.txt", sourceBytes.Length, destination);
        var recycleBin = new TestRecycleBin(Path.Combine(_root, "upload-papierkorb"));
        var uploads = new UploadSessionService(_policy, recycleBin, NullLogger<UploadSessionService>.Instance, metadata);
        var session = await uploads.CreateAsync(request, "test@example.invalid", CancellationToken.None);

        await using (var bad = new MemoryStream(firstChunk))
            await Assert.ThrowsAsync<InvalidDataException>(() => uploads.WriteChunkAsync(session.Id, "test@example.invalid", 0, firstChunk.Length,
                new string('0', 64), bad, CancellationToken.None));
        Assert.Equal(0, new FileInfo(Path.Combine(destination, $".wme-upload-{session.Id}.partial")).Length);

        await using (var first = new MemoryStream(firstChunk))
        {
            var saved = await uploads.WriteChunkAsync(session.Id, "test@example.invalid", 0, firstChunk.Length, firstHash, first, CancellationToken.None);
            Assert.Equal(firstChunk.Length, saved.Offset);
            Assert.False(saved.Complete);
        }

        var restarted = new UploadSessionService(_policy, recycleBin, NullLogger<UploadSessionService>.Instance, metadata);
        var resumed = await restarted.CreateAsync(request with { ResumeId = session.Id }, "test@example.invalid", CancellationToken.None);
        Assert.Equal(firstChunk.Length, resumed.Offset);
        await using (var second = new MemoryStream(secondChunk))
        {
            var saved = await restarted.WriteChunkAsync(session.Id, "test@example.invalid", resumed.Offset, secondChunk.Length, secondHash, second, CancellationToken.None);
            Assert.True(saved.Complete);
        }

        var committed = await restarted.CommitAsync(session.Id, "test@example.invalid", null, CancellationToken.None);
        Assert.True(committed.Completed);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(committed.Path!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)), committed.Sha256);
    }

    [Fact]
    public async Task TwoGigabyteUploadResumesFromPersistedOffsetAfterServiceRestart()
    {
        const string owner = "large-test@example.invalid";
        const long totalBytes = 2L * 1024 * 1024 * 1024;
        const int chunkSize = 8 * 1024 * 1024;
        var chunkCount = checked((int)(totalBytes / chunkSize));
        var restartAtChunk = chunkCount / 2;
        var available = new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace;
        Assert.True(available >= totalBytes + 1024L * 1024 * 1024,
            $"Der 2-GB-Uploadtest benötigt mindestens 3 GB freien Speicher; verfügbar sind {available:N0} Bytes.");

        var destination = Path.Combine(_root, "2-GB-Ziel");
        Directory.CreateDirectory(destination);
        var metadata = Path.Combine(_root, "2-GB-upload-state");
        var request = new UploadCreateRequest("Video-2GB.bin", totalBytes, destination);
        var recycleBin = new TestRecycleBin(Path.Combine(_root, "2-GB-papierkorb"));
        var logger = NullLogger<UploadSessionService>.Instance;
        var uploads = new UploadSessionService(_policy, recycleBin, logger, metadata);
        var session = await uploads.CreateAsync(request, owner, CancellationToken.None);
        var chunk = new byte[chunkSize];
        var chunkHash = Convert.ToHexString(SHA256.HashData(chunk));
        using var wholeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;

        for (var index = 0; index < restartAtChunk; index++)
        {
            await using var input = new MemoryStream(chunk, writable: false);
            await uploads.WriteChunkAsync(session.Id, owner, offset, chunkSize, chunkHash, input, CancellationToken.None);
            wholeHash.AppendData(chunk);
            offset += chunkSize;
        }

        var restarted = new UploadSessionService(_policy, recycleBin, logger, metadata);
        var resumable = await restarted.CreateAsync(request with { ResumeId = session.Id }, owner, CancellationToken.None);
        Assert.Equal(offset, resumable.Offset);

        for (var index = restartAtChunk; index < chunkCount; index++)
        {
            await using var input = new MemoryStream(chunk, writable: false);
            await restarted.WriteChunkAsync(session.Id, owner, offset, chunkSize, chunkHash, input, CancellationToken.None);
            wholeHash.AppendData(chunk);
            offset += chunkSize;
        }

        var expectedHash = Convert.ToHexString(wholeHash.GetHashAndReset());
        var committed = await restarted.CommitAsync(session.Id, owner, null, CancellationToken.None);
        Assert.True(committed.Completed);
        Assert.Equal(expectedHash, committed.Sha256);
        Assert.Equal(totalBytes, new FileInfo(committed.Path!).Length);
    }

    [Fact]
    public async Task OneUndoReversesTheWholeCopyJobWithoutTouchingTheUserRecycleBin()
    {
        var source = Path.Combine(_root, "Mehrere Dateien");
        var nested = Path.Combine(source, "Unterordner");
        var destination = Path.Combine(_root, "Ziel");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "eins.txt"), "eins");
        await File.WriteAllTextAsync(Path.Combine(nested, "zwei.txt"), "zwei");
        var isolatedBin = new TestRecycleBin(Path.Combine(_root, "isolierter-papierkorb"));
        using var service = CreateJobService(isolatedBin);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var copy = await CompleteJobAsync(service, new OperationRequest("copy", [source], destination));
            Assert.True(copy.Status == "completed", $"Kopierauftrag fehlgeschlagen: {copy.Error ?? copy.Message}");
            var copied = Path.Combine(destination, Path.GetFileName(source));
            Assert.True(File.Exists(Path.Combine(copied, "Unterordner", "zwei.txt")));

            var undo = await service.UndoLastAsync(CancellationToken.None);
            Assert.NotNull(undo);
            var completedUndo = await WaitForJobAsync(service, undo!.Id);
            Assert.Equal("completed", completedUndo.Status);
            Assert.False(Directory.Exists(copied));
            Assert.True(Directory.Exists(source));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FailedUndoKeepsTheOriginalUndoRecordAvailableForRetry()
    {
        var source = Path.Combine(_root, "Wiederherstellbare Quelle");
        var destination = Path.Combine(_root, "Ziel");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "Datei.txt"), "bleibt erhalten");

        var recycleBin = new TestRecycleBin(Path.Combine(_root, "isolierter-papierkorb"));
        using var service = CreateJobService(recycleBin);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var copy = await CompleteJobAsync(service, new OperationRequest("copy", [source], destination));
            Assert.Equal("completed", copy.Status);
            var copied = Path.Combine(destination, Path.GetFileName(source));
            Assert.True(File.Exists(Path.Combine(copied, "Datei.txt")));

            recycleBin.FailNextDelete = true;
            var failedUndo = await service.UndoLastAsync(CancellationToken.None);
            Assert.NotNull(failedUndo);
            var failed = await WaitForJobAsync(service, failedUndo!.Id);
            Assert.Equal("failed", failed.Status);
            Assert.True(File.Exists(Path.Combine(copied, "Datei.txt")));

            var retriedUndo = await service.UndoLastAsync(CancellationToken.None);
            Assert.NotNull(retriedUndo);
            var completedUndo = await WaitForJobAsync(service, retriedUndo!.Id);
            Assert.Equal("completed", completedUndo.Status);
            Assert.False(Directory.Exists(copied));
            Assert.True(File.Exists(Path.Combine(source, "Datei.txt")));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ConcurrentUndoRequestsReturnTheSamePendingJob()
    {
        var source = Path.Combine(_root, "Doppelte Undo-Quelle");
        var destination = Path.Combine(_root, "Ziel");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "Datei.txt"), "nur einmal rückgängig machen");

        var recycleBin = new TestRecycleBin(Path.Combine(_root, "isolierter-papierkorb"));
        using var service = CreateJobService(recycleBin);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var copy = await CompleteJobAsync(service, new OperationRequest("copy", [source], destination));
            Assert.Equal("completed", copy.Status);
            var copied = Path.Combine(destination, Path.GetFileName(source));
            recycleBin.BlockNextDelete();

            var first = await service.UndoLastAsync(CancellationToken.None);
            Assert.NotNull(first);
            await recycleBin.WaitUntilDeleteIsBlockedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var second = await service.UndoLastAsync(CancellationToken.None);

            Assert.NotNull(second);
            Assert.Equal(first!.Id, second!.Id);
            recycleBin.ReleaseBlockedDelete();
            var completedUndo = await WaitForJobAsync(service, first.Id);
            Assert.Equal("completed", completedUndo.Status);
            Assert.False(Directory.Exists(copied));
        }
        finally
        {
            recycleBin.ReleaseBlockedDelete();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CrossVolumeFolderMovePreservesFilesAddedAfterTheMovePlanAndCanBeUndone()
    {
        var source = Path.Combine(_root, "Verschiebequelle");
        var destination = Path.Combine(_root, "Verschiebeziel");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var plannedFile = Path.Combine(source, "geplant.txt");
        var lateFile = Path.Combine(source, "später-hinzugefügt.txt");
        await File.WriteAllTextAsync(plannedFile, "geplant");

        var injected = false;
        bool DifferentVolumes(string first, string second)
        {
            if (!injected && string.Equals(Path.GetFullPath(first), source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(second), destination, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(lateFile, "kam nach der Planung dazu");
                injected = true;
            }
            return false;
        }

        using var service = CreateJobService(sameVolumeResolver: DifferentVolumes);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var moved = await CompleteJobAsync(service, new OperationRequest("move", [source], destination));
            var copiedFolder = Path.Combine(destination, Path.GetFileName(source));
            Assert.True(injected);
            Assert.Equal("completed-with-skips", moved.Status);
            Assert.Equal("geplant", await File.ReadAllTextAsync(Path.Combine(copiedFolder, "geplant.txt")));
            Assert.Equal("kam nach der Planung dazu", await File.ReadAllTextAsync(lateFile));
            Assert.False(File.Exists(plannedFile));
            Assert.True(Directory.Exists(source));

            var undo = await service.UndoLastAsync(CancellationToken.None);
            Assert.NotNull(undo);
            var undone = await WaitForJobAsync(service, undo!.Id);
            Assert.Equal("completed", undone.Status);
            Assert.Equal("geplant", await File.ReadAllTextAsync(plannedFile));
            Assert.Equal("kam nach der Planung dazu", await File.ReadAllTextAsync(lateFile));
            Assert.False(Directory.Exists(copiedFolder));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CrossVolumeMoveLeavesSourceWhenItChangesAfterTheMovePlan()
    {
        var source = Path.Combine(_root, "GeänderteQuelle");
        var destination = Path.Combine(_root, "ZielFürÄnderung");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var original = Path.Combine(source, "datei.txt");
        await File.WriteAllTextAsync(original, "geplant");

        var injected = false;
        bool DifferentVolumes(string first, string second)
        {
            if (!injected && string.Equals(Path.GetFullPath(first), source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(second), destination, StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(25);
                File.WriteAllText(original, "geändert");
                injected = true;
            }
            return false;
        }

        using var service = CreateJobService(sameVolumeResolver: DifferentVolumes);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var moved = await CompleteJobAsync(service, new OperationRequest("move", [source], destination));
            Assert.True(injected);
            Assert.Equal("failed", moved.Status);
            Assert.Contains("Quelldatei", moved.Error, StringComparison.Ordinal);
            Assert.Equal("geändert", await File.ReadAllTextAsync(original));
            Assert.False(File.Exists(Path.Combine(destination, Path.GetFileName(source), "datei.txt")));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CrossVolumeFolderMoveUndoRestoresEmptyDirectories()
    {
        var source = Path.Combine(_root, "QuelleMitLeerenOrdnern");
        var emptyDirectory = Path.Combine(source, "Unterordner", "Leer");
        var destination = Path.Combine(_root, "ZielMitLeerenOrdnern");
        Directory.CreateDirectory(emptyDirectory);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "datei.txt"), "inhalt");

        using var service = CreateJobService(sameVolumeResolver: static (_, _) => false);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var moved = await CompleteJobAsync(service, new OperationRequest("move", [source], destination));
            var movedFolder = Path.Combine(destination, Path.GetFileName(source));
            Assert.Equal("completed", moved.Status);
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(Path.Combine(movedFolder, "Unterordner", "Leer")));

            var undo = await service.UndoLastAsync(CancellationToken.None);
            Assert.NotNull(undo);
            var undone = await WaitForJobAsync(service, undo!.Id);
            Assert.Equal("completed", undone.Status);
            Assert.Equal("inhalt", await File.ReadAllTextAsync(Path.Combine(source, "datei.txt")));
            Assert.True(Directory.Exists(emptyDirectory));
            Assert.False(Directory.Exists(movedFolder));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task QueuedJobCanBeCancelledBeforeAWorkerStarts()
    {
        using var service = CreateJobService();
        var created = await service.EnqueueAsync(new OperationRequest("new-folder", [], _root, "NichtAnlegen"), CancellationToken.None);

        Assert.True(service.Cancel(created.Id));
        Assert.Equal("cancelled", service.GetJob(created.Id)!.Status);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(50);
            Assert.Equal("cancelled", service.GetJob(created.Id)!.Status);
            Assert.False(Directory.Exists(Path.Combine(_root, "NichtAnlegen")));
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task TenThousandFileCopyReportsProgressAndCancelsWithoutPartialFiles()
    {
        var source = Path.Combine(_root, "10.000 Quelldateien");
        var destination = Path.Combine(_root, "Ziel");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        for (var index = 0; index < 10_000; index++)
            await File.WriteAllTextAsync(Path.Combine(source, $"{index:D5}.txt"), "x");

        using var service = CreateJobService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var created = await service.EnqueueAsync(new OperationRequest("copy", [source], destination), CancellationToken.None);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            var copied = Path.Combine(destination, Path.GetFileName(source));
            JobSnapshot? progressed = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                progressed = service.GetJob(created.Id);
                var hasCompletedFile = Directory.Exists(copied) && Directory
                    .EnumerateFiles(copied, "*", SearchOption.AllDirectories)
                    .Any(path => !Path.GetFileName(path).StartsWith(".wme-", StringComparison.OrdinalIgnoreCase));
                if (hasCompletedFile) break;
                if (progressed is { Status: "failed" }) break;
                await Task.Delay(10);
            }

            Assert.NotNull(progressed);
            Assert.True(progressed.TotalItems.GetValueOrDefault() >= 10_000, progressed.Error);
            Assert.True(progressed.CompletedItems > 0, progressed.Error);
            Assert.True(service.Cancel(created.Id));
            var cancelled = await WaitForJobAsync(service, created.Id, TimeSpan.FromSeconds(45));
            Assert.Equal("cancelled", cancelled.Status);
            Assert.Equal(10_000, Directory.EnumerateFiles(source).Count());
            Assert.True(Directory.Exists(copied));
            Assert.True(Directory.EnumerateFiles(copied).Any());
            var cleanupDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
            string[] partialFiles;
            do
            {
                partialFiles = Directory.EnumerateFiles(copied, ".wme-*.partial", SearchOption.AllDirectories).ToArray();
                if (partialFiles.Length == 0) break;
                await Task.Delay(20);
            } while (DateTimeOffset.UtcNow < cleanupDeadline);
            Assert.Empty(partialFiles);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task TextEditorRejectsStaleWritesAndPropertiesComputeHash()
    {
        var path = Path.Combine(_root, "Änderbarer Text.txt");
        await File.WriteAllTextAsync(path, "erste Version", new UTF8Encoding(false));
        var documents = new TextDocumentService(_policy);
        var document = await documents.ReadAsync(path, CancellationToken.None);
        var saved = await documents.SaveAsync(new TextSaveRequest(path, "zweite Version", document.ETag), CancellationToken.None);
        Assert.Equal("zweite Version", saved.Content);
        await File.WriteAllTextAsync(path, "Änderung in einem anderen Programm", new UTF8Encoding(false));
        await Assert.ThrowsAsync<DocumentConflictException>(() => documents.SaveAsync(
            new TextSaveRequest(path, "veraltete Version", saved.ETag), CancellationToken.None));

        var properties = new FilePropertiesService(_policy);
        var result = await properties.GetAsync(path, "sha256", CancellationToken.None);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), result.Hash);
        Assert.Equal(new FileInfo(path).Length, result.Size);
    }

    [Fact]
    public async Task RecursiveSearchStreamsNestedUnicodeFilenameAndTextMatches()
    {
        var nested = Path.Combine(_root, "Ordner", "Tiefer");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "Notiz ä.txt");
        await File.WriteAllTextAsync(file, "Der gesuchte Inhalt steht hier.", new UTF8Encoding(false));
        var search = new RecursiveSearchService(_policy);
        var found = new List<FileEntryDto>();
        await foreach (var entry in search.SearchAsync(_root, "gesuchte", false, true, ".txt", null, null, null, null,
                           CancellationToken.None))
            found.Add(entry);
        Assert.Contains(found, entry => entry.Path == file);
    }

    [Fact]
    public async Task FolderListingFiltersByAttributesTypeSizeAndDateAndRefreshesItsSnapshot()
    {
        var hiddenFile = Path.Combine(_root, "versteckt.txt");
        var systemFile = Path.Combine(_root, "system.txt");
        var smallFile = Path.Combine(_root, "klein.txt");
        var largeFile = Path.Combine(_root, "groß.txt");
        var oldFile = Path.Combine(_root, "alt.txt");
        var imageFile = Path.Combine(_root, "bild.png");
        await File.WriteAllTextAsync(hiddenFile, "hidden");
        await File.WriteAllTextAsync(systemFile, "system");
        await File.WriteAllTextAsync(smallFile, "ab");
        await File.WriteAllTextAsync(largeFile, "mehr als fünf Bytes");
        await File.WriteAllTextAsync(oldFile, "alt");
        await File.WriteAllTextAsync(imageFile, "png");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        File.SetAttributes(systemFile, FileAttributes.System);
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));

        var listing = new FileExplorerService(_policy);
        try
        {
            var normal = await listing.ListAsync(_root, 0, 500, "name", false, null, false, CancellationToken.None, refresh: true);
            Assert.DoesNotContain(normal.Entries, entry => entry.Name == Path.GetFileName(hiddenFile));
            Assert.DoesNotContain(normal.Entries, entry => entry.Name == Path.GetFileName(systemFile));

            var hiddenVisible = await listing.ListAsync(_root, 0, 500, "name", false, null, true, CancellationToken.None, refresh: true);
            Assert.Contains(hiddenVisible.Entries, entry => entry.Name == Path.GetFileName(hiddenFile));
            Assert.DoesNotContain(hiddenVisible.Entries, entry => entry.Name == Path.GetFileName(systemFile));

            var systemVisible = await listing.ListAsync(_root, 0, 500, "name", false, null, false, CancellationToken.None,
                showSystem: true, refresh: true);
            Assert.Contains(systemVisible.Entries, entry => entry.Name == Path.GetFileName(systemFile));
            Assert.DoesNotContain(systemVisible.Entries, entry => entry.Name == Path.GetFileName(hiddenFile));

            var filtered = await listing.ListAsync(_root, 0, 500, "name", false, null, false, CancellationToken.None,
                extension: "txt", minSize: 5, modifiedAfter: DateTimeOffset.UtcNow.AddDays(-1), refresh: true);
            Assert.Contains(filtered.Entries, entry => entry.Name == Path.GetFileName(largeFile));
            Assert.DoesNotContain(filtered.Entries, entry => entry.Name == Path.GetFileName(smallFile));
            Assert.DoesNotContain(filtered.Entries, entry => entry.Name == Path.GetFileName(oldFile));
            Assert.DoesNotContain(filtered.Entries, entry => entry.Name == Path.GetFileName(imageFile));

            var images = await listing.ListAsync(_root, 0, 500, "name", false, null, false, CancellationToken.None,
                kind: "images", refresh: true);
            Assert.Equal([Path.GetFileName(imageFile)], images.Entries.Select(entry => entry.Name));

            var cached = await listing.ListAsync(_root, 0, 500, "name", false, null, false, CancellationToken.None, refresh: true);
            var newFile = Path.Combine(_root, "nach-snapshot.txt");
            await File.WriteAllTextAsync(newFile, "neu");
            var sameSnapshot = await listing.ListAsync(_root, 0, 500, "name", false, null, false, CancellationToken.None);
            Assert.Equal(cached.TotalCount, sameSnapshot.TotalCount);
            Assert.DoesNotContain(sameSnapshot.Entries, entry => entry.Name == Path.GetFileName(newFile));
            var refreshed = await listing.ListAsync(_root, 0, 500, "name", false, null, false, CancellationToken.None, refresh: true);
            Assert.Contains(refreshed.Entries, entry => entry.Name == Path.GetFileName(newFile));
        }
        finally
        {
            File.SetAttributes(hiddenFile, FileAttributes.Normal);
            File.SetAttributes(systemFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task FiftyThousandEntryFolderReturnsStableVirtualizationPages()
    {
        var folder = Path.Combine(_root, "50.000 Einträge");
        Directory.CreateDirectory(folder);
        for (var index = 0; index < 50_000; index++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"datei-{index:D5}.txt"), "x");

        var listing = new FileExplorerService(_policy);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var first = await listing.ListAsync(folder, 0, 160, "name", false, null, false, CancellationToken.None, refresh: true);
        var firstPageDuration = started.Elapsed;
        var second = await listing.ListAsync(folder, 160, 160, "name", false, null, false, CancellationToken.None);
        _output.WriteLine($"50.000 Einträge: erste Seite {firstPageDuration.TotalMilliseconds:N0} ms, Folgeseite mit Snapshot geladen.");

        Assert.Equal(50_000, first.TotalCount);
        Assert.Equal(160, first.Entries.Count);
        Assert.True(first.HasMore);
        Assert.Equal("datei-00000.txt", first.Entries[0].Name);
        Assert.Equal("datei-00159.txt", first.Entries[^1].Name);
        Assert.Equal("datei-00160.txt", second.Entries[0].Name);
        Assert.Equal(160, second.Entries.Count);
        Assert.True(second.HasMore);
        Assert.True(firstPageDuration < TimeSpan.FromSeconds(30), $"Die erste Seite dauerte {firstPageDuration.TotalSeconds:N1} Sekunden.");
    }

    [Fact]
    public async Task ShellShortcutsResolveUnicodeTargetsAndStayInsideSharedRoots()
    {
        var target = Path.Combine(_root, "Ziel mit Umlaut ä.txt");
        await File.WriteAllTextAsync(target, "Verknüpfungsziel");
        var firstShortcut = Path.Combine(_root, "Erster Link.lnk");
        var chainedShortcut = Path.Combine(_root, "Kettenlink.lnk");
        CreateShortcut(firstShortcut, target);
        CreateShortcut(chainedShortcut, firstShortcut);

        var folderTarget = Path.Combine(_root, "Zielordner");
        Directory.CreateDirectory(folderTarget);
        var folderShortcut = Path.Combine(_root, "Ordnerlink.lnk");
        CreateShortcut(folderShortcut, folderTarget);

        var listing = await new FileExplorerService(_policy).ListAsync(_root, 0, 100, "name", false,
            null, false, CancellationToken.None);
        var listedShortcut = Assert.Single(listing.Entries, entry => entry.Path == chainedShortcut);
        Assert.True(listedShortcut.IsLink);
        Assert.True(listedShortcut.IsShortcut);

        var service = new ShortcutService(_policy);
        var resolvedFile = await service.ResolveAsync(chainedShortcut, CancellationToken.None);
        Assert.Equal(target, resolvedFile.Path);
        Assert.Equal("Ziel mit Umlaut ä.txt", resolvedFile.Name);
        Assert.False(resolvedFile.IsDirectory);

        var resolvedFolder = await service.ResolveAsync(folderShortcut, CancellationToken.None);
        Assert.Equal(folderTarget, resolvedFolder.Path);
        Assert.True(resolvedFolder.IsDirectory);

        var outsideTarget = Path.Combine(Path.GetTempPath(), $"WME-Outside-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(outsideTarget, "nicht freigegeben");
            var outsideShortcut = Path.Combine(_root, "Nicht freigegeben.lnk");
            CreateShortcut(outsideShortcut, outsideTarget);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveAsync(outsideShortcut, CancellationToken.None));
        }
        finally { File.Delete(outsideTarget); }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Die Windows Shell-Verknüpfungsfunktion ist nicht verfügbar.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Die Windows Shell-Verknüpfungsfunktion konnte nicht gestartet werden.");
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            if (shortcut is null) throw new InvalidOperationException("Die Testverknüpfung konnte nicht angelegt werden.");
            shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private ExplorerJobService CreateJobService(IRecycleBinService? recycleBin = null,
        Func<string, string, bool>? sameVolumeResolver = null) => new(_policy,
        recycleBin ?? new TestRecycleBin(Path.Combine(_root, $"isolierter-papierkorb-{Guid.NewGuid():N}")),
        NullLogger<ExplorerJobService>.Instance, Path.Combine(_root, $"jobs-{Guid.NewGuid():N}.json"), sameVolumeResolver);

    private static async Task<JobSnapshot> CompleteJobAsync(ExplorerJobService service, OperationRequest request)
    {
        var created = await service.EnqueueAsync(request, CancellationToken.None);
        return await WaitForJobAsync(service, created.Id);
    }

    private static async Task<JobSnapshot> WaitForJobAsync(ExplorerJobService service, string id, TimeSpan? timeout = null)
    {
        var until = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTimeOffset.UtcNow < until)
        {
            var current = service.GetJob(id)!;
            if (current.Status is "completed" or "completed-with-skips" or "failed" or "cancelled") return current;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Auftrag {id} wurde nicht rechtzeitig abgeschlossen.");
    }

    private sealed class TestRecycleBin(string root) : IRecycleBinService
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);
        private TaskCompletionSource? _deleteBlocked;
        private TaskCompletionSource? _releaseDelete;
        private bool _blockNextDelete;
        public bool FailNextDelete { get; set; }
        public void BlockNextDelete()
        {
            _deleteBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockNextDelete = true;
        }
        public Task WaitUntilDeleteIsBlockedAsync() => _deleteBlocked?.Task ?? throw new InvalidOperationException("No delete is currently blocked.");
        public void ReleaseBlockedDelete() => _releaseDelete?.TrySetResult();
        public async Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_blockNextDelete)
            {
                _blockNextDelete = false;
                _deleteBlocked!.TrySetResult();
                await _releaseDelete!.Task.WaitAsync(cancellationToken);
            }
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new IOException("Injected recycle-bin failure for undo coverage.");
            }
            Directory.CreateDirectory(root);
            var saved = Path.Combine(root, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
            if (Directory.Exists(path)) Directory.Move(path, saved);
            else File.Move(path, saved);
            _items[path] = saved;
        }
        public Task RestoreOriginalPathAsync(string originalPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var saved = _items[originalPath];
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            if (Directory.Exists(saved)) Directory.Move(saved, originalPath);
            else File.Move(saved, originalPath);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<RecycleBinEntryDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RecycleBinEntryDto>>([]);
        public Task RestoreAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeletePermanentlyAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EmptyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
