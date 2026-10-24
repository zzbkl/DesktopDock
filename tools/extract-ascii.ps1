$path = 'C:\Users\asus\AppData\Local\Programs\AirDeck\AirDeck.dll'
$fs = [System.IO.File]::OpenRead($path)
$buf = New-Object byte[] (2MB)
$enc = [System.Text.Encoding]::GetEncoding(28591)
$out = New-Object System.Collections.Generic.HashSet[string]
while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
  $s = $enc.GetString($buf, 0, $n)
  foreach ($m in [regex]::Matches($s, '[\x20-\x7E]{5,}')) { [void]$out.Add($m.Value) }
}
$fs.Close()
$out | Sort-Object | Set-Content 'D:\DesktopDock\shots\airdeck-ascii.txt' -Encoding UTF8
"ascii: " + $out.Count
