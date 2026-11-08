param(
    [string]$Args = "",
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$WaitMs = 7000,
    # 弹窗类（设置/标签/列编辑器）用标题定位那个模态窗口
    [string]$Title = ""
)
$ErrorActionPreference = 'Stop'
$repo = 'D:\Projects\LatentFolio'
$exe = Join-Path $repo 'src\LatentFolio.App\bin\Release\net10.0-windows\LatentFolio.exe'

Get-Process LatentFolio -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$argv = @()
if ($Args.Length -gt 0) { $argv = $Args.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) }
$p = Start-Process -FilePath $exe -ArgumentList $argv -PassThru
for ($i = 0; $i -lt 80; $i++) {
    Start-Sleep -Milliseconds 250
    if ($p.HasExited) { throw '进程提前退出' }
    if ($p.MainWindowHandle -ne 0) { break }
}
Start-Sleep -Milliseconds $WaitMs

$tmp = Join-Path $env:TEMP "lf-$Name.png"
$shot = @{ Out = $tmp }
if ($Title.Length -gt 0) { $shot['Title'] = $Title }
& (Join-Path $repo 'tools\screenshot.ps1') @shot | Out-Null

Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($tmp)
try {
    $enc = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $prm = New-Object System.Drawing.Imaging.EncoderParameters 1
    $prm.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality, 88L)
    $out = Join-Path $repo "docs\shots\$Name.jpg"
    $img.Save($out, $enc, $prm)
    Write-Output "$out  $($img.Width)x$($img.Height)  $([int]((Get-Item $out).Length/1KB)) KB"
}
finally {
    $img.Dispose()
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}
