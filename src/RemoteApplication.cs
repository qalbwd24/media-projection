using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PhoneRemote
{
    internal sealed class RemoteApplication : ApplicationContext
    {
        private readonly Control ui = new Control();
        private readonly Preferences preferences = new Preferences();
        private readonly Icon icon = Icon.ExtractAssociatedIcon(Program.Executable);
        private readonly RemoteWindow window;
        private readonly DockWindow bar;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem dockItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly BleController bluetooth;
        private readonly KeyboardService shortcuts;
        private readonly LocalControl local;
        private ConnectionView visible;
        private bool quitting;
        private bool disposed;

        internal RemoteApplication(bool startInTray)
        {
            var handle = ui.Handle;
            window = new RemoteWindow(preferences, icon);
            bar = new DockWindow(ShowRemote);
            bar.LoadPosition(preferences.DockPosition, preferences.DockInTaskbar);
            bar.SetTaskbarBackground(preferences.DockBackground);
            bar.PositionChanged += preferences.SetDockPosition;
            trayMenu.Items.Add(new ToolStripMenuItem("打开媒体投射工具", null, (s, e) => ShowRemote()));
            trayMenu.Items.Add(new ToolStripMenuItem("重新连接", null, (s, e) => Reconnect()));
            trayMenu.Items.Add(new ToolStripSeparator());
            dockItem = new ToolStripMenuItem("曲目小窗", null, (s, e) => SetDock(!preferences.Dock));
            startupItem = new ToolStripMenuItem("登录时启动", null, (s, e) => SetStartup(!preferences.AutoStart));
            trayMenu.Items.Add(dockItem);
            var layout = new ToolStripMenuItem("小窗位置");
            layout.DropDownItems.Add(new ToolStripMenuItem("放到任务栏", null, (s, e) =>
            {
                if (!bar.PutInTaskbar()) tray.ShowBalloonTip(2800, Preferences.AppName, "任务栏目前不可停靠，有空位时会自动贴回。", ToolTipIcon.Info);
            }));
            var move = new ToolStripMenuItem("移动小窗", null, (s, e) => bar.ToggleEditing());
            layout.DropDownItems.Add(move);
            layout.DropDownItems.Add(new ToolStripMenuItem("重置位置", null, (s, e) => bar.ResetPlacement()));
            trayMenu.Items.Add(layout);
            var background = new ToolStripMenuItem("任务栏小窗底色");
            var transparent = new ToolStripMenuItem("透明", null, (s, e) => SetDockBackground(0));
            var subtle = new ToolStripMenuItem("淡底（20% 不透明）", null, (s, e) => SetDockBackground(20));
            var darker = new ToolStripMenuItem("深底（45% 不透明）", null, (s, e) => SetDockBackground(45));
            background.DropDownItems.AddRange(new ToolStripItem[] { transparent, subtle, darker });
            trayMenu.Items.Add(background);
            trayMenu.Opening += (s, e) =>
            {
                layout.Enabled = preferences.Dock;
                move.Text = bar.Editing ? "锁定小窗位置" : "移动小窗";
                background.Enabled = preferences.Dock;
                transparent.Checked = preferences.DockBackground == 0;
                subtle.Checked = preferences.DockBackground == 20;
                darker.Checked = preferences.DockBackground == 45;
            };
            trayMenu.Items.Add(startupItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => ExitRemote()));
            tray = new NotifyIcon { Icon = icon, ContextMenuStrip = trayMenu, Text = Preferences.AppName, Visible = true };
            tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowRemote(); };

            bluetooth = new BleController(preferences.Phone);
            bluetooth.Updated += OnMediaChanged;
            shortcuts = new KeyboardService((command, hold) => bluetooth.Send(command, hold));
            window.Command += command => bluetooth.Send(command);
            window.Seek += bluetooth.SendSeek;
            window.VisibleChanged += (s, e) => UpdateProgress(true);
            bar.Seek += bluetooth.SendSeek;
            bar.VisibleChanged += (s, e) => UpdateProgress(true);
            window.Reconnect += Reconnect;
            window.DockOptionChanged += SetDock;
            window.AutoStartChanged += SetStartup;
            window.Install += Install;
            window.SelectPhone += SelectPhone;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            local = new LocalControl(ReceiveLocalCommand);
            ApplyView(bluetooth.View);
            SyncSettings();
            if (!startInTray) ShowRemote();
        }

        private void ShowRemote() { if (!quitting) window.ShowRemote(); }

        private void Reconnect() { if (!quitting) bluetooth.Reconnect(preferences.Phone); }

        private void OnMediaChanged(ConnectionView ignored) { Post(() => ApplyView(bluetooth.View)); }

        private void ApplyView(ConnectionView view)
        {
            if (quitting) return;
            if (ReferenceEquals(visible, view))
            {
                UpdateProgress(false);
                return;
            }
            var previous = visible == null ? null : visible.Media;
            var current = view.Media;
            bool refreshProgress = current != null && current.session &&
                (previous == null || !previous.session || previous.trackRevision != current.trackRevision || previous.playing != current.playing);
            visible = view;
            window.SetView(view);
            bar.SetView(view);
            tray.Text = Preferences.AppName + " · " + Visuals.StatusText(view.Status);
            UpdateProgress(refreshProgress);
        }

        private void UpdateProgress(bool refresh)
        {
            if (quitting || window.IsDisposed) return;
            bool supported = bluetooth.SupportsProgress;
            var media = visible == null ? null : visible.Media;
            bool active = (window.Visible || bar.Visible) && supported && media != null && media.session;
            if (active && refresh && bluetooth.Progress == null) bluetooth.RequestProgress();
            var progress = bluetooth.Progress;
            window.SetProgress(progress, supported, visible != null && visible.Status == "connected");
            bar.SetProgress(progress);
        }

        private void SyncSettings()
        {
            window.SyncSettings(preferences);
            dockItem.Checked = preferences.Dock;
            startupItem.Checked = preferences.AutoStart;
            if (preferences.Dock)
            {
                bar.AnchorToTaskbar();
                if (!bar.Visible) bar.Show();
            }
            else bar.Hide();
        }

        private void SetDock(bool value)
        {
            preferences.SetDock(value);
            SyncSettings();
        }

        private void SetDockBackground(int percent)
        {
            preferences.SetDockBackground(percent);
            bar.SetTaskbarBackground(percent);
        }

        private void SetStartup(bool value)
        {
            preferences.SetAutoStart(value);
            SyncSettings();
        }

        private void Install()
        {
            try
            {
                Preferences.InstallShortcut();
                MessageBox.Show(window, "已添加。打开开始菜单，搜索「媒体投射工具」。", Preferences.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                MessageBox.Show(window, error.Message, Preferences.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void SelectPhone()
        {
            try
            {
                var devices = await BleLink.PairedNames();
                if (devices.Length == 0)
                {
                    MessageBox.Show(window, "请先在 Windows 蓝牙设置中配对手机。", Preferences.AppName);
                    return;
                }
                using (var dialog = new Form { Text = "选择已配对的手机", StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                    ShowInTaskbar = false, ClientSize = new Size(360, 100), Font = Visuals.Font(10), Icon = icon,
                    AutoScaleMode = AutoScaleMode.None })
                {
                    var names = new ComboBox { Location = new Point(15, 14), Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
                    names.Items.AddRange(devices);
                    names.SelectedIndex = Math.Max(0, Array.IndexOf(devices, preferences.Phone));
                    var confirm = new Button { Text = "选择", Bounds = new Rectangle(168, 55, 83, 31), DialogResult = DialogResult.OK };
                    var cancel = new Button { Text = "取消", Bounds = new Rectangle(262, 55, 83, 31), DialogResult = DialogResult.Cancel };
                    dialog.Controls.AddRange(new Control[] { names, confirm, cancel });
                    dialog.AcceptButton = confirm;
                    dialog.CancelButton = cancel;
                    float scale = Visuals.ScreenScale(Screen.FromControl(window));
                    dialog.Scale(new SizeF(scale, scale));
                    if (dialog.ShowDialog(window) == DialogResult.OK)
                    {
                        preferences.SelectPhone((string)names.SelectedItem);
                        Reconnect();
                    }
                }
            }
            catch (Exception error) { MessageBox.Show(window, error.Message, Preferences.AppName); }
        }

        private void OnDisplayChanged(object sender, EventArgs args) { Post(Reanchor); }
        private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) { Post(Reanchor); }
        private void OnPowerChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Resume) Post(Reconnect);
        }

        private void Reanchor() { if (!quitting && bar.Visible) bar.DesktopLayoutChanged(); }

        private void Post(Action action)
        {
            try { if (!ui.IsDisposed && ui.IsHandleCreated) ui.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private Task<string> ReceiveLocalCommand(string command)
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { result.TrySetResult(PerformLocalCommand(command)); }
                catch (Exception error) { result.TrySetResult(Json(new { ok = false, error = error.Message })); }
            });
            return result.Task;
        }

        private string PerformLocalCommand(string command)
        {
            switch (command)
            {
                case "version": return Json(new { ok = true, version = Program.Version, protocol = 1 });
                case "status":
                    var current = visible;
                    var bounds = bar.Bounds;
                    return Json(new { ok = true, version = Program.Version, status = current.Status, phone = current.Phone,
                        detail = current.Detail, media = current.Media, window = window.Visible, dock = bar.Visible,
                        dockBounds = bar.Visible ? new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height } : null,
                        dockInTaskbar = bar.Visible && bar.InTaskbar, dockPrefersTaskbar = preferences.DockInTaskbar,
                        dockEditing = bar.Visible && bar.Editing,
                        dockBackground = preferences.DockBackground, dockRendered = bar.Visible && bar.Rendered,
                        dockMarqueeHz = bar.MarqueeRate, dockMarqueeFrames = bar.MarqueeFrames,
                        autoStart = preferences.AutoStart, shortcuts = shortcuts.Installed, lastCommand = bluetooth.LastCommand,
                        fastPushes = bluetooth.FastPushes, fullReads = bluetooth.FullReads, summaryReads = bluetooth.SummaryReads,
                        supportsProgress = bluetooth.SupportsProgress, progress = bluetooth.Progress, progressReads = bluetooth.ProgressReads,
                        connectionFailures = bluetooth.ConnectionFailures, lastConnectionFailure = bluetooth.LastConnectionFailure });
                case "progress refresh":
                    bool requested = bluetooth.RequestProgress();
                    return Json(new { ok = requested, queued = requested });
                case "show": ShowRemote(); break;
                case "hide": window.Hide(); break;
                case "reconnect": Reconnect(); break;
                case "dock on": SetDock(true); break;
                case "dock off": SetDock(false); break;
                case "dock taskbar":
                    if (!preferences.Dock) SetDock(true);
                    if (!bar.PutInTaskbar()) return Json(new { ok = false, error = "任务栏目前不可停靠，有空位时会自动贴回。" });
                    break;
                case "dock move": if (preferences.Dock) bar.ToggleEditing(); break;
                case "dock reset": bar.ResetPlacement(); break;
                case "dock background 0": SetDockBackground(0); break;
                case "dock background 20": SetDockBackground(20); break;
                case "dock background 45": SetDockBackground(45); break;
                case "autostart on": SetStartup(true); break;
                case "autostart off": SetStartup(false); break;
                case "exit":
                    // Let this pipe response finish before releasing the Bluetooth connection and closing the app.
                    var exit = new Timer { Interval = 125 };
                    exit.Tick += (s, e) => { exit.Dispose(); ExitRemote(); };
                    exit.Start();
                    break;
                default:
                    if (command.StartsWith("seek ", StringComparison.Ordinal))
                    {
                        int position;
                        var progress = bluetooth.Progress;
                        bool queued = progress != null && Int32.TryParse(command.Substring(5), out position) &&
                            bluetooth.SendSeek(progress.Track, position);
                        return Json(new { ok = queued, queued = queued });
                    }
                    if (BleLink.IsCommand(command))
                    {
                        bool accepted = bluetooth.Send(command);
                        return Json(new { ok = accepted, queued = accepted });
                    }
                    return Json(new { ok = false, error = "未知操作。" });
            }
            return Json(new { ok = true });
        }

        private static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }

        private async void ExitRemote()
        {
            if (quitting) return;
            quitting = true;
            tray.Visible = false;
            bar.Hide();
            shortcuts.Dispose();
            try { await bluetooth.Stop(); }
            finally { ExitThread(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
                SystemEvents.PowerModeChanged -= OnPowerChanged;
                bluetooth.Updated -= OnMediaChanged;
                shortcuts.Dispose();
                local.Dispose();
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
                if (!window.IsDisposed) window.Dispose();
                bar.Dispose();
                ui.Dispose();
                icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
