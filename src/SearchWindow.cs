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
    /// The Spotlight-style bar: drops in on the hotkey, searches files, apps,
    /// Settings and sums as you type, previews the selection, and hides when
    /// it loses focus.
    public sealed class SearchWindow : Window
    {
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");
        private const double ListWidth = 760, PreviewWidth = 360, MaxListHeight = 470;

        private static readonly (string label, string filter)[] Chips =
        {
            ("All", ""), ("Apps", "@apps"), ("Files", "type:file"), ("Folders", "type:dir"),
            ("Documents", "kind:doc"), ("Code", "kind:code"), ("Images", "kind:image"),
        };

        private readonly FileIndex index;
        private readonly Settings settings;
        private readonly bool acrylic;
        private readonly Border root;
        private readonly TextBox box;
        private readonly TextBlock placeholder, footer, glass;
        private readonly StackPanel list, chipRow;
        private readonly ScrollViewer scroller;
        private readonly Border divider, previewHost;
        private readonly Grid body;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<Border> chipViews = new List<Border>();
        private int selected = -1, chip;
        private CancellationTokenSource cts, previewCts;
        private IntPtr hwnd;
        private bool dragging, shown, darkApplied;
        private Point dragStart;
        private static string codeExe;

        public Action OpenSettings;

        private sealed class Row { public Hit Hit; public Border View; }

        public SearchWindow(FileIndex index, Settings settings)
        {
            this.index = index;
            this.settings = settings;
            acrylic = Environment.OSVersion.Version.Build >= 22621;
            Theme.Apply(settings.Theme);

            Title = "Glint";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            Background = Brushes.Transparent;
            AllowsTransparency = !acrylic;
            FontFamily = UiFont;
            UseLayoutRounding = true;

            // search row
            glass = new TextBlock { Text = "\uE721", FontFamily = IconFont, FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 14, 0) };
            box = new TextBox
            {
                FontSize = 26, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            };
            placeholder = new TextBlock { Text = "Search files, apps, settings and contents", FontSize = 26, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), FontFamily = box.FontFamily };
            var field = new Grid { Margin = new Thickness(0, 0, 24, 0) };
            field.Children.Add(placeholder);
            field.Children.Add(box);
            var top = new DockPanel { Height = 68 };
            DockPanel.SetDock(glass, Dock.Left);
            top.Children.Add(glass);
            top.Children.Add(field);

            // filter chips
            chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 20, 10) };
            for (int i = 0; i < Chips.Length; i++)
            {
                int n = i;
                var t = new TextBlock { Text = Chips[i].label, FontSize = 12.5, FontWeight = FontWeights.Medium };
                var b = new Border { Child = t, CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 3, 11, 4), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand };
                b.MouseLeftButtonDown += (s, e) => { SetChip(n); e.Handled = true; };
                chipViews.Add(b);
                chipRow.Children.Add(b);
            }

            divider = new Border { Height = 1, Visibility = Visibility.Collapsed };
            list = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            scroller = new ScrollViewer { Content = list, MaxHeight = MaxListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            previewHost = new Border { Width = PreviewWidth, Margin = new Thickness(0, 10, 12, 10), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Visibility = Visibility.Collapsed };
            body = new Grid { Visibility = Visibility.Collapsed };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(previewHost, 1);
            body.Children.Add(scroller);
            body.Children.Add(previewHost);

            footer = new TextBlock { FontSize = 12.5, Margin = new Thickness(24, 10, 24, 12), TextTrimming = TextTrimming.CharacterEllipsis };
            var footerWrap = new StackPanel { Visibility = Visibility.Collapsed };
            footerWrap.Children.Add(new Border { Height = 1, Tag = "line" });
            footerWrap.Children.Add(footer);

            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(chipRow);
            stack.Children.Add(divider);
            stack.Children.Add(body);
            stack.Children.Add(footerWrap);

            root = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(acrylic ? 0 : 18),
                BorderThickness = new Thickness(acrylic ? 0 : 1),
                Margin = new Thickness(acrylic ? 0 : 30),
            };
            if (!acrylic) root.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.5, Color = Colors.Black };
            Content = root;
            ApplyTheme();
            UpdateWidth();

            box.TextChanged += (s, e) => { placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Requery(); };
            PreviewKeyDown += OnKey;
            Deactivated += (s, e) => { if (!dragging && !Demo) HideBar(); };
            index.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && box.Text.Length > 0) Requery(); else if (IsVisible) ShowStatus(); }));

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                if (acrylic)
                {
                    HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                    if (!Native.TryAcrylic(hwnd, Theme.Dark)) root.Background = Theme.PanelSolid;
                }
                else Native.TryAcrylic(hwnd, Theme.Dark);
                darkApplied = Theme.Dark;
            };
        }

        /// Re-reads the theme (Windows may have switched) and repaints the chrome.
        public void ApplyTheme()
        {
            Theme.Apply(settings.Theme);
            root.Background = acrylic ? Theme.Panel : Theme.PanelSolid;
            root.BorderBrush = acrylic ? null : Theme.Line;
            box.Foreground = Theme.Text; box.CaretBrush = Theme.Text; box.SelectionBrush = Theme.Sel;
            placeholder.Foreground = Theme.Sub; glass.Foreground = Theme.Sub; footer.Foreground = Theme.Sub;
            divider.Background = Theme.Line;
            previewHost.Background = Theme.Card;
            if (footer.Parent is StackPanel fw && fw.Children[0] is Border l) l.Background = Theme.Line;
            PaintChips();
            if (hwnd != IntPtr.Zero && darkApplied != Theme.Dark)
            {
                if (!Native.TryAcrylic(hwnd, Theme.Dark) && acrylic) root.Background = Theme.PanelSolid;
                darkApplied = Theme.Dark;
            }
        }

        private double screenDip = 4000;

        public void UpdateWidth()
        {
            double want = ListWidth + (settings.Preview ? PreviewWidth + 12 : 0) + (acrylic ? 0 : 60);
            // on a small screen the bar shrinks rather than running off the edge
            Width = Math.Min(want, Math.Max(560, screenDip - 32));
            if (!settings.Preview) previewHost.Visibility = Visibility.Collapsed;
        }

        private void PaintChips()
        {
            for (int i = 0; i < chipViews.Count; i++)
            {
                bool on = i == chip;
                chipViews[i].Background = on ? Theme.ChipOn : Theme.Chip;
                ((TextBlock)chipViews[i].Child).Foreground = on ? Theme.SelText : Theme.Sub;
            }
        }

        private void SetChip(int n)
        {
            chip = (n + Chips.Length) % Chips.Length;
            PaintChips();
            Requery();
            box.Focus();
        }

        // ---------- show / hide with the drop-in ----------

        /// Demo mode keeps the bar up when focus leaves (used for screenshots).
        public bool Demo { get; set; }
        public IntPtr Handle => hwnd;
        public void SetQuery(string q) { box.Text = q; box.CaretIndex = q.Length; }
        public void SetChipByName(string name) { int i = Array.FindIndex(Chips, c => c.label.Equals(name, StringComparison.OrdinalIgnoreCase)); SetChip(i < 0 ? 0 : i); }

        public void Toggle() { if (IsVisible && IsActive) HideBar(); else ShowBar(); }

        public void ShowBar()
        {
            ApplyTheme();
            root.Opacity = 0;
            if (!shown) { Left = -20000; Top = -20000; Show(); shown = true; }
            else if (!IsVisible) Show();
            UpdateLayout();
            Native.GetCursorPos(out var pt);
            var scr = WinForms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y)).WorkingArea;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(scr.Width / scale - screenDip) > 1) { screenDip = scr.Width / scale; UpdateWidth(); UpdateLayout(); }
            Native.GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left;
            int x = scr.Left + (scr.Width - w) / 2;
            int y = scr.Top + (int)(scr.Height * 0.18) - (acrylic ? 0 : 30);
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
            previewCts?.Cancel();
            Hide();
        }

        // ---------- searching ----------

        /// Empty bar: what you open most, then the index status.
        private void ShowStatus()
        {
            ClearRows();
            var recent = new List<Hit>();
            foreach (var key in Usage.Top(12))
            {
                if (recent.Count >= 6) break;
                if (key.StartsWith("app:")) { var a = AppIndex.Find(key.Substring(4)); if (a != null) recent.Add(a); continue; }
                bool dir = Directory.Exists(key);
                if (dir || File.Exists(key)) recent.Add(new Hit { Kind = HitKind.File, Name = Path.GetFileName(key.TrimEnd('\\')), Path = key, IsDir = dir, Index = -1 });
            }
            if (recent.Count > 0 && chip == 0)
            {
                AddHeader("Recent");
                foreach (var h in recent) AddHitRow(h, "");
                Select(0);
            }
            string tip = index.UsedMft ? "" : "  ·  Tray > Use fast NTFS index for instant updates";
            string content = settings.ContentIndex && ContentIndex.Status.Length > 0 ? "  ·  " + ContentIndex.Status : "";
            SetFooter(index.Ready ? index.Status + content + tip : index.Status);
        }

        private Query ParseWithChip(string text, out bool appsOnly)
        {
            string f = Chips[chip].filter;
            appsOnly = f == "@apps";
            return Query.Parse(appsOnly || f.Length == 0 ? text : text + " " + f);
        }

        private async void Requery()
        {
            cts?.Cancel();
            var my = cts = new CancellationTokenSource();
            string text = box.Text;
            if (text.Trim().Length == 0) { ShowStatus(); return; }
            try { await Task.Delay(25, my.Token); } catch { return; }

            var q = ParseWithChip(text, out bool appsOnly);
            bool content = q.Grep != null || q.Regex != null;
            bool plain = q.Exts == null && q.In == null && q.DirsOnly == null && !content && q.PathContains == null;
            var calc = settings.Apps && chip == 0 && !content ? Calc.TryAnswer(text) : null;
            var apps = settings.Apps && (appsOnly || (chip == 0 && plain)) ? AppIndex.Search(q, appsOnly ? 30 : 4) : new List<Hit>();

            var sw = Stopwatch.StartNew();
            List<Hit> hits;
            try { hits = appsOnly ? new List<Hit>() : await Task.Run(() => Matcher.Search(index, q, my.Token), my.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { SetFooter("Search failed: " + ex.Message); return; }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (my.IsCancellationRequested) return;

            if (!content)
            {
                var dates = await Task.Run(() => hits.Take(30).Select(h => Describe(h)).ToList());
                if (my.IsCancellationRequested) return;
                RenderHits(calc, apps, hits, dates);
                string did = hits.Count > 0 && hits[0].ViaTypo && (apps.Count == 0 || hits[0].Score > apps[0].Score) ? $"Did you mean {Stem(hits[0].Name)}?  ·  " : "";
                int total = hits.Count + apps.Count;
                SetFooter(calc != null && total == 0 ? "Enter copies the answer" :
                    total == 0 ? (index.Ready ? "No matches" : index.Status)
                    : appsOnly ? $"{total} result{(total == 1 ? "" : "s")}  ·  Enter open   Tab filters   Ctrl+, settings"
                    : $"{did}{total} result{(total == 1 ? "" : "s")} in {ms:0.0} ms  ·  Enter open   Ctrl+Enter show in folder   Tab filters   Ctrl+, settings");
                return;
            }

            // content search: stream matches in as files are read
            ClearRows();
            AddHeader("Inside files");
            var pending = new List<Hit>();
            var lockObj = new object();
            int shownCount = 0;
            bool viaIndex = ContentIndex.Complete && q.Words.Count == 0;
            SetFooter(viaIndex ? "Searching the content index…" : "Searching inside files…");
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
                $"{shownCount} match{(shownCount == 1 ? "" : "es")} in {files} file{(files == 1 ? "" : "s")}  ·  {swc.Elapsed.TotalMilliseconds:0} ms{(viaIndex ? " (indexed)" : "")}  ·  Enter opens at the line{(FindCode() != null ? " in VS Code" : "")}");
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

        private void RenderHits(Calc.Answer calc, List<Hit> apps, List<Hit> hits, List<string> dates)
        {
            ClearRows();
            string DateOf(Hit h) { int i = hits.IndexOf(h); return i >= 0 && i < dates.Count ? dates[i] : ""; }
            if (calc != null)
            {
                AddHeader("Calculator");
                AddHitRow(new Hit { Kind = HitKind.Calc, Name = calc.Text, Subtitle = calc.Hint, Launch = calc.Value, Index = -1 }, "Enter to copy");
            }
            if (apps.Count == 0 && hits.Count == 0) { if (rows.Count > 0) Select(0); return; }

            // the single best thing, app or file, goes first
            Hit topHit = null;
            if (apps.Count > 0 && (hits.Count == 0 || apps[0].Score >= hits[0].Score)) topHit = apps[0];
            else if (hits.Count > 0) topHit = hits[0];
            AddHeader("Top hit");
            AddHitRow(topHit, topHit.Kind == HitKind.File ? DateOf(topHit) : topHit.Subtitle);

            var restApps = apps.Where(a => a != topHit).ToList();
            if (restApps.Count > 0) { AddHeader("Apps & settings"); foreach (var a in restApps) AddHitRow(a, a.Subtitle); }
            var files = hits.Where(h => h != topHit && !h.IsDir).ToList();
            var dirs = hits.Where(h => h != topHit && h.IsDir).ToList();
            if (files.Count > 0) { AddHeader("Files"); foreach (var h in files) AddHitRow(h, DateOf(h)); }
            if (dirs.Count > 0) { AddHeader("Folders"); foreach (var h in dirs) AddHitRow(h, DateOf(h)); }
            Select(calc != null && topHit != null ? 1 : 0);
        }

        private void ClearRows()
        {
            list.Children.Clear();
            rows.Clear();
            selected = -1;
            divider.Visibility = body.Visibility = Visibility.Collapsed;
            previewHost.Visibility = Visibility.Collapsed;
            previewCts?.Cancel();
        }

        private void SetFooter(string text)
        {
            footer.Text = text;
            ((FrameworkElement)footer.Parent).Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddHeader(string text)
        {
            divider.Visibility = body.Visibility = Visibility.Visible;
            list.Children.Add(new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Head, Margin = new Thickness(24, 10, 0, 6) });
        }

        private void AddHitRow(Hit h, string right)
        {
            var name = new TextBlock { FontSize = 16, FontWeight = FontWeights.Medium, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "main" };
            name.Text = h.Name;
            string subText = h.Kind == HitKind.File ? ShortPath(Path.GetDirectoryName(h.Path) ?? h.Path) : h.Subtitle;
            if (h.Kind == HitKind.Calc) { name.FontSize = 20; name.FontWeight = FontWeights.SemiBold; }
            var sub = new TextBlock { Text = subText, FontSize = 12.5, Foreground = Theme.Sub, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Tag = "sub" };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(name); text.Children.Add(sub);
            AddRow(h, text, right, h.Kind == HitKind.Calc ? 60 : 54);
        }

        private void AddContentRow(Hit h)
        {
            divider.Visibility = body.Visibility = Visibility.Visible;
            var head = new TextBlock { FontSize = 15, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "main" };
            head.Inlines.Add(new Run(h.Name) { FontWeight = FontWeights.SemiBold });
            head.Inlines.Add(new Run("  " + (h.LineLabel ?? "line " + h.Line)) { FontSize = 12.5 });
            string snip = h.Snippet ?? "";
            int lead = snip.Length - snip.TrimStart().Length;
            snip = snip.TrimStart();
            int ms = Math.Max(0, h.MatchStart - lead), ml = Math.Max(0, Math.Min(h.MatchLength, snip.Length - ms));
            var code = new TextBlock { FontFamily = Mono, FontSize = 13, Foreground = Theme.Sub, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), Tag = "sub" };
            if (ms <= snip.Length)
            {
                code.Inlines.Add(new Run(snip.Substring(0, ms)));
                code.Inlines.Add(new Run(snip.Substring(ms, ml)) { Background = Theme.Hl, Foreground = Brushes.Black });
                code.Inlines.Add(new Run(snip.Substring(ms + ml)));
            }
            var sub = new TextBlock { Text = ShortPath(Path.GetDirectoryName(h.Path) ?? ""), FontSize = 11.5, Foreground = Theme.Sub, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Tag = "sub" };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(head); text.Children.Add(code); text.Children.Add(sub);
            AddRow(h, text, "", 82);
        }

        private void AddRow(Hit h, FrameworkElement text, string right, double height)
        {
            FrameworkElement icon;
            if (h.Kind == HitKind.Calc)
                icon = new TextBlock { Text = "\uE8EF", FontFamily = IconFont, FontSize = 24, Width = 32, TextAlignment = TextAlignment.Center, Foreground = Theme.Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 14, 0), Tag = "sub" };
            else
            {
                var img = new Image { Width = 32, Height = 32, Source = Icons.For(h), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 14, 0) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                icon = img;
            }
            var r = new TextBlock { Text = right ?? "", FontSize = 12.5, Foreground = Theme.Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 16, 0), Tag = "sub" };
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
                if (e.LeftButton != MouseButtonState.Pressed || h.Kind != HitKind.File) return;
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
            ShowPreview(rows[i].Hit);
        }

        private static void Paint(Row r, bool on)
        {
            r.View.Background = on ? Theme.Sel : Brushes.Transparent;
            foreach (var tb in FindAll<TextBlock>(r.View))
            {
                if (tb.Tag as string == "main") tb.Foreground = on ? Theme.SelText : Theme.Text;
                else if (tb.Tag as string == "sub") tb.Foreground = on ? new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)) : Theme.Sub;
            }
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

        // ---------- preview pane ----------

        private async void ShowPreview(Hit h)
        {
            previewCts?.Cancel();
            if (!settings.Preview || h == null) { previewHost.Visibility = Visibility.Collapsed; return; }
            var my = previewCts = new CancellationTokenSource();
            try { await Task.Delay(70, my.Token); } catch { return; }
            Preview.Content content;
            try { content = await Task.Run(() => Preview.Build(h, my.Token), my.Token); }
            catch { return; }
            if (my.IsCancellationRequested || content == null) return;
            previewHost.Child = Preview.Render(content, h);
            previewHost.Visibility = Visibility.Visible;
        }

        // ---------- keys and actions ----------

        private void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            Hit cur = selected >= 0 && selected < rows.Count ? rows[selected].Hit : null;
            switch (e.Key)
            {
                case Key.Escape:
                    if (box.Text.Length > 0) box.Clear(); else if (chip != 0) SetChip(0); else HideBar();
                    e.Handled = true; break;
                case Key.Down: Select(selected + 1); e.Handled = true; break;
                case Key.Up: Select(selected - 1); e.Handled = true; break;
                case Key.PageDown: Select(selected + 8); e.Handled = true; break;
                case Key.PageUp: Select(selected - 8); e.Handled = true; break;
                case Key.Tab: SetChip(chip + (shift ? -1 : 1)); e.Handled = true; break;
                case Key.D1: case Key.D2: case Key.D3: case Key.D4: case Key.D5: case Key.D6: case Key.D7:
                    if (ctrl) { SetChip(e.Key - Key.D1); e.Handled = true; }
                    break;
                case Key.OemComma when ctrl:
                    HideBar(); OpenSettings?.Invoke(); e.Handled = true; break;
                case Key.P when ctrl:
                    settings.Preview = !settings.Preview; settings.Save(); UpdateWidth();
                    if (settings.Preview && cur != null) ShowPreview(cur);
                    e.Handled = true; break;
                case Key.Enter:
                    if (cur != null) Open(cur, ctrl);
                    e.Handled = true; break;
                case Key.C when ctrl && cur != null && (shift || box.SelectionLength == 0):
                    try
                    {
                        if (cur.Kind == HitKind.Calc) { Clipboard.SetText(cur.Launch); SetFooter("Answer copied"); }
                        else if (cur.Kind != HitKind.File) { }
                        else if (shift) { Clipboard.SetText(cur.Path); SetFooter("Path copied"); }
                        else { var sc = new System.Collections.Specialized.StringCollection { cur.Path }; Clipboard.SetFileDropList(sc); SetFooter("Copied — paste it into any folder or chat"); }
                    }
                    catch { }
                    e.Handled = true; break;
            }
        }

        private void Open(Hit h, bool reveal)
        {
            try
            {
                switch (h.Kind)
                {
                    case HitKind.Calc:
                        Clipboard.SetText(h.Launch);
                        SetFooter($"Copied {h.Launch}");
                        return;
                    case HitKind.App:
                    case HitKind.Setting:
                        AppIndex.Launch(h);
                        HideBar();
                        return;
                }
                if (reveal) Process.Start("explorer.exe", $"/select,\"{h.Path}\"");
                else if (h.Line > 0 && h.LineLabel == null && FindCode() is string code)
                    Process.Start(new ProcessStartInfo(code, $"-g \"{h.Path}:{h.Line}\"") { UseShellExecute = false, CreateNoWindow = true });
                else Process.Start(new ProcessStartInfo(h.Path) { UseShellExecute = true });
                Usage.Record(h.Path);
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

        public static ImageSource For(Hit h)
        {
            if (h.Kind == HitKind.App && h.Path != null && h.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                if (Cache.TryGetValue(h.Path, out var c)) return c;
                var img = AppIndex.ShellImage(h.Path, 64) ?? For("app.exe", false);
                Cache[h.Path] = img;
                return img;
            }
            if (h.Kind == HitKind.Setting && h.Path != null && !Path.IsPathRooted(h.Path))
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                return For(Path.Combine(sys, h.Path), false);
            }
            return For(h.Path, h.IsDir);
        }

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
