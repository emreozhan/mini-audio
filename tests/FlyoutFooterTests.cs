// Tests for the flyout's footer: the Auto switch and Software volume toggles, the Reset device button
// and the settings gear. The flyout is laid out off screen and clicked through its own mouse handler;
// nothing is shown and no audio setting or device changes (the controls only raise events).

using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace MiniAudio.Tests
{
    static class FlyoutFooterTests
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        public static void AddTo(TestList t)
        {
            t.Add("Flyout footer: clicking the Software volume toggle raises only SoftwareVolumeToggled", delegate
            {
                WithFlyout(delegate(Flyout f, int[] raised)
                {
                    Click(f, Center(Rect(f, "softToggle")));
                    Check.Equal("soft 1, auto 0", "soft " + raised[0] + ", auto " + raised[1]);
                });
            });
            t.Add("Flyout footer: clicking the Auto switch toggle raises only AutoToggled", delegate
            {
                WithFlyout(delegate(Flyout f, int[] raised)
                {
                    Click(f, Center(Rect(f, "autoToggle")));
                    Check.Equal("soft 0, auto 1", "soft " + raised[0] + ", auto " + raised[1]);
                });
            });
            t.Add("Flyout footer: the Software volume toggle works on the Apps tab too", delegate
            {
                WithFlyout(delegate(Flyout f, int[] raised)
                {
                    using (f.Snapshot(FlyoutTab.Apps)) { }
                    Click(f, Center(Rect(f, "softToggle")));
                    Check.Equal(1, raised[0]);
                });
            });
            t.Add("Flyout footer: toggles, Reset and gear do not overlap and fit inside the flyout", delegate
            {
                WithFlyout(delegate(Flyout f, int[] raised)
                {
                    Rectangle auto = Rect(f, "autoToggle"), soft = Rect(f, "softToggle"), reset = Rect(f, "reset"), gear = Rect(f, "gear");
                    Check.True(soft.Width > 0 && soft.Height > 0, "the Software volume toggle has no size");
                    Check.True(reset.Width > 0 && reset.Height > 0, "the Reset button has no size");
                    Check.True(!auto.IntersectsWith(soft), "Auto switch " + auto + " overlaps Software volume " + soft);
                    Check.True(!soft.IntersectsWith(reset), "Software volume " + soft + " overlaps Reset " + reset);
                    Check.True(!reset.IntersectsWith(gear), "Reset " + reset + " overlaps the gear " + gear);
                    Check.True(auto.Left >= 0 && gear.Right <= f.ClientSize.Width, "footer controls leave the flyout");
                });
            });
            t.Add("Flyout footer: the toggle labels fit", delegate
            {
                WithFlyout(delegate(Flyout f, int[] raised)
                {
                    float scale;
                    using (var g = f.CreateGraphics()) scale = g.DpiX / 96f;
                    using (var font = new Font("Segoe UI", 13 * scale, GraphicsUnit.Pixel))
                        foreach (var toggle in new[] { new[] { "autoToggle", "Auto switch" }, new[] { "softToggle", "Software volume" } })
                        {
                            int label = TextRenderer.MeasureText(toggle[1], font, Size.Empty, TextFormatFlags.NoPadding).Width;
                            int room = Rect(f, toggle[0]).Width - (int)(48 * scale); // the label starts after the switch
                            Check.True(label <= room, "\"" + toggle[1] + "\" needs " + label + " px but has " + room);
                        }
                });
            });
            t.Add("Flyout footer: clicking Reset raises only ResetRequested when the default output can be reset", delegate
            {
                WithFlyout(true, delegate(Flyout f, int[] raised)
                {
                    Click(f, Center(Rect(f, "reset")));
                    Check.Equal("soft 0, auto 0, reset 1", "soft " + raised[0] + ", auto " + raised[1] + ", reset " + raised[2]);
                });
            });
            t.Add("Flyout footer: clicking Reset does nothing when the default output cannot be reset", delegate
            {
                WithFlyout(false, delegate(Flyout f, int[] raised)
                {
                    Click(f, Center(Rect(f, "reset")));
                    Check.Equal("soft 0, auto 0, reset 0", "soft " + raised[0] + ", auto " + raised[1] + ", reset " + raised[2]);
                });
            });
        }

        static void WithFlyout(Action<Flyout, int[]> body) { WithFlyout(true, body); }

        // A laid-out flyout (Devices tab); raised[0] counts SoftwareVolumeToggled, raised[1] AutoToggled,
        // raised[2] ResetRequested. canReset: whether the flyout is told its default output can be reset.
        static void WithFlyout(bool canReset, Action<Flyout, int[]> body)
        {
            var audio = new AudioService(null);
            var config = new Config();
            using (var f = new Flyout(audio, new AppAudioService(audio, config, false), config))
            {
                var raised = new int[3];
                f.CanReset = delegate { return canReset; };
                f.SoftwareVolumeToggled += delegate { raised[0]++; };
                f.AutoToggled += delegate { raised[1]++; };
                f.ResetRequested += delegate { raised[2]++; };
                using (f.Snapshot(FlyoutTab.Devices)) { }
                body(f, raised);
            }
        }

        static Rectangle Rect(Flyout f, string field)
        {
            var info = typeof(Flyout).GetField(field, Private);
            if (info == null) throw new Exception("Flyout has no field \"" + field + "\"");
            return (Rectangle)info.GetValue(f);
        }

        static Point Center(Rectangle r) { return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

        static void Click(Flyout f, Point p)
        {
            typeof(Flyout).GetMethod("OnMouseDown", Private).Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0) });
        }
    }
}
