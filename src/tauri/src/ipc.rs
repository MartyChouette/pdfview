//! A socket that lets a second launch hand its files to the first instance.
//! The same job the named pipe does in the Windows app.

use std::io::{Read, Write};
use std::os::unix::net::{UnixListener, UnixStream};
use std::path::PathBuf;

use tauri::AppHandle;

use crate::windows;

fn socket_path() -> PathBuf {
    // XDG_RUNTIME_DIR is per-user and cleared at logout. macOS has no such
    // thing, and the state directory is per-user by construction.
    match std::env::var_os("XDG_RUNTIME_DIR") {
        Some(dir) if !dir.is_empty() => PathBuf::from(dir).join("pdfview.sock"),
        _ => crate::state::directory().join("open.sock"),
    }
}

/// One path per line; an empty message means "just come to the front".
pub fn send_to_running_instance(files: &[String]) -> bool {
    let Ok(mut stream) = UnixStream::connect(socket_path()) else { return false };
    stream.write_all(files.join("\n").as_bytes()).is_ok()
}

pub fn start_server(app: AppHandle) {
    let path = socket_path();
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    // Nobody answered on it a moment ago, so whatever is there is left over
    // from an instance that did not get to clean up.
    let _ = std::fs::remove_file(&path);
    let Ok(listener) = UnixListener::bind(&path) else { return };

    let _ = std::thread::Builder::new().name("pdfview-ipc".into()).spawn(move || {
        for stream in listener.incoming() {
            // A malformed or abandoned connection should not kill the listener.
            let Ok(mut stream) = stream else { continue };
            let mut text = String::new();
            if stream.read_to_string(&mut text).is_err() {
                continue;
            }

            let files: Vec<String> = text
                .lines()
                .map(str::trim)
                .filter(|line| !line.is_empty())
                .map(str::to_string)
                .collect();

            if files.is_empty() {
                windows::open_from_launch(&app, None);
            }
            for file in files {
                windows::open_from_launch(&app, Some(file));
            }
        }
    });
}

pub fn stop_server() {
    let _ = std::fs::remove_file(socket_path());
}
