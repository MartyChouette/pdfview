using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfView;

sealed class RecentItem
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("page")] public int Page { get; set; } = 1;
    [JsonPropertyName("pages")] public int? Pages { get; set; }
    [JsonPropertyName("opened")] public long Opened { get; set; }
}

sealed class WindowPlacement
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }
}

sealed class StateFile
{
    [JsonPropertyName("recent")] public List<RecentItem> Recent { get; set; } = new();
    [JsonPropertyName("window")] public WindowPlacement? Window { get; set; }
    [JsonPropertyName("background")] public string? Background { get; set; }
}

/// The on-disk store shared with earlier versions: recent files, the page you
/// left each one on, and where the window sat.
static class AppState
{
    const int MaxRecent = 25;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Directory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pdfview");

    public static string File { get; } = System.IO.Path.Combine(Directory, "state.json");

    /// The parsed file, kept in memory. This process is the only writer, so once
    /// it has been read there is nothing on disk worth re-reading. Opening a
    /// window used to parse it three times over: the remembered background, the
    /// remembered placement, and the recent list.
    static StateFile? _cached;
    static DateTime _cachedAt;

    static StateFile Read()
    {
        // Normally this process is the only writer, so the cache stands. It is
        // not guaranteed, though: when the single-instance mutex is held by
        // something wedged, a second launch opens its own windows and writes
        // here too. A stat is cheap enough to pay on every read, and a full
        // parse is not.
        var stamp = LastWritten();
        if (_cached is not null && stamp == _cachedAt) return _cached;

        try
        {
            _cached = JsonSerializer.Deserialize<StateFile>(System.IO.File.ReadAllText(File));
        }
        catch
        {
            // Missing, truncated or hand-edited into nonsense. Start clean
            // rather than refusing to open.
            _cached = null;
        }

        _cachedAt = stamp;
        return _cached ??= new StateFile();
    }

    static DateTime LastWritten()
    {
        try { return System.IO.File.GetLastWriteTimeUtc(File); }
        catch { return default; }
    }

    static void Write(StateFile state)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = File + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        System.IO.File.Move(temp, File, overwrite: true);

        _cached = state;
        _cachedAt = LastWritten();
    }

    /// True when asking the filesystem about this path is cheap. A recent entry
    /// on a disconnected share answers in its own time, and the recent list is
    /// on the path that opens a document, so it is not allowed to wait.
    static bool CheapToStat(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var drive = new DriveInfo(root);
            return drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch
        {
            return false;
        }
    }

    /// Recent files, minus any that have since been deleted or moved.
    public static List<RecentItem> Recent()
    {
        lock (Gate)
        {
            var state = Read();
            var live = state.Recent
                .Where(r => !CheapToStat(r.Path) || System.IO.File.Exists(r.Path))
                .ToList();
            if (live.Count != state.Recent.Count)
            {
                state.Recent = live;
                Write(state);
            }
            return live;
        }
    }

    public static void Remember(RecentItem item)
    {
        lock (Gate)
        {
            var state = Read();
            state.Recent.RemoveAll(r => string.Equals(r.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            item.Opened = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (string.IsNullOrEmpty(item.Name)) item.Name = System.IO.Path.GetFileName(item.Path);
            state.Recent.Insert(0, item);
            if (state.Recent.Count > MaxRecent) state.Recent.RemoveRange(MaxRecent, state.Recent.Count - MaxRecent);
            Write(state);
        }
    }

    public static void Forget(string path)
    {
        lock (Gate)
        {
            var state = Read();
            state.Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            Write(state);
        }
    }

    public static WindowPlacement? Placement()
    {
        lock (Gate) return Read().Window;
    }

    public static void SavePlacement(WindowPlacement placement)
    {
        lock (Gate)
        {
            var state = Read();
            state.Window = placement;
            Write(state);
        }
    }

    /// The colour a new window paints before the page has rendered. Remembering
    /// it is what keeps a dark-theme window from flashing white on open.
    public static System.Drawing.Color Background()
    {
        string? saved;
        lock (Gate) saved = Read().Background;
        return ParseColour(saved);
    }

    public static void SaveBackground(string colour)
    {
        lock (Gate)
        {
            var state = Read();
            if (state.Background == colour) return;
            state.Background = colour;
            Write(state);
        }
    }

    public static System.Drawing.Color ParseColour(string? hex)
    {
        var fallback = System.Drawing.Color.FromArgb(0xF4, 0xF5, 0xF7);
        if (hex is null) return fallback;

        var digits = hex.Trim().TrimStart('#');
        if (digits.Length == 3)
        {
            digits = string.Concat(digits.Select(c => new string(c, 2)));
        }
        if (digits.Length != 6 ||
            !int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            return fallback;
        }

        return System.Drawing.Color.FromArgb(
            (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }
}
