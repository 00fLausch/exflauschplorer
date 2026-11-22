using Explorer.Core.Security;
using Explorer.Host.Configuration;

namespace Explorer.Host.Services;

public sealed record UpdateFavoritesRequest(IReadOnlyList<string>? Paths);

/// <summary>Stores folder favorites on the PC, scoped to the authenticated Tailscale login.</summary>
public sealed class ExplorerFavoritesService
{
    public const int MaximumFavorites = ExplorerSettings.MaximumFavoriteFoldersPerLogin;

    private readonly ExplorerSettings _settings;
    private readonly PathAccessPolicy _policy;
    private readonly Action<ExplorerSettings> _save;

    public ExplorerFavoritesService(ExplorerSettings settings, PathAccessPolicy policy, Action<ExplorerSettings>? save = null)
    {
        _settings = settings;
        _policy = policy;
        _save = save ?? SettingsStore.Save;
        _settings.FavoritesByLogin ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Get(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) return [];
        lock (SettingsStore.SyncRoot)
        {
            var key = FindLoginKey(login);
            if (key is null || !_settings.FavoritesByLogin.TryGetValue(key, out var paths)) return [];
            return paths
                .Where(path => _policy.IsAllowed(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumFavorites)
                .ToArray();
        }
    }

    public IReadOnlyList<string> Update(string login, IReadOnlyList<string>? requestedPaths)
    {
        if (string.IsNullOrWhiteSpace(login)) throw new ArgumentException("Das Tailscale-Konto fehlt.", nameof(login));
        if (requestedPaths is null) throw new ArgumentException("Die Favoritenliste fehlt.", nameof(requestedPaths));
        if (requestedPaths.Count > MaximumFavorites)
            throw new ArgumentException($"Es können höchstens {MaximumFavorites} Ordner als Favoriten gespeichert werden.");

        var paths = new List<string>(requestedPaths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requestedPath in requestedPaths)
        {
            var path = _policy.Authorize(requestedPath, resolveLinks: false);
            if (!Directory.Exists(path))
                throw new ArgumentException($"Der Favorit „{path}“ ist kein erreichbarer Ordner.");
            if (seen.Add(path)) paths.Add(path);
        }

        lock (SettingsStore.SyncRoot)
        {
            var key = FindLoginKey(login) ?? login.Trim();
            var updatedFavorites = _settings.FavoritesByLogin.ToDictionary(
                item => item.Key,
                item => new List<string>(item.Value),
                StringComparer.OrdinalIgnoreCase);
            updatedFavorites[key] = paths;

            var updated = new ExplorerSettings
            {
                Port = _settings.Port,
                TailscaleHttpsPort = _settings.TailscaleHttpsPort,
                AllowedLogins = [.. _settings.AllowedLogins],
                ShareAllDrives = _settings.ShareAllDrives,
                Roots = [.. _settings.Roots.Select(root => new ConfiguredRoot { Path = root.Path, ReadOnly = root.ReadOnly, Label = root.Label })],
                ReadOnlyMode = _settings.ReadOnlyMode,
                ProtectSystemFolders = _settings.ProtectSystemFolders,
                ShowHiddenByDefault = _settings.ShowHiddenByDefault,
                ShowSystemByDefault = _settings.ShowSystemByDefault,
                PinHash = _settings.PinHash,
                FavoritesByLogin = updatedFavorites
            };

            _save(updated);
            _settings.FavoritesByLogin = updatedFavorites;
            return paths;
        }
    }

    private string? FindLoginKey(string login) => _settings.FavoritesByLogin.Keys
        .FirstOrDefault(key => key.Equals(login.Trim(), StringComparison.OrdinalIgnoreCase));
}
