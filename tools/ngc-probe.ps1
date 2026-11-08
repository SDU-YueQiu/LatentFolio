<#
.SYNOPSIS
  NGC API 探测 v2：完整落地响应 + 结构摘要（不截断）。
.EXAMPLE
  .\ngc-probe.ps1 -ApiKey 'nvapi-xxx'
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
  'versions' = 'https://api.ngc.nvidia.com/v2/org/nvidia/team/maxine/resources/vfx_sdk_core/versions'
  'files'    = "https://api.ngc.nvidia.com/v2/org/nvidia/team/maxine/resources/vfx_sdk_core/versions/$Version/files"
}
if (Test-Path (Join-Path $out 'extra_urls.txt')) {
  $i = 0
  Get-Content (Join-Path $out 'extra_urls.txt') | Where-Object { $_.Trim() } | ForEach-Object {
    $i++; $targets["extra$i"] = $_.Trim()
  }
}

function Describe($o, $depth = 0) {
  $pad = ' ' * ($depth * 2)
  if ($null -eq $o) { return "$pad(null)" }
  if ($o -is [string]) { $s = if ($o.Length -gt 60) { $o.Substring(0, 60) + '…' } else { $o }; return "$pad[string] `"$s`"" }
  if ($o -is [ValueType]) { return "$pad[$($o.GetType().Name)] $o" }
  if ($o -is [array]) {
    $r = @("$pad[array x$($o.Count)]")
    if ($o.Count -gt 0) { $r += Describe $o[0] ($depth + 1) }
    return ($r -join "`n")
  }
  $lines = @("$pad[object]")
  foreach ($p in $o.PSObject.Properties) {
    $v = $p.Value
    if ($v -is [string] -or $v -is [ValueType] -or $null -eq $v) {
      $lines += (Describe $v ($depth + 1) -replace '^' + (' ' * (($depth + 1) * 2)), ($p.Name + ': '))
    } elseif ($v -is [array]) {
      $lines += "$pad  $($p.Name): [array x$($v.Count)]"
      if ($v.Count -gt 0 -and ($v[0] -is [string] -or $v[0] -is [ValueType])) {
        $lines += "$pad    first=$($v[0])"
      } elseif ($v.Count -gt 0) {
        $head = @($v[0].PSObject.Properties | Select-Object -First 8 -ExpandProperty Name) -join ', '
        $lines += "$pad    first-keys=($head)"
        foreach ($q in $v[0].PSObject.Properties) {
          if ($q.Value -is [string] -or $q.Value -is [ValueType]) { $lines += "$pad      $($q.Name)=$($q.Value)" }
        }
      }
    } else {
      $lines += "$pad  $($p.Name): [object]"
    }
  }
  return ($lines -join "`n")
}

$struct = @()
foreach ($k in $targets.Keys) {
  $url = $targets[$k]
  $lines = @("### $k", "URL: $url", "TIME: $(Get-Date -Format s)")
  $body = $null
  try {
    $r = Invoke-WebRequest -Uri $url -Headers $h -TimeoutSec 60 -UseBasicParsing
    $ct = $r.Headers['Content-Type']; if ($ct -is [array]) { $ct = $ct -join '; ' }
    $lines += "STATUS: $([int]$r.StatusCode)"
    $lines += "CONTENT-TYPE: $ct"
    $lines += "LENGTH: $($r.Content.Length)"
    $body = $r.Content
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    $lines += "ERROR: $($_.Exception.Message)"
    if ($code) { $lines += "STATUS: $code" }
  }
  if ($body) {
    Set-Content -Path (Join-Path $out "$k.json") -Value $body -Encoding utf8
    try {
      $j = $body | ConvertFrom-Json
      $struct += "=== $k ==="
      $struct += (Describe $j)
      $struct += ''
    } catch {
      $struct += "=== $k === (非 JSON)"
      $struct += $body.Substring(0, [Math]::Min(500, $body.Length))
      $struct += ''
    }
  }
  Set-Content -Path (Join-Path $out "$k.txt") -Value ($lines -join "`n") -Encoding utf8
  Write-Host "[$k] $($lines[-1])"
}

Set-Content -Path (Join-Path $out 'structure.txt') -Value ($struct -join "`n") -Encoding utf8
Write-Host "`n完成：$out" -ForegroundColor Green
Get-ChildItem $out | Select-Object Name, Length | Format-Table -AutoSize
