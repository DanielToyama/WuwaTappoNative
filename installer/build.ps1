param(
  [string]$Configuration = "Release",
  [string]$Version = "0.0.29"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publishDir = "publish/app-$Version"
$distDir = "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host "== publish $Version (win-x64, self-contained single file) =="
Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
dotnet publish -c $Configuration -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false -o $publishDir -v q --nologo

$exe = Join-Path $publishDir 'WuwatappoNative.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "publish 未生成 WuwatappoNative.exe" }
$size = (Get-Item -LiteralPath $exe).Length
if ($size -lt 10MB) { throw "单文件 exe 异常偏小: $size" }

Write-Host "== build Inno Setup installer =="
$isccCandidates = @(
  (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
  (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
  (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source }
if (-not $iscc) { throw "缺少 Inno Setup 6 的 ISCC.exe，请先安装 Inno Setup 6" }
& $iscc "/DMyVersion=$Version" (Join-Path $root 'installer\WuwatappoNative.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败" }

Write-Host "== portable zip =="
$zip = "$distDir/WuwatappoNative-$Version-portable.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$publishDir/*" -DestinationPath $zip

Write-Host "== token extractor =="
& (Join-Path $root 'token-extractor\build.ps1')

Write-Host "== done =="
Get-ChildItem $distDir | Select-Object Name, Length | Format-Table
