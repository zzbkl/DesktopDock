Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WT2 {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);

  public static IntPtr FindChrome() {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      if (found != IntPtr.Zero) return true;
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      if (cls.ToString() == "Chrome_WidgetWin_1" && IsWindowVisible(h)) {
        RECT r; GetWindowRect(h, out r);
        if ((r.R - r.L) > 600) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  public static string Report(uint pid) {
    var sb = new StringBuilder();
    var fg = GetForegroundWindow(); var fcls = new StringBuilder(256); GetClassName(fg, fcls, 256);
    sb.Append("fg=" + fcls + " | ");
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint p2; GetWindowThreadProcessId(h, out p2);
      if (p2 == pid && IsWindowVisible(h)) {
        RECT r; GetWindowRect(h, out r);
        sb.Append("vis[" + r.L + "," + r.T + " " + (r.R-r.L) + "x" + (r.B-r.T) + "] ");
      }
      return true;
    }, IntPtr.Zero);
    return sb.ToString();
  }
}
'@
$p = (Get-Process DesktopDock).Id
$chrome = [WT2]::FindChrome()
"chrome hwnd=$chrome"
if ($chrome -ne [IntPtr]::Zero) { [WT2]::ShowWindow($chrome, 9) | Out-Null; [WT2]::SetForegroundWindow($chrome) | Out-Null }
Start-Sleep -Seconds 2
"after-app: " + [WT2]::Report([uint32]$p)
