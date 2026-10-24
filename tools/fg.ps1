param([string]$Name = 'notepad', [string]$Action = 'focus')
# 抢前台 + 可选最大化/还原，用来验证 dock 的遮挡让位逻辑
Add-Type -Namespace FG -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
'@

$script:rows = New-Object System.Collections.ArrayList
$cb = [FG.U+EnumProc]{
    param($h, $l)
    if ([FG.U]::IsWindowVisible($h) -and [FG.U]::GetWindow($h, 4) -eq [IntPtr]::Zero) {
        $procId = 0
        [void][FG.U]::GetWindowThreadProcessId($h, [ref]$procId)
        $pn = ''
        try { $pn = (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { $pn = '' }
        [void]$script:rows.Add([pscustomobject]@{ Handle = $h; Proc = $pn })
    }
    return $true
}
[void][FG.U]::EnumWindows($cb, [IntPtr]::Zero)

$hit = @($script:rows | Where-Object { $_.Proc -eq $Name })
if ($hit.Count -eq 0) { "no window for $Name"; exit 1 }
$target = $hit[0].Handle

if ($Action -eq 'max') { [void][FG.U]::ShowWindow($target, 3); "maximized" }
elseif ($Action -eq 'restore') { [void][FG.U]::ShowWindow($target, 9); "restored" }
elseif ($Action -eq 'min') { [void][FG.U]::ShowWindow($target, 6); "minimized" }

# 假 ALT 按键解除前台锁定，再 AttachThreadInput
[FG.U]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[FG.U]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60

$fg = [FG.U]::GetForegroundWindow()
$fgPid = 0
$fgThread = [FG.U]::GetWindowThreadProcessId($fg, [ref]$fgPid)
$me = [FG.U]::GetCurrentThreadId()
[void][FG.U]::AttachThreadInput($me, $fgThread, $true)
[void][FG.U]::BringWindowToTop($target)
$ok = [FG.U]::SetForegroundWindow($target)
[void][FG.U]::AttachThreadInput($me, $fgThread, $false)

$now = [FG.U]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[void][FG.U]::GetClassName($now, $sb, 256)
"target={0} setfg={1} now={2} cls={3}" -f $target, $ok, $now, $sb.ToString()
