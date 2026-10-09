// Reads the Windows theme (light/dark) and accent color; provides the Fluent icon font.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace MiniAudio
{
    class Theme
    {
        public bool Dark;
        public Color Bg, Surface, SurfaceStrong, Border, Text, SubText, Accent, OnAccent, Track, Danger;

        public static bool? ForceDark; // --snapshot only

        public static Theme Current()
        {
            var t = new Theme();
            t.Dark = ForceDark ?? ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
            Color accentLight = Color.FromArgb(0x4C, 0xC2, 0xFF), accentDark = Color.FromArgb(0x00, 0x5F, 0xB8);
            ReadAccentPalette(ref accentLight, ref accentDark);
            if (t.Dark)
            {
                t.Bg = Color.FromArgb(0x2B, 0x2B, 0x2B);
                t.Surface = Color.FromArgb(0x35, 0x35, 0x35);
                t.SurfaceStrong = Color.FromArgb(0x40, 0x40, 0x40);
                t.Border = Color.FromArgb(0x45, 0x45, 0x45);
                t.Text = Color.FromArgb(0xF5, 0xF5, 0xF5);
                t.SubText = Color.FromArgb(0xA8, 0xA8, 0xA8);
                t.Accent = accentLight;
                t.OnAccent = Color.FromArgb(0x10, 0x10, 0x10);
                t.Track = Color.FromArgb(0x5A, 0x5A, 0x5A);
                t.Danger = Color.FromArgb(0xFF, 0x99, 0xA4);
            }
            else
            {
                t.Bg = Color.FromArgb(0xF7, 0xF7, 0xF7);
                t.Surface = Color.FromArgb(0xEC, 0xEC, 0xEC);
                t.SurfaceStrong = Color.FromArgb(0xE0, 0xE0, 0xE0);
                t.Border = Color.FromArgb(0xDD, 0xDD, 0xDD);
                t.Text = Color.FromArgb(0x1A, 0x1A, 0x1A);
                t.SubText = Color.FromArgb(0x5F, 0x5F, 0x5F);
                t.Accent = accentDark;
                t.OnAccent = Color.White;
                t.Track = Color.FromArgb(0xB8, 0xB8, 0xB8);
                t.Danger = Color.FromArgb(0xC4, 0x2B, 0x1C);
            }
            return t;
        }

        public static bool TaskbarLight()
        {
            return ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) != 0;
        }

        // AccentPalette: 8 colors × RGBA. 1 = light shade (dark theme), 4 = dark shade (light theme)
        static void ReadAccentPalette(ref Color light, ref Color dark)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    if (p == null || p.Length < 32) return;
                    light = Color.FromArgb(p[4], p[5], p[6]);
                    dark = Color.FromArgb(p[16], p[17], p[18]);
                }
            }
            catch { }
        }

        static int ReadDword(string path, string name, int fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(path))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : fallback;
                }
            }
            catch { return fallback; }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, path);
        }

        public static void FillCircle(Graphics g, Color c, float cx, float cy, float radius)
        {
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, cx - radius, cy - radius, radius * 2, radius * 2);
        }
    }

    static class Glyphs
    {
        public const string Volume0 = "", Volume1 = "", Volume2 = "", Volume3 = "";
        public const string Mute = "", Headphones = "", Speakers = "", Monitor = "";
        public const string Microphone = "", MicOff = "", Settings = "", Headset = "";

        static string family;

        public static Font Font(float px)
        {
            if (family == null)
            {
                family = "Segoe MDL2 Assets";
                using (var f = new Font("Segoe Fluent Icons", 10f))
                    if (f.Name == "Segoe Fluent Icons") family = f.Name; // Windows 11
            }
            return new Font(family, px, GraphicsUnit.Pixel);
        }

        public static string ForDevice(AudioDevice d)
        {
            if (d == null) return Speakers;
            if (d.Flow == EDataFlow.Capture) return Microphone;
            switch (d.FormFactor)
            {
                case FormFactor.Headphones: return Headphones;
                case FormFactor.Headset: return Headset;
                case FormFactor.DisplayAudio: return Monitor;
                default: return Speakers;
            }
        }

        public static string ForVolume(AudioDevice d, float volume, bool muted)
        {
            if (d != null && d.Flow == EDataFlow.Capture) return muted ? MicOff : Microphone;
            if (muted) return Mute;
            if (volume <= 0.001f) return Volume0;
            if (volume < 0.34f) return Volume1;
            if (volume < 0.67f) return Volume2;
            return Volume3;
        }
    }
}
