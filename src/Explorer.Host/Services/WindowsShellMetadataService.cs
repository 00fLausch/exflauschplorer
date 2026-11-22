using System.Globalization;
using System.Runtime.InteropServices;

namespace Explorer.Host.Services;

internal sealed record ShellFileMetadata(
    DateTimeOffset? PhotoTaken,
    string? ImageDimensions,
    string? CameraManufacturer,
    string? CameraModel,
    double? MediaDurationSeconds)
{
    public static ShellFileMetadata Empty { get; } = new(null, null, null, null, null);
}

internal static class WindowsShellMetadataService
{
    private static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid PhotoPropertySet = new("14B81DA1-0135-4D31-96D9-6CBFC9671A99");
    private static readonly Guid MediaPropertySet = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid ImagePropertySet = new("6444048F-4C8B-11D1-8B70-080036B11A03");

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".tif", ".tiff", ".heic", ".heif", ".png", ".webp", ".bmp" };

    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpg", ".mpeg", ".mts", ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".ogg", ".opus" };

    public static ShellFileMetadata Read(string path, bool image, bool media)
    {
        if (!OperatingSystem.IsWindows()) return ShellFileMetadata.Empty;

        IPropertyStore? store = null;
        try
        {
            var interfaceId = PropertyStoreId;
            var result = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref interfaceId, out store);
            if (result < 0 || store is null) return ShellFileMetadata.Empty;

            var photoTaken = image ? ReadDateTime(store, new PropertyKey(PhotoPropertySet, 36867)) : null;
            var dimensions = image ? ReadString(store, new PropertyKey(ImagePropertySet, 13)) : null;
            var manufacturer = image ? ReadString(store, new PropertyKey(PhotoPropertySet, 271)) : null;
            var model = image ? ReadString(store, new PropertyKey(PhotoPropertySet, 272)) : null;
            var duration = media ? ReadDuration(store, new PropertyKey(MediaPropertySet, 3)) : null;
            return new ShellFileMetadata(photoTaken, dimensions, manufacturer, model, duration);
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return ShellFileMetadata.Empty;
        }
        finally
        {
            if (store is not null && Marshal.IsComObject(store)) Marshal.ReleaseComObject(store);
        }
    }

    private static DateTimeOffset? ReadDateTime(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try
        {
            return value.VariantType switch
            {
                64 => new DateTimeOffset(DateTime.FromFileTimeUtc(value.Int64Value)),
                7 => new DateTimeOffset(DateTime.FromOADate(value.DoubleValue).ToUniversalTime()),
                31 or 8 when TryReadString(value, out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
                _ => null
            };
        }
        catch (ArgumentOutOfRangeException) { return null; }
        finally { PropVariantClear(ref value); }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return TryReadString(value, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null; }
        finally { PropVariantClear(ref value); }
    }

    private static double? ReadDuration(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try
        {
            var seconds = value.VariantType switch
            {
                21 => value.UInt64Value / 10_000_000d,
                20 => value.Int64Value / 10_000_000d,
                _ => 0d
            };
            return double.IsFinite(seconds) && seconds > 0 ? seconds : null;
        }
        finally { PropVariantClear(ref value); }
    }

    private static bool TryReadString(PropVariant value, out string text)
    {
        text = string.Empty;
        if (value.PointerValue == IntPtr.Zero) return false;
        text = value.VariantType switch
        {
            31 => Marshal.PtrToStringUni(value.PointerValue) ?? string.Empty,
            8 => Marshal.PtrToStringBSTR(value.PointerValue) ?? string.Empty,
            _ => string.Empty
        };
        return text.Length > 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr bindContext,
        uint flags,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public long Int64Value;
        [FieldOffset(8)] public ulong UInt64Value;
        [FieldOffset(8)] public double DoubleValue;
        [FieldOffset(8)] public IntPtr PointerValue;
    }
}
