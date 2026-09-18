using System.Diagnostics;

namespace PdfView;

/// Startup timing. Off unless PDFVIEW_TRACE=1, and when it is off every Mark
/// call is a single bool test, so the marks can sit on the real startup path
/// rather than in a build nobody ships.
///
/// Time zero is when Windows created the process, not when Main ran, because
/// the host and runtime start-up in between is a real part of what the user
/// waits for and it is the part that is easiest to forget to measure.
static class Trace
{
    public static readonly bool On =
        Environment.GetEnvironmentVariable("PDFVIEW_TRACE") == "1";

    static readonly DateTime Origin = ProcessStart();
    static readonly List<(string Name, double Ms)> Marks = new();

    static DateTime ProcessStart()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    public static void Mark(string name)
    {
        if (!On) return;
        var ms = (DateTime.UtcNow - Origin).TotalMilliseconds;
        lock (Marks) Marks.Add((name, ms));
    }

    /// Called once the first document is on screen. Appends one run per line
    /// group so repeated launches can be compared without clearing the file.
    public static void Dump()
    {
        if (!On) return;

        (string, double)[] snapshot;
        lock (Marks) snapshot = Marks.ToArray();
        if (snapshot.Length == 0) return;

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pdfview", "trace.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var lines = new List<string> { "--- run " + DateTime.Now.ToString("HH:mm:ss.fff") + " ---" };
        double previous = 0;
        foreach (var (name, ms) in snapshot)
        {
            lines.Add($"{ms,8:F1} ms  (+{ms - previous,6:F1})  {name}");
            previous = ms;
        }

        try { System.IO.File.AppendAllLines(path, lines); } catch { /* tracing never fails a launch */ }
    }
}
