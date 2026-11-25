using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace PhoneRemote
{
    internal sealed class FrameClock : IDisposable
    {
        private sealed class NativeTimer : WaitHandle
        {
            internal NativeTimer(IntPtr handle) { SafeWaitHandle = new SafeWaitHandle(handle, true); }
        }

        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private readonly Action tick;
        private Thread worker;
        private volatile int rate;
        private volatile bool disposed;

        internal FrameClock(Action tick) { this.tick = tick; }
        internal int Rate { get { return rate; } }

        internal void SetRate(int value)
        {
            if (disposed || rate == value) return;
            rate = value;
            if (worker == null)
            {
                worker = new Thread(Run) { Name = "Taskbar title animation", IsBackground = true };
                worker.Start();
            }
            changed.Set();
        }

        private void Run()
        {
            // TIMER_MODIFY_STATE | SYNCHRONIZE, with a high-resolution one-shot timer.
            IntPtr handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x00100002);
            using (var timer = handle == IntPtr.Zero ? null : new NativeTimer(handle))
            {
                var waits = timer == null ? null : new WaitHandle[] { changed, timer };
                double due = 0;
                int previous = 0;
                while (!disposed)
                {
                    int frequency = rate;
                    if (frequency == 0)
                    {
                        previous = 0;
                        changed.WaitOne();
                        continue;
                    }
                    double interval = Stopwatch.Frequency / (double)frequency;
                    long now = Stopwatch.GetTimestamp();
                    if (frequency != previous || due <= now - interval) due = now + interval;
                    previous = frequency;
                    double remaining = Math.Max(0, due - now);
                    long nativeDelay = -Math.Max(1, (long)Math.Ceiling(remaining * (10000000.0 / Stopwatch.Frequency)));
                    bool interrupted = timer != null && SetWaitableTimer(handle, ref nativeDelay, 0, IntPtr.Zero, IntPtr.Zero, false)
                        ? WaitHandle.WaitAny(waits) == 0
                        : changed.WaitOne((int)Math.Ceiling(remaining * (1000.0 / Stopwatch.Frequency)));
                    if (interrupted || disposed || rate != frequency) { previous = 0; continue; }
                    tick();
                    due += interval;
                }
            }
        }

        internal static int RefreshRate(Screen screen)
        {
            var dc = CreateDC("DISPLAY", screen.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return 60;
            try
            {
                int frequency = GetDeviceCaps(dc, 116);
                return frequency > 1 ? Math.Min(120, frequency) : 60;
            }
            finally { DeleteDC(dc); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (worker != null)
            {
                changed.Set();
                worker.Join();
            }
            changed.Dispose();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerEx(
            IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr handle, ref long dueTime,
            int period, IntPtr completion, IntPtr argument, bool resume);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(
            string driver, string device, string output, IntPtr devMode);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    }
}
