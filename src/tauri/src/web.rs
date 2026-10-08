//! Serves the viewer's files and its small API to every window in the app.
//! Nothing listens on a socket: requests to the pdfview scheme are answered
//! in-process, so no other program can reach any of this.
//!
//! The routes mirror src/PdfView/WebHost.cs, with two differences that come
//! from the webviews on macOS and Linux. A response here is a block of bytes
//! rather than a stream, so a document is read through /api/chunk a range at a
//! time instead of as one streamed file. And WebKitGTK does not reliably hand
//! over a request body, so everything the page sends travels in the query.

use std::borrow::Cow;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Component, Path, PathBuf};

use include_dir::{include_dir, Dir};
use percent_encoding::{percent_decode_str, utf8_percent_encode, NON_ALPHANUMERIC};
use serde_json::json;
use tauri::http::{header, Request, Response, StatusCode};
use tauri::{AppHandle, Manager, UriSchemeResponder};
use tauri_plugin_dialog::DialogExt;
use url::Url;

use crate::state::{self, RecentItem};
use crate::windows;

pub const SCHEME: &str = "pdfview";

/// The most one /api/chunk request may ask for. pdf.js asks for far less; this
/// only bounds what a single request can make the process allocate.
const MAX_CHUNK: u64 = 32 << 20;

static APP: Dir<'_> = include_dir!("$CARGO_MANIFEST_DIR/../../app");
static VENDOR: Dir<'_> = include_dir!("$CARGO_MANIFEST_DIR/../../vendor");

type Body = Cow<'static, [u8]>;

/// WebView2 cannot register a scheme of its own on the fly, so on Windows the
/// same handler is reached through a reserved http host instead.
fn origin() -> &'static str {
    if cfg!(any(windows, target_os = "android")) {
        "http://pdfview.localhost"
    } else {
        "pdfview://localhost"
    }
}

pub fn url_for(file: Option<&str>) -> Url {
    let text = match file {
        Some(path) => format!("{}/?path={}", origin(), utf8_percent_encode(path, NON_ALPHANUMERIC)),
        None => format!("{}/", origin()),
    };
    Url::parse(&text).expect("the origin is a valid URL")
}

pub fn is_own(url: &Url) -> bool {
    url.scheme() == SCHEME || url.host_str() == Some("pdfview.localhost")
}

pub fn handle(app: AppHandle, label: String, request: Request<Vec<u8>>, responder: UriSchemeResponder) {
    // Off the UI thread: some of these read files and two of them sit in a
    // modal dialog until it closes.
    tauri::async_runtime::spawn_blocking(move || {
        responder.respond(route(&app, &label, &request));
    });
}

/* ---------- routing ---------- */

fn route(app: &AppHandle, label: &str, request: &Request<Vec<u8>>) -> Response<Body> {
    let Ok(url) = Url::parse(&request.uri().to_string()) else {
        return text(StatusCode::BAD_REQUEST, "bad request");
    };
    let path = percent_decode_str(url.path()).decode_utf8_lossy().into_owned();
    let query = |name: &str| {
        url.query_pairs().find(|(key, _)| key == name).map(|(_, value)| value.into_owned())
    };

    match path.as_str() {
        "/" | "/index.html" => asset("app/index.html"),
        "/favicon.ico" => asset("app/favicon.svg"),

        "/api/stat" => stat(query("path")),
        "/api/chunk" => chunk(query("path"), query("start"), query("end")),
        "/api/file" => whole_file(query("path")),
        "/api/recent" => recent(query("remember"), query("forget")),
        "/api/open" => open_dialog(app, label),
        "/api/save" => save_dialog(app, label, query("path")),

        "/api/print" => {
            if let Some(window) = app.get_webview_window(label) {
                let _ = window.print();
            }
            ok()
        }
        "/api/reveal" => {
            if let Some(path) = query("path") {
                windows::reveal(app, &path);
            }
            ok()
        }
        "/api/window" => {
            windows::new_window(app, query("path"));
            ok()
        }
        "/api/external" => {
            if let Some(target) = query("url") {
                windows::open_externally(app, &target);
            }
            ok()
        }
        "/api/message" => {
            // Messages from the page are best-effort; a malformed one is not fatal.
            if let Some(Ok(message)) = query("json").map(|j| serde_json::from_str(&j)) {
                windows::on_message(app, label, &message);
            }
            ok()
        }

        p if p.starts_with("/app/") || p.starts_with("/vendor/") => asset(&p[1..]),
        p if p.starts_with("/api/") => {
            json(StatusCode::NOT_FOUND, json!({ "error": "unknown endpoint" }))
        }
        _ => text(StatusCode::NOT_FOUND, "Not found"),
    }
}

