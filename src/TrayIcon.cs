using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace DesktopDock
{
    // 系统托盘图标：左键显示/收起 dock，右键弹出菜单。
    // 托盘图标挂在一个 message-only 窗口上，dock 本身隐藏时它依然在。
    internal class TrayIcon : IDisposable
    {
        private const int WM_TRAY = 0x0400 + 1;       // WM_APP + 1
        private const int NIF_MESSAGE = 0x01;
        private const int NIF_ICON = 0x02;
        private const int NIF_TIP = 0x04;
        private const int NIM_ADD = 0x00;
        private const int NIM_MODIFY = 0x01;
        private const int NIM_DELETE = 0x02;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // 资源管理器重启后会广播这个注册消息，收到就必须重新 Shell_NotifyIcon(NIM_ADD)，
        // 否则托盘图标从此消失（AirDeck 也单独写了个 TaskbarRecovery.exe 兜这件事）
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);

        // ⑦ shell hook：explorer 的窗口级事件会直接投到我们的消息窗口，
        // 不用靠"定时去轮询 FindWindow"。装不上时 Program 那边的探针照样能兜。
        [DllImport("user32.dll")]
        private static extern bool RegisterShellHookWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool DeregisterShellHookWindow(IntPtr hwnd);

        private HwndSource source;
        private IntPtr hIcon;
        private bool added;
        private int taskbarCreatedMsg;
        private int shellHookMsg;
        private string tipText = "DesktopDock";
        private readonly int uID = 0x0D02;

        public bool ShellHookActive { get; private set; }
        public int ShellEventCount { get; private set; }
        public long LastShellEventAt { get; private set; }

        public Action LeftClick { get; set; }
        public Action RightClick { get; set; }

        public void Create(string tip)
        {
            HwndSourceParameters p = new HwndSourceParameters("DesktopDockTrayWindow");
            p.WindowStyle = 0;
            p.ParentWindow = new IntPtr(-3);   // HWND_MESSAGE
            p.Width = 0;
            p.Height = 0;
            source = new HwndSource(p);
            source.AddHook(WndProc);

            taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            shellHookMsg = RegisterWindowMessage("SHELLHOOK");
            try { ShellHookActive = RegisterShellHookWindow(source.Handle); }
            catch { ShellHookActive = false; }
            Program.Log("tray: shell hook=" + ShellHookActive.ToString() + " (HSHELL_WINDOWCREATED 事件驱动)");
            tipText = tip;
            hIcon = MakeIcon();
            NOTIFYICONDATA d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = source.Handle;
            d.uID = uID;
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            d.uCallbackMessage = WM_TRAY;
            d.hIcon = hIcon;
            d.szTip = tip;
            d.szInfo = "";
            d.szInfoTitle = "";
            added = Shell_NotifyIcon(NIM_ADD, ref d);
            Program.Log("tray icon added=" + added.ToString());
        }

        public void ReAdd()
        {
            if (source == null) return;
            added = false;
            TryAdd(0);
        }

        // 资源管理器刚重启时任务栏还没准备好，Shell_NotifyIcon 会失败，
        // 所以隔 1.2s 重试几次（AirDeck 也有 TaskbarRecovery 兜这一步）。
        private void TryAdd(int attempt)
        {
            Add();
            Program.Log("tray: re-add attempt " + (attempt + 1).ToString() + " ok=" + added.ToString());
            if (added)
            {
                Program.Log("tray: re-added after shell restart (ok=True)");
                return;
            }
            if (attempt >= 5) { Program.Log("tray: re-add gave up after 6 attempts"); return; }
            System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(1200);
            t.Tick += delegate
            {
                t.Stop();
                TryAdd(attempt + 1);
            };
            t.Start();
        }

        // 托盘图标现在还在不在（资源管理器重启/图标被挤掉时能自检出来）
        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left, top, right, bottom;
        }

        [DllImport("shell32.dll")]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

        public bool Exists()
        {
            if (source == null || !added) return false;
            try
            {
                NOTIFYICONIDENTIFIER id = new NOTIFYICONIDENTIFIER();
                id.cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
                id.hWnd = source.Handle;
                id.uID = uID;
                RECT r;
                return Shell_NotifyIconGetRect(ref id, out r) == 0;
            }
            catch { return false; }
        }

        private void Add()
        {
            NOTIFYICONDATA d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = source.Handle;
            d.uID = uID;
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            d.uCallbackMessage = WM_TRAY;
            d.hIcon = hIcon;
            d.szTip = tipText;
            d.szInfo = "";
            d.szInfoTitle = "";
            added = Shell_NotifyIcon(NIM_ADD, ref d);
        }

        public void SetTip(string tip)
        {
            if (source == null) return;
            NOTIFYICONDATA d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = source.Handle;
            d.uID = uID;
            d.uFlags = NIF_TIP;
            d.szTip = tip;
            d.szInfo = "";
            d.szInfoTitle = "";
            Shell_NotifyIcon(NIM_MODIFY, ref d);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (taskbarCreatedMsg != 0 && msg == taskbarCreatedMsg)
            {
                handled = true;
                Program.Log("tray: TaskbarCreated received, re-adding icon");
                ReAdd();
                return IntPtr.Zero;
            }

            if (shellHookMsg != 0 && msg == shellHookMsg)
            {
                // wParam 低 15 位是 HSHELL_* 事件：1=WINDOWCREATED, 2=WINDOWDESTROYED,
                // 3=ACTIVATESHELLWINDOW, 6=WINDOWACTIVATED … 我们只要"有事件"这个事实。
                ShellEventCount++;
                LastShellEventAt = Environment.TickCount;
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_TRAY)
            {
                int ev = lParam.ToInt32() & 0xFFFF;
                const int WM_LBUTTONUP = 0x0202;
                const int WM_LBUTTONDBLCLK = 0x0203;
                const int WM_RBUTTONUP = 0x0205;
                if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK)
                {
                    handled = true;
                    if (LeftClick != null) LeftClick();
                }
                else if (ev == WM_RBUTTONUP)
                {
                    handled = true;
                    if (RightClick != null) RightClick();
                }
            }
            return IntPtr.Zero;
        }

        // 一个 32x32 的小图标：深色圆角条 + 三个白点，跟 dock 一个调子
        private IntPtr MakeIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        Rectangle r = new Rectangle(2, 8, 28, 16);
                        using (GraphicsPath path = RoundRect(r, 6))
                        {
                            using (LinearGradientBrush b = new LinearGradientBrush(
                                new System.Drawing.Point(0, 8), new System.Drawing.Point(0, 24),
                                Color.FromArgb(255, 0x3A, 0x3C, 0x46),
                                Color.FromArgb(255, 0x18, 0x19, 0x1E)))
                            {
                                g.FillPath(b, path);
                            }
                            using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1))
                            {
                                g.DrawPath(pen, path);
                            }
                        }
                        using (SolidBrush white = new SolidBrush(Color.White))
                        {
                            g.FillEllipse(white, 8, 15, 4, 4);
                            g.FillEllipse(white, 14, 15, 4, 4);
                            g.FillEllipse(white, 20, 15, 4, 4);
                        }
                    }
                    return bmp.GetHicon();
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Dispose()
        {
            if (source != null)
            {
                if (ShellHookActive)
                {
                    try { DeregisterShellHookWindow(source.Handle); } catch { }
                    ShellHookActive = false;
                }
                if (added)
                {
                    NOTIFYICONDATA d = new NOTIFYICONDATA();
                    d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
                    d.hWnd = source.Handle;
                    d.uID = uID;
                    Shell_NotifyIcon(NIM_DELETE, ref d);
                    added = false;
                }
                source.Dispose();
                source = null;
            }
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
                hIcon = IntPtr.Zero;
            }
        }
    }
}
