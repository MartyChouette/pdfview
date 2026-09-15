using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PdfView;

/// One document window.
sealed class ViewerForm : Form
{
    static int _cascade;

    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly string? _startFile;

    FormBorderStyle _borderBeforeFullScreen;
    FormWindowState _stateBeforeFullScreen;
    bool _fullScreen;
    bool _ready;

    public string? CurrentFile { get; private set; }

    public ViewerForm(string? file)
    {
        _startFile = file;

        Text = "pdfview";
        Icon = AppIcon.Load();
        MinimumSize = new Size(480, 360);
        BackColor = AppState.Background();
        StartPosition = FormStartPosition.Manual;
        Bounds = PlaceWindow();
        AllowDrop = true;

        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);

        DragEnter += OnDragEnter;
        DragOver += (_, e) => e.Effect = DragDropEffects.Copy;
        DragLeave += (_, _) => ShowDropOverlay(false);
        DragDrop += OnDragDrop;
    }

    /* ---------- lifetime ---------- */

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var environment = await Shell.EnvironmentAsync();
            await _web.EnsureCoreWebView2Async(environment);
            Configure(environment);
            _web.CoreWebView2.Navigate(Shell.Host(environment).UrlFor(_startFile));
            CurrentFile = _startFile;
            _ready = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "pdfview needs the Microsoft Edge WebView2 runtime.\n\n" + ex.Message,
                "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    void Configure(CoreWebView2Environment environment)
    {
        var core = _web.CoreWebView2;
        var settings = core.Settings;

        settings.AreDefaultContextMenusEnabled = true;   // keeps right-click copy
        settings.AreBrowserAcceleratorKeysEnabled = false; // the page owns Ctrl+F, Ctrl+P, F5
        settings.IsZoomControlEnabled = false;             // the page owns Ctrl+wheel
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreDevToolsEnabled =
            Environment.GetEnvironmentVariable("PDFVIEW_DEV") == "1";

        _web.AllowExternalDrop = false;                    // the form handles drops, so we get real paths

        Shell.Host(environment).Attach(core, this);

        core.DocumentTitleChanged += (_, _) =>
            Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "pdfview" : core.DocumentTitle;

        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_fullScreen)
        {
            AppState.SavePlacement(new WindowPlacement
            {
                X = RestoreBounds.X,
                Y = RestoreBounds.Y,
                Width = RestoreBounds.Width,
                Height = RestoreBounds.Height,
                Maximized = WindowState == FormWindowState.Maximized,
            });
        }
        base.OnFormClosing(e);
    }

    /* ---------- placement ---------- */

    static Rectangle PlaceWindow()
    {
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var saved = AppState.Placement();

        var bounds = saved is null
            ? new Rectangle(
                work.X + (work.Width - Math.Min(1180, work.Width - 80)) / 2,
                work.Y + (work.Height - Math.Min(900, work.Height - 80)) / 2,
                Math.Min(1180, work.Width - 80),
                Math.Min(900, work.Height - 80))
            : new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);

        // Offset each extra window so they do not land exactly on top of each other.
        var step = 28 * (_cascade++ % 6);
        bounds.Offset(step, step);

        // Drag it back on screen if the saved monitor is gone.
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            bounds.Location = new Point(work.X + 60, work.Y + 60);

        return bounds;
    }

    public void Surface()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        BringToFront();
    }

    /* ---------- page messages ---------- */

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "opened":
                    CurrentFile = root.TryGetProperty("path", out var path) ? path.GetString() : null;
                    break;

                case "theme":
                    if (root.TryGetProperty("background", out var background) &&
                        background.GetString() is { Length: > 0 } colour)
                    {
                        AppState.SaveBackground(colour);
                        var parsed = AppState.ParseColour(colour);
                        BackColor = parsed;
                        _web.DefaultBackgroundColor = parsed;
                    }
                    break;
            }
        }
        catch
        {
            // Messages from the page are best-effort; a malformed one is not fatal.
        }
    }

    public void OpenDocument(string file)
    {
        if (!_ready) return;
        var literal = JsonSerializer.Serialize(file);
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.pdfviewHost && window.pdfviewHost.open({literal})");
    }

    void ShowDropOverlay(bool visible)
    {
        if (!_ready) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(
            "window.pdfviewHost && window.pdfviewHost.dragOverlay(" + (visible ? "true" : "false") + ")");
    }

    /* ---------- navigation ---------- */

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The window shows this app and nothing else; a link in a PDF that
        // points elsewhere goes to the default browser instead.
        if (e.Uri.StartsWith(WebHost.Origin, StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        OpenExternally(e.Uri);
    }

    void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // No default handler for the scheme; nothing useful to do.
        }
    }

    /* ---------- fullscreen ---------- */

    void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;

        if (on)
        {
            _borderBeforeFullScreen = FormBorderStyle;
            _stateBeforeFullScreen = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;   // forces a resize when already maximised
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _borderBeforeFullScreen;
            WindowState = _stateBeforeFullScreen;
        }
    }

    /* ---------- drag and drop ---------- */

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            ShowDropOverlay(true);
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        ShowDropOverlay(false);
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] dropped) return;

        var pdfs = dropped
            .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .ToArray();
        if (pdfs.Length == 0) return;

        Surface();
        OpenDocument(pdfs[0]);
        foreach (var extra in pdfs.Skip(1)) Shell.NewWindow(extra);
    }
}

/// The window icon, read once from the executable's own resources.
static class AppIcon
{
    static Icon? _icon;

    public static Icon? Load()
    {
        if (_icon is not null) return _icon;
        var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("PdfView.pdfview.ico");
        if (stream is null) return null;
        using (stream) return _icon = new Icon(stream);
    }
}
