Add-Type -Namespace Probe1 -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
'@
$ok = [Probe1.N]::SetProcessDPIAware()
"SetProcessDPIAware: $ok"
[void][Probe1.N]::SetCursorPos(872, 1545)
$p = New-Object Probe1.N+POINT
[void][Probe1.N]::GetCursorPos([ref]$p)
"cursor now: {0},{1}" -f $p.x, $p.y
$h = [Probe1.N]::WindowFromPoint($p)
$wpid = 0; [void][Probe1.N]::GetWindowThreadProcessId($h, [ref]$wpid)
$sb = New-Object System.Text.StringBuilder 256
[void][Probe1.N]::GetClassName($h, $sb, 256)
"hit: hwnd={0} pid={1} cls={2}" -f $h, $wpid, $sb.ToString()
"dd pid: " + (Get-Process DesktopDock).Id
Add-Type -AssemblyName System.Windows.Forms
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
"virtual screen: {0}x{1}" -f $vs.Width, $vs.Height
