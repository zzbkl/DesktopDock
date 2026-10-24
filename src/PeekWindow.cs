using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopDock
{
    // 被窗口遮挡时留在屏幕上的那条"把手"：很细、不抢焦点、鼠标碰到就把 dock 探出来。
    internal class PeekWindow : Window
    {
        public const double StripWidth = 220;
        public const double StripHeight = 8;

        public DockWindow Dock { get; set; }

        private Border body;

        public PeekWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = false;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = StripWidth;
            Height = StripHeight;
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x44));
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            body = new Border();
            body.CornerRadius = new CornerRadius(4);
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0);
            g.EndPoint = new Point(0, 1);
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xB2, 0xB6, 0xC2), 0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x6C, 0x70, 0x7C), 1));
            body.Background = g;
            body.Margin = new Thickness(0);
            Content = body;

            // 只认点击：悬停太容易在贴着屏幕底边划鼠标时误触
            MouseLeftButtonUp += delegate
            {
                if (Dock != null) Dock.ShowDock(false);   // 点一下探出，但不由它钉住；鼠标离开后自动收回
            };
            Cursor = Cursors.Hand;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Native.MakeNoActivate(this);
        }

        public void Place()
        {
            double waBottom = SystemParameters.WorkArea.Bottom;
            double band = Native.TaskbarBandDip(new WindowInteropHelper(this).Handle);
            double top = waBottom - band - StripHeight - 2;
            Left = Math.Round((SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2.0));
            Top = Math.Round(top);
            Program.Log("peek place: waBottom=" + waBottom.ToString("0.##") + " band=" + band.ToString("0.##")
                + " top=" + Top.ToString("0.##") + " left=" + Left.ToString("0.##"));
        }
    }
}

