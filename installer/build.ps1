param(
  [string]$Configuration = "Release",
  [string]$Version = "0.0.23"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publishDir = "publish/app-$Version"
$appDir = "$publishDir/app"
$distDir = "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host "== publish $Version (win-x64, self-contained) =="
Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
dotnet publish -c $Configuration -r win-x64 --self-contained true `
  -p:DebugType=none -p:DebugSymbols=false -o $appDir -v q --nologo

if (-not (Test-Path "$appDir/WuwatappoNative.exe")) { throw "publish 未生成内部 exe" }
$size = (Get-Item "$appDir/WuwatappoNative.exe").Length
if ($size -lt 100KB) { throw "内部 exe 异常偏小: $size" }

Write-Host "== build outer launcher =="
$cscCandidates = @(
  (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
  (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw "缺少 .NET Framework 4.x csc.exe，无法生成外层启动器" }
$launcher = Join-Path $publishDir 'WuwatappoNative.exe'
$launcherSource = Join-Path $root 'launcher\Launcher.cs'
$versionInfo = Join-Path $env:TEMP "WuwatappoNativeLauncher-$Version.cs"
$icon = Join-Path $root 'assets\app.ico'
$versionInfoText = @"
using System.Reflection;
[assembly: AssemblyTitle("Wuwatappo Native Launcher")]
[assembly: AssemblyProduct("Wuwatappo Native")]
[assembly: AssemblyCompany("Wuwatappo Native")]
[assembly: AssemblyVersion("$($Version).0")]
[assembly: AssemblyFileVersion("$($Version).0")]
"@
[IO.File]::WriteAllText($versionInfo, $versionInfoText, [Text.UTF8Encoding]::new($false))
try {
  & $csc /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 `
    "/win32icon:$icon" "/out:$launcher" `
    /reference:System.dll /reference:System.Windows.Forms.dll `
    $launcherSource $versionInfo
  if ($LASTEXITCODE -ne 0) { throw "外层启动器编译失败" }
}
finally {
  Remove-Item -Force $versionInfo -ErrorAction SilentlyContinue
}
if (-not (Test-Path -LiteralPath $launcher)) { throw "外层启动器未生成" }
if ((Get-Item -LiteralPath $launcher).Length -gt 1MB) { throw "外层启动器异常偏大" }

Write-Host "== generate WiX source =="
python installer/gen-wxs.py $Version

Write-Host "== build MSI =="
$wix = ".wix/wix.exe"
if (-not (Test-Path $wix)) { throw "缺少 WiX：先运行 dotnet tool install --tool-path .wix wix --version 5.0.2" }
& $wix build installer/product.wxs -o "$distDir/WuwatappoNative-$Version-x64.msi"

Write-Host "== portable zip =="
$zip = "$distDir/WuwatappoNative-$Version-portable.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$publishDir/*" -DestinationPath $zip

Write-Host "== token extractor =="
& (Join-Path $root 'token-extractor\build.ps1')

Write-Host "== done =="
Get-ChildItem $distDir | Select-Object Name, Length | Format-Table
