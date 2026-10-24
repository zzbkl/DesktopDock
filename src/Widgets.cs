using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopDock
{
    // ---- 系统数据：CPU / 内存（不引 WMI，直接调 kernel32，开销可以忽略） ----
    internal static class Stats
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
            public ulong Value { get { return ((ulong)High << 32) | Low; } }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        private static ulong lastIdle;
        private static ulong lastBusy;
        private static bool hasLast;

        public static double CpuPercent()
        {
            FILETIME idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return 0;
            ulong i = idle.Value;
            ulong busy = kernel.Value + user.Value - i;
            double pct = 0;
            if (hasLast)
            {
                ulong di = i - lastIdle;
                ulong db = busy - lastBusy;
                ulong total = di + db;
                if (total > 0) pct = db * 100.0 / total;
            }
            lastIdle = i;
            lastBusy = busy;
            hasLast = true;
            return pct;
        }

        // 网速：所有已连接网卡的收发字节差分（KB/s）
        private static long lastRx = -1;
        private static long lastTx = -1;
        private static DateTime lastNetAt = DateTime.MinValue;

        public static void Net(out double downKB, out double upKB)
        {
            downKB = 0; upKB = 0;
            long rx = 0, tx = 0;
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in
                    System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                    System.Net.NetworkInformation.IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
            }
            catch { return; }
            DateTime now = DateTime.UtcNow;
            if (lastRx >= 0 && now > lastNetAt)
            {
                double secs = (now - lastNetAt).TotalSeconds;
                if (secs > 0.1)
                {
                    downKB = Math.Max(0, (rx - lastRx) / 1024.0 / secs);
                    upKB = Math.Max(0, (tx - lastTx) / 1024.0 / secs);
                }
            }
            lastRx = rx; lastTx = tx; lastNetAt = now;
        }

        // 返回：已用 GB / 总 GB / 百分比
        public static void Memory(out double used, out double total, out double percent)
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            used = 0; total = 0; percent = 0;
            if (!GlobalMemoryStatusEx(ref m)) return;
            total = m.ullTotalPhys / 1073741824.0;
            used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            if (m.ullTotalPhys > 0) percent = (m.ullTotalPhys - m.ullAvailPhys) * 100.0 / m.ullTotalPhys;
        }
    }

    internal class WidgetPanel : Window
    {
        private const double PanelW = 372;
        private const double PanelH = 136;

        private Image backdrop;
        private TextBlock timeText;
        private TextBlock dateText;
        private TextBlock cpuText;
        private TextBlock memText;
        private TextBlock netText;
        private Border cpuFill;
        private Border memFill;
        private DispatcherTimer timer;

        private const double BarW = 118;

        public WidgetPanel()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Width = PanelW;
            Height = PanelH;
            UseLayoutRounding = true;

            Grid root = new Grid();

            backdrop = new Image();
            backdrop.Stretch = Stretch.Fill;
            backdrop.Opacity = 0.92;
            backdrop.Effect = new BlurEffect();
            ((BlurEffect)backdrop.Effect).Radius = 30;
            ((BlurEffect)backdrop.Effect).KernelType = KernelType.Gaussian;
            root.Children.Add(backdrop);

            Border tint = new Border();
            tint.CornerRadius = new CornerRadius(20);
            tint.Background = Glass.TintedGradient(0x40, 0x74);
            tint.BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
            tint.BorderThickness = new Thickness(1);
            root.Children.Add(tint);

            Grid body = new Grid();
            body.Margin = new Thickness(20, 14, 20, 14);
            ColumnDefinition leftCol = new ColumnDefinition();
            leftCol.Width = GridLength.Auto;
            body.ColumnDefinitions.Add(leftCol);
            ColumnDefinition rightCol = new ColumnDefinition();
            rightCol.Width = new GridLength(1, GridUnitType.Star);
            body.ColumnDefinitions.Add(rightCol);

            StackPanel left = new StackPanel();
            left.VerticalAlignment = VerticalAlignment.Center;
            timeText = new TextBlock();
            timeText.Text = "00:00";
            timeText.FontSize = 32;
            timeText.FontWeight = FontWeights.Light;
            timeText.Foreground = Brushes.White;
            timeText.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
            left.Children.Add(timeText);
            dateText = new TextBlock();
            dateText.FontSize = 12;
            dateText.Margin = new Thickness(1, 2, 0, 0);
            dateText.Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE6));
            left.Children.Add(dateText);
            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            StackPanel right = new StackPanel();
            right.VerticalAlignment = VerticalAlignment.Center;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            StackPanel cpuRow = Row("CPU", out cpuText, out cpuFill);
            StackPanel memRow = Row("内存", out memText, out memFill);
            StackPanel netRow = NetRow(out netText);
            right.Children.Add(cpuRow);
            right.Children.Add(memRow);
            right.Children.Add(netRow);
            Grid.SetColumn(right, 1);
            body.Children.Add(right);

            root.Children.Add(body);
            Content = root;

            // 拖动：按住卡片任意位置拖到哪就是哪，松手记位置
            MouseRightButtonUp += delegate
            {
                Program.Log("widgets: hidden by right click (只本次使用，设置里可再打开)");
                if (owner != null) owner.WidgetWanted = false;
                HidePanel();
            };

            MouseLeftButtonDown += delegate(object s6, MouseButtonEventArgs e6)
            {
                try
                {
                    dragStart = PointToScreen(e6.GetPosition(this));
                    dragLeft = Left;
                    dragTop = Top;
                    dragging = true;
                    CaptureMouse();
                    Program.Log("widgets: drag start at screen " + dragStart.ToString());
                }
                catch (Exception ex) { Program.Log("widgets: drag failed: " + ex.Message); }
            };

            MouseMove += delegate(object s7, MouseEventArgs e7)
            {
                if (!dragging) return;
                try
                {
                    Point now = PointToScreen(e7.GetPosition(this));
                    DpiScale dpi = VisualTreeHelper.GetDpi(this);
                    double nx = dragLeft + (now.X - dragStart.X) / (dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX);
                    double ny = dragTop + (now.Y - dragStart.Y) / (dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY);
                    double maxX = ScreenW() - PanelW - 8;
                    double maxY = ScreenH() - PanelH - 8;
                    Left = Math.Min(Math.Max(8, nx), Math.Max(8, maxX));
                    Top = Math.Min(Math.Max(8, ny), Math.Max(8, maxY));
                }
                catch { }
            };

            MouseLeftButtonUp += delegate
            {
                if (!dragging) return;
                dragging = false;
                try { ReleaseMouseCapture(); } catch { }
                SavePos();
                Program.Log("widgets: moved to " + Left.ToString("0") + "," + Top.ToString("0"));
            };

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += delegate { Refresh(); };
        }

        private StackPanel Row(string label, out TextBlock value, out Border fill)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(0, 3, 0, 3);

            StackPanel head = new StackPanel();
            head.Orientation = Orientation.Horizontal;
            TextBlock name = new TextBlock();
            name.Text = label;
            name.FontSize = 11;
            name.Width = 30;
            name.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD0, 0xDB));
            head.Children.Add(name);
            TextBlock v = new TextBlock();
            v.FontSize = 11;
            v.Foreground = Brushes.White;
            v.HorizontalAlignment = HorizontalAlignment.Right;
            v.Width = 76;
            v.TextAlignment = TextAlignment.Right;
            head.Children.Add(v);
            sp.Children.Add(head);

            Border track = new Border();
            track.Width = BarW;
            track.Height = 4;
            track.CornerRadius = new CornerRadius(2);
            track.Margin = new Thickness(0, 4, 0, 0);
            track.Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            track.HorizontalAlignment = HorizontalAlignment.Left;
            Border f = new Border();
            f.Height = 4;
            f.CornerRadius = new CornerRadius(2);
            f.Width = 0;
            f.HorizontalAlignment = HorizontalAlignment.Left;
            f.Background = new SolidColorBrush(Color.FromRgb(0x8F, 0xC8, 0xFF));
            track.Child = f;
            sp.Children.Add(track);

            value = v;
            fill = f;
            return sp;
        }

        private StackPanel NetRow(out TextBlock value)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(0, 3, 0, 3);
            TextBlock name = new TextBlock();
            name.Text = "网速";
            name.FontSize = 11;
            name.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD0, 0xDB));
            sp.Children.Add(name);
            TextBlock v = new TextBlock();
            v.FontSize = 11;
            v.Width = BarW;
            v.Margin = new Thickness(0, 2, 0, 0);
            v.Foreground = Brushes.White;
            sp.Children.Add(v);
            value = v;
            return sp;
        }

        private bool shown;
        private DockWindow owner;
        private bool dragging;
        private Point dragStart;
        private double dragLeft;
        private double dragTop;

        // 启动早期 SystemParameters 可能是 0，这里给一层兜底，免得位置被夹到 8
        private static double ScreenW()
        {
            double v = SystemParameters.VirtualScreenWidth;
            if (v <= 0) v = SystemParameters.PrimaryScreenWidth;
            if (v <= 0) v = 1920;
            return v;
        }

        private static double ScreenH()
        {
            double v = SystemParameters.VirtualScreenHeight;
            if (v <= 0) v = SystemParameters.PrimaryScreenHeight;
            if (v <= 0) v = 1080;
            return v;
        }

        public bool Showing { get { return shown; } }

        public void ShowAbove(DockWindow dock) { ShowAbove(dock, false); }

        public void ShowAbove(DockWindow dock, bool force)
        {
            owner = dock;
            if (shown && !force) return;
            ConfigStore.DockSettings cfg = ConfigStore.LoadSettings();
            Program.Log("widgets: cfg x=" + cfg.WidgetX.ToString() + " y=" + cfg.WidgetY.ToString() + " scr=" + ScreenW().ToString("0") + "x" + ScreenH().ToString("0") + " dock=" + dock.Left.ToString("0") + "," + dock.Top.ToString("0"));
            if (cfg.WidgetX >= 0 && cfg.WidgetY >= 0)
            {
                // 用户拖过：按记下的位置放，但仍保证在屏幕里
                double sw = ScreenW();
                double sh = ScreenH();
                double maxX = sw - PanelW - 8;
                double maxY = sh - PanelH - 8;
                Left = Math.Min(Math.Max(8, cfg.WidgetX), Math.Max(8, maxX));
                Top = Math.Min(Math.Max(8, cfg.WidgetY), Math.Max(8, maxY));
            }
            else
            {
                // 默认位置：屏幕水平居中、栏正上方（用工作区算，避免启动早期 dock.Top 还是 0）
                double barH = ConfigStore.LoadSettings().BarHeight;
                double barTop = dock.Top;
                if (barTop < 100)
                {
                    // 启动早期 dock 还没摆好，用工作区反推
                    double anchorBottom = SystemParameters.WorkArea.Bottom;
                    if (anchorBottom <= 0) anchorBottom = ScreenH() - 48;
                    barTop = anchorBottom - barH;
                }
                Left = Math.Round((ScreenW() - PanelW) / 2);
                Top = Math.Round(barTop - PanelH - 8);
                if (Top < 8) Top = 8;
            }

            if (shown) { shown = false; Hide(); }

            BitmapSource src = Glass.CaptureRect(this, Left, Top, PanelW, PanelH);
            if (src != null) backdrop.Source = src;

            Native.ApplyRoundedRegion(this, 40);
            Refresh();
            Show();
            Opacity = 0;
            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
            fade.EasingFunction = new QuadraticEase();
            BeginAnimation(OpacityProperty, fade);
            shown = true;
            timer.Start();
            Program.Log("widgets: shown " + Left.ToString("0") + "," + Top.ToString("0"));
        }

        public void HidePanel()
        {
            if (!shown) return;
            shown = false;
            timer.Stop();
            Hide();
        }

        private void SavePos()
        {
            try
            {
                ConfigStore.DockSettings cfg = ConfigStore.LoadSettings();
                double maxX = ScreenW() - PanelW - 8;
                double maxY = ScreenH() - PanelH - 8;
                cfg.WidgetX = (int)Math.Round(Math.Min(Math.Max(8, Left), Math.Max(8, maxX)));
                cfg.WidgetY = (int)Math.Round(Math.Min(Math.Max(8, Top), Math.Max(8, maxY)));
                ConfigStore.SaveSettings(cfg);
                Program.Log("widgets: moved to " + cfg.WidgetX.ToString() + "," + cfg.WidgetY.ToString());
            }
            catch (Exception ex) { Program.Log("widgets: save pos failed: " + ex.Message); }
        }

        private void Refresh()
        {
            DateTime now = DateTime.Now;
            timeText.Text = now.ToString("HH:mm");
            string[] wk = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            dateText.Text = now.Month.ToString() + "月" + now.Day.ToString() + "日  " + wk[(int)now.DayOfWeek];

            double cpu = Stats.CpuPercent();
            cpuText.Text = cpu.ToString("0") + "%";
            cpuFill.Width = Math.Max(1, BarW * Math.Min(100, cpu) / 100.0);
            cpuFill.Background = BarBrush(cpu);

            double used, total, memPct;
            Stats.Memory(out used, out total, out memPct);
            memText.Text = memPct.ToString("0") + "%  " + used.ToString("0.0") + "/" + total.ToString("0") + "G";
            memFill.Width = Math.Max(1, BarW * Math.Min(100, memPct) / 100.0);
            memFill.Background = BarBrush(memPct);

            double down, up;
            Stats.Net(out down, out up);
            netText.Text = "↓ " + Speed(down) + "   ↑ " + Speed(up);
        }

        private static string Speed(double kb)
        {
            if (kb >= 1024) return (kb / 1024).ToString("0.0") + " MB/s";
            return kb.ToString("0") + " KB/s";
        }

        private static Brush BarBrush(double pct)
        {
            if (pct >= 85) return new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x8A));
            if (pct >= 65) return new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x7A));
            return new SolidColorBrush(Color.FromRgb(0x8F, 0xC8, 0xFF));
        }
    }
}
