Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class AW {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  public static string Styles(IntPtr h) {
    int ex = GetWindowLong(h, -20);
    int st = GetWindowLong(h, -16);
    var sb = new StringBuilder();
    if ((ex & 0x00000008) != 0) sb.Append("TOPMOST ");
    if ((ex & 0x00000080) != 0) sb.Append("TOOLWINDOW ");
    if ((ex & 0x08000000) != 0) sb.Append("NOACTIVATE ");
    if ((ex & 0x00080000) != 0) sb.Append("LAYERED ");
    if ((ex & 0x00040000) != 0) sb.Append("APPWINDOW ");
    if ((ex & 0x00000020) != 0) sb.Append("TRANSPARENT ");
    if ((st & 0x08000000) != 0) sb.Append("WS_DISABLED ");
    if ((st & 0x10000000) != 0) sb.Append("WS_VISIBLE ");
    if ((st & 0x04000000) != 0) sb.Append("WS_CLIPSIBLINGS ");
    if ((st & 0x02000000) != 0) sb.Append("WS_CLIPCHILDREN ");
    return sb.ToString().Trim();
  }

  public static string Dump(uint pid) {
    var sb = new StringBuilder();
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint p2; GetWindowThreadProcessId(h, out p2);
      if (p2 == pid) {
        RECT r; GetWindowRect(h, out r);
        var cls = new StringBuilder(256); GetClassName(h, cls, 256);
        var tx = new StringBuilder(256); GetWindowText(h, tx, 256);
        int cloak = 0; DwmGetWindowAttribute(h, 14, out cloak, 4);
        IntPtr owner = GetWindow(h, 4); // GW_OWNER
        bool vis = IsWindowVisible(h);
        if (vis || r.R - r.L > 40) {
          sb.AppendLine((vis ? "VIS " : "hid ") + cls + " [" + r.L + "," + r.T + " " + (r.R-r.L) + "x" + (r.B-r.T) +
            "] cloak=" + cloak + " owner=0x" + owner.ToString("X") + " ex={" + Styles(h) + "} \"" + tx + "\"");
        }
      }
      return true;
    }, IntPtr.Zero);
    return sb.ToString();
  }
  public static string WorkArea() {
    RECT r; SystemParametersInfo(0x0030, 0, out r, 0);
    return "work=" + r.L + "," + r.T + "," + r.R + "," + r.B;
  }
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint act, uint p, out RECT r, uint f);
}
'@
$p = Get-Process AirDeck
"PID=$($p.Id)  " + [AW]::WorkArea()
[AW]::Dump([uint32]$p.Id)
