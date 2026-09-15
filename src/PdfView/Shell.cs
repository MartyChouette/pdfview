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
        // A never-shown window gives background threads something to marshal onto.
        _marshaller = new Form();
        _ = _marshaller.Handle;

        Ipc.OpenRequested += path => _marshaller.BeginInvoke(() => OpenFromAnotherLaunch(path));
        Ipc.StartServer(Cancel.Token);

        _context = new ApplicationContext();
        NewWindow(file);
        Application.Run(_context);

        Cancel.Cancel();
    }

    public static Task<CoreWebView2Environment> EnvironmentAsync() =>
        _environment ??= CreateEnvironment();

    static async Task<CoreWebView2Environment> CreateEnvironment()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pdfview", "WebView2");
        Directory.CreateDirectory(userData);

        var options = new CoreWebView2EnvironmentOptions
        {
            // The viewer is the whole app; none of Edge's own UI belongs in it.
            AdditionalBrowserArguments = "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection",
            AllowSingleSignOnUsingOSPrimaryAccount = false,
        };

        return await CoreWebView2Environment.CreateAsync(null, userData, options);
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

    public static void PrintLater(string? file, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        _marshaller?.BeginInvoke(() => new PrintForm(file, pageCount).Show());
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
