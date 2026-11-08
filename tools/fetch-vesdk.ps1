<#
.SYNOPSIS
  直接从 NGC REST API 下载 VFX SDK Core，不装 ngccli。

.DESCRIPTION
  NGC 的下载走的是 api.ngc.nvidia.com，ngccli 只是个壳。
  这里用 API Key + curl 直接下，支持断点续传；装过 aria2 的话自动多线程。

.EXAMPLE
  .\fetch-vesdk.ps1 -ApiKey '<你的 NGC API Key>' -List
  .\fetch-vesdk.ps1 -ApiKey '<key>' -Version 1.3.0.0_windows
  .\fetch-vesdk.ps1 -ApiKey '<key>' -UseAria            # 装了 aria2 时
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$ApiKey,
  [string]$Version = '1.3.0.0_windows',
  [string]$Resource = 'vfx_sdk_core',
  [string]$Team = 'maxine',
  [string]$Org = 'nvidia',
  [string]$OutDir = '',
  [switch]$List,
  [switch]$UseAria
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$apiRoot = "https://api.ngc.nvidia.com/v2/org/$Org/team/$Team/resources/$Resource"
$headers = @{
  'Authorization' = "Bearer $ApiKey"
  'Accept'        = 'application/json'
  'User-Agent'    = 'LatentFolio-fetch/1.0'
}

if (-not $OutDir) {
  $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) "third_party\$Resource-$Version"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-Json($url) {
  try {
    return Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 60
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 401) { throw "401：API Key 不对或没权限。去 https://org.ngc.nvidia.com/setup/api-key 重新生成。" }
    if ($code -eq 404) { throw "404：路径不存在 -> $url（可能版本号或 resource 名不对）" }
    throw "请求失败 $code : $url`n$($_.Exception.Message)"
  }
}

Write-Host "== 版本列表 ==" -ForegroundColor Cyan
try {
  $vers = Get-Json "$apiRoot/versions"
  $names = @($vers | ForEach-Object { $_.versionId })
  Write-Host ("  " + ($names -join ', '))
} catch {
  Write-Host "  (列版本失败，继续尝试指定版本) $($_.Exception.Message)" -ForegroundColor DarkYellow
}

Write-Host "`n== $Version 的文件 ==" -ForegroundColor Cyan
$resp = Get-Json "$apiRoot/versions/$Version/files"
$items = @()
if ($resp.PSObject.Properties.Name -contains 'content') { $items = @($resp.content) }
elseif ($resp -is [array]) { $items = @($resp) }
else { $items = @($resp) }

if ($items.Count -eq 0) { throw "没拿到文件列表，检查版本号：$Version" }

foreach ($it in $items) {
  $sz = if ($it.size) { "{0:N1} MB" -f ($it.size / 1MB) } else { '?' }
  Write-Host ("  {0,-50} {1}" -f $it.path, $sz)
}
if ($List) { Write-Host "`n(只列不下载)" -ForegroundColor Green; return }

$aria = $null
if ($UseAria) {
  $aria = (Get-Command aria2c -ErrorAction SilentlyContinue).Source
  if (-not $aria) { Write-Host "没找到 aria2c，回退 curl。装法：scoop install aria2" -ForegroundColor DarkYellow }
}

foreach ($it in $items) {
  if ([string]::IsNullOrWhiteSpace($it.path)) { continue }
  $fileName = Split-Path $it.path -Leaf
  $dest = Join-Path $OutDir $fileName
  $url = "$apiRoot/versions/$Version/files/" + [uri]::EscapeDataString($it.path)
  Write-Host "`n>> $fileName" -ForegroundColor Cyan
  $sw = [Diagnostics.Stopwatch]::StartNew()
  if ($aria) {
    & $aria -x 16 -s 16 -k 1M --console-log-level=warn --summary-interval=5 `
      --header="Authorization: Bearer $ApiKey" -d $OutDir -o $fileName $url
  } else {
    # curl 自带断点续传 -C -，并且打印进度
    & curl.exe -L --fail --retry 5 --retry-delay 3 -C - --progress-bar `
      -H "Authorization: Bearer $ApiKey" -o $dest $url
  }
  $sw.Stop()
  if (Test-Path $dest) {
    $mb = (Get-Item $dest).Length / 1MB
    $sec = [math]::Max(0.1, $sw.Elapsed.TotalSeconds)
    Write-Host ("   完成 {0:N1} MB / {1:N1} s = {2:N2} MB/s" -f $mb, $sec, ($mb / $sec)) -ForegroundColor Green
  }
}

Write-Host "`n== 解压 ==" -ForegroundColor Cyan
Get-ChildItem $OutDir -Filter '*.zip' | ForEach-Object {
  $target = Join-Path $OutDir ($_.BaseName)
  if (-not (Test-Path $target)) {
    Write-Host "  expanding $($_.Name) -> $target"
    Expand-Archive -Path $_.FullName -DestinationPath $target -Force
  }
}
Write-Host "`n完成，输出目录：$OutDir" -ForegroundColor Green
Get-ChildItem $OutDir -Directory | ForEach-Object {
  Write-Host "--- $($_.Name) ---"
  Get-ChildItem $_.FullName | Select-Object -First 12 -ExpandProperty Name
}
