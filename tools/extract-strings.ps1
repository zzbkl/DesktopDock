$path = 'C:\Users\asus\AppData\Local\Programs\AirDeck\AirDeck.dll'
$fs = [System.IO.File]::OpenRead($path)
$buf = New-Object byte[] (2MB)
$sb = New-Object System.Collections.Generic.List[string]
$carry = New-Object System.Text.StringBuilder
$enc = [System.Text.Encoding]::Unicode
$out = New-Object System.Collections.Generic.HashSet[string]
while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
  $s = $enc.GetString($buf, 0, $n - ($n % 2))
  foreach ($m in [regex]::Matches($s, '[\u0020-\u007E\u4E00-\u9FFF]{4,}')) {
    $v = $m.Value
    if ($v.Length -ge 4) { [void]$out.Add($v) }
  }
}
$fs.Close()
$out | Sort-Object | Set-Content 'D:\DesktopDock\shots\airdeck-strings.txt' -Encoding UTF8
"total strings: " + $out.Count
