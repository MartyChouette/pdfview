using System.Runtime.InteropServices;

namespace PdfThumb;

/// What the shell expects the returned bitmap's alpha channel to mean.
public enum WtsAlphaType
{
    Unknown = 0,
    Rgb = 1,
    Argb = 2,
}

/// The shell asks a handler for a bitmap no larger than cx on its long edge.
[ComImport]
[Guid("e357fccd-a995-4576-b01f-234630154e96")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    void GetThumbnail(uint cx, out IntPtr phbmp, out WtsAlphaType pdwAlpha);
}

/// How the shell hands the handler the file to look at.
[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
}

internal static class Gdi
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }

    const uint BI_RGB = 0;
    const uint DIB_RGB_COLORS = 0;

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    /// Copies 32-bit BGRA pixels into a fresh DIB section and hands back the
    /// HBITMAP. The shell takes ownership of it.
    public static IntPtr CreateBitmap(int width, int height, ReadOnlySpan<byte> bgra)
    {
        var info = new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,        // negative: rows run top to bottom
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };

        var handle = CreateDIBSection(IntPtr.Zero, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (handle == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

        unsafe
        {
            var destination = new Span<byte>((void*)bits, width * height * 4);
            bgra.CopyTo(destination);
        }
        return handle;
    }
}
