using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDock
{
    // 统一的图标磁贴：有图标包就用包里的 png（本身已是圆角磁贴），
    // 没有就给系统图标套一个同尺寸的圆角底 + 内缩，避免"有的浮着有的贴边"的杂乱感。
    internal static class IconTile
    {
        public static FrameworkElement Build(AppItem item, double size, out bool usedPack)
        {
            usedPack = false;
            string packIcon = null;
            if (IconPack.Enabled && item != null) packIcon = IconPack.Resolve(item.Target);
            if (!string.IsNullOrEmpty(packIcon))
            {
                BitmapSource bmp = Icons.LoadPng(packIcon, 128);
                if (bmp != null)
                {
                    usedPack = true;
                    Image pimg = new Image();
                    pimg.Source = bmp;
                    pimg.Width = size;
                    pimg.Height = size;
                    pimg.Stretch = Stretch.Uniform;
                    RenderOptions.SetBitmapScalingMode(pimg, BitmapScalingMode.HighQuality);
                    return pimg;
                }
            }

            BitmapSource native = null;
            string iconPath = item == null ? null : item.IconPath;
            if (!string.IsNullOrEmpty(iconPath)) native = Icons.Load(iconPath, 128);
            if (native == null) native = Icons.Load("C:\\Windows\\System32\\shell32.dll", 128);

            Border tile = new Border();
            tile.Width = size;
            tile.Height = size;
            tile.CornerRadius = new CornerRadius(size * 0.22);
            tile.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            Image img = new Image();
            img.Source = native;
            img.Width = size * 0.74;
            img.Height = size * 0.74;
            img.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            tile.Child = img;
            return tile;
        }
    }
}
