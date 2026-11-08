param(
    [Parameter(Mandatory = $true)][string]$Args,
    [string]$Title = "",
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitMs = 5000
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\LatentFolio.App\bin\Release\net10.0-windows\LatentFolio.exe"

Get-Process LatentFolio -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$argv = @()
if ($Args.Length -gt 0) { $argv = $Args.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) }
$p = Start-Process -FilePath $exe -ArgumentList $argv -PassThru
for ($i = 0; $i -lt 80; $i++) {
    Start-Sleep -Milliseconds 250
    if ($p.HasExited) { Write-Error "进程提前退出"; exit 2 }
    if ($p.MainWindowHandle -ne 0) { break }
}
Start-Sleep -Milliseconds $WaitMs

$shotArgs = @{ Out = (Join-Path $root $Out) }
if ($Title.Length -gt 0) { $shotArgs.Title = $Title }
& (Join-Path $PSScriptRoot "screenshot.ps1") @shotArgs
