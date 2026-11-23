using System.IO.Compression;
using System.IO.Pipelines;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Configuration;
using Explorer.Host.Logging;
using Explorer.Host.Security;
using Explorer.Host.Services;

namespace Explorer.Host;

public static class ExplorerWebApp
{
    private static readonly JsonSerializerOptions JobEventJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim ArchiveDownloads = new(2, 2);

    public static WebApplication Build(ExplorerSettings settings, string[]? args = null)
        => Build(settings, isolatedTest: false, args, new TailscaleConnectionTrust());

    internal static WebApplication Build(ExplorerSettings settings, bool isolatedTest, string[]? args = null)
        => Build(settings, isolatedTest, args, new TailscaleConnectionTrust());

    internal static WebApplication Build(ExplorerSettings settings, bool isolatedTest, string[]? args,
        ITailscaleConnectionTrust tailscaleConnection)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 18L * 1024 * 1024;
            options.Listen(IPAddress.Loopback, settings.Port);
        });
        builder.Logging.ClearProviders();
        var logDirectory = Path.Combine(SettingsStore.DataDirectory, "logs");
        builder.Logging.AddProvider(new RollingFileLoggerProvider(logDirectory));

        var policy = SettingsStore.CreatePolicy(settings);
        var folderSizes = new FolderSizeService(policy);
        var files = new FileExplorerService(policy, folderSizes: folderSizes);
        var favorites = new ExplorerFavoritesService(settings, policy);
        var drives = new DriveService(policy, settings);
        var knownFolders = new KnownFolderService();
        var shortcuts = new ShortcutService(policy);
        var windowsRecent = new WindowsRecentFileService(shortcuts,
            Environment.GetEnvironmentVariable("WINDOWS_MOBILE_EXPLORER_RECENT_DIR"));
        var quickAccess = new WindowsQuickAccessService(policy, new WindowsShellQuickAccessFolderSource());
        var properties = new FilePropertiesService(policy);
        var everythingSearch = new EverythingCliSearchProvider();
        var search = new RecursiveSearchService(policy, everythingSearch);
        var textDocuments = new TextDocumentService(policy);
        IRecycleBinService recycleBin = isolatedTest
            ? new IsolatedRecycleBinService(Path.Combine(SettingsStore.DataDirectory, "e2e-recycle-bin"))
            : new ShellRecycleBinService();
        var clean = new CleanService(policy, recycleBin: recycleBin);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton(files);
        builder.Services.AddSingleton<AudioWaveformService>();
        builder.Services.AddSingleton(favorites);
        builder.Services.AddSingleton(drives);
        builder.Services.AddSingleton(knownFolders);
        builder.Services.AddSingleton(shortcuts);
        builder.Services.AddSingleton(windowsRecent);
        builder.Services.AddSingleton(quickAccess);
        builder.Services.AddSingleton<ShellThumbnailService>();
        builder.Services.AddSingleton(properties);
        builder.Services.AddSingleton<IEverythingSearchProvider>(everythingSearch);
        builder.Services.AddSingleton(search);
        builder.Services.AddSingleton(textDocuments);
        builder.Services.AddSingleton(recycleBin);
        builder.Services.AddSingleton<IRecycleBinService>(recycleBin);
        builder.Services.AddSingleton<UploadSessionService>();
        builder.Services.AddHostedService<UploadMaintenanceService>();
        builder.Services.AddSingleton<PinProtectionService>();
        builder.Services.AddSingleton<ExplorerLiveStreams>();
        builder.Services.AddSingleton<ExplorerJobService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ExplorerJobService>());
        var trust = new ExplorerRequestTrust(settings, isolatedTest);
        builder.Services.AddSingleton(trust);
        var app = builder.Build();

        app.Map("/api/v1/local-setup", branch => branch.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(context.Request.Method) || context.Request.Path.HasValue ||
                context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != settings.Port ||
                !IPAddress.Loopback.Equals(context.Connection.RemoteIpAddress) ||
                !trust.AllowsLocalSetupToken(context.Request.Headers["X-Explorer-Setup"]))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var activeJobs = context.RequestServices.GetRequiredService<ExplorerJobService>().CountActiveJobs();
            var activeUploads = context.RequestServices.GetRequiredService<UploadSessionService>().PendingCount;
            await context.Response.WriteAsJsonAsync(new { ok = true, activeJobs, activeUploads });
        }));

        app.Use(async (context, next) =>
        {
            if (!trust.AllowsHost(context.Request.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new ApiErrorDto(
                    "Dieser Hostname oder Port ist für den Datei-Explorer nicht freigegeben. Prüfe die Tailscale-Adresse und führe die Einrichtung bei Bedarf erneut aus.",
                    "host_not_allowed"));
                return;
            }
            if (!isolatedTest && !tailscaleConnection.IsTrusted(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new ApiErrorDto(
                    "Die Tailscale-Verbindung konnte nicht verifiziert werden. Stelle sicher, dass Tailscale auf dem PC läuft, und führe die Einrichtung erneut aus.",
                    "tailscale_peer_untrusted"));
                return;
            }

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; font-src 'self' blob: data:; worker-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

            if (IsWriteMethod(context.Request.Method))
            {
                if (context.Request.Headers["X-Explorer-Request"] != "1" ||
                    context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
                    !trust.AllowsOrigin(context.Request.Headers.Origin, context.Request.Host))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new ApiErrorDto("Die Anfrage wurde aus Sicherheitsgründen abgelehnt.", "csrf_blocked"));
                    return;
                }
            }

            await next();
        });
        app.UseMiddleware<TailscaleIdentityMiddleware>();
        app.UseMiddleware<PinProtectionMiddleware>();
        app.UseMiddleware<ExplorerLiveStreams>();
        app.UseDefaultFiles();
        var staticTypes = new FileExtensionContentTypeProvider();
        staticTypes.Mappings[".mjs"] = "text/javascript";
        staticTypes.Mappings[".bcmap"] = "application/octet-stream";
        staticTypes.Mappings[".pfb"] = "application/octet-stream";
        staticTypes.Mappings[".icc"] = "application/octet-stream";
        staticTypes.Mappings[".wasm"] = "application/wasm";
        app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticTypes });

        PinEndpoints.MapRoutes(app);
        app.MapGet("/api/v1/health", (HttpContext context) => Results.Ok(new
        {
            ok = true,
            version = typeof(ExplorerWebApp).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            user = context.Items["Explorer.User"]?.ToString(),
            readOnly = settings.ReadOnlyMode,
            shareAllDrives = settings.ShareAllDrives
        }));
        var shareSettings = new ExplorerSettingsService(settings, policy);
        app.MapGet("/api/v1/settings/shares", () => Results.Ok(shareSettings.Get() with { CanEdit = isolatedTest }));
        app.MapPut("/api/v1/settings/shares", (HttpContext context, UpdateExplorerShareSettings request) =>
        {
            if (!isolatedTest)
                return Results.Json(new ApiErrorDto("Ändere Freigaben lokal am Windows-PC über das App-Symbol im Infobereich.", "local_settings_required"),
                    statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var result = shareSettings.Update(request);
                context.RequestServices.GetRequiredService<ExplorerLiveStreams>().Revoke(all: true);
                app.Logger.LogWarning(
                    "Freigabeeinstellungen von {User} geändert: AlleLaufwerke={ShareAllDrives}, NurLesen={ReadOnlyMode}, SystemordnerSchützen={ProtectSystemFolders}, Ordner={RootCount}",
                    context.Items["Explorer.User"]?.ToString(), result.ShareAllDrives, result.ReadOnlyMode,
                    result.ProtectSystemFolders, result.Roots.Count);
                return Results.Ok(result with { CanEdit = true });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new ApiErrorDto(exception.Message));
            }
        });
        app.MapGet("/api/v1/drives", async (CancellationToken cancellationToken) => Results.Ok(await drives.GetDrivesAsync(cancellationToken)));
        app.MapGet("/api/v1/known-folders", () => Results.Ok(knownFolders.GetFolders()
            .Where(folder => string.IsNullOrWhiteSpace(folder.Path) || policy.IsAllowed(folder.Path))));
        app.MapGet("/api/v1/windows-recent", async (CancellationToken cancellationToken) =>
            Results.Ok(await windowsRecent.GetAsync(cancellationToken)));
        app.MapGet("/api/v1/quick-access", async (CancellationToken cancellationToken) =>
            Results.Ok(await quickAccess.GetAsync(cancellationToken)));
        app.MapGet("/api/v1/favorites", (HttpContext context, ExplorerFavoritesService service) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(service.Get(context.Items["Explorer.User"]?.ToString() ?? ""));
        });
        app.MapPut("/api/v1/favorites", (HttpContext context, UpdateFavoritesRequest request, ExplorerFavoritesService service) =>
        {
            try
            {
                var login = context.Items["Explorer.User"]?.ToString() ?? "";
                var result = service.Update(login, request.Paths);
                app.Logger.LogInformation("Favoriten von {User} aktualisiert: {FavoriteCount} Ordner", login, result.Count);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new ApiErrorDto(exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.BadRequest(new ApiErrorDto(exception.Message));
            }
        });
        app.MapGet("/api/v1/shortcuts/resolve", async (string path, ShortcutService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ResolveAsync(path, cancellationToken)));
        app.MapPost("/api/v1/open", (OpenFileRequest request) =>
        {
            if (!request.Reveal)
                return Results.Json(new ApiErrorDto("Dateien lassen sich vom Handy aus im Windows-Explorer anzeigen, aber nicht starten.", "remote_launch_disabled"),
                    statusCode: StatusCodes.Status403Forbidden);
            var path = policy.Authorize(request.Path, write: false);
            if (!File.Exists(path) && !Directory.Exists(path)) return Results.NotFound(new ApiErrorDto("Die Datei oder der Ordner wurde nicht gefunden."));
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new ProcessStartInfo(explorer) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(File.Exists(path) ? $"/select,{path}" : path);
            _ = Process.Start(start) ?? throw new IOException("Der Windows-Explorer konnte nicht gestartet werden.");
            return Results.NoContent();
        });
        app.MapGet("/api/v1/uploads", (HttpContext context, UploadSessionService uploads) =>
            Results.Ok(uploads.List(context.Items["Explorer.User"]?.ToString() ?? "")));
        app.MapPost("/api/v1/uploads", async (HttpContext context, UploadCreateRequest request, UploadSessionService uploads) =>
        {
            var session = await uploads.CreateAsync(request, context.Items["Explorer.User"]?.ToString() ?? "", context.RequestAborted);
            return Results.Ok(session);
        });
        app.MapPut("/api/v1/uploads/{id}/chunk", async (HttpContext context, string id, long offset, string sha256, UploadSessionService uploads) =>
        {
            if (context.Request.ContentLength is not long length || length < 1 || length > 16L * 1024 * 1024)
                return Results.BadRequest(new ApiErrorDto("Ein Upload-Teil muss zwischen 1 Byte und 16 MB groß sein."));
            var chunk = await uploads.WriteChunkAsync(id, context.Items["Explorer.User"]?.ToString() ?? "", offset,
                (int)length, sha256, context.Request.Body, context.RequestAborted);
            return Results.Ok(chunk);
        });
        app.MapPost("/api/v1/uploads/{id}/commit", async (HttpContext context, string id, UploadCommitRequest request, UploadSessionService uploads) =>
        {
            var result = await uploads.CommitAsync(id, RequestOwner(context), request.ConflictMode, context.RequestAborted, request.ExpectedSha256);
            return Results.Ok(result);
        });
        app.MapDelete("/api/v1/uploads/{id}", async (HttpContext context, string id, UploadSessionService uploads) =>
        {
            await uploads.CancelAsync(id, context.Items["Explorer.User"]?.ToString() ?? "", context.RequestAborted);
            return Results.NoContent();
        });
        app.MapGet("/api/v1/entries", async (HttpContext context, string path, int? skip, int? pageSize, string? sort,
            bool? descending, string? q, bool? showHidden, bool? showSystem, string? extension, long? minSize,
            long? maxSize, DateTimeOffset? modifiedAfter, DateTimeOffset? modifiedBefore, bool? refresh, string? kind,
            CancellationToken cancellationToken) =>
        {
            var page = await files.ListAsync(path, skip ?? 0, pageSize ?? 100, sort ?? "name", descending ?? false,
                q, showHidden ?? settings.ShowHiddenByDefault, cancellationToken,
                showSystem ?? settings.ShowSystemByDefault, extension, minSize, maxSize,
                modifiedAfter, modifiedBefore, refresh ?? false, kind ?? "all");
            context.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(page);
        });
        app.MapGet("/api/v1/folder-sizes", (HttpContext context, string path, bool? refresh) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = folderSizes.GetOrStart(path, refresh ?? false);
            return result is null ? Results.Json(new { status = "processing" }, statusCode: 202) : Results.Ok(new { status = "ready" });
        });
        app.MapGet("/api/v1/clean/scan", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await clean.ScanAsync(context.Items["Explorer.User"]?.ToString() ?? "", cancellationToken));
        });
        app.MapPost("/api/v1/clean/delete", async (HttpContext context, CleanDeleteRequest request) =>
        {
            var result = await clean.DeleteAsync(RequestOwner(context), request, context.RequestAborted);
            app.Logger.LogInformation("Clean-Modus: {Deleted} Dateien gelöscht, {Skipped} übersprungen", result.Deleted, result.Skipped);
            return Results.Ok(result);
        });
        app.MapGet("/api/v1/stream", (HttpContext context, string path, bool? download, FileExplorerService service) =>
        {
            var absolute = service.AuthorizeFile(path);
            if (!File.Exists(absolute)) return Results.NotFound(new ApiErrorDto("Die Datei wurde nicht gefunden."));
            var types = new FileExtensionContentTypeProvider();
            if (!types.TryGetContentType(absolute, out var contentType)) contentType = "application/octet-stream";
            context.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            context.Response.Headers.CacheControl = "no-store";
            var inline = contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                contentType is "application/pdf" or "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/avif" or "image/bmp" or "image/tiff" or "image/heic" or "image/heif";
            // Serve text previews as data even when a document is opened as a top-level URL.
            if (!inline && download != true) contentType = "text/plain; charset=utf-8";
            return Results.File(absolute, contentType, download == true ? Path.GetFileName(absolute) : null,
                enableRangeProcessing: true);
        });
        app.MapGet("/api/v1/waveform", (HttpContext context, string path, AudioWaveformService service) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var waveform = service.GetOrStart(path);
                if (waveform is not null) return Results.Ok(waveform);
                context.Response.Headers.RetryAfter = "2";
                return Results.Json(new { status = "processing" }, statusCode: StatusCodes.Status202Accepted);
            }
            catch (WaveformUnavailableException exception)
            {
                return Results.Json(new ApiErrorDto(exception.Message, "waveform_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidDataException exception)
            {
                return Results.UnprocessableEntity(new ApiErrorDto(exception.Message, "waveform_failed"));
            }
        });
        app.MapGet("/api/v1/thumbnail", async (HttpContext context, string path, int? size, ShellThumbnailService service,
            CancellationToken cancellationToken) =>
        {
            var imagePath = await service.GetThumbnailPathAsync(path, size ?? 320, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(imagePath, "image/png");
        });
        app.MapGet("/api/v1/properties", async (string path, string? hashAlgorithm, FilePropertiesService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(path, hashAlgorithm, cancellationToken)));
        app.MapPost("/api/v1/properties/attributes", async (HttpContext context, SetAttributesRequest request, FilePropertiesService service, CancellationToken cancellationToken) =>
        {
            await service.SetAttributesAsync(request.Path, request.ReadOnly, request.Hidden, request.Archive, cancellationToken);
            app.Logger.LogInformation("Dateiattribute von {User} für {Path} geändert", context.Items["Explorer.User"]?.ToString(), request.Path);
            return Results.NoContent();
        });
        app.MapGet("/api/v1/search", async (HttpContext context, RecursiveSearchService service, string root, string q,
            bool? showHidden, bool? content, string? extension, long? minSize, long? maxSize,
            DateTimeOffset? modifiedAfter, DateTimeOffset? modifiedBefore, bool? showSystem) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            await foreach (var result in service.SearchAsync(root, q, showHidden ?? settings.ShowHiddenByDefault, content ?? false,
                               extension, minSize, maxSize, modifiedAfter, modifiedBefore, context.RequestAborted,
                               showSystem ?? settings.ShowSystemByDefault))
            {
                await context.Response.WriteAsync($"event: result\ndata: {JsonSerializer.Serialize(result, JobEventJsonOptions)}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
            await context.Response.WriteAsync("event: done\ndata: {}\n\n", context.RequestAborted);
        });
        app.MapGet("/api/v1/text", async (string path, TextDocumentService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ReadAsync(path, cancellationToken)));
        app.MapPut("/api/v1/text", async (HttpContext context, TextSaveRequest request, TextDocumentService service, CancellationToken cancellationToken) =>
        {
            var document = await service.SaveAsync(request, cancellationToken);
            app.Logger.LogInformation("Textdatei von {User} gespeichert: {Path}", context.Items["Explorer.User"]?.ToString(), document.Path);
            return Results.Ok(document);
        });
        app.MapGet("/api/v1/jobs", (HttpContext context, ExplorerJobService jobs) => Results.Ok(jobs.GetJobs(RequestOwner(context))));
        app.MapGet("/api/v1/recycle-bin", async (IRecycleBinService service, CancellationToken cancellationToken) =>
            Results.Ok((await service.ListAsync(cancellationToken)).Where(item => policy.IsAllowed(item.OriginalPath))));
        app.MapPost("/api/v1/recycle-bin/{id}/restore", async (HttpContext context, string id, IRecycleBinService service, CancellationToken cancellationToken) =>
        {
            await AuthorizeRecycleEntryAsync(id, policy, service, cancellationToken);
            await service.RestoreAsync(id, cancellationToken);
            app.Logger.LogInformation("Papierkorbeintrag {RecycleBinId} von {User} wiederhergestellt", id, context.Items["Explorer.User"]?.ToString());
            return Results.NoContent();
        });
        app.MapPost("/api/v1/recycle-bin/{id}/delete", async (HttpContext context, string id, IRecycleBinService service, CancellationToken cancellationToken) =>
        {
            await AuthorizeRecycleEntryAsync(id, policy, service, cancellationToken);
            await service.DeletePermanentlyAsync(id, cancellationToken);
            app.Logger.LogWarning("Papierkorbeintrag {RecycleBinId} von {User} endgültig gelöscht", id, context.Items["Explorer.User"]?.ToString());
            return Results.NoContent();
        });
        app.MapPost("/api/v1/recycle-bin/empty", async (HttpContext context, IRecycleBinService service, CancellationToken cancellationToken) =>
        {
            if (settings.ReadOnlyMode) throw new UnauthorizedAccessException("Die Freigaben sind schreibgeschützt.");
            foreach (var item in await service.ListAsync(cancellationToken))
            {
                if (!CanMutateRecycleEntry(policy, item.OriginalPath)) continue;
                try { await service.DeletePermanentlyAsync(item.Id, cancellationToken); }
                catch (FileNotFoundException) { /* Already removed by another request or Windows Explorer. */ }
            }
            app.Logger.LogWarning("Freigegebene Papierkorbeinträge von {User} geleert", context.Items["Explorer.User"]?.ToString());
            return Results.NoContent();
        });
        app.MapGet("/api/v1/jobs/events", async (HttpContext context, ExplorerJobService jobs) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            await foreach (var update in jobs.Watch(context.RequestAborted, RequestOwner(context)))
            {
                await context.Response.WriteAsync($"event: job\ndata: {JsonSerializer.Serialize(update, JobEventJsonOptions)}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });
        app.MapPost("/api/v1/jobs", async (HttpContext context, OperationRequest request, ExplorerJobService jobs, CancellationToken cancellationToken) =>
        {
            var job = await jobs.EnqueueAsync(request, cancellationToken, RequestOwner(context));
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        app.MapPost("/api/v1/jobs/{id}/pause", (HttpContext context, string id, ExplorerJobService jobs) =>
            jobs.Pause(id, RequestOwner(context)) ? Results.Ok(jobs.GetJob(id, RequestOwner(context))) : Results.BadRequest(new ApiErrorDto("Dieser Auftrag kann gerade nicht pausiert werden.")));
        app.MapPost("/api/v1/jobs/{id}/resume", (HttpContext context, string id, ExplorerJobService jobs) =>
            jobs.Resume(id, RequestOwner(context)) ? Results.Ok(jobs.GetJob(id, RequestOwner(context))) : Results.BadRequest(new ApiErrorDto("Dieser Auftrag ist nicht pausiert.")));
        app.MapPost("/api/v1/jobs/{id}/cancel", (HttpContext context, string id, ExplorerJobService jobs) =>
            jobs.Cancel(id, RequestOwner(context)) ? Results.Ok(jobs.GetJob(id, RequestOwner(context))) : Results.BadRequest(new ApiErrorDto("Dieser Auftrag kann nicht mehr abgebrochen werden.")));
        app.MapPost("/api/v1/jobs/{id}/conflict", (HttpContext context, string id, ConflictResolution resolution, ExplorerJobService jobs) =>
            jobs.ResolveConflict(id, resolution, RequestOwner(context)) ? Results.Ok(jobs.GetJob(id, RequestOwner(context))) : Results.BadRequest(new ApiErrorDto("Die Konfliktentscheidung ist nicht mehr aktuell.")));
        app.MapPost("/api/v1/undo", async (HttpContext context, ExplorerJobService jobs, CancellationToken cancellationToken) =>
        {
            var job = await jobs.UndoLastAsync(cancellationToken, RequestOwner(context));
            return job is null ? Results.NotFound(new ApiErrorDto("Für den letzten Vorgang gibt es keine Rückgängig-Aktion.")) : Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        app.MapGet("/api/v1/watch", FolderWatchEndpoint.StreamAsync);
        app.MapGet("/api/v1/archive", StreamArchiveAsync);
        app.MapFallbackToFile("index.html");

        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "API-Anfrage fehlgeschlagen: {Method} {Path}", context.Request.Method, context.Request.Path);
                if (context.Response.HasStarted) { context.Abort(); return; }
                var status = exception switch
                {
                    UnauthorizedAccessException => StatusCodes.Status403Forbidden,
                    DocumentConflictException => StatusCodes.Status409Conflict,
                    UploadConflictException or UploadOffsetException => StatusCodes.Status409Conflict,
                    FileNotFoundException or DirectoryNotFoundException => StatusCodes.Status404NotFound,
                    TimeoutException => StatusCodes.Status504GatewayTimeout,
                    ArgumentException or InvalidDataException => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError
                };
                context.Response.StatusCode = status;
                var error = exception switch
                {
                    UploadConflictException => new ApiErrorDto(exception.Message, "upload_conflict"),
                    UploadOffsetException => new ApiErrorDto(exception.Message, "upload_offset"),
                    _ => new ApiErrorDto(GermanErrors.For(exception))
                };
                await context.Response.WriteAsJsonAsync(error, context.RequestAborted);
            }
        });
        return app;
    }

    private static bool IsWriteMethod(string method) => method is "POST" or "PUT" or "PATCH" or "DELETE";
    private static string RequestOwner(HttpContext context) => (string)context.Items["Explorer.User"]!;

    private static async Task AuthorizeRecycleEntryAsync(string id, PathAccessPolicy policy, IRecycleBinService service, CancellationToken token)
    {
        var entry = (await service.ListAsync(token)).FirstOrDefault(item => item.Id == id && policy.IsAllowed(item.OriginalPath))
            ?? throw new FileNotFoundException("Der Eintrag ist nicht verfügbar.");
        _ = policy.AuthorizeEntryMutation(entry.OriginalPath);
    }

    private static bool CanMutateRecycleEntry(PathAccessPolicy policy, string path)
    {
        try { _ = policy.AuthorizeEntryMutation(path); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException) { return false; }
    }

    private static async Task StreamArchiveAsync(HttpContext context, string path, FileExplorerService service)
    {
        if (!await ArchiveDownloads.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new ApiErrorDto("Es laufen bereits zwei ZIP-Downloads."));
            return;
        }
        try { await StreamArchiveCoreAsync(context, path, service); }
        finally { ArchiveDownloads.Release(); }
    }

    private static async Task StreamArchiveCoreAsync(HttpContext context, string path, FileExplorerService service)
    {
        const int maximumEntries = 10_000;
        const long maximumBytes = 4L * 1024 * 1024 * 1024;
        var folder = service.Policy.Authorize(path);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
        if (string.Equals(folder, Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ein ganzes Laufwerk kann nicht als ZIP heruntergeladen werden.");
        var entries = new List<(string Path, string Name, long Length, bool IsDirectory)>();
        var pending = new Stack<string>();
        pending.Push(folder);
        long totalBytes = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || !service.Policy.IsAllowed(child)) continue;
                var directory = (attributes & FileAttributes.Directory) != 0;
                var relative = Path.GetRelativePath(folder, child).Replace(Path.DirectorySeparatorChar, '/');
                var length = directory ? 0 : new FileInfo(child).Length;
                totalBytes += length;
                if (entries.Count >= maximumEntries || totalBytes > maximumBytes)
                    throw new ArgumentException("Der ZIP-Download ist auf 10.000 Einträge und 4 GB begrenzt.");
                entries.Add((child, directory ? relative + "/" : relative, length, directory));
                if (directory) pending.Push(child);
            }
        }

        var folderName = new DirectoryInfo(folder).Name;
        context.Response.ContentType = "application/zip";
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(folderName + ".zip");
        context.Response.GetTypedHeaders().ContentDisposition = disposition;
        context.Response.Headers.CacheControl = "no-store";

        // ZipArchiveEntry may write synchronously when disposing a compressor.
        // A bounded pipe keeps those writes away from Kestrel and applies backpressure.
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 128 * 1024));
        var producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await WriteArchiveEntriesAsync(pipe.Writer.AsStream(leaveOpen: true), entries, service, transfer.Token);
            }
            catch (Exception exception) { failure = exception; }
            finally { await pipe.Writer.CompleteAsync(failure); }
        });
        try { await pipe.Reader.CopyToAsync(context.Response.Body, transfer.Token); }
        finally
        {
            await transfer.CancelAsync();
            await pipe.Reader.CompleteAsync();
            await producer;
        }
    }

    private static async Task WriteArchiveEntriesAsync(Stream destination,
        IReadOnlyList<(string Path, string Name, long Length, bool IsDirectory)> entries,
        FileExplorerService service, CancellationToken token)
    {
        await using var archive = await ZipArchive.CreateAsync(destination, ZipArchiveMode.Create,
            leaveOpen: true, entryNameEncoding: null, cancellationToken: token);
        var buffer = new byte[128 * 1024];
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var currentPath = service.Policy.Authorize(entry.Path);
            if (!string.Equals(currentPath, entry.Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Ein Eintrag wurde während des ZIP-Downloads verschoben.");
            var zipEntry = archive.CreateEntry(entry.Name, CompressionLevel.Fastest);
            await using var output = await zipEntry.OpenAsync(token);
            if (entry.IsDirectory) continue;
            await using var input = new FileStream(currentPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != entry.Length) throw new IOException("Eine Datei hat sich während des ZIP-Downloads geändert.");
            var remaining = entry.Length;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (read == 0) throw new EndOfStreamException("Eine Datei ist während des ZIP-Downloads kürzer geworden.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                remaining -= read;
            }
        }
    }

    public sealed record OpenFileRequest(string Path, bool Reveal = false);
    public sealed record SetAttributesRequest(string Path, bool? ReadOnly, bool? Hidden, bool? Archive);
}
