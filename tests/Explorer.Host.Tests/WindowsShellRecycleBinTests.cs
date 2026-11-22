using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

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
                        string.Equals(item.OriginalPath, root, StringComparison.OrdinalIgnoreCase) &&
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
