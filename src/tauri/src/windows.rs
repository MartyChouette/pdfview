//! The open document windows: creating and placing them, routing a file to
//! the right one, and the messages each page sends back.

use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Mutex, MutexGuard};

use tauri::window::Color;
use tauri::{
    AppHandle, DragDropEvent, Manager, WebviewUrl, WebviewWindow, WebviewWindowBuilder, WindowEvent,
};
use tauri_plugin_opener::OpenerExt;

use crate::state::{self, WindowPlacement};
use crate::web;

const DEFAULT_WIDTH: f64 = 1180.0;
const DEFAULT_HEIGHT: f64 = 900.0;
const MIN_WIDTH: f64 = 480.0;
const MIN_HEIGHT: f64 = 360.0;
/// Each extra window is offset by this much so they do not land exactly on
/// top of each other, wrapping after `CASCADE_WRAP` windows.
const CASCADE_STEP: f64 = 28.0;
const CASCADE_WRAP: usize = 6;

#[derive(Default)]
struct WindowInfo {
    /// The document the page says it is showing.
    current: Option<String>,
    /// Set once the page has run far enough to accept `pdfviewHost.open`.
    ready: bool,
    /// A document that arrived before the page was ready for it.
    pending: Option<String>,
}

/// Every open window in the order it was created.
#[derive(Default)]
pub struct Windows {
    open: Mutex<Vec<(String, WindowInfo)>>,
    created: AtomicUsize,
}

impl Windows {
    fn lock(&self) -> MutexGuard<'_, Vec<(String, WindowInfo)>> {
        self.open.lock().unwrap_or_else(|e| e.into_inner())
    }
}

pub fn count(app: &AppHandle) -> usize {
    app.state::<Windows>().lock().len()
}

/* ---------- creating ---------- */

pub fn new_window(app: &AppHandle, file: Option<String>) -> Option<WebviewWindow> {
    let registry = app.state::<Windows>();
    let index = registry.created.fetch_add(1, Ordering::Relaxed);
    let label = format!("w{index}");

    // Registered before it is built: the page can send its first message
    // before build() has returned.
    registry.lock().push((
        label.clone(),
        WindowInfo { current: file.clone(), ..Default::default() },
    ));

    let (r, g, b) = state::background();
    let navigator = app.clone();
    let builder = WebviewWindowBuilder::new(
        app,
        &label,
        WebviewUrl::CustomProtocol(web::url_for(file.as_deref())),
    )
    .title("pdfview")
    .min_inner_size(MIN_WIDTH, MIN_HEIGHT)
    .background_color(Color(r, g, b, 255))
    .on_navigation(move |url| {
        // The window shows this app and nothing else; a link in a PDF that
        // points elsewhere goes to the default browser instead.
        if web::is_own(url) || matches!(url.scheme(), "about" | "blob" | "data") {
            return true;
        }
        open_externally(&navigator, url.as_str());
        false
    });

    let window = match place(app, builder, index).build() {
        Ok(window) => window,
        Err(_) => {
            registry.lock().retain(|(l, _)| l != &label);
            return None;
        }
    };

    let events = window.clone();
    window.on_window_event(move |event| on_window_event(&events, event));
    Some(window)
}

