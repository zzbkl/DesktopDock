using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDock
{
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int GWL_STYLE = -16;

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

        [DllImport("gdi32.dll")]
        public static extern bool GetRgnBox(IntPtr hRgn, out RECT lprc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_CLOAKED = 14;

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        public static int Cloaked(IntPtr hwnd)
        {
            int v = 0;
            try { DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out v, sizeof(int)); } catch { }
            return v;
        }
        public const uint GW_OWNER = 4;

        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);
        // 响应性探针：给目标窗口发 WM_NULL，带 SMTO_ABORTIFHUNG(0x2) 和超时。
        // 返回 False＝对方在 timeoutMs 内没处理消息（explorer 卡死/正在重启）。
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        public static bool IsWindowResponsive(IntPtr hwnd, int timeoutMs)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                IntPtr r;
                IntPtr ok = SendMessageTimeout(hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero, 0x2, (uint)timeoutMs, out r);
                return ok != IntPtr.Zero;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // 任务栏完整高度（DIP）。自动隐藏时 Shell_TrayWnd 只是往下挪出屏幕，矩形高度还是 48，
        // 所以这个值可靠 —— 用来把 dock 抬到任务栏上方，不跟它重叠。
        public static double TaskbarBandDip(IntPtr hWndForDpi)
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return 0;
            RECT r;
            if (!GetWindowRect(tray, out r)) return 0;
            int h = r.Bottom - r.Top;
            if (h <= 0 || h > 400) return 0;
            uint dpi = 0;
            // 窗口还没创建时句柄是 0；GetDpiForSystem 在 PerMonitorV2 进程里返回 96 会算错，
            // 所以优先拿桌面窗口的 DPI（它跟随主屏），再退回 GetDpiForMonitor。
            IntPtr probe = hWndForDpi != IntPtr.Zero ? hWndForDpi : GetDesktopWindow();
            try { dpi = GetDpiForWindow(probe); } catch { dpi = 0; }
            if (dpi <= 96)
            {
                uint dx, dy;
                IntPtr mon = MonitorFromWindow(probe, 1); // MONITOR_DEFAULTTOPRIMARY
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) dpi = dx;
            }
            if (dpi == 0) dpi = 96;
            return h * 96.0 / dpi;
        }

        // ---- 任务栏自动隐藏：立即生效的那条路 ----
        // 改 StuckRects3 注册表要等 explorer 重启才认；ABM_SETSTATE 是直接改活着的任务栏状态，
        // 调完立刻变，不闪屏、不用重启资源管理器。实测：state 1<->0，rect 顶边 1065<->1019。
        public const uint ABM_GETSTATE = 0x00000004;
        public const int ABS_AUTOHIDE = 0x00000001;

        public static bool TaskbarAutoHide()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return true;
            APPBARDATA data = new APPBARDATA();
            data.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA));
            data.hWnd = tray;
            ulong state = SHAppBarMessage(ABM_GETSTATE, ref data).ToUInt64();
            return (state & (ulong)ABS_AUTOHIDE) != 0;
        }

        // 用户自己在"设置 - 个性化 - 任务栏"里选的那个值（注册表里存的偏好）。
        // 活着的状态可能被本程序改过，所以"原始偏好"只能从这里读。
        public static bool TaskbarAutoHidePref()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3"))
                {
                    if (k == null) return true;
                    byte[] b = k.GetValue("Settings") as byte[];
                    if (b == null || b.Length < 9) return true;
                    return (b[8] & 0x01) != 0;
                }
            }
            catch { return true; }
        }

        public static bool SetTaskbarAutoHide(bool on)
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            APPBARDATA data = new APPBARDATA();
            data.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA));
            data.hWnd = tray;
            data.lParam = (IntPtr)(ABS_ALWAYSONTOP | (on ? ABS_AUTOHIDE : 0));
            SHAppBarMessage(ABM_SETSTATE, ref data);
            return TaskbarAutoHide() == on;     // 回读确认，别骗自己
        }

        // （ABM_SETSTATE 只改活着的状态，不写注册表 —— 注册表留给用户自己的偏好，
        //   本程序退出时按启动时读到的值还原，不制造"跑过就变了"的副作用。）

        // ---- 底部 AppBar：向系统真的"占"一条工作区 ----
        // 光靠 Topmost 压不住：最大化窗口的客户区照样铺到屏幕底边，dock 一亮就盖住页面底部。
        // 走 SHAppBarMessage 注册成 AppBar，系统会把工作区缩到这条带子之上，
        // 浏览器 / 资源管理器最大化时会自动让开 —— 这才是"常显但不挡页面"。
        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        public const uint ABM_NEW = 0x00000000;
        public const uint ABM_REMOVE = 0x00000001;
        public const uint ABM_QUERYPOS = 0x00000002;
        public const uint ABM_SETPOS = 0x00000003;
        public const uint ABM_SETSTATE = 0x0000000A;
        public const uint ABE_BOTTOM = 0x00000003;
        public const int ABS_ALWAYSONTOP = 0x00000002;
        public const int APPBAR_CALLBACK = 0x0400 + 0x7A;   // WM_USER + 0x7A，我们自己收
        public const int ABN_POSCHANGED = 1;

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const uint SPI_GETWORKAREA = 0x0030;

        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

        // 当前工作区底边（本进程坐标）。必须现查：WPF 的 SystemParameters.WorkArea
        // 在我们自己改过 AppBar 之后是缓存值，会算错摆放位置。
        public static double WorkAreaBottom()
        {
            RECT r;
            if (!SystemParametersInfo(SPI_GETWORKAREA, 0, out r, 0)) return 0;
            return r.Bottom;
        }

        // 注册底部带子；heightPx 是设备像素。失败返回 false（不抛，调用方只记日志）。
        public static bool RegisterBottomBand(IntPtr hwnd, int heightPx)
        {
            if (hwnd == IntPtr.Zero || heightPx <= 0) return false;
            APPBARDATA data = new APPBARDATA();
            data.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA));
            data.hWnd = hwnd;
            data.uCallbackMessage = (uint)APPBAR_CALLBACK;
            if (SHAppBarMessage(ABM_NEW, ref data) == UIntPtr.Zero) return false;

            int w = GetSystemMetrics(0);   // SM_CXSCREEN
            int h = GetSystemMetrics(1);   // SM_CYSCREEN
            data.uEdge = ABE_BOTTOM;
            data.rc.Left = 0;
            data.rc.Right = w;
            data.rc.Bottom = h;
            data.rc.Top = h - heightPx;

            SHAppBarMessage(ABM_QUERYPOS, ref data);   // 先让系统把任务栏那一截躲开
            int qTop = data.rc.Top, qBottom = data.rc.Bottom;
            // 系统返回的 Bottom 已经贴在任务栏上方，只把上边往上扩到我们的高度。
            // 不要再把 Bottom 挪到 Top（那样带子和任务栏之间会空出一条死区）。
            data.rc.Top = data.rc.Bottom - heightPx;
            SHAppBarMessage(ABM_SETPOS, ref data);

            data.lParam = (IntPtr)ABS_ALWAYSONTOP;
            SHAppBarMessage(ABM_SETSTATE, ref data);
            LastBandLeft = data.rc.Left;
            LastBandRight = data.rc.Right;
            LastBandTop = data.rc.Top;
            LastBandBottom = data.rc.Bottom;
            LastBandDetail = "req=" + heightPx + " scr=" + w + "x" + h
                + " query=" + qTop + ".." + qBottom
                + " final=" + data.rc.Top + ".." + data.rc.Bottom;
            return true;
        }

        public static int LastBandLeft, LastBandTop, LastBandRight, LastBandBottom;

        // 直接按物理像素摆窗口：WPF 的 Left/Top 在本进程里换算过一道 DPI，
        // 拿它去对 AppBar 的物理矩形会整体偏移，所以带子内定位一律走 SetWindowPos。
        public static void MoveWindowPhysical(IntPtr hwnd, int x, int y, int cx, int cy)
        {
            const uint SWP_NOZORDER = 0x0004;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public static string LastBandDetail = "";

        public static void UnregisterBand(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            APPBARDATA data = new APPBARDATA();
            data.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA));
            data.hWnd = hwnd;
            SHAppBarMessage(ABM_REMOVE, ref data);
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("psapi.dll")]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        // ---- 窗口预览：直接让 Windows 合成器把别的窗口画面"投"到我们窗口上（DWM 缩略图），
        //      不抓屏、不复制位图、画面还是活的。任务栏预览用的就是这套。----
        public const int DWM_TNP_RECTDESTINATION = 0x1;
        public const int DWM_TNP_RECTSOURCE = 0x2;
        public const int DWM_TNP_OPACITY = 0x4;
        public const int DWM_TNP_VISIBLE = 0x8;
        public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport("dwmapi.dll")]
        public static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        // 强制把窗口顶到最上层并且可见（WPF 的 Topmost 在某些时序下不生效，尤其是刚 Show 的无激活窗口）
        // 点预览缩略图时把那个窗口带到前台
        public static void ActivateWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch { }
        }

        public static void ForceTopmostVisible(Window w)
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        public static string TitleOfWindow(IntPtr h)
        {
            try
            {
                int n = GetWindowTextLength(h);
                if (n <= 0) return "";
                System.Text.StringBuilder sb = new System.Text.StringBuilder(n + 2);
                GetWindowText(h, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);

        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);

        public static string DescribeForeground()
        {
            int state;
            int hr = SHQueryUserNotificationState(out state);
            IntPtr fg = GetForegroundWindow();
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            if (fg != IntPtr.Zero) GetClassName(fg, sb, 256);
            int style = fg == IntPtr.Zero ? 0 : GetWindowLong(fg, -16);
            string cover = "?";
            RECT r;
            if (fg != IntPtr.Zero && GetWindowRect(fg, out r))
            {
                IntPtr mon = MonitorFromWindow(fg, 2);
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(mon, ref mi))
                {
                    bool c = r.Left <= mi.rcMonitor.Left + 3 && r.Top <= mi.rcMonitor.Top + 3 &&
                             r.Right >= mi.rcMonitor.Right - 3 && r.Bottom >= mi.rcMonitor.Bottom - 3;
                    cover = c + "(" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + ")";
                }
            }
            return "hr=" + hr + " quns=" + state + " cls=" + sb.ToString() + " style=0x" + style.ToString("X8") + " cover=" + cover;
        }

        // 前台是不是"占满工作区的最大化窗口"（浏览器最大化这类）。
        // 它在前台时 dock 也要让开：dock 是 topmost 的，压在页面底部会挡住内容。
        public static bool IsForegroundMaximized(IntPtr ignore1, IntPtr ignore2)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == ignore1 || fg == ignore2) return false;
            if (!IsWindowVisible(fg)) return false;
            if (!IsZoomed(fg)) return false;
            // 第三方桌面（AirDeck 之类）的最大化层不算
            if (IsDesktopShellForeground(IntPtr.Zero)) return false;
            return true;
        }

        // 前台是不是桌面（没开任何窗口，或在桌面上点了图标）
        // 任务栏/开始菜单/桌面这类 shell 窗口在前台时，不应被当成"全屏应用"
        public static bool IsShellForeground()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            System.Text.StringBuilder sb2 = new System.Text.StringBuilder(256);
            GetClassName(fg, sb2, 256);
            string cls = sb2.ToString();
            if (string.IsNullOrEmpty(cls)) return false;
            string[] shells = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
                                "Start", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
                                "ForegroundStaging", "MultitaskingViewFrame", "Shell_InputSwitchTopLevelWindow",
                                "NotifyIconOverflowWindow" };
            foreach (string s in shells)
                if (string.Equals(cls, s, StringComparison.OrdinalIgnoreCase)) return true;
            // Win11 的过渡窗 / 通知区带后缀，按前缀认
            if (cls.StartsWith("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsDesktopForeground()
        {
            return IsDesktopForeground(IntPtr.Zero);
        }

        // allow 非零时，该窗口在前台也算桌面（小组件自己在前台时不该把自己藏起来）
        public static bool IsDesktopForeground(IntPtr allow)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return true;
            if (allow != IntPtr.Zero && fg == allow) return true;
            // 隐藏窗口也能拿到前台（托盘工具、浮窗应用的隐形热键层，实测 MyClash 的
            // FLUTTER_RUNNER_WIN32_WINDOW 就是这样）。用户眼前还是桌面，所以按桌面算。
            if (!IsWindowVisible(fg)) return true;
            if (IsDesktopHostWindow(fg)) return true;
            return IsDesktopShellForeground(allow);
        }

        // 前台这个窗口是不是"承载桌面图标的那一层"。
        // Progman 直接算；WorkerW 不能只凭类名 —— Windows 会额外创建若干顶层 WorkerW
        // （动态壁纸宿主、第三方桌面的容器都跟它同名），它们背后才是桌面。
        // 裸类名判断会把"桌面上飘着的那个东西"当成"用户回到了桌面"，dock 就错误常显了。
        // 判据照 Seelen-UI 的 is_desktop：WorkerW 必须挂着 SHELLDLL_DefView 子窗口，
        // 桌面图标（SysListView32）就挂在它下面。
        public static bool IsDesktopHostWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            GetClassName(hwnd, sb, 64);
            string cls = sb.ToString();
            if (string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase)) return false;

            const uint GW_CHILD = 5, GW_HWNDNEXT = 2;
            IntPtr child = GetWindow(hwnd, GW_CHILD);
            int guard = 0;                                    // 窗口树万一成环，别卡死在这
            while (child != IntPtr.Zero && guard < 64)
            {
                guard++;
                System.Text.StringBuilder cb = new System.Text.StringBuilder(64);
                GetClassName(child, cb, 64);
                if (string.Equals(cb.ToString(), "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase)) return true;
                child = GetWindow(child, GW_HWNDNEXT);
            }
            return false;
        }

        // 前台是不是"独占全屏的应用"。两条判据：系统通知状态（D3D 全屏/演示/忙碌）+ 窗口矩形铺满整块屏幕。
        // 桌面（Progman/WorkerW）和任务栏永远不算全屏应用。
        public static bool IsFullscreenAppForeground(IntPtr ignore1, IntPtr ignore2)
        {
            int state;
            if (SHQueryUserNotificationState(out state) != 0) state = 0;

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == ignore1 || fg == ignore2) return false;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            GetClassName(fg, sb, 256);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" ||
                cls == "Shell_SecondaryTrayWnd" || cls == "NotifyIconOverflowWindow" ||
                cls == "XamlExplorerHostIslandWindow" || cls == "Windows.UI.Core.CoreWindow" ||
                cls == "MultitaskingViewFrame" || cls == "ForegroundStaging") return false;

            // 桌面外壳（AirDeck 等）的无标题全屏层不是"全屏应用"，
            // 否则切回桌面会被当成游戏，dock 和叫出把手被一起藏掉
            if (IsDesktopShellForeground(IntPtr.Zero)) return false;

            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            IntPtr mon = MonitorFromWindow(fg, 2); // MONITOR_DEFAULTTONEAREST
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref mi)) return false;

            const int tol = 3;
            bool coversMonitor = r.Left <= mi.rcMonitor.Left + tol && r.Top <= mi.rcMonitor.Top + tol &&
                                 r.Right >= mi.rcMonitor.Right - tol && r.Bottom >= mi.rcMonitor.Bottom - tol;
            if (!coversMonitor) return false;

            // 关键：本机任务栏是自动隐藏的，"最大化"窗口的矩形也铺满整块屏幕。
            // 所以先看矩形，再靠"有没有标题栏"区分真全屏和普通最大化窗口。
            // QUNS（系统通知状态）只是补充判据，不能单独用 —— 它反映的是全局"有全屏程序在跑"，
            // 哪怕那个游戏已经最小化也仍然是 busy，会把 dock 永久藏起来。
            const int WS_CAPTION = 0x00C00000;
            int style = GetWindowLong(fg, -16); // GWL_STYLE
            if ((style & WS_CAPTION) != WS_CAPTION) return true;          // 无边框 + 铺满 = 真全屏（游戏/播放器）
            return state == 2 || state == 3 || state == 4;                 // 有标题栏但系统判定全屏（F11/演示）
        }

        // 游戏/高性能模式：真全屏，或者前台进程在 games.json 名单里（窗口化的游戏也算）
        public static string[] GameProcessNames = new string[0];

        // 桌面外壳进程：本机装了第三方桌面（AirDeck），Win+D / 最小化全部之后前台是它，而不是 Progman。
        // 这类进程在前台时按"桌面"处理：不算全屏应用，dock 照常显示。
        public static string[] DesktopShells = new string[0];

        public static bool IsDesktopShellForeground(IntPtr allow)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (allow != IntPtr.Zero && fg == allow) return false;
            string[] list = DesktopShells;
            if (list == null || list.Length == 0) return false;
            string proc = ProcessNameOfWindow(fg);
            if (proc.Length == 0) return false;
            foreach (string s in list)
            {
                if (s == null) continue;
                string t = s.Trim();
                if (t.Length > 4 && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
                if (string.Equals(proc, t, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string ProcessNameOfWindow(IntPtr hwnd)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "";
                return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch { return ""; }
        }

        public static bool IsHeavyAppForeground(IntPtr ignore1, IntPtr ignore2)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == ignore1 || fg == ignore2) return false;
            if (IsIconic(fg)) return false;

            if (IsFullscreenAppForeground(ignore1, ignore2)) return true;

            string proc = ProcessNameOfWindow(fg);
            if (proc.Length == 0) return false;
            string[] list = GameProcessNames;
            if (list == null) return false;
            for (int i = 0; i < list.Length; i++)
            {
                string g = list[i];
                if (string.IsNullOrEmpty(g)) continue;
                if (g.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) g = g.Substring(0, g.Length - 4);
                if (string.Equals(g, proc, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        // 前台窗口压在 dock 上就值得让位：只要它横向盖住栏的 60% 以上、下沿越过栏的顶边。
        // 一条判据同时覆盖三种情况：游戏独占全屏、浏览器/编辑器最大化（本机任务栏自动隐藏，
        // 最大化窗口的矩形其实比屏幕还大）、以及任何被拉到屏幕底部的宽窗口。
        // 桌面/任务栏/开始菜单这类 shell 窗口永远不算。
        public static bool ForegroundCoversBand(IntPtr ignore1, IntPtr ignore2, int left, int top, int right, int bottom)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == ignore1 || fg == ignore2) return false;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            GetClassName(fg, sb, 256);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" ||
                cls == "Shell_SecondaryTrayWnd" || cls == "NotifyIconOverflowWindow" ||
                cls == "XamlExplorerHostIslandWindow" || cls == "Windows.UI.Core.CoreWindow" ||
                cls == "MultitaskingViewFrame" || cls == "ForegroundStaging") return false;

            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            // 最小化的窗口虽然还占着"前台"这个名字，但它根本没盖住任何东西——
            // 有些全屏游戏最小化后不交还前台，不加这条 dock 会一直被藏死。
            if (IsIconic(fg)) return false;

            int bandWidth = right - left;
            if (bandWidth <= 0) return false;
            int overlap = Math.Min(r.Right, right) - Math.Max(r.Left, left);
            if (overlap * 100 < bandWidth * 60) return false;   // 横向没压住这条栏
            if (r.Bottom <= top + 2) return false;              // 下沿还没到栏的位置
            return true;
        }

        // 枚举所有"可见、无 owner、非工具窗口"的顶层窗口句柄
        public static List<IntPtr> VisibleTopLevelWindows()
        {
            List<IntPtr> list = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // 一次枚举，把所有可见顶层窗口的进程名收成集合。
        // 老写法（每个图标各调一次 IsRunning）每轮都要 EnumWindows + 逐窗口取进程名，
        // 十几个图标就是几百毫秒，全堆在 ShowDock 里——用户感知就是"按下去先顿一下才出来"。
        public static HashSet<string> VisibleWindowProcessNames()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<IntPtr> wins = VisibleTopLevelWindows();
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (IntPtr h in wins)
            {
                int pid = GetPidOfWindow(h);
                if (pid <= 0 || pid == self) continue;
                try { set.Add(System.Diagnostics.Process.GetProcessById(pid).ProcessName); }
                catch { }
            }
            return set;
        }

        public static int GetPidOfWindow(IntPtr hWnd)        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return (int)pid;
        }

        // 圆角裁剪（不透明度 100% 的普通窗口也能有圆角，且不失去硬件加速）
        public static void ApplyRoundedRegion(Window w, int radiusPx)
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int pxW = (int)Math.Round(w.ActualWidth * DpiScaleX(w));
            int pxH = (int)Math.Round(w.ActualHeight * DpiScaleY(w));
            if (pxW <= 0 || pxH <= 0) return;
            IntPtr rgn = CreateRoundRectRgn(0, 0, pxW + 1, pxH + 1, radiusPx, radiusPx);
            SetWindowRgn(hwnd, rgn, true);
        }

        public static double DpiScaleX(Visual v)
        {
            try { return VisualTreeHelperDpi(v).DpiScaleX; }
            catch { return 1.0; }
        }

        public static double DpiScaleY(Visual v)
        {
            try { return VisualTreeHelperDpi(v).DpiScaleY; }
            catch { return 1.0; }
        }

        private static DpiScale VisualTreeHelperDpi(Visual v)
        {
            return System.Windows.Media.VisualTreeHelper.GetDpi(v);
        }

        public static void ForceForeground(Window w)
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            ForceForegroundHwnd(hwnd);
        }

        // 把别的进程的窗口抢到前台。Windows 默认禁止后台进程抢前台，这里用三种手段依次尝试：
        // 1) AllowSetForegroundWindow；2) ALT 假按键解除前台锁定；3) AttachThreadInput 附加到前台线程
        public static bool ForceForegroundHwnd(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            if (GetForegroundWindow() == hwnd) return true;

            AllowSetForegroundWindow(-1); // ASFW_ANY
            if (SetForegroundWindow(hwnd)) return true;

            const byte VK_MENU = 0x12;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (SetForegroundWindow(hwnd)) return true;

            IntPtr fg = GetForegroundWindow();
            uint fgPid;
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out fgPid);
            uint me = GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != me)
            {
                attached = AttachThreadInput(me, fgThread, true);
            }
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetFocus(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            return GetForegroundWindow() == hwnd;
        }

        public static void MakeNoActivate(Window w)
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int x, int y) { cx = x; cy = y; }
    }

    // 热角用：光标位置（物理像素）
    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORPT
    {
        public int x;
        public int y;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    internal static class HotCornerNative
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out CURSORPT p);

        public static bool GetCursorPos(out int x, out int y)
        {
            CURSORPT p;
            bool ok = GetCursorPos(out p);
            x = p.x;
            y = p.y;
            return ok;
        }

        // ---------- 低级鼠标钩子（WH_MOUSE_LL）----------
        // 轮询 GetCursorPos 每 200ms 一次，热角最多要等 600ms 才响；改成钩子推送后，
        // 光标一进角落就能拿到位置，判定粒度只受"评估定时器"影响（80ms）。
        // 注意：钩子回调运行在装钩子的线程（我们的 UI 线程）上，Windows 对 LL 钩子有
        // 超时限制（默认 300ms 不给结果就静默摘下），所以回调里只能做极轻的事。
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // 委托必须由静态字段持有，否则会被 GC 回收，回调一进来就崩
        private static LowLevelMouseProc hookProc;
        private static IntPtr hookHandle = IntPtr.Zero;

        public static int CursorX { get; private set; }
        public static int CursorY { get; private set; }
        public static int MoveCount { get; private set; }
        public static int LastMoveAt { get; private set; }
        public static bool HookInstalled { get { return hookHandle != IntPtr.Zero; } }

        // 由 DockWindow 挂上：光标一动就调（在 UI 线程），实现里只许做极轻的判断
        public static Action OnMove;

        public static bool InstallHook()
        {
            if (hookHandle != IntPtr.Zero) return true;
            try
            {
                hookProc = delegate(int nCode, IntPtr wParam, IntPtr lParam)
                {
                    if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
                    {
                        int x, y;
                        if (GetCursorPos(out x, out y))
                        {
                            CursorX = x;
                            CursorY = y;
                            MoveCount++;
                            LastMoveAt = Environment.TickCount;
                        }
                        Action cb = OnMove;
                        if (cb != null)
                        {
                            try { cb(); }
                            catch { }
                        }
                    }
                    return CallNextHookEx(hookHandle, nCode, wParam, lParam);
                };
                hookHandle = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
                return hookHandle != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Program.Log("hot hook install failed: " + ex.Message);
                hookHandle = IntPtr.Zero;
                return false;
            }
        }

        public static void UninstallHook()
        {
            try
            {
                if (hookHandle != IntPtr.Zero) UnhookWindowsHookEx(hookHandle);
            }
            catch { }
            hookHandle = IntPtr.Zero;
        }
    }

    internal static class Icons
    {
        private const int SIIGBF_BIGGERSIZEOK = 0x01;
        private const int SIIGBF_ICONONLY = 0x04;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        private static Dictionary<string, BitmapSource> cache = new Dictionary<string, BitmapSource>();
        private static Dictionary<string, BitmapSource> pngCache = new Dictionary<string, BitmapSource>();

        // 直接读 png（图标包用）：DecodePixelWidth 限制解码尺寸，省内存
        public static BitmapSource LoadPng(string path, int px)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "|" + px.ToString();
            BitmapSource hit;
            if (pngCache.TryGetValue(key, out hit)) return hit;
            BitmapSource result = null;
            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.DecodePixelWidth = px;
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                result = bi;
            }
            catch { result = null; }
            pngCache[key] = result;
            return result;
        }

        public static BitmapSource Load(string path, int px)
        {
            if (path == null) return null;
            // 内存优化：图标显示最大也就 84 物理像素（56 DIP @150%），统一按 128 解码，
            // 这样 dock 与抽屉共用同一份缓存，每个应用只留一张 128x128（约 65KB）。
            if (px > 128) px = 128;
            string key = path + "|" + px.ToString();
            BitmapSource hit;
            if (cache.TryGetValue(key, out hit)) return hit;

            BitmapSource result = null;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                IShellItemImageFactory factory = null;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
                if (factory != null)
                {
                    IntPtr hbm;
                    int hr = factory.GetImage(new SIZE(px, px), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm);
                    Marshal.ReleaseComObject(factory);
                    if (hr == 0 && hbm != IntPtr.Zero)
                    {
                        result = Imaging.CreateBitmapSourceFromHBitmap(
                            hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        Native.DeleteObject(hbm);
                        result.Freeze();
                    }
                }
            }
            catch
            {
                result = null;
            }
            cache[key] = result;
            return result;
        }
    }

    internal static class Shortcuts
    {
        // 用 WScript.Shell 读 .lnk 的目标与参数（比自写 IShellLink 互操作稳）
        public static void Resolve(string lnkPath, out string target, out string args)
        {
            target = null;
            args = null;
            object shell = null;
            object sc = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                shell = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                target = (string)t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                args = (string)t.InvokeMember("Arguments", BindingFlags.GetProperty, null, sc, null);
            }
            catch
            {
                target = null;
                args = null;
            }
            finally
            {
                if (sc != null) { try { Marshal.ReleaseComObject(sc); } catch { } }
                if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch { } }
            }
        }
    }
}

