using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Explorer.Core.Security;
using Explorer.Host.Configuration;
using Explorer.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Explorer.Host.Tests;

public sealed class HttpSecurityRegressionTests
{
    [Fact]
    public async Task PdfRangesArePreservedAndHtmlPreviewIsPlainTextWithSandbox()
    {
        await using var host = await TestHost.StartAsync();
        var pdfPath = Path.Combine(host.Share, "document.pdf");
        var htmlPath = Path.Combine(host.Share, "document.html");
        const string html = "<!doctype html><title>Example</title><p>A benign preview.</p>";
        await File.WriteAllBytesAsync(pdfPath, CreatePdf());
        await File.WriteAllTextAsync(htmlPath, html);

        using var range = new HttpRequestMessage(HttpMethod.Get, FileUrl("stream", pdfPath));
        range.Headers.Range = new RangeHeaderValue(0, 7);
        using var pdfResponse = await host.Client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, pdfResponse.StatusCode);
        Assert.Equal("application/pdf", pdfResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF-1.4", await pdfResponse.Content.ReadAsStringAsync());
        AssertSandboxWithoutCache(pdfResponse);

        using var htmlResponse = await host.Client.GetAsync(FileUrl("stream", htmlPath));
        Assert.Equal(HttpStatusCode.OK, htmlResponse.StatusCode);
        Assert.Equal("text/plain", htmlResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(html, await htmlResponse.Content.ReadAsStringAsync());
        AssertSandboxWithoutCache(htmlResponse);
    }

    [Fact]
    public async Task ZipDownloadIsValidAndIncludesAnEmptyFolder()
    {
        await using var host = await TestHost.StartAsync();
        var folder = Directory.CreateDirectory(Path.Combine(host.Share, "Ordner mit Grüße")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        await File.WriteAllTextAsync(Path.Combine(folder, "note.txt"), "synthetic check");

        using var response = await GetZipResponseAsync(host, folder);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        using var bytes = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("empty/"));
        var note = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("note.txt"));
        using var reader = new StreamReader(note.Open());
        Assert.Equal("synthetic check", await reader.ReadToEndAsync());
    }

    private static async Task<HttpResponseMessage> GetZipResponseAsync(TestHost host, string folder)
    {
        try { return await host.Client.GetAsync(FileUrl("archive", folder)); }
        catch (HttpRequestException exception)
        {
            throw new IOException("The ZIP stream failed. Server log: " + host.ReadRecentLog(), exception);
        }
    }

