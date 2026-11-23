using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Explorer.Host.Tests;

public sealed class WindowsShellRecycleBinTests
{
    [Fact]
    public void LocalFixedVolumeIsEligibleForWindowsRecycleBin()
    {
        var path = Path.Combine(Path.GetTempPath(), "wme-recycle-check.txt");
        ShellRecycleBinService.ValidateRecyclePath(path, DriveType.Fixed);
    }

    [Theory]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    [InlineData(DriveType.Unknown)]
    [InlineData(DriveType.NoRootDirectory)]
    public void StorageWithoutReliableWindowsRecycleBinIsRejectedBeforeDelete(DriveType driveType)
    {
        var exception = Assert.Throws<IOException>(() =>
            ShellRecycleBinService.ValidateRecyclePath(@"C:\Temp\wme-recycle-check.txt", driveType));

        Assert.StartsWith("Die Datei wurde nicht gelöscht.", exception.Message);
        Assert.Contains("Endgültig löschen", exception.Message);
    }

    [Theory]
    [InlineData(@"\\server\share\wme-recycle-check.txt")]
    [InlineData(@"\\?\UNC\server\share\wme-recycle-check.txt")]
    public void NetworkPathsAreRejectedBeforeDelete(string path)
    {
        var exception = Assert.Throws<IOException>(() =>
            ShellRecycleBinService.ValidateRecyclePath(path, DriveType.Fixed));

        Assert.StartsWith("Die Datei wurde nicht gelöscht.", exception.Message);
        Assert.Contains("Netzwerkpfade", exception.Message);
    }

