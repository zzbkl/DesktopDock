Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace DockAct -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
public const uint LEFTDOWN = 2;
public const uint LEFTUP = 4;
'@
[void][DockAct.N]::SetProcessDPIAware()

$proc = Get-Process DesktopDock -ErrorAction SilentlyContinue
if (-not $proc) { throw 'DesktopDock not running' }
$targetPid = [int]$proc.Id

# 在回调里直接取"最靠下"的可见窗口 = dock，避免对象排序问题
$script:walk = @()
$script:dockL = 0; $script:dockT = 0; $script:dockR = 0; $script:dockB = 0
$cb = [DockAct.N+EnumProc]{ param($hw,$l)
  $wpid = 0; [void][DockAct.N]::GetWindowThreadProcessId($hw, [ref]$wpid)
  if ($wpid -eq $targetPid -and [DockAct.N]::IsWindowVisible($hw)) {
    $r = New-Object DockAct.N+RECT
    [void][DockAct.N]::GetWindowRect($hw, [ref]$r)
    $script:walk += ("  ({0},{1})-({2},{3}) {4}x{5}" -f $r.L,$r.T,$r.R,$r.B,($r.R-$r.L),($r.B-$r.T))
    if ($r.B -gt $script:dockB) {
      $script:dockL = $r.L; $script:dockT = $r.T; $script:dockR = $r.R; $script:dockB = $r.B
    }
  }
  return $true }
[void][DockAct.N]::EnumWindows($cb, [IntPtr]::Zero)

"visible windows before click:"
$script:walk | ForEach-Object { $_ }
"dock rect: ({0},{1})-({2},{3})" -f $script:dockL, $script:dockT, $script:dockR, $script:dockB

$dockH = $script:dockB - $script:dockT
$cx = $script:dockL + 54
$cy = $script:dockT + [int]($dockH / 2)
"target grid button: {0},{1}" -f $cx, $cy

[void][DockAct.N]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 250
[DockAct.N]::mouse_event([DockAct.N]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 90
[DockAct.N]::mouse_event([DockAct.N]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1400

$script:walk2 = @()
$cb2 = [DockAct.N+EnumProc]{ param($hw,$l)
  $wpid = 0; [void][DockAct.N]::GetWindowThreadProcessId($hw, [ref]$wpid)
  if ($wpid -eq $targetPid -and [DockAct.N]::IsWindowVisible($hw)) {
    $r = New-Object DockAct.N+RECT
    [void][DockAct.N]::GetWindowRect($hw, [ref]$r)
    $script:walk2 += ("  ({0},{1})-({2},{3}) {4}x{5}" -f $r.L,$r.T,$r.R,$r.B,($r.R-$r.L),($r.B-$r.T))
  }
  return $true }
[void][DockAct.N]::EnumWindows($cb2, [IntPtr]::Zero)
"visible windows after click:"
$script:walk2 | ForEach-Object { $_ }

$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
$bmp.Save('D:\DesktopDock\shots\after-click.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"saved after-click.png {0}x{1}" -f $vs.Width, $vs.Height
