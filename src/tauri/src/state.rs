//! The on-disk store, in the same shape the Windows app writes: recent files,
//! the page you left each one on, and where the window sat.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

const MAX_RECENT: usize = 25;

/// The colour a window paints before anything has been remembered.
pub const DEFAULT_BACKGROUND: (u8, u8, u8) = (0xF4, 0xF5, 0xF7);

#[derive(Clone, Default, Serialize, Deserialize)]
pub struct RecentItem {
    #[serde(default)]
    pub path: String,
    #[serde(default)]
    pub name: String,
    #[serde(default = "first_page")]
    pub page: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pages: Option<u32>,
    #[serde(default)]
    pub opened: u64,
}

fn first_page() -> u32 {
    1
}

#[derive(Clone, Copy, Default, Serialize, Deserialize)]
pub struct WindowPlacement {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    #[serde(default)]
    pub maximized: bool,
}

#[derive(Clone, Default, Serialize, Deserialize)]
struct StateFile {
    #[serde(default)]
    recent: Vec<RecentItem>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    window: Option<WindowPlacement>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    background: Option<String>,
}

/// The parsed file and the modification time it was parsed at. This process is
/// normally the only writer, so a stat is all a read costs.
static CACHE: Mutex<Option<(StateFile, Option<SystemTime>)>> = Mutex::new(None);

pub fn directory() -> PathBuf {
    let home = std::env::var_os("HOME")
        .or_else(|| std::env::var_os("USERPROFILE"))
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir);
    home.join(".pdfview")
}

fn file() -> PathBuf {
    directory().join("state.json")
}

fn last_written() -> Option<SystemTime> {
    fs::metadata(file()).and_then(|m| m.modified()).ok()
}

/// Runs `work` on the state and writes it back if `work` says it changed.
fn with_state<T>(work: impl FnOnce(&mut StateFile) -> (T, bool)) -> T {
    let mut cache = CACHE.lock().unwrap_or_else(|e| e.into_inner());

    let stamp = last_written();
    if !matches!(&*cache, Some((_, at)) if *at == stamp) {
        // Missing, truncated or hand-edited into nonsense all start clean
        // rather than refusing to open.
        let parsed = fs::read_to_string(file())
            .ok()
            .and_then(|text| serde_json::from_str::<StateFile>(&text).ok())
            .unwrap_or_default();
        *cache = Some((parsed, stamp));
    }

    let (state, at) = cache.as_mut().expect("filled above");
    let (result, changed) = work(state);
    if changed {
        write(state);
        *at = last_written();
    }
    result
}

fn write(state: &StateFile) {
    let Ok(text) = serde_json::to_string_pretty(state) else { return };
    let _ = fs::create_dir_all(directory());
    let temp = file().with_extension("json.tmp");
    if fs::write(&temp, text).is_ok() {
        let _ = fs::rename(&temp, file());
    }
}

/// macOS volumes are case-insensitive unless someone went out of their way.
fn same_path(a: &str, b: &str) -> bool {
    if cfg!(target_os = "linux") {
        a == b
    } else {
        a.eq_ignore_ascii_case(b)
    }
}

/// True when asking the filesystem about this path is cheap. A recent entry on
/// a share that has gone away answers in its own time, and the recent list is
/// on the path that opens a document, so it is not allowed to wait. There is no
/// portable way to ask whether a mount is a network one without touching it,
/// so this goes by where network and removable volumes are mounted.
fn cheap_to_stat(path: &str) -> bool {
    const SLOW_ROOTS: [&str; 6] = ["/Volumes/", "/net/", "/mnt/", "/media/", "/run/media/", "/run/user/"];
    !SLOW_ROOTS.iter().any(|root| path.starts_with(root)) && !path.starts_with(r"\\")
}

/// Recent files, minus any that have since been deleted or moved.
pub fn recent() -> Vec<RecentItem> {
    with_state(|state| {
        let before = state.recent.len();
        state
            .recent
            .retain(|r| !cheap_to_stat(&r.path) || Path::new(&r.path).is_file());
        (state.recent.clone(), state.recent.len() != before)
    })
}

pub fn remember(mut item: RecentItem) {
    with_state(|state| {
        state.recent.retain(|r| !same_path(&r.path, &item.path));
        item.opened = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis() as u64)
            .unwrap_or(0);
        if item.name.is_empty() {
            item.name = Path::new(&item.path)
                .file_name()
                .map(|n| n.to_string_lossy().into_owned())
                .unwrap_or_default();
        }
        state.recent.insert(0, item);
        state.recent.truncate(MAX_RECENT);
        ((), true)
    })
}

pub fn forget(path: &str) {
    with_state(|state| {
        state.recent.retain(|r| !same_path(&r.path, path));
        ((), true)
    })
}

pub fn placement() -> Option<WindowPlacement> {
    with_state(|state| (state.window, false))
}

pub fn save_placement(placement: WindowPlacement) {
    with_state(|state| {
        state.window = Some(placement);
        ((), true)
    })
}

/// The colour a new window paints before the page has rendered. Remembering it
/// is what keeps a dark-theme window from flashing white on open.
pub fn background() -> (u8, u8, u8) {
    with_state(|state| (parse_colour(state.background.as_deref()), false))
}

pub fn save_background(colour: &str) {
    with_state(|state| {
        if state.background.as_deref() == Some(colour) {
            return ((), false);
        }
        state.background = Some(colour.to_string());
        ((), true)
    })
}

pub fn parse_colour(hex: Option<&str>) -> (u8, u8, u8) {
    let Some(hex) = hex else { return DEFAULT_BACKGROUND };

    let digits = hex.trim().trim_start_matches('#');
    let digits: String = if digits.len() == 3 {
        digits.chars().flat_map(|c| [c, c]).collect()
    } else {
        digits.to_string()
    };
    if digits.len() != 6 {
        return DEFAULT_BACKGROUND;
    }
    match u32::from_str_radix(&digits, 16) {
        Ok(value) => ((value >> 16) as u8, (value >> 8) as u8, value as u8),
        Err(_) => DEFAULT_BACKGROUND,
    }
}
