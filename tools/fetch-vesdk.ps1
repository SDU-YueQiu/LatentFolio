<#
.SYNOPSIS
  从 NGC REST API 下载 VFX SDK Core（绕过 ngccli）。v3 已按真实响应结构写死。

.NOTES
  实测响应结构（2026-11-08）：
    GET .../versions/{ver}/files  →  { recipe, recipeVersion, recipeFiles[], paginationInfo, requestStatus }
    recipeFiles[i] = { path, sizeInBytes, createdDate, sha256_base64 }
    VFX SDK Core 1.3.0.0_windows → VFXSDK_windows_1.3.0.0.zip / 1,063,840,846 B / 1 个文件

  API Key 三种给法（优先级从高到低）：
    -ApiKey 'nvapi-...'   |   环境变量 NGC_API_KEY   |   项目根目录 .ngc_key 文件

.EXAMPLE
  .\fetch-vesdk.ps1 -List
  .\fetch-vesdk.ps1
  .\fetch-vesdk.ps1 -UseAria
#>
[CmdletBinding()]
param(
  [string]$ApiKey = '',
  [string]$Version = '1.3.0.0_windows',
  [string]$Resource = 'vfx_sdk_core',
  [string]$Team = 'maxine',
  [string]$Org = 'nvidia',
  [string]$OutDir = '',
  [switch]$List,
  [switch]$UseAria,
  [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot

# ---- key 解析
if (-not $ApiKey) { $ApiKey = $env:NGC_API_KEY }
if (-not $ApiKey) {
  $kf = Join-Path $root '.ngc_key'
  if (Test-Path $kf) { $ApiKey = (Get-Content $kf -Raw).Trim() }
}
if (-not $ApiKey) {
  throw "缺少 API Key。三种给法：-ApiKey 'nvapi-...' / 环境变量 NGC_API_KEY / 在 $root 放一个 .ngc_key 文件（内容就是 key）"
}
if ($ApiKey -notmatch '^nvapi-') {
  Write-Host "警告：key 不像 nvapi- 开头，继续尝试" -ForegroundColor DarkYellow
}

if (-not $OutDir) { $OutDir = Join-Path $root "third_party\$Resource-$Version" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$h = @{
  'Authorization' = "Bearer $ApiKey"
  'Accept'        = 'application/json'
  'User-Agent'    = 'LatentFolio-fetch/3.0'
}
$base = "https://api.ngc.nvidia.com/v2/org/$Org/team/$Team/resources/$Resource"

# ---- 1. 文件清单
Write-Host "== 查询 $Version 文件清单 ==" -ForegroundColor Cyan
$resp = Invoke-RestMethod -Uri "$base/versions/$Version/files" -Headers $h -TimeoutSec 60

$files = @()
if ($resp.recipeFiles) { $files = @($resp.recipeFiles) }
if ($files.Count -eq 0) { throw "响应里没有 recipeFiles，把原始响发我。" }

$total = 0
foreach ($f in $files) {
  $total += [int64]$f.sizeInBytes
  Write-Host ("  {0,-40} {1,12:N0} B  ({2:N0} MB)" -f $f.path, $f.sizeInBytes, ([double]$f.sizeInBytes / 1MB))
}
if ($resp.recipeVersion) {
  Write-Host ("  版本 {0} · 状态 {1} · 扫描 {2}" -f $resp.recipeVersion.versionId,
    $resp.recipeVersion.status, $resp.recipeVersion.malwareScanStatus) -ForegroundColor DarkGray
}
Write-Host ("  合计 {0:N0} MB" -f ($total / 1MB)) -ForegroundColor DarkGray
if ($List) { Write-Host "`n(只列不下载)" -ForegroundColor Green; return }

# ---- 2. 下载
$aria = $null
if ($UseAria) {
  $aria = (Get-Command aria2c -ErrorAction SilentlyContinue).Source
  if (-not $aria) { Write-Host "没找到 aria2c，回退 curl。装法：scoop install aria2" -ForegroundColor DarkYellow }
}

foreach ($f in $files) {
  $fileName = Split-Path $f.path -Leaf
  $dest = Join-Path $OutDir $fileName
  $url = "$base/versions/$Version/files/" + [uri]::EscapeDataString($f.path)

  if ((Test-Path $dest) -and (Get-Item $dest).Length -eq [int64]$f.sizeInBytes) {
    Write-Host "`n>> $fileName 已存在且大小一致，跳过下载" -ForegroundColor DarkGray
  } else {
    Write-Host "`n>> $fileName" -ForegroundColor Cyan
    Write-Host "   $url" -ForegroundColor DarkGray
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ($aria) {
      & $aria -x 16 -s 16 -k 1M -c --console-log-level=warn --summary-interval=10 `
        --header="Authorization: Bearer $ApiKey" -d $OutDir -o $fileName $url
    } else {
      & curl.exe -L --fail --retry 5 --retry-delay 3 -C - --progress-bar `
        -H "Authorization: Bearer $ApiKey" -o $dest $url
    }
    $sw.Stop()
  }

  if (-not (Test-Path $dest)) { throw "下载失败，文件不存在：$dest" }
  $got = (Get-Item $dest).Length
  if ($got -ne [int64]$f.sizeInBytes) {
    Write-Host ("  大小不符：{0:N0} B，期望 {1:N0} B。再跑一次可断点续传。" -f $got, $f.sizeInBytes) -ForegroundColor DarkYellow
  } else {
    $mb = $got / 1MB
    Write-Host ("  完成 {0:N0} MB" -f $mb) -ForegroundColor Green
  }

  # ---- 3. 校验 SHA256（响应里给的是 base64）
  if (-not $SkipVerify -and $f.sha256_base64) {
    $expected = [Convert]::FromBase64String($f.sha256_base64)
    $actual = (Get-FileHash -Path $dest -Algorithm SHA256).Hash
    $actualB64 = [Convert]::ToBase64String([byte[]]::new(0)) # placeholder
    $hexExp = ($expected | ForEach-Object { $_.ToString('x2') }) -join ''
    if ($actual -ieq $hexExp) {
      Write-Host "  SHA256 校验通过" -ForegroundColor Green
    } else {
      Write-Host "  SHA256 不匹配！`n    expected=$hexExp`n    actual  =$actual" -ForegroundColor Red
    }
    Write-Host "  sha256(hex) = $hexExp" -ForegroundColor DarkGray
  }
}

# ---- 4. 解压
Write-Host "`n== 解压 ==" -ForegroundColor Cyan
Get-ChildItem $OutDir -Filter '*.zip' | ForEach-Object {
  $target = Join-Path $OutDir ($_.BaseName)
  if (Test-Path $target) { Write-Host "  已存在，跳过：$target" -ForegroundColor DarkGray }
  else {
    Write-Host "  $($_.Name) -> $target"
    Expand-Archive -Path $_.FullName -DestinationPath $target -Force
  }
}
# VFXSDK_windows_1.3.0.0.zip 通常解出 VFXSDK 目录，统一一下
$inner = Get-ChildItem $OutDir -Directory | Where-Object { $_.Name -match '^(VFXSDK|vfx)' } | Select-Object -First 1
Write-Host "`n输出目录：$OutDir" -ForegroundColor Green
Get-ChildItem $OutDir -Directory | ForEach-Object {
  Write-Host "--- $($_.Name) ---" -ForegroundColor Cyan
  Get-ChildItem $_.FullName | Select-Object -First 15 -ExpandProperty Name | ForEach-Object { "   $_" }
}
if ($inner) {
  $hits = Get-ChildItem $inner.FullName -Recurse -Include 'NvVideoEffects.h','*.lib' -ErrorAction SilentlyContinue | Select-Object -First 6
  if ($hits) {
    Write-Host "`n找到 C++ 头/库：" -ForegroundColor Green
    $hits | ForEach-Object { "   $($_.FullName)" }
  }
}
