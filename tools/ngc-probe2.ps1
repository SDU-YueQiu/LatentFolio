<#
.SYNOPSIS
  NGC 探测 v3：把候选端点的**完整**响应落盘，并暴力试探下载文件名。
  你跑一次，我直接读结果，不需要你贴任何东西。
.EXAMPLE
  .\ngc-probe2.ps1 -ApiKey 'nvapi-xxx'
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$ApiKey,
  [string]$Version = '1.3.0.0_windows'
)
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'third_party\ngc-probe2'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$baseRes = 'https://api.ngc.nvidia.com/v2/org/nvidia/team/maxine/resources/vfx_sdk_core'
$baseVer = "$baseRes/versions/$Version"
$auth = "Authorization: Bearer $ApiKey"
$ua = "User-Agent: curl/8.4.0"

$report = @()

function Dump([string]$name, [string]$url) {
  $dest = Join-Path $out "$name.json"
  $code = & curl.exe -sS -L --max-time 60 -H $auth -H $ua -H 'Accept: application/json' `
    -o $dest -w '%{http_code}' $url 2>$null
  $len = if (Test-Path $dest) { (Get-Item $dest).Length } else { 0 }
  $line = "[$name] HTTP $code  $len bytes  $url"
  Write-Host $line
  $script:report += $line
  return $dest
}

Write-Host "`n=== 1. 完整响应落盘 ===" -ForegroundColor Cyan
$urls = [ordered]@{
  'r_versions'      = "$baseRes/versions"
  'r_version'       = "$baseVer"
  'r_files'         = "$baseVer/files"
  'r_files_slash'   = "$baseVer/files/"
  'r_files_page'    = "$baseVer/files?page=1"
  'r_resource'      = $baseRes
}
foreach ($k in $urls.Keys) { [void](Dump $k $urls[$k]) }

Write-Host "`n=== 2. 试探下载文件名（HEAD，看 302/200）===" -ForegroundColor Cyan
$guesses = @(
  'vfx_sdk_core_1.3.0.0_windows.zip'
  'vfx_sdk_core-1.3.0.0-windows.zip'
  "vfx_sdk_core_$Version.zip"
  'vfx_sdk_core.zip'
  'vfx_sdk_core_windows.zip'
  'vfx_sdk_core_1.3.0.0.zip'
  'vfx_sdk_core_1.3.0.0_windows.7z'
  'vfx_sdk_core-1.3.0.0_windows.7z'
  'nvidia_vfx_sdk_core_1.3.0.0_windows.zip'
  'vfx_sdk_core_1.3.0.0_windows.tar.gz'
)
foreach ($g in $guesses) {
  $u = "$baseVer/files/$g"
  $res = & curl.exe -sS -I --max-time 30 -H $auth -H $ua -o NUL -w '%{http_code}|%{redirect_url}' $u 2>$null
  $parts = ($res -split '\|')
  $code = $parts[0]
  $redir = if ($parts.Count -gt 1) { $parts[1] } else { '' }
  $tag = if ($code -in @('200', '302', '303', '307')) { '  <== 命中!' } else { '' }
  $line = ("  {0,-46} {1} {2}{3}" -f $g, $code, $redir, $tag)
  Write-Host $line -ForegroundColor $(if ($tag) { 'Green' } else { 'Gray' })
  $report += $line
}

Write-Host "`n=== 3. 版本详情里的 size / 文件数 ===" -ForegroundColor Cyan
$vf = Join-Path $out 'r_version.json'
if (Test-Path $vf) {
  try {
    $j = Get-Content $vf -Raw -Encoding utf8 | ConvertFrom-Json
    $j.PSObject.Properties | ForEach-Object {
      $v = $_.Value
      if ($v -is [string] -and $v.Length -gt 80) { $v = $v.Substring(0, 80) + '…' }
      $line = "  $($_.Name) = $v"
      Write-Host $line
      $report += $line
    }
  } catch { Write-Host '  (解析失败)' }
}

Set-Content -Path (Join-Path $out 'REPORT.txt') -Value ($report -join "`n") -Encoding utf8
Write-Host "`n全部结果：$out\REPORT.txt" -ForegroundColor Green
Write-Host "（我直接读，不用你贴）"
