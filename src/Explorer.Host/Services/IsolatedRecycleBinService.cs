using System.Collections.Concurrent;
using Explorer.Core.Models;

namespace Explorer.Host.Services;

/// <summary>Uses a test-owned folder instead of the Windows Recycle Bin for browser E2E runs.</summary>
public sealed class IsolatedRecycleBinService(string root) : IRecycleBinService
{
    private readonly ConcurrentDictionary<string, StoredItem> _items = new(StringComparer.Ordinal);

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originalPath = Path.GetFullPath(path);
        var isDirectory = Directory.Exists(originalPath);
        if (!isDirectory && !File.Exists(originalPath)) throw new FileNotFoundException("Die Datei wurde nicht gefunden.", originalPath);

        var id = Guid.NewGuid().ToString("N");
        var itemDirectory = Path.Combine(root, id);
        Directory.CreateDirectory(itemDirectory);
        var savedPath = Path.Combine(itemDirectory, Path.GetFileName(originalPath));
        long? size = isDirectory ? (long?)null : new FileInfo(originalPath).Length;
        if (isDirectory) Directory.Move(originalPath, savedPath);
        else File.Move(originalPath, savedPath);

        _items[id] = new StoredItem(id, Path.GetFileName(originalPath), originalPath, savedPath, size, DateTimeOffset.UtcNow, isDirectory);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecycleBinEntryDto>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RecycleBinEntryDto> entries = _items.Values
            .OrderByDescending(item => item.DeletedAt)
            .Select(item => item.ToDto())
            .ToArray();
        return Task.FromResult(entries);
    }

    public Task RestoreAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.TryGetValue(id, out var item)) throw new FileNotFoundException("Der Eintrag ist nicht mehr im Papierkorb.");
        Restore(item);
        return Task.CompletedTask;
    }

    public Task RestoreOriginalPathAsync(string originalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(originalPath);
        var item = _items.Values
            .Where(candidate => string.Equals(candidate.OriginalPath, fullPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.DeletedAt)
            .FirstOrDefault() ?? throw new FileNotFoundException("Der Eintrag ist nicht mehr im Papierkorb.");
        Restore(item);
        return Task.CompletedTask;
    }

    public Task DeletePermanentlyAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.TryGetValue(id, out var item)) throw new FileNotFoundException("Der Eintrag ist nicht mehr im Papierkorb.");
        DeleteSaved(item);
        _items.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task EmptyAsync(CancellationToken cancellationToken)
    {
        foreach (var item in _items.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteSaved(item);
            _items.TryRemove(item.Id, out _);
        }
        return Task.CompletedTask;
    }

    private void Restore(StoredItem item)
    {
        if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            throw new IOException("Am ursprünglichen Ort ist bereits ein Element mit diesem Namen vorhanden.");
        var parent = Path.GetDirectoryName(item.OriginalPath) ?? throw new IOException("Der ursprüngliche Ordner ist ungültig.");
        Directory.CreateDirectory(parent);
        if (item.IsDirectory) Directory.Move(item.SavedPath, item.OriginalPath);
        else File.Move(item.SavedPath, item.OriginalPath);
        _items.TryRemove(item.Id, out _);
        TryDeleteEmptyParent(item.SavedPath);
    }

    private static void DeleteSaved(StoredItem item)
    {
        if (item.IsDirectory) Directory.Delete(item.SavedPath, recursive: true);
        else File.Delete(item.SavedPath);
        TryDeleteEmptyParent(item.SavedPath);
    }

    private static void TryDeleteEmptyParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null) return;
        try { Directory.Delete(parent); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record StoredItem(string Id, string Name, string OriginalPath, string SavedPath, long? Size, DateTimeOffset DeletedAt, bool IsDirectory)
    {
        public RecycleBinEntryDto ToDto() => new(Id, Name, OriginalPath, Size, DeletedAt, IsDirectory);
    }
}
