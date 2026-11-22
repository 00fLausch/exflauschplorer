using System.Runtime.InteropServices;
using Explorer.Core.Models;

namespace Explorer.Host.Services;

public sealed class KnownFolderService
{
    private static readonly (string Id, string Name, Guid Guid)[] FolderIds =
    [
        ("desktop", "Desktop", new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")),
        ("documents", "Dokumente", new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7")),
        ("downloads", "Downloads", new Guid("374DE290-123F-4565-9164-39C4925E467B")),
        ("pictures", "Bilder", new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB")),
        ("videos", "Videos", new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC")),
        ("music", "Musik", new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"))
    ];

    public IReadOnlyList<KnownFolderDto> GetFolders() => FolderIds.Select(folder =>
    {
        var path = TryGetPath(folder.Guid);
        return new KnownFolderDto(folder.Id, folder.Name, path ?? "", path is not null && Directory.Exists(path));
    }).ToArray();

    private static string? TryGetPath(Guid id)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pointer);
            return result == 0 && pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pointer) : null;
        }
        catch (DllNotFoundException) { return null; }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
