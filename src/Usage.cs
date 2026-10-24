using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopDock
{
    // 使用次数统计：不再只看"有没有在抽屉里点过"，
    // 只要某个应用真的被切到前台并停留几秒，就记一次。
    internal static class Usage
    {
        private const int DwellMs = 8000;        // 在前台待够 8 秒才算一次（防止点一下就走）
        private const int DedupeMs = 30000;      // 同一个应用 30 秒内只记一次（点开 + 变前台不会算两遍）

        private static Dictionary<string, int> recent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static IntPtr lastFg = IntPtr.Zero;
        private static int sinceTick;
        private static bool countedThisWindow;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr h);

        // 游戏模式时挂起统计，省一次 1.5 秒的窗口查询
        public static bool Paused;

        public static void Note(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            int t;
            if (recent.TryGetValue(name, out t) && Environment.TickCount - t < DedupeMs) return;
            recent[name] = Environment.TickCount;
            ConfigStore.BumpUsage(name);
        }

        // 后台盯着前台窗口：应用被切到前台并待够 DwellMs 就记一次
        public static void StartTracker(List<AppItem> apps)
        {
            List<AppItem> list = apps;
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(1500);
            t.Tick += delegate
            {
                try
                {
                    TrackOnce(list);
                }
                catch { }
            };
            t.Start();
            Program.Log("usage tracker started, apps=" + (list == null ? 0 : list.Count).ToString());
        }

        private static void TrackOnce(List<AppItem> apps)
        {
            if (Paused) { Reset(); return; }
            if (apps == null) return;

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) { Reset(); return; }

            StringBuilder cls = new StringBuilder(256);
            GetClassNameW(fg, cls, 256);
            string c = cls.ToString();

            // 桌面 / 任务栏 / 开始菜单 / 我们自己的窗口都不算使用
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd"
                || c == "XamlExplorerHostIslandWindow" || c == "Windows.UI.Core.CoreWindow"
                || c == "NotifyIconOverflowWindow" || c == "ForegroundStaging"
                || c.StartsWith("HwndWrapper[DesktopDock", StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                return;
            }

            if (IsIconic(fg)) { Reset(); return; }

            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            if (pid == 0) { Reset(); return; }

            if (fg != lastFg)
            {
                lastFg = fg;
                sinceTick = Environment.TickCount;
                countedThisWindow = false;
                return;
            }

            if (countedThisWindow) return;
            if (Environment.TickCount - sinceTick < DwellMs) return;

            string exe;
            try
            {
                Process p = Process.GetProcessById((int)pid);
                exe = p.ProcessName + ".exe";
            }
            catch { Reset(); return; }

            AppItem hit = Match(apps, exe);
            if (hit != null)
            {
                countedThisWindow = true;
                Note(hit.Name);
                Program.Log("usage +1: " + hit.Name + " (" + exe + ")");
            }
            else
            {
                countedThisWindow = true;   // 不是我们列表里的应用，这一轮不再重复找
            }
        }

        private static void Reset()
        {
            lastFg = IntPtr.Zero;
            countedThisWindow = false;
        }

        // 把前台进程名对回列表里的应用（.lnk 看 Target，UWP/Electron 看可执行名）
        private static AppItem Match(List<AppItem> apps, string exe)
        {
            foreach (AppItem a in apps)
            {
                if (a == null) continue;
                string target = string.IsNullOrEmpty(a.Target) ? a.Shortcut : a.Target;
                if (string.IsNullOrEmpty(target)) continue;
                string file;
                try { file = Path.GetFileName(target); }
                catch { continue; }
                if (string.Equals(file, exe, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }
    }
}


