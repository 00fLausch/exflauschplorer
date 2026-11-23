using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Configuration;
using Explorer.Host.Security;
using Explorer.Host.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Explorer.Host.Tests;

public sealed class SecurityReviewRegressionTests : IDisposable
{
    private const string Owner = "test@example.invalid";
    private readonly string _tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    private readonly string _root;
    private readonly string _share;
    private readonly PathAccessPolicy _policy;

    public SecurityReviewRegressionTests()
    {
        _root = Path.Combine(_tempRoot, $"WME-Safety-{Guid.NewGuid():N}");
        _share = Path.Combine(_root, "share");
        Directory.CreateDirectory(_share);
        _policy = new PathAccessPolicy([new ShareRoot(_share)]);
    }

    [Fact]
    public void ProductionHostAndOriginRequireTheConfiguredHttpsAuthority()
    {
        var trust = new ExplorerRequestTrust(new ExplorerSettings
        {
            TailscaleHostName = "test-device.test-tailnet.ts.net.",
            TailscaleHttpsPort = 8443
        }, isolatedTest: false);
        var configuredHost = new HostString("test-device.test-tailnet.ts.net", 8443);

        Assert.True(trust.AllowsHost(configuredHost));
        Assert.True(trust.AllowsOrigin("https://test-device.test-tailnet.ts.net:8443", configuredHost));
        Assert.False(trust.AllowsHost(new HostString("another-device.test-tailnet.ts.net", 8443)));
        Assert.False(trust.AllowsHost(new HostString("test-device.test-tailnet.ts.net", 443)));
        Assert.False(trust.AllowsOrigin("http://test-device.test-tailnet.ts.net:8443", configuredHost));
        Assert.False(trust.AllowsOrigin("https://test-device.test-tailnet.ts.net:8443/path", configuredHost));
    }

