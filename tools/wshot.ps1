Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WT4 {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
  public static void WinD() { keybd_event(0x5B,0,0,IntPtr.Zero); keybd_event(0x44,0,0,IntPtr.Zero); keybd_event(0x44,0,2,IntPtr.Zero); keybd_event(0x5B,0,2,IntPtr.Zero); }
  public static IntPtr ByClass(string want) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      if (found != IntPtr.Zero) return true;
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      if (cls.ToString() == want && IsWindowVisible(h)) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@
Add-Type -AssemblyName System.Drawing
function Snap([string]$out) {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DesktopDock\tools\shot.ps1 | Out-Null
  $src = [System.Drawing.Image]::FromFile('D:\DesktopDock\shots\shot.png')
  $d = New-Object System.Drawing.Bitmap 1200, 460
  $gd = [System.Drawing.Graphics]::FromImage($d)
  $gd.DrawImage($src, (New-Object System.Drawing.Rectangle 0,0,1200,460), (New-Object System.Drawing.Rectangle 560,0,1200,460), [System.Drawing.GraphicsUnit]::Pixel)
  $d.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $gd.Dispose(); $d.Dispose(); $src.Dispose()
}
# 1) 桌面：小组件在
[WT4]::WinD()
Start-Sleep -Seconds 3
Snap 'D:\DesktopDock\shots\w-on-desk.png'
# 2) 记事本窗口顶上来：小组件必须消失
Start-Process notepad
Start-Sleep -Seconds 2
$np = [WT4]::ByClass("Notepad")
if ($np -ne [IntPtr]::Zero) {
  [WT4]::MoveWindow($np, 620, 0, 1100, 700, $true) | Out-Null
  [WT4]::ShowWindow($np, 9) | Out-Null
  [WT4]::SetForegroundWindow($np) | Out-Null
}
Start-Sleep -Seconds 3
Snap 'D:\DesktopDock\shots\w-behind-app.png'
Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force
"captured"
