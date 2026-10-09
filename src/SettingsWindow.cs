using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace Glint
{
    /// Glint's settings: shortcut, look, what gets indexed, and updates.
    /// Every change is saved straight away.
    internal sealed class SettingsWindow : Window
    {
        private readonly Settings s;
        private readonly TextBlock status;
        private readonly Button restart;

        public SettingsWindow(Settings settings, Hotkey hotkey, SearchWindow bar, Action restartApp, Action relaunchElevated, Action rebuild)
        {
            s = settings;
            Title = "Glint Settings";
            Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13.5;
            Theme.Apply(s.Theme);
            Background = Theme.Dark ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x26)) : Brushes.White;
            Foreground = Theme.Text;

            var stack = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
            Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };

            void Head(string t) => stack.Children.Add(new TextBlock { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, Margin = new Thickness(0, 14, 0, 6) });
            void Note(string t) => stack.Children.Add(new TextBlock { Text = t, FontSize = 12, Foreground = Theme.Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, -2, 0, 6) });
            CheckBox Check(string t, bool on, Action<bool> set)
            {
                var c = new CheckBox { Content = new TextBlock { Text = t, Foreground = Theme.Text }, IsChecked = on, Margin = new Thickness(0, 4, 0, 4) };
                c.Checked += (o, e) => { set(true); s.Save(); };
                c.Unchecked += (o, e) => { set(false); s.Save(); };
                stack.Children.Add(c);
                return c;
            }
            ComboBox Combo(string label, string[] items, string cur, Action<string> set)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var l = new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Text };
                var cb = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var i in items) cb.Items.Add(i);
                cb.SelectedItem = cur;
                cb.SelectionChanged += (o, e) => { set((string)cb.SelectedItem); s.Save(); };
                DockPanel.SetDock(l, Dock.Left);
                row.Children.Add(l); row.Children.Add(cb);
                stack.Children.Add(row);
                return cb;
            }

            stack.Children.Add(new TextBlock { Text = "Glint " + Updater.Short(Updater.Current), FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text });

            Head("General");
            Combo("Shortcut", new[] { "Alt+Space", "Ctrl+Space", "Win+Alt+Space", "Ctrl+Alt+Space" }, s.Hotkey, v => { s.Hotkey = v; hotkey.Register(); });
            Combo("Theme", new[] { "System", "Dark", "Light" }, s.Theme, v => { s.Theme = v; bar.ApplyTheme(); });
            Check("Show a preview beside results", s.Preview, v => { s.Preview = v; bar.UpdateWidth(); });
            Note("Ctrl+P switches it from the bar.");
            Check("Find apps, Settings pages and do sums", s.Apps, v => s.Apps = v);
            bool login = false; try { login = Settings.LaunchAtLogin; } catch { }
            Check("Open at sign-in", login, v => { try { Settings.LaunchAtLogin = v; } catch { } });

            Head("Index");
            if (!Native.IsAdmin())
            {
                var b = new Button { Content = "Use fast NTFS index (restart as administrator)", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 4, 0, 4) };
                b.Click += (o, e) => { Close(); relaunchElevated(); };
                stack.Children.Add(b);
                Note("Reads each drive's file table directly: seconds to index, changes show up instantly.");
            }
            else Note("Fast NTFS index is on.");
            Check("Save the index so Glint is ready instantly", s.SaveIndex, v => { s.SaveIndex = v; if (!v) IndexCache.Delete(); });
            Check("Include USB drives", s.IncludeRemovable, v => { s.IncludeRemovable = v; NeedRestart(); });
            Check("Include network drives", s.IncludeNetwork, v => { s.IncludeNetwork = v; NeedRestart(); });
            stack.Children.Add(new TextBlock { Text = "Never show results from these folders", Foreground = Theme.Text, Margin = new Thickness(0, 10, 0, 4) });
            FolderList(stack, s.Excluded, () => { rebuildExcluded?.Invoke(); });

            Head("Inside files");
            Check("Keep a content index for instant grep:", s.ContentIndex, v => { s.ContentIndex = v; if (!v) ContentIndex.Clear(); ContentIndex.Start(s); });
            Note("Indexes text, code, PDF and Office files in the background. Leave the list empty to cover your user folder and your other drives.");
            FolderList(stack, s.ContentFolders, () => ContentIndex.Start(s));

            Head("Updates");
            Check("Check for updates automatically", s.AutoUpdate, v => { s.AutoUpdate = v; Updater.Start(s); });
            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            Button Btn(string t, Action a) { var b = new Button { Content = t, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 8) }; b.Click += (o, e) => a(); buttons.Children.Add(b); return b; }
            Btn("Check now", async () => { status.Text = "Checking…"; status.Text = await Updater.Check(); restart.Visibility = Updater.ReadyVersion != null ? Visibility.Visible : Visibility.Collapsed; });
            Btn("Rebuild index", () => { IndexCache.Delete(); rebuild(); status.Text = "Rebuilding the index…"; });
            Btn("Clear history", () => { Usage.Clear(); status.Text = "History cleared"; });
            restart = Btn("Restart Glint", () => { if (!Updater.ApplyAndRestart(restartApp)) restartApp(); });
            restart.Visibility = Updater.ReadyVersion != null ? Visibility.Visible : Visibility.Collapsed;
            stack.Children.Add(buttons);
            status = new TextBlock { Foreground = Theme.Sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = Updater.ReadyVersion != null ? $"Glint {Updater.ReadyVersion} is ready: restart to update" : "" };
            stack.Children.Add(status);

            void NeedRestart() { status.Text = "Restart Glint to apply this."; restart.Visibility = Visibility.Visible; }
            rebuildExcluded = () => { };
        }

        private Action rebuildExcluded;
        public Action ExcludedChanged { set { rebuildExcluded = value; } }

        private void FolderList(StackPanel stack, List<string> items, Action changed)
        {
            var lb = new ListBox { Height = 86, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var i in items) lb.Items.Add(i);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var add = new Button { Content = "Add folder…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
            var rem = new Button { Content = "Remove", Padding = new Thickness(10, 3, 10, 3) };
            add.Click += (o, e) =>
            {
                using var d = new WinForms.FolderBrowserDialog { ShowNewFolderButton = false };
                if (d.ShowDialog() != WinForms.DialogResult.OK) return;
                if (items.Contains(d.SelectedPath)) return;
                items.Add(d.SelectedPath); lb.Items.Add(d.SelectedPath); s.Save(); changed();
            };
            rem.Click += (o, e) =>
            {
                if (lb.SelectedItem is string p) { items.Remove(p); lb.Items.Remove(p); s.Save(); changed(); }
            };
            row.Children.Add(add); row.Children.Add(rem);
            stack.Children.Add(lb);
            stack.Children.Add(row);
        }
    }
}
