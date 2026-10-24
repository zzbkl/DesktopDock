using System;
using System.IO;
using Microsoft.Win32;

namespace DesktopDock
{
    // 开机自启：写 HKCU\...\Run（不需要管理员权限）
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DesktopDock";

        public static string ExePath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        public static void SetEnabled(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue(ValueName, "\"" + ExePath + "\" --autostart");
                    else k.DeleteValue(ValueName, false);
                }
                Program.Log("autostart " + (on ? "enabled" : "disabled") + " -> " + ExePath);
            }
            catch (Exception ex)
            {
                Program.Log("autostart failed: " + ex.Message);
            }
        }

        // 用户明确要求"开机自启动"，首次跑起来就登记；已登记则不动
        public static void EnsureEnabled()
        {
            if (IsEnabled()) return;
            if (!File.Exists(ExePath)) return;
            SetEnabled(true);
        }
    }
}