    [Fact]
    public async Task ZipDownloadRejectsWholeDriveBeforeStreamingAnArchive()
    {
        await using var host = await TestHost.StartAsync();
        using var drive = TemporaryDrive.Map(host.Share);
        var policy = host.App.Services.GetRequiredService<PathAccessPolicy>();
        policy.ReplaceRoots([new ShareRoot(drive.Root, ReadOnly: true)]);
        Assert.Equal(drive.Root, policy.Authorize(drive.Root));

        using var response = await host.Client.GetAsync(FileUrl("archive", drive.Root));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Ein ganzes Laufwerk", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FolderWatchWorksWhenNoPinIsConfigured()
    {
        await using var host = await TestHost.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var open = await host.Client.GetAsync(FileUrl("watch", host.Share),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal("text/event-stream", open.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await open.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal("event: ready", await reader.ReadLineAsync(timeout.Token));
    }

    [Fact]
    public async Task LockingThePinDisconnectsAnOpenFolderWatch()
    {
        await using var host = await TestHost.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var configure = WriteRequest(host, "/api/v1/security/pin/configure");
        configure.Content = JsonContent.Create(new { pin = "421709" });
        using var configured = await host.Client.SendAsync(configure, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        // The test transport is plain loopback. Use the Secure cookie manually.
        var cookie = Assert.Single(configured.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var watch = new HttpRequestMessage(HttpMethod.Get, FileUrl("watch", host.Share));
        watch.Headers.Add("Cookie", cookie);
        using var open = await host.Client.SendAsync(watch, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal("text/event-stream", open.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await open.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal("event: ready", await reader.ReadLineAsync(timeout.Token));
        Assert.StartsWith("data: ", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("", await reader.ReadLineAsync(timeout.Token));

        using var lockRequest = WriteRequest(host, "/api/v1/security/pin/lock");
        lockRequest.Headers.Add("Cookie", cookie);
        using var locked = await host.Client.SendAsync(lockRequest, timeout.Token);
        Assert.Equal(HttpStatusCode.NoContent, locked.StatusCode);

        try { Assert.Null(await reader.ReadLineAsync(timeout.Token)); }
        catch (IOException) { /* Kestrel may reset an aborted HTTP/1 connection instead of sending EOF. */ }
    }

    [Fact]
    public async Task ProductionModeAcceptsTheConfiguredTailscaleHost()
    {
        await using var server = await TestHost.StartAsync(false, new FakeTailscaleTrust(true));
        using var request = ProductionRequest(HttpMethod.Get, "/api/v1/health", "https://test.ts.net");

        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("falsch.ts.net", "test.ts.net")]
    [InlineData("test.ts.net:8443", "test.ts.net:8443")]
    public async Task ProductionModeRejectsWrongHostOrPort(string sentHost, string expectedHost)
    {
        await using var server = await TestHost.StartAsync(false, new FakeTailscaleTrust(true));
        using var request = ProductionRequest(HttpMethod.Get, "/api/v1/health", "https://" + expectedHost);
        request.Headers.Host = sentHost;

        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertApiErrorAsync(response, "host_not_allowed", expectSetupHint: true);
    }

    [Fact]
    public async Task ProductionModeRejectsUntrustedLoopbackConnections()
    {
        await using var server = await TestHost.StartAsync(false, new FakeTailscaleTrust(false));
        using var request = ProductionRequest(HttpMethod.Get, "/api/v1/health", "https://test.ts.net");

        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertApiErrorAsync(response, "tailscale_peer_untrusted", expectSetupHint: true);
    }

    [Fact]
    public async Task ProductionModeRejectsWriteRequestsWithForeignOrigins()
    {
        await using var server = await TestHost.StartAsync(false, new FakeTailscaleTrust(true));
        using var request = ProductionRequest(HttpMethod.Post, "/api/v1/recycle-bin/empty", "https://evil.example");

        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertApiErrorAsync(response, "csrf_blocked");
    }

    private static HttpRequestMessage ProductionRequest(HttpMethod method, string path, string origin)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Host = "test.ts.net";
        request.Headers.Add("Tailscale-User-Login", "test@example.invalid");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Explorer-Request", "1");
        return request;
    }

    private static async Task AssertApiErrorAsync(HttpResponseMessage response, string code, bool expectSetupHint = false)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        if (expectSetupHint)
            Assert.Contains("Einrichtung", document.RootElement.GetProperty("message").GetString());
    }

    private sealed class FakeTailscaleTrust(bool trusted) : ITailscaleConnectionTrust
    {
        public bool IsTrusted(HttpContext context) => trusted;
    }

    private static void AssertSandboxWithoutCache(HttpResponseMessage response)
    {
        Assert.Equal("sandbox; default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static HttpRequestMessage WriteRequest(TestHost host, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-Explorer-Request", "1");
        return request;
    }

    private static string FileUrl(string endpoint, string path) => $"/api/v1/{endpoint}?path={Uri.EscapeDataString(path)}";

    private static byte[] CreatePdf()
    {
        var document = new StringBuilder("%PDF-1.4\n");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"
        ];
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(document.Length);
            document.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = document.Length;
        document.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            document.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        document.Append($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    private sealed class TestHost(WebApplication app, HttpClient client, string root, string share, string? previousDataDirectory)
        : IAsyncDisposable
    {
        private const string DataDirectoryVariable = "WINDOWS_MOBILE_EXPLORER_DATA_DIR";
        public HttpClient Client { get; } = client;
        public WebApplication App { get; } = app;
        public string Share { get; } = share;

        public string ReadRecentLog()
        {
            var logs = Path.Combine(root, "profile", "logs");
            if (!Directory.Exists(logs)) return "(none)";
            foreach (var line in Directory.EnumerateFiles(logs, "app-*.jsonl").SelectMany(File.ReadLines).Reverse())
            {
                using var entry = JsonDocument.Parse(line);
                if (entry.RootElement.GetProperty("Exception").GetString() is { } exception) return exception;
            }
            return "(none)";
        }

        public static async Task<TestHost> StartAsync(bool isolatedTest = true, ITailscaleConnectionTrust? trust = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"WME-HTTP-Safety-{Guid.NewGuid():N}");
            var share = Directory.CreateDirectory(Path.Combine(root, "share")).FullName;
            var previous = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            Environment.SetEnvironmentVariable(DataDirectoryVariable, Path.Combine(root, "profile"));
            WebApplication? app = null;
            try
            {
                var settings = new ExplorerSettings
                {
                    Port = 0,
                    ShareAllDrives = false,
                    Roots = [new ConfiguredRoot { Path = share }],
                    TailscaleHostName = isolatedTest ? null : "test.ts.net",
                    TailscaleHttpsPort = 443,
                    AllowedLogins = isolatedTest ? [] : ["test@example.invalid"]
                };
                app = trust is null
                    ? ExplorerWebApp.Build(settings, isolatedTest)
                    : ExplorerWebApp.Build(settings, isolatedTest, args: null, trust);
                await app.StartAsync();
                var address = new Uri(Assert.Single(app.Urls));
                settings.Port = address.Port;
                var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, UseProxy = false })
                {
                    BaseAddress = address,
                    Timeout = TimeSpan.FromSeconds(8)
                };
                return new TestHost(app, client, root, share, previous);
            }
            catch
            {
                try { if (app is not null) await app.DisposeAsync(); }
                finally { RestoreProfile(root, previous); }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try { await App.DisposeAsync(); }
            finally { RestoreProfile(root, previousDataDirectory); }
        }

        private static void RestoreProfile(string root, string? previous)
        {
            Environment.SetEnvironmentVariable(DataDirectoryVariable, previous);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Directory.Exists(root) && string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("WME-HTTP-Safety-", StringComparison.Ordinal) &&
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TemporaryDrive(string drive, string marker) : IDisposable
    {
        public string Root => drive + "\\";

        public static TemporaryDrive Map(string target)
        {
            var roots = DriveInfo.GetDrives().Select(info => info.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var letter = Enumerable.Range('R', 'Z' - 'R' + 1).Reverse()
                .Select(value => $"{(char)value}:")
                .FirstOrDefault(candidate => !roots.Contains(candidate + "\\"))
                ?? throw new InvalidOperationException("No free drive letter is available for the isolated fixture.");
            var marker = $"wme-mapped-drive-{Guid.NewGuid():N}.txt";
            File.WriteAllText(Path.Combine(target, marker), marker);
            RunSubst(letter, target);
            var mapping = new TemporaryDrive(letter, marker);
            Assert.Equal(marker, File.ReadAllText(Path.Combine(mapping.Root, marker)));
            return mapping;
        }

        public void Dispose()
        {
            var path = Path.Combine(Root, marker);
            if (File.Exists(path) && File.ReadAllText(path) == marker) RunSubst(drive, "/d");
        }

        private static void RunSubst(string drive, string argument)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "subst.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(drive);
            start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start subst.exe.");
            Assert.True(process.WaitForExit(5_000), "subst.exe did not finish.");
            var errors = process.StandardError.ReadToEnd();
            Assert.True(process.ExitCode == 0, errors);
        }
    }
}
