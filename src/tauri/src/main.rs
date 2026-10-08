//! pdfview for macOS and Linux.
//!
//! The Windows app in src/PdfView is a WinForms window around WebView2. This
//! is the same host around the system webview on the other two: WKWebView on
//! macOS, WebKitGTK on Linux. Both show the one front end in app/.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

#[cfg(unix)]
mod ipc;
mod state;
mod web;
mod windows;

use std::sync::OnceLock;
use std::time::Instant;

static STARTED: OnceLock<Instant> = OnceLock::new();

/// A startup timeline on stderr when PDFVIEW_TRACE=1.
pub fn trace(label: &str) {
    static ON: OnceLock<bool> = OnceLock::new();
    if *ON.get_or_init(|| std::env::var("PDFVIEW_TRACE").is_ok_and(|v| v == "1")) {
        let elapsed = STARTED.get_or_init(Instant::now).elapsed();
        eprintln!("{:>6} ms  {label}", elapsed.as_millis());
    }
}

/// The documents named on the command line, as absolute paths. A desktop
/// launcher may pass them as file:// URLs.
fn files_from_arguments() -> Vec<String> {
    std::env::args()
        .skip(1)
        .filter(|arg| !arg.starts_with('-'))
        .filter_map(|arg| {
            let path = match url::Url::parse(&arg) {
                Ok(url) if url.scheme() == "file" => url.to_file_path().ok()?,
                _ => std::path::PathBuf::from(arg),
            };
            std::path::absolute(path).ok()
        })
        .map(|path| path.to_string_lossy().into_owned())
        .collect()
}

fn main() {
    STARTED.get_or_init(Instant::now);
    trace("main");

    let files = files_from_arguments();

    // A second launch hands its files to the running instance and steps aside,
    // so every document lives in one process.
    #[cfg(unix)]
    if ipc::send_to_running_instance(&files) {
        return;
    }
    trace("single-instance checked");

    let app = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .manage(windows::Windows::default())
        .register_asynchronous_uri_scheme_protocol(web::SCHEME, |context, request, responder| {
            web::handle(
                context.app_handle().clone(),
                context.webview_label().to_string(),
                request,
                responder,
            );
        })
        .setup(move |app| {
            let handle = app.handle().clone();

            #[cfg(unix)]
            ipc::start_server(handle.clone());

            let mut files = files.into_iter();
            match files.next() {
                Some(first) => {
                    windows::new_window(&handle, Some(first));
                }
                // Finder may already have delivered a document, in which case
                // there is a window and an empty one would only be in the way.
                None if windows::count(&handle) == 0 => {
                    windows::new_window(&handle, None);
                }
                None => {}
            }
            for extra in files {
                windows::new_window(&handle, Some(extra));
            }
            trace("window created");
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("pdfview could not start");

    app.run(|_app, event| match event {
        // Finder does not put a double-clicked document on the command line;
        // it sends it here, to a fresh launch and a running one alike.
        #[cfg(target_os = "macos")]
        tauri::RunEvent::Opened { urls } => {
            for url in urls {
                if let Ok(path) = url.to_file_path() {
                    windows::open_from_launch(_app, Some(path.to_string_lossy().into_owned()));
                }
            }
        }
        #[cfg(unix)]
        tauri::RunEvent::Exit => ipc::stop_server(),
        _ => {}
    });
}
