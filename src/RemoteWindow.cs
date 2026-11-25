using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PhoneRemote
{
    internal sealed class RemoteWindow : Form
    {
        private readonly TitleGlyph mark = new TitleGlyph();
        private readonly Label phone;
        private readonly Label badge;
        private readonly Label mode;
        private readonly Label title;
        private readonly Label volume;
        private readonly Label detail;
        private readonly Label elapsed;
        private readonly Label total;
        private readonly SeekSlider timeline = new SeekSlider();
        private readonly Timer timelineTimer = new Timer { Interval = 250 };
        private readonly MediaButton previous;
        private readonly MediaButton toggle;
        private readonly MediaButton next;
        private readonly MediaButton decrease;
        private readonly MediaButton increase;
        private readonly VolumeMeter meter = new VolumeMeter();
        private readonly CheckBox dock;
        private readonly CheckBox autoStart;
        private readonly ToolTip tips = new ToolTip();
        private bool placingSettings;
        private bool allowClose;
        private bool timelinePlaying;
        private bool seekPending;
        private uint currentTrack;
        private PlaybackProgress progress;
        private float currentScale = 1f;
        internal bool Preview { get; set; }

        internal event Action<string> Command;
        internal event Func<uint, int, bool> Seek;
        internal event Action Reconnect;
        internal event Action SelectPhone;
        internal event Action Install;
        internal event Action<bool> DockOptionChanged;
        internal event Action<bool> AutoStartChanged;

        internal RemoteWindow(Preferences preferences, Icon appIcon)
        {
            AutoScaleMode = AutoScaleMode.None;
            SuspendLayout();
            Text = Preferences.AppName;
            Icon = appIcon;
            ClientSize = new Size(468, 521);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Visuals.Background;
            ForeColor = Visuals.Text;
            Font = Visuals.Font(10);

            Put(this, mark, 24, 23, 42, 42);
            TextLabel(this, Preferences.AppName, 80, 20, 250, 33, 17, Visuals.Text, FontStyle.Bold);
            phone = TextLabel(this, preferences.Phone, 81, 54, 245, 21, 9, Visuals.Muted);
            badge = TextLabel(this, "● 连接中", 344, 27, 100, 29, 10, Visuals.Amber, FontStyle.Bold);
            badge.BackColor = Visuals.Panel;
            badge.TextAlign = ContentAlignment.MiddleCenter;

            var player = new Card();
            Put(this, player, 24, 89, 420, 220);
            mode = TextLabel(player, "手机播放", 17, 13, 380, 25, 10, Visuals.Muted, FontStyle.Bold);
            title = TextLabel(player, "等待手机连接", 17, 38, 385, 41, 19, Visuals.Text, FontStyle.Bold);
            Put(player, timeline, 17, 82, 386, 25);
            elapsed = TextLabel(player, "—:—", 17, 106, 132, 24, 9, Visuals.Muted);
            total = TextLabel(player, "—:—", 262, 106, 141, 24, 9, Visuals.Muted);
            total.TextAlign = ContentAlignment.MiddleRight;
            timelineTimer.Tick += (s, e) => RenderProgress();
            timeline.PreviewChanged += RenderProgress;
            timeline.Committed += position =>
            {
                var handler = Seek;
                seekPending = handler != null && handler(currentTrack, position);
                RenderProgress();
            };
            previous = Transport(player, "previous", "上一曲", "previous", 79, 151, 63, 49);
            toggle = Transport(player, "play", "播放 / 暂停", "play-pause", 153, 148, 114, 55, true);
            next = Transport(player, "next", "下一曲", "next", 278, 151, 63, 49);
            tips.SetToolTip(previous, "上一曲 · Caps + ←");
            tips.SetToolTip(toggle, "播放 / 暂停 · Caps + 空格");
            tips.SetToolTip(next, "下一曲 · Caps + →");

            var levels = new Card();
            Put(this, levels, 24, 322, 420, 83);
            TextLabel(levels, "媒体音量", 17, 9, 155, 23, 10, Visuals.Muted, FontStyle.Bold);
            volume = TextLabel(levels, "— / 16", 285, 8, 80, 24, 11, Visuals.Text, FontStyle.Bold);
            volume.TextAlign = ContentAlignment.MiddleRight;
            decrease = Transport(levels, "minus", "调低音量", "volume-down", 17, 36, 36, 32);
            increase = Transport(levels, "plus", "调高音量", "volume-up", 368, 36, 36, 32);
            Put(levels, meter, 64, 43, 294, 18);
            tips.SetToolTip(decrease, "调低音量 · Caps + ↓");
            tips.SetToolTip(increase, "调高音量 · Caps + ↑");

            dock = Option("曲目小窗", 24, 416);
            autoStart = Option("登录时启动", 174, 416);
            LinkButton("重连", 384, 418, 60, () => { var action = Reconnect; if (action != null) action(); });
            Put(this, new Panel { BackColor = Visuals.Border }, 24, 454, 420, 1);
            detail = TextLabel(this, "Caps + ←/→ 切歌   ↑/↓ 音量   空格 播放/暂停", 24, 463, 420, 23, 9, Visuals.Muted);
            LinkButton("切换手机", 24, 491, 78, () => { var action = SelectPhone; if (action != null) action(); });
            LinkButton("添加到开始菜单", 320, 491, 124, () => { var action = Install; if (action != null) action(); });
            SyncSettings(preferences);
            dock.CheckedChanged += (s, e) => { var action = DockOptionChanged; if (!placingSettings && action != null) action(dock.Checked); };
            autoStart.CheckedChanged += (s, e) => { var action = AutoStartChanged; if (!placingSettings && action != null) action(autoStart.Checked); };
            SetView(new ConnectionView(String.IsNullOrWhiteSpace(preferences.Phone) ? "unselected" : "connecting", preferences.Phone, "", null));
            SetScale(Visuals.ScreenScale(Screen.PrimaryScreen));
            ResumeLayout(false);
        }

        private void SetScale(float scale)
        {
            if (scale <= 0 || Math.Abs(scale - currentScale) < 0.01f) return;
            SuspendLayout();
            float relative = scale / currentScale;
            currentScale = scale;
            Scale(new SizeF(relative, relative));
            ClientSize = new Size((int)Math.Round(468 * scale), (int)Math.Round(521 * scale));
            ResumeLayout(true);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x02E0)
            {
                SetScale((message.WParam.ToInt32() & 0xffff) / 96f);
                if (message.LParam != IntPtr.Zero) Location = new Point(Marshal.ReadInt32(message.LParam), Marshal.ReadInt32(message.LParam, 4));
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
        }

        internal void SyncSettings(Preferences settings)
        {
            placingSettings = true;
            dock.Checked = settings.Dock;
            autoStart.Checked = settings.AutoStart;
            placingSettings = false;
        }

        internal void SetView(ConnectionView view)
        {
            phone.Text = view.Status == "unselected" ? "蓝牙媒体遥控" : view.Phone + "  ·  蓝牙";
            tips.SetToolTip(phone, view.Phone);
            badge.Text = "● " + Visuals.StatusText(view.Status);
            badge.ForeColor = Visuals.StatusColor(view.Status);
            bool online = view.Status == "connected";
            var media = view.Media;
            bool hasMedia = online && media != null && media.session;
            bool isPlaying = hasMedia && media.playing;
            timelinePlaying = isPlaying;
            uint track = hasMedia ? media.trackRevision : 0;
            if (track != currentTrack)
            {
                currentTrack = track;
                seekPending = false;
                timeline.Cancel();
            }
            mode.Text = !online ? "手机播放" : !hasMedia ? "手机暂无播放" : isPlaying ? "正在播放" : "已暂停";
            mode.ForeColor = isPlaying ? Visuals.Mint : Visuals.Muted;
            title.Text = view.Status == "unselected" ? "选择已配对的手机" : !online ? "等待手机连接" : !hasMedia ? "打开手机播放器" :
                media.title == null ? "正在读取曲名…" : String.IsNullOrWhiteSpace(media.title) ? "未知曲目" : media.title;
            tips.SetToolTip(title, title.Text);
            volume.Text = media == null ? "— / 16" : media.volume + " / " + media.max;
            meter.SetLevel(media);
            toggle.SetSymbol(isPlaying ? "pause" : "play", isPlaying ? "暂停" : "播放");
            previous.Enabled = toggle.Enabled = next.Enabled = hasMedia;
            decrease.Enabled = online && media != null && media.volume > media.min;
            increase.Enabled = online && media != null && media.volume < media.max;
            detail.Text = String.IsNullOrWhiteSpace(view.Detail) ? "Caps + ←/→ 切歌   ↑/↓ 音量   空格 播放/暂停" : view.Detail;
            detail.ForeColor = String.IsNullOrWhiteSpace(view.Detail) ? Visuals.Muted : Visuals.Amber;
            tips.SetToolTip(detail, detail.Text);
        }

        internal void SetProgress(PlaybackProgress sample, bool supported, bool online)
        {
            if (sample != null && (sample.Track != currentTrack || currentTrack == 0)) sample = null;
            if (!ReferenceEquals(progress, sample)) seekPending = false;
            progress = sample;
            int duration = progress == null ? 0 : progress.Duration;
            bool canSeek = progress != null && progress.CanSeek;
            timeline.SetRange(duration, canSeek);
            string hint = !online ? "等待蓝牙连接" : !supported ? "更新手机端后可以读取进度" :
                currentTrack == 0 ? "手机暂无播放" : progress == null ? "正在读取播放进度" :
                canSeek ? "点击或拖动到指定位置" : "当前播放器未提供进度跳转";
            tips.SetToolTip(timeline, hint);
            timeline.AccessibleDescription = hint;
            SyncTimeline();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Disposing || IsDisposed || elapsed == null) return;
            if (!Visible) { seekPending = false; timeline.Cancel(); }
            SyncTimeline();
        }

        private void SyncTimeline()
        {
            timelineTimer.Enabled = Visible && timelinePlaying && progress != null && progress.Duration > 0;
            RenderProgress();
        }

        private void RenderProgress()
        {
            if (progress == null || progress.Duration == 0)
            {
                elapsed.Text = total.Text = "—:—";
                return;
            }
            if (!timeline.Scrubbing && !seekPending) timeline.SetValue(progress.CurrentPosition(timelinePlaying));
            elapsed.Text = progress.Position < 0 && !timeline.Scrubbing && !seekPending ? "—:—" : FormatTime(timeline.Value);
            total.Text = FormatTime(progress.Duration);
        }

        internal static string FormatTime(int milliseconds)
        {
            int seconds = milliseconds / 1000;
            return seconds >= 3600 ? String.Format("{0}:{1:00}:{2:00}", seconds / 3600, seconds / 60 % 60, seconds % 60) :
                String.Format("{0}:{1:00}", seconds / 60, seconds % 60);
        }

        internal void ShowRemote()
        {
            WindowState = FormWindowState.Normal;
            if (!Visible) Show();
            Activate();
        }

        internal void CloseForExit() { allowClose = true; Close(); }

        protected override bool ShowWithoutActivation { get { return Preview; } }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) Hide();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            int caption = ColorTranslator.ToWin32(Visuals.Background);
            int label = ColorTranslator.ToWin32(Visuals.Text);
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(Handle, 36, ref label, sizeof(int));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timelineTimer.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }

        private MediaButton Transport(Control parent, string icon, string caption, string command, int x, int y,
            int width, int height, bool primary = false)
        {
            var button = new MediaButton(icon, caption, primary);
            button.Click += (sender, args) => { var handler = Command; if (handler != null) handler(command); };
            Put(parent, button, x, y, width, height);
            return button;
        }

        private CheckBox Option(string caption, int x, int y)
        {
            var check = new CheckBox { Text = caption, AutoSize = false, ForeColor = Visuals.Text, Cursor = Cursors.Hand };
            Put(this, check, x, y, 132, 30);
            return check;
        }

        private void LinkButton(string caption, int x, int y, int width, Action action)
        {
            var link = new LinkLabel { Text = caption, LinkColor = Visuals.Muted, ActiveLinkColor = Visuals.Mint,
                VisitedLinkColor = Visuals.Muted, LinkBehavior = LinkBehavior.HoverUnderline, Font = Visuals.Font(9) };
            link.LinkClicked += (sender, args) => action();
            Put(this, link, x, y, width, 22);
        }

        private static Label TextLabel(Control parent, string value, int x, int y, int w, int h, float font,
            Color color, FontStyle style = FontStyle.Regular)
        {
            var label = new Label { Text = value, Font = Visuals.Font(font, style), ForeColor = color,
                BackColor = parent is Card ? Visuals.Panel : Visuals.Background, AutoEllipsis = true, UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft };
            Put(parent, label, x, y, w, h);
            return label;
        }

        private static void Put(Control parent, Control control, int x, int y, int w, int h)
        {
            control.SetBounds(x, y, w, h);
            parent.Controls.Add(control);
        }

        private sealed class TitleGlyph : Control
        {
            protected override void OnPaint(PaintEventArgs e) { Visuals.DrawMark(e.Graphics, ClientRectangle); }
        }

        private sealed class SeekSlider : Control
        {
            private int duration;
            internal int Value { get; private set; }
            internal bool Scrubbing { get; private set; }
            internal event Action PreviewChanged;
            internal event Action<int> Committed;

            internal SeekSlider()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
                AccessibleName = "播放进度";
                AccessibleRole = AccessibleRole.Slider;
                BackColor = Visuals.Panel;
                Enabled = false;
            }

            internal void SetRange(int length, bool seekable)
            {
                if (duration != length || Enabled != seekable)
                {
                    Cancel();
                    duration = length;
                    Enabled = seekable;
                    Cursor = seekable ? Cursors.Hand : Cursors.Default;
                    SetValue(Value);
                    Invalidate();
                }
            }

            internal void SetValue(int position)
            {
                position = Math.Max(0, Math.Min(duration, position));
                if (Value == position) return;
                Value = position;
                Invalidate();
            }

            internal void Cancel()
            {
                if (!Scrubbing) return;
                Scrubbing = false;
                Capture = false;
            }

            private float Inset { get { return Math.Max(4, Height * 0.24f); } }
            private float Span { get { return Math.Max(1, Width - 2 * Inset); } }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float y = Height / 2f, x = Inset + Span * Value / Math.Max(1, duration);
                float stroke = Math.Max(2, Height * 0.14f), radius = Inset * 0.72f;
                using (var rail = new Pen(Visuals.Border, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var played = new Pen(Enabled ? Visuals.Mint : Visuals.Muted, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var thumb = new SolidBrush(Visuals.Mint))
                {
                    g.DrawLine(rail, Inset, y, Inset + Span, y);
                    if (x > Inset) g.DrawLine(played, Inset, y, x, y);
                    if (Enabled) g.FillEllipse(thumb, x - radius, y - radius, radius * 2, radius * 2);
                }
                if (Focused) ControlPaint.DrawFocusRectangle(g, ClientRectangle, Visuals.Muted, BackColor);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || !Enabled) return;
                Focus();
                Scrubbing = true;
                Capture = true;
                PreviewAt(e.X);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (Scrubbing) PreviewAt(e.X);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (!Scrubbing || e.Button != MouseButtons.Left) return;
                PreviewAt(e.X);
                Cancel();
                var handler = Committed;
                if (handler != null) handler(Value);
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (!Capture && Scrubbing)
                {
                    Scrubbing = false;
                    var handler = PreviewChanged;
                    if (handler != null) handler();
                }
            }

            private void PreviewAt(int x)
            {
                double fraction = Math.Max(0, Math.Min(1, (x - Inset) / Span));
                SetValue((int)Math.Round(fraction * duration));
                var handler = PreviewChanged;
                if (handler != null) handler();
            }

            protected override bool IsInputKey(Keys keyData)
            {
                return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (!Enabled || Scrubbing) return;
                long target;
                switch (e.KeyData)
                {
                    case Keys.Left: target = (long)Value - 5000; break;
                    case Keys.Right: target = (long)Value + 5000; break;
                    case Keys.Home: target = 0; break;
                    case Keys.End: target = duration; break;
                    default: return;
                }
                SetValue((int)Math.Max(0, Math.Min(duration, target)));
                var handler = Committed;
                if (handler != null) handler(Value);
                e.Handled = true;
            }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
