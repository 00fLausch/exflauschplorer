using System.Text.Json;
using Explorer.Core.Security;

namespace Explorer.Host.Configuration;

public sealed class ExplorerSettings
{
    public const int MaximumFavoriteFoldersPerLogin = 100;

    public int Port { get; set; } = 5180;
    public int TailscaleHttpsPort { get; set; } = 8443;
    public string? TailscaleHostName { get; set; }
    public string? LocalSetupToken { get; set; }
    public List<string> AllowedLogins { get; set; } = [];
    public bool ShareAllDrives { get; set; }
    public List<ConfiguredRoot> Roots { get; set; } = [];
    public bool ReadOnlyMode { get; set; }
    public bool ProtectSystemFolders { get; set; } = true;
    public bool ShowHiddenByDefault { get; set; }
    public bool ShowSystemByDefault { get; set; }
    public string? PinHash { get; set; }
    public Dictionary<string, List<string>> FavoritesByLogin { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ConfiguredRoot
{
    public string Path { get; set; } = "";
    public bool ReadOnly { get; set; }
    public string? Label { get; set; }
}

public static class SettingsStore
{
    public static object SyncRoot { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DataDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("WINDOWS_MOBILE_EXPLORER_DATA_DIR");
            return !string.IsNullOrWhiteSpace(overridePath)
                ? Path.GetFullPath(overridePath)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsMobileExplorer");
        }
    }

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static ExplorerSettings Load()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(SettingsPath))
        {
            var initial = new ExplorerSettings();
            Save(initial);
            return initial;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<ExplorerSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                           ?? new ExplorerSettings();
            settings.Port = Math.Clamp(settings.Port, 1024, 65535);
            settings.TailscaleHttpsPort = Math.Clamp(settings.TailscaleHttpsPort, 1, 65535);
            settings.AllowedLogins = settings.AllowedLogins
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var favorites = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (login, paths) in settings.FavoritesByLogin ?? [])
            {
                if (string.IsNullOrWhiteSpace(login) || paths is null || favorites.ContainsKey(login.Trim())) continue;
                favorites.Add(login.Trim(), paths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(ExplorerSettings.MaximumFavoriteFoldersPerLogin)
                    .ToList());
            }
            settings.FavoritesByLogin = favorites;
            return settings;
        }
        catch (JsonException)
        {
            var backup = SettingsPath + ".invalid-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(SettingsPath, backup, overwrite: false);
            var defaults = new ExplorerSettings();
            Save(defaults);
            return defaults;
        }
    }

    public static void Save(ExplorerSettings settings)
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(DataDirectory);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
    }

    public static PathAccessPolicy CreatePolicy(ExplorerSettings settings)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var blocked = new List<string> { DataDirectory };
        if (!string.IsNullOrEmpty(profile))
            blocked.AddRange([Path.Combine(profile, ".ssh"), Path.Combine(profile, ".gnupg")]);
        if (!string.IsNullOrEmpty(local))
            blocked.AddRange([
                Path.Combine(local, "Programs", "WindowsMobileExplorer"),
                Path.Combine(local, "Google", "Chrome", "User Data"),
                Path.Combine(local, "Microsoft", "Edge", "User Data"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
                Path.Combine(local, "Vivaldi", "User Data"),
                Path.Combine(local, "Microsoft", "WindowsApps")]);
        if (!string.IsNullOrEmpty(roaming))
            blocked.AddRange([Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"),
                Path.Combine(roaming, "Opera Software")]);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrEmpty(documents))
            blocked.AddRange([Path.Combine(documents, "WindowsPowerShell"), Path.Combine(documents, "PowerShell")]);
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup,
                     Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu
                 })
        {
            var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrEmpty(path)) blocked.Add(path);
        }
        foreach (var drive in DriveInfo.GetDrives())
            blocked.AddRange([Path.Combine(drive.Name, "$Recycle.Bin"), Path.Combine(drive.Name, "System Volume Information")]);
        return new PathAccessPolicy(CreateRoots(settings), blocked);
    }

    public static IReadOnlyList<ShareRoot> CreateRoots(ExplorerSettings settings)
    {
        var roots = new List<ShareRoot>();
        if (settings.ShareAllDrives)
        {
            foreach (var drive in DriveInfo.GetDrives())
                roots.Add(new ShareRoot(drive.Name, settings.ReadOnlyMode));
        }

        roots.AddRange(settings.Roots
            .Where(root => !string.IsNullOrWhiteSpace(root.Path))
            .Select(root => new ShareRoot(root.Path, settings.ReadOnlyMode || root.ReadOnly, root.Label)));

        if (roots.Count == 0)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile)) roots.Add(new ShareRoot(profile, settings.ReadOnlyMode, "Benutzerordner"));
        }

        if (settings.ProtectSystemFolders)
            ProtectSystemFolders(roots);

        return roots;
    }

    private static void ProtectSystemFolders(List<ShareRoot> roots)
    {
        var protectedFolders = new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Windows (schreibgeschützt)"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Programme (schreibgeschützt)"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Programme x86 (schreibgeschützt)")
            }
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Item1))
            .Select(folder => (Path: PathAccessPolicy.NormalizeAbsolute(folder.Item1!), Label: folder.Item2))
            .DistinctBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < roots.Count; index++)
        {
            var rootPath = PathAccessPolicy.NormalizeAbsolute(roots[index].Path);
            var protectedFolder = protectedFolders.FirstOrDefault(folder => IsWithin(folder.Path, rootPath));
            if (protectedFolder.Path is not null)
                roots[index] = roots[index] with { ReadOnly = true, Label = roots[index].Label ?? protectedFolder.Label };
        }

        foreach (var protectedFolder in protectedFolders)
        {
            if (roots.Any(root => IsWithin(PathAccessPolicy.NormalizeAbsolute(root.Path), protectedFolder.Path)) &&
                !roots.Any(root => string.Equals(PathAccessPolicy.NormalizeAbsolute(root.Path), protectedFolder.Path, StringComparison.OrdinalIgnoreCase)))
                roots.Add(new ShareRoot(protectedFolder.Path, true, protectedFolder.Label));
        }
    }

    private static bool IsWithin(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
