using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Web.WebView2.Core;

namespace PdfView;

/// Serves the viewer's files and its small API to every WebView2 in the app.
/// Nothing listens on a socket: requests to https://pdfview.local are answered
/// in-process, so no other program can reach any of this.
sealed partial class WebHost
{
    public const string Origin = "https://pdfview.local";

    static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".bcmap"] = "application/octet-stream",
        [".pfb"] = "application/octet-stream",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".pdf"] = "application/pdf",
    };

    readonly CoreWebView2Environment _env;
    readonly string _root;

    public WebHost(CoreWebView2Environment env, string root)
    {
        _env = env;
        // No trailing separator: the containment check below appends its own.
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string UrlFor(string? pdfPath) =>
        pdfPath is null ? Origin + "/" : Origin + "/?path=" + Uri.EscapeDataString(pdfPath);

    public void Attach(CoreWebView2 core, Form owner)
    {
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => Route(e, owner);
    }

    /* ---------- routing ---------- */

    void Route(CoreWebView2WebResourceRequestedEventArgs e, Form owner)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(uri.Query);
            var method = e.Request.Method ?? "GET";

            switch (path)
            {
                case "/":
                case "/index.html":
                    e.Response = StaticFile(Path.Combine(_root, "app", "index.html"), e.Request);
                    return;

                case "/favicon.ico":
                    e.Response = StaticFile(Path.Combine(_root, "app", "favicon.svg"), e.Request);
                    return;

                case "/api/file":
                    e.Response = PdfFile(query["path"], e.Request);
                    return;

                case "/api/recent":
                    e.Response = Recent(method, query, e.Request);
                    return;

                case "/api/open":
                    OpenDialog(e, owner);
                    return;

                case "/api/save":
                    SaveDialog(e, owner, query["path"]);
                    return;

                case "/api/print":
                    Shell.PrintLater(query["path"]);
                    e.Response = Json(new { ok = true });
                    return;

                case "/api/reveal":
                    Reveal(query["path"]);
                    e.Response = Json(new { ok = true });
                    return;

                case "/api/window":
                    Shell.OpenWindowLater(query["path"]);
                    e.Response = Json(new { ok = true });
                    return;
            }

            if (path.StartsWith("/app/", StringComparison.Ordinal) ||
                path.StartsWith("/vendor/", StringComparison.Ordinal))
            {
                e.Response = StaticFile(SafePath(path), e.Request);
                return;
            }

            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                e.Response = Json(new { error = "unknown endpoint" }, 404, "Not Found");
                return;
            }

            e.Response = Text(404, "Not Found", "Not found");
        }
        catch (Exception ex)
        {
            e.Response = Text(500, "Internal Error", ex.Message);
        }
    }

    /* ---------- static files ---------- */

    string? SafePath(string urlPath)
    {
        var relative = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    CoreWebView2WebResourceResponse StaticFile(string? file, CoreWebView2WebResourceRequest request)
    {
        if (file is null || !File.Exists(file)) return Text(404, "Not Found", "Not found");

        var info = new FileInfo(file);
        var etag = "\"" + info.Length.ToString("x") + "-" + info.LastWriteTimeUtc.Ticks.ToString("x") + "\"";
        if (request.Headers.Contains("If-None-Match") &&
            request.Headers.GetHeader("If-None-Match") == etag)
        {
            return _env.CreateWebResourceResponse(null, 304, "Not Modified", "ETag: " + etag);
        }

        var type = MimeTypes.GetValueOrDefault(info.Extension, "application/octet-stream");
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

        return _env.CreateWebResourceResponse(stream, 200, "OK", Headers(
            ("Content-Type", type),
            ("Content-Length", info.Length.ToString()),
            ("ETag", etag),
            ("Cache-Control", "no-cache")));
    }

    /* ---------- pdf streaming ---------- */

    CoreWebView2WebResourceResponse PdfFile(string? path, CoreWebView2WebResourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(path)) return Text(400, "Bad Request", "path required");

        FileInfo info;
        try { info = new FileInfo(Path.GetFullPath(path)); }
        catch { return Text(400, "Bad Request", "bad path"); }
        if (!info.Exists) return Text(404, "Not Found", "File not found: " + path);

        var name = info.Name.Replace("\"", "");
        var common = new List<(string, string)>
        {
            ("Content-Type", "application/pdf"),
            ("Accept-Ranges", "bytes"),
            ("Cache-Control", "no-store"),
            ("Content-Disposition", "inline; filename=\"" + name + "\""),
        };

        var range = request.Headers.Contains("Range") ? request.Headers.GetHeader("Range") : null;
        var match = range is null ? null : RangeHeader().Match(range.Trim());

        if (match is { Success: true })
        {
            long start, end;
            var from = match.Groups[1].Value;
            var to = match.Groups[2].Value;

            if (from.Length == 0)
            {
                // "bytes=-500": the last 500 bytes.
                var tail = to.Length == 0 ? 0 : long.Parse(to);
                start = Math.Max(0, info.Length - tail);
                end = info.Length - 1;
            }
            else
            {
                start = long.Parse(from);
                end = to.Length == 0 ? info.Length - 1 : Math.Min(long.Parse(to), info.Length - 1);
            }

            if (start > end || start >= info.Length)
            {
                return _env.CreateWebResourceResponse(null, 416, "Range Not Satisfiable",
                    Headers(("Content-Range", "bytes */" + info.Length)));
            }

            var length = end - start + 1;
            common.Add(("Content-Range", "bytes " + start + "-" + end + "/" + info.Length));
            common.Add(("Content-Length", length.ToString()));
            return _env.CreateWebResourceResponse(
                new RangeStream(info.FullName, start, length), 206, "Partial Content", Headers(common));
        }

        common.Add(("Content-Length", info.Length.ToString()));
        return _env.CreateWebResourceResponse(
            new RangeStream(info.FullName, 0, info.Length), 200, "OK", Headers(common));
    }

    [GeneratedRegex(@"^bytes=(\d*)-(\d*)$")]
    private static partial Regex RangeHeader();

    /* ---------- recent files ---------- */

    CoreWebView2WebResourceResponse Recent(
        string method,
        System.Collections.Specialized.NameValueCollection query,
        CoreWebView2WebResourceRequest request)
    {
        switch (method.ToUpperInvariant())
        {
            case "GET":
                return Json(new { recent = AppState.Recent() });

            case "POST":
            {
                var body = ReadBody(request);
                var item = string.IsNullOrEmpty(body) ? null : JsonSerializer.Deserialize<RecentItem>(body);
                if (item is null || string.IsNullOrWhiteSpace(item.Path))
                    return Json(new { error = "path required" }, 400, "Bad Request");
                AppState.Remember(item);
                return Json(new { ok = true });
            }

            case "DELETE":
                if (query["path"] is { } target) AppState.Forget(target);
                return Json(new { ok = true });

            default:
                return Json(new { error = "method not allowed" }, 405, "Method Not Allowed");
        }
    }

    static string ReadBody(CoreWebView2WebResourceRequest request)
    {
        if (request.Content is null) return "";
        using var reader = new StreamReader(request.Content, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    /* ---------- native dialogs ---------- */

    void OpenDialog(CoreWebView2WebResourceRequestedEventArgs e, Form owner)
    {
        // A modal dialog cannot run inside the request handler without wedging
        // the browser process, so answer the request once the dialog closes.
        var deferral = e.GetDeferral();
        owner.BeginInvoke(() =>
        {
            try
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Open PDF",
                    Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
                    Multiselect = true,
                    CheckFileExists = true,
                };
                e.Response = dialog.ShowDialog(owner) == DialogResult.OK
                    ? Json(new { files = dialog.FileNames })
                    : Json(new { canceled = true });
            }
            catch (Exception ex)
            {
                e.Response = Json(new { error = ex.Message }, 500, "Internal Error");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    void SaveDialog(CoreWebView2WebResourceRequestedEventArgs e, Form owner, string? source)
    {
        var deferral = e.GetDeferral();
        owner.BeginInvoke(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                {
                    e.Response = Json(new { error = "no document" }, 400, "Bad Request");
                    return;
                }

                using var dialog = new SaveFileDialog
                {
                    Title = "Save a copy",
                    Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
                    FileName = Path.GetFileName(source),
                    DefaultExt = "pdf",
                    OverwritePrompt = true,
                };
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    e.Response = Json(new { canceled = true });
                    return;
                }

                File.Copy(source, dialog.FileName, overwrite: true);
                e.Response = Json(new { ok = true, path = dialog.FileName });
            }
            catch (Exception ex)
            {
                e.Response = Json(new { error = ex.Message }, 500, "Internal Error");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    static void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Path.GetFullPath(path) + "\"")
        {
            UseShellExecute = true,
        });
    }

    /* ---------- response helpers ---------- */

    static string Headers(params (string Name, string Value)[] headers) =>
        Headers((IEnumerable<(string, string)>)headers);

    static string Headers(IEnumerable<(string Name, string Value)> headers) =>
        string.Join("\r\n", headers.Select(h => h.Name + ": " + h.Value));

    CoreWebView2WebResourceResponse Json(object body, int status = 200, string reason = "OK")
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        return _env.CreateWebResourceResponse(new MemoryStream(bytes), status, reason, Headers(
            ("Content-Type", "application/json; charset=utf-8"),
            ("Content-Length", bytes.Length.ToString()),
            ("Cache-Control", "no-store")));
    }

    CoreWebView2WebResourceResponse Text(int status, string reason, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return _env.CreateWebResourceResponse(new MemoryStream(bytes), status, reason, Headers(
            ("Content-Type", "text/plain; charset=utf-8"),
            ("Content-Length", bytes.Length.ToString()),
            ("Cache-Control", "no-store")));
    }
}