    [Fact]
    public async Task RecycleIdIsStableWhenTheShellNameHidesTheExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WME-RecycleId-{Guid.NewGuid():N}");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        var originalPath = Path.Combine(root, "Bericht.txt");
        var contentPath = Path.Combine(bin, "$R" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(contentPath, []);
        await File.WriteAllBytesAsync(ShellRecycleBinService.GetRecycleBinMetadataPath(contentPath)!,
            CreateRecycleMetadataV2(originalPath));
        var deletedAt = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var first = new FakeShellItem(contentPath, "Bericht", root, 7, deletedAt, false);
        var second = new FakeShellItem(contentPath, "Bericht", root, 7, deletedAt, false);
        try
        {
            var info = ShellRecycleBinService.ReadRecycleEntryInfo(first);
            var repeated = ShellRecycleBinService.ReadRecycleEntryInfo(second);

            Assert.NotNull(info);
            Assert.NotNull(repeated);
            Assert.Equal(originalPath, info!.OriginalPath);
            Assert.Equal("Bericht.txt", info.Name);
            Assert.Equal(info.Id, repeated!.Id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] CreateRecycleMetadataV2(string originalPath)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write(2L);
        writer.Write(123L);
        writer.Write(DateTime.UtcNow.ToFileTimeUtc());
        var characters = originalPath.Length + 1;
        writer.Write(characters);
        writer.Write(originalPath.ToCharArray());
        writer.Write('\0');
        writer.Flush();
        return stream.ToArray();
    }

    public sealed record FakeShellItem(
        string Path, string Name, string DeletedFrom, long Size, DateTime DateDeleted, bool IsFolder)
    {
        public object ExtendedProperty(string name) => name switch
        {
            "System.Recycle.DeletedFrom" => DeletedFrom,
            "System.Recycle.DateDeleted" => DateDeleted,
            _ => ""
        };
    }

    [Fact]
    public async Task HiddenExtensionUsesTheRealNameFromTheRecycleBinMetadata()
    {
        var previous = SetHideFileExtensions(1);
        var root = Path.Combine(Path.GetTempPath(), $"WME-RecycleHiddenExt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Bericht.txt");
        var contents = $"Hidden extension regression fixture {Guid.NewGuid():N}";
        await File.WriteAllTextAsync(path, contents);
        var service = new ShellRecycleBinService();
        var deleteAttempted = false;
        try
        {
            await service.DeleteAsync(path, CancellationToken.None);
            deleteAttempted = true;
            RecycleBinEntryDto? entry = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            do
            {
                entry = (await service.ListAsync(CancellationToken.None))
                    .SingleOrDefault(item => string.Equals(item.OriginalPath, path, StringComparison.OrdinalIgnoreCase));
                if (entry is not null) break;
                await Task.Delay(100);
            } while (DateTimeOffset.UtcNow < deadline);

            Assert.NotNull(entry);
            Assert.Equal("Bericht.txt", entry!.Name);
            await service.RestoreAsync(entry.Id, CancellationToken.None);
            deleteAttempted = false;
            Assert.Equal(contents, await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (deleteAttempted && !File.Exists(path))
            {
                try { await service.RestoreOriginalPathAsync(path, CancellationToken.None); }
                catch (FileNotFoundException) { }
            }
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            SetHideFileExtensions(previous);
        }
    }

    [Fact]
    public async Task UploadReplaceLeavesTheOriginalInTheWindowsRecycleBinUnderItsRealName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WME-RecycleUploadReplace-{Guid.NewGuid():N}");
        var share = Directory.CreateDirectory(Path.Combine(root, "share")).FullName;
        var target = Path.Combine(share, "Ersetzte-Datei.txt");
        const string owner = "recycle-test@example.invalid";
        await File.WriteAllTextAsync(target, "original");
        var replacement = "replacement"u8.ToArray();
        var recycleBin = new ShellRecycleBinService();
        var uploads = new UploadSessionService(new PathAccessPolicy([new ShareRoot(share)]), recycleBin,
            NullLogger<UploadSessionService>.Instance, Path.Combine(root, "metadata"));
        try
        {
            var session = await uploads.CreateAsync(new UploadCreateRequest("Ersetzte-Datei.txt", replacement.Length, share),
                owner, CancellationToken.None);
            await uploads.WriteChunkAsync(session.Id, owner, 0, replacement.Length,
                Convert.ToHexString(SHA256.HashData(replacement)), new MemoryStream(replacement), CancellationToken.None);
            var result = await uploads.CommitAsync(session.Id, owner, "replace", CancellationToken.None);

            Assert.True(result.Completed);
            Assert.Equal("replacement", await File.ReadAllTextAsync(target));
            var entry = (await recycleBin.ListAsync(CancellationToken.None))
                .SingleOrDefault(item => string.Equals(item.OriginalPath, target, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(entry);
            Assert.Equal("Ersetzte-Datei.txt", entry!.Name);
            await recycleBin.DeletePermanentlyAsync(entry.Id, CancellationToken.None);
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
            var leftover = (await recycleBin.ListAsync(CancellationToken.None))
                .SingleOrDefault(item => string.Equals(item.OriginalPath, target, StringComparison.OrdinalIgnoreCase));
            if (leftover is not null) await recycleBin.DeletePermanentlyAsync(leftover.Id, CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermanentDeleteRemovesTheMatchingRecycleBinMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WME-RecyclePurgeMeta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Metadaten-Prüfdatei.txt");
        var service = new ShellRecycleBinService();
        string? contentPath = null;
        string? metadataPath = null;
        try
        {
            await File.WriteAllTextAsync(path, "A synthetic $I cleanup regression fixture.");
            await service.DeleteAsync(path, CancellationToken.None);
            (contentPath, metadataPath) = FindRecycleBinFiles(path);
            Assert.NotNull(contentPath);
            Assert.NotNull(metadataPath);
            Assert.True(File.Exists(metadataPath));

            var entry = (await service.ListAsync(CancellationToken.None))
                .SingleOrDefault(item => string.Equals(item.OriginalPath, path, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(entry);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await service.DeletePermanentlyAsync(entry!.Id, deadline.Token);

            Assert.False(File.Exists(contentPath));
            Assert.False(File.Exists(metadataPath));
        }
        finally
        {
            if (!File.Exists(path))
            {
                try { await service.RestoreOriginalPathAsync(path, CancellationToken.None); }
                catch (FileNotFoundException) { }
            }
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root);
            if (metadataPath is not null && File.Exists(metadataPath) &&
                string.Equals(ShellRecycleBinService.TryReadRecycleBinOriginalPath(contentPath), path, StringComparison.OrdinalIgnoreCase))
                File.Delete(metadataPath);
        }
    }

    private static (string? ContentPath, string? MetadataPath) FindRecycleBinFiles(string originalPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var drive in DriveInfo.GetDrives().Where(item => item.DriveType == DriveType.Fixed))
        {
            var bin = Path.Combine(drive.Name, "$Recycle.Bin");
            if (!Directory.Exists(bin)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(bin, "$R*", options).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (!string.Equals(ShellRecycleBinService.TryReadRecycleBinOriginalPath(file), originalPath, StringComparison.OrdinalIgnoreCase)) continue;
                return (file, ShellRecycleBinService.GetRecycleBinMetadataPath(file));
            }
        }
        return (null, null);
    }

    [Fact]
    public async Task DeleteListsAndRestoresOnlyTheTestFileThroughWindowsShell()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WME-RecycleBin-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Prüfdatei ä.txt");
        var contents = $"Recycle-Bin-Integrationstest {Guid.NewGuid():N}";
        await File.WriteAllTextAsync(path, contents);

        var service = new ShellRecycleBinService();
        var deleteAttempted = false;
        try
        {
            deleteAttempted = true;
            await service.DeleteAsync(path, CancellationToken.None);

            Assert.False(File.Exists(path));
            RecycleBinEntryDto? entry = null;
            var listDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            do
            {
                entry = (await service.ListAsync(CancellationToken.None))
                    .SingleOrDefault(item =>
                        string.Equals(item.OriginalPath, path, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.Name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
                if (entry is not null) break;
                await Task.Delay(100);
            } while (DateTimeOffset.UtcNow < listDeadline);

            Assert.NotNull(entry);
            Assert.Equal("Prüfdatei ä.txt", entry!.Name);

            await service.RestoreAsync(entry!.Id, CancellationToken.None);
            deleteAttempted = false;

            Assert.True(File.Exists(path));
            Assert.Equal(contents, await File.ReadAllTextAsync(path));

            await service.DeleteAsync(path, CancellationToken.None);
            deleteAttempted = true;
            Assert.False(File.Exists(path));
            await service.RestoreOriginalPathAsync(path, CancellationToken.None);
            deleteAttempted = false;

            Assert.True(File.Exists(path));
            Assert.Equal(contents, await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (deleteAttempted && !File.Exists(path))
            {
                var restoreDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
                do
                {
                    try
                    {
                        await service.RestoreOriginalPathAsync(path, CancellationToken.None);
                        break;
                    }
                    catch (FileNotFoundException) { await Task.Delay(100); }
                    catch (IOException) when (File.Exists(path)) { break; }
                    catch (IOException) { await Task.Delay(100); }
                } while (!File.Exists(path) && DateTimeOffset.UtcNow < restoreDeadline);
            }

            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermanentlyDeleteOneSyntheticEntryFromTheWindowsRecycleBin()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WME-RecyclePurge-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "synthetische-pruefdatei.txt");
        var service = new ShellRecycleBinService();
        try
        {
            await File.WriteAllTextAsync(path, "A synthetic recycle-bin regression fixture.");
            await service.DeleteAsync(path, CancellationToken.None);
            var entry = Assert.Single(await service.ListAsync(CancellationToken.None), item =>
                string.Equals(item.OriginalPath, path, StringComparison.OrdinalIgnoreCase));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await service.DeletePermanentlyAsync(entry.Id, deadline.Token);
            Assert.DoesNotContain(await service.ListAsync(CancellationToken.None), item => item.Id == entry.Id);
        }
        finally
        {
            if (!File.Exists(path))
            {
                try { await service.RestoreOriginalPathAsync(path, CancellationToken.None); }
                catch (FileNotFoundException) { }
            }
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task UndoOfDeleteRestoresTheOriginalWindowsShellRecycleBinItem()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WME-RecycleBin-Undo-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Undo-Prüfdatei.txt");
        var contents = $"Undo-Recycle-Bin-Integrationstest {Guid.NewGuid():N}";
        await File.WriteAllTextAsync(path, contents);

        var recycleBin = new ShellRecycleBinService();
        using var jobs = new ExplorerJobService(
            new PathAccessPolicy([new ShareRoot(root)]),
            recycleBin,
            NullLogger<ExplorerJobService>.Instance,
            Path.Combine(root, "jobs.json"));
        await jobs.StartAsync(CancellationToken.None);
        var deleteAttempted = false;
        try
        {
            deleteAttempted = true;
            var deletedJob = await jobs.EnqueueAsync(
                new OperationRequest("delete", [path]), CancellationToken.None);
            var deleted = await WaitForJobAsync(jobs, deletedJob.Id);
            Assert.Equal("completed", deleted.Status);
            Assert.False(File.Exists(path));

            var undoJob = await jobs.UndoLastAsync(CancellationToken.None);
            Assert.NotNull(undoJob);
            var undone = await WaitForJobAsync(jobs, undoJob!.Id);
            Assert.Equal("completed", undone.Status);
            deleteAttempted = false;

            Assert.True(File.Exists(path));
            Assert.Equal(contents, await File.ReadAllTextAsync(path));
        }
        finally
        {
            await jobs.StopAsync(CancellationToken.None);
            if (deleteAttempted && !File.Exists(path))
            {
                var restoreDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
                do
                {
                    try
                    {
                        await recycleBin.RestoreOriginalPathAsync(path, CancellationToken.None);
                        break;
                    }
                    catch (FileNotFoundException) { await Task.Delay(100); }
                    catch (IOException) when (File.Exists(path)) { break; }
                    catch (IOException) { await Task.Delay(100); }
                } while (!File.Exists(path) && DateTimeOffset.UtcNow < restoreDeadline);
            }

            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int SetHideFileExtensions(int value)
    {
        const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        using var key = Registry.CurrentUser.OpenSubKey(keyName, writable: true)
            ?? throw new InvalidOperationException("Explorer-Einstellungen sind nicht verfügbar.");
        var previous = Convert.ToInt32(key.GetValue("HideFileExt", 0));
        key.SetValue("HideFileExt", value, RegistryValueKind.DWord);
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        return previous;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static async Task<JobSnapshot> WaitForJobAsync(
        ExplorerJobService jobs,
        string id,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = jobs.GetJob(id);
            if (current is { Status: "completed" or "completed-with-skips" or "failed" or "cancelled" })
                return current;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Auftrag {id} wurde nicht rechtzeitig abgeschlossen.");
    }
}
