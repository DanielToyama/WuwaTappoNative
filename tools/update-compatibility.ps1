$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root "docs/compatibility.json"
$json = Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json

function Get-OfficialVersion([string]$hostUrl, [string]$hostFile) {
    $headers = @{ "User-Agent" = "WuwatappoNative compatibility sync" }
    $hostInfo = Invoke-RestMethod -Uri "$hostUrl/app/json-host/$hostFile" -Headers $headers
    if ([string]::IsNullOrWhiteSpace($hostInfo.url)) {
        throw "missing url in $hostUrl/app/json-host/$hostFile"
    }
    $manifest = Invoke-RestMethod -Uri $hostInfo.url -Headers $headers
    if ([string]::IsNullOrWhiteSpace($manifest.version)) {
        throw "missing version in $($hostInfo.url)"
    }
    return $manifest
}

$cn = Get-OfficialVersion "https://mc-wuwatappo.kurogames-global.com" "latest_host.json"
$global = Get-OfficialVersion "https://wutheringwaves-wuwatappo.kurogames-global.com" "latest-global_host.json"

$changed = $false
if ($json.official.cn.latest -ne $cn.version) {
    $json.official.cn.latest = $cn.version
    $changed = $true
}
if (-not [string]::IsNullOrWhiteSpace($cn.min_ota_client_version) -and
    $json.official.cn.minOta -ne $cn.min_ota_client_version) {
    $json.official.cn.minOta = $cn.min_ota_client_version
    $changed = $true
}
if ($json.official.global.latest -ne $global.version) {
    $json.official.global.latest = $global.version
    $changed = $true
}
if (-not [string]::IsNullOrWhiteSpace($global.min_ota_client_version) -and
    $json.official.global.minOta -ne $global.min_ota_client_version) {
    $json.official.global.minOta = $global.min_ota_client_version
    $changed = $true
}
if ($json.official.global.inheritCn -ne $false) {
    $json.official.global.inheritCn = $false
    $changed = $true
}

if (-not $changed) {
    Write-Host "Official versions unchanged."
    exit 0
}

$json.updatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$tested = if ([string]::IsNullOrWhiteSpace($json.protocol.testedOfficial)) {
    "未验证"
} else {
    $json.protocol.testedOfficial
}
$json.message = "官方客户端已更新至 $($cn.version)；当前协议验证基于 $tested，暂未发现破坏性变更。"
$json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding utf8
Write-Host "Updated compatibility.json: cn=$($cn.version), global=$($global.version)"
