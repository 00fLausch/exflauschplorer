namespace Explorer.Host.Services;

public sealed class UploadMaintenanceService(UploadSessionService uploads) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do { await uploads.ExpireSessionsAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
