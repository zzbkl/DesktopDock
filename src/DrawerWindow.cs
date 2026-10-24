using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopDock
{
    internal class DrawerWindow : Window
    {
        private const double PanelWidth = 900;
        private const double PanelHeight = 620;
        private const double TileSize = 108;

        private TextBox searchBox;
        private WrapPanel tiles;
        private ScrollViewer scroller;
        private TextBlock footer;
        private TextBlock watermark;
        private Image backdrop;
        private Border editBtn;
        private TextBlock editLabel;
        private bool editMode;
        private WrapPanel tabsHost;
        private Dictionary<string, Border> tabBorders = new Dictionary<string, Border>();
        private Dictionary<string, TextBlock> tabLabels = new Dictionary<string, TextBlock>();
        private string activeCat = "全部";
        private Dictionary<string, string> catOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private List<AppItem> all = new List<AppItem>();
        private HashSet<string> hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool built;
        // 内容/顺序变了才重建 tile（否则每次打开都白重建一遍）
        private bool tileDirty = true;
        private string lastBuiltQuery = "";
        private long lastBackdropAt = -100000;
        private Dictionary<string, Border> tileCache = new Dictionary<string, Border>();
        private bool grace;
        private int graceUntil;

        // 测试/截图用：置 true 后失焦也不自动收起（--drawer 启动时打开）
        public bool PinOpen { get; set; }

        // 测试/截图用：打开就进整理模式（--edit）
        public bool StartInEdit { get; set; }

        public DrawerWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = false;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = PanelWidth;
            Height = PanelHeight;
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A));
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            CreateContent();
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    HidePanel();
                }
            };
            Deactivated += delegate
            {
                if (grace || PinOpen) return;
                // 鼠标还在抽屉上，或刚打开不到 1.2 秒：不要因为"后台程序抢回焦点"就关掉
                if (IsMouseOver) return;
                if (Environment.TickCount < graceUntil) return;
                HidePanel();
            };
            IsVisibleChanged += delegate(object s, DependencyPropertyChangedEventArgs e)
            {
                if ((bool)e.NewValue && searchBox != null)
                {
                    searchBox.Focus();
                    searchBox.SelectAll();
                }
            };
        }

        private void CreateContent()
        {
            Grid root = new Grid();
            root.Margin = new Thickness(14);

            RowDefinition r0 = new RowDefinition();
            r0.Height = GridLength.Auto;
            RowDefinition rTab = new RowDefinition();
            rTab.Height = GridLength.Auto;
            RowDefinition r1 = new RowDefinition();
            r1.Height = new GridLength(1, GridUnitType.Star);
            RowDefinition r2 = new RowDefinition();
            r2.Height = GridLength.Auto;
            root.RowDefinitions.Add(r0);
            root.RowDefinitions.Add(rTab);
            root.RowDefinitions.Add(r1);
            root.RowDefinitions.Add(r2);

            searchBox = new TextBox();
            searchBox.FontSize = 15;
            searchBox.Padding = new Thickness(12, 8, 12, 8);
            searchBox.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
            searchBox.Foreground = Brushes.White;
            searchBox.BorderThickness = new Thickness(0);
            searchBox.CaretBrush = Brushes.White;
            searchBox.ToolTip = "输入即过滤，Esc 收起";
            searchBox.VerticalContentAlignment = VerticalAlignment.Center;

            watermark = new TextBlock();
            watermark.Text = "搜索应用名或拼音首字母，如 wx = 微信…";
            watermark.FontSize = 15;
            watermark.Foreground = new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF));
            watermark.Margin = new Thickness(15, 0, 0, 0);
            watermark.VerticalAlignment = VerticalAlignment.Center;
            watermark.IsHitTestVisible = false;

            searchBox.TextChanged += delegate
            {
                if (watermark != null)
                {
                    watermark.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                }
                RebuildTiles(searchBox.Text);
            };

            Border searchShell = new Border();
            searchShell.CornerRadius = new CornerRadius(10);
            searchShell.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
            searchShell.Child = searchBox;

            Grid searchHost = new Grid();
            searchHost.Margin = new Thickness(0, 0, 0, 14);
            ColumnDefinition c0 = new ColumnDefinition();
            c0.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition c1 = new ColumnDefinition();
            c1.Width = GridLength.Auto;
            searchHost.ColumnDefinitions.Add(c0);
            searchHost.ColumnDefinitions.Add(c1);
            searchHost.Children.Add(searchShell);
            searchHost.Children.Add(watermark);

            // 添加应用：自己挑 exe / 快捷方式塞进抽屉（扫描器认不出来的程序走这里）
            Border addBtn = new Border();
            addBtn.CornerRadius = new CornerRadius(10);
            addBtn.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            addBtn.BorderThickness = new Thickness(1);
            addBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            addBtn.Padding = new Thickness(12, 8, 12, 8);
            addBtn.Cursor = Cursors.Hand;
            addBtn.VerticalAlignment = VerticalAlignment.Stretch;
            addBtn.ToolTip = "添加应用：选 exe 或快捷方式，加进抽屉";
            TextBlock addLabel = new TextBlock();
            addLabel.Text = "＋ 添加";
            addLabel.FontSize = 13;
            addLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
            addLabel.VerticalAlignment = VerticalAlignment.Center;
            addBtn.Child = addLabel;
            addBtn.MouseLeftButtonUp += delegate { AddAppsFromDialog(); };

            // 整理模式开关：点开以后，点图标=把它从抽屉里移出去，不用一个个右键
            editBtn = new Border();
            editBtn.CornerRadius = new CornerRadius(10);
            editBtn.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            editBtn.BorderThickness = new Thickness(1);
            editBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            editBtn.Padding = new Thickness(14, 8, 14, 8);
            editBtn.Margin = new Thickness(8, 0, 0, 0);
            editBtn.Cursor = Cursors.Hand;
            editBtn.VerticalAlignment = VerticalAlignment.Stretch;
            editLabel = new TextBlock();
            editLabel.Text = "整理";
            editLabel.FontSize = 13;
            editLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
            editLabel.VerticalAlignment = VerticalAlignment.Center;
            editBtn.Child = editLabel;
            editBtn.MouseLeftButtonUp += delegate { ToggleEditMode(); };

            StackPanel btnRow = new StackPanel();
            btnRow.Orientation = Orientation.Horizontal;
            btnRow.Margin = new Thickness(10, 0, 0, 0);
            btnRow.VerticalAlignment = VerticalAlignment.Stretch;
            btnRow.Children.Add(addBtn);
            btnRow.Children.Add(editBtn);
            Grid.SetColumn(btnRow, 1);
            searchHost.Children.Add(btnRow);

            Grid.SetRow(searchHost, 0);
            root.Children.Add(searchHost);

            // 分类页签：全部 / 常用 / 办公 / 开发 / 游戏 / 工控 / 其他
            tabsHost = new WrapPanel();
            tabsHost.Orientation = Orientation.Horizontal;
            tabsHost.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(tabsHost, 1);
            root.Children.Add(tabsHost);
            BuildTabs();

            tiles = new WrapPanel();
            tiles.Orientation = Orientation.Horizontal;
            tiles.Margin = new Thickness(0, 0, 0, 0);

            scroller = new ScrollViewer();
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroller.Content = tiles;
            scroller.PanningMode = PanningMode.VerticalOnly;
            // 分页懒加载的触发点：滚到快到底时再建下一批（预取早于看见）
            scroller.ScrollChanged += delegate
            {
                if (pendingItems == null || pendingItems.Count == 0) return;
                // 只认"用户真的往下滚了"：如果 VerticalOffset 还是 0，这条事件是布局长高触发的
                // （会把整抽屉一次性填满，等于没懒加载）。
                if (scroller.VerticalOffset <= 1) return;
                if (scroller.VerticalOffset + scroller.ViewportHeight > scroller.ExtentHeight - 260)
                {
                    FillNextChunk();
                }
            };
            Grid.SetRow(scroller, 2);
            root.Children.Add(scroller);

            footer = new TextBlock();
            footer.FontSize = 11;
            footer.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            footer.Margin = new Thickness(2, 8, 0, 0);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Border shell = new Border();
            shell.CornerRadius = new CornerRadius(20);
            LinearGradientBrush dg = new LinearGradientBrush();
            dg.StartPoint = new Point(0, 0);
            dg.EndPoint = new Point(0, 1);
            dg.GradientStops.Add(new GradientStop(Color.FromArgb(0x84, 0x22, 0x23, 0x2B), 0));
            dg.GradientStops.Add(new GradientStop(Color.FromArgb(0xA8, 0x14, 0x15, 0x1A), 1));
            shell.Background = dg;
            shell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
            shell.BorderThickness = new Thickness(1);
            shell.Child = root;
            shell.Margin = new Thickness(0);

            backdrop = new Image();
            backdrop.Stretch = Stretch.Fill;
            backdrop.IsHitTestVisible = false;

            Grid layer = new Grid();
            layer.Children.Add(backdrop);
            layer.Children.Add(shell);
            Content = layer;
        }

        // 抓一张自己不在屏幕上的背景图，模糊后当玻璃底
        public void RefreshBackdrop()
        {
            if (backdrop == null) return;
            // 缓存优先：抓屏 40–50ms，是"点开要等一下"的主要来源，能不抓就不抓
            if (UseCachedBackdrop()) return;
            if (Visibility == Visibility.Visible) return;   // 可见时抓会把图标拍进背景图
            CaptureBackdropNow();
        }

        // ---- 背景图缓存（和 DockWindow 同一套思路：隐藏时后台预热，打开时零等待）----
        private System.Windows.Media.Imaging.BitmapSource bdSrc;
        private double bdW = -1, bdH = -1, bdLum = -1;
        private bool bdFetching;

        // 烘好的模糊图：抽屉打开动画期间不再每帧重算高斯模糊（这是首开掉帧的主因）
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
                baked = Glass.BakeBlur(src, w);
                bdBakedBlur = Glass.BlurDip;
            }
            if (baked != null)
            {
                bdBaked = baked;
                bdBakedFor = src;
                Glass.ApplyBaked(backdrop, baked, w, h);
            }
            else Glass.ApplyTo(backdrop, this, src, w, h);
        }

        private double PanelWd { get { return Width > 0 ? Width : PanelWidth; } }
        private double PanelHd { get { return Height > 0 ? Height : PanelHeight; } }

        private bool UseCachedBackdrop()
        {
            if (backdrop == null || bdSrc == null) return false;
            double w = PanelWd, h = PanelHd;
            if (Math.Abs(w - bdW) > 0.5 || Math.Abs(h - bdH) > 0.5) return false;
            ApplyBackdropCached(bdSrc, null, w, h);
            return true;
        }

        private bool CaptureBackdropNow()
        {
            if (backdrop == null) return false;
            double wd = PanelWd, hd = PanelHd;
            if (wd < 4 || hd < 4) return false;
            double sx = Native.DpiScaleX(this), sy = Native.DpiScaleY(this);
            int pxX = (int)Math.Round(Left * sx), pxY = (int)Math.Round(Top * sy);
            int pxW = (int)Math.Round(wd * sx), pxH = (int)Math.Round(hd * sy);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            System.Windows.Media.Imaging.BitmapSource src = Glass.CapturePx(pxX, pxY, pxW, pxH);
            if (src == null) return false;
            lastBackdropAt = Environment.TickCount;
            bdSrc = src; bdW = wd; bdH = hd;
            bdLum = Glass.Luminance(src);
            ApplyBackdropCached(src, null, wd, hd);
            Program.Log("drawer backdrop capture " + wd.ToString("0") + "x" + hd.ToString("0")
                + " in " + sw.ElapsedMilliseconds.ToString() + "ms");
            return true;
        }

        // 收起之后在后台抓：DwmFlush + 等一帧都在线程池里，谁都不用等
        private void PrewarmBackdrop()
        {
            if (backdrop == null || bdFetching) return;
            if (Visibility == Visibility.Visible) return;
            double wd = PanelWd, hd = PanelHd;
            if (wd < 4 || hd < 4) return;
            double sx = Native.DpiScaleX(this), sy = Native.DpiScaleY(this);
            int pxX = (int)Math.Round(Left * sx), pxY = (int)Math.Round(Top * sy);
            int pxW = (int)Math.Round(wd * sx), pxH = (int)Math.Round(hd * sy);
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
                    baked = Glass.BakeBlur(src, wd);       // 后台烘模糊，打开时直接贴
                }
                catch { }
                long ms = sw.ElapsedMilliseconds;
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        bdFetching = false;
                        if (src == null) return;
                        bdSrc = src; bdW = wd; bdH = hd; bdLum = lum;
                        lastBackdropAt = Environment.TickCount;
                        if (IsVisible && backdrop != null) ApplyBackdropCached(src, baked, wd, hd);
                        Program.Log("drawer backdrop prewarm " + wd.ToString("0") + "x" + hd.ToString("0")
                            + " in " + ms.ToString() + "ms (bg)");
                    }));
                }
                catch { }
            });
        }

        // 手动添加：选 exe / 快捷方式，写进 items.json 后立刻出现在抽屉里
        private void AddAppsFromDialog()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "添加应用到抽屉";
            dlg.Filter = "程序或快捷方式|*.exe;*.lnk|所有文件|*.*";
            dlg.Multiselect = true;
            bool ok = false;
            try { ok = dlg.ShowDialog(this) == true; }
            catch (Exception ex) { Program.Log("add dialog failed: " + ex.Message); return; }
            if (!ok || dlg.FileNames == null || dlg.FileNames.Length == 0) return;
            AddAppFiles(dlg.FileNames);
        }

        // 供界面对话框和 --add 命令行共用
        public int AddAppFiles(string[] files)
        {
            if (files == null || files.Length == 0) return 0;
            int added = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;

                AppItem item = new AppItem();
                string baseName = System.IO.Path.GetFileNameWithoutExtension(path);
                item.Name = baseName;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    item.Shortcut = path;
                    string t;
                    string a;
                    Shortcuts.Resolve(path, out t, out a);
                    item.Target = string.IsNullOrEmpty(t) ? path : t;
                    item.Arguments = a == null ? "" : a;
                }
                else
                {
                    item.Target = path;
                    item.Shortcut = "";
                    item.Arguments = "";
                }

                bool dup = false;
                for (int j = 0; j < all.Count; j++)
                {
                    if (all[j] == null) continue;
                    if (string.Equals(all[j].Name, item.Name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (dup) continue;

                all.Add(item);
                added++;
            }

            if (added > 0)
            {
                ConfigStore.Save(all);
                // 加进来的东西默认不该是隐藏状态
                if (hidden != null && hidden.Count > 0)
                {
                    List<string> h = new List<string>(hidden);
                    hidden = new HashSet<string>(h, StringComparer.OrdinalIgnoreCase);
                }
                Program.Log("drawer: added " + added.ToString() + " app(s), total=" + all.Count.ToString());
                built = false;
                SetApps(all);
                if (IsVisible) RebuildTiles(searchBox == null ? "" : searchBox.Text);
            }
            return added;
        }

        // 调试用：预填搜索框（验证拼音首字母）
        public void SetSearchText(string kw)
        {
            if (searchBox == null) return;
            searchBox.Text = kw == null ? "" : kw;
            searchBox.CaretIndex = searchBox.Text.Length;
            RebuildTiles(searchBox.Text);
        }

        public void SetApps(List<AppItem> apps)
        {
            all = apps == null ? new List<AppItem>() : apps;
            hidden = new HashSet<string>(ConfigStore.LoadHidden(), StringComparer.OrdinalIgnoreCase);
            catOverrides = ConfigStore.LoadCategories();
            all.Sort(delegate(AppItem x, AppItem y)
            {
                string a = x == null || x.Name == null ? "" : x.Name;
                string b = y == null || y.Name == null ? "" : y.Name;
                // 点得多的排前面；次数一样（含都没点过）就按名字排
                int ua = ConfigStore.UsageCount(a);
                int ub = ConfigStore.UsageCount(b);
                if (ua != ub) return ub.CompareTo(ua);
                return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            });
            built = false;
            tileDirty = true;
            tileCache.Clear();   // 应用列表变了，缓存的 tile 全部作废
        }

        // 换了图标包/图标设置后调用：清掉缓存的图标位并重建
        public void RefreshIcons()
        {
            tileCache.Clear();
            built = false;
            tileDirty = true;
            if (IsVisible) RebuildTiles(searchBox == null ? "" : searchBox.Text);
        }

        // 启动后空闲时先把图标位建好，第一次点开就不用等 450ms
        public void Prebuild()
        {
            if (built) return;
            built = true;
            RebuildTiles("");
            Program.Log("drawer: prebuilt " + tiles.Children.Count.ToString() + " tiles");
        }

        // 首次栅格化最贵：43 个 tile 第一次上屏实测只有 36fps（冷字形/冷图标/新建合成层）。
        // 启动后在屏幕外做一次"假开屏"，把该建的都建好；用户第一次点开就是热态。
        // 只做一次、不抢焦点、不上屏（-32000 是 Windows 约定的屏外坐标）。
        public void Warmup(DockWindow dock, System.Action done)
        {
            if (IsVisible || !built) { if (done != null) { try { done(); } catch { } } return; }
            bool sa = ShowActivated;
            double oldO = Opacity;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            ShowActivated = false;
            // 首开掉到 30Hz 的根因：这扇窗"第一次在屏幕上被合成"的那一段，会被压到 ~30ms/帧、
            // 持续约 500ms（实测 CPU 只占 4%，纯粹在等合成器）。所有"取巧"的取消方式都试过、都无效：
            //   · WS_EX_TRANSPARENT 真实点到穿 → 53fps
            //   · Topmost=false（垫到别的窗口后面）→ 54fps
            //   · 挪到 -32000 屏外 → 54fps
            // 只有"真的在最终位置上、前置、可见地被合成一遍"才算数（71fps）。所以只能真露一次脸：
            // 3% 不透明度看不见，持续 700ms 把那段成本先付掉。
            PositionAbove(dock);
            Opacity = 0.03;
            UIElement hold = Content as UIElement;
            bool oldHit = hold == null ? true : hold.IsHitTestVisible;
            if (hold != null) hold.IsHitTestVisible = false;   // 预热期间点不到任何图标，避免误启动程序
            try
            {
                Show();
                UpdateLayout();
            }
            catch (Exception ex) { Program.Log("drawer warmup failed: " + ex.Message); }
            // 必须让它真的呈现一段时间再收：上一版 Show 完立刻 Hide，一帧都没合成过，等于没预热。
            // 700ms 覆盖实测的慢段长度（首次合成后约 500ms 内在 30Hz）。
            System.Windows.Threading.DispatcherTimer close = new System.Windows.Threading.DispatcherTimer();
            close.Interval = TimeSpan.FromMilliseconds(700);
            close.Tick += delegate(object s, EventArgs e)
            {
                close.Stop();
                try
                {
                    Hide();
                    Program.Log("drawer warmup " + sw.ElapsedMilliseconds.ToString() + "ms (pre-compose)");
                }
                catch { }
                if (hold != null) hold.IsHitTestVisible = oldHit;
                ShowActivated = sa;
                Opacity = oldO;
                if (done != null) { try { done(); } catch { } }
            };
            close.Start();
        }

        // 启动时按"真实位置"把背景抓好、模糊烘好。
        // 不做这一步，启动后第一次打开必然命中不到缓存：要在 UI 线程同步抓 900x620（实测 75ms 含烘焙），
        // 用户看到的就是"第一次点开总要顿一下"，之后几次才顺。
        public void PrewarmFor(DockWindow dock)
        {
            try
            {
                PositionAbove(dock);
                PrewarmBackdrop();
            }
            catch (Exception ex) { Program.Log("drawer prewarm failed: " + ex.Message); }
        }

        public void ToggleFrom(DockWindow dock)
        {
            if (IsVisible)
            {
                HidePanel();
                return;
            }
            ShowPanel(dock);
        }

        public void KeepOpenFor(int ms) { graceUntil = Environment.TickCount + ms; }

        public void ShowPanel(DockWindow dock)
        {
            Program.Log("drawer: ShowPanel called");
            // 采样起点必须在 Show 之前：用户抱怨的"刚打开那一下"正好是最前面几帧，之前从 Show 之后才开始量，漏掉了。
            UIFrame.Bench("drawer open", 1500);
            System.Diagnostics.Stopwatch swPanel = System.Diagnostics.Stopwatch.StartNew();
            double msRebuild = 0;
            // 只在"顺序/内容真的变了"或"上次留在搜索结果上"时才重建。
            // 老代码每次打开都重建 32 个 tile —— 冷启动实测 350–450ms，这就是"点开要等一下"。
            bool rebuilt = false;
            if (!built || tileDirty || lastBuiltQuery.Length > 0)
            {
                RebuildTiles("");
                built = true;
                rebuilt = true;
                msRebuild = swPanel.Elapsed.TotalMilliseconds;
            }
            tileDirty = false;
            PositionAbove(dock);
            grace = true;
            RefreshBackdrop();       // 在 Show 之前抓：窗口还没上屏，抓回来的背景里不会有自己的图标
            Show();
            if (rebuilt || ActualWidth < 4) UpdateLayout();   // 没重建就别跑整趟布局
            Activate();
            Native.ForceForeground(this);
            if (searchBox != null)
            {
                searchBox.Text = "";
                searchBox.Focus();
            }
            DispatcherTimer g = new DispatcherTimer();
            g.Interval = TimeSpan.FromMilliseconds(450);
            g.Tick += delegate(object s, EventArgs e)
            {
                g.Stop();
                grace = false;
            };
            g.Start();
            if (StartInEdit && !editMode) ToggleEditMode();
            Program.Log("drawer open " + swPanel.Elapsed.TotalMilliseconds.ToString("0.0") + "ms (rebuild=" + msRebuild.ToString("0.0") + "ms)");
            Program.Log("drawer: shown, visible=" + IsVisible.ToString() + " active=" + IsActive.ToString());
        }

        public void HidePanel()
        {
            string st = Environment.StackTrace.Replace("\r\n", " | ");
            if (st.Length > 240) st = st.Substring(0, 240);
            Program.Log("drawer: HidePanel, was visible=" + IsVisible.ToString() + " from " + st);
            if (IsVisible) Hide();
            // 收起后在后台把下次要用的背景图抓好，下次打开就是 0 等待
            PrewarmBackdrop();
        }

        private void PositionAbove(DockWindow dock)
        {
            Rect wa = SystemParameters.WorkArea;
            Left = Math.Round(wa.Left + (wa.Width - Width) / 2.0);
            double dockTop = wa.Bottom;
            if (dock != null) dockTop = dock.Top;
            Top = Math.Round(dockTop - Height - 10);
            if (Top < wa.Top) Top = wa.Top;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Native.ApplyRoundedRegion(this, 36);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Native.ApplyRoundedRegion(this, 36);
        }

        // 整理模式点图标 / --hide 参数都走这里
        public bool HideApp(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (hidden.Contains(name)) return false;
            hidden.Add(name);
            ConfigStore.SaveHidden(new List<string>(hidden));
            RebuildTiles(searchBox == null ? "" : searchBox.Text);
            Program.Log("drawer: hidden \"" + name + "\", hiddenCount=" + hidden.Count.ToString());
            return true;
        }

        // 每个页签上显示真实数量：分类不是摆设，点进去必须是这个数
        private void UpdateTabCounts()
        {
            if (tabLabels == null || tabLabels.Count == 0) return;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (string c in AppCategory.All) counts[c] = 0;
            int happy = 0;
            foreach (AppItem it in all)
            {
                if (hidden.Contains(it.Name)) continue;
                happy++;
                string c = AppCategory.Classify(it, catOverrides);
                if (counts.ContainsKey(c)) counts[c] = counts[c] + 1;
                if (AppCategory.IsCommon(it)) counts["常用"] = counts["常用"] + 1;
            }
            counts["全部"] = happy;
            foreach (string c in AppCategory.All)
            {
                TextBlock t;
                if (tabLabels.TryGetValue(c, out t) && t != null) t.Text = c + " " + (counts.ContainsKey(c) ? counts[c] : 0).ToString();
            }
        }
        private void BuildTabs()
        {
            if (tabsHost == null) return;
            tabsHost.Children.Clear();
            tabBorders.Clear();
            tabLabels.Clear();
            foreach (string cat in AppCategory.All)
            {
                string c = cat;
                Border b = new Border();
                b.CornerRadius = new CornerRadius(9);
                b.Padding = new Thickness(12, 5, 12, 5);
                b.Margin = new Thickness(0, 0, 6, 0);
                b.Cursor = Cursors.Hand;
                TextBlock t = new TextBlock();
                t.Text = c;
                t.FontSize = 12;
                b.Child = t;
                b.MouseLeftButtonUp += delegate
                {
                    activeCat = c;
                    Program.Log("drawer: category=" + c);
                    PaintTabs();
                    RebuildTiles(searchBox == null ? "" : searchBox.Text);
                };
                tabsHost.Children.Add(b);
                tabBorders[c] = b;
                tabLabels[c] = t;
            }
            PaintTabs();
        }

        private void PaintTabs()
        {
            foreach (string cat in AppCategory.All)
            {
                Border b;
                TextBlock t;
                if (!tabBorders.TryGetValue(cat, out b)) continue;
                tabLabels.TryGetValue(cat, out t);
                bool on = string.Equals(cat, activeCat, StringComparison.Ordinal);
                b.Background = new SolidColorBrush(on
                    ? Color.FromArgb(0xC8, 0x3B, 0x82, 0xF6)
                    : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                if (t != null)
                {
                    t.Foreground = new SolidColorBrush(on
                        ? Colors.White
                        : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
                }
            }
        }

        public void SetCategory(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return;
            activeCat = cat;
            PaintTabs();
            RebuildTiles(searchBox == null ? "" : searchBox.Text);
        }

        private void ToggleEditMode()
        {
            editMode = !editMode;
            if (editLabel != null) editLabel.Text = editMode ? "完成" : "整理";
            if (editBtn != null)
            {
                editBtn.Background = new SolidColorBrush(editMode
                    ? Color.FromArgb(0xC0, 0x3B, 0x82, 0xF6)
                    : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                editBtn.BorderBrush = new SolidColorBrush(editMode
                    ? Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            }
            RebuildTiles(searchBox == null ? "" : searchBox.Text);
            Program.Log("drawer: edit mode=" + editMode.ToString());
        }

        // ── 分页懒加载（照 AirDeck 的 [PAGE-PREPARE]/[WARM] 思路做）
        // 一次只建"一屏多一行"，剩下的放在 pendingItems 里等滚动到底再建。
        // 好处：抽屉第一次打开不再为 100+ 个图标付全款；坏处：几乎没有，滚动时手感更平滑。
        private List<AppItem> pendingItems;
        private int pendingTotal;
        private const int FirstPage = 32;   // 8 列 × 4 行
        private const int ChunkSize = 16;   // 每次滚动补 2 行

        private void RebuildTiles(string query)
        {
            if (tiles == null) return;
            tileDirty = false;
            lastBuiltQuery = query == null ? "" : query;
            tiles.Children.Clear();
            string q = (query == null ? "" : query.Trim()).ToLowerInvariant();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            pendingItems = new List<AppItem>();
            int total = 0;
            foreach (AppItem item in all)
            {
                if (hidden.Contains(item.Name)) continue;
                total++;
                if (!string.Equals(activeCat, "全部", StringComparison.Ordinal))
                {
                    if (string.Equals(activeCat, "常用", StringComparison.Ordinal))
                    {
                        if (!AppCategory.IsCommon(item)) continue;
                    }
                    else if (!string.Equals(AppCategory.Classify(item, catOverrides), activeCat, StringComparison.Ordinal)) continue;
                }
                if (q.Length > 0)
                {
                    if (!Pinyin.Matches(item.Name, q)) continue;
                }
                pendingItems.Add(item);
            }
            pendingTotal = pendingItems.Count;
            int first = Math.Min(FirstPage, pendingItems.Count);
            for (int i = 0; i < first; i++)
            {
                AddTile(pendingItems[i]);
            }
            pendingItems.RemoveRange(0, first);
            if (footer != null) UpdateFooter(first);
            if (sw.ElapsedMilliseconds > 0)
            {
                Program.Log("drawer: page 1 tiles=" + first.ToString() + " built in " + sw.ElapsedMilliseconds.ToString()
                    + "ms (pending=" + pendingItems.Count.ToString() + ")");
            }
            // 每次重建都回到顶部（必须等布局更新完再滚，否则 ScrollViewer 还没拿到新高度，等于没滚）
            if (scroller != null)
            {
                scroller.ScrollToVerticalOffset(0);
                scroller.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)delegate
                    {
                        scroller.ScrollToHome();
                        scroller.ScrollToTop();
                    });
            }
            UpdateTabCounts();
            // 相邻页预取：第一屏画完后再悄悄补一批，滚起来就不会看到空白
            if (pendingItems.Count > 0)
            {
                DispatcherTimer pf = new DispatcherTimer();
                pf.Interval = TimeSpan.FromMilliseconds(220);
                pf.Tick += delegate
                {
                    pf.Stop();
                    FillNextChunk();
                };
                pf.Start();
            }
        }

        // 调试用：--drawer-scroll 会让抽屉往下滚一段，验证"滚动触发补页"这条路径
        public void DebugScroll(double off)
        {
            if (scroller == null) return;
            scroller.ScrollToVerticalOffset(off);
            Program.Log("drawer debug: scrolled to " + scroller.VerticalOffset.ToString("0")
                + " extent=" + scroller.ExtentHeight.ToString("0") + " view=" + scroller.ViewportHeight.ToString("0"));
        }

        // 补下一批；滚动到底附近会自动调，也可以被"预取"提前调
        private void FillNextChunk()
        {
            if (pendingItems == null || pendingItems.Count == 0) return;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int n = Math.Min(ChunkSize, pendingItems.Count);
            for (int i = 0; i < n; i++)
            {
                AddTile(pendingItems[i]);
            }
            pendingItems.RemoveRange(0, n);
            int shown = pendingTotal - pendingItems.Count;
            if (footer != null) UpdateFooter(shown);
            Program.Log("drawer: page +" + n.ToString() + " tiles=" + shown.ToString() + " in " + sw.ElapsedMilliseconds.ToString()
                + "ms (pending=" + pendingItems.Count.ToString() + ")");
        }

        private void AddTile(AppItem item)
        {
            Border tile;
            string tileKey = (editMode ? "E|" : "N|") + item.Name;
            if (!tileCache.TryGetValue(tileKey, out tile) || tile == null)
            {
                tile = (Border)MakeTile(item);
                tileCache[tileKey] = tile;
            }
            tiles.Children.Add(tile);
        }

        private void UpdateFooter(int shown)
        {
            if (footer == null) return;
            string catTag = string.Equals(activeCat, "全部", StringComparison.Ordinal) ? "" : "　·　" + activeCat;
            if (editMode)
            {
                footer.Text = string.Format("{0} / {1} 个应用{2}　·　整理中：点图标移出抽屉　·　点右上「完成」退出", shown, pendingTotal, catTag);
            }
            else
            {
                footer.Text = string.Format("{0} / {1} 个应用{2}　·　按使用次数排序　·　右键可隐藏　·　Esc 收起", shown, pendingTotal, catTag);
            }
        }

        private FrameworkElement MakeTile(AppItem item)
        {
            Border b = new Border();
            b.Width = TileSize;
            b.Height = TileSize;
            b.CornerRadius = new CornerRadius(12);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            int used = ConfigStore.UsageCount(item.Name);
            b.ToolTip = used > 0 ? item.Name + "　·　使用 " + used.ToString() + " 次" : item.Name;

            ScaleTransform b2 = new ScaleTransform(1, 1);
            b.RenderTransform = b2;
            b.RenderTransformOrigin = new Point(0.5, 0.5);

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;

            bool usedPack;
            FrameworkElement iconEl = IconTile.Build(item, 56, out usedPack);
            iconEl.Margin = new Thickness(0, 6, 0, 4);
            iconEl.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(iconEl);

            TextBlock label = new TextBlock();
            label.Text = item.Name;
            label.FontSize = 11;
            label.Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
            label.TextAlignment = TextAlignment.Center;
            label.TextWrapping = TextWrapping.Wrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.MaxWidth = TileSize - 12;
            label.MaxHeight = 28;
            label.HorizontalAlignment = HorizontalAlignment.Center;

            sp.Children.Add(label);

            if (editMode)
            {
                Grid wrap = new Grid();
                wrap.Children.Add(sp);
                Border badge = new Border();
                badge.Width = 18;
                badge.Height = 18;
                badge.CornerRadius = new CornerRadius(9);
                badge.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0xE0, 0x3B, 0x3B));
                badge.HorizontalAlignment = HorizontalAlignment.Right;
                badge.VerticalAlignment = VerticalAlignment.Top;
                badge.Margin = new Thickness(0, 2, 2, 0);
                TextBlock x = new TextBlock();
                x.Text = "\u2715";
                x.FontSize = 10;
                x.Foreground = Brushes.White;
                x.HorizontalAlignment = HorizontalAlignment.Center;
                x.VerticalAlignment = VerticalAlignment.Center;
                badge.Child = x;
                wrap.Children.Add(badge);
                b.Child = wrap;
            }
            else
            {
                b.Child = sp;
            }

            b.MouseEnter += delegate
            {
                b.Background = DockWindow.HoverBg;
                b2.ScaleX = 1.06;
                b2.ScaleY = 1.06;
            };
            b.MouseLeave += delegate
            {
                b.Background = Brushes.Transparent;
                b2.ScaleX = 1;
                b2.ScaleY = 1;
            };
            b.MouseLeftButtonUp += delegate
            {
                if (editMode)
                {
                    HideApp(item.Name);
                    return;
                }
                string msg;
                LaunchResult r = Launcher.ActivateOrLaunch(item, out msg);
                Usage.Note(item.Name);   // 记一次（30 秒内同一个应用只记一次）
                if (footer != null) footer.Text = msg;
                if (r != LaunchResult.Failed) HidePanel();
            };

            // 右键：把用不到的东西从这个抽屉里拿掉（按名字记进 hidden.json，重扫也不会回来）
            ContextMenu menu = new ContextMenu();
            MenuItem miHide = new MenuItem();
            miHide.Header = "从抽屉隐藏";
            miHide.Click += delegate
            {
                hidden.Add(item.Name);
                ConfigStore.SaveHidden(new List<string>(hidden));
                RebuildTiles(searchBox.Text);
            };
            menu.Items.Add(miHide);
            MenuItem miLaunch = new MenuItem();
            miLaunch.Header = "启动";
            miLaunch.Click += delegate
            {
                string msg;
                Launcher.ActivateOrLaunch(item, out msg);
                if (footer != null) footer.Text = msg;
            };
            menu.Items.Add(miLaunch);

            // 分类：手动指定这个应用属于哪一类（写 categories.json，优先于自动归类）
            MenuItem miCat = new MenuItem();
            miCat.Header = "分类";
            foreach (string cat in AppCategory.All)
            {
                if (string.Equals(cat, "全部", StringComparison.Ordinal)) continue;
                string c = cat;
                MenuItem sub = new MenuItem();
                sub.Header = c;
                if (string.Equals(AppCategory.Classify(item, catOverrides), c, StringComparison.Ordinal))
                {
                    sub.IsCheckable = true;
                    sub.IsChecked = true;
                }
                sub.Click += delegate
                {
                    catOverrides[item.Name] = c;
                    ConfigStore.SaveCategories(catOverrides);
                    if (footer != null) footer.Text = "已归到「" + c + "」：" + item.Name;
                    RebuildTiles(searchBox == null ? "" : searchBox.Text);
                };
                miCat.Items.Add(sub);
            }
            MenuItem subAuto = new MenuItem();
            subAuto.Header = "自动归类";
            subAuto.Click += delegate
            {
                catOverrides.Remove(item.Name);
                ConfigStore.SaveCategories(catOverrides);
                RebuildTiles(searchBox == null ? "" : searchBox.Text);
            };
            miCat.Items.Add(new Separator());
            miCat.Items.Add(subAuto);
            menu.Items.Add(miCat);

            b.ContextMenu = menu;

            return b;
        }
    }
}