/* ---------- static files ---------- */

fn mime(path: &str) -> &'static str {
    match path.rsplit('.').next().unwrap_or("").to_ascii_lowercase().as_str() {
        "html" => "text/html; charset=utf-8",
        "css" => "text/css; charset=utf-8",
        "js" | "mjs" => "text/javascript; charset=utf-8",
        "json" => "application/json; charset=utf-8",
        "svg" => "image/svg+xml",
        "png" => "image/png",
        "ico" => "image/x-icon",
        "ttf" => "font/ttf",
        "otf" => "font/otf",
        "pdf" => "application/pdf",
        _ => "application/octet-stream",
    }
}

/// `relative` is "app/..." or "vendor/...". The files are compiled into the
/// executable, unless PDFVIEW_WEB points at a folder holding app/ and vendor/
/// (handy when editing the front end).
fn asset(relative: &str) -> Response<Body> {
    let not_found = || text(StatusCode::NOT_FOUND, "Not found");

    // Nothing reaches outside the two folders.
    if !Path::new(relative).components().all(|c| matches!(c, Component::Normal(_))) {
        return not_found();
    }

    let body: Body = match std::env::var_os("PDFVIEW_WEB").filter(|dir| Path::new(dir).is_dir()) {
        Some(dir) => match std::fs::read(PathBuf::from(dir).join(relative)) {
            Ok(bytes) => bytes.into(),
            Err(_) => return not_found(),
        },
        None => {
            let (root, rest) = relative.split_once('/').unwrap_or((relative, ""));
            let folder = if root == "app" { &APP } else { &VENDOR };
            match folder.get_file(rest) {
                Some(file) => Cow::Borrowed(file.contents()),
                None => return not_found(),
            }
        }
    };

    respond(StatusCode::OK, mime(relative), "no-cache", body)
}

/* ---------- pdf bytes ---------- */

fn stat(path: Option<String>) -> Response<Body> {
    let Some(path) = path.filter(|p| !p.trim().is_empty()) else {
        return json(StatusCode::BAD_REQUEST, json!({ "error": "path required" }));
    };
    match std::fs::metadata(&path) {
        Ok(meta) if meta.is_file() => json(StatusCode::OK, json!({ "size": meta.len() })),
        _ => json(StatusCode::NOT_FOUND, json!({ "error": "File not found: ".to_string() + &path })),
    }
}

/// Bytes `start` up to but not including `end`.
fn chunk(path: Option<String>, start: Option<String>, end: Option<String>) -> Response<Body> {
    let (Some(path), Some(start), Some(end)) = (
        path,
        start.and_then(|s| s.parse::<u64>().ok()),
        end.and_then(|e| e.parse::<u64>().ok()),
    ) else {
        return text(StatusCode::BAD_REQUEST, "path, start and end required");
    };

    let read = || -> std::io::Result<Vec<u8>> {
        let mut file = File::open(&path)?;
        let size = file.metadata()?.len();
        let end = end.min(size);
        let length = end.saturating_sub(start).min(MAX_CHUNK);
        let mut bytes = vec![0u8; length as usize];
        file.seek(SeekFrom::Start(start))?;
        file.read_exact(&mut bytes)?;
        Ok(bytes)
    };

    match read() {
        Ok(bytes) => respond(StatusCode::OK, "application/octet-stream", "no-store", bytes.into()),
        Err(error) => text(StatusCode::NOT_FOUND, &error.to_string()),
    }
}

