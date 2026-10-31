param(
  [string]$Configuration = "Release",
  [string]$Version = "0.0.12"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publishDir = "publish/app-$Version"
$distDir = "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host "== publish $Version (win-x64, self-contained) =="
Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
dotnet publish -c $Configuration -r win-x64 --self-contained true `
  -p:DebugType=none -p:DebugSymbols=false -o $publishDir -v q --nologo

if (-not (Test-Path "$publishDir/WuwatappoNative.exe")) { throw "publish 未生成 exe" }
$size = (Get-Item "$publishDir/WuwatappoNative.exe").Length
if ($size -lt 100KB) { throw "exe 异常偏小: $size" }

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
