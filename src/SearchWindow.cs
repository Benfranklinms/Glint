using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace Glint
{
    /// The Spotlight-style bar: drops in on the hotkey, searches as you type,
    /// and hides when it loses focus.
    public sealed class SearchWindow : Window
    {
        private static readonly Color Accent = Color.FromRgb(0x00, 0x78, 0xD4);
        private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(240, 240, 245)));
        private static readonly Brush SubBrush = Freeze(new SolidColorBrush(Color.FromRgb(160, 162, 172)));
        private static readonly Brush HeadBrush = Freeze(new SolidColorBrush(Color.FromRgb(140, 142, 152)));
        private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)));
        private static readonly Brush SelBrush = Freeze(new SolidColorBrush(Accent));
        private static readonly Brush HlBrush = Freeze(new SolidColorBrush(Color.FromArgb(200, 255, 196, 0)));
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");
        private const double PanelWidth = 760, MaxListHeight = 470;

        private readonly FileIndex index;
        private readonly bool acrylic;
        private readonly Border root;
        private readonly TextBox box;
        private readonly TextBlock placeholder, footer;
        private readonly StackPanel list;
        private readonly ScrollViewer scroller;
        private readonly Border divider;
        private readonly List<Row> rows = new List<Row>();
        private int selected = -1;
        private CancellationTokenSource cts;
        private IntPtr hwnd;
        private bool dragging, shown;
        private Point dragStart;
        private static string codeExe;

        private sealed class Row { public Hit Hit; public Border View; }

        public SearchWindow(FileIndex index)
        {
            this.index = index;
            acrylic = Environment.OSVersion.Version.Build >= 22621;

            Title = "Glint";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = PanelWidth + (acrylic ? 0 : 60);
            SizeToContent = SizeToContent.Height;
            Background = Brushes.Transparent;
            AllowsTransparency = !acrylic;
            FontFamily = UiFont;
            UseLayoutRounding = true;

            // search row
            var glass = new TextBlock { Text = "\uE721", FontFamily = IconFont, FontSize = 22, Foreground = SubBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 14, 0) };
            box = new TextBox
            {
                FontSize = 26, Foreground = TextBrush, CaretBrush = TextBrush, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                SelectionBrush = SelBrush,
            };
            placeholder = new TextBlock { Text = "Search files, folders and contents", FontSize = 26, Foreground = SubBrush, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), FontFamily = box.FontFamily };
            var field = new Grid { Margin = new Thickness(0, 0, 24, 0) };
            field.Children.Add(placeholder);
            field.Children.Add(box);
            var top = new DockPanel { Height = 68 };
            DockPanel.SetDock(glass, Dock.Left);
            top.Children.Add(glass);
            top.Children.Add(field);

            divider = new Border { Height = 1, Background = LineBrush, Visibility = Visibility.Collapsed };
            list = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            scroller = new ScrollViewer { Content = list, MaxHeight = MaxListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Focusable = false };
            footer = new TextBlock { FontSize = 12.5, Foreground = SubBrush, Margin = new Thickness(24, 10, 24, 12), TextTrimming = TextTrimming.CharacterEllipsis };
            var footerWrap = new StackPanel { Visibility = Visibility.Collapsed };
            footerWrap.Children.Add(new Border { Height = 1, Background = LineBrush });
            footerWrap.Children.Add(footer);

            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(divider);
            stack.Children.Add(scroller);
            stack.Children.Add(footerWrap);
            footerWrap.Tag = "footer";

            root = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(acrylic ? 0 : 18),
                Background = new SolidColorBrush(acrylic ? Color.FromArgb(0x99, 0x20, 0x20, 0x26) : Color.FromArgb(0xF2, 0x20, 0x20, 0x26)),
                BorderBrush = acrylic ? null : LineBrush,
                BorderThickness = new Thickness(acrylic ? 0 : 1),
                Margin = new Thickness(acrylic ? 0 : 30),
            };
            if (!acrylic) root.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.5, Color = Colors.Black };
            Content = root;

            box.TextChanged += (s, e) => { placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Requery(); };
            PreviewKeyDown += OnKey;
            Deactivated += (s, e) => { if (!dragging) HideBar(); };
            index.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && box.Text.Length > 0) Requery(); else if (IsVisible) ShowStatus(); }));

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                if (acrylic)
                {
                    HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                    if (!Native.TryAcrylic(hwnd)) root.Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x20, 0x20, 0x26));
                }
                else Native.TryAcrylic(hwnd);
            };
        }

        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        // ---------- show / hide with the drop-in ----------

        public void Toggle() { if (IsVisible && IsActive) HideBar(); else ShowBar(); }

        public void ShowBar()
        {
            root.Opacity = 0;
            if (!shown) { Left = -20000; Top = -20000; Show(); shown = true; }
            else if (!IsVisible) Show();
            UpdateLayout();
            Native.GetCursorPos(out var pt);
            var scr = WinForms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y)).WorkingArea;
            Native.GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left;
            int x = scr.Left + (scr.Width - w) / 2;
            int y = scr.Top + (int)(scr.Height * 0.20) - (acrylic ? 0 : 30);
            box.SelectAll();
            Native.ForceForeground(hwnd);
            Activate();
            box.Focus();
            Keyboard.Focus(box);
            DropIn(x, y, scr.Height);
            if (box.Text.Length == 0) ShowStatus(); else Requery();
        }

        /// The bar falls from a little above its spot and settles with a soft
        /// spring, fading in as it goes — about a third of a second.
        private void DropIn(int x, int y, int screenH)
        {
            double fall = Math.Max(40, screenH * 0.06);
            var clock = Stopwatch.StartNew();
            const double Duration = 0.42;
            root.Opacity = 0;
            EventHandler tick = null;
            tick = (s, e) =>
            {
                double p = Math.Min(1, clock.Elapsed.TotalSeconds / Duration);
                double spring = p >= 1 ? 1 : 1 - Math.Exp(-6 * p) * Math.Cos(9 * p);
                int yy = (int)Math.Round(y - fall + fall * spring);
                Native.SetWindowPos(hwnd, IntPtr.Zero, x, yy, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                root.Opacity = Math.Min(1, p * 2.4);
                if (p >= 1) CompositionTarget.Rendering -= tick;
            };
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, (int)(y - fall), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            CompositionTarget.Rendering += tick;
        }

        public void HideBar()
        {
            cts?.Cancel();
            Hide();
        }

        // ---------- searching ----------

        private void ShowStatus()
        {
            ClearRows();
            SetFooter(index.Ready ? index.Status + (index.UsedMft ? "" : "  ·  Tray > Use fast NTFS index for instant updates") : index.Status);
        }

        private async void Requery()
        {
            cts?.Cancel();
            var my = cts = new CancellationTokenSource();
            string text = box.Text;
            if (text.Trim().Length == 0) { ShowStatus(); return; }
            try { await Task.Delay(25, my.Token); } catch { return; }

            var q = Query.Parse(text);
            var sw = Stopwatch.StartNew();
            List<Hit> hits;
            try { hits = await Task.Run(() => Matcher.Search(index, q, my.Token), my.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { SetFooter("Search failed: " + ex.Message); return; }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (my.IsCancellationRequested) return;

            bool content = q.Grep != null || q.Regex != null;
            if (!content)
            {
                var dates = await Task.Run(() => hits.Take(30).Select(h => Describe(h)).ToList());
                if (my.IsCancellationRequested) return;
                RenderNameHits(hits, dates);
                string did = hits.Count > 0 && hits[0].ViaTypo ? $"Did you mean {Stem(hits[0].Name)}?  ·  " : "";
                SetFooter(hits.Count == 0
                    ? (index.Ready ? "No matches" : index.Status)
                    : $"{did}{hits.Count} result{(hits.Count == 1 ? "" : "s")} in {ms:0.0} ms  ·  Enter open   Ctrl+Enter show in folder   Ctrl+Shift+C copy path   drag to drop");
                return;
            }

            // content search: stream matches in as files are read
            ClearRows();
            AddHeader("Inside files");
            var pending = new List<Hit>();
            var lockObj = new object();
            int shownCount = 0;
            SetFooter("Searching inside files…");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (s, e) =>
            {
                List<Hit> batch;
                lock (lockObj) { batch = pending.ToList(); pending.Clear(); }
                foreach (var h in batch) { AddContentRow(h); shownCount++; }
                if (shownCount > 0 && selected < 0) Select(0);
            };
            timer.Start();
            var swc = Stopwatch.StartNew();
            try { await Task.Run(() => ContentSearch.Run(index, q, hits, h => { lock (lockObj) pending.Add(h); }, my.Token)); }
            catch { }
            await Task.Delay(90);
            timer.Stop();
            if (my.IsCancellationRequested) return;
            lock (lockObj) { foreach (var h in pending) { AddContentRow(h); shownCount++; } pending.Clear(); }
            if (shownCount > 0 && selected < 0) Select(0);
            int files = rows.Select(r => r.Hit.Path).Distinct().Count();
            SetFooter(shownCount == 0 ? "No matches inside files" :
                $"{shownCount} match{(shownCount == 1 ? "" : "es")} in {files} file{(files == 1 ? "" : "s")}  ·  {swc.Elapsed.TotalMilliseconds:0} ms  ·  Enter opens at the line{(FindCode() != null ? " in VS Code" : "")}");
        }

        private static string Stem(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        private static string Describe(Hit h)
        {
            try
            {
                DateTime t = h.IsDir ? Directory.GetLastWriteTime(h.Path) : File.GetLastWriteTime(h.Path);
                if (t.Year < 1990) return "";
                var today = DateTime.Today;
                if (t >= today) return "Today";
                if (t >= today.AddDays(-1)) return "Yesterday";
                if (t >= today.AddDays(-6)) return t.ToString("ddd");
                return t.Year == today.Year ? t.ToString("d MMM") : t.ToString("d MMM yyyy");
            }
            catch { return ""; }
        }

        private void RenderNameHits(List<Hit> hits, List<string> dates)
        {
            ClearRows();
            if (hits.Count == 0) return;
            AddHeader("Top hit");
            AddNameRow(hits[0], dates.Count > 0 ? dates[0] : "");
            var files = new List<int>(); var dirs = new List<int>();
            for (int i = 1; i < hits.Count; i++) (hits[i].IsDir ? dirs : files).Add(i);
            if (files.Count > 0) { AddHeader("Files"); foreach (int i in files) AddNameRow(hits[i], i < dates.Count ? dates[i] : ""); }
            if (dirs.Count > 0) { AddHeader("Folders"); foreach (int i in dirs) AddNameRow(hits[i], i < dates.Count ? dates[i] : ""); }
            Select(0);
        }

        private void ClearRows()
        {
            list.Children.Clear();
            rows.Clear();
            selected = -1;
            bool any = false;
            divider.Visibility = scroller.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetFooter(string text)
        {
            footer.Text = text;
            ((FrameworkElement)footer.Parent).Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddHeader(string text)
        {
            divider.Visibility = scroller.Visibility = Visibility.Visible;
            list.Children.Add(new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = HeadBrush, Margin = new Thickness(24, 10, 0, 6) });
        }

        private void AddNameRow(Hit h, string right)
        {
            var name = new TextBlock { FontSize = 16, FontWeight = FontWeights.Medium, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Text = h.Name;
            var sub = new TextBlock { Text = ShortPath(Path.GetDirectoryName(h.Path) ?? h.Path), FontSize = 12.5, Foreground = SubBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(name); text.Children.Add(sub);
            AddRow(h, text, right, 54);
        }

        private void AddContentRow(Hit h)
        {
            divider.Visibility = scroller.Visibility = Visibility.Visible;
            var head = new TextBlock { FontSize = 15, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            head.Inlines.Add(new Run(h.Name) { FontWeight = FontWeights.SemiBold });
            head.Inlines.Add(new Run("  line " + h.Line) { Foreground = SubBrush, FontSize = 12.5 });
            string snip = h.Snippet ?? "";
            int lead = snip.Length - snip.TrimStart().Length;
            snip = snip.TrimStart();
            int ms = Math.Max(0, h.MatchStart - lead), ml = Math.Max(0, Math.Min(h.MatchLength, snip.Length - ms));
            var code = new TextBlock { FontFamily = Mono, FontSize = 13, Foreground = SubBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
            if (ms <= snip.Length)
            {
                code.Inlines.Add(new Run(snip.Substring(0, ms)));
                code.Inlines.Add(new Run(snip.Substring(ms, ml)) { Background = HlBrush, Foreground = Brushes.Black });
                code.Inlines.Add(new Run(snip.Substring(ms + ml)));
            }
            var sub = new TextBlock { Text = ShortPath(Path.GetDirectoryName(h.Path) ?? ""), FontSize = 11.5, Foreground = SubBrush, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(head); text.Children.Add(code); text.Children.Add(sub);
            AddRow(h, text, "", 82);
        }

        private void AddRow(Hit h, FrameworkElement text, string right, double height)
        {
            var icon = new Image { Width = 32, Height = 32, Source = Icons.For(h.Path, h.IsDir), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 14, 0) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var r = new TextBlock { Text = right, FontSize = 12.5, Foreground = SubBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 16, 0) };
            var dock = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(r, Dock.Right);
            dock.Children.Add(icon); dock.Children.Add(r); dock.Children.Add(text);
            var view = new Border { Child = dock, Height = height, CornerRadius = new CornerRadius(10), Margin = new Thickness(10, 0, 10, 0), Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            var row = new Row { Hit = h, View = view };
            int n = rows.Count;
            view.MouseLeftButtonDown += (s, e) =>
            {
                Select(n);
                dragStart = e.GetPosition(this);
                if (e.ClickCount == 2) { Open(h, false); e.Handled = true; }
            };
            view.MouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - dragStart.X) < 6 && Math.Abs(p.Y - dragStart.Y) < 6) return;
                StartDrag(h);
            };
            rows.Add(row);
            list.Children.Add(view);
        }

        private static string ShortPath(string p)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (p.StartsWith(home, StringComparison.OrdinalIgnoreCase)) return "~" + p.Substring(home.Length);
            return p;
        }

        private void Select(int i)
        {
            if (rows.Count == 0) { selected = -1; return; }
            i = Math.Clamp(i, 0, rows.Count - 1);
            if (selected >= 0 && selected < rows.Count) Paint(rows[selected], false);
            selected = i;
            Paint(rows[i], true);
            rows[i].View.BringIntoView();
        }

        private static void Paint(Row r, bool on)
        {
            r.View.Background = on ? SelBrush : Brushes.Transparent;
            foreach (var tb in FindAll<TextBlock>(r.View))
                if (tb.Foreground == SubBrush || tb.Tag as string == "sub") { tb.Tag = "sub"; tb.Foreground = on ? Brushes.Gainsboro : SubBrush; }
        }

        private static IEnumerable<T> FindAll<T>(DependencyObject d) where T : DependencyObject
        {
            foreach (var o in LogicalTreeHelper.GetChildren(d))
            {
                if (!(o is DependencyObject c)) continue;
                if (c is T t) yield return t;
                foreach (var x in FindAll<T>(c)) yield return x;
            }
        }

        // ---------- keys and actions ----------

        private void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            Hit cur = selected >= 0 && selected < rows.Count ? rows[selected].Hit : null;
            switch (e.Key)
            {
                case Key.Escape:
                    if (box.Text.Length > 0) box.Clear(); else HideBar();
                    e.Handled = true; break;
                case Key.Down: Select(selected + 1); e.Handled = true; break;
                case Key.Up: Select(selected - 1); e.Handled = true; break;
                case Key.PageDown: Select(selected + 8); e.Handled = true; break;
                case Key.PageUp: Select(selected - 8); e.Handled = true; break;
                case Key.Enter:
                    if (cur != null) Open(cur, ctrl);
                    e.Handled = true; break;
                case Key.C when ctrl && cur != null && (shift || box.SelectionLength == 0):
                    try
                    {
                        if (shift) Clipboard.SetText(cur.Path);
                        else { var sc = new System.Collections.Specialized.StringCollection { cur.Path }; Clipboard.SetFileDropList(sc); }
                        SetFooter(shift ? "Path copied" : "Copied — paste it into any folder or chat");
                    }
                    catch { }
                    e.Handled = true; break;
            }
        }

        private void Open(Hit h, bool reveal)
        {
            try
            {
                if (reveal) Process.Start("explorer.exe", $"/select,\"{h.Path}\"");
                else if (h.Line > 0 && FindCode() is string code)
                    Process.Start(new ProcessStartInfo(code, $"-g \"{h.Path}:{h.Line}\"") { UseShellExecute = false, CreateNoWindow = true });
                else Process.Start(new ProcessStartInfo(h.Path) { UseShellExecute = true });
                HideBar();
            }
            catch (Exception ex) { SetFooter("Couldn't open: " + ex.Message); }
        }

        private static string FindCode()
        {
            if (codeExe != null) return codeExe.Length == 0 ? null : codeExe;
            codeExe = "";
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "code.cmd");
                    if (File.Exists(p)) { codeExe = p; break; }
                }
                catch { }
            }
            return codeExe.Length == 0 ? null : codeExe;
        }

        /// Drag a result out to Explorer, the desktop, a chat or an upload box.
        private void StartDrag(Hit h)
        {
            if (dragging || !File.Exists(h.Path) && !Directory.Exists(h.Path)) return;
            dragging = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { h.Path });
                DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch { }
            finally { dragging = false; }
            if (!IsActive) HideBar();
        }
    }

    /// Shell icons, cached by extension (and per file for exe/lnk/ico).
    internal static class Icons
    {
        private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource For(string path, bool dir)
        {
            string ext = dir ? "<dir>" : Path.GetExtension(path);
            bool perFile = !dir && (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase));
            string key = perFile ? path : ext;
            if (Cache.TryGetValue(key, out var img)) return img;
            img = Load(perFile ? path : (dir ? "folder" : "file" + ext), dir, !perFile);
            Cache[key] = img;
            return img;
        }

        private static ImageSource Load(string path, bool dir, bool byType)
        {
            try
            {
                var info = new Native.SHFILEINFO();
                uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON | (byType ? Native.SHGFI_USEFILEATTRIBUTES : 0);
                Native.SHGetFileInfo(path, dir ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info), flags);
                if (info.hIcon == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                Native.DestroyIcon(info.hIcon);
                src.Freeze();
                return src;
            }
            catch { return null; }
        }
    }
}
