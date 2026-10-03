using System.Diagnostics;
using System.Reflection;

namespace PostarrTray;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Single instance — don't stack multiple tray icons if launched twice
        // (e.g. Startup shortcut + manual launch).
        using var mutex = new Mutex(true, "Postarr.Tray.SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        using var ctx = new TrayContext();
        Application.Run(ctx);
    }
}

/// <summary>
/// A minimal system-tray presence for Postarr. Left-click (or the menu's "Open")
/// launches the web UI in the default browser; the menu can start/stop/restart the
/// Windows service (via an elevated sc.exe) and exit the tray helper.
/// </summary>
sealed class TrayContext : ApplicationContext
{
    const string Url = "http://localhost:5286";
    const string ServiceName = "Postarr";

    readonly NotifyIcon _tray;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Open Postarr", null, (_, _) => OpenUi());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Start service",   null, (_, _) => ControlService("start")));
        menu.Items.Add(new ToolStripMenuItem("Stop service",    null, (_, _) => ControlService("stop")));
        menu.Items.Add(new ToolStripMenuItem("Restart service", null, (_, _) => RestartService()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitTray()));

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Postarr",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Left-click opens the UI; right-click shows the menu (WinForms default).
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenUi(); };

        // Windows 11 hides a *new* app's tray icon in the "^" overflow flyout by default, so after
        // the rename the icon looks "gone". Announce ourselves once so the user can find it (and
        // drag it onto the taskbar). One-time only — a flag file keeps later logins quiet.
        AnnounceOnFirstRun();
    }

    static string StateDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postarr");
        Directory.CreateDirectory(dir);
        return dir;
    }

    void AnnounceOnFirstRun()
    {
        try
        {
            var flag = Path.Combine(StateDir(), ".tray-introduced");
            if (File.Exists(flag)) return;
            File.WriteAllText(flag, DateTime.UtcNow.ToString("o"));
            _tray.ShowBalloonTip(6000, "Postarr is running here",
                "Click this icon to open Postarr. If it's hidden, click the ^ arrow by the clock and " +
                "drag it onto the taskbar. Right-click for service controls.", ToolTipIcon.Info);
        }
        catch { /* discovery hint only — never block startup */ }
    }

    void OpenUi()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "Postarr", "Couldn't open the browser: " + ex.Message, ToolTipIcon.Error);
        }
    }

    // sc.exe needs administrator rights, so relaunch it elevated (UAC prompt).
    void ControlService(string verb)
    {
        try
        {
            Process.Start(new ProcessStartInfo("sc.exe", $"{verb} {ServiceName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception)
        {
            // User dismissed the UAC prompt — nothing to do.
        }
    }

    void RestartService()
    {
        try
        {
            // One elevation prompt for the whole restart, via cmd.
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c sc stop {ServiceName} & timeout /t 2 /nobreak >nul & sc start {ServiceName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception)
        {
        }
    }

    void ExitTray()
    {
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    static Icon LoadIcon()
    {
        // Fall back to a system icon rather than crashing if the embedded icon is ever missing or
        // unparseable — a tray helper with no icon that throws on startup would just vanish, which
        // is exactly the "icon disappeared" symptom we're guarding against.
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null) return new Icon(s);
            }
        }
        catch { /* fall through to the system icon */ }
        return SystemIcons.Application;
    }
}
