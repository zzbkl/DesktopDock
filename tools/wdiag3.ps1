Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WT3 {
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
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
  public static void WinD() { keybd_event(0x5B,0,0,IntPtr.Zero); keybd_event(0x44,0,0,IntPtr.Zero); keybd_event(0x44,0,2,IntPtr.Zero); keybd_event(0x5B,0,2,IntPtr.Zero); }
  public static IntPtr ByClass(string want) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      if (found != IntPtr.Zero) return true;
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      if (cls.ToString() == want && IsWindowVisible(h)) {
        RECT r; GetWindowRect(h, out r);
        if ((r.R - r.L) > 300) { found = h; return false; }
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
$dockPid = (Get-Process DesktopDock).Id
Start-Process notepad
Start-Sleep -Seconds 2
$np = [WT3]::ByClass("Notepad")
"notepad hwnd=$np"
if ($np -ne [IntPtr]::Zero) { [WT3]::ShowWindow($np, 9) | Out-Null; [WT3]::SetForegroundWindow($np) | Out-Null }
Start-Sleep -Seconds 2
"with-notepad: " + [WT3]::Report([uint32]$dockPid)
[WT3]::WinD()
Start-Sleep -Seconds 2
"back-to-desk: " + [WT3]::Report([uint32]$dockPid)
Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force
