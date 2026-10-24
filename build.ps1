# DesktopDock build — 系统自带 csc（C# 5）+ net48 WPF 程序集，零安装、零 NuGet
$ErrorActionPreference = 'Stop'

$asm  = Join-Path $env:WINDIR 'Microsoft.NET\assembly'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root = 'D:\DesktopDock'
$src  = Join-Path $root 'src'
$out  = Join-Path $root 'build'
$exe  = Join-Path $out 'DesktopDock.exe'

$refList = @(
  'GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll',
  'GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll',
  'GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll',
  'GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll'
)

New-Item -ItemType Directory -Force -Path $out | Out-Null

$compileArgs = New-Object System.Collections.Generic.List[string]
$compileArgs.Add('/nologo')
$compileArgs.Add('/target:winexe')
$compileArgs.Add('/platform:anycpu')
$compileArgs.Add('/codepage:65001')
$compileArgs.Add('/optimize+')
$compileArgs.Add('/debug-')
$compileArgs.Add(('/out:' + $exe))
$compileArgs.Add(('/win32manifest:' + (Join-Path $src 'app.manifest')))
foreach ($rel in $refList) {
  $full = Join-Path $asm $rel
  if (-not (Test-Path $full)) { throw ("missing ref: " + $full) }
  $compileArgs.Add(('/r:' + $full))
}
Get-ChildItem -Path $src -Filter *.cs | ForEach-Object { $compileArgs.Add($_.FullName) }

& $csc $compileArgs.ToArray()
$code = $LASTEXITCODE
if ($code -ne 0) { Write-Output ("BUILD FAILED exit=" + $code); exit $code }
Write-Output ("BUILD OK -> " + $exe)
