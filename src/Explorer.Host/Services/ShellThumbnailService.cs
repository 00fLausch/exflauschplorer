using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Explorer.Core.Security;
using Explorer.Host.Configuration;
using Microsoft.Extensions.Logging;

namespace Explorer.Host.Services;

public sealed class ShellThumbnailService
{
    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    private readonly PathAccessPolicy policy;
    private readonly ILogger<ShellThumbnailService> _logger;
    private readonly SemaphoreSlim _generationSlots = new(2, 2);
    private readonly object _cacheTrim = new();
    private int _blockedSlotLogged;
    private static string CacheDirectory => Path.Combine(SettingsStore.DataDirectory, "thumbnails");

    public ShellThumbnailService(PathAccessPolicy policy, ILogger<ShellThumbnailService> logger)
    {
        this.policy = policy;
        _logger = logger;
    }

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
        if (TryTouchCached(destination)) return destination;

        if (!await _generationSlots.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken))
            throw new TimeoutException("Die Windows-Vorschaubilddienste antworten nicht rechtzeitig.");
        // A blocked Shell handler cannot be interrupted safely. Keep its worker slot reserved.
        var generation = GenerateAsync(authorized, size, destination);
        try
        {
            return await generation.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (TimeoutException)
        {
            if (Interlocked.Exchange(ref _blockedSlotLogged, 1) == 0)
                _logger.LogError("Ein Windows-Vorschaubild-Handler blockiert einen Slot dauerhaft (Pfad: {ThumbnailPath}, Größe: {ThumbnailSize}).", authorized, size);
            throw;
        }
    }

    private async Task<string> GenerateAsync(string path, int size, string destination)
    {
        string? temporary = null;
        try
        {
            if (TryTouchCached(destination)) return destination;
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try { completion.SetResult(CreateThumbnail(path, size)); }
                catch (Exception exception) { completion.SetException(exception); }
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            var bytes = await completion.Task;
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
            lock (_cacheTrim) TrimCacheIfNeeded();
            return destination;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            _generationSlots.Release();
        }
    }

    private static bool TryTouchCached(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(1)) File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return true;
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
            var files = new DirectoryInfo(CacheDirectory).GetFiles("*.png").OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
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
