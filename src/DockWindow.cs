using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Interlocked = System.Threading.Interlocked;

namespace PhoneRemote
{
    internal sealed class DockWindow : Form
    {
        private readonly Font caption = Visuals.Font(9, FontStyle.Bold);
        private readonly Font digits = Visuals.Font(9, FontStyle.Bold);
        private readonly Font taskbarCaption = Visuals.Font(10.5f, FontStyle.Bold);
        private readonly Font taskbarDigits = Visuals.Font(11, FontStyle.Bold);
        private readonly Font timeFont = Visuals.Font(7.25f);
        private readonly Action showRemote;
        private static readonly Color TaskbarInk = Color.FromArgb(180, 247, 223);
        private readonly Timer layoutTimer = new Timer { Interval = 250 };
        private readonly Timer dragTimer = new Timer { Interval = 16 };
        private readonly Timer taskbarTimer = new Timer { Interval = 500 };
        private readonly Timer displayTimer = new Timer { Interval = 180 };
        private readonly Timer disconnectGrace = new Timer { Interval = 900 };
        private readonly LayeredSurface surface = new LayeredSurface();
        private readonly FrameClock marqueeClock;
        private readonly TaskbarObserver taskbar;
        private ConnectionView view;
        private ConnectionView pendingConnection;
        private PlaybackProgress progress;
        private bool scrubbing;
        private bool hoverProgress;
        private int? seekPreview;
        private bool marquee;
        private long marqueeStart;
        private int displayRate = 60;
        private int framePending;
        private long marqueeFrames;
        private int paintedSecond = -1;
        private string titleText = "等待手机连接";
        private Bitmap marqueeText;
        private Bitmap baseFrame;
        private Color marqueeColor;
        private Font marqueeFont;
        private float scale = 1;
        private Point? savedPosition;
        private bool preferTaskbar = true;
        private bool inTaskbar;
        private bool editing;
        private bool held;
        private bool dragging;
        private int taskbarBackground;
        private int placementRevision;
        private int layoutChecks;
        private bool layoutPending;
        private bool shellWasActive;
        private Rectangle confirmedSlot;
        private TaskbarLayout? pendingLayout;
        private int pendingSince;
        private const int LayoutSettleMilliseconds = 1500;
        private Point downAt;
        private Point grabAt;

        internal event Action<Point?, bool> PositionChanged;
        internal event Func<uint, int, bool> Seek;
        internal bool InTaskbar { get { return inTaskbar; } }
        internal bool Editing { get { return editing; } }
        internal bool Rendered { get; private set; }
        internal int MarqueeRate { get { return marqueeClock.Rate; } }
        internal long MarqueeFrames { get { return marqueeFrames; } }

        internal DockWindow(Action show)
        {
            showRemote = show;
            marqueeClock = new FrameClock(RequestMarqueeFrame);
            Text = Preferences.AppName + " · 小窗";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            taskbar = new TaskbarObserver(ScheduleLayout);
            Cursor = Cursors.Hand;
            layoutTimer.Tick += (s, e) => { layoutTimer.Stop(); ReflowFromShell(); };
            taskbarTimer.Tick += (s, e) => CheckTaskbar();
            displayTimer.Tick += (s, e) =>
            {
                if (marquee || CurrentPosition() / 1000 != paintedSecond) ShowLayer();
            };
            disconnectGrace.Tick += (s, e) =>
            {
                disconnectGrace.Stop();
                ApplyView(pendingConnection);
                SetProgress(null);
            };
            // A nonactivating window may lose capture as the pointer leaves it.
            dragTimer.Tick += (s, e) =>
            {
                var pointer = Cursor.Position;
                if ((MouseButtons & MouseButtons.Left) != 0) MoveDrag(pointer);
                else FinishDrag(pointer);
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var settings = base.CreateParams;
                settings.ExStyle |= 0x08000000 | 0x80000 | 0x80;
                return settings;
            }
        }

        internal void LoadPosition(Point? position, bool taskbarPosition)
        {
            savedPosition = position;
            preferTaskbar = taskbarPosition;
            placementRevision++;
        }

        internal void AnchorToTaskbar()
        {
            PlaceForPreference(true);
        }

