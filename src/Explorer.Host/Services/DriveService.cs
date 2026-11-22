using Explorer.Core.Models;
using Explorer.Core.Security;
using Explorer.Host.Configuration;

namespace Explorer.Host.Services;

public sealed class DriveService(PathAccessPolicy policy, ExplorerSettings settings)
{
    public async Task<IReadOnlyList<DriveInfoDto>> GetDrivesAsync(CancellationToken cancellationToken)
    {
        var tasks = DriveInfo.GetDrives().Select(async drive =>
        {
            var description = Task.Run(() => Describe(drive), CancellationToken.None);
            try { return await description.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (TimeoutException) { return Unavailable(drive); }
        });
        return (await Task.WhenAll(tasks)).Where(result => result is not null).Cast<DriveInfoDto>().ToArray();
    }

    private DriveInfoDto? Describe(DriveInfo drive)
    {
        try
        {
            var path = PathAccessPolicy.NormalizeAbsolute(drive.Name);
            var root = policy.Roots.FirstOrDefault(item =>
                string.Equals(PathAccessPolicy.NormalizeAbsolute(item.Path), path, StringComparison.OrdinalIgnoreCase));
            if (root is null) return null;
            var ready = drive.IsReady;
            return new DriveInfoDto(
                drive.Name,
                ready && !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.VolumeLabel : drive.Name.TrimEnd('\\'),
                drive.DriveType.ToString(),
                ready ? SafeFormat(drive) : "",
                ready ? SafeLong(() => drive.TotalSize) : null,
                ready ? SafeLong(() => drive.AvailableFreeSpace) : null,
                ready,
                settings.ReadOnlyMode || root?.ReadOnly == true);
        }
        catch (Exception) { return Unavailable(drive); }
    }

    private DriveInfoDto Unavailable(DriveInfo drive) => new(drive.Name, drive.Name.TrimEnd('\\'), drive.DriveType.ToString(),
        "", null, null, false, settings.ReadOnlyMode);

    private static string SafeFormat(DriveInfo drive) { try { return drive.DriveFormat; } catch { return ""; } }
    private static long? SafeLong(Func<long> getter) { try { return getter(); } catch { return null; } }
}
