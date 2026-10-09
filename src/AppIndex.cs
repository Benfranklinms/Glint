using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Glint
{
    /// Start-menu apps (desktop and Store), Settings pages and admin tools,
    /// matched with the same fuzzy scorer as files and shown above them.
    public static class AppIndex
    {
        private sealed class App { public string Name; public string Launch; public string Icon; public HitKind Kind; public string Sub; public string[] Keywords; }

        private static volatile List<App> apps = new List<App>();
        public static bool Ready { get; private set; }

        public static void Start()
        {
            Task.Run(() =>
            {
                var list = new List<App>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { StartMenu(list, seen); } catch { }
                apps = list.Concat(SettingsPages()).ToList();
                Ready = true;
                try { StoreApps(list, seen); } catch { }
                apps = list.Concat(SettingsPages()).ToList();
            });
        }

        private static readonly string[] Junk = { "uninstall", "readme", "read me", "help", "documentation", "website", "release notes", "license", "changelog", "manual" };

        private static void StartMenu(List<App> list, HashSet<string> seen)
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(root, "*", opts))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".url" && ext != ".appref-ms") continue;
                    string name = Path.GetFileNameWithoutExtension(f);
                    string low = name.ToLowerInvariant();
                    if (Junk.Any(j => low.Contains(j))) continue;
                    if (!seen.Add(name)) continue;
                    list.Add(new App { Name = name, Launch = f, Icon = f, Kind = HitKind.App, Sub = "Application" });
                }
            }
        }

        /// Store and packaged apps (Calculator, Photos, Terminal…) come from Get-StartApps.
        private static void StoreApps(List<App> list, HashSet<string> seen)
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            string json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            lock (list)
                foreach (var e in items)
                {
                    string name = e.TryGetProperty("Name", out var n) ? n.GetString() : null;
                    string id = e.TryGetProperty("AppID", out var a) ? a.GetString() : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)) continue;
                    if (!id.Contains("!") ) continue;           // desktop apps were already found as shortcuts
                    if (Junk.Any(j => name.ToLowerInvariant().Contains(j)) || !seen.Add(name)) continue;
                    list.Add(new App { Name = name, Launch = @"shell:AppsFolder\" + id, Icon = @"shell:AppsFolder\" + id, Kind = HitKind.App, Sub = "App" });
                }
        }

        private static List<App> settingsCache;

        private static List<App> SettingsPages()
        {
            if (settingsCache != null) return settingsCache;
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string settingsIcon = Path.Combine(sys, "ImmersiveControlPanel", "SystemSettings.exe");
            var l = new List<App>();
            void S(string name, string uri, params string[] kw) => l.Add(new App { Name = name, Launch = "ms-settings:" + uri, Icon = settingsIcon, Kind = HitKind.Setting, Sub = "Settings", Keywords = kw });
            void T(string name, string cmd, string icon, params string[] kw) => l.Add(new App { Name = name, Launch = cmd, Icon = icon, Kind = HitKind.Setting, Sub = "System tool", Keywords = kw });
            S("Display", "display", "screen", "resolution", "brightness", "scale", "monitor");
            S("Night light", "nightlight", "blue light", "warm");
            S("Sound", "sound", "audio", "volume", "speaker", "microphone");
            S("Notifications", "notifications", "focus", "do not disturb");
            S("Power & battery", "powersleep", "battery", "sleep", "power");
            S("Storage", "storagesense", "disk", "space", "cleanup");
            S("Multitasking", "multitasking", "snap", "alt tab");
            S("Clipboard", "clipboard", "history", "paste");
            S("About this PC", "about", "system info", "specs", "rename pc", "windows version");
            S("Bluetooth & devices", "bluetooth", "devices", "pair", "headphones");
            S("Printers & scanners", "printers", "printer", "scanner");
            S("Mouse", "mousetouchpad", "pointer", "cursor", "scroll");
            S("Touchpad", "devices-touchpad", "trackpad", "gestures");
            S("Wi-Fi", "network-wifi", "wifi", "wireless", "internet");
            S("Network & internet", "network", "ethernet", "proxy", "vpn");
            S("VPN", "network-vpn");
            S("Mobile hotspot", "network-mobilehotspot", "hotspot", "tethering");
            S("Airplane mode", "network-airplanemode", "flight mode");
            S("Personalization", "personalization", "theme", "colors");
            S("Background", "personalization-background", "wallpaper", "desktop picture");
            S("Colors", "personalization-colors", "dark mode", "light mode", "accent");
            S("Lock screen", "lockscreen");
            S("Taskbar", "taskbar", "start menu", "tray");
            S("Fonts", "fonts", "typeface");
            S("Installed apps", "appsfeatures", "uninstall", "programs", "remove app");
            S("Default apps", "defaultapps", "open with", "browser");
            S("Startup apps", "startupapps", "startup", "autostart", "boot");
            S("Accounts", "yourinfo", "profile", "microsoft account");
            S("Sign-in options", "signinoptions", "password", "pin", "windows hello", "fingerprint");
            S("Date & time", "dateandtime", "clock", "timezone", "time zone");
            S("Language & region", "regionlanguage", "language", "region", "keyboard layout");
            S("Typing", "typing", "autocorrect", "spell check");
            S("Gaming", "gaming-gamebar", "game bar", "game mode", "xbox");
            S("Accessibility", "easeofaccess", "narrator", "magnifier", "contrast", "text size");
            S("Privacy & security", "privacy", "permissions", "camera", "location");
            S("Windows Security", "windowsdefender", "antivirus", "defender", "firewall", "virus");
            S("Windows Update", "windowsupdate", "update", "upgrade", "patch");
            S("Recovery", "recovery", "reset this pc", "restore");
            S("Activation", "activation", "product key", "license key");
            S("Developer settings", "developers", "developer mode", "sudo");
            S("Optional features", "optionalfeatures", "features");
            T("Control Panel", "control.exe", "control.exe", "classic");
            T("Device Manager", "devmgmt.msc", "mmc.exe", "drivers", "hardware");
            T("Task Manager", "taskmgr.exe", "taskmgr.exe", "processes", "kill", "cpu", "memory");
            T("Disk Management", "diskmgmt.msc", "mmc.exe", "partitions", "format", "volumes");
            T("Registry Editor", "regedit.exe", "regedit.exe", "regedit", "registry");
            T("Services", "services.msc", "mmc.exe", "background services");
            T("Event Viewer", "eventvwr.msc", "mmc.exe", "logs", "errors");
            T("System Information", "msinfo32.exe", "msinfo32.exe", "msinfo", "hardware info");
            T("Environment Variables", "rundll32.exe sysdm.cpl,EditEnvironmentVariables", "SystemPropertiesAdvanced.exe", "path", "env");
            T("Network Connections", "ncpa.cpl", "control.exe", "adapters", "ip address");
            T("Programs and Features", "appwiz.cpl", "control.exe", "uninstall");
            T("Command Prompt", "cmd.exe", "cmd.exe", "cmd", "terminal", "shell");
            T("PowerShell", "powershell.exe", "powershell.exe", "terminal", "shell");
            settingsCache = l;
            return l;
        }

        public static List<Hit> Search(Query q, int max)
        {
            var include = q.Words.Where(w => !w.Exclude).ToList();
            if (include.Count == 0) return new List<Hit>();
            var hits = new List<Hit>();
            foreach (var a in apps)
            {
                int total = 0; bool ok = true; bool typo = false;
                foreach (var w in include)
                {
                    int s = Matcher.ScoreWord(w, a.Name, out bool t);
                    if (s <= 0 && a.Keywords != null)
                        foreach (var k in a.Keywords)
                        {
                            int ks = Matcher.ScoreWord(w, k, out bool kt);
                            if (ks > 0 && (k.Length <= w.Text.Length + 6 || k.StartsWith(w.Text, StringComparison.OrdinalIgnoreCase))) { s = Math.Max(s, ks - 120); t |= kt; }
                        }
                    if (s <= 0) { ok = false; break; }
                    total += s; typo |= t;
                }
                if (!ok) continue;
                // a weak scattered match on a long app name isn't worth showing
                if (total < 220 * include.Count) continue;
                total += (a.Kind == HitKind.App ? 260 : 120) + Usage.Bonus("app:" + a.Launch);
                hits.Add(new Hit { Kind = a.Kind, Name = a.Name, Path = a.Icon, Launch = a.Launch, Subtitle = a.Sub, Score = total, ViaTypo = typo, Index = -1 });
            }
            return hits.OrderByDescending(h => h.Score).Take(max).ToList();
        }

        /// The app or Settings page behind a usage key ("app:" + its launch string).
        public static Hit Find(string launch)
        {
            var a = apps.FirstOrDefault(x => string.Equals(x.Launch, launch, StringComparison.OrdinalIgnoreCase));
            return a == null ? null : new Hit { Kind = a.Kind, Name = a.Name, Path = a.Icon, Launch = a.Launch, Subtitle = a.Sub, Index = -1 };
        }

        public static void Launch(Hit h)
        {
            Usage.Record("app:" + h.Launch);
            string l = h.Launch;
            if (l.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + l + "\"") { UseShellExecute = true });
            else if (l.StartsWith("rundll32.exe ", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("rundll32.exe", l.Substring(13)) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(l) { UseShellExecute = true });
        }

        // ---------- icons for packaged apps ----------

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

        /// A shell item's own picture (works for shell:AppsFolder\… ids), or null.
        public static System.Windows.Media.ImageSource ShellImage(string parsingName, int px = 64)
        {
            try
            {
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var f);
                if (f.GetImage(new SIZE { cx = px, cy = px }, 0x0 /*SIIGBF_RESIZETOFIT*/, out IntPtr hbm) != 0 || hbm == IntPtr.Zero) return null;
                try
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DeleteObject(hbm); }
            }
            catch { return null; }
        }
    }
}
