using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopDock
{
    // 悬停图标弹出的窗口预览。
    //
    // 为什么不用 WPF 窗口：DWM 缩略图要求"目标窗口不是分层窗口"，而 WPF 里
    // AllowsTransparency=true 的窗口就是分层窗口，缩略图注不进去；改成
    // AllowsTransparency=false 的 WPF 窗口在这台机器上又怎么都不上屏（试过
    // ShowInTaskbar / ShowActivated / 激活 / 强制 Topmost / 查 cloaked，全部无效，
    // 窗口 WS_VISIBLE、区域正确、rect 正确，就是不合成）。
    // 所以这里直接开一个原生 Win32 窗口，自己画背景和标题，DWM 缩略图往里贴。
    internal class PreviewHost
    {
        private const string ClassName = "DesktopDockPreviewHost";
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_DESTROY = 0x0002;

        private const double Pad = 12;
        private const double Gap = 10;
        private const double InfoH = 24;

        private static PreviewHost instance;
        private static WndProcDelegate wndProcDelegate;

        private IntPtr hwnd = IntPtr.Zero;
        private readonly List<IntPtr> thumbIds = new List<IntPtr>();
        private readonly List<IntPtr> sources = new List<IntPtr>();
        private double cellW = 240;
        private double cellH = 135;
        private float dpi = 1.0f;
        private bool tracking;
        private bool enteredReported;

        public static bool DebugCorner = false;

        public event EventHandler PointerEntered;
        public event EventHandler PointerLeft;

        public bool Showing { get { return hwnd != IntPtr.Zero && IsWindowVisible(hwnd); } }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void EnsureClass()
        {
            if (wndProcDelegate != null) return;
            wndProcDelegate = new WndProcDelegate(StaticWndProc);

            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.style = 0;
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate);
            wc.hInstance = GetModuleHandle(null);
            wc.hCursor = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW
            wc.lpszClassName = ClassName;
            wc.hbrBackground = IntPtr.Zero;
            RegisterClassEx(ref wc);
        }

        private static IntPtr StaticWndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            PreviewHost self = instance;
            if (self != null && self.hwnd == hWnd)
            {
                IntPtr result;
                if (self.HandleMessage(msg, wParam, lParam, out result)) return result;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            switch (msg)
            {
                case WM_ERASEBKGND:
                    result = new IntPtr(1);
                    return true;
                case WM_PAINT:
                    Paint();
                    result = IntPtr.Zero;
                    return true;
                case WM_MOUSEMOVE:
                    if (!enteredReported)
                    {
                        enteredReported = true;
                        if (PointerEntered != null) PointerEntered(this, EventArgs.Empty);
                    }
                    if (!tracking)
                    {
                        tracking = true;
                        TRACKMOUSEEVENT t = new TRACKMOUSEEVENT();
                        t.cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT));
                        t.dwFlags = 0x00000002;   // TME_LEAVE
                        t.hwndTrack = hwnd;
                        TrackMouseEvent(ref t);
                    }
                    return false;
                case WM_MOUSELEAVE:
                    tracking = false;
                    enteredReported = false;
                    if (PointerLeft != null) PointerLeft(this, EventArgs.Empty);
                    return false;
                case WM_LBUTTONUP:
                    int lx = (int)((long)lParam & 0xFFFF);
                    int ly = (int)(((long)lParam >> 16) & 0xFFFF);
                    int idx = HitTest(lx, ly);
                    if (idx >= 0 && idx < sources.Count) Native.ActivateWindow(sources[idx]);
                    Hide();
                    return true;
                case WM_DESTROY:
                    hwnd = IntPtr.Zero;
                    return false;
            }
            return false;
        }

        private int HitTest(int pxX, int pxY)
        {
            double sx = pxX / dpi, sy = pxY / dpi;
            for (int i = 0; i < sources.Count; i++)
            {
                double cx = Pad + i * (cellW + Gap);
                if (sx >= cx && sx <= cx + cellW && sy >= Pad && sy <= Pad + cellH) return i;
            }
            return -1;
        }

        private void Paint()
        {
            PAINTSTRUCT ps;
            IntPtr hdc = BeginPaint(hwnd, out ps);
            if (hdc == IntPtr.Zero) return;
            try
            {
                RECT rc;
                GetClientRect(hwnd, out rc);
                using (Graphics g = Graphics.FromHdc(hdc))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(0x16, 0x16, 0x1C)))
                        g.FillRectangle(bg, 0, 0, rc.Right + 2, rc.Bottom + 2);

                    using (SolidBrush cell = new SolidBrush(Color.FromArgb(0x24, 0x24, 0x2C)))
                    using (Pen edge = new Pen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)))
                    using (SolidBrush txt = new SolidBrush(Color.FromArgb(0xC8, 0xC8, 0xD4)))
                    using (Font font = new Font("Microsoft YaHei UI", 8.5f))
                    using (StringFormat fmt = new StringFormat())
                    {
                        fmt.Alignment = StringAlignment.Center;
                        fmt.Trimming = StringTrimming.EllipsisCharacter;
                        fmt.FormatFlags = StringFormatFlags.NoWrap;
                        for (int i = 0; i < sources.Count; i++)
                        {
                            float cx = (float)((Pad + i * (cellW + Gap)) * dpi);
                            float cy = (float)(Pad * dpi);
                            float cw = (float)(cellW * dpi);
                            float ch = (float)(cellH * dpi);
                            using (GraphicsPath path = Rounded(new RectangleF(cx, cy, cw, ch), 8f * dpi))
                            {
                                g.FillPath(cell, path);
                                g.DrawPath(edge, path);
                            }
                            string title = Native.TitleOfWindow(sources[i]);
                            if (string.IsNullOrEmpty(title)) title = Native.ProcessNameOfWindow(sources[i]);
                            title = Shorten(title, 30);
                            RectangleF tr = new RectangleF(cx, cy + ch + 4f * dpi, cw, (float)(InfoH * dpi));
                            g.DrawString(title, font, txt, tr, fmt);
                        }
                    }
                }
            }
            finally
            {
                EndPaint(hwnd, ref ps);
            }
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(无标题)";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        public void ShowPreviews(List<IntPtr> windows, double anchorCenterX, double barTop)
        {
            if (windows == null || windows.Count == 0)
            {
                Hide();
                return;
            }

            EnsureClass();
            instance = this;

            ClearThumbs();
            sources.Clear();
            enteredReported = false;
            int n = Math.Min(windows.Count, 4);
            for (int i = 0; i < n; i++) sources.Add(windows[i]);

            cellW = n == 1 ? 340 : 240;
            cellH = n == 1 ? 191 : 135;
            double wdip = Pad * 2 + n * cellW + (n - 1) * Gap;
            double hdip = Pad * 2 + cellH + InfoH;

            double waLeft = SystemParameters.WorkArea.Left;
            double waRight = SystemParameters.WorkArea.Right;
            double left = anchorCenterX - wdip / 2;
            left = Math.Max(waLeft + 4, Math.Min(left, waRight - wdip - 4));
            double top = barTop - hdip - 8;
            if (top < 0) top = barTop + 8;
            if (DebugCorner) { left = 80; top = 80; }

            dpi = (float)DpiForPoint(left, top);

            if (hwnd == IntPtr.Zero)
            {
                hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "preview",
                    WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, 0, 0, 10, 10,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                {
                    Program.Log("preview: CreateWindowEx failed, err=" + Marshal.GetLastWin32Error());
                    return;
                }
            }

            int pxW = (int)Math.Round(wdip * dpi);
            int pxH = (int)Math.Round(hdip * dpi);
            int pxX = (int)Math.Round(left * dpi);
            int pxY = (int)Math.Round(top * dpi);
            SetWindowPos(hwnd, new IntPtr(-1), pxX, pxY, pxW, pxH, SWP_NOACTIVATE | SWP_SHOWWINDOW);

            IntPtr rgn = CreateRoundRectRgn(0, 0, pxW + 1, pxH + 1, (int)(14 * dpi), (int)(14 * dpi));
            SetWindowRgn(hwnd, rgn, true);
            InvalidateRect(hwnd, IntPtr.Zero, true);
            UpdateWindow(hwnd);

            RegisterThumbs();

            Program.Log("preview: shown " + pxX + "," + pxY + " " + pxW + "x" + pxH
                + " cells=" + n + " hwnd=0x" + hwnd.ToInt64().ToString("X")
                + " visible=" + IsWindowVisible(hwnd).ToString());
        }

        private static double DpiForPoint(double left, double top)
        {
            try
            {
                IntPtr p = MonitorFromPoint(new POINT { x = (int)left, y = (int)top }, 2);
                uint x = 0, y = 0;
                if (GetDpiForMonitor(p, 0, out x, out y) == 0 && x > 0) return x / 96.0;
            }
            catch { }
            return 1.0;
        }

        private void RegisterThumbs()
        {
            for (int i = 0; i < sources.Count; i++)
            {
                IntPtr thumb;
                int hr = DwmRegisterThumbnail(hwnd, sources[i], out thumb);
                if (hr != 0 || thumb == IntPtr.Zero) continue;
                thumbIds.Add(thumb);

                DWM_THUMBNAIL_PROPERTIES props = new DWM_THUMBNAIL_PROPERTIES();
                props.dwFlags = 0x1 | 0x8 | 0x10 | 0x100;   // RECTDESTINATION | OPACITY | VISIBLE | SOURCECLIENTAREAONLY
                props.rcDestination = new RECT
                {
                    Left = (int)Math.Round((Pad + i * (cellW + Gap)) * dpi),
                    Top = (int)Math.Round(Pad * dpi),
                    Right = (int)Math.Round((Pad + i * (cellW + Gap) + cellW) * dpi),
                    Bottom = (int)Math.Round((Pad + cellH) * dpi)
                };
                props.opacity = 255;
                props.fVisible = true;
                props.fSourceClientAreaOnly = false;
                DwmUpdateThumbnailProperties(thumb, ref props);
            }
            Program.Log("preview: " + thumbIds.Count + "/" + sources.Count + " thumbnail(s) registered");
        }

        private void ClearThumbs()
        {
            foreach (IntPtr t in thumbIds)
            {
                if (t != IntPtr.Zero) DwmUnregisterThumbnail(t);
            }
            thumbIds.Clear();
        }

        public void Hide()
        {
            ClearThumbs();
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
            {
                ShowWindow(hwnd, 0);   // SW_HIDE
                Program.Log("preview: hidden");
            }
        }

        public void NotifyEntered()
        {
            if (PointerEntered != null) PointerEntered(this, EventArgs.Empty);
        }

        // ---- Win32 ----

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            public bool fVisible;
            public bool fSourceClientAreaOnly;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int name);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rc);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rc, bool erase);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, int flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    }
}
