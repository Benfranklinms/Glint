using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
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
            var window = new SearchWindow(index) { Demo = true };
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

                    // drop-in frames
                    var frames = Path.Combine(outDir, "dropin");
                    Directory.CreateDirectory(frames);
                    window.SetQuery(queries.Length > 0 ? queries[0] : "");
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
                        window.SetQuery(queries[i]);
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
