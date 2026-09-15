using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PdfView;

/// Registers pdfview with Windows so it shows up under "Open with" and in
/// Settings > Default apps. Everything is written under HKEY_CURRENT_USER, so
/// this needs no administrator rights and touches nothing for other users.
///
/// Windows 11 will not let a program make itself the default handler; that last
/// click belongs to you. Registering is what puts pdfview on the list to click.
static class Installer
{
    const string ProgId = "pdfview.Document";
    const string AppName = "pdfview";
    const string ExeKey = "pdfview.exe";

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static int Register(bool quiet = false)
    {
        try
        {
            var exe = ExePath;
            var command = "\"" + exe + "\" \"%1\"";
            var icon = "\"" + exe + "\",0";

            // 1. The file type itself.
            using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                progId.SetValue("", "PDF Document");
                progId.SetValue("FriendlyTypeName", "PDF Document");
                using (var iconKey = progId.CreateSubKey("DefaultIcon")) iconKey.SetValue("", icon);
                using (var open = progId.CreateSubKey(@"shell\open"))
                {
                    open.SetValue("FriendlyAppName", AppName);
                    using var cmd = open.CreateSubKey("command");
                    cmd.SetValue("", command);
                }
            }

            // 2. The application, which is what "Open with" lists.
            using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + ExeKey))
            {
                app.SetValue("FriendlyAppName", AppName);
                using (var iconKey = app.CreateSubKey("DefaultIcon")) iconKey.SetValue("", icon);
                using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
                using (var types = app.CreateSubKey("SupportedTypes")) types.SetValue(".pdf", "");
            }

            // 3. Offer this handler for .pdf without disturbing the current default.
            using (var assoc = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
            {
                assoc.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            // 4. Appear in Settings > Default apps as an app in its own right.
            using (var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\pdfview\Capabilities"))
            {
                capabilities.SetValue("ApplicationName", AppName);
                capabilities.SetValue("ApplicationDescription", "A fast, quiet PDF reader.");
                capabilities.SetValue("ApplicationIcon", icon);
                using var files = capabilities.CreateSubKey("FileAssociations");
                files.SetValue(".pdf", ProgId);
            }

            using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            {
                registered.SetValue(AppName, @"Software\pdfview\Capabilities");
            }

            // 5. Page previews for PDF icons in Explorer.
            var thumbnails = RegisterThumbnailHandler(exe);

            CreateStartMenuShortcut(exe);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            if (quiet) return 0;

            var answer = MessageBox.Show(
                "pdfview is registered.\n\n" +
                "It now appears under \"Open with\" for PDF files and in Settings > Default apps.\n\n" +
                (thumbnails
                    ? "PDF icons will show the first page with the pdfview badge on it.\n\n"
                    : "The thumbnail handler was not found, so PDF icons stay plain.\n\n") +
                "Windows only lets you set the default yourself. Open the settings page now?",
                "pdfview", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (answer == DialogResult.Yes) OpenDefaultAppsSettings();
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not register pdfview.\n\n" + ex.Message,
                "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static int Unregister(bool quiet = false)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\" + ExeKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\pdfview", throwOnMissingSubKey: false);

            using (var assoc = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true))
            {
                assoc?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            {
                registered?.DeleteValue(AppName, throwOnMissingValue: false);
            }

            UnregisterThumbnailHandler();

            var shortcut = StartMenuShortcutPath();
            if (File.Exists(shortcut)) File.Delete(shortcut);

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            if (quiet) return 0;

            MessageBox.Show(
                "pdfview is no longer registered with Windows.\n\n" +
                "If it was your default PDF reader, pick a new one in Settings > Default apps.",
                "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not unregister pdfview.\n\n" + ex.Message,
                "pdfview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /* ---------- thumbnails ---------- */

    /// The COM class in dist\thumbnail that draws PDF page previews.
    const string ThumbClsid = "{9B8BC688-593A-4034-A030-CA482B42EEF4}";

    /// IThumbnailProvider. Explorer looks under this key on a file type to find
    /// out who draws its icons.
    const string ThumbnailProviderIid = "{e357fccd-a995-4576-b01f-234630154e96}";

    static bool RegisterThumbnailHandler(string exe)
    {
        var dll = Path.Combine(Path.GetDirectoryName(exe) ?? "", "thumbnail", "PdfThumb.comhost.dll");
        if (!File.Exists(dll)) return false;

        using (var clsid = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + ThumbClsid))
        {
            clsid.SetValue("", "pdfview PDF thumbnail handler");

            using var server = clsid.CreateSubKey("InprocServer32");
            server.SetValue("", dll);
            server.SetValue("ThreadingModel", "Both");
        }
        // No AppID and no DllSurrogate here on purpose. Windows already runs
        // thumbnail handlers in its own isolated, low-privilege host, which is
        // better than a plain dllhost, and it only does so for handlers that
        // take a stream. Forcing our own surrogate would opt out of that.

        using (var shellEx = Registry.CurrentUser.CreateSubKey(
            @"Software\Classes\.pdf\ShellEx\" + ThumbnailProviderIid))
        {
            shellEx.SetValue("", ThumbClsid);
        }

        return true;
    }

    static void UnregisterThumbnailHandler()
    {
        Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Classes\CLSID\" + ThumbClsid, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Classes\AppID\" + ThumbClsid, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Classes\.pdf\ShellEx\" + ThumbnailProviderIid, throwOnMissingSubKey: false);
    }

    /* ---------- start menu ---------- */

    static string StartMenuShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "pdfview.lnk");

    static void CreateStartMenuShortcut(string exe)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
        link.SetIconLocation(exe, 0);
        link.SetDescription("A fast, quiet PDF reader");

        var target = StartMenuShortcutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        ((IPersistFile)link).Save(target, true);
    }

    static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + AppName)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Settings is unavailable on some SKUs; the registration still stands.
        }
    }

    /* ---------- interop ---------- */

    const int SHCNE_ASSOCCHANGED = 0x08000000;
    const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
            int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon,
            int maxPath, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName,
            [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
