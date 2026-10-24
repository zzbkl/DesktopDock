param([int]$X, [int]$Y)
Add-Type -Namespace MClick -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
public const uint LEFTDOWN = 2;
public const uint LEFTUP = 4;
'@
[void][MClick.N]::SetCursorPos($X, $Y)
Start-Sleep -Milliseconds 120
[MClick.N]::mouse_event([MClick.N]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[MClick.N]::mouse_event([MClick.N]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Write-Output ("clicked " + $X + "," + $Y)
