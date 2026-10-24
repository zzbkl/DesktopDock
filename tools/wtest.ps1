param([string]$Step)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class K {
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static void WinD() { keybd_event(0x5B,0,0,IntPtr.Zero); keybd_event(0x44,0,0,IntPtr.Zero); keybd_event(0x44,0,2,IntPtr.Zero); keybd_event(0x5B,0,2,IntPtr.Zero); }
  public static void Desktop() { IntPtr p = FindWindow("Progman", null); if (p != IntPtr.Zero) SetForegroundWindow(p); }
}
'@
function Shot([string]$out, [int]$x, [int]$y, [int]$w, [int]$h) {
  $ps = "powershell.exe"
  & $ps -NoProfile -ExecutionPolicy Bypass -File D:\DesktopDock\tools\shot.ps1 | Out-Null
  $src = [System.Drawing.Image]::FromFile('D:\DesktopDock\shots\shot.png')
  $d = New-Object System.Drawing.Bitmap $w, $h
  $gd = [System.Drawing.Graphics]::FromImage($d)
  $gd.DrawImage($src, (New-Object System.Drawing.Rectangle 0,0,$w,$h), (New-Object System.Drawing.Rectangle $x,$y,$w,$h), [System.Drawing.GraphicsUnit]::Pixel)
  $d.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $gd.Dispose(); $d.Dispose(); $src.Dispose()
}
if ($Step -eq 'desk') { [K]::Desktop(); [K]::WinD() }
$log = Join-Path $env:APPDATA 'DesktopDock\dock.log'
Start-Sleep -Seconds 3
Shot 'D:\DesktopDock\shots\wtest.png' 700 0 1200 400
Get-Content $log -Encoding UTF8 | Select-String 'widgets' | Select-Object -Last 4 | ForEach-Object { $_.Line }