        private void PlaceForPreference(bool retainSlot)
        {
            if (held || IsDisposed) return;
            placementRevision++;
            pendingLayout = null;
            if (preferTaskbar)
            {
                var primary = Screen.PrimaryScreen;
                float displayScale = Visuals.ScreenScale(primary);
                if (retainSlot && inTaskbar && TaskbarSpace.HasShellFocus()) return;
                var layout = FindTaskbarSlot(displayScale,
                    retainSlot ? confirmedSlot : Rectangle.Empty);
                if (layout.Kind == TaskbarLayoutKind.Available)
                {
                    scale = displayScale;
                    SetPlacement(layout.Slot, true);
                    WatchTaskbar();
                    return;
                }
                if (inTaskbar)
                {
                    if (Bounds != confirmedSlot) SetPlacement(confirmedSlot, true);
                    WatchTaskbar();
                    RequestReflow(250);
                    return;
                }
            }
            var desktop = savedPosition.HasValue && !preferTaskbar
                ? Screen.FromPoint(savedPosition.Value) : Screen.PrimaryScreen;
            PlaceFloating(desktop, preferTaskbar ? null : savedPosition);
            WatchTaskbar();
        }

        internal bool PutInTaskbar()
        {
            editing = false;
            Remember(preferTaskbar ? savedPosition : (Point?)Location, true);
            PlaceForPreference(false);
            return inTaskbar;
        }

        internal void ToggleEditing()
        {
            editing = !editing;
            Cursor = editing ? Cursors.SizeAll : Cursors.Hand;
            ShowLayer();
        }

        internal void ResetPlacement()
        {
            editing = false;
            Remember(null, true);
            PlaceForPreference(false);
        }

        private static TaskbarLayout FindTaskbarSlot(float displayScale, Rectangle current)
        {
            return TaskbarSpace.FindSlot((int)Math.Round(340 * displayScale),
                (int)Math.Round(225 * displayScale), (int)Math.Round(4 * displayScale), current);
        }

        private void PlaceFloating(Screen desktop, Point? position)
        {
            scale = Visuals.ScreenScale(desktop);
            var area = desktop.WorkingArea;
            int width = Math.Min(Scaled(340), area.Width - Scaled(16));
            int height = Scaled(33);
            var nearest = position ?? new Point(area.Right - width - Scaled(8), area.Bottom - height - Scaled(5));
            int x = Math.Max(area.Left, Math.Min(nearest.X, area.Right - width));
            int y = Math.Max(area.Top, Math.Min(nearest.Y, area.Bottom - height));
            SetPlacement(new Rectangle(x, y, width, height), false);
        }

        private void SetPlacement(Rectangle position, bool onTaskbar)
        {
            inTaskbar = onTaskbar;
            displayRate = FrameClock.RefreshRate(Screen.FromRectangle(position));
            confirmedSlot = onTaskbar ? position : Rectangle.Empty;
            if (Bounds != position) SetBounds(position.X, position.Y, position.Width, position.Height);
            Cursor = editing ? Cursors.SizeAll : Cursors.Hand;
            ShowLayer();
            KeepTaskbarLayer();
        }

