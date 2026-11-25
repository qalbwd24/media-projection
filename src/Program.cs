using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("媒体投射工具")]
[assembly: AssemblyDescription("用蓝牙查看和控制手机播放的媒体")]
[assembly: AssemblyProduct("媒体投射工具")]
[assembly: AssemblyVersion("1.7.3.0")]
[assembly: AssemblyFileVersion("1.7.3.0")]

namespace PhoneRemote
{
    internal static class Program
    {
        internal static readonly string Executable = Assembly.GetExecutingAssembly().Location;
        internal const string Version = "1.7.3";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string action = args.Length == 0 ? "" : args[0];
            try
            {
                if (action == "--install")
                {
                    Preferences.InstallShortcut();
                    if (new Preferences().AutoStart) new Preferences().SetAutoStart(true);
                    return 0;
                }
                if (action == "--render-preview" && args.Length == 2)
                {
                    Render(args[1]);
                    return 0;
                }
                if (action == "--verify-hotkeys" && args.Length == 2) return VerifyHotkeys(args[1]);
                if (action.Length != 0 && action != "--tray") return 2;
                using (var instance = new Mutex(false, @"Local\MediaProjectionTool." + WindowsIdentity.GetCurrent().User.Value))
                {
                    bool own;
                    try { own = instance.WaitOne(0); }
                    catch (AbandonedMutexException) { own = true; }
                    if (!own) return action == "--tray" || LocalControl.Nudge() ? 0 : 1;
                    try
                    {
                        using (var app = new RemoteApplication(action == "--tray")) Application.Run(app);
                    }
                    finally { instance.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception error)
            {
                Trace.WriteLine(error.ToString());
                if (action != "--tray" && action != "--render-preview" && action != "--verify-hotkeys") MessageBox.Show(error.Message, Preferences.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int VerifyHotkeys(string output)
        {
            const int marker = 0x3154A53;
            var tag = new UIntPtr(marker);
            var seen = new List<string>();
            var repeats = new List<CancellationToken>();
            var results = new List<string>();
            int exitCode = 1;
            using (var hook = new CapsKeys((action, hold) =>
            {
                seen.Add(action);
                if (hold.CanBeCanceled) repeats.Add(hold);
            }, new IntPtr(marker)))
            using (var tick = new System.Windows.Forms.Timer { Interval = 30 })
            {
                if (!hook.Installed) return 1;
                tick.Tick += async (s, e) =>
                {
                    tick.Stop();
                    try
                    {
                        await CheckHotkeys(seen, repeats, results, tag);
                        exitCode = 0;
                    }
                    catch (Exception error) { results.Add(error.Message); }
                    finally
                    {
                        foreach (byte key in new byte[] { 0x25, 0x26, 0x27, 0x28, 0x20, 0x14 }) keybd_event(key, 0, 2, tag);
                        Application.ExitThread();
                    }
                };
                tick.Start();
                Application.Run();
            }
            File.WriteAllLines(output, results);
            return exitCode;
        }

        private static async Task CheckHotkeys(List<string> seen, List<CancellationToken> repeats, List<string> results, UIntPtr tag)
        {
            const byte caps = 0x14;
            var transport = new byte[] { 0x25, 0x27, 0x20 };
            keybd_event(caps, 0, 0, tag);
            foreach (byte key in transport) keybd_event(key, 0, 0, tag);
            await Task.Delay(60);
            ExpectHotkeys(seen, results, "Transport waits for release", "");
            foreach (byte key in transport) keybd_event(key, 0, 2, tag);
            await Task.Delay(60);
            ExpectHotkeys(seen, results, "Short releases", "previous,next,play-pause");

            foreach (byte key in transport) keybd_event(key, 0, 0, tag);
            await Task.Delay(CapsKeys.TapMilliseconds + 100);
            foreach (byte key in transport)
            {
                keybd_event(key, 0, 0, tag);
                keybd_event(key, 0, 2, tag);
            }
            await Task.Delay(60);
            ExpectHotkeys(seen, results, "Long holds and repeated key-downs cancel", "");

            keybd_event(0x20, 0, 0, tag);
            keybd_event(caps, 0, 2, tag);
            await Task.Delay(60);
            ExpectHotkeys(seen, results, "Releasing Caps first waits for the action key", "");
            keybd_event(0x20, 0, 2, tag);
            await Task.Delay(60);
            ExpectHotkeys(seen, results, "Short tap after releasing Caps", "play-pause");

            foreach (byte volume in new byte[] { 0x26, 0x28 })
            {
                string command = CapsKeys.Command(volume);
                keybd_event(caps, 0, 0, tag);
                keybd_event(volume, 0, 0, tag);
                keybd_event(volume, 0, 0, tag);
                await Task.Delay(70);
                ExpectHotkeys(seen, results, command + " starts once on key-down", command);
                if (repeats.Count != 0) throw new InvalidOperationException(command + " repeated too early");
                await Task.Delay(CapsKeys.FirstRepeatMilliseconds + 2 * CapsKeys.RepeatMilliseconds);
                if (seen.Count < 2 || seen.Exists(item => item != command) || repeats.Count != seen.Count ||
                    repeats.Exists(hold => hold.IsCancellationRequested))
                    throw new InvalidOperationException(command + " did not repeat as expected: " + String.Join(",", seen));
                results.Add(command + " hold repeats: " + seen.Count);
                seen.Clear();
                byte releasedFirst = volume == 0x26 ? volume : caps;
                keybd_event(releasedFirst, 0, 2, tag);
                await Task.Delay(CapsKeys.RepeatMilliseconds * 2);
                ExpectHotkeys(seen, results, command + " stops on release of " + (releasedFirst == caps ? "Caps" : "the arrow"), "");
                if (repeats.Exists(hold => !hold.IsCancellationRequested))
                    throw new InvalidOperationException(command + " left held steps active after release");
                repeats.Clear();
                keybd_event(releasedFirst == caps ? volume : caps, 0, 2, tag);
            }
        }

        private static void ExpectHotkeys(List<string> seen, List<string> results, string scenario, string expected)
        {
            var actual = String.Join(",", seen);
            seen.Clear();
            if (actual != expected) throw new InvalidOperationException(scenario + " expected '" + expected + "', got '" + actual + "'.");
            results.Add(scenario + ": OK");
        }

        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

        private static void Render(string output)
        {
            var preferences = new Preferences();
            using (var icon = Icon.ExtractAssociatedIcon(Executable))
            using (var form = new RemoteWindow(preferences, icon))
            using (var bar = new DockWindow(() => { }))
            {
                var sample = new ConnectionView("connected", "Magic7", "", new MediaState
                { v = 1, title = "城市的夜晚 · 一首很长的歌名", session = true, playing = true, volume = 10, min = 0, max = 16, trackRevision = 1 });
                form.Preview = true;
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                form.Show();
                form.SetView(sample);
                var progress = new byte[17];
                progress[0] = progress[16] = 1;
                BitConverter.GetBytes(81000).CopyTo(progress, 4);
                BitConverter.GetBytes(214000).CopyTo(progress, 8);
                form.SetProgress(PlaybackProgress.Decode(progress), true, true);
                bar.LoadPosition(preferences.DockPosition, preferences.DockInTaskbar);
                bar.SetTaskbarBackground(preferences.DockBackground);
                bar.AnchorToTaskbar();
                bar.SetView(sample);
                bar.SetProgress(PlaybackProgress.Decode(progress));
                using (var panelImage = new Bitmap(form.Width, form.Height))
                using (var dockImage = bar.CreateImage())
                {
                    form.DrawToBitmap(panelImage, new Rectangle(Point.Empty, panelImage.Size));
                    panelImage.Save(output, ImageFormat.Png);
                    dockImage.Save(Path.ChangeExtension(output, ".dock.png"), ImageFormat.Png);
                }
                form.WindowState = FormWindowState.Minimized;
                if (form.Visible) throw new InvalidOperationException("The minimized window should be hidden in the tray.");
                form.WindowState = FormWindowState.Normal;
                form.Show();
                form.Close();
                if (form.Visible || form.IsDisposed) throw new InvalidOperationException("Closing the window should keep the tray app available.");
            }
        }
    }
}
