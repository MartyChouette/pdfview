using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Data.Pdf;

using Windows.Storage.Streams;

namespace PdfThumb;

/// Draws page one of a PDF and badges the pdfview mark into the corner.
internal static class Renderer
{
    /// The badge's width as a fraction of the thumbnail's width.
    const float BadgeShare = 0.34f;

    /// Gap between the badge and the page edges, as a fraction of the width.
    const float BadgeMargin = 0.04f;

    /// Below this the badge would be mush, so the page is left bare.
    const int BadgeFloor = 40;

    public static IntPtr RenderFile(string path, int cx)
    {
        using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        return Render(file, cx);
    }

    public static IntPtr Render(Stream file, int cx)
    {
        if (cx <= 0) return IntPtr.Zero;

        // The document reads from this lazily, so it has to outlive the render.
        using var page = OpenFirstPage(file);
        if (page is null) return IntPtr.Zero;

        var size = page.Size;
        if (size.Width <= 0 || size.Height <= 0) return IntPtr.Zero;

        // Fit the long edge to cx, the way the shell expects.
        var scale = Math.Min(cx / size.Width, cx / size.Height);
        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));

        using var rendered = RenderPage(page, width, height);
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(canvas))
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // A PDF page is paper: anything the page leaves transparent is white.
            g.Clear(Color.White);
            g.DrawImage(rendered, new Rectangle(0, 0, width, height));

            if (Math.Min(width, height) >= BadgeFloor) DrawBadge(g, width, height);
        }

        return ToHBitmap(canvas);
    }

    /* ---------- the PDF itself ---------- */

    static PdfPage? OpenFirstPage(Stream file)
    {
        // Reading through a stream rather than a StorageFile keeps the shell's
        // file broker out of it, and accepts any path the shell hands us.
        var document = PdfDocument
            .LoadFromStreamAsync(file.AsRandomAccessStream())
            .AsTask().GetAwaiter().GetResult();
        return document.PageCount == 0 ? null : document.GetPage(0);
    }

    static Bitmap RenderPage(PdfPage page, int width, int height)
    {
        using var buffer = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)width,
            DestinationHeight = (uint)height,
        };
        page.RenderToStreamAsync(buffer, options).AsTask().GetAwaiter().GetResult();

        buffer.Seek(0);
        using var stream = buffer.AsStreamForRead();

        // Copy out of the WinRT stream: Bitmap keeps a reference to what it is
        // given, and this one is going away.
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        return new Bitmap(memory);
    }

    /* ---------- the badge ---------- */

    static void DrawBadge(Graphics g, int width, int height)
    {
        var side = Math.Max(16, (int)Math.Round(width * BadgeShare));
        using var badge = LoadBadge(side);
        if (badge is null) return;

        var margin = (int)Math.Round(width * BadgeMargin);
        var x = width - badge.Width - margin;
        var y = height - badge.Height - margin;

        // A soft plate under the mark keeps it readable over a busy page.
        using (var shadow = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
        {
            g.FillEllipse(shadow, x - 2, y - 1, badge.Width + 4, badge.Height + 4);
        }

        g.DrawImage(badge, new Rectangle(x, y, badge.Width, badge.Height));
    }

    static Bitmap? LoadBadge(int side)
    {
        using var stream = typeof(Renderer).Assembly.GetManifestResourceStream("PdfThumb.pdfview.ico");
        if (stream is null) return null;

        // The .ico carries several sizes; ask for the nearest and scale from there.
        using var icon = new Icon(stream, side, side);
        using var source = icon.ToBitmap();

        var scaled = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, side, side));
        return scaled;
    }

    /* ---------- handing it to the shell ---------- */

    static IntPtr ToHBitmap(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            // Everything is drawn over an opaque white page, so the alpha channel
            // is already 255 throughout and needs no premultiplication.
            return Gdi.CreateBitmap(bitmap.Width, bitmap.Height, bytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
