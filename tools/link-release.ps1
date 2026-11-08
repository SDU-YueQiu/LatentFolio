# 编译 Release，并把根目录的 LatentFolio.lnk 指过去。
# 功能改完跑一次这个，双击根目录快捷方式就是最新的程序。
#
#   .\tools\link-release.ps1            编译 + 重建快捷方式
#   .\tools\link-release.ps1 -NoBuild   只重建快捷方式

param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'src\LatentFolio.App\bin\Release\net10.0-windows\LatentFolio.exe'

# 正在运行的实例会把输出目录里的 dll 锁住，先关掉
Get-Process -Name LatentFolio -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}
Start-Sleep -Milliseconds 400

if (-not $NoBuild) {
    Push-Location $repo
    try {
        & dotnet build 'LatentFolio.slnx' -c Release -v q --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Release 构建失败' }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $exe)) { throw "找不到 $exe" }

$lnk = Join-Path $repo 'LatentFolio.lnk'
$ws = New-Object -ComObject WScript.Shell
try {
    $sc = $ws.CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = Split-Path $exe
    $sc.IconLocation = "$exe,0"
    $sc.Description = 'LatentFolio 图库'
    $sc.Save()
}
finally {
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($ws) | Out-Null
}

$size = [math]::Round((Get-Item $exe).Length / 1KB)
Write-Output "Release: $exe  ($size KB)"
Write-Output "快捷方式: $lnk"
