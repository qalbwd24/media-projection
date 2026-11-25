using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PhoneRemote
{
    internal sealed class Preferences
    {
        private const string SettingsKey = @"Software\MediaProjectionTool";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "MediaProjectionTool";
        internal const string AppName = "媒体投射工具";

        internal string Phone { get; private set; }
        internal bool Dock { get; private set; }
        internal bool AutoStart { get; private set; }
        internal Point? DockPosition { get; private set; }
        internal bool DockInTaskbar { get; private set; }
        internal int DockBackground { get; private set; }

        internal Preferences()
        {
            DockInTaskbar = true;
            using (var settings = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                Phone = settings == null ? "" : settings.GetValue("Phone", "") as string ?? "";
                Dock = settings != null && Convert.ToInt32(settings.GetValue("Dock", 0)) == 1;
                int background = settings == null ? 0 : Convert.ToInt32(settings.GetValue("DockBackground", 0));
                DockBackground = background == 20 || background == 45 ? background : 0;
                var placement = settings == null ? null : settings.GetValue("DockPlacement") as string;
                if (placement != null)
                {
                    var parts = placement.Split(',');
                    int x, y;
                    if (parts.Length == 3 && Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
                        Int32.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
                    {
                        DockPosition = new Point(x, y);
                        DockInTaskbar = parts[0] == "taskbar";
                    }
                }
            }
            using (var startup = Registry.CurrentUser.OpenSubKey(RunKey))
                AutoStart = startup != null && startup.GetValue(RunName) is string;
        }

        internal void SelectPhone(string phone)
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(SettingsKey)) settings.SetValue("Phone", phone);
            Phone = phone;
        }

        internal void SetDock(bool enabled)
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(SettingsKey)) settings.SetValue("Dock", enabled ? 1 : 0);
            Dock = enabled;
        }

        internal void SetDockPosition(Point? position, bool taskbar)
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (position.HasValue)
                    settings.SetValue("DockPlacement", String.Format(CultureInfo.InvariantCulture, "{0},{1},{2}",
                        taskbar ? "taskbar" : "floating", position.Value.X, position.Value.Y));
                else settings.DeleteValue("DockPlacement", false);
            }
            DockPosition = position;
            DockInTaskbar = taskbar;
        }

        internal void SetDockBackground(int percent)
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(SettingsKey)) settings.SetValue("DockBackground", percent);
            DockBackground = percent;
        }

        internal void SetAutoStart(bool enabled)
        {
            using (var startup = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) startup.SetValue(RunName, "\"" + Program.Executable + "\" --tray");
                else startup.DeleteValue(RunName, false);
            }
            AutoStart = enabled;
        }

        internal static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); }
        }

        internal static void InstallShortcut()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartMenuLink));
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            object link = null;
            try
            {
                link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { StartMenuLink });
                SetLink(link, "TargetPath", Program.Executable);
                SetLink(link, "WorkingDirectory", Path.GetDirectoryName(Program.Executable));
                SetLink(link, "Description", "用蓝牙查看和控制手机正在播放的媒体");
                SetLink(link, "IconLocation", Program.Executable + ",0");
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, new object[0]);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void SetLink(object link, string name, string value)
        {
            link.GetType().InvokeMember(name, BindingFlags.SetProperty, null, link, new object[] { value });
        }
    }
}
