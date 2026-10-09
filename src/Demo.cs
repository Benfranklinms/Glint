using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Glint
{
    /// glint --demo <outDir> <query>...  Indexes, then shows the bar for each
    /// query and saves screenshots (plus frames of the first drop-in). Used by CI.
    internal static class Demo
    {
        public static void Run(string outDir, string[] queries)
        {
            Directory.CreateDirectory(outDir);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var index = new FileIndex();
            var settings = new Settings { SaveIndex = true, ContentIndex = true, Preview = true, Theme = "Dark" };
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            settings.ContentFolders.Add(Path.Combine(home, "Documents")); settings.ContentFolders.Add(Path.Combine(home, "Developer"));
            index.Settings = settings;
            AppIndex.Start();
            var window = new SearchWindow(index, settings) { Demo = true };
            var log = new StreamWriter(Path.Combine(outDir, "demo-log.txt")) { AutoFlush = true };
            app.Startup += async (s, e) =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    index.Start();
                    while (!index.Ready && sw.Elapsed.TotalSeconds < 240) await Task.Delay(500);
                    log.WriteLine($"index ready={index.Ready} mft={index.UsedMft} after {sw.Elapsed.TotalSeconds:0.0}s: {index.Status}");
                    await Task.Delay(1500);
                    index.SaveNow();
                    log.WriteLine($"saved index: {new FileInfo(IndexCache.FilePath).Length / 1048576.0:0.0} MB");

                    // instant start: a second index loads from the saved file
                    var sw2 = Stopwatch.StartNew();
                    var again = new FileIndex { Settings = settings };
                    again.Start();
                    while (!again.Ready && sw2.Elapsed.TotalSeconds < 120) await Task.Delay(50);
                    log.WriteLine($"reload ready={again.Ready} fromCache={again.FromCache} after {sw2.Elapsed.TotalSeconds:0.00}s: {again.Status} count={again.Count}");

                    ContentIndex.Start(settings);
                    var sw3 = Stopwatch.StartNew();
                    while (!ContentIndex.Complete && sw3.Elapsed.TotalSeconds < 300) await Task.Delay(500);
                    log.WriteLine($"content index complete={ContentIndex.Complete} after {sw3.Elapsed.TotalSeconds:0.0}s: {ContentIndex.Status}");
                    log.WriteLine($"apps ready={AppIndex.Ready}");
                    await Task.Delay(4000); // Store apps arrive a little later

                    foreach (var raw in queries)
                    {
                        string qtext = raw.StartsWith("@") ? raw.Substring(raw.IndexOf(' ') + 1) : raw;
                        var q = Query.Parse(qtext);
                        var t = Stopwatch.StartNew();
                        var names = Matcher.Search(index, q, default);
                        double ms = t.Elapsed.TotalMilliseconds;
                        var apps = AppIndex.Search(q, 3);
                        var calc = Calc.TryAnswer(qtext);
                        string line = $"Q {raw}: {ms:0.0} ms names=[{string.Join(" | ", names.Take(3).Select(h => h.Path))}] apps=[{string.Join(" | ", apps.Select(a => a.Name))}] calc={calc?.Text}";
                        if (q.Grep != null || q.Regex != null)
                        {
                            var cands = ContentIndex.Candidates(q);
                            var tc = Stopwatch.StartNew();
                            var found = new System.Collections.Concurrent.ConcurrentBag<Hit>();
                            ContentSearch.Run(index, q, names, h => found.Add(h), default);
                            line += $" index-candidates={(cands == null ? "n/a" : cands.Count().ToString())} grep={tc.Elapsed.TotalMilliseconds:0} ms hits=[{string.Join(" | ", found.Take(3).Select(h => h.Name + " " + (h.LineLabel ?? "line " + h.Line)))}]";
                        }
                        log.WriteLine(line);
                    }

                    // drop-in frames
                    var frames = Path.Combine(outDir, "dropin");
                    Directory.CreateDirectory(frames);
                    Apply(window, queries.Length > 0 ? queries[0] : "");
                    var clock = Stopwatch.StartNew();
                    window.ShowBar();
                    int n = 0;
                    while (clock.Elapsed.TotalSeconds < 0.9)
                    {
                        await Task.Delay(12);
                        GrabScreen(Path.Combine(frames, $"f{n++:D3}.png"), 0.62);
                    }
                    log.WriteLine($"dropin frames={n}");

                    for (int i = 0; i < queries.Length; i++)
                    {
                        Apply(window, queries[i]);
                        await Task.Delay(queries[i].Contains("grep:") || queries[i].Contains("regex:") ? 6000 : 2500);
                        GrabWindow(window.Handle, Path.Combine(outDir, $"{i + 1:D2}.png"));
                        log.WriteLine($"{i + 1:D2} {queries[i]}");
                    }
                    GrabScreen(Path.Combine(outDir, "fullscreen.png"), 1.0);
                }
                catch (Exception ex) { log.WriteLine(ex.ToString()); }
                finally { log.Dispose(); app.Shutdown(); }
            };
            app.Run();
        }

        /// "@Apps calc" picks the Apps chip, then types "calc".
        private static void Apply(SearchWindow w, string raw)
        {
            if (raw.StartsWith("@")) { int sp = raw.IndexOf(' '); w.SetChipByName(raw.Substring(1, sp - 1)); w.SetQuery(raw.Substring(sp + 1)); }
            else { w.SetChipByName("All"); w.SetQuery(raw); }
        }

        private static void GrabWindow(IntPtr hwnd, string file)
        {
            Native.GetWindowRect(hwnd, out var r);
            const int pad = 36;
            Save(r.Left - pad, r.Top - pad, r.Right - r.Left + 2 * pad, r.Bottom - r.Top + 2 * pad, file);
        }

        private static void GrabScreen(string file, double heightFrac)
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Save(b.Left, b.Top, b.Width, (int)(b.Height * heightFrac), file);
        }

        private static void Save(int x, int y, int w, int h, string file)
        {
            var scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            x = Math.Max(scr.Left, x); y = Math.Max(scr.Top, y);
            w = Math.Min(w, scr.Right - x); h = Math.Min(h, scr.Bottom - y);
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
            bmp.Save(file, ImageFormat.Png);
        }
    }
}
