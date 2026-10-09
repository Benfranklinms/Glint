using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace Glint
{
    /// Light or dark colours for the bar, following Windows unless chosen in Settings.
    public static class Theme
    {
        public static bool Dark { get; private set; } = true;
        public static Brush Text, Sub, Head, Line, Sel, SelText, Hl, Chip, ChipOn, Panel, PanelSolid, Card;
        public static Color PanelColor;

        public static bool SystemIsDark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return !(k?.GetValue("AppsUseLightTheme") is int v && v == 1);
            }
            catch { return true; }
        }

        public static void Apply(string choice)
        {
            Dark = choice == "Dark" || (choice != "Light" && SystemIsDark());
            if (Dark)
            {
                Text = B(240, 240, 245); Sub = B(160, 162, 172); Head = B(140, 142, 152); Line = B(40, 255, 255, 255);
                Chip = B(28, 255, 255, 255); Card = B(18, 255, 255, 255);
                PanelColor = Color.FromRgb(0x20, 0x20, 0x26);
            }
            else
            {
                Text = B(24, 24, 28); Sub = B(96, 98, 108); Head = B(110, 112, 122); Line = B(30, 0, 0, 0);
                Chip = B(16, 0, 0, 0); Card = B(10, 0, 0, 0);
                PanelColor = Color.FromRgb(0xF7, 0xF7, 0xFA);
            }
            Sel = B(0x00, 0x78, 0xD4); SelText = B(255, 255, 255); ChipOn = B(0x00, 0x78, 0xD4);
            Hl = B(200, 255, 196, 0);
            Panel = B(0x99, PanelColor.R, PanelColor.G, PanelColor.B);
            PanelSolid = B(0xF4, PanelColor.R, PanelColor.G, PanelColor.B);
        }

        private static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        private static Brush B(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }

        static Theme() { Apply("Dark"); }
    }
}
