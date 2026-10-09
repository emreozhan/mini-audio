// Brief notice at the bottom center of the screen after a switch. Never takes focus, clicks pass through.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MiniAudio
{
    class Osd : Form
    {
        readonly Timer timer;
        Theme theme;
        float sc = 1f;
        Font fontTitle, fontSub, fontGlyph;
        string glyph, title, subtitle;
        int shownAt;
        const int HoldMs = 1600, FadeMs = 350;

        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            using (var g = CreateGraphics()) sc = g.DpiX / 96f;
            fontTitle = new Font("Segoe UI Semibold", 14 * sc, GraphicsUnit.Pixel);
            fontSub = new Font("Segoe UI", 12 * sc, GraphicsUnit.Pixel);
            fontGlyph = Glyphs.Font(20 * sc);
            timer = new Timer();
            timer.Interval = 30;
            timer.Tick += delegate { OnTick(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT;
                cp.ClassStyle |= Win32.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.RoundCorners(Handle, false);
        }

        public void ShowMessage(string glyph, string title, string subtitle)
        {
            this.glyph = glyph; this.title = title; this.subtitle = subtitle;
            theme = Theme.Current();
            BackColor = theme.Bg;

            int textW = Math.Max(TextRenderer.MeasureText(title, fontTitle).Width, TextRenderer.MeasureText(subtitle, fontSub).Width);
            int w = Math.Min((int)(520 * sc), Math.Max((int)(260 * sc), (int)(76 * sc) + textW));
            int h = (int)(64 * sc);
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Bottom - h - (int)(48 * sc), w, h);
            Opacity = 1;
            shownAt = Environment.TickCount;
            if (!Visible) Show();
            Invalidate();
            timer.Start();
        }

        public Bitmap Snapshot(string glyph, string title, string subtitle)
        {
            ShowMessage(glyph, title, subtitle);
            timer.Stop();
            Hide();
            var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
            return bmp;
        }

        void OnTick()
        {
            int el = Environment.TickCount - shownAt;
            if (el < HoldMs) return;
            double o = 1 - (el - HoldMs) / (double)FadeMs;
            if (o <= 0) { timer.Stop(); Hide(); return; }
            Opacity = o;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(theme.Border))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            float r = 18 * sc, cx = 14 * sc + r, cy = ClientSize.Height / 2f;
            Theme.FillCircle(g, theme.Accent, cx, cy, r);
            var ir = new Rectangle((int)(cx - r), (int)(cy - r), (int)(r * 2), (int)(r * 2));
            TextRenderer.DrawText(g, glyph, fontGlyph, ir, theme.OnAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int tx = (int)(64 * sc), tw = ClientSize.Width - tx - (int)(14 * sc);
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, title, fontTitle, new Rectangle(tx, (int)(12 * sc), tw, (int)(22 * sc)), theme.Text, flags);
            TextRenderer.DrawText(g, subtitle, fontSub, new Rectangle(tx, (int)(34 * sc), tw, (int)(18 * sc)), theme.SubText, flags);
        }
    }
}
