using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PhoneRemote
{
    internal static class Visuals
    {
        internal static readonly Color Background = Color.FromArgb(16, 24, 37);
        internal static readonly Color Panel = Color.FromArgb(25, 36, 53);
        internal static readonly Color Border = Color.FromArgb(46, 61, 82);
        internal static readonly Color Button = Color.FromArgb(38, 54, 74);
        internal static readonly Color Text = Color.FromArgb(245, 248, 254);
        internal static readonly Color Muted = Color.FromArgb(161, 179, 201);
        internal static readonly Color Mint = Color.FromArgb(127, 243, 213);
        internal static readonly Color Amber = Color.FromArgb(247, 201, 123);
        internal static readonly Color Offline = Color.FromArgb(188, 157, 175);
        internal static readonly Color AccentInk = Color.FromArgb(15, 53, 49);

        internal static Font Font(float points, FontStyle style = FontStyle.Regular)
        {
            return new Font("Microsoft YaHei UI", points, style, GraphicsUnit.Point);
        }

        internal static float ScreenScale(Screen screen)
        {
            var bounds = screen.Bounds;
            var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            uint x, y;
            return GetDpiForMonitor(MonitorFromPoint(center, 2), 0, out x, out y) == 0 && x > 0 ? x / 96f : 1f;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int mode, out uint x, out uint y);

        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            float d = radius * 2;
            var shape = new GraphicsPath();
            shape.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            shape.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            shape.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            shape.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            shape.CloseFigure();
            return shape;
        }

        internal static void RoundedRect(Graphics canvas, RectangleF bounds, float radius, Color fill, Color outline)
        {
            using (var shape = Rounded(bounds, radius))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(outline))
            {
                canvas.FillPath(brush, shape);
                if (outline != Color.Transparent) canvas.DrawPath(pen, shape);
            }
        }

        internal static Color StatusColor(string status)
        {
            return status == "connected" ? Mint : status == "connecting" ? Amber : Offline;
        }

        internal static string StatusText(string status)
        {
            return status == "connected" ? "已连接" : status == "connecting" ? "连接中" : status == "unselected" ? "未选手机" : "未连接";
        }

        internal static void DrawMark(Graphics canvas, Rectangle target)
        {
            var saved = canvas.Save();
            try
            {
                canvas.TranslateTransform(target.X, target.Y);
                canvas.ScaleTransform(target.Width / 40f, target.Height / 40f);
                canvas.SmoothingMode = SmoothingMode.AntiAlias;
                RoundedRect(canvas, new RectangleF(0.5f, 0.5f, 39, 39), 11, Color.FromArgb(36, 60, 79), Color.FromArgb(70, 96, 113));
                using (var outline = Rounded(new RectangleF(9, 8, 15, 24), 3.5f))
                using (var pen = new Pen(Text, 1.45f)) canvas.DrawPath(pen, outline);
                using (var fill = new SolidBrush(Mint))
                    canvas.FillPolygon(fill, new[] { new PointF(14, 15), new PointF(20, 20), new PointF(14, 25) });
                using (var pen = new Pen(Mint, 1.55f))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    canvas.DrawBezier(pen, new PointF(27, 17), new PointF(30, 18), new PointF(30, 22), new PointF(27, 23));
                    canvas.DrawBezier(pen, new PointF(30, 13), new PointF(35, 16), new PointF(35, 24), new PointF(30, 27));
                }
            }
            finally { canvas.Restore(saved); }
        }
    }

    internal sealed class Card : Panel
    {
        internal Card()
        {
            DoubleBuffered = true;
            BackColor = Visuals.Background;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Visuals.RoundedRect(e.Graphics, new RectangleF(0.7f, 0.7f, Width - 1.4f, Height - 1.4f), 13, Visuals.Panel, Visuals.Border);
            base.OnPaint(e);
        }
    }

    internal sealed class MediaButton : Button
    {
        private readonly bool primary;
        private string symbol;
        private bool hover;

        internal MediaButton(string symbol, string text, bool primary = false)
        {
            this.primary = primary;
            this.symbol = symbol;
            Text = text;
            AccessibleName = text;
            BackColor = Visuals.Panel;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        internal void SetSymbol(string value, string label)
        {
            if (value == symbol) return;
            symbol = value;
            Text = AccessibleName = label;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var canvas = e.Graphics;
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.Clear(BackColor);
            Color body = primary && Enabled ? Visuals.Mint : hover && Enabled ? Visuals.Border : Visuals.Button;
            Color ink = !Enabled ? Color.FromArgb(115, 134, 158) : primary ? Visuals.AccentInk : Visuals.Text;
            Visuals.RoundedRect(canvas, new RectangleF(1, 1, Width - 2, Height - 2), 10, body,
                Focused && ShowFocusCues ? Visuals.Text : Color.Transparent);
            float x = Width / 2f;
            float y = Height / 2f;
            float s = Math.Min(Height / 42f, Width / 37f);
            using (var pen = new Pen(ink, 1.8f * s))
            using (var fill = new SolidBrush(ink))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                if (symbol == "pause")
                {
                    canvas.FillRectangle(fill, x - s * 5.5f, y - s * 7, s * 3.2f, s * 14);
                    canvas.FillRectangle(fill, x + s * 2.3f, y - s * 7, s * 3.2f, s * 14);
                }
                else if (symbol == "plus" || symbol == "minus")
                {
                    canvas.DrawLine(pen, x - 6 * s, y, x + 6 * s, y);
                    if (symbol == "plus") canvas.DrawLine(pen, x, y - 6 * s, x, y + 6 * s);
                }
                else
                {
                    int d = symbol == "previous" ? -1 : 1;
                    bool step = symbol == "previous" || symbol == "next";
                    canvas.FillPolygon(fill, new[]
                    {
                        new PointF(x - d * 6 * s, y - 7 * s),
                        new PointF(x + d * (step ? 3 : 7) * s, y),
                        new PointF(x - d * 6 * s, y + 7 * s)
                    });
                    if (step) canvas.DrawLine(pen, x + d * 7 * s, y - 6.5f * s, x + d * 7 * s, y + 6.5f * s);
                }
            }
        }
    }

    internal sealed class VolumeMeter : Control
    {
        private int minimum;
        private int maximum = 16;
        private int level;

        internal VolumeMeter()
        {
            DoubleBuffered = true;
            BackColor = Visuals.Panel;
            AccessibleName = "手机媒体音量";
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        internal void SetLevel(MediaState media)
        {
            minimum = media == null ? 0 : media.min;
            maximum = media == null ? 16 : media.max;
            level = media == null ? 0 : media.volume;
            AccessibleDescription = media == null ? "无音量数据" : level + " / " + maximum;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int count = maximum - minimum;
            float pitch = (float)Width / count;
            float width = Math.Max(1, pitch - 3);
            using (var on = new SolidBrush(Visuals.Mint))
            using (var off = new SolidBrush(Visuals.Border))
            {
                for (int i = 0; i < count; i++)
                {
                    var bar = new RectangleF(i * pitch, 2, width, Height - 4);
                    using (var shape = Visuals.Rounded(bar, Math.Min(2, width / 2)))
                        e.Graphics.FillPath(i < level - minimum ? on : off, shape);
                }
            }
        }
    }
}
