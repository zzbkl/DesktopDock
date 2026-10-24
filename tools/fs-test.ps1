Add-Type -Namespace Win -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
'@
$script:fg = [Win.U]::GetForegroundWindow()
function Show-Fg($tag) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][Win.U]::GetClassName($script:fg, $sb, 256)
    Write-Output ("{0,-16} handle={1} class={2}" -f $tag, $script:fg, $sb.ToString())
}
Show-Fg 'current'
[void][Win.U]::ShowWindow($script:fg, 6)
Start-Sleep -Milliseconds 1500
$after = [Win.U]::GetForegroundWindow()
$sb2 = New-Object System.Text.StringBuilder 256
[void][Win.U]::GetClassName($after, $sb2, 256)
Write-Output ("after minimize   handle={0} class={1}" -f $after, $sb2.ToString())
Start-Sleep -Seconds 2
[void][Win.U]::ShowWindow($script:fg, 9)
Start-Sleep -Milliseconds 1500
$after2 = [Win.U]::GetForegroundWindow()
$sb3 = New-Object System.Text.StringBuilder 256
[void][Win.U]::GetClassName($after2, $sb3, 256)
Write-Output ("after restore    handle={0} class={1}" -f $after2, $sb3.ToString())
