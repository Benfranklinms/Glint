using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glint
{
    /// The pane beside the results: a picture, the first lines of a text file
    /// (or the lines around a grep match), a PDF page's text, a document's
    /// opening paragraphs, or a folder's contents — plus size and date.
    public static class Preview
    {
        public sealed class Content
        {
            public string Title, Kind, Meta;
            public BitmapSource Image;
            public List<string> Lines;      // text/code
            public int FirstLine = 1, MarkLine = -1;
            public string Prose;            // pdf/docx text
            public List<(string name, bool dir)> Children;
            public int ChildCount;
            public string Big;              // calculator answer
        }

        private static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "tif", "tiff", "jfif" };
        private static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        public static Content Build(Hit h, CancellationToken ct)
        {
            var c = new Content { Title = h.Name };
            switch (h.Kind)
            {
                case HitKind.Calc: c.Kind = "Calculator"; c.Big = h.Name; c.Meta = h.Subtitle + "\nEnter copies the answer"; return c;
                case HitKind.App: c.Kind = h.Subtitle ?? "App"; c.Meta = "Enter opens it"; return c;
                case HitKind.Setting: c.Kind = h.Subtitle ?? "Settings"; c.Meta = "Enter opens this page"; return c;
            }
            string path = h.Path;
            if (h.IsDir || Directory.Exists(path))
            {
                c.Kind = "Folder";
                try
                {
                    var items = new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }).Take(400).ToList();
                    c.ChildCount = items.Count;
                    c.Children = items.OrderByDescending(i => (i.Attributes & FileAttributes.Directory) != 0).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                                      .Take(14).Select(i => (i.Name, (i.Attributes & FileAttributes.Directory) != 0)).ToList();
                    c.Meta = $"{(c.ChildCount >= 400 ? "400+" : c.ChildCount.ToString())} items  ·  modified {Directory.GetLastWriteTime(path):d MMM yyyy}";
                }
                catch { c.Meta = "Can't read this folder"; }
                return c;
            }
            FileInfo fi;
            try { fi = new FileInfo(path); if (!fi.Exists) return null; } catch { return null; }
            string ext = fi.Extension.TrimStart('.');
            c.Meta = $"{Size(fi.Length)}  ·  modified {fi.LastWriteTime:d MMM yyyy, HH:mm}";
            c.Kind = ext.Length > 0 ? ext.ToUpperInvariant() + " file" : "File";
            try
            {
                if (ImageExts.Contains(ext) && fi.Length < 60L << 20)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.DecodePixelWidth = 640;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    bmp.Freeze();
                    c.Image = bmp;
                    c.Kind = $"{ext.ToUpperInvariant()} image";
                    c.Meta = $"{bmp.PixelWidth}×{bmp.PixelHeight}  ·  " + c.Meta;
                    return c;
                }
                if (ContentSearch.DocExts.Contains(ext))
                {
                    var sb = new StringBuilder();
                    int want = h.Line > 0 && h.LineLabel != null && h.LineLabel.StartsWith("page") ? h.Line : 0;
                    foreach (var (n, label, text) in ContentSearch.Lines(path, ext, true))
                    {
                        if (ct.IsCancellationRequested) return null;
                        if (text == null) break;
                        if (want > 0 && n < want) continue;
                        if (sb.Length == 0 && label != null && label.StartsWith("page")) sb.Append(char.ToUpper(label[0]) + label.Substring(1)).Append("\n\n");
                        sb.Append(text.Trim()).Append(ext.Equals("pdf", StringComparison.OrdinalIgnoreCase) ? " " : "\n\n");
                        if (sb.Length > 1600) break;
                    }
                    c.Prose = sb.Length > 1600 ? sb.ToString(0, 1600) + "…" : sb.ToString();
                    c.Kind = ext.ToUpperInvariant() + " document";
                    return c;
                }
                if (ContentSearch.TextExts.Contains(ext) || fi.Length < 512 * 1024 && LooksText(path))
                {
                    var lines = new List<string>();
                    int mark = h.Line > 0 && h.LineLabel == null ? h.Line : -1;
                    int from = mark > 0 ? Math.Max(1, mark - 8) : 1;
                    using var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, true);
                    string line; int ln = 0;
                    while ((line = sr.ReadLine()) != null && lines.Count < 40)
                    {
                        ln++;
                        if (ln < from) continue;
                        lines.Add(line.Length > 200 ? line.Substring(0, 200) : line.Replace('\t', ' '));
                    }
                    c.Lines = lines; c.FirstLine = from; c.MarkLine = mark;
                    return c;
                }
            }
            catch { }
            return c;
        }

        private static bool LooksText(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var b = new byte[2048];
                int n = fs.Read(b, 0, b.Length);
                for (int i = 0; i < n; i++) if (b[i] == 0) return false;
                return n > 0;
            }
            catch { return false; }
        }

        public static string Size(long b)
        {
            string[] u = { "bytes", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{b:N0} bytes" : $"{v:0.#} {u[i]}";
        }

        public static UIElement Render(Content c, Hit h)
        {
            var panel = new StackPanel();
            if (c.Image != null)
            {
                var img = new Image { Source = c.Image, MaxHeight = 300, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                panel.Children.Add(img);
            }
            else if (c.Big != null)
            {
                panel.Children.Add(new TextBlock { Text = c.Big, FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
            }
            else
            {
                var icon = new Image { Width = 64, Height = 64, Source = Icons.For(h), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                panel.Children.Add(icon);
            }
            if (c.Big == null)
                panel.Children.Add(new TextBlock { Text = c.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = c.Kind, FontSize = 12.5, Foreground = Theme.Sub, Margin = new Thickness(0, 2, 0, 0) });
            if (!string.IsNullOrEmpty(c.Meta))
                panel.Children.Add(new TextBlock { Text = c.Meta, FontSize = 12, Foreground = Theme.Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

            if (c.Lines != null && c.Lines.Count > 0)
            {
                var code = new TextBlock { FontFamily = Mono, FontSize = 11.5, Foreground = Theme.Text, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.NoWrap };
                for (int i = 0; i < c.Lines.Count; i++)
                {
                    int n = c.FirstLine + i;
                    if (i > 0) code.Inlines.Add(new LineBreak());
                    code.Inlines.Add(new Run($"{n,4}  ") { Foreground = Theme.Head });
                    var run = new Run(c.Lines[i]);
                    if (n == c.MarkLine) run.Background = Theme.Hl;
                    code.Inlines.Add(run);
                }
                panel.Children.Add(new Border { Child = code, ClipToBounds = true, MaxHeight = 330 });
            }
            if (!string.IsNullOrWhiteSpace(c.Prose))
                panel.Children.Add(new TextBlock { Text = c.Prose, FontSize = 12.5, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), MaxHeight = 330 });
            if (c.Children != null)
            {
                var l = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                foreach (var (name, dir) in c.Children)
                    l.Children.Add(new TextBlock { Text = (dir ? "📁  " : "      ") + name, FontSize = 12.5, Foreground = dir ? Theme.Text : Theme.Sub, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1) });
                if (c.ChildCount > c.Children.Count) l.Children.Add(new TextBlock { Text = $"and {(c.ChildCount >= 400 ? "many" : (c.ChildCount - c.Children.Count).ToString())} more", FontSize = 12, Foreground = Theme.Head, Margin = new Thickness(0, 4, 0, 0) });
                panel.Children.Add(l);
            }
            if (h.Kind == HitKind.File)
                panel.Children.Add(new TextBlock { Text = h.Path, FontSize = 11, Foreground = Theme.Head, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = 440, Focusable = false };
        }
    }
}
