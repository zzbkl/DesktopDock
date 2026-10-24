using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopDock
{
    // 专门的管理窗口：开机自启、外观、应用、游戏模式都在这里改
    internal class SettingsWindow : Window
    {
        private static readonly Color Bg = Color.FromRgb(0x1B, 0x1C, 0x21);
        private static readonly Color Card = Color.FromRgb(0x23, 0x24, 0x2B);
        private static readonly Color Accent = Color.FromRgb(0x3B, 0x82, 0xF6);
        private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

        private DockWindow dock;
        private StackPanel content;
        private string section = "常规";
        private ConfigStore.DockSettings cfg;
        private static SettingsWindow current;
        public static string PendingSection;

        public static void Open(DockWindow dock)
        {
            try
            {
                if (current != null)
                {
                    current.Activate();
                    return;
                }
                current = new SettingsWindow(dock);
                current.Show();
                current.Activate();
            }
            catch (Exception ex) { Program.Log("settings open failed: " + ex.Message); }
        }

        private SettingsWindow(DockWindow dock)
        {
            this.dock = dock;
            cfg = ConfigStore.LoadSettings();
            if (!string.IsNullOrEmpty(PendingSection)) { section = PendingSection; PendingSection = null; }

            Title = "DesktopDock 设置";
            Width = 880;
            Height = 610;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Bg);
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            UseLayoutRounding = true;
            ShowInTaskbar = true;
            Topmost = true;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            UIElement head = BuildHeader();
            Grid.SetRow(head, 0);
            root.Children.Add(head);
            UIElement bodyEl = BuildBody();
            Grid.SetRow(bodyEl, 1);
            root.Children.Add(bodyEl);
            Content = root;

            Closed += delegate { current = null; };
        }

        private UIElement BuildHeader()
        {
            Border head = new Border();
            head.Height = 56;
            head.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1D));
            head.MouseLeftButtonDown += delegate
            {
                try { DragMove(); }
                catch { }
            };

            Grid g = new Grid();
            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;
            left.Margin = new Thickness(20, 0, 0, 0);
            TextBlock title = new TextBlock();
            title.Text = "DesktopDock";
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = Brushes.White;
            title.VerticalAlignment = VerticalAlignment.Center;
            TextBlock sep = new TextBlock();
            sep.Text = "｜设置";
            sep.FontSize = 13;
            sep.Margin = new Thickness(8, 0, 0, 0);
            sep.Foreground = Dim;
            sep.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(title);
            left.Children.Add(sep);
            g.Children.Add(left);

            Button close = new Button();
            close.Content = "✕";
            close.FontSize = 13;
            close.Width = 40;
            close.Height = 30;
            close.Margin = new Thickness(0, 12, 12, 12);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Background = Brushes.Transparent;
            close.Foreground = Dim;
            close.BorderThickness = new Thickness(0);
            close.Cursor = Cursors.Hand;
            close.Click += delegate { Close(); };
            g.Children.Add(close);

            head.Child = g;
            return head;
        }

        private UIElement BuildBody()
        {
            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StackPanel nav = new StackPanel();
            nav.Margin = new Thickness(12, 18, 8, 12);
            string[] items = { "常规", "外观", "应用", "游戏模式", "关于" };
            for (int i = 0; i < items.Length; i++)
            {
                nav.Children.Add(NavButton(items[i]));
            }
            Grid.SetColumn(nav, 0);
            body.Children.Add(nav);

            content = new StackPanel();
            content.Margin = new Thickness(16, 18, 20, 18);
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = content;
            Grid.SetColumn(sv, 1);
            body.Children.Add(sv);

            Paint();
            return body;
        }

        private Button NavButton(string name)
        {
            Button b = new Button();
            b.Content = name;
            b.Height = 38;
            b.Margin = new Thickness(0, 0, 0, 6);
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(14, 0, 0, 0);
            b.FontSize = 13;
            b.Cursor = Cursors.Hand;
            b.BorderThickness = new Thickness(0);
            b.Foreground = name == section ? Brushes.White : Dim;
            b.Background = name == section ? new SolidColorBrush(Accent) : Brushes.Transparent;
            b.Click += delegate
            {
                section = name;
                Paint();
            };
            return b;
        }

        private void Paint()
        {
            // 只重画正文，导航按钮高亮靠重建整个 body 太贵，这里简单整体刷新
            content.Children.Clear();
            if (section == "常规") PaintGeneral();
            else if (section == "外观") PaintLook();
            else if (section == "应用") PaintApps();
            else if (section == "游戏模式") PaintGames();
            else PaintAbout();

            // 导航按钮高亮
            Grid body = content.Parent is ScrollViewer ? ((ScrollViewer)content.Parent).Parent as Grid : null;
            if (body != null && body.Children.Count > 1)
            {
                StackPanel nav = body.Children[0] as StackPanel;
                if (nav != null)
                {
                    string[] items = { "常规", "外观", "应用", "游戏模式", "关于" };
                    for (int i = 0; i < nav.Children.Count && i < items.Length; i++)
                    {
                        Button b = nav.Children[i] as Button;
                        if (b == null) continue;
                        b.Foreground = items[i] == section ? Brushes.White : Dim;
                        b.Background = items[i] == section ? new SolidColorBrush(Accent) : Brushes.Transparent;
                    }
                }
            }
        }

        private Border Card2(double topMargin)
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(12);
            card.Background = new SolidColorBrush(Card);
            card.Padding = new Thickness(18, 14, 18, 14);
            card.Margin = new Thickness(0, topMargin, 0, 10);
            return card;
        }

        private TextBlock Title2(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 14;
            t.FontWeight = FontWeights.SemiBold;
            t.Foreground = Brushes.White;
            t.Margin = new Thickness(0, 0, 0, 6);
            return t;
        }

        private TextBlock Hint(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12;
            t.Foreground = Dim;
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 2, 0, 0);
            return t;
        }

        private Grid Row(string label, UIElement control)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 6, 0, 6);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock l = new TextBlock();
            l.Text = label;
            l.FontSize = 13;
            l.Foreground = new SolidColorBrush(Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF));
            l.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(l);
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        private static CheckBox SourceCheck(string text, bool on)
        {
            CheckBox b = new CheckBox();
            b.Content = text;
            b.IsChecked = on;
            b.Foreground = Brushes.White;
            b.FontSize = 13;
            b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }

        private Button ActionButton(string text, Action click)
        {
            Button b = new Button();
            b.Content = text;
            b.FontSize = 13;
            b.Padding = new Thickness(14, 6, 14, 6);
            b.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            b.Foreground = Brushes.White;
            b.BorderThickness = new Thickness(0);
            b.Cursor = Cursors.Hand;
            b.Click += delegate { try { click(); } catch (Exception ex) { Program.Log("settings action failed: " + ex.Message); } };
            return b;
        }

        private void PaintGeneral()
        {
            content.Children.Add(Title2("常规"));

            Border c1 = Card2(6);
            StackPanel s1 = new StackPanel();
            CheckBox auto = new CheckBox();
            auto.Content = "开机自启动";
            auto.FontSize = 13;
            auto.Foreground = Brushes.White;
            auto.IsChecked = Startup.IsEnabled();
            auto.VerticalContentAlignment = VerticalAlignment.Center;
            auto.Click += delegate
            {
                bool on = auto.IsChecked == true;
                Startup.SetEnabled(on);
                Program.Log("autostart set to " + on.ToString());
            };
            s1.Children.Add(auto);
            s1.Children.Add(Hint("登录系统后自动运行，启动后只留任务栏上方那条细把手，不占画面。"));
            c1.Child = s1;
            content.Children.Add(c1);

            Border c2 = Card2(0);
            StackPanel s2 = new StackPanel();
            s2.Children.Add(Title2("全局热键"));
            s2.Children.Add(Row("打开全部应用抽屉", new TextBlock { Text = "Alt + `", FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }));
            s2.Children.Add(Row("打开 / 收起这条栏", new TextBlock { Text = "Alt + Shift + Space", FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }));
            s2.Children.Add(Hint("所有应用都收起时不占屏幕；全屏游戏/高性能程序运行时会自动整个挂起，切回来就恢复。"));
            c2.Child = s2;
            content.Children.Add(c2);

            Border c3 = Card2(0);
            StackPanel s3 = new StackPanel();
            s3.Children.Add(Title2("热角"));
            s3.Children.Add(Hint("鼠标贴到屏幕角落停 0.6 秒触发一次。四角可分别设置，也可以留空不触发。"));
            ConfigStore.DockSettings hs = ConfigStore.LoadSettings();
            s3.Children.Add(HotCornerRow("左上角", "TL", hs.HotCornerTL));
            s3.Children.Add(HotCornerRow("右上角", "TR", hs.HotCornerTR));
            s3.Children.Add(HotCornerRow("左下角", "BL", hs.HotCornerBL));
            s3.Children.Add(HotCornerRow("右下角", "BR", hs.HotCornerBR));
            c3.Child = s3;
            content.Children.Add(c3);

            Border c4 = Card2(0);
            StackPanel s4 = new StackPanel();
            s4.Children.Add(Title2("维护"));
            WrapPanel btns = new WrapPanel();
            Button rescan = ActionButton("重新扫描应用", delegate { dock.RescanFromSettings(); });
            rescan.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(rescan);
            Button folder = ActionButton("打开配置目录", delegate
            {
                System.Diagnostics.Process.Start("explorer.exe", ConfigStore.Dir);
            });
            folder.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(folder);
            Button quit = ActionButton("退出 DesktopDock", delegate { Application.Current.Shutdown(); });
            btns.Children.Add(quit);
            s4.Children.Add(btns);
            s4.Children.Add(Hint("配置目录：" + ConfigStore.Dir));
            c4.Child = s4;
            content.Children.Add(c4);
        }

        // 热角下拉框：空 / 打开抽屉 / 显示栏
        private UIElement HotCornerRow(string label, string key, string current)
        {
            ComboBox box = new ComboBox();
            box.Width = 140;
            box.FontSize = 13;
            box.Items.Add("不触发");
            box.Items.Add("打开全部应用");
            box.Items.Add("显示这条栏");
            box.SelectedIndex = current == "drawer" ? 1 : (current == "dock" ? 2 : 0);
            box.SelectionChanged += delegate
            {
                string val = box.SelectedIndex == 1 ? "drawer" : (box.SelectedIndex == 2 ? "dock" : "");
                ConfigStore.DockSettings cfg = ConfigStore.LoadSettings();
                if (key == "TL") cfg.HotCornerTL = val;
                else if (key == "TR") cfg.HotCornerTR = val;
                else if (key == "BL") cfg.HotCornerBL = val;
                else cfg.HotCornerBR = val;
                cfg.HotCorners = true;
                ConfigStore.SaveSettings(cfg);
                if (dock != null) dock.LoadHotCorners();
                Program.Log("hot corner " + key + " set to " + (val.Length == 0 ? "(none)" : val));
            };
            return Row(label, box);
        }

        private void PaintLook()
        {
            content.Children.Add(Title2("外观"));
            Border card = Card2(6);
            StackPanel s = new StackPanel();

            StackPanel iconRow = new StackPanel();
            iconRow.Orientation = Orientation.Horizontal;
            TextBlock iconLabel = new TextBlock();
            iconLabel.Text = "图标大小";
            iconLabel.FontSize = 13;
            iconLabel.Foreground = Brushes.White;
            iconLabel.Width = 90;
            iconLabel.VerticalAlignment = VerticalAlignment.Center;
            Slider iconSlider = new Slider();
            iconSlider.Minimum = 36;
            iconSlider.Maximum = 76;
            iconSlider.TickFrequency = 4;
            iconSlider.IsSnapToTickEnabled = true;
            iconSlider.Value = cfg.IconSize;
            iconSlider.Width = 320;
            iconSlider.VerticalAlignment = VerticalAlignment.Center;
            TextBlock iconVal = new TextBlock();
            iconVal.Text = cfg.IconSize.ToString("0") + " px";
            iconVal.FontSize = 12;
            iconVal.Foreground = Dim;
            iconVal.Margin = new Thickness(12, 0, 0, 0);
            iconVal.VerticalAlignment = VerticalAlignment.Center;
            iconSlider.ValueChanged += delegate
            {
                cfg.IconSize = Math.Round(iconSlider.Value);
                iconVal.Text = cfg.IconSize.ToString("0") + " px";
                if (cfg.BarHeight < cfg.IconSize + 24) cfg.BarHeight = cfg.IconSize + 44;
                DockWindow.IconSize = cfg.IconSize;
                DockWindow.BarHeight = cfg.BarHeight;
                ConfigStore.SaveSettings(cfg);
                dock.ApplySettings();
            };
            iconRow.Children.Add(iconLabel);
            iconRow.Children.Add(iconSlider);
            iconRow.Children.Add(iconVal);
            s.Children.Add(iconRow);

            StackPanel barRow = new StackPanel();
            barRow.Orientation = Orientation.Horizontal;
            barRow.Margin = new Thickness(0, 10, 0, 0);
            TextBlock barLabel = new TextBlock();
            barLabel.Text = "栏高度";
            barLabel.FontSize = 13;
            barLabel.Foreground = Brushes.White;
            barLabel.Width = 90;
            barLabel.VerticalAlignment = VerticalAlignment.Center;
            Slider barSlider = new Slider();
            barSlider.Minimum = 64;
            barSlider.Maximum = 150;
            barSlider.TickFrequency = 4;
            barSlider.IsSnapToTickEnabled = true;
            barSlider.Value = cfg.BarHeight;
            barSlider.Width = 320;
            barSlider.VerticalAlignment = VerticalAlignment.Center;
            TextBlock barVal = new TextBlock();
            barVal.Text = cfg.BarHeight.ToString("0") + " px";
            barVal.FontSize = 12;
            barVal.Foreground = Dim;
            barVal.Margin = new Thickness(12, 0, 0, 0);
            barVal.VerticalAlignment = VerticalAlignment.Center;
            barSlider.ValueChanged += delegate
            {
                double v = Math.Round(barSlider.Value);
                if (v < DockWindow.IconSize + 20) v = DockWindow.IconSize + 20;
                cfg.BarHeight = v;
                barVal.Text = v.ToString("0") + " px";
                DockWindow.BarHeight = v;
                ConfigStore.SaveSettings(cfg);
                dock.ApplySettings();
            };
            barRow.Children.Add(barLabel);
            barRow.Children.Add(barSlider);
            barRow.Children.Add(barVal);
            s.Children.Add(barRow);

            StackPanel glassRow = new StackPanel();
            glassRow.Orientation = Orientation.Horizontal;
            glassRow.Margin = new Thickness(0, 10, 0, 0);
            TextBlock glassLabel = new TextBlock();
            glassLabel.Text = "玻璃深浅";
            glassLabel.FontSize = 13;
            glassLabel.Foreground = Brushes.White;
            glassLabel.Width = 90;
            glassLabel.VerticalAlignment = VerticalAlignment.Center;
            Slider glassSlider = new Slider();
            glassSlider.Minimum = 0x18;
            glassSlider.Maximum = 0xB0;
            glassSlider.TickFrequency = 0x10;
            glassSlider.IsSnapToTickEnabled = true;
            glassSlider.Value = cfg.TintTop;
            glassSlider.Width = 320;
            glassSlider.VerticalAlignment = VerticalAlignment.Center;
            TextBlock glassVal = new TextBlock();
            glassVal.Text = cfg.TintTop < 0x50 ? "偏通透" : (cfg.TintTop < 0x80 ? "中等" : "偏暗");
            glassVal.FontSize = 12;
            glassVal.Foreground = Dim;
            glassVal.Margin = new Thickness(12, 0, 0, 0);
            glassVal.VerticalAlignment = VerticalAlignment.Center;
            glassSlider.ValueChanged += delegate
            {
                int top = (int)Math.Round(glassSlider.Value);
                cfg.TintTop = top;
                cfg.TintBottom = Math.Min(0xE0, top + 0x2C);
                glassVal.Text = top < 0x50 ? "偏通透" : (top < 0x80 ? "中等" : "偏暗");
                DockWindow.TintTop = cfg.TintTop;
                DockWindow.TintBottom = cfg.TintBottom;
                ConfigStore.SaveSettings(cfg);
                dock.ApplySettings();
            };
            glassRow.Children.Add(glassLabel);
            glassRow.Children.Add(glassSlider);
            glassRow.Children.Add(glassVal);
            s.Children.Add(glassRow);

            s.Children.Add(Hint("拖动滑块会立刻作用到这条栏上（毛玻璃是抓屏模糊，越暗越像 macOS 的深色 Dock）。"));

            // 毛玻璃的两个技术参数：模糊半径 / 抓屏下采样（对应 AirDeck 的 DockFrostRadius / DockFrostDownscale）
            StackPanel frostRow = new StackPanel();
            frostRow.Orientation = Orientation.Horizontal;
            frostRow.Margin = new Thickness(0, 10, 0, 0);
            TextBlock frostLabel = new TextBlock();
            frostLabel.Text = "模糊半径";
            frostLabel.FontSize = 13;
            frostLabel.Foreground = Brushes.White;
            frostLabel.Width = 90;
            frostLabel.VerticalAlignment = VerticalAlignment.Center;
            Slider frostSlider = new Slider();
            frostSlider.Minimum = 4;
            frostSlider.Maximum = 48;
            frostSlider.TickFrequency = 4;
            frostSlider.IsSnapToTickEnabled = true;
            frostSlider.Value = cfg.FrostRadius;
            frostSlider.Width = 320;
            frostSlider.VerticalAlignment = VerticalAlignment.Center;
            TextBlock frostVal = new TextBlock();
            frostVal.Text = ((int)cfg.FrostRadius).ToString() + " px";
            frostVal.FontSize = 12;
            frostVal.Foreground = Dim;
            frostVal.Margin = new Thickness(12, 0, 0, 0);
            frostVal.VerticalAlignment = VerticalAlignment.Center;
            frostSlider.ValueChanged += delegate
            {
                cfg.FrostRadius = Math.Round(frostSlider.Value);
                frostVal.Text = ((int)cfg.FrostRadius).ToString() + " px";
                ConfigStore.SaveSettings(cfg);
                dock.ApplySettings();
            };
            frostRow.Children.Add(frostLabel);
            frostRow.Children.Add(frostSlider);
            frostRow.Children.Add(frostVal);
            s.Children.Add(frostRow);

            StackPanel dsRow = new StackPanel();
            dsRow.Orientation = Orientation.Horizontal;
            dsRow.Margin = new Thickness(0, 8, 0, 0);
            TextBlock dsLabel = new TextBlock();
            dsLabel.Text = "抓屏精度";
            dsLabel.FontSize = 13;
            dsLabel.Foreground = Brushes.White;
            dsLabel.Width = 90;
            dsLabel.VerticalAlignment = VerticalAlignment.Center;
            Slider dsSlider = new Slider();
            dsSlider.Minimum = 2;
            dsSlider.Maximum = 4;
            dsSlider.TickFrequency = 1;
            dsSlider.IsSnapToTickEnabled = true;
            dsSlider.Value = cfg.FrostDownscale;
            dsSlider.Width = 320;
            dsSlider.VerticalAlignment = VerticalAlignment.Center;
            TextBlock dsVal = new TextBlock();
            dsVal.Text = "1/" + cfg.FrostDownscale.ToString() + " 分辨率";
            dsVal.FontSize = 12;
            dsVal.Foreground = Dim;
            dsVal.Margin = new Thickness(12, 0, 0, 0);
            dsVal.VerticalAlignment = VerticalAlignment.Center;
            dsSlider.ValueChanged += delegate
            {
                cfg.FrostDownscale = (int)Math.Round(dsSlider.Value);
                dsVal.Text = "1/" + cfg.FrostDownscale.ToString() + " 分辨率";
                ConfigStore.SaveSettings(cfg);
                dock.ApplySettings();
            };
            dsRow.Children.Add(dsLabel);
            dsRow.Children.Add(dsSlider);
            dsRow.Children.Add(dsVal);
            s.Children.Add(dsRow);
            s.Children.Add(Hint("模糊半径越大越糊，抓屏精度 1/3、1/4 更省内存也更容易出块状色带。"));

            // 图标包：把系统图标换成统一的圆角磁贴（macOS 风格）
            StackPanel packRow = new StackPanel();
            packRow.Orientation = Orientation.Horizontal;
            packRow.Margin = new Thickness(0, 14, 0, 0);
            CheckBox packBox = new CheckBox();
            packBox.Content = "使用图标包（圆角磁贴）";
            packBox.FontSize = 13;
            packBox.Foreground = Brushes.White;
            packBox.IsChecked = cfg.UseIconPack;
            packBox.VerticalAlignment = VerticalAlignment.Center;
            packRow.Children.Add(packBox);
            TextBlock packVal = new TextBlock();
            packVal.FontSize = 12;
            packVal.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            packVal.Margin = new Thickness(12, 0, 0, 0);
            packVal.VerticalAlignment = VerticalAlignment.Center;
            packVal.Text = IconPack.Count > 0 ? ("已识别 " + IconPack.Count.ToString() + " 个图标") : "未找到图标包";
            packRow.Children.Add(packVal);
            packBox.Click += delegate
            {
                cfg.UseIconPack = packBox.IsChecked == true;
                IconPack.Enabled = cfg.UseIconPack;
                ConfigStore.SaveSettings(cfg);
                dock.ApplySettings();
            };
            s.Children.Add(packRow);
            s.Children.Add(Hint(IconPack.Folder == null
                ? "图标包目录：把 macOS 风格的 png 放进 %APPDATA%\\DesktopDock\\icons 即可（支持 pack.json 清单）。"
                : "图标包目录：" + IconPack.Folder));

            // 小组件：时间 / CPU / 内存 贴在栏上方
            StackPanel wRow = new StackPanel();
            wRow.Orientation = Orientation.Horizontal;
            wRow.Margin = new Thickness(0, 12, 0, 0);
            CheckBox wBox = new CheckBox();
            wBox.Content = "在栏上方显示小组件（时间 / CPU / 内存 / 网速）";
            wBox.FontSize = 13;
            wBox.Foreground = Brushes.White;
            wBox.IsChecked = cfg.ShowWidgets;
            wBox.VerticalAlignment = VerticalAlignment.Center;
            wRow.Children.Add(wBox);
            wBox.Click += delegate
            {
                cfg.ShowWidgets = wBox.IsChecked == true;
                ConfigStore.SaveSettings(cfg);
                if (dock.Widgets != null)
                {
                    dock.WidgetWanted = cfg.ShowWidgets;
                    if (cfg.ShowWidgets) dock.Widgets.ShowAbove(dock, true);
                    else dock.Widgets.HidePanel();
                }
            };
            s.Children.Add(wRow);

            // 不压住窗口：默认只在桌面 / 栏展开时出现
            StackPanel wdRow = new StackPanel();
            wdRow.Orientation = Orientation.Horizontal;
            wdRow.Margin = new Thickness(0, 8, 0, 0);
            CheckBox wdBox = new CheckBox();
            wdBox.Content = "小组件不压住窗口（只在桌面或栏展开时出现）";
            wdBox.FontSize = 13;
            wdBox.Foreground = Brushes.White;
            wdBox.IsChecked = cfg.WidgetsDesktopOnly;
            wdBox.VerticalAlignment = VerticalAlignment.Center;
            wdRow.Children.Add(wdBox);
            wdBox.Click += delegate
            {
                cfg.WidgetsDesktopOnly = wdBox.IsChecked == true;
                ConfigStore.SaveSettings(cfg);
                dock.LoadWidgetPrefs();
                if (!cfg.WidgetsDesktopOnly && dock.Widgets != null) dock.Widgets.ShowAbove(dock, true);
                Program.Log("widgets: desktop-only=" + cfg.WidgetsDesktopOnly.ToString());
            };
            s.Children.Add(wdRow);

            // 回到桌面时自动显示：边沿触发，只在"从别处回到桌面"那一下亮出来
            StackPanel deskRow = new StackPanel();
            deskRow.Orientation = Orientation.Horizontal;
            deskRow.Margin = new Thickness(0, 8, 0, 0);
            CheckBox deskBox = new CheckBox();
            deskBox.Content = "回到桌面时自动显示 dock";
            deskBox.FontSize = 13;
            deskBox.Foreground = Brushes.White;
            deskBox.IsChecked = cfg.AutoShowOnDesktop;
            deskBox.VerticalAlignment = VerticalAlignment.Center;
            deskRow.Children.Add(deskBox);
            deskBox.Click += delegate
            {
                cfg.AutoShowOnDesktop = deskBox.IsChecked == true;
                ConfigStore.SaveSettings(cfg);
                dock.LoadWidgetPrefs();
                Program.Log("desk auto-show=" + cfg.AutoShowOnDesktop.ToString());
            };
            s.Children.Add(deskRow);

            Button wReset = ActionButton("小组件回到栏上方（清掉手动拖的位置）", delegate
            {
                ConfigStore.DockSettings c2 = ConfigStore.LoadSettings();
                c2.WidgetX = -1;
                c2.WidgetY = -1;
                ConfigStore.SaveSettings(c2);
                dock.WidgetWanted = true;
                if (dock.Widgets != null) dock.Widgets.ShowAbove(dock, true);
                Program.Log("widgets: position reset");
            });
            wReset.Margin = new Thickness(0, 8, 0, 0);
            s.Children.Add(wReset);

            Button reset = ActionButton("恢复默认", delegate
            {
                cfg = new ConfigStore.DockSettings();
                ConfigStore.SaveSettings(cfg);
                DockWindow.IconSize = cfg.IconSize;
                DockWindow.BarHeight = cfg.BarHeight;
                DockWindow.TintTop = cfg.TintTop;
                DockWindow.TintBottom = cfg.TintBottom;
                dock.ApplySettings();
                Paint();
            });
            reset.Margin = new Thickness(0, 12, 0, 0);
            s.Children.Add(reset);

            card.Child = s;
            content.Children.Add(card);
        }

        private void PaintApps()
        {
            content.Children.Add(Title2("应用"));

            List<AppItem> all = dock.AllApps;
            Border c1 = Card2(6);
            StackPanel s1 = new StackPanel();
            s1.Children.Add(Title2("抽屉里的应用"));
            TextBlock count = new TextBlock();
            count.Text = "共 " + (all == null ? 0 : all.Count).ToString() + " 个（含被隐藏的）";
            count.FontSize = 13;
            count.Foreground = Brushes.White;
            s1.Children.Add(count);
            s1.Children.Add(Hint("调整顺序：按住常驻栏里的图标左右拖。去掉不用的：抽屉里点「整理」再点图标。加新应用：抽屉里点「＋ 添加」。"));
            WrapPanel btns = new WrapPanel();
            btns.Margin = new Thickness(0, 10, 0, 0);
            Button openDrawer = ActionButton("打开全部应用抽屉", delegate { dock.ShowDock(true); dock.Drawer.ShowPanel(dock); });
            openDrawer.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(openDrawer);
            Button rescan = ActionButton("重新扫描", delegate { dock.RescanFromSettings(); });
            rescan.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(rescan);
            Button clearHidden = ActionButton("恢复所有被隐藏的应用", delegate
            {
                ConfigStore.SaveHidden(new List<string>());
                if (dock.Drawer != null) dock.Drawer.SetApps(dock.AllApps);
                Program.Log("hidden list cleared from settings");
                PaintApps();
            });
            btns.Children.Add(clearHidden);
            s1.Children.Add(btns);
            c1.Child = s1;
            content.Children.Add(c1);

            Border cD = Card2(6);
            StackPanel sD = new StackPanel();
            sD.Children.Add(Title2("自动发现 / 自动更新"));
            sD.Children.Add(Hint("扫描开始菜单、桌面快捷方式，再额外扫商店应用和 Steam 库；新装的会自动出现，卸载的会自动消失。系统自带的那些（设置、相机、地图、商店…）单独归到「自带」页签，不跟第三方混在一起。"));
            WrapPanel srcs = new WrapPanel();
            srcs.Margin = new Thickness(0, 8, 0, 0);
            ConfigStore.DockSettings ds = ConfigStore.LoadSettings();
            CheckBox cu = SourceCheck("商店应用 + 系统自带（UWP）", ds.ScanUwp);
            CheckBox cs = SourceCheck("Steam 游戏库", ds.ScanSteam);
            CheckBox cd = SourceCheck("程序目录深扫（慢）", ds.ScanDeep);
            cu.Margin = new Thickness(0, 0, 16, 0);
            cs.Margin = new Thickness(0, 0, 16, 0);
            srcs.Children.Add(cu);
            srcs.Children.Add(cs);
            srcs.Children.Add(cd);
            sD.Children.Add(srcs);

            WrapPanel iv = new WrapPanel();
            iv.Margin = new Thickness(0, 10, 0, 0);
            TextBlock ivLabel = new TextBlock();
            ivLabel.Text = "自动刷新：每";
            ivLabel.Foreground = Brushes.White;
            ivLabel.FontSize = 13;
            ivLabel.VerticalAlignment = VerticalAlignment.Center;
            iv.Children.Add(ivLabel);
            TextBox ivBox = new TextBox();
            ivBox.Text = ds.AutoRefreshMinutes.ToString();
            ivBox.Width = 52;
            ivBox.Margin = new Thickness(8, 0, 8, 0);
            ivBox.VerticalContentAlignment = VerticalAlignment.Center;
            iv.Children.Add(ivBox);
            TextBlock ivTail = new TextBlock();
            ivTail.Text = "分钟（0 = 关闭）";
            ivTail.Foreground = Brushes.White;
            ivTail.FontSize = 13;
            ivTail.VerticalAlignment = VerticalAlignment.Center;
            iv.Children.Add(ivTail);
            sD.Children.Add(iv);

            TextBlock lastScan = new TextBlock();
            lastScan.Text = "上次检测：" + Program.LastScanText;
            lastScan.FontSize = 12;
            lastScan.Margin = new Thickness(0, 8, 0, 0);
            lastScan.Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xBE));
            Button testBtn = ActionButton("立即检测一次", delegate
            {
                ConfigStore.DockSettings cur = ConfigStore.LoadSettings();
                cur.ScanUwp = cu.IsChecked == true;
                cur.ScanSteam = cs.IsChecked == true;
                cur.ScanDeep = cd.IsChecked == true;
                int mins;
                if (!int.TryParse(ivBox.Text.Trim(), out mins)) mins = cur.AutoRefreshMinutes;
                if (mins < 0) mins = 0;
                if (mins > 240) mins = 240;
                cur.AutoRefreshMinutes = mins;
                ConfigStore.SaveSettings(cur);
                dock.RescanFromSettings();
                lastScan.Text = "上次检测：" + Program.LastScanText;
            });
            testBtn.Margin = new Thickness(0, 10, 0, 0);
            sD.Children.Add(testBtn);
            sD.Children.Add(lastScan);
            cD.Child = sD;
            content.Children.Add(cD);

            Border cFolder = Card2(6);
            StackPanel sF = new StackPanel();
            sF.Children.Add(Title2("图标文件夹"));
            sF.Children.Add(Hint("把几个应用塞进栏上的一个图标里，点开弹出小面板。适合日常只用一两个的小工具。"));
            List<AppFolder> folders = FolderStore.Load();
            if (folders.Count == 0)
            {
                TextBlock none = new TextBlock();
                none.Text = "还没有文件夹。";
                none.FontSize = 12;
                none.Margin = new Thickness(0, 6, 0, 0);
                none.Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xBE));
                sF.Children.Add(none);
            }
            foreach (AppFolder f in folders)
            {
                WrapPanel line = new WrapPanel();
                line.Margin = new Thickness(0, 6, 0, 0);
                TextBlock nm = new TextBlock();
                nm.Text = f.Name + " · " + f.Members.Count.ToString() + " 个应用";
                nm.FontSize = 13;
                nm.Foreground = Brushes.White;
                nm.VerticalAlignment = VerticalAlignment.Center;
                nm.Margin = new Thickness(0, 0, 10, 0);
                line.Children.Add(nm);
                string id = f.Id;
                Button del = ActionButton("删除", delegate
                {
                    List<AppFolder> now = FolderStore.Load();
                    List<AppFolder> keep = new List<AppFolder>();
                    foreach (AppFolder x in now) if (x.Id != id) keep.Add(x);
                    FolderStore.Save(keep);
                    if (dock != null) dock.Rebuild();
                    PaintApps();
                });
                line.Children.Add(del);
                sF.Children.Add(line);
            }

            TextBlock newLabel = new TextBlock();
            newLabel.Text = "新建：";
            newLabel.FontSize = 13;
            newLabel.Foreground = Brushes.White;
            newLabel.Margin = new Thickness(0, 12, 0, 4);
            sF.Children.Add(newLabel);

            TextBox nameBox = new TextBox();
            nameBox.Width = 220;
            nameBox.FontSize = 13;
            nameBox.Text = "";
            nameBox.HorizontalAlignment = HorizontalAlignment.Left;
            sF.Children.Add(nameBox);

            ListBox pick = new ListBox();
            pick.SelectionMode = SelectionMode.Multiple;
            pick.Height = 150;
            pick.Margin = new Thickness(0, 6, 0, 0);
            pick.FontSize = 12;
            if (all != null)
            {
                List<string> names = new List<string>();
                foreach (AppItem it in all) if (it != null && !string.IsNullOrEmpty(it.Name)) names.Add(it.Name);
                names.Sort(StringComparer.CurrentCulture);
                foreach (string nm2 in names) pick.Items.Add(nm2);
            }
            sF.Children.Add(pick);

            Button create = ActionButton("创建文件夹", delegate
            {
                string nm3 = nameBox.Text == null ? "" : nameBox.Text.Trim();
                if (nm3.Length == 0) { newLabel.Text = "新建：请先填名字"; return; }
                List<string> members = new List<string>();
                foreach (object o in pick.SelectedItems) members.Add(o as string);
                if (members.Count == 0) { newLabel.Text = "新建：至少选一个应用"; return; }
                List<AppFolder> list = FolderStore.Load();
                AppFolder af = new AppFolder();
                af.Id = FolderStore.NewId();
                af.Name = nm3;
                af.Members = members;
                list.Add(af);
                FolderStore.Save(list);
                Program.Log("folder created: " + nm3 + " (" + members.Count.ToString() + ")");
                if (dock != null) dock.Rebuild();
                PaintApps();
            });
            create.Margin = new Thickness(0, 8, 0, 0);
            sF.Children.Add(create);
            cFolder.Child = sF;
            content.Children.Add(cFolder);

            Border c2 = Card2(0);
            StackPanel s2 = new StackPanel();
            s2.Children.Add(Title2("常驻栏（7 个位置）"));
            string[] pinned = dock.PinnedNames();
            for (int i = 0; i < pinned.Length; i++)
            {
                TextBlock t = new TextBlock();
                t.Text = (i + 1).ToString() + ".  " + pinned[i];
                t.FontSize = 13;
                t.Margin = new Thickness(0, 3, 0, 0);
                t.Foreground = Brushes.White;
                s2.Children.Add(t);
            }
            s2.Children.Add(Hint("顺序就是栏上从左到右的顺序，拖一次就记住了。"));
            c2.Child = s2;
            content.Children.Add(c2);
        }

        private void PaintGames()
        {
            content.Children.Add(Title2("游戏模式"));

            Border c1 = Card2(6);
            StackPanel s1 = new StackPanel();
            s1.Children.Add(new TextBlock
            {
                Text = dock.GameModeOn ? "当前状态：游戏模式（后台已挂起）" : "当前状态：普通（正常运行）",
                FontSize = 13,
                Foreground = Brushes.White
            });
            s1.Children.Add(Hint("独占全屏的程序会自动触发，不用写名单。下面这些是“窗口化/无边框”运行的游戏，靠进程名识别。"));
            c1.Child = s1;
            content.Children.Add(c1);

            Border c2 = Card2(0);
            StackPanel s2 = new StackPanel();
            s2.Children.Add(Title2("进程名名单（每行一个）"));
            TextBox box = new TextBox();
            box.AcceptsReturn = true;
            box.Height = 190;
            box.FontSize = 13;
            box.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            box.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x20));
            box.Foreground = Brushes.White;
            box.BorderThickness = new Thickness(1);
            box.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            box.Padding = new Thickness(8);
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            string[] games = ConfigStore.LoadGames();
            box.Text = string.Join("\r\n", games);
            s2.Children.Add(box);

            WrapPanel btns = new WrapPanel();
            btns.Margin = new Thickness(0, 10, 0, 0);
            TextBlock saved = new TextBlock();
            saved.FontSize = 12;
            saved.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xD6, 0x8A));
            saved.Margin = new Thickness(10, 0, 0, 0);
            saved.VerticalAlignment = VerticalAlignment.Center;
            Button save = ActionButton("保存名单", delegate
            {
                string[] lines = box.Text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> clean = new List<string>();
                for (int i = 0; i < lines.Length; i++)
                {
                    string v = lines[i].Trim();
                    if (v.Length > 0) clean.Add(v);
                }
                ConfigStore.SaveGames(clean.ToArray());
                Native.GameProcessNames = clean.ToArray();
                saved.Text = "已保存 " + clean.Count.ToString() + " 条";
                Program.Log("games list saved, count=" + clean.Count.ToString());
            });
            save.Margin = new Thickness(0, 0, 8, 0);
            btns.Children.Add(save);
            btns.Children.Add(saved);
            s2.Children.Add(btns);
            s2.Children.Add(Hint("进程名在任务管理器“详细信息”里看，不带 .exe 也行。改完立刻生效，不用重启。"));
            c2.Child = s2;
            content.Children.Add(c2);
        }

        private void PaintAbout()
        {
            content.Children.Add(Title2("关于"));
            Border card = Card2(6);
            StackPanel s = new StackPanel();
            string[] lines =
            {
                "DesktopDock  v0.6（自建，WPF / .NET Framework 4.8）",
                "毛玻璃：抓屏 + 高斯模糊（真模糊，非静态贴图）",
                "配置目录：" + ConfigStore.Dir,
                "程序：" + System.Reflection.Assembly.GetExecutingAssembly().Location,
                "内存：" + (System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024).ToString() + " MB"
            };
            for (int i = 0; i < lines.Length; i++)
            {
                TextBlock t = new TextBlock();
                t.Text = lines[i];
                t.FontSize = 12;
                t.Margin = new Thickness(0, 3, 0, 0);
                t.TextWrapping = TextWrapping.Wrap;
                t.Foreground = i == 0 ? Brushes.White : Dim;
                s.Children.Add(t);
            }
            card.Child = s;
            content.Children.Add(card);
        }
    }
}


