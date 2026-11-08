param(
    [int]$WaitSec = 8,
    [string]$Shot = "",
    [switch]$NoBuild,
    [switch]$KeepDb
)
$ErrorActionPreference = 'Stop'
$repo = 'D:\Projects\LatentFolio'
Set-Location $repo

Get-Process -Name LatentFolio -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Kill()
    $_.WaitForExit(5000)
}
Start-Sleep -Milliseconds 500

if (-not $KeepDb) {
    # keep the scanned DB between runs — only wipe when explicitly asked via -KeepDb:$false
}

if (-not $NoBuild) {
    $out = & dotnet build LatentFolio.slnx -c Debug -v q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        $out | Select-Object -First 40
        Write-Error "构建失败"
    }
    Write-Output "build ok"
}

$exe = Join-Path $repo 'src\LatentFolio.App\bin\Debug\net10.0-windows\LatentFolio.exe'
Start-Process $exe
Start-Sleep -Seconds $WaitSec

$p = Get-Process -Name LatentFolio -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Error '进程没起来'; }
Write-Output "pid=$($p.Id) ws=$([math]::Round($p.WorkingSet64/1MB))MB"

if ($Shot -ne '') {
    & (Join-Path $repo 'tools\screenshot.ps1') -Out $Shot
}
