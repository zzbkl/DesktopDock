using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDock
{
    // Win11 的 DWM 毛玻璃：给窗口挂上"系统背景材质"（acrylic），DWM 会实时把窗口后面的内容模糊后画进来，
    // 前提是 WPF 的客户区真的透明（CompositionTarget.BackgroundColor = 全透明），
    // 并且窗口不能开 AllowsTransparency（那个会踢掉硬件加速，GlassBoard 就是栽在这）。
    internal static class Glass
    {
        // 抓屏模糊的两个可调参数（对应 AirDeck 的 DockFrostRadius / DockFrostDownscale）
        public static double BlurDip = 16;
        public static int Downscale = 2;

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;
        private const int DWMSBT_TRANSIENTWINDOW = 3;   // 亚克力（acrylic）
        private const int DWMSBT_MAINWINDOW = 2;        // 云母（mica）

        public static bool TryAcrylic(Window w, bool mica)
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return false;

            int corner = DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            int backdrop = mica ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
            int hr = Native.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            if (hr != 0) return false;

            HwndSource src = PresentationSource.FromVisual(w) as HwndSource;
            if (src != null && src.CompositionTarget != null)
            {
                src.CompositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
            }
            return true;
        }

        // 深色系的两个渐变（带一点透明，让底下的模糊透上来）
        public static LinearGradientBrush TintedGradient(byte topAlpha, byte bottomAlpha)
        {
            LinearGradientBrush grad = new LinearGradientBrush();
            grad.StartPoint = new Point(0, 0);
            grad.EndPoint = new Point(0, 1);
            // 顶部一道很轻的白色 specular：玻璃的"透亮"主要来自这 10px 的亮边。
            // 它加在 tint 里（图标之下），而不是叠一层白蒙版 —— 叠在上面会把图标洗白。
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(topAlpha, 0x2A, 0x2C, 0x34), 0.12));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(bottomAlpha, 0x14, 0x15, 0x1A), 1));
            return grad;
        }

        // WPF 的客户区不参与 DWM 合成，光设系统背景材质透不出来 —— 那就自己抓屏再高斯模糊。
        // 只在窗口"收起状态"抓（抓的是墙纸/窗口，不是自己），拿到的是真实背景的虚化版本。
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static System.Windows.Media.Imaging.BitmapSource Capture(Window w)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return null;
                RECT r;
                if (!GetWindowRect(hwnd, out r)) return null;
                int pxW = r.Right - r.Left;
                int pxH = r.Bottom - r.Top;
                if (pxW <= 0 || pxH <= 0 || pxW > 4000 || pxH > 3000) return null;

                // 性能：按 1/2 分辨率抓回来再模糊（视觉上没差别，模糊的像素量只有 1/4，
                // 内存也从 900x620 的 2.2MB 降到 0.55MB）
                int ds = Downscale < 1 ? 1 : (Downscale > 4 ? 4 : Downscale);
                int halfW = Math.Max(1, pxW / ds);
                int halfH = Math.Max(1, pxH / ds);

                using (System.Drawing.Bitmap full = new System.Drawing.Bitmap(pxW, pxH))
                {
                    using (System.Drawing.Graphics gf = System.Drawing.Graphics.FromImage(full))
                    {
                        gf.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(pxW, pxH));
                    }
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(halfW, halfH))
                    {
                        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(full, new System.Drawing.Rectangle(0, 0, halfW, halfH));
                        }
                        IntPtr hbmp = bmp.GetHbitmap();
                        try
                        {
                            System.Windows.Media.Imaging.BitmapSource src =
                                Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                                    BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            return src;
                        }
                        finally
                        {
                            Native.DeleteObject(hbmp);
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        // 按"我们自己给的矩形"抓屏：窗口还没 Show（ActualWidth=0）时也能抓到正确区域，
        // 而且抓的是窗口隐藏状态下的屏幕，不会把自己拍进去。
        public static BitmapSource CaptureRect(Window w, double leftDip, double topDip, double widthDip, double heightDip)
        {
            try
            {
                double sx = Native.DpiScaleX(w), sy = Native.DpiScaleY(w);
                int pxX = (int)Math.Round(leftDip * sx);
                int pxY = (int)Math.Round(topDip * sy);
                int pxW = (int)Math.Round(widthDip * sx);
                int pxH = (int)Math.Round(heightDip * sy);
                if (pxW <= 0 || pxH <= 0 || pxW > 6000 || pxH > 4000) return null;

                int ds = Downscale < 1 ? 1 : (Downscale > 4 ? 4 : Downscale);
                int halfW = Math.Max(1, pxW / ds);
                int halfH = Math.Max(1, pxH / ds);
                using (System.Drawing.Bitmap full = new System.Drawing.Bitmap(pxW, pxH))
                {
                    using (System.Drawing.Graphics gf = System.Drawing.Graphics.FromImage(full))
                    {
                        gf.CopyFromScreen(pxX, pxY, 0, 0, new System.Drawing.Size(pxW, pxH));
                    }
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(halfW, halfH))
                    {
                        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(full, new System.Drawing.Rectangle(0, 0, halfW, halfH));
                        }
                        IntPtr hbmp = bmp.GetHbitmap();
                        try
                        {
                            BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            return src;
                        }
                        finally { Native.DeleteObject(hbmp); }
                    }
                }
            }
            catch { return null; }
        }

        // 模糊效果复用：以前每贴一次背景图就 new 一个 BlurEffect，
        // 打开一次要重新建一遍效果图、旧对象等 GC（WPF 效果图是 unmanaged 的，回收不及时就抖内存）。
        // 现在同一个 Image 只持有一个 BlurEffect，只改 Radius。
        private static void SetBlur(Image img)
        {
            if (img == null) return;
            System.Windows.Media.Effects.BlurEffect be = img.Effect as System.Windows.Media.Effects.BlurEffect;
            if (be == null || be.IsFrozen)
            {
                be = new System.Windows.Media.Effects.BlurEffect();
                be.KernelType = System.Windows.Media.Effects.KernelType.Gaussian;
                img.Effect = be;
            }
            // 图只有一半分辨率，这里 16 的模糊半径放大回去约等于原来的 32
            be.Radius = BlurDip;
        }

        public static void ApplyTo(Image img, Window w, BitmapSource src, double widthDip, double heightDip)
        {
            if (img == null || src == null) return;
            img.Source = src;
            img.Stretch = Stretch.Fill;
            img.Width = widthDip;
            img.Height = heightDip;
            SetBlur(img);
        }

        // 把抓到的背景铺成一张模糊图，尺寸按 DIP 对齐窗口
        public static void ApplyTo(Image img, Window w)
        {
            if (img == null) return;
            System.Windows.Media.Imaging.BitmapSource src = Capture(w);
            if (src == null) return;
            img.Source = src;
            img.Stretch = Stretch.Fill;
            img.Width = w.ActualWidth;
            img.Height = w.ActualHeight;
            SetBlur(img);
        }

        // ---- 预烘焙模糊：把 BlurEffect 从"每次重绘算一遍"挪到"抓完算一次" ----
        // WPF 的 BlurEffect 会让那一层退回软件渲染，窗口淡入/移动/内容变化时每帧都要重算高斯模糊，
        // 帧率天然上不去。这里在窗口还没上屏时用 RenderTargetBitmap 把模糊结果烘成一张位图：
        // 之后上屏只是普通位图贴图，Effect = null，动画期间零模糊开销，帧率全靠合成器。
        // 只碰 BitmapSource / DrawingVisual，可在后台线程调用。
        public static BitmapSource BakeBlur(BitmapSource src, double widthDip)
        {
            if (src == null || widthDip < 2) return null;
            try
            {
                int w = src.PixelWidth, h = src.PixelHeight;
                if (w < 2 || h < 2 || w > 8000 || h > 6000) return null;
                double r = BlurDip * (w / widthDip);        // 与在线 BlurEffect 视觉等价的源像素半径
                if (r < 1) r = 1;
                DrawingVisual dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                {
                    dc.DrawImage(src, new Rect(0, 0, w, h));
                }
                System.Windows.Media.Effects.BlurEffect be = new System.Windows.Media.Effects.BlurEffect();
                be.KernelType = System.Windows.Media.Effects.KernelType.Gaussian;
                be.Radius = r;
                dv.Effect = be;                              // 只在这一次离屏渲染里生效
                RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        // 贴已经烘好的背景图：不挂 Effect，上屏就是一次位图拷贝
        public static void ApplyBaked(Image img, BitmapSource baked, double widthDip, double heightDip)
        {
            if (img == null || baked == null) return;
            if (img.Effect != null) img.Effect = null;
            img.Source = baked;
            img.Stretch = Stretch.Fill;
            img.Width = widthDip;
            img.Height = heightDip;
        }

        // 后台线程可调：只吃像素坐标，不碰任何 WPF 对象（Window/Visual 有线程亲和）。
        // 调用方在 UI 线程先把 DIP 换算成像素，再丢给线程池。
        public static BitmapSource CapturePx(int pxX, int pxY, int pxW, int pxH)
        {
            try
            {
                if (pxW <= 0 || pxH <= 0 || pxW > 6000 || pxH > 4000) return null;
                int ds = Downscale < 1 ? 1 : (Downscale > 4 ? 4 : Downscale);
                int halfW = Math.Max(1, pxW / ds);
                int halfH = Math.Max(1, pxH / ds);
                using (System.Drawing.Bitmap full = new System.Drawing.Bitmap(pxW, pxH))
                {
                    using (System.Drawing.Graphics gf = System.Drawing.Graphics.FromImage(full))
                    {
                        gf.CopyFromScreen(pxX, pxY, 0, 0, new System.Drawing.Size(pxW, pxH));
                    }
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(halfW, halfH))
                    {
                        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(full, new System.Drawing.Rectangle(0, 0, halfW, halfH));
                        }
                        IntPtr hbmp = bmp.GetHbitmap();
                        try
                        {
                            BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            return src;
                        }
                        finally { Native.DeleteObject(hbmp); }
                    }
                }
            }
            catch { return null; }
        }

        // 亮度：网格采样（最多 48x48 个点）。原来每次打开都把整张图转 Bgra32 再逐像素算，
        // 现在只在抓图那一次（后台线程）算好存起来，打开路径零开销。
        public static double Luminance(BitmapSource src)
        {
            try
            {
                if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return -1;
                FormatConvertedBitmap conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                int stride = w * 4;
                byte[] buf = new byte[stride * h];
                conv.CopyPixels(buf, stride, 0);
                int stepX = Math.Max(1, w / 48);
                int stepY = Math.Max(1, h / 48);
                double sum = 0;
                int n = 0;
                for (int y = 0; y < h; y += stepY)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += stepX)
                    {
                        int i = row + x * 4;
                        double b = buf[i] / 255.0, gg = buf[i + 1] / 255.0, r = buf[i + 2] / 255.0;
                        sum += 0.2126 * r + 0.7152 * gg + 0.0722 * b;
                        n++;
                    }
                }
                return n == 0 ? -1 : sum / n;
            }
            catch { return -1; }
        }
    }
}
