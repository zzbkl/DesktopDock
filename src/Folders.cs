using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopDock
{
    // 常驻栏上的"应用文件夹"：一个图标里装多个应用，点开弹出小面板
    internal class AppFolder
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> Members { get; set; }

        public AppFolder()
        {
            Members = new List<string>();
        }
    }

    internal static class FolderStore
    {
        public static string FilePath
        {
            get { return Path.Combine(ConfigStore.Dir, "folders.json"); }
        }

        public static List<AppFolder> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<AppFolder>();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                List<AppFolder> list = ser.Deserialize<List<AppFolder>>(File.ReadAllText(FilePath, System.Text.Encoding.UTF8));
                if (list == null) return new List<AppFolder>();
                foreach (AppFolder f in list)
                {
                    if (f.Members == null) f.Members = new List<string>();
                    if (string.IsNullOrEmpty(f.Name)) f.Name = "文件夹";
                }
                return list;
            }
            catch (Exception ex)
            {
                Program.Log("folders: load failed " + ex.Message);
                return new List<AppFolder>();
            }
        }

        public static void Save(List<AppFolder> list)
        {
            try
            {
                if (!Directory.Exists(ConfigStore.Dir)) Directory.CreateDirectory(ConfigStore.Dir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(list);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                Program.Log("folders: saved " + list.Count.ToString() + " folder(s)");
            }
            catch (Exception ex)
            {
                Program.Log("folders: save failed " + ex.Message);
            }
        }

        public static string NewId()
        {
            return "f" + DateTime.Now.Ticks.ToString();
        }
    }

    // 点文件夹图标后弹出的小面板（毛玻璃 + 圆角，和小组件同一套做法）
    internal class FolderPopup : Window
    {
        private const double TileW = 78;
        private const double TileH = 92;
        private const double PadX = 14;

        private static FolderPopup current;
        private string folderId;
        private bool shown;
        private bool entered;
        private DispatcherTimer closeTimer;

        public static bool IsOpen
        {
            get { return current != null; }
        }

        public static void CloseCurrent()
        {
            if (current == null) return;
            FolderPopup p = current;
            current = null;
            try { p.shown = false; p.Close(); }
            catch { }
        }

        public static void Open(DockWindow dock, AppFolder folder, FrameworkElement anchor)
        {
            if (current != null && current.folderId == folder.Id)
            {
                CloseCurrent();
                return;
            }
            CloseCurrent();

            List<AppItem> members = new List<AppItem>();
            List<AppItem> pool = dock.AllApps;
            if (pool != null)
            {
                foreach (string name in folder.Members)
                {
                    foreach (AppItem it in pool)
                    {
                        if (it != null && it.Name == name) { members.Add(it); break; }
                    }
                }
            }
            if (members.Count == 0)
            {
                Program.Log("folder popup: '" + folder.Name + "' has no resolvable members");
                return;
            }

            int cols = members.Count < 4 ? members.Count : 4;
            int rows = (members.Count + cols - 1) / cols;
            double w = cols * TileW + PadX * 2;
            double h = rows * TileH + 46;

            FolderPopup p = new FolderPopup();
            p.folderId = folder.Id;
            p.WindowStyle = WindowStyle.None;
            p.ResizeMode = ResizeMode.NoResize;
            p.AllowsTransparency = false;
            p.ShowInTaskbar = false;
            p.ShowActivated = false;
            p.Topmost = true;
            p.SizeToContent = SizeToContent.Manual;
            p.Width = w;
            p.Height = h;

            // 摆到文件夹图标正上方
            Point corner = new Point(0, 0);
            try { corner = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0)); }
            catch { }
            DpiScale dpi = VisualTreeHelper.GetDpi(anchor);
            double scale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double cx = corner.X / scale;
            double cy = corner.Y / scale;
            p.Left = Math.Round(cx - w / 2);
            p.Top = Math.Round(cy - h - 10);
            if (p.Top < 8) p.Top = 8;
            if (p.Left < 8) p.Left = 8;
            double maxX = SystemParameters.VirtualScreenWidth - w - 8;
            if (p.Left > maxX) p.Left = maxX;

            Grid root = new Grid();
            Image backdrop = new Image();
            backdrop.Stretch = Stretch.Fill;
            backdrop.IsHitTestVisible = false;
            root.Children.Add(backdrop);

            Border tint = new Border();
            tint.CornerRadius = new CornerRadius(18);
            tint.Background = Glass.TintedGradient(0x30, 0x66);
            tint.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            tint.BorderThickness = new Thickness(1);
            root.Children.Add(tint);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(PadX, 10, PadX, 10);
            TextBlock title = new TextBlock();
            title.Text = folder.Name;
            title.FontSize = 12;
            title.Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xD6, 0xE2));
            title.Margin = new Thickness(2, 0, 0, 6);
            body.Children.Add(title);

            WrapPanel grid = new WrapPanel();
            grid.Orientation = Orientation.Horizontal;
            foreach (AppItem it in members)
            {
                grid.Children.Add(p.MakeTile(dock, it));
            }
            body.Children.Add(grid);
            root.Children.Add(body);
            p.Content = root;

            p.closeTimer = new DispatcherTimer();
            p.closeTimer.Interval = TimeSpan.FromMilliseconds(450);
            p.closeTimer.Tick += delegate(object s, EventArgs e)
            {
                p.closeTimer.Stop();
                if (p.IsMouseOver) return;
                CloseCurrent();
            };
            p.MouseLeave += delegate { if (p.entered && p.closeTimer != null) p.closeTimer.Start(); };
            p.MouseEnter += delegate { p.entered = true; if (p.closeTimer != null) p.closeTimer.Stop(); };

            current = p;
            p.shown = true;
            try
            {
                p.Show();
                BitmapSource src = Glass.CaptureRect(p, p.Left, p.Top, w, h);
                if (src != null) backdrop.Source = src;
                Native.ApplyRoundedRegion(p, 28);
            }
            catch (Exception ex)
            {
                Program.Log("folder popup: show failed " + ex.Message);
            }
            Program.Log("folder popup: '" + folder.Name + "' " + members.Count.ToString() + " item(s) at " + p.Left.ToString("0") + "," + p.Top.ToString("0") + " " + w.ToString("0") + "x" + h.ToString("0"));
        }

        private FrameworkElement MakeTile(DockWindow dock, AppItem item)
        {
            Border tile = new Border();
            tile.Width = TileW - 4;
            tile.Height = TileH - 4;
            tile.CornerRadius = new CornerRadius(10);
            tile.Background = Brushes.Transparent;
            tile.Cursor = Cursors.Hand;
            tile.ToolTip = item.Name;
            tile.Margin = new Thickness(2);

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            Image img = new Image();
            bool usedPack;
            FrameworkElement iconEl = IconTile.Build(item, 44, out usedPack);
            iconEl.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(iconEl);
            TextBlock name = new TextBlock();
            name.Text = item.Name;
            name.FontSize = 10;
            name.Foreground = Brushes.White;
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.MaxWidth = TileW - 10;
            name.Margin = new Thickness(0, 6, 0, 0);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(name);
            tile.Child = sp;

            tile.MouseEnter += delegate
            {
                tile.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            };
            tile.MouseLeave += delegate { tile.Background = Brushes.Transparent; };
            tile.MouseLeftButtonUp += delegate
            {
                Program.Log("folder popup: launch " + item.Name);
                CloseCurrent();
                string msg;
                Launcher.ActivateOrLaunch(item, out msg);
                if (dock != null) dock.HideDock();
            };
            return tile;
        }
    }
}
