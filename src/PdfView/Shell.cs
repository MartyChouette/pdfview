using Microsoft.Web.WebView2.Core;

namespace PdfView;

/// Owns the one WebView2 environment, the open windows, and the message loop.
static class Shell
{
    static readonly List<ViewerForm> Windows = new();
    static readonly CancellationTokenSource Cancel = new();

    static ApplicationContext? _context;
    static Form? _marshaller;
    static Task<CoreWebView2Environment>? _environment;
    static WebHost? _host;

    /// Where app/ and vendor/ sit: next to the executable, unless PDFVIEW_WEB
    /// points somewhere else (handy when editing the front end).
    public static string WebRoot { get; } =
        Environment.GetEnvironmentVariable("PDFVIEW_WEB") is { Length: > 0 } dev && Directory.Exists(dev)
            ? dev
            : AppContext.BaseDirectory;

    public static void Run(string? file)
    {
        // Locating the Edge runtime and preparing the user data folder needs no
        // window, so it runs while the UI thread is still paying WinForms' one-off
        // start-up cost.
        _ = EnvironmentAsync();

        // The window comes first. The browser process cannot start spawning until
        // a window exists to host it, and everything below happens while it does.
        _context = new ApplicationContext();
        NewWindow(file);
        Trace.Mark("window shown");

        // A never-shown window gives background threads something to marshal onto
        // that outlives any particular document window.
        _marshaller = new Form();
        _ = _marshaller.Handle;

        Ipc.OpenRequested += path => _marshaller.BeginInvoke(() => OpenFromAnotherLaunch(path));
        Ipc.StartServer(Cancel.Token);
        Trace.Mark("ipc started");

        Application.Run(_context);

        Cancel.Cancel();
    }

    public static Task<CoreWebView2Environment> EnvironmentAsync() =>
        _environment ??= CreateEnvironment();

    /// Where Edge keeps this app's profile. Every environment in the process
    /// must name the same folder and the same options or WebView2 refuses to
    /// share one browser process between them, which is the entire point of
    /// BrowserWarmup.
    public static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "pdfview", "WebView2");

    public static CoreWebView2EnvironmentOptions BrowserOptions() =>
        new()
        {
            // The first group turns off Edge's own UI, which the viewer replaces.
            // The rest are start-up work this app has no use for: there is no
            // first run to greet, no extensions, no sync account, and nothing
            // for the browser to phone home about.
            AdditionalBrowserArguments = string.Join(' ',
                "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-extensions",
                "--disable-sync",
                "--disable-background-networking",
                "--disable-component-update",
                "--disable-domain-reliability",
                "--disable-breakpad"),
            AllowSingleSignOnUsingOSPrimaryAccount = false,
        };

    static async Task<CoreWebView2Environment> CreateEnvironment()
    {
        Directory.CreateDirectory(UserDataFolder);
        Trace.Mark("webview2 environment requested");
        var environment = await CoreWebView2Environment.CreateAsync(
            null, UserDataFolder, BrowserOptions());
        Trace.Mark("webview2 environment ready");
        return environment;
    }

    public static WebHost Host(CoreWebView2Environment environment) =>
        _host ??= new WebHost(environment, WebRoot);

    /* ---------- windows ---------- */

    public static ViewerForm NewWindow(string? file)
    {
        var form = new ViewerForm(file);
        Windows.Add(form);
        form.FormClosed += (_, _) =>
        {
            Windows.Remove(form);
            if (Windows.Count == 0) _context?.ExitThread();
        };
        form.Show();
        return form;
    }

    public static void OpenWindowLater(string? file) =>
        _marshaller?.BeginInvoke(() => NewWindow(file));

    public static void PrintLater(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        _marshaller?.BeginInvoke(() => new PrintForm(file).Show());
    }

    /// A second launch handed us a file. Reuse a window already showing it,
    /// otherwise use an empty window, otherwise open a new one.
    static void OpenFromAnotherLaunch(string? file)
    {
        if (file is null)
        {
            (Windows.LastOrDefault() ?? NewWindow(null)).Surface();
            return;
        }

        var showing = Windows.FirstOrDefault(w =>
            string.Equals(w.CurrentFile, file, StringComparison.OrdinalIgnoreCase));
        if (showing is not null)
        {
            showing.Surface();
            return;
        }

        var empty = Windows.FirstOrDefault(w => w.CurrentFile is null);
        if (empty is not null)
        {
            empty.OpenDocument(file);
            empty.Surface();
            return;
        }

        NewWindow(file).Surface();
    }
}
