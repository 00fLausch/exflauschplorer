using Explorer.Core.Models;

namespace Explorer.Host.Services;

public interface IRecycleBinService
{
    Task DeleteAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecycleBinEntryDto>> ListAsync(CancellationToken cancellationToken);
    Task RestoreAsync(string id, CancellationToken cancellationToken);
    Task RestoreOriginalPathAsync(string originalPath, CancellationToken cancellationToken);
    Task DeletePermanentlyAsync(string id, CancellationToken cancellationToken);
    Task EmptyAsync(CancellationToken cancellationToken);
}
