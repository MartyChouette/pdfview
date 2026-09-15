using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PdfThumb;

/// Explorer's thumbnail handler for PDF files: the first page, with the pdfview
/// mark badged into the bottom-right corner.
///
/// Windows runs thumbnail handlers inside its own surrogate process, so a fault
/// here cannot take Explorer down with it. Failures are swallowed and written to
/// %LOCALAPPDATA%\pdfview\thumbnail.log; Explorer then falls back to the plain
/// file-type icon.
[ComVisible(true)]
[Guid(Clsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PdfThumbnailProvider : IInitializeWithStream, IInitializeWithFile, IThumbnailProvider
{
    public const string Clsid = "9B8BC688-593A-4034-A030-CA482B42EEF4";

    string? _path;
    IStream? _stream;

    /// What the shell's isolated thumbnail host uses.
    public void Initialize(IStream stream, uint mode) => _stream = stream;

    /// The fallback, for callers that hand over a path instead.
    public void Initialize(string filePath, uint mode) => _path = filePath;

    public void GetThumbnail(uint cx, out IntPtr phbmp, out WtsAlphaType pdwAlpha)
    {
        phbmp = IntPtr.Zero;
        pdwAlpha = WtsAlphaType.Argb;

        var path = _path;
        var stream = _stream;
        if (stream is null && string.IsNullOrEmpty(path))
            throw new COMException("Not initialized", unchecked((int)0x8000FFFF));

        try
        {
            // The render is async and WinRT-flavoured; give it its own MTA
            // thread rather than pumping whatever apartment the shell called on.
            IntPtr result = IntPtr.Zero;
            Exception? failure = null;

            var worker = new Thread(() =>
            {
                try
                {
                    result = stream is not null
                        ? Renderer.Render(new ComStream(stream), (int)cx)
                        : Renderer.RenderFile(path!, (int)cx);
                }
                catch (Exception ex) { failure = ex; }
            });
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();

            if (!worker.Join(TimeSpan.FromSeconds(25)))
                throw new TimeoutException("Rendering took too long.");
            if (failure is not null) throw failure;

            if (result == IntPtr.Zero)
                throw new COMException("No thumbnail", unchecked((int)0x8004B200));

            phbmp = result;
        }
        catch (Exception ex)
        {
            Log.Write(path ?? "<stream>", ex);
            // E_FAIL tells the shell to fall back to the file-type icon.
            throw new COMException(ex.Message, unchecked((int)0x80004005));
        }
    }
}

internal static class Log
{
    static readonly object Gate = new();

    public static void Write(string? path, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pdfview");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "thumbnail.log");

            lock (Gate)
            {
                // Keep the log from growing without bound.
                if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Delete(file);
                File.AppendAllText(file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {path}\n{ex}\n\n");
            }
        }
        catch
        {
            // Logging must never be the thing that breaks a thumbnail.
        }
    }
}
