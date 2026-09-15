using System.Drawing;
using System.Drawing.Printing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PdfView;

/// Printing goes through a plain view of the PDF rather than the rendered
/// canvases, so the printer gets the document's own vectors and text. The
/// printer is chosen in a native dialog and the page is printed bare: no URL
/// stamped across the header and footer.
sealed class PrintForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly string _file;
    readonly int _pageCount;
    bool _asked;

    public PrintForm(string file, int pageCount)
    {
        _file = file;
        _pageCount = pageCount;

        Text = "Print - " + Path.GetFileName(file);
        Icon = AppIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 900);
        MinimumSize = new Size(480, 360);
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
                await AskAndPrint(environment, core);
            };

            core.Navigate(WebHost.Origin + "/api/file?path=" + Uri.EscapeDataString(_file));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    async Task AskAndPrint(CoreWebView2Environment environment, CoreWebView2 core)
    {
        using var dialog = new PrintDialog
        {
            AllowPrintToFile = true,
            AllowSelection = false,
            AllowSomePages = _pageCount > 1,
            UseEXDialog = true,
        };
        dialog.PrinterSettings.MinimumPage = 1;
        dialog.PrinterSettings.MaximumPage = Math.Max(1, _pageCount);
        dialog.PrinterSettings.FromPage = 1;
        dialog.PrinterSettings.ToPage = Math.Max(1, _pageCount);

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            Close();
            return;
        }

        var chosen = dialog.PrinterSettings;
        var settings = environment.CreatePrintSettings();
        settings.ShouldPrintHeaderAndFooter = false;
        settings.PrinterName = chosen.PrinterName;
        settings.Copies = Math.Max(1, (int)chosen.Copies);
        settings.Collation = chosen.Collate
            ? CoreWebView2PrintCollation.Collated
            : CoreWebView2PrintCollation.Uncollated;
        settings.Orientation = chosen.DefaultPageSettings.Landscape
            ? CoreWebView2PrintOrientation.Landscape
            : CoreWebView2PrintOrientation.Portrait;
        settings.ColorMode = chosen.DefaultPageSettings.Color
            ? CoreWebView2PrintColorMode.Color
            : CoreWebView2PrintColorMode.Grayscale;

        if (chosen.PrintRange == PrintRange.SomePages)
        {
            settings.PageRanges = chosen.FromPage + "-" + chosen.ToPage;
        }

        try
        {
            var status = await core.PrintAsync(settings);
            if (status != CoreWebView2PrintStatus.Succeeded)
            {
                MessageBox.Show(this,
                    status == CoreWebView2PrintStatus.PrinterUnavailable
                        ? "That printer is unavailable."
                        : "The document could not be printed.",
                    "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Close();
    }
}
