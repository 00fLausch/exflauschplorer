using Explorer.Core.Security;

namespace Explorer.Host.Configuration;

public sealed record ShareRootSetting(string Path, bool ReadOnly, string? Label = null);

public sealed record ExplorerShareSettings(
    bool ShareAllDrives,
    bool ReadOnlyMode,
    bool ProtectSystemFolders,
    IReadOnlyList<ShareRootSetting> Roots,
    bool CanEdit = false);

public sealed record UpdateExplorerShareSettings(
    bool ShareAllDrives,
    bool ReadOnlyMode,
    bool ProtectSystemFolders,
    IReadOnlyList<ShareRootSetting>? Roots);

/// <summary>Persists share settings and atomically updates the policy used by active requests.</summary>
public sealed class ExplorerSettingsService
{
    private readonly ExplorerSettings _settings;
    private readonly PathAccessPolicy _policy;
    private readonly Action<ExplorerSettings> _save;

    public ExplorerSettingsService(
        ExplorerSettings settings,
        PathAccessPolicy policy,
        Action<ExplorerSettings>? save = null)
    {
        _settings = settings;
        _policy = policy;
        _save = save ?? SettingsStore.Save;
    }

    public ExplorerShareSettings Get()
    {
        lock (SettingsStore.SyncRoot)
            return new ExplorerShareSettings(
                _settings.ShareAllDrives,
                _settings.ReadOnlyMode,
                _settings.ProtectSystemFolders,
                _settings.Roots.Select(root => new ShareRootSetting(root.Path, root.ReadOnly, root.Label)).ToArray());
    }

    public ExplorerShareSettings Update(UpdateExplorerShareSettings request)
    {
        var roots = NormalizeRoots(request.Roots);
        if (!request.ShareAllDrives && roots.Count == 0)
            throw new ArgumentException("Wähle mindestens einen freigegebenen Ordner aus, bevor du alle Laufwerke ausblendest.");

        lock (SettingsStore.SyncRoot)
        {
            var updated = new ExplorerSettings
            {
                Port = _settings.Port,
                TailscaleHttpsPort = _settings.TailscaleHttpsPort,
                TailscaleHostName = _settings.TailscaleHostName,
                LocalSetupToken = _settings.LocalSetupToken,
                AllowedLogins = [.. _settings.AllowedLogins],
                ShareAllDrives = request.ShareAllDrives,
                Roots = roots,
                ReadOnlyMode = request.ReadOnlyMode,
                ProtectSystemFolders = request.ProtectSystemFolders,
                ShowHiddenByDefault = _settings.ShowHiddenByDefault,
                ShowSystemByDefault = _settings.ShowSystemByDefault,
                PinHash = _settings.PinHash,
                FavoritesByLogin = (_settings.FavoritesByLogin ?? new Dictionary<string, List<string>>()).ToDictionary(item => item.Key, item => new List<string>(item.Value), StringComparer.OrdinalIgnoreCase)
            };

            var replacementPolicy = SettingsStore.CreatePolicy(updated);
            _save(updated);

            _settings.ShareAllDrives = updated.ShareAllDrives;
            _settings.ReadOnlyMode = updated.ReadOnlyMode;
            _settings.ProtectSystemFolders = updated.ProtectSystemFolders;
            _settings.Roots = updated.Roots;
            _policy.ReplaceRoots(replacementPolicy.Roots);

            return Get();
        }
    }

    private static List<ConfiguredRoot> NormalizeRoots(IReadOnlyList<ShareRootSetting>? requestedRoots)
    {
        if (requestedRoots?.Count > 128)
            throw new ArgumentException("Es können höchstens 128 zusätzliche Ordner freigegeben werden.");

        var roots = new List<ConfiguredRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in requestedRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root.Path))
                throw new ArgumentException("Gib für jeden freizugebenden Ordner einen Windows-Pfad ein.");

            var path = PathAccessPolicy.NormalizeAbsolute(root.Path);
            if (!seen.Add(path))
                throw new ArgumentException($"Der Ordner „{path}“ ist bereits in der Freigabeliste.");

            var label = string.IsNullOrWhiteSpace(root.Label) ? null : root.Label.Trim();
            roots.Add(new ConfiguredRoot { Path = path, ReadOnly = root.ReadOnly, Label = label });
        }

        return roots;
    }
}
