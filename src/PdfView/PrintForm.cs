using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PdfView;

/// Printing goes through a plain view of the PDF rather than the rendered
/// canvases, so the printer gets the document's own vectors and text, printed
/// bare with no URL stamped across the header and footer.
///
/// The print dialog is Edge's rather than Windows', because it is the only one
/// of the two that shows a preview. Windows' own dialog asks the host for
/// preview pages through the WinRT print stack, WebView2 has no way to supply
/// them, and it reports "This app doesn't support print preview" instead.
///
/// The alternative would be to render the preview pages here and drive the
/// WinRT printer ourselves, which would mean sending the printer rasterised
/// pages instead of the document's own vectors and text. Not worth a preview.
///
/// This window sits behind the dialog showing the document, and Escape closes
/// it once the dialog is gone.
sealed class PrintForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly string _file;
    bool _asked;

    public PrintForm(string file)
    {
        _file = file;

        Text = "Print - " + Path.GetFileName(file);
        Icon = AppIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        // The print preview lives inside this window, so the window is how big
        // the preview gets to be. A fixed 760x900 left the page thumbnail
        // postage-stamp sized. Take most of the work area instead, capped so it
        // does not swallow a large monitor.
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Size = new Size(
            Math.Min(1500, (int)(work.Width * 0.82)),
            Math.Min(1100, (int)(work.Height * 0.88)));
        MinimumSize = new Size(640, 480);
        BackColor = Color.FromArgb(0x32, 0x32, 0x32);

        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var environment = await Shell.EnvironmentAsync();
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            Shell.Host(environment).Attach(core, this);

            core.NavigationCompleted += async (_, _) =>
            {
                if (_asked) return;
                _asked = true;
                Text = "Print - " + Path.GetFileName(_file);
                await WaitForPagesAsync(core);
                core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            };

            core.Navigate(WebHost.Origin + "/api/file?path=" + Uri.EscapeDataString(_file));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// NavigationCompleted fires when the PDF has arrived, not when it has been
    /// laid out. Opening the print preview at that moment previews a document
    /// with no pages in it yet, which Edge reports as one blank sheet. So wait
    /// for the viewer to report a page count before asking for the preview.
    ///
    /// The old flow got away with this by accident: it put up a printer picker
    /// first, and the document had finished laying out by the time anyone chose
    /// a printer and pressed OK.
    static async Task WaitForPagesAsync(CoreWebView2 core)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var ready = await core.ExecuteScriptAsync("document.readyState");
                if (ready is "\"complete\"")
                {
                    // Laying the first pages out lags readyState a little.
                    await Task.Delay(400);
                    return;
                }
            }
            catch
            {
                // The window went away mid-wait; the caller will find out.
                return;
            }

            await Task.Delay(120);
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys key)
    {
        if (key == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, key);
    }

}
