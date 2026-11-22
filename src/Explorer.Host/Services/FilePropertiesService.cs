using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Explorer.Core.Models;
using Explorer.Core.Security;

namespace Explorer.Host.Services;

public sealed class FilePropertiesService(
    PathAccessPolicy policy,
    TimeSpan? directorySizeTimeout = null,
    Func<string, IEnumerable<string>>? enumerateEntries = null)
{
    private static readonly TimeSpan DefaultDirectorySizeTimeout = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectorySizeGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _directorySizeTimeout = ValidateTimeout(directorySizeTimeout);
    private readonly Func<string, IEnumerable<string>> _enumerateEntries = enumerateEntries ??
        (folder => Directory.EnumerateFileSystemEntries(folder));

    public Task SetAttributesAsync(string rawPath, bool? readOnly, bool? hidden, bool? archive, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = policy.Authorize(rawPath, write: true);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException();
        var attributes = File.GetAttributes(path);
        attributes = SetFlag(attributes, FileAttributes.ReadOnly, readOnly);
        attributes = SetFlag(attributes, FileAttributes.Hidden, hidden);
        attributes = SetFlag(attributes, FileAttributes.Archive, archive);
        File.SetAttributes(path, attributes);
    }, cancellationToken);

    public async Task<FilePropertiesDto> GetAsync(string rawPath, string? hashAlgorithm, CancellationToken cancellationToken)
    {
        var properties = await Task.Run(() => Get(rawPath, hashAlgorithm, cancellationToken), cancellationToken);
        if (!properties.IsDirectory) return properties;
        var size = await GetDirectorySizeAsync(properties.Path, cancellationToken);
        return properties with { Size = size };
    }

    private FilePropertiesDto Get(string rawPath, string? hashAlgorithm, CancellationToken cancellationToken)
    {
        var path = policy.Authorize(rawPath);
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path)) throw new FileNotFoundException();
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        var attributes = File.GetAttributes(path);
        long? size = isDirectory ? null : ((FileInfo)info).Length;
        var flags = new List<string>();
        if ((attributes & FileAttributes.ReadOnly) != 0) flags.Add("Schreibgeschützt");
        if ((attributes & FileAttributes.Hidden) != 0) flags.Add("Versteckt");
        if ((attributes & FileAttributes.System) != 0) flags.Add("System");
        if ((attributes & FileAttributes.Archive) != 0) flags.Add("Archiv");
        if ((attributes & FileAttributes.ReparsePoint) != 0) flags.Add("Verknüpfung oder Cloud-Datei");
        if (flags.Count == 0) flags.Add("Standard");

        string? owner = null;
        var permissions = new List<string>();
        try
        {
            var sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity security = isDirectory
                ? FileSystemAclExtensions.GetAccessControl((DirectoryInfo)info, sections)
                : FileSystemAclExtensions.GetAccessControl((FileInfo)info, sections);
            owner = security.GetOwner(typeof(NTAccount))?.Value;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(NTAccount)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var account = rule.IdentityReference is NTAccount nt ? nt.Value : rule.IdentityReference.Translate(typeof(NTAccount)).Value;
                var access = (rule.FileSystemRights & FileSystemRights.Write) != 0 ? "Schreiben" : "Lesen";
                var effect = rule.AccessControlType == AccessControlType.Deny ? "verweigert" : "erlaubt";
                permissions.Add($"{account}: {access} ({effect}{(rule.IsInherited ? ", geerbt" : "")})");
                if (permissions.Count >= 40) break;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SystemException) { }

        string? version = null;
        string? company = null;
        if (!isDirectory && Path.GetExtension(path) is var extension && (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(path);
            version = string.IsNullOrWhiteSpace(versionInfo.FileVersion) ? null : versionInfo.FileVersion;
            company = string.IsNullOrWhiteSpace(versionInfo.CompanyName) ? null : versionInfo.CompanyName;
        }

        var cloudBits = attributes & (FileAttributes)0x00440000;
        var availability = (cloudBits & (FileAttributes)0x00440000) != 0 ? "Nur online oder Abruf bei Zugriff" :
            (attributes & (FileAttributes)0x00080000) != 0 ? "Immer auf diesem Gerät" :
            (attributes & (FileAttributes)0x00100000) != 0 ? "Kann online-only werden" : null;
        var hash = !isDirectory && !string.IsNullOrWhiteSpace(hashAlgorithm)
            ? ComputeHash(path, hashAlgorithm, cancellationToken)
            : null;

        var extensionForMetadata = Path.GetExtension(path);
        var isImage = WindowsShellMetadataService.ImageExtensions.Contains(extensionForMetadata);
        var isMedia = WindowsShellMetadataService.MediaExtensions.Contains(extensionForMetadata);
        var shellMetadata = !isDirectory && (isImage || isMedia)
            ? WindowsShellMetadataService.Read(path, isImage, isMedia)
            : ShellFileMetadata.Empty;

        return new FilePropertiesDto(path, info.Name, isDirectory, size,
            new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero), new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            new DateTimeOffset(info.LastAccessTimeUtc, TimeSpan.Zero), flags, owner, permissions, version, company, availability,
            string.IsNullOrWhiteSpace(hashAlgorithm) ? null : hashAlgorithm.ToUpperInvariant(), hash,
            shellMetadata.PhotoTaken, shellMetadata.ImageDimensions, shellMetadata.CameraManufacturer,
            shellMetadata.CameraModel, shellMetadata.MediaDurationSeconds);
    }

    private async Task<long> GetDirectorySizeAsync(string root, CancellationToken cancellationToken)
    {
        using var timedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timedCancellation.CancelAfter(_directorySizeTimeout);
        var gate = DirectorySizeGates.GetOrAdd(GetDirectorySizeGateKey(root), static _ => new SemaphoreSlim(1, 1));
        try { await gate.WaitAsync(timedCancellation.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw DirectorySizeTimeout();
        }

        Task<long> operation;
        try
        {
            operation = Task.Factory.StartNew(() =>
            {
                try { return DirectorySize(root, timedCancellation.Token); }
                finally { gate.Release(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            gate.Release();
            throw;
        }

        try { return await operation.WaitAsync(timedCancellation.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ObserveIfAbandoned(operation);
            throw DirectorySizeTimeout();
        }
        catch (OperationCanceledException)
        {
            ObserveIfAbandoned(operation);
            throw;
        }
    }

    private long DirectorySize(string root, CancellationToken cancellationToken)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var path in _enumerateEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                        else total += new FileInfo(path).Length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    private static string GetDirectorySizeGateKey(string path)
    {
        var candidate = path;
        if (candidate.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            candidate = "\\\\" + candidate[8..];
        else if (candidate.StartsWith("\\\\?\\", StringComparison.Ordinal))
            candidate = candidate[4..];
        try { return Path.GetPathRoot(candidate) ?? candidate; }
        catch (ArgumentException) { return candidate; }
    }

    private static TimeoutException DirectorySizeTimeout() =>
        new("Das Laufwerk antwortet nicht rechtzeitig. Prüfe die Verbindung und versuche es erneut.");

    private static void ObserveIfAbandoned(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }
        _ = task.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var effective = timeout ?? DefaultDirectorySizeTimeout;
        if (effective <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return effective;
    }

    private static string ComputeHash(string path, string algorithm, CancellationToken cancellationToken)
    {
        var hashName = algorithm.Equals("md5", StringComparison.OrdinalIgnoreCase)
            ? HashAlgorithmName.MD5
            : algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase)
                ? HashAlgorithmName.SHA256
                : throw new ArgumentException("Unterstützt werden SHA-256 und MD5.");
        using var hasher = IncrementalHash.CreateHash(hashName);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static FileAttributes SetFlag(FileAttributes value, FileAttributes flag, bool? enabled) => enabled is null
        ? value
        : enabled.Value ? value | flag : value & ~flag;
}
