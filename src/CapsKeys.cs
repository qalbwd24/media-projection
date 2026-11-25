using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PhoneRemote {
    internal sealed class CapsKeys : IDisposable {
        const int Caps = 0x14;
        internal const int TapMilliseconds = 350;
        internal const int FirstRepeatMilliseconds = 350;
        internal const int RepeatMilliseconds = 150;
        readonly Action<string, CancellationToken> action;
        readonly Hook callback;
        readonly IntPtr diagnosticTag;
        readonly HashSet<int> pressed = new HashSet<int>();
        readonly Dictionary<int, long> owned = new Dictionary<int, long>();
        readonly System.Windows.Forms.Timer volumeRepeat = new System.Windows.Forms.Timer();
        IntPtr handle;
        bool caps;
        bool chordUsed;
        int repeatKey;
        CancellationTokenSource hold;
        string order;

        internal CapsKeys(Action<string, CancellationToken> action, IntPtr diagnosticTag = default(IntPtr)) {
            this.action = action;
            this.diagnosticTag = diagnosticTag;
            callback = OnKey;
            volumeRepeat.Tick += RepeatVolume;
            order = OtherHooks();
            Install();
        }

        internal bool Installed { get { return handle != IntPtr.Zero; } }

        void Install() { handle = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0); }

        static string OtherHooks() {
            var ids = new List<int>();
            foreach (var name in new[] { "AutoHotkey64", "AutoHotkey", "AutoHotkey32" })
                foreach (var item in Process.GetProcessesByName(name))
                    using (item) ids.Add(item.Id);
            ids.Sort();
            return String.Join(",", ids);
        }

        internal void CheckOrder() {
            var current = OtherHooks();
            if (current == order && Installed) return;
            if (caps || owned.Count != 0) return;
            Uninstall();
            order = current;
            Install();
        }

        internal static string Command(int key) {
            switch (key) {
                case 0x25: return "previous";
                case 0x26: return "volume-up";
                case 0x27: return "next";
                case 0x28: return "volume-down";
                case 0x20: return "play-pause";
                default: return null;
            }
        }

        private static bool IsVolume(int key) { return key == 0x26 || key == 0x28; }

        private bool HandleCommandKey(int key, string command, bool down) {
            if (down) {
                if (!pressed.Add(key) || !caps) return owned.ContainsKey(key);
                chordUsed = true;
                owned.Add(key, Stopwatch.GetTimestamp());
                if (IsVolume(key)) {
                    StopVolumeRepeat();
                    repeatKey = key;
                    volumeRepeat.Interval = FirstRepeatMilliseconds;
                    volumeRepeat.Start();
                    action(command, CancellationToken.None);
                }
                return true;
            }

            pressed.Remove(key);
            long began;
            if (!owned.TryGetValue(key, out began)) return false;
            owned.Remove(key);
            if (key == repeatKey) StopVolumeRepeat();
            if (!IsVolume(key) && Stopwatch.GetTimestamp() - began <= Stopwatch.Frequency * TapMilliseconds / 1000)
                action(command, CancellationToken.None);
            return true;
        }

        private void RepeatVolume(object sender, EventArgs args) {
            if (repeatKey == 0) return;
            if (hold == null) hold = new CancellationTokenSource();
            volumeRepeat.Interval = RepeatMilliseconds;
            action(Command(repeatKey), hold.Token);
        }

        private void StopVolumeRepeat() {
            if (repeatKey == 0) return;
            repeatKey = 0;
            volumeRepeat.Stop();
            if (hold != null) {
                hold.Cancel();
                hold.Dispose();
                hold = null;
            }
        }

        IntPtr OnKey(int code, IntPtr message, IntPtr data) {
            if (code >= 0) {
                bool injected = (Marshal.ReadInt32(data, 8) & 0x10) != 0;
                bool test = diagnosticTag != IntPtr.Zero;
                if (test ? !injected || Marshal.ReadIntPtr(data, 16) != diagnosticTag : injected)
                    return CallNextHookEx(handle, code, message, data);
                int key = Marshal.ReadInt32(data);
                int kind = message.ToInt32();
                bool up = kind == 0x101 || kind == 0x105;
                bool down = kind == 0x100 || kind == 0x104;
                if (key == Caps && (up || down)) {
                    bool wasHeld = caps;
                    if (down && !caps) chordUsed = false;
                    caps = down;
                    if (up) StopVolumeRepeat();
                    if (test) return new IntPtr(1);
                    if (order.Length == 0) {
                        if (up && wasHeld && !chordUsed) {
                            keybd_event(Caps, 0, 0, UIntPtr.Zero);
                            keybd_event(Caps, 0, 2, UIntPtr.Zero);
                        }
                        return new IntPtr(1);
                    }
                }
                else {
                    var command = Command(key);
                    if (command != null && (up || down) && HandleCommandKey(key, command, down))
                        return new IntPtr(1);
                }
                if (test) return new IntPtr(1);
            }
            return CallNextHookEx(handle, code, message, data);
        }

        private void Uninstall() {
            if (handle != IntPtr.Zero) UnhookWindowsHookEx(handle);
            handle = IntPtr.Zero;
        }

        public void Dispose() {
            Uninstall();
            StopVolumeRepeat();
            volumeRepeat.Dispose();
        }

        delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    }

    internal sealed class KeyboardService : IDisposable {
        readonly Thread thread;
        readonly Action<string, CancellationToken> dispatch;
        Control messages;
        volatile bool closing;
        internal volatile bool Installed;

        internal KeyboardService(Action<string, CancellationToken> dispatch) {
            this.dispatch = dispatch;
            thread = new Thread(Pump) { IsBackground = true, Name = "Media shortcuts" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        void Pump() {
            using (var queue = new Control())
            using (var hook = new CapsKeys(dispatch))
            using (var reorder = new System.Windows.Forms.Timer { Interval = 12000 }) {
                var native = queue.Handle;
                messages = queue;
                Installed = hook.Installed;
                reorder.Tick += (s, e) => { hook.CheckOrder(); Installed = hook.Installed; };
                reorder.Start();
                if (!closing) Application.Run();
            }
            Installed = false;
        }

        public void Dispose() {
            closing = true;
            var queue = messages;
            if (queue != null && queue.IsHandleCreated) {
                try { queue.BeginInvoke(new Action(Application.ExitThread)); }
                catch (InvalidOperationException) { }
            }
        }
    }
}
