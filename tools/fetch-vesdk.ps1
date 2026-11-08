<#
.SYNOPSIS
  直接从 NGC REST API 下载 VFX SDK Core，不装 ngccli。

.NOTES
  2026-11-08 v2：真机跑 v1 时版本列表为空、文件列表出现一行空白 → 说明响应字段名
  和我猜的不一样。这一版改成"先抓原始 JSON、再自动适配字段名"，抓不到就直接把
  原始响应打到屏幕上，方便定位。
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
  [switch]$UseAria,
  [switch]$Dump
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$headers = @{
  'Authorization'  = "Bearer $ApiKey"
  'Accept'         = 'application/json'
  'User-Agent'     = 'LatentFolio-fetch/2.0'
}

if (-not $OutDir) {
  $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) "third_party\$Resource-$Version"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---------------------------------------------------------------- HTTP
function Get-Raw([string]$url) {
  $r = Invoke-WebRequest -Uri $url -Headers $headers -TimeoutSec 60 -UseBasicParsing
  return $r.Content
}

function Show-Raw([string]$label, [string]$text) {
  Write-Host "`n---- 原始响应 [$label] ----" -ForegroundColor DarkYellow
  Write-Host ($text.Substring(0, [Math]::Min(1200, $text.Length)))
  Write-Host "------------------------------`n" -ForegroundColor DarkYellow
  if ($Dump) {
    $p = Join-Path $OutDir ("raw-$label.json")
    Set-Content -Path $p -Value $text -Encoding utf8
    Write-Host "已存 $p" -ForegroundColor DarkYellow
  }
}

function Try-Json($text) {
  try { return , ($text | ConvertFrom-Json) } catch { return $null }
}

# ---------------------------------------------------------------- 版本列表（仅探测用）
$base = "https://api.ngc.nvidia.com/v2/org/$Org/team/$Team/resources/$Resource"
Write-Host "== 探测版本列表 ==" -ForegroundColor Cyan
$versRaw = $null
foreach ($u in @("$base/versions", "https://api.ngc.nvidia.com/v2/orgs/$Org/teams/$Team/resources/$Resource/versions")) {
  try {
    $versRaw = Get-Raw $u
    Write-Host "  200 OK  $u" -ForegroundColor Green
    break
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    Write-Host "  $code  $u" -ForegroundColor DarkYellow
    $versRaw = $null
    if ($code -eq 401) {
      throw "401：API Key 无效或没被授权访问 nvidia/maxine。去 https://org.ngc.nvidia.com/setup/api-key 重新生成，并确认账号能打开 https://catalog.ngc.nvidia.com/orgs/nvidia/teams/maxine/collections/maxine_vfx_sdk"
    }
  }
}
if ($versRaw) {
  $vj = Try-Json $versRaw
  $ids = @()
  if ($vj) {
    $ids = @($vj | ForEach-Object {
        if ($_.versionId) { $_.versionId }
        elseif ($_.version) { $_.version }
        elseif ($_.name) { $_.name }
        else { $_ }
      } | Where-Object { $_ -is [string] -and $_ })
  }
  if ($ids.Count) { Write-Host ("  版本：" + ($ids -join ', ')) }
  else { Show-Raw 'versions' $versRaw }
}

# ---------------------------------------------------------------- 文件列表
Write-Host "`n== 文件列表 $Version ==" -ForegroundColor Cyan
$filesRaw = $null
try {
  $filesRaw = Get-Raw "$base/versions/$Version/files"
} catch {
  $code = $_.Exception.Response.StatusCode.value__
  if ($code -eq 404) { throw "404：找不到 $Version。用上面列出的版本号试试 -Version <版本>" }
  throw
}

$fj = Try-Json $filesRaw
$items = @()

