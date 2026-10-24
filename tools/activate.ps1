param([string]$Name = 'chrome')
Add-Type -Namespace Win -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
'@

$script:rows = New-Object System.Collections.ArrayList
$cb = [Win.U+EnumProc]{
    param($h, $l)
    if ([Win.U]::IsWindowVisible($h) -and [Win.U]::GetWindow($h, 4) -eq [IntPtr]::Zero) {
        $procId = 0
        [void][Win.U]::GetWindowThreadProcessId($h, [ref]$procId)
        $pn = ''
        try { $pn = (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { $pn = '' }
        [void]$script:rows.Add([pscustomobject]@{ Handle = $h; Proc = $pn })
    }
    return $true
}
[void][Win.U]::EnumWindows($cb, [IntPtr]::Zero)

$matches = @($script:rows | Where-Object { $_.Proc -eq $Name })
"windows total={0}, matched {1}={2}" -f $script:rows.Count, $Name, $matches.Count
if ($matches.Count -eq 0) { $script:rows | ForEach-Object { $_.Proc } | Sort-Object -Unique | Out-String; exit 1 }

$target = $matches[0].Handle
$fg = [Win.U]::GetForegroundWindow()
$fgPid = 0
$fgThread = [Win.U]::GetWindowThreadProcessId($fg, [ref]$fgPid)
$me = [Win.U]::GetCurrentThreadId()
[void][Win.U]::AttachThreadInput($me, $fgThread, $true)
[void][Win.U]::BringWindowToTop($target)
$ok = [Win.U]::SetForegroundWindow($target)
[void][Win.U]::AttachThreadInput($me, $fgThread, $false)
"target={0} setfg={1} now={2}" -f $target, $ok, [Win.U]::GetForegroundWindow()
