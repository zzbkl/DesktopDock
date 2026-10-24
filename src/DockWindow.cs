using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopDock
{
    internal class DockWindow : Window
    {
        public static double IconSize = 52;
        public static double BarHeight = 96;
        public static int TintTop = 0x36;
        public static int TintBottom = 0x62;
        public static readonly Brush HoverBg = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));

        private StackPanel bar;
        private TextBlock statusText;
        private Border statusBox;
        private Border shell;
        private Image backdrop;

        // 悬停放大（AirDock / macOS 那种"波峰"效果）
        private List<Border> appHosts = new List<Border>();
        private Dictionary<Border, AppItem> hostItems = new Dictionary<Border, AppItem>();
        private Dictionary<Border, ScaleTransform> hostScales = new Dictionary<Border, ScaleTransform>();
        private Dictionary<Border, TranslateTransform> hostMoves = new Dictionary<Border, TranslateTransform>();
        private Dictionary<Border, TranslateTransform> hostBounce = new Dictionary<Border, TranslateTransform>();
        private const double MagnifyAmp = 0.60;     // 峰值放大倍数 - 1（对应 AirDeck 的 DockMagnifyScale=1.75）
        private const double MagnifySigma = 62;     // 影响半径（DIP）
        private const double PushAmp = 15;          // 邻居被推开的像素（只改变换，不触发布局）

        private DispatcherTimer tickTimer;
        private DispatcherTimer refreshTimer;
        private DispatcherTimer trimTimer;
        private DispatcherTimer peekTimer;

        // 悬停预览（DWM 缩略图）
        private PreviewHost preview;
        private DispatcherTimer previewTimer;
        private DispatcherTimer previewHideTimer;
        private Border pendingHost;
        private AppItem pendingItem;

        private bool expanded;
        private bool pinnedOpen;      // 从托盘点开、固定住不收
        private bool deskAutoShow = true;   // 回到桌面时自动亮出来（设置里可关）
        private bool deskShown;             // 当前这次显示是"桌面自动出现"，不是用户叫的
        private bool deskWasFg;             // 上一拍前台是不是桌面 —— 用来取上升沿
        private int deskAwayTicks;          // 离开桌面后已经连续几拍不是桌面（防抖，避免闪一下又回来）
        private int lastOverTick;
        private bool autoHideReady;         // 启动时把用户原来的自动隐藏状态记下来，退出时还原
        private bool autoHideApplied = true;
        private int autoHideDebounce;
        private bool glassOn;
        private const int HotkeyId = 0x0D01;
        private const int HotkeyId2 = 0x0D03;   // Alt+Space（沿用 AirDeck 的 Launchpad 热键）

        // 拖动排序
        private Border dragHost;
        private Point dragOrigin;
        private bool dragging;
        private Action orderChanged;

        public DrawerWindow Drawer { get; set; }
        public WidgetPanel Widgets { get; set; }

        // 小组件"想要显示"的开关：设置里勾选 / 取消，右键只是临时收起
        public bool WidgetWanted { get; set; }
        public PeekWindow Peek { get; set; }
        public System.Windows.Controls.ContextMenu BarMenu { get; set; }
        public List<AppItem> Items { get; set; }
        public Action OnRescan { get; set; }
        public List<AppItem> AllApps { get; set; }

        public void RescanFromSettings()
        {
            if (OnRescan != null) OnRescan();
        }

        public string[] PinnedNames()
        {
            if (Items == null) return new string[0];
            string[] names = new string[Items.Count];
            for (int i = 0; i < Items.Count; i++) names[i] = Items[i] == null ? "" : Items[i].Name;
            return names;
        }

        // 调试开关：--force 时不自动收起（截图核对用）
        public bool ForceVisible { get; set; }

        public DockWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = false;     // 千万不要开，否则 WPF 退回软件渲染
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            CreateContent();
            LoadHotCorners();
            LoadWidgetPrefs();
        }

        private void CreateContent()
        {
            bar = new StackPanel();
            bar.Orientation = Orientation.Horizontal;
            bar.VerticalAlignment = VerticalAlignment.Center;

            backdrop = new Image();
            backdrop.Stretch = Stretch.Fill;
            backdrop.IsHitTestVisible = false;

            shell = new Border();
            shell.CornerRadius = new CornerRadius(18);
            shell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF));
            shell.BorderThickness = new Thickness(1);
            shell.Padding = new Thickness(14, 8, 14, 8);
            shell.Child = bar;
            shell.Height = BarHeight;
            shell.RenderTransform = new TranslateTransform(0, 0);

            // 放大效果用指针在整条栏里的 X 位置算，鼠标一动就重算
            shell.MouseMove += delegate(object s, MouseEventArgs e)
            {
                UpdateMagnify(e.GetPosition(bar).X);
            };
            shell.MouseLeave += delegate
            {
                ResetMagnify();
            };

            // 滚轮：在栏上滚，依次切到下一个/上一个正在运行的应用（AirDeck 的滚轮换窗）
            shell.MouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                CycleRunning(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            };

            ContextMenu menu = new ContextMenu();
            MenuItem miSettings = new MenuItem();
            miSettings.Header = "设置（管理面板）…";
            miSettings.Click += delegate { SettingsWindow.Open(this); };
            MenuItem miOpen = new MenuItem();
            miOpen.Header = "打开全部应用抽屉";
            miOpen.Click += delegate { if (Drawer != null) Drawer.ToggleFrom(this); };
            MenuItem miRescan = new MenuItem();
            miRescan.Header = "重新扫描应用";
            miRescan.Click += delegate { if (OnRescan != null) OnRescan(); };
            MenuItem miFolder = new MenuItem();
            miFolder.Header = "打开配置目录";
            miFolder.Click += delegate { OpenConfigFolder(); };
            MenuItem miAuto = new MenuItem();
            miAuto.Header = "开机自启动";
            miAuto.IsCheckable = true;
            miAuto.IsChecked = Startup.IsEnabled();
            miAuto.Click += delegate
            {
                Startup.SetEnabled(miAuto.IsChecked);
                SetStatus(miAuto.IsChecked ? "已设置开机自启动" : "已取消开机自启动");
            };
            MenuItem miExit = new MenuItem();
            miExit.Header = "退出";
            miExit.Click += delegate { Application.Current.Shutdown(); };
            menu.Items.Add(miSettings);
            menu.Items.Add(new Separator());
            menu.Items.Add(miOpen);
            menu.Items.Add(miRescan);
            menu.Items.Add(miFolder);
            menu.Items.Add(miAuto);
            menu.Items.Add(new Separator());
            menu.Items.Add(miExit);
            shell.ContextMenu = menu;
            BarMenu = menu;

            statusText = new TextBlock();
            statusText.FontSize = 11;
            statusText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            statusText.VerticalAlignment = VerticalAlignment.Center;
            statusText.HorizontalAlignment = HorizontalAlignment.Center;
            statusText.Margin = new Thickness(10, 0, 10, 0);

            statusBox = new Border();
            statusBox.CornerRadius = new CornerRadius(10);
            statusBox.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x12, 0x13, 0x18));
            statusBox.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            statusBox.BorderThickness = new Thickness(1);
            statusBox.Padding = new Thickness(12, 4, 12, 4);
            statusBox.HorizontalAlignment = HorizontalAlignment.Center;
            statusBox.VerticalAlignment = VerticalAlignment.Center;
            statusBox.Child = statusText;
            statusBox.Visibility = Visibility.Collapsed;

            Grid root = new Grid();
            root.Children.Add(backdrop);
            root.Children.Add(shell);
            root.Children.Add(statusBox);
            Content = root;

            // 默认外观（抓屏失败时用这个）
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A));
            shell.Background = OpaqueGradient();
        }

        private static Brush OpaqueGradient()
        {
            LinearGradientBrush grad = new LinearGradientBrush();
            grad.StartPoint = new Point(0, 0);
            grad.EndPoint = new Point(0, 1);
            grad.GradientStops.Add(new GradientStop(Color.FromRgb(0x25, 0x26, 0x2D), 0));
            grad.GradientStops.Add(new GradientStop(Color.FromRgb(0x17, 0x18, 0x1D), 1));
            return grad;
        }

        public void SetItems(List<AppItem> items)
        {
            Items = items;
            Rebuild();
        }

        public void SetOrderChanged(Action a)
        {
            orderChanged = a;
        }

        public void Rebuild()
        {
            bar.Children.Clear();
            appHosts.Clear();
            hostScales.Clear();
            hostMoves.Clear();
            hostBounce.Clear();
            slotUsed = new HashSet<string>();
            System.Diagnostics.Stopwatch rbSw = System.Diagnostics.Stopwatch.StartNew();
            rebuildFresh = 0; rebuildReuse = 0;

            bar.Children.Add(MakeGridButton("全部应用", delegate
            {
                if (Drawer != null) Drawer.ToggleFrom(this);
            }));
            bar.Children.Add(MakeSeparator());

            int pinned = 0;
            if (Items != null)
            {
                foreach (AppItem item in Items)
                {
                    if (pinned >= 12) break;
                    bar.Children.Add(MakeAppButton(item));
                    pinned++;
                }
            }

            List<AppFolder> folders = FolderStore.Load();
            foreach (AppFolder f in folders)
            {
                if (f.Members == null || f.Members.Count == 0) continue;
                bar.Children.Add(MakeFolderButton(f));
            }

            bar.Children.Add(MakeSeparator());
            bar.Children.Add(MakeGlyphButton("\uE713", "设置（管理面板）", delegate { SettingsWindow.Open(this); }));
            TrimSlotPool();
            Program.Log("dock rebuild: " + appHosts.Count.ToString() + " slots in " + rbSw.ElapsedMilliseconds.ToString()
                + "ms (fresh=" + rebuildFresh.ToString() + " reused=" + rebuildReuse.ToString() + ")");
            RefreshDots();
            if (expanded) RefreshBackdrop();
        }

        // ② 图标槽生命周期：宿主 Border 按「名字+目标」缓存复用。
        // 拖动排序、加文件夹、改设置都会 Rebuild，以前每次都重新抽图标（几十毫秒 + 一堆 GDI 句柄），
        // 现在旧图标直接回到自己的槽位，只有新出现的图标才建 + 播一段"落地"动画（AirDeck 的 land 那套）。
        private readonly Dictionary<string, Border> slotPool = new Dictionary<string, Border>();
        private HashSet<string> slotUsed = new HashSet<string>();
        private int rebuildFresh;
        private int rebuildReuse;

        private FrameworkElement MakeAppButton(AppItem item)
        {
            string key = "A|" + item.Name + "|" + item.Target;
            slotUsed.Add(key);
            Border host;
            bool fresh = false;
            if (!slotPool.TryGetValue(key, out host) || host == null)
            {
                host = BuildAppButton(item);
                slotPool[key] = host;
                fresh = true;
            }

            // 复用的宿主也要重新登记（这几个字典每次 Rebuild 都会清空）
            TransformGroup tg = host.RenderTransform as TransformGroup;
            appHosts.Add(host);
            hostItems[host] = item;
            if (tg != null && tg.Children.Count >= 3)
            {
                hostScales[host] = tg.Children[0] as ScaleTransform;
                hostMoves[host] = tg.Children[1] as TranslateTransform;
                hostBounce[host] = tg.Children[2] as TranslateTransform;
            }
            if (fresh) { rebuildFresh++; LandIcon(host); }
            else
            {
                rebuildReuse++;
                if (hostScales.ContainsKey(host) && hostScales[host] != null) { hostScales[host].ScaleX = 1; hostScales[host].ScaleY = 1; }
                if (hostMoves.ContainsKey(host) && hostMoves[host] != null) hostMoves[host].X = 0;
                host.Opacity = 1;
            }
            return host;
        }

        // 新图标"落进槽位"：180ms 淡入 + 从下方 8px 归位，缓动 QuinticOut
        private void LandIcon(Border host)
        {
            if (host == null) return;
            TranslateTransform bounce;
            if (!hostBounce.TryGetValue(host, out bounce)) { host.Opacity = 1; return; }
            host.Opacity = 0;
            bounce.Y = 8;
            double t = 0;
            Action<double> step = null;
            step = delegate(double dt)
            {
                t += dt;
                double k = UIFrame.QuinticOut(t / 180.0);
                host.Opacity = k;
                bounce.Y = 8 * (1 - k);
                if (t >= 180)
                {
                    host.Opacity = 1;
                    bounce.Y = 0;
                    UIFrame.Remove(step);
                }
            };
            UIFrame.Add(step);
        }

        // 清掉这一轮没用到的槽（比如某个应用被取消固定了）
        private void TrimSlotPool()
        {
            List<string> dead = null;
            foreach (string k in slotPool.Keys)
            {
                if (!slotUsed.Contains(k))
                {
                    if (dead == null) dead = new List<string>();
                    dead.Add(k);
                }
            }
            if (dead == null) return;
            foreach (string k in dead) slotPool.Remove(k);
            if (dead.Count > 0) Program.Log("dock slots: reused=" + slotUsed.Count.ToString() + ", evicted=" + dead.Count.ToString());
        }

        private Border BuildAppButton(AppItem item)
        {
            Border host = new Border();
            host.Width = IconSize + 22;
            host.Height = IconSize + 26;
            host.CornerRadius = new CornerRadius(11);
            host.Margin = new Thickness(2, 0, 2, 0);
            host.Background = Brushes.Transparent;
            host.Cursor = Cursors.Hand;
            host.ToolTip = item.Name;

            ScaleTransform st = new ScaleTransform(1, 1);
            TranslateTransform tt = new TranslateTransform(0, 0);
            TranslateTransform bounce = new TranslateTransform(0, 0);
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(st);
            tg.Children.Add(tt);
            tg.Children.Add(bounce);
            host.RenderTransform = tg;
            host.RenderTransformOrigin = new Point(0.5, 0.5);

            Grid inner = new Grid();
            host.Child = inner;

            bool usedPack;
            FrameworkElement iconEl = IconTile.Build(item, IconSize, out usedPack);
            inner.Children.Add(iconEl);

            System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse();
            dot.Width = 4;
            dot.Height = 4;
            dot.Fill = Brushes.White;
            dot.HorizontalAlignment = HorizontalAlignment.Center;
            dot.VerticalAlignment = VerticalAlignment.Bottom;
            dot.Margin = new Thickness(0, 0, 0, -10);
            dot.IsHitTestVisible = false;
            dot.Visibility = Visibility.Collapsed;
            inner.Children.Add(dot);
            host.Tag = dot;

            host.MouseEnter += delegate { SchedulePreview(host, item); };
            host.MouseLeave += delegate
            {
                CancelPendingPreview();
                HidePreviewSoon();
            };
            host.MouseLeftButtonDown += delegate(object s2, MouseButtonEventArgs e2) { BeginDrag(host, e2); };
            host.MouseMove += delegate(object s3, MouseEventArgs e3) { ContinueDrag(e3); };
            host.MouseLeftButtonUp += delegate(object s4, MouseButtonEventArgs e4) { EndDrag(host, item); };
            return host;
        }

        // 常驻栏上的文件夹：2x2 迷你图标 + 下角数量，点开弹出小面板
        private FrameworkElement MakeFolderButton(AppFolder folder)
        {
            if (AllApps == null || AllApps.Count == 0)
            {
                try { AllApps = ConfigStore.Load(); }
                catch { }
            }
            Border host = new Border();
            host.Width = IconSize + 22;
            host.Height = IconSize + 26;
            host.CornerRadius = new CornerRadius(11);
            host.Margin = new Thickness(2, 0, 2, 0);
            host.Background = Brushes.Transparent;
            host.Cursor = Cursors.Hand;
            host.ToolTip = folder.Name;

            ScaleTransform st = new ScaleTransform(1, 1);
            TranslateTransform tt = new TranslateTransform(0, 0);
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(st);
            tg.Children.Add(tt);
            host.RenderTransform = tg;
            host.RenderTransformOrigin = new Point(0.5, 0.5);

            Grid wrap = new Grid();
            wrap.Width = IconSize;
            wrap.Height = IconSize;
            wrap.HorizontalAlignment = HorizontalAlignment.Center;
            wrap.VerticalAlignment = VerticalAlignment.Center;

            List<AppItem> picked = new List<AppItem>();
            List<AppItem> pool = AllApps;
            if (pool != null)
            {
                foreach (string name in folder.Members)
                {
                    foreach (AppItem it in pool)
                    {
                        if (it != null && it.Name == name) { picked.Add(it); break; }
                    }
                    if (picked.Count >= 4) break;
                }
            }

            int n = picked.Count;
            Program.Log("folder button: " + folder.Name + " picked=" + n.ToString() + " pool=" + (pool == null ? -1 : pool.Count).ToString());
            double mini = n <= 1 ? IconSize - 10 : (IconSize - 9) / 2;
            Canvas canvas = new Canvas();
            canvas.Width = n <= 1 ? mini : mini * 2 + 3;
            canvas.Height = n <= 1 ? mini : mini * 2 + 3;
            canvas.HorizontalAlignment = HorizontalAlignment.Center;
            canvas.VerticalAlignment = VerticalAlignment.Center;
            for (int k = 0; k < n; k++)
            {
                bool usedPack;
                FrameworkElement el = IconTile.Build(picked[k], mini, out usedPack);
                el.Width = mini;
                el.Height = mini;
                double x = n <= 1 ? 0 : (k % 2) * (mini + 3);
                double y = n <= 1 ? 0 : (k / 2) * (mini + 3);
                Canvas.SetLeft(el, x);
                Canvas.SetTop(el, y);
                canvas.Children.Add(el);
            }
            wrap.Children.Add(canvas);

            Border frame = new Border();
            frame.CornerRadius = new CornerRadius(14);
            frame.Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            frame.BorderThickness = new Thickness(1);
            frame.Width = IconSize + 4;
            frame.Height = IconSize + 4;
            frame.HorizontalAlignment = HorizontalAlignment.Center;
            frame.VerticalAlignment = VerticalAlignment.Center;
            frame.Child = wrap;

            Grid inner = new Grid();
            inner.Children.Add(frame);
            System.Windows.Shapes.Ellipse badge = new System.Windows.Shapes.Ellipse();
            badge.Width = 14;
            badge.Height = 14;
            badge.Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2F, 0x3A));
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Bottom;
            badge.Margin = new Thickness(0, 0, 2, 2);
            inner.Children.Add(badge);
            TextBlock cnt = new TextBlock();
            cnt.Text = folder.Members.Count.ToString();
            cnt.FontSize = 9;
            cnt.Foreground = Brushes.White;
            cnt.HorizontalAlignment = HorizontalAlignment.Right;
            cnt.VerticalAlignment = VerticalAlignment.Bottom;
            cnt.Margin = new Thickness(0, 0, 6, 3);
            cnt.IsHitTestVisible = false;
            inner.Children.Add(cnt);
            host.Child = inner;

            host.MouseLeftButtonUp += delegate { FolderPopup.Open(this, folder, host); };
            return host;
        }

        private FrameworkElement MakeGlyphButton(string glyph, string tip, Action click)
        {
            Border host = new Border();
            host.Width = IconSize + 10;
            host.Height = IconSize + 20;
            host.CornerRadius = new CornerRadius(11);
            host.Margin = new Thickness(2, 0, 2, 0);
            host.Background = Brushes.Transparent;
            host.Cursor = Cursors.Hand;
            host.ToolTip = tip;

            TextBlock t = new TextBlock();
            t.Text = glyph;
            t.FontFamily = new FontFamily("Segoe MDL2 Assets");
            t.FontSize = IconSize * 0.52;
            t.Foreground = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            host.Child = t;

            host.MouseEnter += delegate
            {
                host.Background = HoverBg;
            };
            host.MouseLeave += delegate { host.Background = Brushes.Transparent; };
            host.MouseLeftButtonUp += delegate { if (click != null) click(); };
            return host;
        }

        private FrameworkElement MakeGridButton(string tip, Action click)
        {
            Border host = new Border();
            host.Width = IconSize + 16;
            host.Height = IconSize + 20;
            host.CornerRadius = new CornerRadius(11);
            host.Margin = new Thickness(2, 0, 2, 0);
            host.Background = Brushes.Transparent;
            host.Cursor = Cursors.Hand;
            host.ToolTip = tip;

            Canvas cv = new Canvas();
            cv.Width = Cel * 3 + Pitch * 2;
            cv.Height = Cel * 3 + Pitch * 2;
            cv.HorizontalAlignment = HorizontalAlignment.Center;
            cv.VerticalAlignment = VerticalAlignment.Center;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    Border sq = new Border();
                    sq.Width = Cel;
                    sq.Height = Cel;
                    sq.CornerRadius = new CornerRadius(1.5);
                    sq.Background = Brushes.White;
                    Canvas.SetLeft(sq, c * (Cel + Pitch));
                    Canvas.SetTop(sq, r * (Cel + Pitch));
                    cv.Children.Add(sq);
                }
            }
            host.Child = cv;

            host.MouseEnter += delegate { host.Background = HoverBg; };
            host.MouseLeave += delegate { host.Background = Brushes.Transparent; };
            host.MouseLeftButtonUp += delegate { if (click != null) click(); };
            return host;
        }

        private const double Cel = 5.0;
        private const double Pitch = 5.0;

        // 悬停放大：指针在栏里的位置决定每个图标的倍数
        // ① 逐帧驱动：鼠标事件只更新"目标位置"，真正的插值在 CompositionTarget.Rendering 里跑，
        //    所以 60Hz/120Hz/165Hz 屏都是满帧、掉帧也不会跳变。
        // ② 图标槽：每个图标有固定宿主（Rect + 固定 Margin），放大只改 RenderTransform，
        //    不参与布局 → 邻居不会因为放大而重排（AirDeck 的槽位思路）。
        private void UpdateMagnify(double x)
        {
            magnifyTargetX = x;
            if (magnifyCurX < 0) magnifyCurX = x;
            if (magnifyStep == null) magnifyStep = MagnifyStep;
            UIFrame.Add(magnifyStep);
        }

        private void MagnifyStep(double dt)
        {
            if (magnifyTargetX < 0) { UIFrame.Remove(magnifyStep); return; }
            magnifyCurX = UIFrame.Approach(magnifyCurX, magnifyTargetX, dt, 42);
            if (Math.Abs(magnifyCurX - magnifyTargetX) < 0.4) magnifyCurX = magnifyTargetX;
            ApplyMagnify(magnifyCurX);
            // 追到目标后 300ms 就退订：鼠标不动时不再逐帧回调（省电），
            // 下一次 MouseMove 会重新 Add 回来。
            if (magnifyCurX == magnifyTargetX)
            {
                magnifySettleMs += dt;
                if (magnifySettleMs > 300)
                {
                    magnifySettleMs = 0;
                    UIFrame.Remove(magnifyStep);
                }
            }
            else magnifySettleMs = 0;
        }

        private double magnifySettleMs;

        private double magnifyTargetX = -1;
        private double magnifyCurX = -1;
        private Action<double> magnifyStep;

        private void ApplyMagnify(double x)
        {
            for (int i = 0; i < appHosts.Count; i++)
            {
                Border b = appHosts[i];
                ScaleTransform st;
                if (!hostScales.TryGetValue(b, out st)) continue;
                Point cp = b.TranslatePoint(new Point(b.ActualWidth / 2, 0), bar);
                double d = cp.X - x;
                double f = MagnifyAmp * Math.Exp(-(d * d) / (2 * MagnifySigma * MagnifySigma));
                st.ScaleX = 1 + f;
                st.ScaleY = 1 + f;
                TranslateTransform mv;
                if (hostMoves.TryGetValue(b, out mv))
                {
                    // 顺带把邻居推开一点（只改变换，不动布局）
                    mv.X = (d >= 0 ? 1 : -1) * f * PushAmp;
                }
            }
        }

        private void ResetMagnify()
        {
            magnifyTargetX = -1;
            magnifyCurX = -1;
            if (magnifyStep != null) UIFrame.Remove(magnifyStep);
            foreach (KeyValuePair<Border, ScaleTransform> kv in hostScales)
            {
                kv.Value.ScaleX = 1;
                kv.Value.ScaleY = 1;
            }
            foreach (KeyValuePair<Border, TranslateTransform> kv in hostMoves)
            {
                kv.Value.X = 0;
            }
        }

        private void BounceIcon(Border host)
        {
            if (host == null) return;
            TranslateTransform tt;
            if (!hostBounce.TryGetValue(host, out tt)) return;
            DoubleAnimation a = new DoubleAnimation(0, -11, TimeSpan.FromMilliseconds(120));
            a.AutoReverse = true;
            QuadraticEase ease = new QuadraticEase();
            ease.EasingMode = EasingMode.EaseOut;
            a.EasingFunction = ease;
            tt.BeginAnimation(TranslateTransform.YProperty, a);
        }

        // ---- 拖动排序 ----
        private void BeginDrag(Border host, MouseButtonEventArgs e)
        {
            dragHost = host;
            dragOrigin = e.GetPosition(bar);
            dragging = false;
            if (preview != null) preview.Hide();
        }

        private void ContinueDrag(MouseEventArgs e)
        {
            if (dragHost == null) return;
            Point p = e.GetPosition(bar);
            if (!dragging && Math.Abs(p.X - dragOrigin.X) < 14) return;
            dragging = true;
            TranslateTransform tt;
            if (hostMoves.TryGetValue(dragHost, out tt)) tt.X = p.X - dragOrigin.X;
            ReorderTo(p.X);
        }

        private void ReorderTo(double x)
        {
            if (dragHost == null) return;
            int from = bar.Children.IndexOf(dragHost);
            if (from < 0) return;
            for (int i = 0; i < bar.Children.Count; i++)
            {
                Border b = bar.Children[i] as Border;
                if (b == null || b == dragHost) continue;
                if (!hostItems.ContainsKey(b)) continue;
                Point cp = b.TranslatePoint(new Point(b.ActualWidth / 2, 0), bar);
                if (i < from && x < cp.X)
                {
                    bar.Children.Remove(dragHost);
                    bar.Children.Insert(i, dragHost);
                    return;
                }
                if (i > from && x > cp.X)
                {
                    bar.Children.Remove(dragHost);
                    bar.Children.Insert(i, dragHost);
                    return;
                }
            }
        }

        private void EndDrag(Border host, AppItem item)
        {
            Border h = dragHost;
            bool wasDrag = dragging;
            dragHost = null;
            dragging = false;
            TranslateTransform tt;
            if (h != null && hostMoves.TryGetValue(h, out tt)) tt.X = 0;

            if (!wasDrag)
            {
                if (FolderPopup.IsOpen) FolderPopup.CloseCurrent();
                Usage.Note(item.Name);
                Program.Log("click: " + item.Name);
                BounceIcon(h);
                string msg;
                Launcher.ActivateOrLaunch(item, out msg);
                HideDock();
                return;
            }
            PersistOrder();
        }

        private void PersistOrder()
        {
            if (Items == null) return;
            List<AppItem> ordered = new List<AppItem>();
            foreach (object child in bar.Children)
            {
                Border b = child as Border;
                if (b == null) continue;
                AppItem it;
                if (hostItems.TryGetValue(b, out it) && !ordered.Contains(it)) ordered.Add(it);
            }
            if (ordered.Count == 0) return;
            foreach (AppItem it in Items) if (!ordered.Contains(it)) ordered.Add(it);
            Items = ordered;
            string[] names = PinnedNames();
            ConfigStore.SavePinOrder(new List<string>(names));
            SetStatus("顺序已保存");
            Program.Log("pin order saved: " + string.Join(", ", names));
            if (orderChanged != null) orderChanged();
        }

        // ---- 悬停预览（420ms 才弹，扫过去不弹）----
        private void SchedulePreview(Border host, AppItem item)
        {
            CancelPendingPreview();
            pendingHost = host;
            pendingItem = item;
            previewTimer = new DispatcherTimer();
            previewTimer.Interval = TimeSpan.FromMilliseconds(420);
            previewTimer.Tick += delegate(object s, EventArgs e)
            {
                DispatcherTimer t = s as DispatcherTimer;
                if (t != null) t.Stop();
                if (pendingHost == null || pendingItem == null) return;
                ShowPreviewNow(pendingHost, pendingItem);
            };
            previewTimer.Start();
        }

        private void CancelPendingPreview()
        {
            if (previewTimer != null) { previewTimer.Stop(); previewTimer = null; }
            if (previewHideTimer != null) { previewHideTimer.Stop(); previewHideTimer = null; }
            pendingHost = null;
            pendingItem = null;
        }

        private void ShowPreviewNow(Border host, AppItem item)
        {
            try
            {
                List<IntPtr> wins = Launcher.WindowsForItem(item);
                if (wins == null || wins.Count == 0) return;
                if (host == null) return;
                double cx = host.PointToScreen(new Point(host.ActualWidth / 2, 0)).X / Native.DpiScaleX(this);
                preview = preview ?? new PreviewHost();
                preview.ShowPreviews(wins, cx, Top);
                Program.Log("preview: " + item.Name + " window(s)=" + wins.Count.ToString());
            }
            catch (Exception ex) { Program.Log("preview failed: " + ex.Message); }
        }

        private void HidePreview()
        {
            CancelPendingPreview();
            if (preview != null) preview.Hide();
        }

        private void HidePreviewSoon()
        {
            if (preview == null || !preview.Showing) return;
            previewHideTimer = new DispatcherTimer();
            previewHideTimer.Interval = TimeSpan.FromMilliseconds(280);
            previewHideTimer.Tick += delegate(object s, EventArgs e)
            {
                DispatcherTimer t = s as DispatcherTimer;
                if (t != null) t.Stop();
                if (preview != null) preview.Hide();
            };
            previewHideTimer.Start();
        }

        private FrameworkElement MakeSeparator()
        {
            Border b = new Border();
            b.Width = 1;
            b.Height = 26;
            b.Margin = new Thickness(6, 0, 6, 0);
            b.Background = new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF));
            b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }

        public void SetStatus(string msg)
        {
            statusText.Text = msg;
            statusBox.Visibility = Visibility.Visible;
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(2.2);
            t.Tick += delegate(object s, EventArgs e)
            {
                t.Stop();
                statusBox.Visibility = Visibility.Collapsed;
            };
            t.Start();
        }

        private void Flash()
        {
            shell.BorderBrush = Brushes.OrangeRed;
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(600);
            t.Tick += delegate(object s, EventArgs e)
            {
                t.Stop();
                shell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
            };
            t.Start();
        }

        private void OpenConfigFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConfigStore.Dir);
                System.Diagnostics.Process.Start("explorer.exe", ConfigStore.Dir);
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Native.MakeNoActivate(this);
            PositionAtBottom();

            // 底部常显不做了：把 dock 注册成底部 AppBar 占一条工作区，会让 Windows
            // 把"自动隐藏"的任务栏顶成常显（实测：注册时 tray=0,1528,2560,1600，
            // 注销后自动隐藏恢复成屏外的 0,1598,2560,1670）。任务栏的自动隐藏优先。
            // 相关能力留在 Native.cs（RegisterBottomBand/UnregisterBand），要再启用得先解决这个冲突。

            HwndSource src = (HwndSource)PresentationSource.FromVisual(this);
            if (src != null)
            {
                src.AddHook(WndProc);
                bool ok = Native.RegisterHotKey(src.Handle, HotkeyId, 0x0001 | 0x4000, 0xC0);
                Program.Log("hotkey Alt+` register=" + ok.ToString());
        // Alt+Space 被 AirDeck 的 Launchpad 占着，逐级退到
                bool ok2 = Native.RegisterHotKey(src.Handle, HotkeyId2, 0x0001 | 0x4000, 0x20);
                string keyName = "Alt+Space";
                if (!ok2) { ok2 = Native.RegisterHotKey(src.Handle, HotkeyId2, 0x0001 | 0x0004 | 0x4000, 0x20); keyName = "Alt+Shift+Space"; }
                if (!ok2) { ok2 = Native.RegisterHotKey(src.Handle, HotkeyId2, 0x0002 | 0x0001 | 0x4000, 0x20); keyName = "Ctrl+Alt+Space"; }
                Program.Log("hotkey " + keyName + " register=" + ok2.ToString());
            }

        // 玻璃背景：抓屏 + 高斯模糊（Win11 的系统背景材质对 WPF 客户区实测不透，还是自己抓）
            glassOn = true;
            shell.Background = Glass.TintedGradient((byte)TintTop, (byte)TintBottom);
            Native.ApplyRoundedRegion(this, 32);
            Program.Log("glass: backdrop=screen-capture-blur");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && (wParam.ToInt32() == HotkeyId || wParam.ToInt32() == HotkeyId2))
            {
                handled = true;
                if (Drawer != null) Drawer.ToggleFrom(this);
            }
            return IntPtr.Zero;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (!glassOn) Native.ApplyRoundedRegion(this, 32);
            PositionAtBottom();
        }

        public void PositionAtBottom()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Rect wa = SystemParameters.WorkArea;
            // 抬到任务栏上方；任务栏自动隐藏时也会整条升起，所以必须给它留出完整高度
            double band = hwnd == IntPtr.Zero ? 0 : Native.TaskbarBandDip(hwnd);
            double left = wa.Left + (wa.Width - ActualWidth) / 2.0;
            double top = wa.Bottom - ActualHeight - 10 - band;
            if (double.IsNaN(left) || double.IsNaN(top)) return;
            Left = Math.Round(left);
            Top = Math.Round(top);
        }

        private double DpiScale()
        {
            PresentationSource ps = PresentationSource.FromVisual(this);
            if (ps == null || ps.CompositionTarget == null) return 1.0;
            return ps.CompositionTarget.TransformToDevice.M11;
        }

        // ---- 显示/收起 ----

        public bool Expanded { get { return expanded; } }

        // 滚轮换窗：按栏上的顺序，在"正在运行的应用"之间轮换
        public void DebugCycle(int dir) { CycleRunning(dir); }

        // 调试：直接把一个 X 位置喂给放大算法（本机没法可靠模拟悬停）
        // 调试：按名字点开一个文件夹（本机没法可靠模拟点击）
        public void DebugFolder(string name)
        {
            ShowDock(true);
            UpdateLayout();
            foreach (object child in bar.Children)
            {
                Border b = child as Border;
                if (b == null) continue;
                string tip = b.ToolTip as string;
                if (tip == null || tip != name) continue;
                AppFolder hit = null;
                foreach (AppFolder f in FolderStore.Load()) if (f.Name == name) { hit = f; break; }
                if (hit == null) { Program.Log("debug folder: no folder named " + name); return; }
                FolderPopup.Open(this, hit, b);
                return;
            }
            Program.Log("debug folder: no host for " + name);
        }

        public void DebugMagnify(double x)
        {
            ShowDock(true);
            UpdateMagnify(x);
        }

        // 调试用：按名字直接弹预览（本机没法用代码模拟悬停）
        // 调试：直接预览"当前前台窗口"，用来单独验证 DWM 缩略图这条渲染路径
        public void DebugPreviewTop()
        {
            ShowDock(true);
            IntPtr fg = Native.GetForegroundWindow();
            List<IntPtr> windows = new List<IntPtr>();
            if (fg != IntPtr.Zero) windows.Add(fg);
            double cx = (appHosts != null && appHosts.Count > 0 && appHosts[0] != null)
                ? appHosts[0].PointToScreen(new Point(appHosts[0].ActualWidth / 2, 0)).X / Native.DpiScaleX(this)
                : SystemParameters.WorkArea.Right - 220;
            double barTop = Top + 12;
            preview = preview ?? new PreviewHost();
            preview.ShowPreviews(windows, cx, barTop);
            Program.Log("preview-top: fg=0x" + fg.ToInt64().ToString("X") + " title=" + Native.TitleOfWindow(fg));
        }
        public void DebugPreview(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            ShowDock(true);
            AppItem hit = null;
            Border hitHost = null;
            foreach (KeyValuePair<Border, AppItem> kv in hostItems)
            {
                if (kv.Value != null && kv.Value.Name != null
                    && kv.Value.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = kv.Value;
                    hitHost = kv.Key;
                    break;
                }
            }
            if (hit == null)
            {
                foreach (AppItem it in Items)
                {
                    if (it != null && it.Name != null && it.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (KeyValuePair<Border, AppItem> kv in hostItems)
                        {
                            if (kv.Value == it) { hit = it; hitHost = kv.Key; break; }
                        }
                        break;
                    }
                }
            }
            Program.Log("preview debug: name=" + name + " hit=" + (hit == null ? "(none)" : hit.Name));
            if (hit == null)
            {
        // 只在调试时用：常驻栏里没有这个应用就找一次"正在运行"的应用，
        // 锚点借用第一个图标的位置，验证同一条渲染路径
                foreach (AppItem it in (AllApps == null ? new List<AppItem>() : AllApps))
                {
                    if (it != null && it.Name != null && it.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                        && Launcher.WindowsForItem(it).Count > 0)
                    {
                        hit = it;
                        break;
                    }
                }
                if (hit != null && appHosts.Count > 0) hitHost = appHosts[0];
                Program.Log("preview debug(fallback): hit=" + (hit == null ? "(none)" : hit.Name)
                    + " windows=" + (hit == null ? 0 : Launcher.WindowsForItem(hit).Count));
            }
            if (hit != null) ShowPreviewNow(hitHost, hit);
        }


        private void CycleRunning(int dir)
        {
            try
            {
                List<AppItem> running = new List<AppItem>();
                foreach (AppItem it in Items)
                {
                    if (Launcher.IsRunning(it)) running.Add(it);
                }
                if (running.Count == 0)
                {
                    SetStatus("滚轮：现在没有正在运行的应用");
                    return;
                }
                int cur = -1;
                for (int i = 0; i < running.Count; i++)
                {
                    if (Launcher.IsForegroundApp(running[i])) { cur = i; break; }
                }
                int next = cur < 0 ? (dir > 0 ? 0 : running.Count - 1)
                                   : ((cur + dir) % running.Count + running.Count) % running.Count;
                string msg;
                Launcher.ActivateOrLaunch(running[next], out msg);
                SetStatus(msg);
                Program.Log("wheel: " + msg);
            }
            catch (Exception ex) { Program.Log("wheel failed: " + ex.Message); }
        }

        // 设置窗口改完外观后调用：按新尺寸重排一条栏
        public void ApplySettings()
        {
            try
            {
                ConfigStore.DockSettings c1 = ConfigStore.LoadSettings();
                Glass.BlurDip = c1.FrostRadius;
                Glass.Downscale = c1.FrostDownscale;
                if (shell != null) shell.Height = BarHeight;
                Rebuild();
                if (Drawer != null) Drawer.RefreshIcons();
                Width = double.NaN;
                Height = double.NaN;
                UpdateLayout();
                PositionAtBottom();
                if (expanded) RefreshBackdrop();
                LoadHotCorners();
            LoadWidgetPrefs();
                Program.Log("appearance applied: icon=" + IconSize.ToString("0") + " bar=" + BarHeight.ToString("0") + " tint=" + TintTop.ToString("X") + "/" + TintBottom.ToString("X"));
            }
            catch (Exception ex) { Program.Log("apply settings failed: " + ex.Message); }
        }

        public void ShowDock(bool pin)
        {
            UIFrame.Bench("dock open", 600);   // 采样从 Show 之前开始，覆盖最前面那几帧
            if (pin) pinnedOpen = true;
            lastOverTick = Environment.TickCount;
            // 点开（或热键叫出）之后先给 1.8 秒挪鼠标的时间，否则它会在 900ms 后闪回去
            if (!pin) lastOverTick += 1800;
            PositionAtBottom();
            if (!expanded)
            {
                expanded = true;
                // 按场景给形态：桌面上全尺寸，压在小窗口上时矮一点、半透明、只留常用的几个。
                // 常显模式下不收缩（窗口高度不变，壳缩一半会露出半条空栏）。
                ApplyBarMode(scene == Scene.Windowed);
                // 打开这一帧不许堵。老代码：同步抓屏 + DwmFlush + Sleep(30) ≈ 45–60ms，
                // 用户感知就是"按下去没反应，然后猛地窜出来"。
                // 现在：命中缓存直接贴（≈0ms），只有冷启动才真抓一次（897x96 约 12ms）。
                System.Diagnostics.Stopwatch swOpen = System.Diagnostics.Stopwatch.StartNew();
                bool cacheHit = UseCachedBackdrop();
                if (!cacheHit) CaptureBackdropNow();
                Visibility = Visibility.Visible;
                AnimateIn();
                // 小组件（抓屏 + 读配置）和"运行点"（枚举窗口）加起来 ≈ 200ms，
                // 以前就压在 ShowDock 里，等于按下之后先顿一下再出来。
                // 现在挪到本帧之后：先让 dock 上屏，重活下一拍再做。
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(delegate
                    {
                        if (!expanded) return;
                        if (Widgets != null && ConfigStore.LoadSettings().ShowWidgets) Widgets.ShowAbove(this, false);
                        RefreshDots();
                    }));
                Program.Log("dock show " + swOpen.Elapsed.TotalMilliseconds.ToString("0.0") + "ms cache=" + (cacheHit ? "hit" : "miss"));
                Program.Log("dock shown (pin=" + pin.ToString() + ")");
            }
        }

            // 出场：淡入 + 从下往上轻微滑一段（AirDock 那种"弹出"的手感）
        private void AnimateIn()
        {
            // 200ms 淡入 + 260ms 归位：Apple 的窗口/菜单进场大致就是这一档。
            // 滑动带一点点 BackEase 过冲（≈1.4px），落到位时"沉"一下，不是硬停。
            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            QuadraticEase qe = new QuadraticEase();
            qe.EasingMode = EasingMode.EaseOut;
            fade.EasingFunction = qe;
            BeginAnimation(OpacityProperty, fade);

            TranslateTransform tt = shell.RenderTransform as TranslateTransform;
            if (tt != null)
            {
                DoubleAnimation slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260));
                BackEase ease = new BackEase();
                ease.Amplitude = 0.12;
                ease.EasingMode = EasingMode.EaseOut;
                slide.EasingFunction = ease;
                tt.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }

            // 抓一张"自己没在屏幕上"的背景图，模糊后当玻璃底。抓的时候窗口必须隐藏
        private void RefreshBackdrop()
        {
            if (!glassOn || backdrop == null) return;
            // 只走缓存，绝不在可见时同步抓屏（那等于把自己的图标拍进背景图，还会卡住动画）。
            if (UseCachedBackdrop()) return;
            if (Visibility != Visibility.Visible) PrewarmBackdrop();
        }

        // ---- 背景图缓存：抓屏 12ms（栏）/ 40ms（抽屉），但更贵的是"打开时同步等它" ----
        private System.Windows.Media.Imaging.BitmapSource bdSrc;
        private double bdW = -1, bdH = -1, bdLum = -1;
        private bool bdFetching;

        // 烘好的模糊图：上屏时 img.Effect = null，动画期间不再每帧重算高斯模糊
        private System.Windows.Media.Imaging.BitmapSource bdBaked;
        private object bdBakedFor;
        private double bdBakedBlur = -1;

        private void ApplyBackdropCached(System.Windows.Media.Imaging.BitmapSource src,
            System.Windows.Media.Imaging.BitmapSource baked, double w, double h)
        {
            if (backdrop == null || src == null) return;
            if (baked == null && (bdBaked == null || !object.ReferenceEquals(bdBakedFor, src)
                || Math.Abs(bdBakedBlur - Glass.BlurDip) > 0.01))
            {
                baked = Glass.BakeBlur(src, w);              // 只在换图/改半径时烘一次
                bdBakedBlur = Glass.BlurDip;
            }
            if (baked != null)
            {
                bdBaked = baked;
                bdBakedFor = src;
                Glass.ApplyBaked(backdrop, baked, w, h);
            }
            else Glass.ApplyTo(backdrop, this, src, w, h);   // 烘不出来就退回在线模糊
        }

        // 命中缓存：直接贴，尺寸对不上就当没命中（极少发生：改图标尺寸/栏高）
        private bool UseCachedBackdrop()
        {
            if (backdrop == null || bdSrc == null) return false;
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return false;
            if (Math.Abs(w - bdW) > 0.5 || Math.Abs(h - bdH) > 0.5) return false;
            ApplyBackdropCached(bdSrc, null, w, h);
            return true;
        }

        // 冷启动那一帧：窗口还没上屏，这时候抓最干净（耗时 12ms 左右，一次性）
        private bool CaptureBackdropNow()
        {
            if (!glassOn || backdrop == null) return false;
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return false;
            double sx = Native.DpiScaleX(this), sy = Native.DpiScaleY(this);
            int pxX = (int)Math.Round(Left * sx), pxY = (int)Math.Round(Top * sy);
            int pxW = (int)Math.Round(w * sx), pxH = (int)Math.Round(h * sy);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            System.Windows.Media.Imaging.BitmapSource src = Glass.CapturePx(pxX, pxY, pxW, pxH);
            if (src == null) return false;
            bdSrc = src; bdW = w; bdH = h;
            bdLum = Glass.Luminance(src);
            ApplyBackdropCached(src, null, w, h);
            Program.Log("backdrop capture(cold) " + w.ToString("0") + "x" + h.ToString("0")
                + " in " + sw.ElapsedMilliseconds.ToString() + "ms");
            return true;
        }

        // 隐藏时预热：DwmFlush + 等一帧都挪到后台线程，堵多久都不影响界面。
        // 这样下次打开命中缓存，既是最新背景，又不用等。
        private void PrewarmBackdrop()
        {
            if (!glassOn || backdrop == null || bdFetching) return;
            if (Visibility == Visibility.Visible) return;                  // 上屏时抓 = 拍到自己
            if (Drawer != null && Drawer.IsVisible) return;                // 抽屉盖在上面，同理会拍进去
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;
            double sx = Native.DpiScaleX(this), sy = Native.DpiScaleY(this);
            int pxX = (int)Math.Round(Left * sx), pxY = (int)Math.Round(Top * sy);
            int pxW = (int)Math.Round(w * sx), pxH = (int)Math.Round(h * sy);
            bdFetching = true;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                System.Windows.Media.Imaging.BitmapSource src = null;
                System.Windows.Media.Imaging.BitmapSource baked = null;
                double lum = -1;
                try
                {
                    Native.DwmFlush();
                    System.Threading.Thread.Sleep(12);
                    src = Glass.CapturePx(pxX, pxY, pxW, pxH);
                    lum = Glass.Luminance(src);
                    baked = Glass.BakeBlur(src, w);        // 模糊在线程池里烘，UI 线程零成本
                }
                catch { }
                long ms = sw.ElapsedMilliseconds;
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        bdFetching = false;
                        if (src == null) { Program.Log("backdrop prewarm failed"); return; }
                        bdSrc = src; bdW = w; bdH = h; bdLum = lum;
                        if (expanded && backdrop != null) ApplyBackdropCached(src, baked, w, h);
                        Program.Log("backdrop prewarm " + w.ToString("0") + "x" + h.ToString("0")
                            + " in " + ms.ToString() + "ms (bg)");
                        VerifyTheme();
                    }));
                }
                catch { }
            });
        }

        // ⑤ 主题三元校验：requested（配置里要什么）/ effective（代码实际用了什么）/ onScreen（屏幕上真的长什么样）
        // 三者不一致时明确报出来，避免"滑杆拖了但没生效"这种糊涂账（AirDeck 也这么干）。
        private double lastBackdropLum = -1;

        private void VerifyTheme()
        {
            ConfigStore.DockSettings cfg = ConfigStore.LoadSettings();
            string requested = "tint=" + cfg.TintTop.ToString() + "/" + cfg.TintBottom.ToString()
                + " blur=" + cfg.FrostRadius.ToString("0") + " ds=" + cfg.FrostDownscale.ToString()
                + " darkmode=" + (IsDarkTheme() ? "on" : "off") + " transparency=" + (IsTransparencyOn() ? "on" : "off");
            string effective = "blur=" + Glass.BlurDip.ToString("0") + " ds=" + Glass.Downscale.ToString()
                + " pack=" + (IconPack.Enabled ? "on" : "off");
            double lum = bdLum;
            lastBackdropLum = lum;
            string onScreen = "img=" + (backdrop != null && backdrop.Source != null
                    ? ((System.Windows.Media.Imaging.BitmapSource)backdrop.Source).PixelWidth.ToString() + "x"
                      + ((System.Windows.Media.Imaging.BitmapSource)backdrop.Source).PixelHeight.ToString()
                    : "none")
                + " lum=" + (lum < 0 ? "n/a" : lum.ToString("0.00"));

            bool mismatchBlur = Math.Abs(Glass.BlurDip - cfg.FrostRadius) > 0.5 || Glass.Downscale != cfg.FrostDownscale;
            bool blown = lum >= 0 && (lum > 0.96 || lum < 0.02);
            string tag = (mismatchBlur || blown) ? "MISMATCH " : "";
            Program.Log("theme tri: " + tag + "requested(" + requested + ") effective(" + effective + ") onScreen(" + onScreen + ")");
            if (mismatchBlur) Program.Log("theme tri: blur/downscale 配置与实际不一致，可能 ApplySettings 没跑到");
            if (blown) Program.Log("theme tri: 背景亮度异常（可能抓屏抓到了自己或黑屏）");
        }

        private static double SampleLuminance(System.Windows.Controls.Image img)
        {
            try
            {
                if (img == null) return -1;
                System.Windows.Media.Imaging.BitmapSource bs = img.Source as System.Windows.Media.Imaging.BitmapSource;
                if (bs == null) return -1;
                int pw = bs.PixelWidth, ph = bs.PixelHeight;
                if (pw <= 0 || ph <= 0) return -1;
                System.Windows.Media.Imaging.FormatConvertedBitmap conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(bs, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int stride = conv.PixelWidth * 4;
                byte[] buf = new byte[stride * conv.PixelHeight];
                conv.CopyPixels(buf, stride, 0);
                double sum = 0; int n = 0;
                int stepX = Math.Max(1, conv.PixelWidth / 48);
                int stepY = Math.Max(1, conv.PixelHeight / 16);
                for (int y = 0; y < conv.PixelHeight; y += stepY)
                {
                    int row = y * stride;
                    for (int x = 0; x < conv.PixelWidth; x += stepX)
                    {
                        int i = row + x * 4;
                        double b = buf[i] / 255.0, g = buf[i + 1] / 255.0, r = buf[i + 2] / 255.0;
                        sum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
                        n++;
                    }
                }
                if (n == 0) return -1;
                return sum / n;
            }
            catch { return -1; }
        }

        private static bool IsTransparencyOn()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                    "EnableTransparency", null);
                return v == null || Convert.ToInt32(v) != 0;
            }
            catch { return true; }
        }

        private static bool IsDarkTheme()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                    "AppsUseLightTheme", null);
                return v == null || Convert.ToInt32(v) == 0;
            }
            catch { return true; }
        }

        public void HideDock()
        {
            // 注意：这里不能因为 expanded==false 就直接 return —— 启动时窗口刚 Show 过，
            // expanded 还是 false 但窗口已经可见，早退会导致 dock 一直挂在桌面上"
            bool was = expanded || Visibility == Visibility.Visible;
            expanded = false;
            pinnedOpen = false;
            deskShown = false;
            if (FolderPopup.IsOpen) FolderPopup.CloseCurrent();
            HidePreview();
            // 小组件是独立常驻的，不随栏一起收起
            if (Peek != null && Peek.IsVisible) Peek.Hide();
            PositionAtBottom();
            Visibility = Visibility.Hidden;
            // 趁"已经隐藏、没人等动画"的时刻把下次要用的背景抓在后台：既不挡开合，又是最新画面
            PrewarmBackdrop();
            if (was) Program.Log("dock hidden <- " + (new System.Diagnostics.StackTrace()).GetFrame(1).GetMethod().Name + " force=" + ForceVisible.ToString() + " pin=" + pinnedOpen.ToString());
        }

        public void TogglePinned()
        {
            if (expanded && pinnedOpen)
            {
                pinnedOpen = false;
                HideDock();
            }
            else
            {
                ShowDock(true);
            }
        }

        public void BeginPeek()
        {
            if (peeking) return;
            peeking = true;
            ShowDock(false);
            if (peekTimer != null) peekTimer.Stop();
            peekTimer = new DispatcherTimer();
            peekTimer.Interval = TimeSpan.FromMilliseconds(260);
            peekTimer.Tick += delegate
            {
                bool overDock = IsMouseOver;
                bool overPeek = Peek != null && Peek.IsMouseOver;
                if (overDock || overPeek)
                {
                    lastOverTick = Environment.TickCount;
                    return;
                }
                peekTimer.Stop();
                peeking = false;
            };
            peekTimer.Start();
        }

        private bool peeking;

        public int LastTickAt { get; set; }
        private bool gameMode;
        private DispatcherTimer gameTimer;

        public bool GameModeOn { get { return gameMode; } }

        public bool DebugGame;

            // 某个全屏/高性能程序占着前台时，把整个后台停掉：定时器停、把手收起、抽屉关掉、内存还给系统
        private void EnterGameMode()
        {
            if (gameMode) return;
            gameMode = true;
            try
            {
                if (expanded) HideDock();
                if (Peek != null && Peek.IsVisible) Peek.Hide();
                if (Drawer != null && Drawer.IsVisible) Drawer.HidePanel();
                if (tickTimer != null) tickTimer.Stop();
                if (refreshTimer != null) refreshTimer.Stop();
                Usage.Paused = true;
                try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
                catch { }
                Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
            Program.Log("game mode: ON  (前台是高性能程序，后台挂起)");
            }
            catch (Exception ex) { Program.Log("game mode enter failed: " + ex.Message); }
        }

        private void ExitGameMode()
        {
            if (!gameMode) return;
            gameMode = false;
            try
            {
                Usage.Paused = false;
                try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; }
                catch { }
                if (tickTimer != null) tickTimer.Start();
                if (refreshTimer != null) refreshTimer.Start();
                RestoreStrip(true);
            Program.Log("game mode: OFF (回到普通窗口 / 桌面，恢复)");
            }
            catch (Exception ex) { Program.Log("game mode exit failed: " + ex.Message); }
        }

        // ---- 热角：鼠标怼到屏幕角落停 0.6 秒就触发（AirDeck 的 HotCornerService）----
        public bool debugHot; private int lastHotLog; private bool hotOn;
        private string hotTL = "", hotTR = "", hotBL = "", hotBR = "";
        private string hotFired = "";

        private bool widgetsDesktopOnly = true;

        private IntPtr WidgetsHandle()
        {
            if (Widgets == null) return IntPtr.Zero;
            try { return new WindowInteropHelper(Widgets).Handle; }
            catch { return IntPtr.Zero; }
        }

        public void LoadWidgetPrefs()
        {
            ConfigStore.DockSettings c = ConfigStore.LoadSettings();
            widgetsDesktopOnly = c.WidgetsDesktopOnly;
            deskAutoShow = c.AutoShowOnDesktop;
        }

        public void LoadHotCorners()
        {
            ConfigStore.DockSettings c = ConfigStore.LoadSettings();
            hotOn = c.HotCorners;
            hotTL = c.HotCornerTL == null ? "" : c.HotCornerTL;
            hotTR = c.HotCornerTR == null ? "" : c.HotCornerTR;
            hotBL = c.HotCornerBL == null ? "" : c.HotCornerBL;
            hotBR = c.HotCornerBR == null ? "" : c.HotCornerBR;
            SyncHotHook();
        }

        // 光标落在哪个角落（物理像素）。空串＝不在任何角落。
        private string HitCorner(int x, int y)
        {
            double scale = Native.DpiScaleX(this);
            if (scale <= 0) scale = 1;
            int w = (int)Math.Round(SystemParameters.VirtualScreenWidth * scale);
            int h = (int)Math.Round(SystemParameters.VirtualScreenHeight * scale);
            int tol = (int)Math.Round(10 * scale);
            if (x <= tol && y <= tol) return "TL";
            if (x >= w - tol && y <= tol) return "TR";
            if (x <= tol && y >= h - tol) return "BL";
            if (x >= w - tol && y >= h - tol) return "BR";
            return "";
        }

        // 钩子开关：热角关掉时立刻卸钩，不白占系统资源
        private void SyncHotHook()
        {
            try
            {
                if (hotOn)
                {
                    HotCornerNative.OnMove = OnCursorMoved;
                    bool ok = HotCornerNative.InstallHook();
                    if (!hotHookLogged || ok != HotCornerNative.HookInstalled)
                    {
                        hotHookLogged = true;
                        Program.Log("hot hook: installed=" + ok.ToString() + " (WH_MOUSE_LL, 事件驱动替代 200ms 轮询)");
                    }
                }
                else
                {
                    HotCornerNative.OnMove = null;
                    HotCornerNative.UninstallHook();
                    if (hotFast != null) hotFast.Stop();
                }
            }
            catch (Exception ex) { Program.Log("hot hook sync failed: " + ex.Message); }
        }

        private bool hotHookLogged;

        // 钩子回调：每秒会来几百次，这里只做两件事——记时间、确保评估定时器在跑
        private void OnCursorMoved()
        {
            if (!hotOn || gameMode) return;
            if (hotFast == null)
            {
                hotFast = new DispatcherTimer();
                hotFast.Interval = TimeSpan.FromMilliseconds(80);
                hotFast.Tick += delegate { HotFastTick(); };
            }
            if (!hotFast.IsEnabled) hotFast.Start();
        }

        // 80ms 评估一次：只在"鼠标刚动过"的 700ms 内跑，鼠标一停就停表（空转成本为 0）
        private void HotFastTick()
        {
            if (hotFast == null) return;
            int idle = Environment.TickCount - HotCornerNative.LastMoveAt;
            if (idle > 700 || idle < 0)
            {
                hotFast.Stop();
                hotIn = "";
                hotFired = "";
                return;
            }
            CheckHotCorners();
        }

        private DispatcherTimer hotFast;
        private string hotIn = "";
        private int hotEnterAt;

        private void CheckHotCorners()
        {
            if (!hotOn || gameMode) return;
            int x, y;
            if (HotCornerNative.HookInstalled)
            {
                x = HotCornerNative.CursorX;
                y = HotCornerNative.CursorY;
            }
            else if (!HotCornerNative.GetCursorPos(out x, out y))
            {
                return;
            }
            if (debugHot && Environment.TickCount - lastHotLog > 3000)
            {
                lastHotLog = Environment.TickCount;
                Program.Log("hot dbg: cursor=" + x + "," + y + " moves=" + HotCornerNative.MoveCount.ToString()
                    + " hook=" + HotCornerNative.HookInstalled.ToString() + " tl=" + hotTL
                    + " fired=" + (hotFired == null ? "-" : hotFired));
            }

            string hit = HitCorner(x, y);
            if (hit.Length == 0)
            {
                hotIn = "";
                hotFired = "";
                return;
            }
            if (hit != hotIn)
            {
                hotIn = hit;
                hotEnterAt = Environment.TickCount;
                return;
            }
            if (hotFired == hit) return;
            // 停留判定：250ms。比原来的"3 个 200ms Tick≈600ms"快一半以上，
            // 又不会在鼠标斜穿角落时误触发。
            if (!debugHot && Environment.TickCount - hotEnterAt < hotDwellMs) return;

            hotFired = hit;
            FireHotCorner(hit);
        }

        public const int hotDwellMs = 250;

        // 触发动作（--hot-trigger 调试验证也走这里，保证和真实路径同一条）
        public void FireHotCorner(string hit)
        {
            string action = hit == "TL" ? hotTL : (hit == "TR" ? hotTR : (hit == "BL" ? hotBL : hotBR));
            Program.Log("hot corner: " + hit + " -> " + (action == null || action.Length == 0 ? "(none)" : action));
            if (action == "drawer")
            {
                if (Drawer != null) { Drawer.ShowPanel(this); Drawer.KeepOpenFor(6000); }
            }
            else if (action == "dock")
            {
                ShowDock(true);
            }
        }

        private void CheckGameMode()
        {
            IntPtr mine = new WindowInteropHelper(this).Handle;
            IntPtr drawerHwnd = IntPtr.Zero;
            try
            {
                if (Drawer != null) drawerHwnd = new WindowInteropHelper(Drawer).Handle;
            }
            catch { drawerHwnd = IntPtr.Zero; }

            bool heavy;
            try { heavy = Native.IsHeavyAppForeground(mine, drawerHwnd); }
            catch (Exception ex) { heavy = false; Program.Log("game check failed: " + ex.Message); }
            if (DebugGame)
            {
                Program.Log("game check: heavy=" + heavy.ToString() + " fg=" + Native.ProcessNameOfWindow(Native.GetForegroundWindow()) + " iconic=" + Native.IsIconic(Native.GetForegroundWindow()).ToString() + " games=" + (Native.GameProcessNames == null ? -1 : Native.GameProcessNames.Length).ToString() + " mode=" + gameMode.ToString());
            }

            if (ForceVisible)
            {
                if (gameMode) ExitGameMode();
                return;
            }
            if (heavy) EnterGameMode();
            else ExitGameMode();
        }

        // 看门狗：栏和把手都不在屏幕上、又不是游戏模式、Tick 也停了 —— 强行救回来
        public void SelfHeal()
        {
            try
            {
                if (gameMode) return;
                if (ForceVisible) return;
                // 全屏 / 最大化窗口时 Tick 本来就要把栏和把手全收掉，
                // 这时候再把把手"救"回来，就成了每 15 秒闪一下的死循环。
                if (InFullscreenNow()) return;
                int age = Environment.TickCount - LastTickAt;
                if (age > 4000 || age < 0)
                {
                    if (tickTimer != null) { tickTimer.Stop(); tickTimer.Start(); }
                    if (refreshTimer != null) { refreshTimer.Stop(); refreshTimer.Start(); }
                    Program.Log("self-heal: tick stale " + age.ToString() + "ms, timers restarted");
                    Tick();
                }
                bool dockVisible = Visibility == Visibility.Visible || expanded;
                bool peekVisible = Peek != null && Peek.IsVisible;
                if (!dockVisible && !peekVisible)
                {
                    RestoreStrip(false);
                    Program.Log("self-heal: strip was missing, restored");
                }
                if (Peek != null && Peek.IsVisible) Peek.Place();
            }
            catch (Exception ex) { Program.Log("self-heal failed: " + ex.Message); }
        }

        // 后台巡视：全屏时彻底让位（连把手都不留，只留 Alt+`）；桌面上自动出现；其它情况按需出现
        public void StartWatchers()
        {
            tickTimer = new DispatcherTimer();
            tickTimer.Interval = TimeSpan.FromMilliseconds(350);
            tickTimer.Tick += delegate { Tick(); };
            tickTimer.Start();

            refreshTimer = new DispatcherTimer();
            refreshTimer.Interval = TimeSpan.FromSeconds(4);
            refreshTimer.Tick += delegate { if (expanded) RefreshDots(); };
            refreshTimer.Start();

            // 游戏/高性能监测：这个计时器游戏时也不停，否则回不来
            gameTimer = new DispatcherTimer();
            gameTimer.Interval = TimeSpan.FromMilliseconds(1500);
            gameTimer.Tick += delegate { CheckGameMode(); };
            gameTimer.Start();

        // 空闲回收：收起状态下每 45 秒把不再用到的堆和物理内存还给系统
            trimTimer = new DispatcherTimer();
            trimTimer.Interval = TimeSpan.FromSeconds(45);
            trimTimer.Tick += delegate { TrimMemory(); };
            trimTimer.Start();
        }

        private void TrimMemory()
        {
            if (expanded || gameMode) return;   // 正在用就别动，回收会带来一帧卡顿
            try
            {
                GC.Collect(2, GCCollectionMode.Optimized, false);
                GC.WaitForPendingFinalizers();
                Native.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
                // 别只报工作集：EmptyWorkingSet 之后它必然塌到 1MB，看着像没事，实际私有内存没退。
                // 三个数一起看：ws=物理驻留(可被系统换出) pm=提交(真实占用) heap=托管堆(能靠 GC 退的)
                System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess();
                Program.Log("trim: ws=" + (me.WorkingSet64 / 1048576).ToString()
                    + "MB pm=" + (me.PrivateMemorySize64 / 1048576).ToString()
                    + "MB heap=" + (GC.GetTotalMemory(false) / 1048576).ToString() + "MB");
            }
            catch { }
        }

        // 三种场景，显示策略按场景分流，不再让一堆布尔量互相打架：
        //   Fullscreen 全屏应用/游戏：dock、把手、小组件全收，一点不挡
        //   Desktop    桌面（含 AirDeck 这类盖住桌面的壳）：dock 自动亮出来 + 小组件
        //   Windowed   普通窗口：dock 收起，只留底部那条 8px 把手，鼠标/热键再叫出
        private enum Scene { Fullscreen = 0, Desktop = 1, Windowed = 2 }
        private Scene scene = Scene.Windowed;

        private void SetScene(Scene s)
        {
            if (s == scene) return;
            Program.Log("scene: " + scene + " -> " + s);
            scene = s;
            // 场景一换，栏的形态跟着换：桌面全尺寸，离开桌面收成小窗形态。
            if (expanded) ApplyBarMode(s == Scene.Windowed);
        }

        // ---- 唯一模式裁决 ----
        // 这一拍"我处于什么模式"只在这里判一次，Tick 里所有分支都消费它的结果。
        // 以前 fullscreen / deskBase / desktopNow 在 Tick 里各自调一遍 Native.IsXxxForeground，
        // 加一个场景要同时改三处，漏一处就是"dock 该出现的地方不出现"这类 bug。
        // 优先级（照 TranslucentTB 的 GetConfig 阶梯）：
        //   1 独占全屏（且没被 ForceVisible 压住）        → Fullscreen
        //   2 桌面 / 桌面壳 / 自己人在前台，或已在桌面显示时 shell 抢了一下前台 → Desktop
        //   3 其余                                          → Windowed
        // gameMode 故意不进来：它不是"这一拍是什么"，而是"要不要把整个后台停掉"的锁存态，
        // 带停定时器 / 降优先级 / 还内存这些重副作用，由 CheckGameMode 自己那 1500ms 定时器管。
        private struct ModeDecision
        {
            public Scene Scene;
            public bool Fullscreen;   // 真全屏，且未被 ForceVisible 压住
            public bool Desktop;      // 桌面场景（含"已在桌面时 shell 抢前台"的豁免）
            public bool Covered;      // 前台是最大化窗口（浏览器等）：栏同样要让开
        }

        private ModeDecision ResolveMode(IntPtr mine, IntPtr drawerHwnd)
        {
            ModeDecision d = new ModeDecision();
            d.Scene = Scene.Windowed;

            bool fullscreen = Native.IsFullscreenAppForeground(mine, drawerHwnd);
            // 兜底：只要桌面 / 任务栏 / 开始菜单回到前台，就绝不认为还在全屏，
            // 否则某些游戏 / 视频退出后窗口类仍是全屏窗口，"永远不回来"。
            if (fullscreen && (Native.IsDesktopForeground() || Native.IsShellForeground())) fullscreen = false;
            if (fullscreen && !ForceVisible)
            {
                d.Fullscreen = true;
                d.Scene = Scene.Fullscreen;
                return d;
            }

            // 最大化窗口（浏览器占满工作区）也算"让开"：它不是全屏，但栏压上去一样挡内容。
            // 用户 22:35 的图一/图二就是这种状态——浏览器最大化时不要显示栏。
            d.Covered = !ForceVisible && Native.IsForegroundMaximized(mine, drawerHwnd);

            IntPtr selfH = new WindowInteropHelper(this).Handle;
            IntPtr peekH = Peek != null ? new WindowInteropHelper(Peek).Handle : IntPtr.Zero;
            IntPtr drawerH = Drawer != null ? new WindowInteropHelper(Drawer).Handle : IntPtr.Zero;
            // 桌面上这几个"自己人"（dock / 把手 / 抽屉 / 小组件）当前台时也算还在桌面：
            // dock 一显示就把前台从 Progman 抢走（实测 18:33:23 fg=DesktopDock），
            // 不加这层豁免，下一拍就判成"离开桌面"，dock 自己把自己藏了。
            bool deskBase = deskAutoShow && !ForceVisible && !gameMode &&
                            (Native.IsDesktopForeground(selfH) || Native.IsDesktopForeground(peekH) ||
                             Native.IsDesktopForeground(drawerH) || Native.IsDesktopForeground(WidgetsHandle()));
            // 已经在桌面显示中时，任务栏 / 通知区 / Alt-Tab 过渡窗抢一下前台不算离开桌面
            d.Desktop = deskBase ||
                        (deskShown && deskAutoShow && !ForceVisible && !gameMode && Native.IsShellForeground());
            if (d.Desktop) d.Scene = Scene.Desktop;
            return d;
        }

        // ---- 小窗形态（Windowed 场景被叫出时用）----
        // 桌面场景：全尺寸 + 全图标；小窗场景：栏压矮、半透明、只留最常用的 6 个。
        // 注意：现在按"使用次数"排序取前 6（配置里只存了次数，没有时间戳）。
        private const int CompactIconCount = 6;
        private bool compactBar;

        private void ApplyBarMode(bool compact)
        {
            if (shell == null) return;
            compactBar = compact;
            try
            {
                // 只改透明度和图标数量，绝不缩壳的高度：窗口是按 BarHeight 定高的，
                // 壳缩到 0.72 而窗口没缩，就会露出半条空栏、图标被切掉一半。
                shell.Height = BarHeight;
                shell.Opacity = compact ? 0.88 : 1.0;
                if (bar != null && Items != null)
                {
                    System.Collections.Generic.HashSet<string> keep = compact ? TopUsedNames(CompactIconCount) : null;
                    foreach (object child in bar.Children)
                    {
                        Border b = child as Border;
                        if (b == null) continue;
                        AppItem it;
                        bool show = true;
                        if (keep != null && hostItems.TryGetValue(b, out it))
                            show = it != null && keep.Contains(it.Name);
                        b.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                // 高度变了要重摆位置，但要等排版这一拍过去再量 ActualHeight
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(delegate { if (expanded) PositionAtBottom(); }));
                Program.Log("bar mode: " + (compact ? "compact(" + CompactIconCount + ")" : "full"));
            }
            catch (Exception ex) { Program.Log("bar mode failed: " + ex.Message); }
        }

        private System.Collections.Generic.HashSet<string> TopUsedNames(int n)
        {
            System.Collections.Generic.HashSet<string> names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Items == null) return names;
            System.Collections.Generic.Dictionary<string, int> usage = ConfigStore.LoadUsage();
            List<AppItem> copy = new List<AppItem>(Items);
            copy.Sort(delegate(AppItem a, AppItem b)
            {
                // 先钉住的，再按使用次数；次数一样按名字排，保证每次叫出来是同一批
                if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
                int ua = 0, ub = 0;
                if (usage != null)
                {
                    usage.TryGetValue(a.Name, out ua);
                    usage.TryGetValue(b.Name, out ub);
                }
                int c = ub.CompareTo(ua);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < copy.Count && names.Count < n; i++) names.Add(copy[i].Name);
            return names;
        }

        // 抽屉 / 设置 / 小组件被点开时前台会变成我们自己的窗口，这时候仍按"在桌面"算，
        // 否则任务栏会在抽屉弹出来的同时冒出来。
        private static bool IsOwnForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        // 在桌面 -> 任务栏自动隐藏；离开桌面（浏览器、编辑器、全屏）-> 任务栏常显。
        // 用 ABM_SETSTATE 现改，不重启 explorer；状态有变化才动，且要连续两拍才认，防焦点抖动。
        private void UpdateTaskbarAutoHide()
        {
            IntPtr mine = new WindowInteropHelper(this).Handle;
            // 故意不算 IsShellForeground()：那是"前台是任务栏/开始菜单"。
            // 用户在应用里点任务栏切窗口时，前台会短暂变成 Shell_TrayWnd；
            // 若把它当桌面，任务栏会在手指底下当场收起来。
            bool onDesktop = Native.IsDesktopForeground(mine) || IsOwnForeground();

            if (!autoHideReady)
            {
                autoHideReady = true;
                // 不读注册表：那是"上一次"的快照，可能已经被我们自己改过。
                // 直接以活状态为起点，下一拍按前台重新裁决。
                autoHideApplied = Native.TaskbarAutoHide();
                Program.Log("taskbar: autohide initial=" + autoHideApplied);
            }

            if (onDesktop == autoHideApplied)
            {
                autoHideDebounce = 0;
                return;
            }
            if (++autoHideDebounce < 2) return;     // 约 700ms 确认
            autoHideDebounce = 0;

            if (Native.TaskbarAutoHide() == onDesktop)
            {
                autoHideApplied = onDesktop;        // 别的地方已经改好了，不重复动手
                return;
            }
            if (Native.SetTaskbarAutoHide(onDesktop))
            {
                autoHideApplied = onDesktop;
                // 只改活着的状态，不写注册表：注册表留着用户自己的偏好，退出时照它还原。
                Program.Log("taskbar: autohide=" + (onDesktop ? "on" : "off") +
                            " (foreground " + (onDesktop ? "desktop" : "app") + ")");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                // 退出时别把任务栏留在"常显"：用户要的是桌面下自动隐藏。
                if (autoHideReady && !autoHideApplied)
                {
                    Native.SetTaskbarAutoHide(true);
                    Program.Log("taskbar: autohide restored=True");
                }
            }
            catch { }
            base.OnClosed(e);
        }

        private void Tick()
        {
            IntPtr mine = new WindowInteropHelper(this).Handle;
            IntPtr drawerHwnd = IntPtr.Zero;
            try
            {
                if (Drawer != null) drawerHwnd = new WindowInteropHelper(Drawer).Handle;
            }
            catch { drawerHwnd = IntPtr.Zero; }

            LastTickAt = Environment.TickCount;
            // 这一拍算什么模式，只问 ResolveMode 一次（见"唯一模式裁决"）
            ModeDecision mode = ResolveMode(mine, drawerHwnd);
            bool fullscreen = mode.Fullscreen || mode.Covered;
            SetScene(mode.Scene);
            UpdateTaskbarAutoHide();
            // 桌面 / 任务栏 / 开始菜单回到前台就降级的兜底，已经挪进 ResolveMode，这里不再重算
            // 场景分流第一段：全屏优先，下面对应"全收"策略
            // 热角已改成鼠标钩子事件驱动（见 OnCursorMoved/HotFastTick）；
            // 只有钩子装不上时才退回老的 Tick 轮询路径。
            if (!HotCornerNative.HookInstalled) CheckHotCorners();

            if (fullscreen && !ForceVisible)
            {
                if (expanded) HideDock();
                if (Peek != null && Peek.IsVisible) Peek.Hide();
                if (Widgets != null && Widgets.Showing) Widgets.HidePanel();
                return;
            }

            // 小组件：默认只在桌面/栏展开/鼠标压在上面时显示，不再压住其它应用窗口。
            if (Widgets != null)
            {
                bool wantWidget = WidgetWanted;
                if (wantWidget && widgetsDesktopOnly)
                {
                    bool deskNow = Native.IsDesktopForeground(WidgetsHandle());
                    if (!deskNow && !expanded && !Widgets.IsMouseOver) wantWidget = false;
                }
                if (wantWidget && !Widgets.Showing) Widgets.ShowAbove(this, false);
                else if (!wantWidget && Widgets.Showing) Widgets.HidePanel();
            }

            bool overDock = IsMouseOver;
            bool overPeek = Peek != null && Peek.IsVisible && Peek.IsMouseOver;
            bool drawerOpen = Drawer != null && Drawer.IsVisible;
            bool overDrawer = drawerOpen && Drawer.IsMouseOver;

            if ((overDock || overPeek || overDrawer) && Environment.TickCount > lastOverTick) lastOverTick = Environment.TickCount;

            // 回到桌面自动出现：取"别处 -> 桌面"的上升沿，只在那一拍亮一次。
            // 不能每拍都判断 —— 那样用户手动收起后 350ms 又自己弹回来，正是当初判成误触的原因；
            // 亮出来之后由 deskShown 豁免下面的 900ms 自动收起，否则鼠标一停就被收走，等于没显示。
            // 收起要防抖：桌面、自动隐藏任务栏、AirDeck 这类桌面层会来回抢前台，
            // 一拍的非桌面就让 dock 闪一下再弹回来（实测 18:28:20 亮 → 18:28:21 藏 → 18:28:22 又亮）。
            // 桌面判定（含"自己人在前台"的豁免、shell 抢前台的豁免）已经在 ResolveMode 里做完，
            // 这里只消费结果，不再自己重判一遍。
            bool desktopNow = mode.Desktop;
            if (desktopNow) deskAwayTicks = 0;
            if (desktopNow && !deskWasFg)
            {
                deskWasFg = true;
                deskAwayTicks = 0;
                if (!expanded && !pinnedOpen)
                {
                    ShowDock(false);
                    deskShown = true;
                    Program.Log("desk auto-show");
                }
            }
            else if (!desktopNow && deskWasFg)
            {
                deskWasFg = false;
            }

            // 离开桌面后要连着几拍都不是桌面、且指针不在 dock/抽屉上，才真收起来
            if (deskShown && !desktopNow && !pinnedOpen && !overDock && !overDrawer && !drawerOpen)
            {
                deskAwayTicks++;
                if (deskAwayTicks >= 5)   // 5 × 350ms ≈ 1.8s
                {
                    deskShown = false;
                    deskAwayTicks = 0;
                    HideDock();
                    Program.Log("desk auto-hide (left desktop)");
                }
            }

            if (expanded && !ForceVisible && !pinnedOpen && !deskShown && !overDock && !overPeek && !overDrawer && !drawerOpen)
            {
                if (Environment.TickCount - lastOverTick > 900) HideDock();
            }

            // 把手：只在收起时露一条，全屏时完全不�?
            if (Peek != null)
            {
                if (!expanded)
                {
                    if (!Peek.IsVisible)
                    {
                        Peek.Show();
                        Peek.Place();
                    }
                }
                else if (Peek.IsVisible)
                {
                    Peek.Hide();
                }
            }
        }

        // 把手（任务栏上方细条）重新露出来。forceLog=true 时写日志，方便排�?叫不出来"�?
        // 当前是不是"真全屏且没被 ForceVisible 压住"——和 ResolveMode 里那段判据同源。
        private bool InFullscreenNow()
        {
            IntPtr mine = new WindowInteropHelper(this).Handle;
            IntPtr drawerH = Drawer != null ? new WindowInteropHelper(Drawer).Handle : IntPtr.Zero;
            if (ForceVisible) return false;
            // 最大化窗口也算：这种状态下把手一样会压在页面底部
            return Native.IsFullscreenAppForeground(mine, drawerH)
                || Native.IsForegroundMaximized(mine, drawerH);
        }

        public void RestoreStrip(bool forceLog)
        {
            try
            {
                if (Peek == null) return;
                // 全屏里一律不露：这条路径由托盘看门狗驱动，不经过 Tick 的全屏早退，
                // 以前就是把 8px 细条顶上全屏页面的元凶。
                if (gameMode || InFullscreenNow())
                {
                    if (Peek.IsVisible) Peek.Hide();
                    return;
                }
                PositionAtBottom();
                Peek.Place();
                if (!Peek.IsVisible) Peek.Show();
                if (forceLog) Program.Log("strip restored");
            }
            catch (Exception ex) { Program.Log("restore strip failed: " + ex.Message); }
        }

        public void StopTimers()
        {
            if (refreshTimer != null) refreshTimer.Stop();
            if (tickTimer != null) tickTimer.Stop();
            if (peekTimer != null) peekTimer.Stop();
        }

        private void RefreshDots()
        {
            if (bar == null || Items == null) return;
            // 一次枚举拿到所有在跑进程名，再逐图标比对。老写法每个图标都全量扫窗口，
            // 15 个图标 ≈ 200ms，正是 ShowDock 里那段"卡一下"的大头。
            System.Collections.Generic.HashSet<string> procs = Native.VisibleWindowProcessNames();
            string running = "";
            foreach (object child in bar.Children)
            {
                Border b = child as Border;
                if (b == null) continue;
                System.Windows.Shapes.Ellipse dot = b.Tag as System.Windows.Shapes.Ellipse;
                if (dot == null) continue;
                string tip = b.ToolTip as string;
                if (tip == null) { dot.Visibility = Visibility.Collapsed; continue; }
                AppItem item = FindItemByTip(tip);
                bool on = item != null && Launcher.IsRunning(item, procs);
                dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) running += tip + "|";
            }
            if (running != lastRunning)
            {
                lastRunning = running;
                Program.Log("running: " + (running.Length == 0 ? "(none)" : running));
            }
        }

        private string lastRunning;

        private AppItem FindItemByTip(string tip)
        {
            if (Items == null) return null;
            foreach (AppItem it in Items)
            {
                if (it.Name == tip) return it;
            }
            return null;
        }
    }
}



















