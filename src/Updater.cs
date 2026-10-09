using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Glint
{
    /// Checks GitHub releases once a day; a newer Glint.exe is downloaded in
    /// the background and swapped in when you choose Restart to update.
    public static class Updater
    {
        public const string Repo = "Benfranklinms/Glint";
        public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        public static string ReadyVersion { get; private set; }
        public static event Action Ready;
        private static string Staged => Path.Combine(Settings.Dir, "update", "Glint.exe");
        private static Timer timer;
        private static int busy;

        public static void Start(Settings s)
        {
            timer?.Dispose();
            if (!s.AutoUpdate) return;
            timer = new Timer(_ => { _ = Check(); }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24));
        }

        /// Returns a short status line for the tray and Settings.
        public static async Task<string> Check()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return "Already checking…";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Glint/" + Current);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                using var doc = JsonDocument.Parse(json);
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return "Couldn't read the latest version";
                if (Normal(latest) <= Normal(Current)) return $"Glint {Short(Current)} is up to date";
                string url = null;
                foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), "Glint.exe", StringComparison.OrdinalIgnoreCase))
                        url = a.GetProperty("browser_download_url").GetString();
                if (url == null) return $"Glint {tag} is out, but its download isn't ready yet";
                Directory.CreateDirectory(Path.GetDirectoryName(Staged));
                string tmp = Staged + ".part";
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs);
                }
                if (new FileInfo(tmp).Length < 1_000_000) { File.Delete(tmp); return "The download looked wrong, will try again later"; }
                File.Move(tmp, Staged, true);
                ReadyVersion = Short(latest);
                Ready?.Invoke();
                return $"Glint {ReadyVersion} is ready: restart to update";
            }
            catch (Exception ex) { return "Update check failed: " + ex.Message; }
            finally { busy = 0; }
        }

        private static Version Normal(Version v) => new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        public static string Short(Version v) => $"{v.Major}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";

        /// Replaces the running exe with the staged one and starts it again.
        /// A tiny script waits for this process to exit, since a running exe can't be overwritten.
        public static bool ApplyAndRestart(Action shutdown)
        {
            if (!File.Exists(Staged)) return false;
            string exe = Environment.ProcessPath;
            string script = Path.Combine(Settings.Dir, "update", "apply.cmd");
            int pid = Environment.ProcessId;
            File.WriteAllText(script,
                "@echo off\r\n" +
                $":wait\r\ntasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                $"move /y \"{Staged}\" \"{exe}\" >nul\r\n" +
                $"start \"\" \"{exe}\" --relaunch\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            shutdown();
            return true;
        }
    }
}
