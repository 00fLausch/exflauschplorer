using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Explorer.Core.Security;
using Explorer.Host.Configuration;

namespace Explorer.Host.Services;

public sealed class ShellThumbnailService(PathAccessPolicy policy)
{
    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    private readonly SemaphoreSlim _generationLock = new(1, 1);
    private static string CacheDirectory => Path.Combine(SettingsStore.DataDirectory, "thumbnails");

    public async Task<string> GetThumbnailPathAsync(string path, int size, CancellationToken cancellationToken)
    {
        var authorized = policy.Authorize(path);
        if (!File.Exists(authorized)) throw new FileNotFoundException();
        size = Math.Clamp(size, 48, 1024);
        var info = new FileInfo(authorized);
        var keyText = $"{authorized.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{size}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyText)));
        Directory.CreateDirectory(CacheDirectory);
        var destination = Path.Combine(CacheDirectory, key + ".png");
        if (File.Exists(destination)) return destination;

        await _generationLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(destination)) return destination;
            var bytes = await Task.Run(() => CreateThumbnail(authorized, size), cancellationToken);
            await File.WriteAllBytesAsync(destination + ".tmp", bytes, cancellationToken);
            File.Move(destination + ".tmp", destination, overwrite: true);
            TrimCacheIfNeeded();
            return destination;
        }
        finally { _generationLock.Release(); }
    }

    private static byte[] CreateThumbnail(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var iid = ImageFactoryId;
            var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            Marshal.ThrowExceptionForHR(result);
            // SIIGBF_RESIZETOFIT is zero; omitting INCACHEONLY allows uncached thumbnails to be created.
            result = factory.GetImage(new NativeSize(size, size), ShellImageFlags.BiggerSizeOk, out bitmapHandle);
            Marshal.ThrowExceptionForHR(result);
            using var image = Image.FromHbitmap(bitmapHandle);
            using var output = new MemoryStream();
            image.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            if (factory is not null && Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
        }
    }

    private static void TrimCacheIfNeeded()
    {
        try
        {
            var files = new DirectoryInfo(CacheDirectory).GetFiles("*.png").OrderByDescending(file => file.LastAccessTimeUtc).ToArray();
            long bytes = 0;
            foreach (var file in files) bytes += file.Length;
            foreach (var file in files.Reverse())
            {
                if (bytes <= 256L * 1024 * 1024) break;
                bytes -= file.Length;
                file.Delete();
            }
        }
        catch { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        out IShellItemImageFactory imageFactory);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellImageFlags flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [Flags]
    private enum ShellImageFlags : uint
    {
        BiggerSizeOk = 0x1,
    }
}
