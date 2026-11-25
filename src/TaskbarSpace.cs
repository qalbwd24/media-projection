using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace PhoneRemote
{
    internal enum TaskbarLayoutKind { Unknown, Unavailable, NoRoom, Available }

    internal struct TaskbarLayout
    {
        internal readonly TaskbarLayoutKind Kind;
        internal readonly Rectangle Bar;
        internal readonly Rectangle Slot;

        internal TaskbarLayout(TaskbarLayoutKind kind, Rectangle bar, Rectangle slot = default(Rectangle))
        {
            Kind = kind;
            Bar = bar;
            Slot = slot;
        }

        internal bool SameAs(TaskbarLayout other) { return Kind == other.Kind && Bar == other.Bar && Slot == other.Slot; }
    }

    internal static class TaskbarSpace
    {
        private const int GetState = 4;
        private const int GetPosition = 5;
        private const uint PreviousWindow = 3;
        private const uint PreserveBoundsAndFocus = 0x0001 | 0x0002 | 0x0010 | 0x0200;

        internal static void KeepAboveTaskbar(IntPtr dock, Rectangle bounds)
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return;

            var above = GetWindow(dock, PreviousWindow);
            for (int inspected = 0; above != IntPtr.Zero && inspected < 128; inspected++, above = GetWindow(above, PreviousWindow))
            {
                if (above == taskbar)
                {
                    if (!OverlapsVisibleWindow(taskbar, bounds)) return;
                    // Insert just above Explorer, underneath the windows already above it.
                    var predecessor = GetWindow(taskbar, PreviousWindow);
                    if (predecessor != dock)
                        SetWindowPos(dock, predecessor == IntPtr.Zero ? new IntPtr(-1) : predecessor, 0, 0, 0, 0, PreserveBoundsAndFocus);
                    return;
                }

                // Leave visible overlays between the dock and taskbar in front.
                if (OverlapsVisibleWindow(above, bounds)) return;
            }
        }

        private static bool OverlapsVisibleWindow(IntPtr window, Rectangle bounds)
        {
            NativeRect rect;
            if (!IsWindowVisible(window) || !GetWindowRect(window, out rect) ||
                !bounds.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom))) return false;
            int cloaked;
            return DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) != 0 || cloaked == 0;
        }

        internal static Rectangle Bounds()
        {
            Rectangle bounds;
            return ReadBounds(out bounds) == TaskbarLayoutKind.Available ? bounds : Rectangle.Empty;
        }

        internal static bool HasShellFocus()
        {
            var focused = GetForegroundWindow();
            if (focused == IntPtr.Zero) return false;
            var buffer = new StringBuilder(128);
            GetClassName(focused, buffer, buffer.Capacity);
            switch (buffer.ToString())
            {
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "NotifyIconOverflowWindow":
                case "TopLevelWindowForOverflowXamlIsland": return true;
                case "Windows.UI.Core.CoreWindow":
                case "XamlExplorerHostIslandWindow":
                case "Windows.UI.Composition.DesktopWindowContentBridge":
                    uint pid;
                    GetWindowThreadProcessId(focused, out pid);
                    try
                    {
                        using (var process = Process.GetProcessById((int)pid))
                        {
                            var name = process.ProcessName;
                            return name == "ShellExperienceHost" || name == "ShellHost" || name == "explorer";
                        }
                    }
                    catch (ArgumentException) { return false; }
                    catch (InvalidOperationException) { return false; }
                default: return false;
            }
        }

        private static TaskbarLayoutKind ReadBounds(out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            var data = new AppBarData { Size = (uint)Marshal.SizeOf(typeof(AppBarData)) };
            if (SHAppBarMessage(GetPosition, ref data) == UIntPtr.Zero) return TaskbarLayoutKind.Unknown;
            bounds = Rectangle.FromLTRB(data.Location.Left, data.Location.Top, data.Location.Right, data.Location.Bottom);
            if (bounds.Width <= 0 || bounds.Height <= 0) return TaskbarLayoutKind.Unknown;
            if ((SHAppBarMessage(GetState, ref data).ToUInt64() & 1) != 0 || bounds.Width <= bounds.Height * 2 || bounds.Height < 24)
                return TaskbarLayoutKind.Unavailable;
            return TaskbarLayoutKind.Available;
        }

        internal static TaskbarLayout FindSlot(int preferredWidth, int minimumWidth, int margin, Rectangle current)
        {
            Rectangle bar;
            var kind = ReadBounds(out bar);
            if (kind != TaskbarLayoutKind.Available) return new TaskbarLayout(kind, bar);
            int height = bar.Height / 2;
            try
            {
                var root = TaskbarElement();
                if (root == null) return new TaskbarLayout(TaskbarLayoutKind.Unknown, bar);
                int shellPid = root.Current.ProcessId;
                var controls = root.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                    new PropertyCondition(AutomationElement.ProcessIdProperty, shellPid),
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))));
                var occupied = new List<Rectangle>();
                int trayLeft = bar.Right;
                for (int index = 0; index < controls.Count; index++)
                {
                    var information = controls[index].Current;
                    var rect = information.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width < 2 || rect.Height < 2) continue;
                    var native = Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top),
                        (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
                    if (!IsTaskbarControl(bar, native)) continue;
                    occupied.Add(Rectangle.Intersect(native, bar));
                    if (information.AutomationId == "SystemTrayIcon" && native.Left > bar.Left + bar.Width / 2)
                        trayLeft = Math.Min(trayLeft, native.Left);
                }
                if (trayLeft == bar.Right)
                    return Resolve(bar, occupied, current, bar.Right - preferredWidth - margin, preferredWidth, minimumWidth, height, margin);
                var leftOfTray = Rectangle.FromLTRB(bar.Left, bar.Top, trayLeft, bar.Bottom);
                occupied.RemoveAll(control => control.Left >= trayLeft);
                var dock = Resolve(leftOfTray, occupied, Rectangle.Empty, trayLeft - preferredWidth - margin,
                    preferredWidth, minimumWidth, height, margin);
                return new TaskbarLayout(dock.Kind, bar, dock.Slot);
            }
            catch (ElementNotAvailableException) { return new TaskbarLayout(TaskbarLayoutKind.Unknown, bar); }
            catch (COMException) { return new TaskbarLayout(TaskbarLayoutKind.Unknown, bar); }
            catch (InvalidOperationException) { return new TaskbarLayout(TaskbarLayoutKind.Unknown, bar); }
        }

        internal static bool IsTaskbarControl(Rectangle bar, Rectangle control)
        {
            return Rectangle.Inflate(bar, 2, 2).Contains(control) && bar.IntersectsWith(control);
        }

        internal static TaskbarLayout Resolve(Rectangle bar, List<Rectangle> occupied, Rectangle current,
            int desiredLeft, int preferredWidth, int minimumWidth, int height, int margin)
        {
            if (occupied.Count == 0) return new TaskbarLayout(TaskbarLayoutKind.Unknown, bar);
            var guarded = Rectangle.Inflate(current, margin, 0);
            if (current.Height == height && current.Top == bar.Top + (bar.Height - height) / 2 &&
                current.Width >= minimumWidth && current.Width <= preferredWidth && bar.Contains(guarded) &&
                !occupied.Exists(control => control.IntersectsWith(guarded)))
                return new TaskbarLayout(TaskbarLayoutKind.Available, bar, current);
            var slot = Choose(bar, occupied, desiredLeft, preferredWidth, minimumWidth, height, margin);
            return new TaskbarLayout(slot.IsEmpty ? TaskbarLayoutKind.NoRoom : TaskbarLayoutKind.Available, bar, slot);
        }

        internal static Rectangle Choose(Rectangle bar, List<Rectangle> occupied, int desiredLeft, int preferredWidth,
            int minimumWidth, int height, int margin)
        {
            occupied.Sort((first, second) => first.Left.CompareTo(second.Left));
            int left = bar.Left;
            var best = Rectangle.Empty;
            double bestDistance = Double.MaxValue;
            for (int index = 0; index <= occupied.Count; index++)
            {
                int right = index == occupied.Count ? bar.Right : occupied[index].Left;
                int available = right - left - 2 * margin;
                if (available >= minimumWidth)
                {
                    int width = Math.Min(preferredWidth, available);
                    int position = Math.Max(left + margin, Math.Min(desiredLeft, right - margin - width));
                    double distance = Math.Abs(position - (double)desiredLeft);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = new Rectangle(position, bar.Top + (bar.Height - height) / 2, width, height);
                    }
                }
                if (index < occupied.Count) left = Math.Max(left, occupied[index].Right);
            }
            return best;
        }

        internal static AutomationElement TaskbarElement()
        {
            var handle = FindWindow("Shell_TrayWnd", null);
            return handle == IntPtr.Zero ? null : AutomationElement.FromHandle(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Callback;
            internal uint Edge;
            internal NativeRect Location;
            internal IntPtr Parameter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }

        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(int message, ref AppBarData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string caption);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint direction);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    }

    internal sealed class TaskbarObserver : IDisposable
    {
        private readonly Action changed;
        private readonly object sync = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread worker;
        private bool enabled;
        private bool disposed;

        internal TaskbarObserver(Action changed) { this.changed = changed; }

        internal void SetEnabled(bool value)
        {
            lock (sync)
            {
                if (disposed || enabled == value) return;
                enabled = value;
                if (worker == null)
                {
                    worker = new Thread(Observe) { IsBackground = true, Name = "Taskbar layout events" };
                    worker.SetApartmentState(ApartmentState.MTA);
                    worker.Start();
                }
                wake.Set();
            }
        }

        private void Observe()
        {
            AutomationElement root = null;
            try
            {
                while (true)
                {
                    wake.WaitOne();
                    bool active;
                    lock (sync)
                    {
                        if (disposed) return;
                        active = enabled;
                    }
                    if (active && root == null) root = Subscribe();
                    if (!active && root != null)
                    {
                        Unsubscribe(root);
                        root = null;
                    }
                }
            }
            finally
            {
                if (root != null) Unsubscribe(root);
                wake.Dispose();
            }
        }

        private AutomationElement Subscribe()
        {
            AutomationElement root = null;
            try
            {
                root = TaskbarSpace.TaskbarElement();
                if (root == null) return null;
                Automation.AddStructureChangedEventHandler(root, TreeScope.Descendants, OnStructure);
                Automation.AddAutomationPropertyChangedEventHandler(root, TreeScope.Subtree, OnProperty,
                    AutomationElement.BoundingRectangleProperty, AutomationElement.IsOffscreenProperty);
                return root;
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            if (root != null) Unsubscribe(root);
            return null;
        }

        private void Unsubscribe(AutomationElement root)
        {
            try { Automation.RemoveStructureChangedEventHandler(root, OnStructure); } catch (Exception) { }
            try { Automation.RemoveAutomationPropertyChangedEventHandler(root, OnProperty); } catch (Exception) { }
        }

        private void OnStructure(object sender, StructureChangedEventArgs e) { changed(); }
        private void OnProperty(object sender, AutomationPropertyChangedEventArgs e) { changed(); }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (worker == null) wake.Dispose();
                else wake.Set();
            }
        }
    }
}