/// Sizes and positions a window: where the last one sat if that is still on a
/// screen, otherwise centred on the main display.
fn place<'a>(
    app: &AppHandle,
    builder: WebviewWindowBuilder<'a, tauri::Wry, AppHandle>,
    index: usize,
) -> WebviewWindowBuilder<'a, tauri::Wry, AppHandle> {
    let step = CASCADE_STEP * (index % CASCADE_WRAP) as f64;

    // Everything here is in logical pixels, which is what the builder takes.
    let screens: Vec<(f64, f64, f64, f64)> = app
        .available_monitors()
        .unwrap_or_default()
        .iter()
        .map(|m| {
            let scale = m.scale_factor();
            (
                m.position().x as f64 / scale,
                m.position().y as f64 / scale,
                m.size().width as f64 / scale,
                m.size().height as f64 / scale,
            )
        })
        .collect();

    if let Some(saved) = state::placement() {
        let (x, y) = (saved.x as f64 + step, saved.y as f64 + step);
        let (w, h) = (saved.width as f64, saved.height as f64);
        if w >= MIN_WIDTH && h >= MIN_HEIGHT {
            // Centre it instead if the monitor it was saved on is gone.
            let on_screen = screens
                .iter()
                .any(|&(sx, sy, sw, sh)| x < sx + sw && x + w > sx && y < sy + sh && y + h > sy);
            let sized = builder.inner_size(w, h).maximized(saved.maximized);
            return if on_screen { sized.position(x, y) } else { sized.center() };
        }
    }

    let primary = app.primary_monitor().ok().flatten().map(|m| {
        let scale = m.scale_factor();
        (
            m.position().x as f64 / scale,
            m.position().y as f64 / scale,
            m.size().width as f64 / scale,
            m.size().height as f64 / scale,
        )
    });
    match primary {
        Some((sx, sy, sw, sh)) => {
            let w = DEFAULT_WIDTH.min(sw - 80.0).max(MIN_WIDTH);
            let h = DEFAULT_HEIGHT.min(sh - 120.0).max(MIN_HEIGHT);
            builder
                .inner_size(w, h)
                .position(sx + (sw - w) / 2.0 + step, sy + ((sh - h) / 2.0 - 20.0).max(0.0) + step)
        }
        None => builder.inner_size(DEFAULT_WIDTH, DEFAULT_HEIGHT).center(),
    }
}

/* ---------- window events ---------- */

fn on_window_event(window: &WebviewWindow, event: &WindowEvent) {
    match event {
        // The window takes file drops itself, because only it gets real paths
        // out of them, and drives the page's overlay from here.
        WindowEvent::DragDrop(DragDropEvent::Enter { paths, .. }) => {
            if paths.iter().any(|p| is_pdf(p)) {
                drop_overlay(window, true);
            }
        }
        WindowEvent::DragDrop(DragDropEvent::Leave) => drop_overlay(window, false),
        WindowEvent::DragDrop(DragDropEvent::Drop { paths, .. }) => {
            drop_overlay(window, false);
            let mut pdfs = paths
                .iter()
                .filter(|p| is_pdf(p) && p.is_file())
                .map(|p| p.to_string_lossy().into_owned());
            if let Some(first) = pdfs.next() {
                surface(window);
                open_document(window, first);
                let app = window.app_handle().clone();
                let rest: Vec<String> = pdfs.collect();
                // Not from inside the event handler: building a window there
                // waits on the loop that is running this.
                std::thread::spawn(move || {
                    for extra in rest {
                        new_window(&app, Some(extra));
                    }
                });
            }
        }
        WindowEvent::CloseRequested { .. } => save_placement(window),
        WindowEvent::Destroyed => {
            let registry = window.state::<Windows>();
            registry.lock().retain(|(label, _)| label != window.label());
        }
        _ => {}
    }
}

fn is_pdf(path: &Path) -> bool {
    path.extension().is_some_and(|e| e.eq_ignore_ascii_case("pdf"))
}

fn drop_overlay(window: &WebviewWindow, visible: bool) {
    let script = format!("window.pdfviewHost && window.pdfviewHost.dragOverlay({visible})");
    let _ = window.eval(script.as_str());
}

fn save_placement(window: &WebviewWindow) {
    if window.is_fullscreen().unwrap_or(false) {
        return;
    }

    // There is no "restored bounds" to ask for, so a maximised window keeps
    // the bounds it had before and only records that it was maximised.
    if window.is_maximized().unwrap_or(false) {
        if let Some(previous) = state::placement() {
            state::save_placement(WindowPlacement { maximized: true, ..previous });
            return;
        }
    }

    let (Ok(position), Ok(size), Ok(scale)) =
        (window.outer_position(), window.inner_size(), window.scale_factor())
    else {
        return;
    };
    state::save_placement(WindowPlacement {
        x: (position.x as f64 / scale).round() as i32,
        y: (position.y as f64 / scale).round() as i32,
        width: (size.width as f64 / scale).round() as i32,
        height: (size.height as f64 / scale).round() as i32,
        maximized: window.is_maximized().unwrap_or(false),
    });
}

/* ---------- documents ---------- */

pub fn surface(window: &WebviewWindow) {
    let _ = window.unminimize();
    let _ = window.show();
    let _ = window.set_focus();
}

pub fn open_document(window: &WebviewWindow, file: String) {
    let registry = window.state::<Windows>();
    {
        let mut open = registry.lock();
        let Some((_, info)) = open.iter_mut().find(|(label, _)| label == window.label()) else {
            return;
        };
        if !info.ready {
            // The page picks this up when it reports in.
            info.pending = Some(file);
            return;
        }
    }
    run_open(window, &file);
}