    [Fact]
    public async Task UploadKeepsOnlyTheVerifiedPrefixAfterRestartAndResume()
    {
        var request = new UploadCreateRequest("upload.txt", 8, _share);
        var uploads = CreateUploads();
        var session = await uploads.CreateAsync(request, Owner, CancellationToken.None);
        var prefix = "first"u8.ToArray();
        await WriteChunkAsync(uploads, session.Id, 0, prefix);
        var staging = Path.Combine(_share, $".wme-upload-{session.Id}.partial");
        await File.AppendAllTextAsync(staging, "XY", new UTF8Encoding(false));

        var restarted = CreateUploads();
        Assert.Equal(prefix.Length, Assert.Single(restarted.List(Owner)).Offset);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(staging));
        // The same recovery rule must apply on an explicit resume while the app keeps running.
        await File.AppendAllTextAsync(staging, "XYZ", new UTF8Encoding(false));
        var resumed = await restarted.CreateAsync(request with { ResumeId = session.Id }, Owner, CancellationToken.None);
        Assert.Equal(prefix.Length, resumed.Offset);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(staging));

        await WriteChunkAsync(restarted, session.Id, resumed.Offset, "end"u8.ToArray());
        var committed = await restarted.CommitAsync(session.Id, Owner, null, CancellationToken.None);
        Assert.True(committed.Completed);
        Assert.Equal("firstend", await File.ReadAllTextAsync(Path.Combine(_share, request.FileName)));
    }

    [Fact]
    public async Task UploadReplacePreservesExistingDirectoryAndCanKeepBoth()
    {
        var target = Directory.CreateDirectory(Path.Combine(_share, "Documents")).FullName;
        var marker = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep this");
        var replacement = "new file"u8.ToArray();
        var uploads = CreateUploads();
        var session = await uploads.CreateAsync(new UploadCreateRequest("Documents", replacement.Length, _share),
            Owner, CancellationToken.None);
        await WriteChunkAsync(uploads, session.Id, 0, replacement);

        await Assert.ThrowsAsync<UploadConflictException>(() =>
            uploads.CommitAsync(session.Id, Owner, "replace", CancellationToken.None));
        Assert.Equal("keep this", await File.ReadAllTextAsync(marker));

        var result = await uploads.CommitAsync(session.Id, Owner, "keep-both", CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(Path.Combine(_share, "Documents (2)")));
        Assert.Equal("keep this", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task UploadReplaceMovesOriginalToRecycleBinUnderItsRealName()
    {
        var target = Path.Combine(_share, "report.txt");
        await File.WriteAllTextAsync(target, "original");
        var replacement = "replacement"u8.ToArray();
        var recycleBin = new IsolatedRecycleBinService(Path.Combine(_root, "upload-replace-bin"));
        var uploads = new UploadSessionService(_policy, recycleBin, NullLogger<UploadSessionService>.Instance,
            Path.Combine(_root, "upload-replace-metadata"));
        var session = await uploads.CreateAsync(new UploadCreateRequest("report.txt", replacement.Length, _share),
            Owner, CancellationToken.None);
        await WriteChunkAsync(uploads, session.Id, 0, replacement);

        var result = await uploads.CommitAsync(session.Id, Owner, "replace", CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal("replacement", await File.ReadAllTextAsync(target));
        var entries = await recycleBin.ListAsync(CancellationToken.None);
        var recycled = Assert.Single(entries);
        Assert.Equal("report.txt", recycled.Name);
        Assert.Equal(target, recycled.OriginalPath);
        Assert.True(_policy.IsAllowed(recycled.OriginalPath));
    }

    [Theory]
    [InlineData("report?.txt")]
    [InlineData("report.txt.")]
    [InlineData("report.txt ")]
    [InlineData("NUL.txt")]
    [InlineData("notes.txt:extra")]
    public async Task UploadRejectsInvalidWindowsNamesBeforeStaging(string name)
    {
        var uploads = CreateUploads();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            uploads.CreateAsync(new UploadCreateRequest(name, 1, _share), Owner, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_share));
    }

    [Fact]
    public async Task RenamingMovingAndDeletingJunctionsLeaveTheirTargetUntouched()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target-outside-share")).FullName;
        var marker = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep this");
        var junction = Path.Combine(_share, "junction");
        var renamed = Path.Combine(_share, "renamed");
        var destination = Directory.CreateDirectory(Path.Combine(_share, "destination")).FullName;
        var moved = Path.Combine(destination, "renamed");
        await CreateJunctionAsync(junction, target);

        using var jobs = CreateJobs();
        await jobs.StartAsync(CancellationToken.None);
        try
        {
            var rename = await FinishJobAsync(jobs, new OperationRequest("rename", [junction], NewName: "renamed"));
            Assert.Equal("completed", rename.Status);
            Assert.False(Directory.Exists(junction));
            Assert.Equal(target, new DirectoryInfo(renamed).ResolveLinkTarget(returnFinalTarget: true)?.FullName);

            var move = await FinishJobAsync(jobs, new OperationRequest("move", [renamed], destination));
            Assert.Equal("completed", move.Status);
            Assert.False(Directory.Exists(renamed));
            Assert.Equal(target, new DirectoryInfo(moved).ResolveLinkTarget(returnFinalTarget: true)?.FullName);

            var delete = await FinishJobAsync(jobs, new OperationRequest("delete", [moved], Permanent: true));
            Assert.Equal("completed", delete.Status);
            Assert.False(Directory.Exists(moved));
            Assert.Equal("keep this", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            await jobs.StopAsync(CancellationToken.None);
            foreach (var link in new[] { junction, renamed, moved })
                if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
        }
    }

    [Fact]
    public async Task PermanentDeleteRejectsShareRootAndKeepsItsContents()
    {
        var marker = Path.Combine(_share, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep this");
        using var jobs = CreateJobs();
        await jobs.StartAsync(CancellationToken.None);
        try
        {
            var result = await FinishJobAsync(jobs, new OperationRequest("delete", [_share], Permanent: true));
            Assert.Equal("failed", result.Status);
            Assert.Equal("keep this", await File.ReadAllTextAsync(marker));
        }
        finally { await jobs.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(1252)]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    public async Task TextEditorPreservesOriginalEncodingBomAndWindowsLineEndings(int codePage)
    {
        var encoding = codePage switch
        {
            1252 => CodePagesEncodingProvider.Instance.GetEncoding(1252)!,
            65001 => new UTF8Encoding(true, true),
            1200 => new UnicodeEncoding(false, true, true),
            1201 => new UnicodeEncoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(codePage))
        };
        var path = Path.Combine(_share, "notiz.txt");
        const string original = "Grüße\r\nÖl kostet 3 €\r\n";
        const string edited = "Grüße\r\nÖl kostet 4 €\r\n";
        await File.WriteAllBytesAsync(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(original)]);
        var editor = new TextDocumentService(_policy);
        var document = await editor.ReadAsync(path, CancellationToken.None);
        Assert.Equal(original, document.Content);

        var saved = await editor.SaveAsync(new TextSaveRequest(path, edited.Replace("\r\n", "\n"), document.ETag),
            CancellationToken.None);

        Assert.Equal(edited, saved.Content);
        byte[] expectedBytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(edited)];
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MaintenanceRemovesAbandonedEditPartials()
    {
        var metadata = Path.Combine(_root, "edit-partials");
        var editor = new TextDocumentService(_policy, _root);
        var stale = Path.Combine(_share, ".wme-edit-stale.partial");
        var fresh = Path.Combine(_share, ".wme-edit-fresh.partial");
        var staleRegistry = Path.Combine(metadata, Path.GetFileName(stale) + ".json");
        var freshRegistry = Path.Combine(metadata, Path.GetFileName(fresh) + ".json");
        await File.WriteAllTextAsync(stale, "crash rest");
        await File.WriteAllTextAsync(fresh, "active rest");
        editor.TrackPartial(stale, staleRegistry);
        editor.TrackPartial(fresh, freshRegistry);
        File.SetLastWriteTimeUtc(staleRegistry, DateTime.UtcNow.AddDays(-8));
        try
        {
            var removed = await editor.CleanupAbandonedPartialsAsync(TimeSpan.FromDays(7), CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(stale));
            Assert.False(File.Exists(staleRegistry));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(freshRegistry));
        }
        finally
        {
            if (File.Exists(fresh)) File.Delete(fresh);
            if (File.Exists(freshRegistry)) File.Delete(freshRegistry);
        }
    }

    [Fact]
    public async Task StartupCleanupRemovesAbandonedSevenZipStagingDirectories()
    {
        var orphan = Path.Combine(_share, $".wme-{Guid.NewGuid():N}-{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(orphan);
        File.SetAttributes(orphan, FileAttributes.Hidden);
        await File.WriteAllTextAsync(Path.Combine(orphan, "rest.txt"), "crash rest");
        var decoy = Path.Combine(_share, ".wme-upload-decoy.partial");
        Directory.CreateDirectory(decoy);
        using var jobs = CreateJobs();
        var active = await jobs.EnqueueAsync(new OperationRequest("unzip", ["quelle.7z"], _share, "aktiv"), CancellationToken.None);
        var activeStaging = Path.Combine(_share, $".wme-{active.Id}-{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(activeStaging);
        try
        {
            var removed = jobs.CleanAbandonedStagingDirectories(CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(orphan));
            Assert.True(Directory.Exists(activeStaging));
            Assert.True(Directory.Exists(decoy));
        }
        finally
        {
            if (Directory.Exists(activeStaging)) Directory.Delete(activeStaging, recursive: true);
            if (Directory.Exists(decoy)) Directory.Delete(decoy, recursive: true);
        }
    }

    [Theory]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    public async Task NewEmptyWindowsScriptsUseCrlfLineEndings(string extension)
    {
        var path = Path.Combine(_share, "leeres-skript" + extension);
        await File.WriteAllTextAsync(path, "");
        var editor = new TextDocumentService(_policy);
        var document = await editor.ReadAsync(path, CancellationToken.None);

        await editor.SaveAsync(new TextSaveRequest(path, "echo hallo\necho tschuss\n", document.ETag), CancellationToken.None);

        var saved = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("\n", saved.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\r\n", saved, StringComparison.Ordinal);
    }

    private UploadSessionService CreateUploads() => new(_policy, new IsolatedRecycleBinService(Path.Combine(_root, "bin")),
        NullLogger<UploadSessionService>.Instance, Path.Combine(_root, "upload-metadata"));

    private ExplorerJobService CreateJobs() => new(_policy, new IsolatedRecycleBinService(Path.Combine(_root, "bin")),
        NullLogger<ExplorerJobService>.Instance, Path.Combine(_root, "jobs.json"));

    private static async Task WriteChunkAsync(UploadSessionService uploads, string id, long offset, byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        await uploads.WriteChunkAsync(id, Owner, offset, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            input, CancellationToken.None);
    }

    private static async Task<JobSnapshot> FinishJobAsync(ExplorerJobService jobs, OperationRequest request)
    {
        var queued = await jobs.EnqueueAsync(request, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var current = jobs.GetJob(queued.Id)!;
            if (current.Status is "completed" or "completed-with-skips" or "failed" or "cancelled") return current;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        var command = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path '" +
            link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell.");
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, errors);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root) && Path.GetDirectoryName(_root) == _tempRoot &&
            Path.GetFileName(_root).StartsWith("WME-Safety-", StringComparison.Ordinal) &&
            (File.GetAttributes(_root) & FileAttributes.ReparsePoint) == 0)
            Directory.Delete(_root, recursive: true);
    }
}
