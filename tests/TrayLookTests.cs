// Tests for the tray icon and its tooltip, which show the software volume state at a glance.

using System.Drawing;

namespace MiniAudio.Tests
{
    static class TrayLookTests
    {
        public static void AddTo(TestList t)
        {
            t.Add("Tray text: no output device",
                delegate { Check.Equal("MiniAudio — no output device", TrayLook.Text(null, -1f, false, false, null)); });
            t.Add("Tray text: device and volume",
                delegate { Check.Equal("Arctis — 58%", TrayLook.Text("Arctis", 0.58f, false, false, null)); });
            t.Add("Tray text: muted replaces the volume",
                delegate { Check.Equal("Arctis — muted", TrayLook.Text("Arctis", 0.58f, true, false, null)); });
            t.Add("Tray text: unreadable volume shows only the name",
                delegate { Check.Equal("Arctis", TrayLook.Text("Arctis", -1f, false, false, null)); });
            t.Add("Tray text: software volume on the default device",
                delegate { Check.Equal("Arctis — 58% · Software volume", TrayLook.Text("Arctis", 0.58f, false, true, null)); });
            t.Add("Tray text: software volume on another device names it",
                delegate { Check.Equal("Soundcore — 40% · Software volume: Arctis", TrayLook.Text("Soundcore", 0.4f, false, true, "Arctis")); });
            t.Add("Tray text: long text is cut to the 63 characters a tray icon allows", delegate
            {
                string text = TrayLook.Text("A very long device name that goes on and on", 0.5f, false, true, "Another long device name");
                Check.Equal(63, text.Length);
                Check.True(text.EndsWith("…"), "expected an ellipsis at the end, got \"" + text + "\"");
            });

            t.Add("Tray icon: has the requested size", delegate
            {
                using (var b = TrayLook.Icon(Glyphs.Headphones, false, false, 24))
                    Check.Equal(new Size(24, 24), b.Size);
            });
            t.Add("Tray icon: white glyph on a dark taskbar",
                delegate { GlyphColor(Color.White, false, false); });
            t.Add("Tray icon: dark glyph on a light taskbar",
                delegate { GlyphColor(Color.FromArgb(0x1A, 0x1A, 0x1A), true, false); });
            t.Add("Tray icon: accent-colored glyph while software volume is on (dark taskbar)",
                delegate { GlyphColor(Theme.AccentOn(false), false, true); });
            t.Add("Tray icon: accent-colored glyph while software volume is on (light taskbar)",
                delegate { GlyphColor(Theme.AccentOn(true), true, true); });
            t.Add("Tray icon: the accent color differs from the normal glyph color", delegate
            {
                Check.True(Theme.AccentOn(false).ToArgb() != Color.White.ToArgb(), "accent on a dark taskbar is white");
                Check.True(Theme.AccentOn(true).ToArgb() != Color.FromArgb(0x1A, 0x1A, 0x1A).ToArgb(), "accent on a light taskbar is the normal dark color");
            });
        }

        // The glyph's solid pixels have the expected color
        static void GlyphColor(Color expected, bool lightTaskbar, bool softwareVolume)
        {
            using (var b = TrayLook.Icon(Glyphs.Headphones, lightTaskbar, softwareVolume, 64))
            {
                Color solid = Color.Empty;
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        if (c.A > solid.A) solid = c;
                    }
                Check.True(solid.A >= 250, "the icon has no solid pixel (highest alpha " + solid.A + ")");
                bool same = System.Math.Abs(solid.R - expected.R) <= 2 && System.Math.Abs(solid.G - expected.G) <= 2 && System.Math.Abs(solid.B - expected.B) <= 2;
                Check.True(same, "expected glyph color " + Rgb(expected) + ", got " + Rgb(solid));
            }
        }

        static string Rgb(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
    }
}