fn whole_file(path: Option<String>) -> Response<Body> {
    let Some(path) = path.filter(|p| !p.trim().is_empty()) else {
        return text(StatusCode::BAD_REQUEST, "path required");
    };
    match std::fs::read(&path) {
        Ok(bytes) => respond(StatusCode::OK, "application/pdf", "no-store", bytes.into()),
        Err(_) => text(StatusCode::NOT_FOUND, &("File not found: ".to_string() + &path)),
    }
}

/* ---------- recent files ---------- */

fn recent(remember: Option<String>, forget: Option<String>) -> Response<Body> {
    if let Some(body) = remember {
        return match serde_json::from_str::<RecentItem>(&body) {
            Ok(item) if !item.path.trim().is_empty() => {
                state::remember(item);
                ok()
            }
            _ => json(StatusCode::BAD_REQUEST, json!({ "error": "path required" })),
        };
    }
    if let Some(path) = forget {
        state::forget(&path);
        return ok();
    }
    json(StatusCode::OK, json!({ "recent": state::recent() }))
}

/* ---------- native dialogs ---------- */

fn open_dialog(app: &AppHandle, label: &str) -> Response<Body> {
    let mut dialog = app
        .dialog()
        .file()
        .set_title("Open PDF")
        .add_filter("PDF documents", &["pdf"])
        .add_filter("All files", &["*"]);
    if let Some(window) = app.get_webview_window(label) {
        dialog = dialog.set_parent(&window);
    }

    let files: Vec<String> = dialog
        .blocking_pick_files()
        .unwrap_or_default()
        .into_iter()
        .filter_map(|picked| picked.into_path().ok())
        .map(|path| path.to_string_lossy().into_owned())
        .collect();

    if files.is_empty() {
        json(StatusCode::OK, json!({ "canceled": true }))
    } else {
        json(StatusCode::OK, json!({ "files": files }))
    }
}

fn save_dialog(app: &AppHandle, label: &str, source: Option<String>) -> Response<Body> {
    let Some(source) = source.filter(|s| Path::new(s).is_file()) else {
        return json(StatusCode::BAD_REQUEST, json!({ "error": "no document" }));
    };
    let name = Path::new(&source)
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| "document.pdf".to_string());

    let mut dialog = app
        .dialog()
        .file()
        .set_title("Save a copy")
        .set_file_name(name)
        .add_filter("PDF documents", &["pdf"]);
    if let Some(window) = app.get_webview_window(label) {
        dialog = dialog.set_parent(&window);
    }

    let Some(target) = dialog.blocking_save_file().and_then(|picked| picked.into_path().ok()) else {
        return json(StatusCode::OK, json!({ "canceled": true }));
    };
    match std::fs::copy(&source, &target) {
        Ok(_) => json(StatusCode::OK, json!({ "ok": true, "path": target.to_string_lossy() })),
        Err(error) => {
            json(StatusCode::INTERNAL_SERVER_ERROR, json!({ "error": error.to_string() }))
        }
    }
}

/* ---------- response helpers ---------- */

fn respond(status: StatusCode, content_type: &str, cache: &str, body: Body) -> Response<Body> {
    Response::builder()
        .status(status)
        .header(header::CONTENT_TYPE, content_type)
        .header(header::CACHE_CONTROL, cache)
        .body(body)
        .expect("static header names and values")
}

fn json(status: StatusCode, body: serde_json::Value) -> Response<Body> {
    respond(status, "application/json; charset=utf-8", "no-store", body.to_string().into_bytes().into())
}

fn text(status: StatusCode, body: &str) -> Response<Body> {
    respond(status, "text/plain; charset=utf-8", "no-store", body.as_bytes().to_vec().into())
}

fn ok() -> Response<Body> {
    json(StatusCode::OK, json!({ "ok": true }))
}
