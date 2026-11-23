using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Explorer.Core.Models;

namespace Explorer.Host.Services;

/// <summary>Performs recycle-bin deletes through the Windows Shell at the user's privilege level.</summary>
public sealed class ShellRecycleBinService : IRecycleBinService
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid ShellItemInterfaceId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        ValidateRecyclePath(path);
        return OnStaAsync(() => Delete(path), cancellationToken);
    }

    internal static void ValidateRecyclePath(string path, DriveType? driveTypeOverride = null)
    {
        var normalized = path;
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            normalized = @"\\" + normalized[8..];
        else if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
            normalized = normalized[4..];

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Die Datei wurde nicht gelöscht. Windows kann Netzwerkpfade nicht zuverlässig in den lokalen Papierkorb verschieben. Verwende „Endgültig löschen“ nur, wenn das beabsichtigt ist.");

        var driveType = driveTypeOverride;
        if (driveType is null)
        {
            var fullPath = Path.GetFullPath(normalized);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("Die Datei wurde nicht gelöscht. Der Windows-Papierkorb ist für diesen Speicherort nicht verfügbar.");
            driveType = new DriveInfo(root).DriveType;
        }

        if (driveType == DriveType.Fixed) return;

        var reason = driveType switch
        {
            DriveType.Removable => "Windows kann auf Wechseldatenträgern keinen Papierkorb garantieren.",
            DriveType.Network => "Windows kann Netzwerkpfade nicht zuverlässig in den lokalen Papierkorb verschieben.",
            _ => "Windows bietet für diesen Datenträgertyp keinen zuverlässig wiederherstellbaren Papierkorb."
        };
        throw new IOException($"Die Datei wurde nicht gelöscht. {reason} Verschiebe sie auf ein lokales Laufwerk oder verwende „Endgültig löschen“ nur nach bewusster Bestätigung.");
    }

    public Task<IReadOnlyList<RecycleBinEntryDto>> ListAsync(CancellationToken cancellationToken) =>
        OnStaAsync<IReadOnlyList<RecycleBinEntryDto>>(List, cancellationToken);

    public Task RestoreAsync(string id, CancellationToken cancellationToken) => OnStaAsync(() => Restore(id), cancellationToken);

    public Task RestoreOriginalPathAsync(string originalPath, CancellationToken cancellationToken) =>
        OnStaAsync(() => RestoreOriginalPath(originalPath), cancellationToken);

    public Task DeletePermanentlyAsync(string id, CancellationToken cancellationToken) => OnStaAsync(() => DeletePermanently(id), cancellationToken);

    public Task EmptyAsync(CancellationToken cancellationToken) => OnStaAsync(() =>
    {
        Marshal.ThrowExceptionForHR(SHEmptyRecycleBin(IntPtr.Zero, null, 0x00000001 | 0x00000002 | 0x00000004));
        return true;
    }, cancellationToken);

    private static IReadOnlyList<RecycleBinEntryDto> List()
    {
        dynamic? shell = null;
        dynamic? folder = null;
        dynamic? items = null;
        var result = new List<RecycleBinEntryDto>();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            folder = shell!.NameSpace(10);
            items = folder!.Items();
            foreach (dynamic item in items!)
            {
                try
                {
                    var original = Convert.ToString(item.ExtendedProperty("System.Recycle.DeletedFrom")) ?? "";
                    if (string.IsNullOrWhiteSpace(original)) continue;
                    var name = Convert.ToString(item.Name) ?? Path.GetFileName(original);
                    var size = TryLong(() => item.Size);
                    var deleted = TryDate(() => item.ExtendedProperty("System.Recycle.DateDeleted"));
                    var isDirectory = TryBool(() => item.IsFolder);
                    var key = MakeRecycleId(item);
                    result.Add(new RecycleBinEntryDto(key, name, Path.Combine(original, name), size, deleted, isDirectory));
                }
                catch (COMException) { }
            }
            return result.OrderByDescending(item => item.DeletedAt).ToArray();
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }
    }

    private static bool Restore(string id)
    {
        dynamic? shell = null;
        dynamic? folder = null;
        dynamic? items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            folder = shell!.NameSpace(10);
            items = folder!.Items();
            foreach (dynamic item in items!)
            {
                try
                {
                    if (MakeRecycleId(item) != id) continue;
                    item.InvokeVerb("undelete");
                    return true;
                }
                catch (COMException) { }
            }
            throw new FileNotFoundException("Der Eintrag ist nicht mehr im Papierkorb.");
        }
        finally { Release(items); Release(folder); Release(shell); }
    }

    private static bool RestoreOriginalPath(string originalPath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(originalPath);
        var originalDirectory = Path.GetDirectoryName(fullPath);
        var originalName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(originalDirectory) || string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("Der ursprüngliche Dateipfad ist ungültig.", nameof(originalPath));
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException("Am ursprünglichen Ort ist bereits ein Element mit diesem Namen vorhanden.");
        dynamic? shell = null;
        dynamic? folder = null;
        dynamic? items = null;
        dynamic? match = null;
        DateTimeOffset? matchDeletedAt = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            folder = shell!.NameSpace(10);
            items = folder!.Items();
            foreach (dynamic item in items!)
            {
                try
                {
                    var deletedFrom = Convert.ToString(item.ExtendedProperty("System.Recycle.DeletedFrom")) ?? "";
                    var name = Convert.ToString(item.Name) ?? "";
                    if (!ShellPathsEqual(deletedFrom, originalDirectory) ||
                        !string.Equals(name, originalName, StringComparison.OrdinalIgnoreCase)) continue;
                    var deletedAt = TryDate(() => item.ExtendedProperty("System.Recycle.DateDeleted"));
                    if (match is null || deletedAt.GetValueOrDefault() > matchDeletedAt.GetValueOrDefault())
                    {
                        match = item;
                        matchDeletedAt = deletedAt;
                    }
                }
                catch (COMException) { }
            }
            if (match is null) throw new FileNotFoundException("Der Eintrag ist nicht mehr im Papierkorb.");
            match.InvokeVerb("undelete");
            return true;
        }
        finally { Release(match); Release(items); Release(folder); Release(shell); }
    }

    private static bool DeletePermanently(string id)
    {
        dynamic? shell = null;
        dynamic? folder = null;
        dynamic? items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            folder = shell!.NameSpace(10);
            items = folder!.Items();
            foreach (dynamic item in items!)
            {
                try
                {
                    if (MakeRecycleId(item) != id) continue;
                    var itemPath = Convert.ToString(item.Path);
                    if (string.IsNullOrWhiteSpace(itemPath))
                        throw new IOException("Windows hat den Papierkorbeintrag nicht eindeutig identifiziert.");
                    Delete(itemPath, recycle: false);
                    return true;
                }
                catch (COMException) { }
            }
            throw new FileNotFoundException("Der Eintrag ist nicht mehr im Papierkorb.");
        }
        finally { Release(items); Release(folder); Release(shell); }
    }

    private static string MakeRecycleId(dynamic item)
    {
        var original = Convert.ToString(item.ExtendedProperty("System.Recycle.DeletedFrom")) ?? "";
        var name = Convert.ToString(item.Name) ?? "";
        var shellPath = Convert.ToString(item.Path) ?? "";
        return MakeRecycleId(original, name, shellPath, TryLong(() => item.Size), TryDate(() => item.ExtendedProperty("System.Recycle.DateDeleted")), TryBool(() => item.IsFolder));
    }

    private static string MakeRecycleId(string original, string name, string shellPath, long? size, DateTimeOffset? deleted, bool isDirectory)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{original}\0{name}\0{shellPath}\0{size}\0{deleted:O}\0{isDirectory}"));
        return Convert.ToHexString(bytes);
    }

    private static bool ShellPathsEqual(string left, string right) =>
        string.Equals(NormalizeShellPath(left), NormalizeShellPath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeShellPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static long? TryLong(Func<dynamic> value) { try { return Convert.ToInt64(value()); } catch { return null; } }
    private static DateTimeOffset? TryDate(Func<dynamic> value) { try { var date = value(); return date is DateTime time ? new DateTimeOffset(time) : null; } catch { return null; } }
    private static bool TryBool(Func<dynamic> value) { try { return Convert.ToBoolean(value()); } catch { return false; } }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    private static bool Delete(string path, bool recycle = true)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            var type = Type.GetTypeFromCLSID(FileOperationClassId, throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            var flags = FofSilent | FofNoConfirmation | FofNoErrorUi;
            if (recycle) flags |= FofAllowUndo | FofxRecycleOnDelete;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
            var iid = ShellItemInterfaceId;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted) throw new IOException("Windows hat das Löschen abgebrochen.");
            return true;
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
        }
    }

    private static Task<T> OnStaAsync<T>(Func<T> callback, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(callback());
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "Explorer-Shell-Operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IShellItem shellItem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint displayNameType, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr window);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
