namespace PdfView;

static class Program
{
    static Mutex? _instanceLock;

    [STAThread]
    static int Main(string[] argv)
    {
        Trace.Mark("main");
        var args = CommandLine.Parse(argv);

        if (args.Register) return Installer.Register(args.Quiet);
        if (args.Unregister) return Installer.Unregister(args.Quiet);

        // A second launch hands its file to the running instance and steps aside,
        // so every document lives in one process and one taskbar group.
        _instanceLock = new Mutex(true, @"Local\pdfview.instance", out bool first);
        if (!first)
        {
            if (Ipc.SendToRunningInstance(args.File)) return 0;
            // The holder is gone or wedged; carry on and open our own window.
        }

        Trace.Mark("single-instance checked");

        Application.EnableVisualStyles();
        Trace.Mark("visual styles");
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Trace.Mark("dpi mode");

        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);

        Shell.Run(args.File);
        return 0;
    }

    static void Crash(Exception? ex)
    {
        MessageBox.Show(
            ex?.ToString() ?? "Unknown error.",
            "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

readonly record struct CommandLine(string? File, bool Register, bool Unregister, bool Quiet)
{
    public static CommandLine Parse(string[] argv)
    {
        string? file = null;
        bool register = false, unregister = false, quiet = false;

        foreach (var arg in argv)
        {
            if (arg.Equals("--register", StringComparison.OrdinalIgnoreCase)) register = true;
            else if (arg.Equals("--unregister", StringComparison.OrdinalIgnoreCase)) unregister = true;
            else if (arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase)) quiet = true;
            else if (arg.StartsWith('-')) { /* ignore unknown switches */ }
            else if (file is null) file = Resolve(arg);
        }

        return new CommandLine(file, register, unregister, quiet);
    }

    static string? Resolve(string arg)
    {
        try { return Path.GetFullPath(arg.Trim('"')); }
        catch { return null; }
    }
}