# 不猜字段名：递归找"看起来像文件条目"的对象（有 path/name 这类键 + 有 size 或 url）
function Find-FileEntries($node, $depth = 0) {
  $found = @()
  if ($depth -gt 8 -or $null -eq $node) { return $found }
  if ($node -is [array]) {
    foreach ($e in $node) { $found += Find-FileEntries $e ($depth + 1) }
    return $found
  }
  if ($node -isnot [System.Management.Automation.PSCustomObject]) { return $found }
  $names = $node.PSObject.Properties.Name
  $hasPath = $names | Where-Object { $_ -in @('path', 'name', 'filename', 'fileName', 'file') }
  $hasMeta = $names | Where-Object { $_ -in @('size', 'contentLength', 'url', 'downloadUrl') }
  if ($hasPath -and $hasMeta) { return @($node) }
  foreach ($p in $node.PSObject.Properties) {
    if ($p.Value -is [array] -or $p.Value -is [System.Management.Automation.PSCustomObject]) {
      $found += Find-FileEntries $p.Value ($depth + 1)
    }
  }
  return $found
}

if ($fj) {
  if ($fj.PSObject.Properties.Name -contains 'content') { $items = @($fj.content) }
  if ($items.Count -eq 0) { $items = @(Find-FileEntries $fj) }
  if ($items.Count -eq 0) { $items = @($fj) }
}

function Get-Prop($obj, [string[]]$names) {
  foreach ($n in $names) {
    $v = $obj.PSObject.Properties[$n]
    if ($v -and $v.Value) { return $v.Value }
  }
  return $null
}

$rows = @()
foreach ($it in $items) {
  $p = Get-Prop $it @('path', 'name', 'filename', 'fileName')
  $s = Get-Prop $it @('size', 'contentLength', 'bytes')
  $u = Get-Prop $it @('url', 'downloadUrl', 'link')
  if (-not $p -and -not $u) { continue }
  $rows += [pscustomobject]@{ Path = $p; Size = $s; Url = $u }
}

if ($rows.Count -eq 0) {
  Show-Raw 'files' $filesRaw
  throw "没解析出文件条目。把上面这段原始响应发我，我按真实字段名改。"
}

foreach ($r in $rows) {
  $sz = if ($r.Size) { "{0:N1} MB" -f ([double]$r.Size / 1MB) } else { '?' }
  Write-Host ("  {0,-55} {1}" -f $r.Path, $sz)
}
if ($List) { Write-Host "`n(只列不下载)" -ForegroundColor Green; return }

# ---------------------------------------------------------------- 下载
$aria = $null
if ($UseAria) {
  $aria = (Get-Command aria2c -ErrorAction SilentlyContinue).Source
  if (-not $aria) { Write-Host "没找到 aria2c，回退 curl。装法：scoop install aria2" -ForegroundColor DarkYellow }
}

foreach ($r in $rows) {
  $fileName = if ($r.Path) { Split-Path $r.Path -Leaf } else { "download.bin" }
  $dest = Join-Path $OutDir $fileName
  $url = if ($r.Url) { $r.Url } else { "$base/versions/$Version/files/" + [uri]::EscapeDataString($r.Path) }

  Write-Host "`n>> $fileName" -ForegroundColor Cyan
  $sw = [Diagnostics.Stopwatch]::StartNew()
  if ($aria) {
    & $aria -x 16 -s 16 -k 1M --console-log-level=warn --summary-interval=5 `
      --header="Authorization: Bearer $ApiKey" -d $OutDir -o $fileName $url
  } else {
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

# ---------------------------------------------------------------- 解压
Write-Host "`n== 解压 ==" -ForegroundColor Cyan
Get-ChildItem $OutDir -Filter '*.zip' | ForEach-Object {
  $target = Join-Path $OutDir ($_.BaseName)
  if (-not (Test-Path $target)) {
    Write-Host "  $($_.Name) -> $target"
    Expand-Archive -Path $_.FullName -DestinationPath $target -Force
  } else {
    Write-Host "  已存在 $target"
  }
}
Write-Host "`n输出目录：$OutDir" -ForegroundColor Green
Get-ChildItem $OutDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
  Write-Host "--- $($_.Name) ---"
  Get-ChildItem $_.FullName | Select-Object -First 15 -ExpandProperty Name
}
