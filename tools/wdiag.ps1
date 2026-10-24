Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WT {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);

  public static void WinD() { keybd_event(0x5B,0,0,IntPtr.Zero); keybd_event(0x44,0,0,IntPtr.Zero); keybd_event(0x44,0,2,IntPtr.Zero); keybd_event(0x5B,0,2,IntPtr.Zero); }

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
"step0: " + [WT]::Report([uint32]$p)
[WT]::WinD()
Start-Sleep -Milliseconds 200
"step1: " + [WT]::Report([uint32]$p)
Start-Sleep -Seconds 2
"step2: " + [WT]::Report([uint32]$p)
