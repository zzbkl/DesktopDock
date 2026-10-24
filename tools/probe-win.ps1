Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
'@

$rows = New-Object System.Collections.ArrayList
$cb = [W.U+EnumProc]{
    param($h, $l)
    if ([W.U]::IsWindowVisible($h)) {
        $owner = [W.U]::GetWindow($h, 4)
        $sb = New-Object System.Text.StringBuilder 260
        [void][W.U]::GetWindowText($h, $sb, 260)
        $pid2 = 0
        [void][W.U]::GetWindowThreadProcessId($h, [ref]$pid2)
        $pn = ''
        try { $pn = (Get-Process -Id $pid2 -ErrorAction Stop).ProcessName } catch { }
        $ex = [W.U]::GetWindowLong($h, -20)
        [void]$rows.Add(('{0,-28} pid={1,-7} owner={2} ex=0x{3:X} title={4}' -f $pn, $pid2, $owner, $ex, $sb.ToString()))
    }
    return $true
}
[void][W.U]::EnumWindows($cb, [IntPtr]::Zero)
$rows | Where-Object { $_ -match '7zFM|chrome|Codex|Steam|DeskBox' } | Select-Object -First 20
"---- total visible windows: " + $rows.Count
