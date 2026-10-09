using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace Glint
{
    public static class Program
    {
        private static EventWaitHandle showSignal;

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--demo") { Demo.Run(args[1], args[2..]); return; }
            var single = new Mutex(false, "Glint.SingleInstance");
            bool owned;
            try { owned = single.WaitOne(args.Length > 0 && args[0] == "--relaunch" ? 8000 : 0); }
            catch (AbandonedMutexException) { owned = true; }
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Glint.Show");
            if (!owned) { showSignal.Set(); return; } // already running: just open its bar

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settings = Settings.Load();
            var index = new FileIndex();
            var window = new SearchWindow(index);
            var hotkey = new Hotkey(settings, window);
            Tray tray = null;

            app.Startup += (s, e) =>
            {
                index.Start();
                hotkey.Register();
                tray = new Tray(app, window, index, settings, hotkey, () => { single.ReleaseMutex(); });
                var waiter = new Thread(() =>
                {
                    while (showSignal.WaitOne()) window.Dispatcher.BeginInvoke(new Action(window.ShowBar));
                }) { IsBackground = true };
                waiter.Start();
                if (!settings.Welcomed) { settings.Welcomed = true; settings.Save(); window.ShowBar(); }
            };
            app.Exit += (s, e) => { tray?.Dispose(); hotkey.Dispose(); };
            app.Run();
        }
    }

    /// The global shortcut, held by a hidden message-only window.
    internal sealed class Hotkey : IDisposable
    {
        private readonly Settings settings;
        private readonly SearchWindow window;
        private HwndSource source;
        public string Active { get; private set; }

        public Hotkey(Settings s, SearchWindow w) { settings = s; window = w; }

        private static (uint mods, uint vk) Parse(string combo) => combo switch
        {
            "Ctrl+Space" => (Native.MOD_CONTROL, 0x20u),
            "Win+Alt+Space" => (Native.MOD_WIN | Native.MOD_ALT, 0x20u),
            "Ctrl+Alt+Space" => (Native.MOD_CONTROL | Native.MOD_ALT, 0x20u),
            _ => (Native.MOD_ALT, 0x20u),
        };

        public void Register()
        {
            if (source == null)
            {
                var p = new HwndSourceParameters("GlintHotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
                source = new HwndSource(p);
                source.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == Native.WM_HOTKEY) { window.Toggle(); handled = true; }
                    return IntPtr.Zero;
                });
            }
            Native.UnregisterHotKey(source.Handle, 1);
            foreach (var combo in new[] { settings.Hotkey, "Ctrl+Alt+Space" })
            {
                var (m, vk) = Parse(combo);
                if (Native.RegisterHotKey(source.Handle, 1, m | Native.MOD_NOREPEAT, vk)) { Active = combo; return; }
            }
            Active = null;
        }

        public void Dispose()
        {
            if (source != null) { Native.UnregisterHotKey(source.Handle, 1); source.Dispose(); }
        }
    }

    internal sealed class Tray : IDisposable
    {
        private readonly WinForms.NotifyIcon icon;
        private readonly Application app;
        private readonly SearchWindow window;
        private readonly FileIndex index;
        private readonly Settings settings;
        private readonly Hotkey hotkey;
        private readonly Action releaseInstance;

        public Tray(Application app, SearchWindow window, FileIndex index, Settings settings, Hotkey hotkey, Action releaseInstance)
        {
            this.app = app; this.window = window; this.index = index; this.settings = settings; this.hotkey = hotkey; this.releaseInstance = releaseInstance;
            icon = new WinForms.NotifyIcon { Icon = DrawIcon(), Visible = true, ContextMenuStrip = new WinForms.ContextMenuStrip() };
            icon.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) window.ShowBar(); };
            icon.ContextMenuStrip.Opening += (s, e) => { Fill(); e.Cancel = false; };
            Fill();
            if (hotkey.Active == null)
                icon.ShowBalloonTip(6000, "Glint", "Another app holds the shortcut. Pick a different one from the tray menu.", WinForms.ToolTipIcon.Warning);
        }

        private void Fill()
        {
            icon.Text = Trim("Glint · " + (hotkey.Active ?? "no shortcut") + " · " + index.Status);
            var m = icon.ContextMenuStrip;
            m.Items.Clear();
            m.Items.Add(new WinForms.ToolStripMenuItem("Search", null, (s, e) => window.ShowBar()) { ShortcutKeyDisplayString = hotkey.Active ?? "" });
            m.Items.Add(new WinForms.ToolStripMenuItem(index.Status) { Enabled = false });
            m.Items.Add(new WinForms.ToolStripSeparator());

            var keys = new WinForms.ToolStripMenuItem("Shortcut");
            foreach (var combo in new[] { "Alt+Space", "Ctrl+Space", "Win+Alt+Space", "Ctrl+Alt+Space" })
            {
                string c = combo;
                keys.DropDownItems.Add(new WinForms.ToolStripMenuItem(c, null, (s, e) => { settings.Hotkey = c; settings.Save(); hotkey.Register(); }) { Checked = hotkey.Active == c });
            }
            m.Items.Add(keys);

            if (!Native.IsAdmin())
                m.Items.Add(new WinForms.ToolStripMenuItem("Use fast NTFS index (restart as administrator)", null, (s, e) => Relaunch(true))
                { ToolTipText = "Reads the drive's file table directly: seconds to index, changes show up instantly." });
            m.Items.Add(new WinForms.ToolStripMenuItem("Open at sign-in", null, (s, e) => { try { Settings.LaunchAtLogin = !SafeLogin(); } catch { } }) { Checked = SafeLogin() });
            m.Items.Add(new WinForms.ToolStripSeparator());
            m.Items.Add(new WinForms.ToolStripMenuItem("Quit Glint", null, (s, e) => { icon.Visible = false; app.Shutdown(); }));
        }

        private static bool SafeLogin() { try { return Settings.LaunchAtLogin; } catch { return false; } }
        private static string Trim(string s) => s.Length > 63 ? s.Substring(0, 63) : s;

        private void Relaunch(bool elevated)
        {
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath, "--relaunch") { UseShellExecute = true, Verb = elevated ? "runas" : "" };
                Process.Start(psi);
                icon.Visible = false;
                releaseInstance();
                app.Shutdown();
            }
            catch { /* UAC declined */ }
        }

        /// A small glint: a four-point star inside a search ring, drawn in code.
        private static Icon DrawIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var ring = new Pen(Color.White, 3f);
                g.DrawEllipse(ring, 3, 3, 20, 20);
                g.DrawLine(ring, 20, 20, 29, 29);
                var star = new[] { new PointF(13, 6), new PointF(15, 11), new PointF(20, 13), new PointF(15, 15), new PointF(13, 20), new PointF(11, 15), new PointF(6, 13), new PointF(11, 11) };
                using var b = new SolidBrush(Color.FromArgb(255, 210, 90));
                g.FillPolygon(b, star);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        public void Dispose() { icon.Visible = false; icon.Dispose(); }
    }
}
