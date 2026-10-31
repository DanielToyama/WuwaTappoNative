param(
  [string]$Configuration = "Release",
  [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$project = "token-extractor/WuwatappoTokenExtractor.csproj"
$publishDir = "publish/token-extractor"
$distDir = "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue

Write-Host "== publish token extractor $Version (win-x64, self-contained single file) =="
dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false `
  -o $publishDir -v q --nologo

if (-not (Test-Path "$publishDir/WuwatappoTokenExtractor.exe")) {
  throw "token extractor publish 未生成 exe"
}
$readme = Get-Content "token-extractor/README.md" -Raw
[IO.File]::WriteAllText(
  (Join-Path $publishDir "README.txt"),
  $readme,
  (New-Object System.Text.UTF8Encoding($true))
)

$zip = Join-Path $distDir "WuwatappoTokenExtractor-$Version-x64.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$publishDir/*" -DestinationPath $zip

Write-Host "== done =="
Get-ChildItem $publishDir | Select-Object Name, Length | Format-Table
Get-FileHash $zip -Algorithm SHA256 | Select-Object Hash, Path