        private void Remember(Point? point, bool onTaskbar)
        {
            savedPosition = point;
            preferTaskbar = onTaskbar;
            pendingLayout = null;
            placementRevision++;
            var changed = PositionChanged;
            if (changed != null) changed(point, onTaskbar);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            grabAt = e.Location;
            downAt = PointToScreen(e.Location);
            held = true;
            Capture = true;
            scrubbing = !editing && progress != null && progress.CanSeek && ProgressArea().Contains(e.Location);
            if (scrubbing) PreviewAt(e.Location.X);
            dragTimer.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetProgressHover(!editing && progress != null && progress.CanSeek && ProgressArea().Contains(e.Location));
            MoveDrag(PointToScreen(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetProgressHover(false);
        }

        private void SetProgressHover(bool value)
        {
            if (hoverProgress == value) return;
            hoverProgress = value;
            ShowLayer();
        }

        private void MoveDrag(Point point)
        {
            if (!held) return;
            if (scrubbing)
            {
                PreviewAt(PointToClient(point).X);
                return;
            }
            if (!dragging)
            {
                var threshold = SystemInformation.DragSize;
                if (Math.Abs(point.X - downAt.X) < threshold.Width / 2 && Math.Abs(point.Y - downAt.Y) < threshold.Height / 2) return;
                dragging = true;
            }
            if (editing || !inTaskbar) Location = new Point(point.X - grabAt.X, point.Y - grabAt.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) FinishDrag(PointToScreen(e.Location));
            base.OnMouseUp(e);
        }

        private void FinishDrag(Point pointer)
        {
            if (!held) return;
            MoveDrag(pointer);
            bool dropped = dragging;
            bool wasScrubbing = scrubbing;
            held = dragging = false;
            scrubbing = false;
            dragTimer.Stop();
            Capture = false;
            if (wasScrubbing)
            {
                var handler = Seek;
                if (handler == null || progress == null || !seekPreview.HasValue || !handler(progress.Track, seekPreview.Value)) seekPreview = null;
                ShowLayer();
            }
            else if (dropped && (editing || !inTaskbar)) DropAt(pointer);
            else if (!dropped && !editing && ClientRectangle.Contains(PointToClient(pointer))) showRemote();
        }

        private void DropAt(Point pointer)
        {
            var bounds = TaskbarSpace.Bounds();
            if (!bounds.IsEmpty && Rectangle.Inflate(bounds, 0, Scaled(12)).Contains(pointer))
            {
                editing = false;
                Remember(Location, true);
                PlaceForPreference(false);
                return;
            }
            editing = false;
            PlaceFloating(Screen.FromPoint(pointer), Location);
            Remember(Location, false);
            WatchTaskbar();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Disposing || IsDisposed) return;
            if (!Visible && held)
            {
                held = dragging = scrubbing = false;
                seekPreview = null;
                dragTimer.Stop();
                Capture = false;
            }
            WatchTaskbar();
            if (!Visible) { displayTimer.Stop(); marqueeClock.SetRate(0); hoverProgress = false; }
            ShowLayer();
            KeepTaskbarLayer();
        }

        private void WatchTaskbar()
        {
            bool observing = Visible && preferTaskbar && !IsDisposed && !Disposing;
            if (taskbar != null) taskbar.SetEnabled(observing);
            if (taskbarTimer.Enabled == observing) return;
            layoutChecks = 0;
            shellWasActive = false;
            taskbarTimer.Enabled = observing;
        }

        private void CheckTaskbar()
        {
            if (!Visible || held || !preferTaskbar) return;
            KeepTaskbarLayer();
            if (TaskbarSpace.HasShellFocus())
            {
                shellWasActive = true;
                pendingLayout = null;
                return;
            }
            if (shellWasActive)
            {
                shellWasActive = false;
                RequestReflow(350);
            }
            // Extra checks recover from missed Explorer accessibility notifications and taskbar restarts.
            if (++layoutChecks < (inTaskbar ? 16 : 4)) return;
            layoutChecks = 0;
            RequestReflow(250);
        }

        private void KeepTaskbarLayer()
        {
            if (inTaskbar && Visible && IsHandleCreated && !Disposing && !held) TaskbarSpace.KeepAboveTaskbar(Handle, Bounds);
        }

        private async void ReflowFromShell()
        {
            if (!Visible || !preferTaskbar || held || editing || layoutPending || IsDisposed || TaskbarSpace.HasShellFocus()) return;
            var desktop = Screen.PrimaryScreen;
            var displayScale = Visuals.ScreenScale(desktop);
            int request = placementRevision;
            var original = confirmedSlot;
            layoutPending = true;
            TaskbarLayout observed;
            try { observed = await Task.Run(() => FindTaskbarSlot(displayScale, original)); }
            finally { layoutPending = false; }
            if (IsDisposed || held || editing || !Visible || !preferTaskbar || request != placementRevision || TaskbarSpace.HasShellFocus()) return;
            ApplyObservation(observed, displayScale);
        }

        private void ApplyObservation(TaskbarLayout observed, float displayScale)
        {
            if (observed.Kind == TaskbarLayoutKind.Unknown ||
                (inTaskbar && observed.Kind == TaskbarLayoutKind.Available && confirmedSlot == observed.Slot && Math.Abs(scale - displayScale) < 0.01f))
            {
                pendingLayout = null;
                return;
            }
            if (inTaskbar)
            {
                if (!pendingLayout.HasValue || !pendingLayout.Value.SameAs(observed))
                {
                    pendingLayout = observed;
                    pendingSince = Environment.TickCount;
                }
                int remaining = LayoutSettleMilliseconds - unchecked(Environment.TickCount - pendingSince);
                if (remaining > 0)
                {
                    RequestReflow(Math.Max(250, remaining));
                    return;
                }
            }
            pendingLayout = null;
            if (observed.Kind == TaskbarLayoutKind.Available)
            {
                scale = displayScale;
                SetPlacement(observed.Slot, true);
            }
            else if (inTaskbar) PlaceFloating(Screen.PrimaryScreen, null);
        }

        private void RequestReflow(int delay)
        {
            if (layoutTimer.Enabled && layoutTimer.Interval <= delay) return;
            layoutTimer.Stop();
            layoutTimer.Interval = delay;
            layoutTimer.Start();
        }

        internal void ScheduleLayout()
        {
            try
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() =>
                {
                    if (!Visible || !preferTaskbar || IsDisposed) return;
                    RequestReflow(250);
                }));
            }
            catch (InvalidOperationException) { }
        }

        internal void DesktopLayoutChanged()
        {
            displayRate = FrameClock.RefreshRate(Screen.FromRectangle(Bounds));
            if (marqueeClock.Rate != 0) marqueeClock.SetRate(displayRate);
            if (preferTaskbar) ScheduleLayout();
            else AnchorToTaskbar();
        }

        internal void SetView(ConnectionView value)
        {
            pendingConnection = value;
            if (Visible && view != null && view.Status == "connected" && value.Status != "connected")
            {
                if (!disconnectGrace.Enabled) disconnectGrace.Start();
                return;
            }
            disconnectGrace.Stop();
            ApplyView(value);
        }

        private void ApplyView(ConnectionView value)
        {
            view = value;
            var media = view.Media;
            string next = view.Status != "connected" ? (view.Status == "offline" ? "正在重连" : Visuals.StatusText(view.Status)) :
                media == null || !media.session ? "手机暂无播放" : media.title == null ? "正在读取曲名…" :
                String.IsNullOrWhiteSpace(media.title) ? "未知曲目" : media.title;
            next = next.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            if (next != titleText)
            {
                titleText = next;
                marqueeStart = Stopwatch.GetTimestamp();
                ClearMarquee();
            }
            ShowLayer();
        }

        internal void SetProgress(PlaybackProgress value)
        {
            if (value == null && disconnectGrace.Enabled) return;
            if (ReferenceEquals(progress, value)) return;
            if (scrubbing && (value == null || progress == null || value.Track != progress.Track || !value.CanSeek))
            {
                scrubbing = held = dragging = false;
                dragTimer.Stop();
                Capture = false;
            }
            if (!scrubbing) seekPreview = null;
            progress = value;
            ShowLayer();
        }

        private Rectangle ProgressArea()
        {
            int begin = Scaled(35);
            int width = Math.Max(1, (int)Math.Round((Width - begin - Scaled(10)) * 0.62));
            return new Rectangle(begin, 0, width, Height);
        }

        private int CurrentPosition()
        {
            if (seekPreview.HasValue) return seekPreview.Value;
            return progress == null ? 0 : progress.CurrentPosition(view != null && view.Media != null && view.Media.playing);
        }

        private void PreviewAt(int x)
        {
            if (progress == null) return;
            var area = Rectangle.Inflate(ProgressArea(), -Scaled(4), 0);
            double fraction = Math.Max(0, Math.Min(1, (double)(x - area.Left) / Math.Max(1, area.Width)));
            int target = (int)Math.Round(fraction * progress.Duration);
            if (seekPreview == target) return;
            seekPreview = target;
            ShowLayer();
        }

        internal void SetTaskbarBackground(int percent)
        {
            taskbarBackground = percent;
            if (inTaskbar) ShowLayer();
        }

        private void ShowLayer() { ShowLayer(false); }

        private void ShowLayer(bool scrollTick)
        {
            if (!Visible || !IsHandleCreated || Disposing || Width < 2 || Height < 2) return;
            var image = surface.GetBitmap(ClientSize, 96 * scale);
            if (image == null) { Rendered = false; return; }
            bool rebuildBuffer = baseFrame == null || baseFrame.Size != image.Size || baseFrame.HorizontalResolution != image.HorizontalResolution;
            if (!scrollTick || rebuildBuffer || CurrentPosition() / 1000 != paintedSecond)
            {
                if (rebuildBuffer)
                {
                    if (baseFrame != null) baseFrame.Dispose();
                    baseFrame = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
                    baseFrame.SetResolution(image.HorizontalResolution, image.VerticalResolution);
                }
                using (var canvas = Graphics.FromImage(baseFrame)) DrawBase(canvas);
            }
            using (var canvas = Graphics.FromImage(image))
            {
                canvas.CompositingMode = CompositingMode.SourceCopy;
                canvas.DrawImageUnscaled(baseFrame, 0, 0);
                canvas.CompositingMode = CompositingMode.SourceOver;
                DrawTitle(canvas);
            }
            Rendered = surface.Present(Handle);
            bool moving = marquee && MarqueeElapsed >= 850;
            if (moving && Rendered && scrollTick) marqueeFrames++;
            marqueeClock.SetRate(moving ? displayRate : 0);
            bool animate = !moving && (marquee || (progress != null && progress.Duration > 0 && progress.Speed != 0 &&
                view != null && view.Media != null && view.Media.playing));
            if (displayTimer.Enabled != animate) displayTimer.Enabled = animate;
        }

        private double MarqueeElapsed { get { return (Stopwatch.GetTimestamp() - marqueeStart) * (1000.0 / Stopwatch.Frequency); } }

        private void RequestMarqueeFrame()
        {
            if (Interlocked.Exchange(ref framePending, 1) != 0) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try { if (marqueeClock.Rate != 0) ShowLayer(true); }
                    finally { Interlocked.Exchange(ref framePending, 0); }
                }));
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref framePending, 0); }
        }

        internal Bitmap CreateImage()
        {
            var image = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            image.SetResolution(96 * scale, 96 * scale);
            try
            {
                using (var canvas = Graphics.FromImage(image)) DrawStrip(canvas);
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        private void DrawStrip(Graphics canvas)
        {
            DrawBase(canvas);
            DrawTitle(canvas);
        }

        private void DrawBase(Graphics canvas)
        {
            // A layered window passes clicks through fully transparent pixels.
            canvas.Clear(Color.FromArgb(1, 0, 0, 0));
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (!inTaskbar || editing || taskbarBackground > 0)
            {
                int alpha = !inTaskbar ? 255 : editing ? 208 : (taskbarBackground * 255 + 50) / 100;
                Color border = editing ? Visuals.Amber : inTaskbar ? Color.Transparent : Visuals.Border;
                Visuals.RoundedRect(canvas, new RectangleF(0.6f, 0.6f, Width - 1.2f, Height - 1.2f),
                    Scaled(8), Color.FromArgb(alpha, Visuals.Panel), border);
            }
            if (view == null) return;
            var media = view.Media;
            var area = ProgressArea();
            var ink = inTaskbar ? TaskbarInk : Visuals.Mint;
            DrawLabel(canvas, media == null ? "—" : media.volume.ToString(), inTaskbar ? taskbarDigits : digits,
                new RectangleF(Scaled(5), 0, Scaled(28), Height), ink);
            DrawProgress(canvas, area, ink);
        }

        private void DrawTitle(Graphics canvas)
        {
            if (view == null) return;
            canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var area = ProgressArea();
            int begin = area.Right + Scaled(7);
            DrawMarquee(canvas, new RectangleF(begin, 0, Math.Max(1, Width - begin - Scaled(4)), Height),
                inTaskbar ? taskbarCaption : caption, inTaskbar ? TaskbarInk : Visuals.Text);
        }

        private void DrawProgress(Graphics canvas, Rectangle area, Color ink)
        {
            int position = CurrentPosition();
            paintedSecond = position / 1000;
            int duration = progress == null ? 0 : progress.Duration;
            float center = area.Height * 0.74f;
            int left = area.Left + Scaled(4), right = area.Right - Scaled(4);
            float fill = duration == 0 ? left : left + (right - left) * (float)position / duration;
            int radius = hoverProgress || scrubbing ? Scaled(3.4) : Scaled(2.2);
            using (var rail = new Pen(Color.FromArgb(110, ink), Math.Max(1, Scaled(1))))
            using (var played = new Pen(ink, Math.Max(2, Scaled(1))))
            using (var thumb = new SolidBrush(ink))
            {
                canvas.DrawLine(rail, left, center, right, center);
                if (fill > left) canvas.DrawLine(played, left, center, fill, center);
                if (progress != null && progress.CanSeek) canvas.FillEllipse(thumb, fill - radius, center - radius, radius * 2, radius * 2);
            }
            using (var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var fillInk = new SolidBrush(ink))
            {
                var upper = new RectangleF(left - Scaled(1), 0, right - left + Scaled(2), area.Height * 0.54f);
                canvas.DrawString(duration > 0 && (progress.Position >= 0 || seekPreview.HasValue) ? RemoteWindow.FormatTime(position) : "—:—", timeFont, fillInk, upper, format);
                format.Alignment = StringAlignment.Far;
                canvas.DrawString(duration == 0 ? "—:—" : RemoteWindow.FormatTime(duration), timeFont, fillInk, upper, format);
            }
        }

        private void DrawMarquee(Graphics canvas, RectangleF area, Font font, Color color)
        {
            if (marqueeText == null || marqueeText.Height != Height || marqueeText.HorizontalResolution != canvas.DpiX ||
                marqueeColor != color || marqueeFont != font)
            {
                ClearMarquee();
                int width = Math.Max(1, (int)Math.Ceiling(canvas.MeasureString(titleText, font).Width) + Scaled(3));
                marqueeText = new Bitmap(width, Height, PixelFormat.Format32bppPArgb);
                marqueeText.SetResolution(canvas.DpiX, canvas.DpiY);
                marqueeColor = color;
                marqueeFont = font;
                using (var title = Graphics.FromImage(marqueeText))
                {
                    title.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    DrawLabel(title, titleText, font, new RectangleF(0, 0, width, Height), color);
                }
            }
            marquee = marqueeText.Width > area.Width;
            if (!marquee) { DrawLabel(canvas, titleText, font, area, color); return; }
            int step = marqueeText.Width + Scaled(20);
            float offset = (float)((Math.Max(0, MarqueeElapsed - 850) * (22 * scale) / 1000) % step);
            var clipping = canvas.Save();
            try
            {
                canvas.SetClip(area);
                canvas.InterpolationMode = InterpolationMode.Bilinear;
                canvas.PixelOffsetMode = PixelOffsetMode.Half;
                var text = new RectangleF(area.X - offset, area.Y, marqueeText.Width, marqueeText.Height);
                var source = new RectangleF(0, 0, marqueeText.Width, marqueeText.Height);
                canvas.DrawImage(marqueeText, text, source, GraphicsUnit.Pixel);
                text.Offset(step, 0);
                canvas.DrawImage(marqueeText, text, source, GraphicsUnit.Pixel);
            }
            finally { canvas.Restore(clipping); }
        }

        private void ClearMarquee()
        {
            if (marqueeText == null) return;
            marqueeText.Dispose();
            marqueeText = null;
        }

        private void DrawLabel(Graphics canvas, string value, Font font, RectangleF area, Color color)
        {
            using (var format = new StringFormat(StringFormatFlags.NoWrap)
            {
                LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter
            })
            using (var ink = new SolidBrush(color))
            {
                if (inTaskbar && taskbarBackground == 0 && !editing)
                {
                    var shade = area;
                    shade.Offset(0, 1);
                    using (var shadow = new SolidBrush(Color.FromArgb(170, 13, 26, 22)))
                        canvas.DrawString(value, font, shadow, shade, format);
                }
                canvas.DrawString(value, font, ink, area, format);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                taskbar.Dispose(); layoutTimer.Dispose(); dragTimer.Dispose(); taskbarTimer.Dispose(); displayTimer.Dispose(); disconnectGrace.Dispose();
                marqueeClock.Dispose(); surface.Dispose(); ClearMarquee();
                if (baseFrame != null) baseFrame.Dispose();
                caption.Dispose(); digits.Dispose(); taskbarCaption.Dispose(); taskbarDigits.Dispose(); timeFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private int Scaled(double value) { return (int)Math.Round(value * scale); }

    }
}
