<#
.SYNOPSIS
  NGC API 探测：把原始响应落盘，供排查（不需要你复制粘贴）。
.EXAMPLE
  .\ngc-probe.ps1 -ApiKey 'nvapi-xxx'
  然后看 third_party\ngc-probe\ 下的文件。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$ApiKey,
  [string]$Version = '1.3.0.0_windows'
)
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'third_party\ngc-probe'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$h = @{
  'Authorization' = "Bearer $ApiKey"
  'Accept'        = 'application/json'
  'User-Agent'    = 'curl/8.4.0'
}

$targets = [ordered]@{
  'versions_a' = 'https://api.ngc.nvidia.com/v2/org/nvidia/team/maxine/resources/vfx_sdk_core/versions'
  'versions_b' = 'https://api.ngc.nvidia.com/v2/orgs/nvidia/teams/maxine/resources/vfx_sdk_core/versions'
  'resource'   = 'https://api.ngc.nvidia.com/v2/org/nvidia/team/maxine/resources/vfx_sdk_core'
  'files'      = "https://api.ngc.nvidia.com/v2/org/nvidia/team/maxine/resources/vfx_sdk_core/versions/$Version/files"
  'keycheck'   = 'https://api.ngc.nvidia.com/v2/users/me'
}

$report = @()
foreach ($k in $targets.Keys) {
  $url = $targets[$k]
  $lines = @("### $k", "URL: $url", "TIME: $(Get-Date -Format s)")
  try {
    $r = Invoke-WebRequest -Uri $url -Headers $h -TimeoutSec 45 -UseBasicParsing -MaximumRedirection 5
    $ct = $r.Headers['Content-Type']
    if ($ct -is [array]) { $ct = $ct -join '; ' }
    $lines += "STATUS: $([int]$r.StatusCode)"
    $lines += "CONTENT-TYPE: $ct"
    $lines += "LENGTH: $($r.Content.Length)"
    $lines += '----- BODY (first 2000) -----'
    $lines += $r.Content.Substring(0, [Math]::Min(2000, $r.Content.Length))
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    $lines += "ERROR: $($_.Exception.Message)"
    if ($code) { $lines += "STATUS: $code" }
    try {
      $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
      $body = $sr.ReadToEnd()
      $lines += '----- BODY (first 2000) -----'
      $lines += $body.Substring(0, [Math]::Min(2000, $body.Length))
    } catch { }
  }
  $p = Join-Path $out "$k.txt"
  Set-Content -Path $p -Value ($lines -join "`n") -Encoding utf8
  Write-Host "[$k] -> $p"
  Write-Host ($lines[2..([Math]::Min(5, $lines.Count - 1))] -join '  ')
}

Write-Host "`n完成。文件在：$out" -ForegroundColor Green
Write-Host "（我直接读这些文件就行，不用你贴）"
