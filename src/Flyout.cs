// Flyout opened from the tray icon. Two tabs:
//  - Devices: outputs/inputs, default selection, volume, mute
//  - Apps: apps with an audio session, per-app output device, volume, mute, left/right balance
// Fully owner-drawn (looks like the Windows 11 sound flyout).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MiniAudio
{
    enum FlyoutTab { Devices, Apps }

    class Flyout : Form
    {
        enum Part { None, Header, Mute, Slider, Balance, AutoToggle, Gear, TabDevices, TabApps }

        class Row
        {
            public AudioDevice Dev;   // device row
            public AppSession App;    // app row
            public bool IsDefault;
            public int ContentY;
            public Rectangle Bounds, Header, Icon, MuteBtn, Slider, BalanceLabel, BalanceSlider;
            public float Volume = -1f, Peak, Balance;
            public bool Muted, HasBalance;
        }

        class Section
        {
            public string Title;
            public int ContentY, Y;
            public bool Empty;
        }

        const string ChevronDown = "";

        readonly AudioService audio;
        readonly AppAudioService appAudio;
        readonly Config config;
        readonly Timer tick;
        readonly ContextMenuStrip routeMenu = new ContextMenuStrip();
        Theme theme;
        float sc = 1f;
        Font fontName, fontNameBold, fontSmall, fontSection, fontGlyph, fontGlyphSmall, fontGlyphTiny;
        FlyoutTab tab = FlyoutTab.Devices;
        List<Row> rows = new List<Row>();
        List<Section> sections = new List<Section>();
        string emptyText;
        Rectangle tabs, tabDevices, tabApps, viewport, footer, autoToggle, gear;
        int contentHeight, scroll, maxViewport;
        int tickCount;
        bool menuOpen;

        Row hoverRow, dragRow;
        Part hoverPart, dragPart;

        public int LastHiddenTick { get; private set; }

        public event Action<AudioDevice> SwitchRequested;
        public event Action SettingsRequested;
        public event Action AutoToggled;

        public Flyout(AudioService audio, AppAudioService appAudio, Config config)
        {
            this.audio = audio;
            this.appAudio = appAudio;
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
            fontGlyphTiny = Glyphs.Font(S(10));
            maxViewport = S(700);

            routeMenu.Closed += OnRouteMenuClosed;

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
            var wa = Screen.FromPoint(cursor).WorkingArea;
            int m = S(12);
            maxViewport = Math.Max(S(150), wa.Height - 2 * m - S(48) - S(48));
            scroll = 0;
            Rebuild();
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
            if (!menuOpen) HideFlyout();
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
        public Bitmap Snapshot(FlyoutTab which)
        {
            theme = Theme.Current();
            tab = which;
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
            RebuildKeepingBottom();
        }

        void RebuildKeepingBottom()
        {
            int oldHeight = Height;
            Rebuild();
            if (Height != oldHeight) Top += oldHeight - Height; // keep the bottom edge in place
            Invalidate();
        }

        // ---- data ----

        void Rebuild()
        {
            hoverRow = null; dragRow = null; hoverPart = Part.None;
            rows.Clear();
            sections.Clear();
            int pad = S(10), y = 0;

            if (tab == FlyoutTab.Devices)
            {
                var outs = audio.GetDevices(EDataFlow.Render, DeviceState.Active);
                outs.Sort(delegate(AudioDevice a, AudioDevice b)
                {
                    int r = config.Rank(a.Id).CompareTo(config.Rank(b.Id));
                    return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
                y = AddDeviceSection("Output", outs, audio.GetDefaultId(EDataFlow.Render, ERole.Console), y);
                if (config.ShowMics)
                {
                    var ins = audio.GetDevices(EDataFlow.Capture, DeviceState.Active);
                    ins.Sort(delegate(AudioDevice a, AudioDevice b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
                    y = AddDeviceSection("Input", ins, audio.GetDefaultId(EDataFlow.Capture, ERole.Console), y);
                }
            }
            else
            {
                var apps = appAudio.GetApps();
                emptyText = apps.Count == 0 ? "No app has audio open right now" : null;
                if (emptyText != null) y += S(56);
                foreach (var a in apps)
                {
                    var r = new Row();
                    r.App = a;
                    r.ContentY = y;
                    ReadLevels(r);
                    rows.Add(r);
                    y += S(100);
                }
            }
            contentHeight = y + S(4);
            PlaceAll(pad);
        }

        int AddDeviceSection(string title, List<AudioDevice> devs, string defaultId, int y)
        {
            var sec = new Section();
            sec.Title = title;
            sec.ContentY = y;
            sec.Empty = devs.Count == 0;
            sections.Add(sec);
            y += S(30);
            if (sec.Empty) y += S(28);
            foreach (var d in devs)
            {
                var r = new Row();
                r.Dev = d;
                r.IsDefault = string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase);
                r.ContentY = y;
                ReadLevels(r);
                rows.Add(r);
                y += S(72);
            }
            return y + S(4);
        }

        // ---- layout ----

        void PlaceAll(int pad)
        {
            int w = S(380);
            tabs = new Rectangle(pad, S(8), w - pad * 2, S(32));
            tabDevices = new Rectangle(tabs.X, tabs.Y, tabs.Width / 2, tabs.Height);
            tabApps = new Rectangle(tabs.X + tabs.Width / 2, tabs.Y, tabs.Width - tabs.Width / 2, tabs.Height);

            int top = S(48);
            int vh = Math.Min(contentHeight, maxViewport);
            scroll = Math.Max(0, Math.Min(scroll, contentHeight - vh));
            viewport = new Rectangle(0, top, w, vh);

            foreach (var sec in sections) sec.Y = top + sec.ContentY - scroll;
            foreach (var r in rows) PlaceRow(r, pad, w, top + r.ContentY - scroll);

            int fy = top + vh;
            footer = new Rectangle(0, fy, w, S(48));
            autoToggle = new Rectangle(pad + S(6), fy + S(12), S(190), S(24));
            gear = new Rectangle(w - pad - S(36), fy + S(8), S(32), S(32));
            ClientSize = new Size(w, fy + footer.Height);
        }

        void PlaceRow(Row r, int pad, int w, int y)
        {
            bool app = r.App != null;
            r.Bounds = new Rectangle(pad, y, w - pad * 2, (app ? S(100) : S(72)) - S(4));
            int x0 = r.Bounds.X, y0 = r.Bounds.Y, sw = r.Bounds.Width - S(52) - S(46);
            r.Header = new Rectangle(x0, y0, r.Bounds.Width, S(42));
            r.Icon = new Rectangle(x0 + S(8), y0 + S(6), S(32), S(32));
            r.MuteBtn = new Rectangle(x0 + S(8), y0 + S(42), S(32), S(24));
            r.Slider = new Rectangle(x0 + S(52), y0 + S(42), sw, S(24));
            r.BalanceLabel = new Rectangle(x0 + S(8), y0 + S(68), S(32), S(24));
            r.BalanceSlider = new Rectangle(x0 + S(52), y0 + S(68), sw, S(24));
        }

        void ReadLevels(Row r)
        {
            if (r.Dev != null)
            {
                r.Volume = r.Dev.GetVolume();
                r.Muted = r.Dev.GetMute();
            }
            else
            {
                r.Volume = r.App.GetVolume();
                r.Muted = r.App.GetMute();
                r.HasBalance = r.App.HasBalance;
                if (r.HasBalance) r.Balance = r.App.GetBalance();
            }
        }

        // ---- live update ----

        void OnTick()
        {
            tickCount++;
            bool readLevels = tickCount % 6 == 0;
            if (tab == FlyoutTab.Apps && tickCount % 38 == 0 && dragRow == null && !menuOpen) { RefreshApps(); return; }
            foreach (var r in rows)
            {
                float p = r.App != null ? r.App.GetPeak() : (r.Dev.Flow == EDataFlow.Render ? r.Dev.GetPeak() : 0f);
                r.Peak = Math.Max(p, r.Peak - 0.035f);
                if (readLevels && r != dragRow) ReadLevels(r);
            }
            Invalidate();
        }

        // Apps come and go; rebuild only when the set changes so rows don't jump while being used
        void RefreshApps()
        {
            var fresh = appAudio.GetApps();
            bool same = fresh.Count == rows.Count;
            for (int i = 0; same && i < fresh.Count; i++) same = rows[i].App != null && rows[i].App.Key == fresh[i].Key;
            if (!same) { RebuildKeepingBottom(); return; }
            for (int i = 0; i < fresh.Count; i++) { rows[i].App = fresh[i]; ReadLevels(rows[i]); }
            Invalidate();
        }

        // ---- mouse ----

        Row HitRow(Point p, out Part part)
        {
            part = Part.None;
            if (tabDevices.Contains(p)) { part = Part.TabDevices; return null; }
            if (tabApps.Contains(p)) { part = Part.TabApps; return null; }
            if (autoToggle.Contains(p)) { part = Part.AutoToggle; return null; }
            if (gear.Contains(p)) { part = Part.Gear; return null; }
            if (!viewport.Contains(p)) return null;
            foreach (var r in rows)
            {
                if (!r.Bounds.Contains(p)) continue;
                if (r.App != null && p.Y >= r.BalanceSlider.Top - S(2) && p.X >= r.BalanceSlider.Left - S(8) && p.X <= r.BalanceSlider.Right + S(8))
                    part = r.HasBalance ? Part.Balance : Part.None;
                else if (r.MuteBtn.Contains(p)) part = Part.Mute;
                else if (p.Y >= r.Slider.Top - S(4) && p.X >= r.Slider.Left - S(8) && p.X <= r.Slider.Right + S(8)) part = Part.Slider;
                else if (r.Header.Contains(p)) part = Part.Header;
                return r;
            }
            return null;
        }

        bool HeaderClickable(Row r)
        {
            if (r == null) return false;
            if (r.Dev != null) return !r.IsDefault;
            return !r.App.IsSystem && AppRouting.Available && r.App.Pids.Count > 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragRow != null)
            {
                if (dragPart == Part.Balance) SetBalanceFromX(dragRow, e.X, false);
                else SetVolumeFromX(dragRow, e.X);
                return;
            }
            Part part;
            var r = HitRow(e.Location, out part);
            if (r != hoverRow || part != hoverPart)
            {
                hoverRow = r; hoverPart = part;
                bool hand = part == Part.Mute || part == Part.Slider || part == Part.Balance || part == Part.AutoToggle
                         || part == Part.Gear || part == Part.TabDevices || part == Part.TabApps
                         || (part == Part.Header && HeaderClickable(r));
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
                    if (r.Volume < 0) break;
                    dragRow = r; dragPart = Part.Slider;
                    if (r.Muted) SetMute(r, false);
                    SetVolumeFromX(r, e.X);
                    break;
                case Part.Balance:
                    dragRow = r; dragPart = Part.Balance;
                    SetBalanceFromX(r, e.X, false);
                    break;
                case Part.Mute:
                    SetMute(r, !r.Muted);
                    Invalidate();
                    break;
                case Part.Header:
                    if (!HeaderClickable(r)) break;
                    if (r.Dev != null) { if (SwitchRequested != null) SwitchRequested(r.Dev); }
                    else ShowRouteMenu(r);
                    break;
                case Part.TabDevices:
                case Part.TabApps:
                    var t = part == Part.TabApps ? FlyoutTab.Apps : FlyoutTab.Devices;
                    if (t != tab) { tab = t; scroll = 0; RebuildKeepingBottom(); }
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
            if (dragRow == null) return;
            if (dragPart == Part.Balance) SetBalanceFromX(dragRow, e.X, true); // saves the final value
            dragRow = null;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            Part part;
            var r = HitRow(e.Location, out part);
            if (part == Part.Balance) { r.Balance = 0f; appAudio.SetBalance(r.App, 0f); Invalidate(); } // double-click centers
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Part part;
            var r = HitRow(PointToClient(Cursor.Position), out part);
            float notches = e.Delta / 120f;

            if (part == Part.Balance)
            {
                r.Balance = Snap(Math.Max(-1f, Math.Min(1f, r.Balance + 0.05f * notches)));
                appAudio.SetBalance(r.App, r.Balance);
                Invalidate();
                return;
            }
            // Over a volume slider (or anywhere when nothing scrolls) the wheel changes volume; otherwise it scrolls
            bool scrollable = contentHeight > viewport.Height;
            if (r != null && r.Volume >= 0 && (part == Part.Slider || !scrollable))
            {
                r.Volume = Math.Max(0f, Math.Min(1f, r.Volume + 0.02f * notches));
                SetVolume(r, r.Volume);
                Invalidate();
                return;
            }
            if (scrollable)
            {
                scroll -= (int)(S(48) * notches);
                PlaceAll(S(10));
                Invalidate();
            }
        }

        // ---- actions ----

        void SetVolume(Row r, float v)
        {
            if (r.Dev != null) r.Dev.SetVolume(v); else r.App.SetVolume(v);
        }

        void SetMute(Row r, bool mute)
        {
            r.Muted = mute;
            if (r.Dev != null) r.Dev.SetMute(mute); else r.App.SetMute(mute);
        }

        void SetVolumeFromX(Row r, int x)
        {
            float v = (x - r.Slider.Left) / (float)r.Slider.Width;
            r.Volume = (float)Math.Round(Math.Max(0f, Math.Min(1f, v)) * 100f) / 100f;
            SetVolume(r, r.Volume);
            Invalidate();
        }

        void SetBalanceFromX(Row r, int x, bool save)
        {
            float v = (x - r.BalanceSlider.Left) / (float)r.BalanceSlider.Width * 2f - 1f;
            r.Balance = Snap(Math.Max(-1f, Math.Min(1f, v)));
            if (save) appAudio.SetBalance(r.App, r.Balance);
            else r.App.SetBalance(r.Balance);
            Invalidate();
        }

        // Rounds to whole percent and snaps to center near the middle
        static float Snap(float b)
        {
            b = (float)Math.Round(b * 100f) / 100f;
            return Math.Abs(b) < 0.04f ? 0f : b;
        }

        void ShowRouteMenu(Row r)
        {
            var app = r.App;
            routeMenu.Items.Clear();
            var def = audio.GetDefault(EDataFlow.Render);
            var item = new ToolStripMenuItem("Default" + (def != null ? " (" + def.Name + ")" : ""));
            item.Checked = app.RouteId == null;
            item.Click += delegate { Route(app, null); };
            routeMenu.Items.Add(item);
            routeMenu.Items.Add(new ToolStripSeparator());
            foreach (var d in audio.GetDevices(EDataFlow.Render, DeviceState.Active))
            {
                var it = new ToolStripMenuItem(d.Name);
                it.Checked = string.Equals(app.RouteId, d.Id, StringComparison.OrdinalIgnoreCase);
                string id = d.Id;
                it.Click += delegate { Route(app, id); };
                routeMenu.Items.Add(it);
            }
            menuOpen = true;
            routeMenu.Show(this, new Point(r.Slider.X, r.Header.Bottom));
        }

        void OnRouteMenuClosed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            menuOpen = false;
            // Clicking elsewhere on the desktop closes both; picking an item keeps the flyout open
            if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange) HideFlyout();
            else if (Visible) Activate();
        }

        void Route(AppSession app, string deviceId)
        {
            if (!appAudio.SetRoute(app, deviceId))
                MessageBox.Show(this, "Could not change the output device of " + app.Name + ".", "MiniAudio",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            tickCount = 38 * (tickCount / 38 + 1) - 12; // list refresh in ~0.5 s, after the session has moved to the new device
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

            DrawTabs(g);

            var state = g.Save();
            g.SetClip(viewport);
            foreach (var sec in sections)
            {
                var tr = new Rectangle(S(20), sec.Y, ClientSize.Width - S(40), S(30));
                DrawText(g, sec.Title, fontSection, theme.SubText, tr, TextFormatFlags.VerticalCenter);
                if (sec.Empty)
                    DrawText(g, "No active devices", fontSmall, theme.SubText,
                        new Rectangle(S(20), sec.Y + S(30), tr.Width, S(24)), TextFormatFlags.VerticalCenter);
            }
            if (tab == FlyoutTab.Apps && emptyText != null)
                DrawText(g, emptyText, fontSmall, theme.SubText,
                    new Rectangle(S(20), viewport.Y + S(16), ClientSize.Width - S(40), S(24)), TextFormatFlags.VerticalCenter);
            foreach (var r in rows)
                if (r.Bounds.IntersectsWith(viewport)) DrawRow(g, r);
            DrawScrollbar(g);
            g.Restore(state);

            DrawFooter(g);
        }

        void DrawTabs(Graphics g)
        {
            Theme.FillRound(g, theme.Surface, tabs, S(6f));
            var sel = tab == FlyoutTab.Devices ? tabDevices : tabApps;
            var inner = new RectangleF(sel.X + S(3f), sel.Y + S(3f), sel.Width - S(6f), sel.Height - S(6f));
            Theme.FillRound(g, theme.Dark ? theme.SurfaceStrong : Color.White, inner, S(4f));
            DrawTab(g, "Devices", tabDevices, tab == FlyoutTab.Devices, hoverPart == Part.TabDevices);
            DrawTab(g, "Apps", tabApps, tab == FlyoutTab.Apps, hoverPart == Part.TabApps);
        }

        void DrawTab(Graphics g, string text, Rectangle r, bool selected, bool hover)
        {
            DrawText(g, text, selected ? fontNameBold : fontName, selected || hover ? theme.Text : theme.SubText, r,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void DrawScrollbar(Graphics g)
        {
            if (contentHeight <= viewport.Height) return;
            float h = Math.Max(S(24f), viewport.Height * viewport.Height / (float)contentHeight);
            float y = viewport.Y + (viewport.Height - h) * scroll / (float)(contentHeight - viewport.Height);
            Theme.FillRound(g, theme.Track, new RectangleF(viewport.Right - S(6f), y, S(3f), h), S(1.5f));
        }

        void DrawRow(Graphics g, Row r)
        {
            bool hover = r == hoverRow;
            if (hover && dragRow == null)
                Theme.FillRound(g, theme.Surface, r.Bounds, S(6f));

            // Accent bar on the left of the default device (Windows 11 selection indicator)
            if (r.IsDefault)
                Theme.FillRound(g, theme.Accent, new RectangleF(r.Bounds.X, r.Icon.Y + S(8f), S(3f), r.Icon.Height - S(16f)), S(1.5f));

            int tx = r.Slider.X;
            int right = r.Bounds.Right - S(10);
            if (r.Dev != null) DrawDeviceHeader(g, r, hover, tx, right);
            else DrawAppHeader(g, r, hover, tx, right);

            // mute button
            if (hover && hoverPart == Part.Mute)
                Theme.FillRound(g, theme.SurfaceStrong, r.MuteBtn, S(4f));
            DrawText(g, Glyphs.ForVolume(r.Dev, r.Volume, r.Muted), fontGlyphSmall, r.Muted ? theme.Danger : theme.SubText,
                r.MuteBtn, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            DrawVolume(g, r, hover);
            if (r.App != null) DrawBalance(g, r, hover);
        }

        void DrawDeviceHeader(Graphics g, Row r, bool hover, int tx, int right)
        {
            float cx = r.Icon.X + r.Icon.Width / 2f, cy = r.Icon.Y + r.Icon.Height / 2f;
            Theme.FillCircle(g, r.IsDefault ? theme.Accent : theme.SurfaceStrong, cx, cy, r.Icon.Width / 2f);
            DrawText(g, Glyphs.ForDevice(r.Dev), fontGlyph, r.IsDefault ? theme.OnAccent : theme.Text, r.Icon,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

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
        }

        void DrawAppHeader(Graphics g, Row r, bool hover, int tx, int right)
        {
            var app = r.App;
            if (app.Icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(app.Icon, new Rectangle(r.Icon.X + S(2), r.Icon.Y + S(2), r.Icon.Width - S(4), r.Icon.Height - S(4)));
            }
            else
            {
                float cx = r.Icon.X + r.Icon.Width / 2f, cy = r.Icon.Y + r.Icon.Height / 2f;
                Theme.FillCircle(g, theme.SurfaceStrong, cx, cy, r.Icon.Width / 2f);
                DrawText(g, Glyphs.Speakers, fontGlyph, theme.Text, r.Icon, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int badgeW = 0;
            if (app.Active)
            {
                badgeW = TextRenderer.MeasureText(g, "Playing", fontSmall, Size.Empty, TextFormatFlags.NoPadding).Width + S(6);
                DrawText(g, "Playing", fontSmall, theme.Accent,
                    new Rectangle(right - badgeW, r.Bounds.Y + S(6), badgeW, S(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            DrawText(g, app.Name, app.Active ? fontNameBold : fontName, theme.Text,
                new Rectangle(tx, r.Bounds.Y + S(5), right - tx - badgeW, S(20)), TextFormatFlags.VerticalCenter);

            // output device ("route") line; clickable when routing is available
            string route;
            if (app.IsSystem) route = "Follows the default output";
            else if (!AppRouting.Available) route = "Per-app output needs Windows 11";
            else if (app.RouteId == null)
            {
                var def = audio.GetDefault(EDataFlow.Render);
                route = "Default output" + (def != null ? " · " + def.Name : "");
            }
            else
            {
                var d = audio.GetDevice(app.RouteId);
                route = d != null && d.IsActive ? d.Name : "Disconnected device";
            }
            bool clickable = HeaderClickable(r);
            bool hot = clickable && hover && hoverPart == Part.Header;
            Color rc = hot ? theme.Text : (app.RouteId != null ? theme.Accent : theme.SubText);
            int rw = Math.Min(right - tx - S(16), TextRenderer.MeasureText(g, route, fontSmall, Size.Empty, TextFormatFlags.NoPadding).Width + S(2));
            DrawText(g, route, fontSmall, rc, new Rectangle(tx, r.Bounds.Y + S(23), rw, S(17)), TextFormatFlags.VerticalCenter);
            if (clickable)
                DrawText(g, ChevronDown, fontGlyphTiny, rc, new Rectangle(tx + rw + S(4), r.Bounds.Y + S(24), S(12), S(17)),
                    TextFormatFlags.VerticalCenter);
        }

        void DrawVolume(Graphics g, Row r, bool hover)
        {
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

            // live level (shows at a glance what is actually playing)
            if (r.Peak > 0.005f && !r.Muted)
            {
                float mw = track.Width * Math.Min(1f, r.Peak) * Math.Max(r.Volume, 0.02f);
                Theme.FillRound(g, Color.FromArgb(150, theme.Accent), new RectangleF(track.X, trackY + S(6f), mw, S(2f)), S(1f));
            }

            bool thumbHover = (r == dragRow && dragPart == Part.Slider) || (hover && hoverPart == Part.Slider);
            DrawThumb(g, track.X + fillW, trackY, fillColor, thumbHover);

            DrawText(g, ((int)Math.Round(r.Volume * 100)).ToString(), fontName, theme.Text,
                new Rectangle(r.Slider.Right + S(6), r.Slider.Y, r.Bounds.Right - r.Slider.Right - S(10), r.Slider.Height),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        void DrawBalance(Graphics g, Row r, bool hover)
        {
            DrawText(g, "L R", fontSmall, theme.SubText, r.BalanceLabel, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (!r.HasBalance)
            {
                DrawText(g, "Balance unavailable (mono)", fontSmall, theme.SubText, r.BalanceSlider, TextFormatFlags.VerticalCenter);
                return;
            }
            float trackY = r.BalanceSlider.Y + r.BalanceSlider.Height / 2f;
            float th = S(3f);
            var track = new RectangleF(r.BalanceSlider.X, trackY - th / 2, r.BalanceSlider.Width, th);
            Theme.FillRound(g, theme.Track, track, th / 2);
            float cx = track.X + track.Width / 2f;
            float bx = cx + r.Balance * track.Width / 2f;
            // fill from the center towards the thumb, plus a center tick
            if (Math.Abs(bx - cx) > 0.5f)
                Theme.FillRound(g, theme.Accent, new RectangleF(Math.Min(cx, bx), track.Y, Math.Abs(bx - cx), th), th / 2);
            using (var b = new SolidBrush(theme.SubText))
                g.FillRectangle(b, cx - S(0.75f), trackY - S(5f), S(1.5f), S(10f));

            bool thumbHover = (r == dragRow && dragPart == Part.Balance) || (hover && hoverPart == Part.Balance);
            DrawThumb(g, bx, trackY, theme.Accent, thumbHover);

            int pct = (int)Math.Round(Math.Abs(r.Balance) * 100);
            string label = pct == 0 ? "C" : (r.Balance < 0 ? "L" : "R") + pct;
            DrawText(g, label, fontSmall, pct == 0 ? theme.SubText : theme.Text,
                new Rectangle(r.BalanceSlider.Right + S(6), r.BalanceSlider.Y, r.Bounds.Right - r.BalanceSlider.Right - S(10), r.BalanceSlider.Height),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        void DrawThumb(Graphics g, float x, float y, Color fill, bool hover)
        {
            Theme.FillCircle(g, theme.Dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.White, x, y, S(9f));
            using (var pen = new Pen(theme.Border, 1f))
                g.DrawEllipse(pen, x - S(9f), y - S(9f), S(18f), S(18f));
            Theme.FillCircle(g, fill, x, y, hover ? S(6f) : S(4.5f));
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
