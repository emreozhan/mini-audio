// Flyout opened from the tray icon: device list, default selection, volume, mute.
// Fully owner-drawn (looks like the Windows 11 sound flyout).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MiniAudio
{
    class Flyout : Form
    {
        enum Part { None, Header, Mute, Slider, AutoToggle, Gear }

        class Row
        {
            public AudioDevice Dev;
            public bool IsDefault;
            public Rectangle Bounds, Header, Icon, MuteBtn, Slider;
            public float Volume = -1f, Peak;
            public bool Muted;
        }

        class Section
        {
            public string Title;
            public int Y;
            public List<Row> Rows = new List<Row>();
        }

        readonly AudioService audio;
        readonly Config config;
        readonly Timer tick;
        Theme theme;
        float sc = 1f;
        Font fontName, fontNameBold, fontSmall, fontSection, fontGlyph, fontGlyphSmall;
        List<Section> sections = new List<Section>();
        Rectangle footer, autoToggle, gear;
        int tickCount;

        Row hoverRow, dragRow;
        Part hoverPart;

        public int LastHiddenTick { get; private set; }

        public event Action<AudioDevice> SwitchRequested;
        public event Action SettingsRequested;
        public event Action AutoToggled;

        public Flyout(AudioService audio, Config config)
        {
            this.audio = audio;
            this.config = config;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            Text = "MiniAudio";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            using (var g = CreateGraphics()) sc = g.DpiX / 96f;
            fontName = new Font("Segoe UI", S(13), GraphicsUnit.Pixel);
            fontNameBold = new Font("Segoe UI Semibold", S(13), GraphicsUnit.Pixel);
            fontSmall = new Font("Segoe UI", S(11.5f), GraphicsUnit.Pixel);
            fontSection = new Font("Segoe UI Semibold", S(12), GraphicsUnit.Pixel);
            fontGlyph = Glyphs.Font(S(16));
            fontGlyphSmall = Glyphs.Font(S(14));

            tick = new Timer();
            tick.Interval = 40;
            tick.Tick += delegate { OnTick(); };
        }

        int S(int v) { return (int)Math.Round(v * sc); }
        float S(float v) { return v * sc; }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= Win32.CS_DROPSHADOW;
                cp.ExStyle |= Win32.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.RoundCorners(Handle, false);
        }

        // ---- show / hide ----

        public void ShowNear(Point cursor)
        {
            theme = Theme.Current();
            BackColor = theme.Bg;
            Rebuild();
            var wa = Screen.FromPoint(cursor).WorkingArea;
            int m = S(12);
            int x = wa.Right - Width - m;
            int y = cursor.Y < wa.Top + wa.Height / 2 ? wa.Top + m : wa.Bottom - Height - m;
            Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
            Show();
            Activate();
            Win32.SetForegroundWindow(Handle);
            tick.Start();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            HideFlyout();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) HideFlyout();
        }

        void HideFlyout()
        {
            if (!Visible) return;
            tick.Stop();
            dragRow = null;
            Hide();
            LastHiddenTick = Environment.TickCount;
        }

        // Renders without showing (for --snapshot checks)
        public Bitmap Snapshot()
        {
            theme = Theme.Current();
            Rebuild();
            for (int i = 0; i < 8; i++) OnTick(); // let the level meters fill
            var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
            return bmp;
        }

        // Called when the device list changes (while open)
        public void RefreshDevices()
        {
            if (!Visible) return;
            int oldHeight = Height;
            Rebuild();
            if (Height != oldHeight) Top += oldHeight - Height; // keep the bottom edge in place
            Invalidate();
        }

        // ---- layout ----

        void Rebuild()
        {
            hoverRow = null; dragRow = null; hoverPart = Part.None;
            sections.Clear();

            var outs = audio.GetDevices(EDataFlow.Render, DeviceState.Active);
            outs.Sort(delegate(AudioDevice a, AudioDevice b)
            {
                int r = config.Rank(a.Id).CompareTo(config.Rank(b.Id));
                return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            AddSection("Output", outs, audio.GetDefaultId(EDataFlow.Render, ERole.Console));

            if (config.ShowMics)
            {
                var ins = audio.GetDevices(EDataFlow.Capture, DeviceState.Active);
                ins.Sort(delegate(AudioDevice a, AudioDevice b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
                AddSection("Input", ins, audio.GetDefaultId(EDataFlow.Capture, ERole.Console));
            }

            int w = S(380), pad = S(10), rowH = S(72), secH = S(30);
            int y = S(8);
            foreach (var sec in sections)
            {
                sec.Y = y;
                y += secH;
                if (sec.Rows.Count == 0) y += S(28); // "no devices" line
                foreach (var r in sec.Rows)
                {
                    r.Bounds = new Rectangle(pad, y, w - pad * 2, rowH - S(4));
                    int x0 = r.Bounds.X, y0 = r.Bounds.Y;
                    r.Header = new Rectangle(x0, y0, r.Bounds.Width, S(42));
                    r.Icon = new Rectangle(x0 + S(8), y0 + S(6), S(32), S(32));
                    r.MuteBtn = new Rectangle(x0 + S(8), y0 + S(42), S(32), S(24));
                    r.Slider = new Rectangle(x0 + S(52), y0 + S(42), r.Bounds.Width - S(52) - S(46), S(24));
                    ReadLevels(r);
                    y += rowH;
                }
                y += S(4);
            }
            footer = new Rectangle(0, y, w, S(48));
            autoToggle = new Rectangle(pad + S(6), y + S(12), S(190), S(24));
            gear = new Rectangle(w - pad - S(36), y + S(8), S(32), S(32));
            ClientSize = new Size(w, y + footer.Height);
        }

        void AddSection(string title, List<AudioDevice> devs, string defaultId)
        {
            var sec = new Section();
            sec.Title = title;
            foreach (var d in devs)
            {
                var r = new Row();
                r.Dev = d;
                r.IsDefault = string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase);
                sec.Rows.Add(r);
            }
            sections.Add(sec);
        }

        static void ReadLevels(Row r)
        {
            r.Volume = r.Dev.GetVolume();
            r.Muted = r.Dev.GetMute();
        }

        // ---- live update ----

        void OnTick()
        {
            tickCount++;
            bool readLevels = tickCount % 6 == 0;
            foreach (var sec in sections)
                foreach (var r in sec.Rows)
                {
                    if (r.Dev.Flow == EDataFlow.Render)
                    {
                        float p = r.Dev.GetPeak();
                        r.Peak = Math.Max(p, r.Peak - 0.035f);
                    }
                    if (readLevels && r != dragRow) ReadLevels(r);
                }
            Invalidate();
        }

        // ---- mouse ----

        Row HitRow(Point p, out Part part)
        {
            part = Part.None;
            if (autoToggle.Contains(p)) { part = Part.AutoToggle; return null; }
            if (gear.Contains(p)) { part = Part.Gear; return null; }
            foreach (var sec in sections)
                foreach (var r in sec.Rows)
                {
                    if (!r.Bounds.Contains(p)) continue;
                    if (r.MuteBtn.Contains(p)) part = Part.Mute;
                    else if (p.Y >= r.Slider.Top - S(4) && p.X >= r.Slider.Left - S(8) && p.X <= r.Slider.Right + S(8)) part = Part.Slider;
                    else if (r.Header.Contains(p)) part = Part.Header;
                    return r;
                }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragRow != null) { SetFromX(dragRow, e.X); return; }
            Part part;
            var r = HitRow(e.Location, out part);
            if (r != hoverRow || part != hoverPart)
            {
                hoverRow = r; hoverPart = part;
                bool hand = part == Part.Mute || part == Part.Slider || part == Part.AutoToggle || part == Part.Gear
                         || (part == Part.Header && r != null && !r.IsDefault);
                Cursor = hand ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (dragRow == null) { hoverRow = null; hoverPart = Part.None; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Part part;
            var r = HitRow(e.Location, out part);
            switch (part)
            {
                case Part.Slider:
                    dragRow = r;
                    if (r.Muted) { r.Dev.SetMute(false); r.Muted = false; }
                    SetFromX(r, e.X);
                    break;
                case Part.Mute:
                    r.Muted = !r.Muted;
                    r.Dev.SetMute(r.Muted);
                    Invalidate();
                    break;
                case Part.Header:
                    if (!r.IsDefault && SwitchRequested != null) SwitchRequested(r.Dev);
                    break;
                case Part.AutoToggle:
                    if (AutoToggled != null) AutoToggled();
                    Invalidate();
                    break;
                case Part.Gear:
                    HideFlyout();
                    if (SettingsRequested != null) SettingsRequested();
                    break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragRow != null) { dragRow = null; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Part part;
            var r = HitRow(PointToClient(Cursor.Position), out part);
            if (r == null || r.Volume < 0) return;
            float step = 0.02f * (e.Delta / 120f);
            r.Volume = Math.Max(0f, Math.Min(1f, r.Volume + step));
            r.Dev.SetVolume(r.Volume);
            Invalidate();
        }

        void SetFromX(Row r, int x)
        {
            if (r.Volume < 0) return;
            float v = (x - r.Slider.Left) / (float)r.Slider.Width;
            r.Volume = (float)Math.Round(Math.Max(0f, Math.Min(1f, v)) * 100f) / 100f;
            r.Dev.SetVolume(r.Volume);
            Invalidate();
        }

        // ---- painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var pen = new Pen(theme.Border))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            foreach (var sec in sections)
            {
                var tr = new Rectangle(S(20), sec.Y, ClientSize.Width - S(40), S(30));
                DrawText(g, sec.Title, fontSection, theme.SubText, tr, TextFormatFlags.VerticalCenter);
                if (sec.Rows.Count == 0)
                    DrawText(g, "No active devices", fontSmall, theme.SubText,
                        new Rectangle(S(20), sec.Y + S(30), tr.Width, S(24)), TextFormatFlags.VerticalCenter);
                foreach (var r in sec.Rows) DrawRow(g, r);
            }
            DrawFooter(g);
        }

        void DrawRow(Graphics g, Row r)
        {
            bool hover = r == hoverRow;
            if (hover && dragRow == null)
                Theme.FillRound(g, theme.Surface, r.Bounds, S(6f));

            // Accent bar on the left of the default device (Windows 11 selection indicator)
            if (r.IsDefault)
                Theme.FillRound(g, theme.Accent, new RectangleF(r.Bounds.X, r.Icon.Y + S(8f), S(3f), r.Icon.Height - S(16f)), S(1.5f));

            // icon circle
            float cx = r.Icon.X + r.Icon.Width / 2f, cy = r.Icon.Y + r.Icon.Height / 2f;
            Theme.FillCircle(g, r.IsDefault ? theme.Accent : theme.SurfaceStrong, cx, cy, r.Icon.Width / 2f);
            DrawText(g, Glyphs.ForDevice(r.Dev), fontGlyph, r.IsDefault ? theme.OnAccent : theme.Text, r.Icon,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // name + detail
            int tx = r.Slider.X;
            int right = r.Bounds.Right - S(10);
            string badge = r.IsDefault ? "Default" : (hover && hoverPart == Part.Header ? "Set as default" : null);
            int badgeW = 0;
            if (badge != null)
            {
                badgeW = TextRenderer.MeasureText(g, badge, fontSmall, Size.Empty, TextFormatFlags.NoPadding).Width + S(6);
                DrawText(g, badge, fontSmall, r.IsDefault ? theme.Accent : theme.SubText,
                    new Rectangle(right - badgeW, r.Bounds.Y + S(6), badgeW, S(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            DrawText(g, r.Dev.Name, r.IsDefault ? fontNameBold : fontName, theme.Text,
                new Rectangle(tx, r.Bounds.Y + S(5), right - tx - badgeW, S(20)), TextFormatFlags.VerticalCenter);
            DrawText(g, r.Dev.Detail, fontSmall, theme.SubText,
                new Rectangle(tx, r.Bounds.Y + S(23), right - tx, S(17)), TextFormatFlags.VerticalCenter);

            // mute button
            if (hover && hoverPart == Part.Mute)
                Theme.FillRound(g, theme.SurfaceStrong, r.MuteBtn, S(4f));
            DrawText(g, Glyphs.ForVolume(r.Dev, r.Volume, r.Muted), fontGlyphSmall, r.Muted ? theme.Danger : theme.SubText,
                r.MuteBtn, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // slider
            if (r.Volume < 0)
            {
                DrawText(g, "Volume unavailable", fontSmall, theme.SubText, r.Slider, TextFormatFlags.VerticalCenter);
                return;
            }
            float trackY = r.Slider.Y + r.Slider.Height / 2f;
            float th = S(4f);
            var track = new RectangleF(r.Slider.X, trackY - th / 2, r.Slider.Width, th);
            Theme.FillRound(g, theme.Track, track, th / 2);
            float fillW = track.Width * r.Volume;
            Color fillColor = r.Muted ? theme.SubText : theme.Accent;
            if (fillW > 0.5f) Theme.FillRound(g, fillColor, new RectangleF(track.X, track.Y, fillW, th), th / 2);

            // live level (shows at a glance which device is actually playing)
            if (r.Peak > 0.005f && !r.Muted)
            {
                float mw = track.Width * Math.Min(1f, r.Peak) * Math.Max(r.Volume, 0.02f);
                Theme.FillRound(g, Color.FromArgb(150, theme.Accent), new RectangleF(track.X, trackY + S(6f), mw, S(2f)), S(1f));
            }

            float tx2 = track.X + fillW;
            bool thumbHover = r == dragRow || (hover && hoverPart == Part.Slider);
            Theme.FillCircle(g, theme.Dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.White, tx2, trackY, S(9f));
            using (var pen = new Pen(theme.Border, 1f))
                g.DrawEllipse(pen, tx2 - S(9f), trackY - S(9f), S(18f), S(18f));
            Theme.FillCircle(g, fillColor, tx2, trackY, thumbHover ? S(6f) : S(4.5f));

            DrawText(g, ((int)Math.Round(r.Volume * 100)).ToString(), fontName, theme.Text,
                new Rectangle(r.Slider.Right + S(6), r.Slider.Y, r.Bounds.Right - r.Slider.Right - S(10), r.Slider.Height),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        void DrawFooter(Graphics g)
        {
            using (var b = new SolidBrush(theme.Dark ? Color.FromArgb(0x24, 0x24, 0x24) : Color.FromArgb(0xEE, 0xEE, 0xEE)))
                g.FillRectangle(b, 1, footer.Y, footer.Width - 2, footer.Height - 1);
            using (var pen = new Pen(theme.Border))
                g.DrawLine(pen, 1, footer.Y, footer.Width - 2, footer.Y);

            // toggle switch
            bool on = config.Auto;
            var sw = new RectangleF(autoToggle.X, autoToggle.Y + S(2f), S(40f), S(20f));
            if (on) Theme.FillRound(g, theme.Accent, sw, sw.Height / 2);
            else
                using (var path = Theme.RoundRect(sw, sw.Height / 2))
                using (var pen = new Pen(theme.SubText, S(1f)))
                    g.DrawPath(pen, path);
            float knobR = hoverPart == Part.AutoToggle ? S(7f) : S(6f);
            float kx = on ? sw.Right - S(10f) : sw.X + S(10f);
            Theme.FillCircle(g, on ? theme.OnAccent : theme.SubText, kx, sw.Y + sw.Height / 2, knobR);
            DrawText(g, "Auto switch", fontName, theme.Text,
                new Rectangle(autoToggle.X + S(48), autoToggle.Y, autoToggle.Width - S(48), autoToggle.Height), TextFormatFlags.VerticalCenter);

            if (hoverPart == Part.Gear) Theme.FillRound(g, theme.SurfaceStrong, gear, S(4f));
            DrawText(g, Glyphs.Settings, fontGlyph, theme.Text, gear, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        static void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text ?? "", font, r, color,
                flags | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}
