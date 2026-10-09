using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Glint
{
    public sealed class Settings
    {
        public string Hotkey { get; set; } = "Alt+Space";   // or "Ctrl+Space", "Win+Alt+Space"
        public bool Welcomed { get; set; }

        private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static Settings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string Exe => Environment.ProcessPath;

        /// Elevated: a logon task with highest privileges, so the fast NTFS index
        /// works at every sign-in without a UAC prompt. Otherwise a Run key.
        public static bool LaunchAtLogin
        {
            get
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                if (k?.GetValue("Glint") != null) return true;
                return Schtasks("/query /tn Glint") == 0;
            }
            set
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (!value)
                {
                    k.DeleteValue("Glint", false);
                    if (Native.IsAdmin()) Schtasks("/delete /tn Glint /f");
                    return;
                }
                if (Native.IsAdmin() && Schtasks($"/create /tn Glint /tr \"\\\"{Exe}\\\"\" /sc onlogon /rl highest /f") == 0)
                    k.DeleteValue("Glint", false);
                else
                    k.SetValue("Glint", "\"" + Exe + "\"");
            }
        }

        /// Once Glint runs elevated, swap a plain Run-key start for the logon
        /// task, so the fast index keeps working at every sign-in.
        public static void UpgradeLoginIfElevated()
        {
            if (!Native.IsAdmin()) return;
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue("Glint") != null) LaunchAtLogin = true;
        }

        private static int Schtasks(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                p.WaitForExit(5000);
                return p.ExitCode;
            }
            catch { return -1; }
        }
    }
}