fn run_open(window: &WebviewWindow, file: &str) {
    let literal = serde_json::to_string(file).unwrap_or_default();
    let script = format!("window.pdfviewHost && window.pdfviewHost.open({literal})");
    let _ = window.eval(script.as_str());
}

/// A file arrived from outside: another launch, or Finder. Reuse a window
/// already showing it, otherwise use an empty window, otherwise open a new one.
pub fn open_from_launch(app: &AppHandle, file: Option<String>) {
    enum Target {
        Surface(String),
        Fill(String),
        New,
    }

    let target = {
        let registry = app.state::<Windows>();
        let open = registry.lock();
        match &file {
            None => open.last().map_or(Target::New, |(label, _)| Target::Surface(label.clone())),
            Some(file) => {
                let showing = open.iter().find(|(_, info)| {
                    info.current.as_deref() == Some(file) || info.pending.as_deref() == Some(file)
                });
                let empty = open
                    .iter()
                    .find(|(_, info)| info.current.is_none() && info.pending.is_none());
                match (showing, empty) {
                    (Some((label, _)), _) => Target::Surface(label.clone()),
                    (None, Some((label, _))) => Target::Fill(label.clone()),
                    (None, None) => Target::New,
                }
            }
        }
    };

    match target {
        Target::Surface(label) => {
            if let Some(window) = app.get_webview_window(&label) {
                surface(&window);
            }
        }
        Target::Fill(label) => {
            if let (Some(window), Some(file)) = (app.get_webview_window(&label), file) {
                open_document(&window, file);
                surface(&window);
            }
        }
        Target::New => {
            if let Some(window) = new_window(app, file) {
                surface(&window);
            }
        }
    }
}

/* ---------- page messages ---------- */

pub fn on_message(app: &AppHandle, label: &str, message: &serde_json::Value) {
    let Some(window) = app.get_webview_window(label) else { return };
    let text = |key: &str| message.get(key).and_then(|v| v.as_str());

    match text("type") {
        Some("trace") => {
            let Some(mark) = text("mark") else { return };
            crate::trace(&format!("page: {mark}"));
            if mark != "start" {
                return;
            }
            // "start" is the first mark sent after the page has defined
            // pdfviewHost, so anything held back for it can go now.
            let pending = {
                let registry = app.state::<Windows>();
                let mut open = registry.lock();
                open.iter_mut().find(|(l, _)| l == label).and_then(|(_, info)| {
                    info.ready = true;
                    info.pending.take()
                })
            };
            if let Some(file) = pending {
                run_open(&window, &file);
            }
        }

        Some("opened") => {
            let path = text("path").map(str::to_string);
            let name = path
                .as_deref()
                .and_then(|p| Path::new(p).file_name())
                .map(|n| n.to_string_lossy().into_owned());
            let _ = window.set_title(&match name {
                Some(name) => format!("{name} - pdfview"),
                None => "pdfview".to_string(),
            });

            let registry = app.state::<Windows>();
            let mut open = registry.lock();
            if let Some((_, info)) = open.iter_mut().find(|(l, _)| l == label) {
                info.current = path;
            }
        }

        Some("theme") => {
            if let Some(colour) = text("background").filter(|c| !c.is_empty()) {
                state::save_background(colour);
                let (r, g, b) = state::parse_colour(Some(colour));
                let _ = window.set_background_color(Some(Color(r, g, b, 255)));
            }
        }

        // Sent only when the webview would not put the page fullscreen itself.
        Some("fullscreen") => {
            let on = window.is_fullscreen().unwrap_or(false);
            let _ = window.set_fullscreen(!on);
        }

        _ => {}
    }
}

/* ---------- leaving the app ---------- */

pub fn open_externally(app: &AppHandle, target: &str) {
    let Ok(parsed) = url::Url::parse(target) else { return };
    if !matches!(parsed.scheme(), "http" | "https" | "mailto") {
        return;
    }
    // No default handler for the scheme leaves nothing useful to do.
    let _ = app.opener().open_url(target, None::<&str>);
}

pub fn reveal(app: &AppHandle, path: &str) {
    if Path::new(path).is_file() {
        let _ = app.opener().reveal_item_in_dir(path);
    }
}
