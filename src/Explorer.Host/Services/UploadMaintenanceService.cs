namespace Explorer.Host.Services;

public sealed class UploadMaintenanceService(UploadSessionService uploads, TextDocumentService textDocuments) : BackgroundService
{
    private static readonly TimeSpan PartialLifetime = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            await uploads.ExpireSessionsAsync(stoppingToken);
            await textDocuments.CleanupAbandonedPartialsAsync(PartialLifetime, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
