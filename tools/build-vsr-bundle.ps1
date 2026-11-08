# 把 NVVideoEffects 需要的 6 个 DLL 收拢到一个目录，供 LatentFolio 检测。
#
# 来源只能是装了 NVIDIA Video Effects SDK 的地方：
#   - Python 包 nvvfx（pip install nvidia-nvvfx）自带全部依赖
#   - 或从 NGC 下的 VFX SDk Core（见 tools/fetch-vesdk.ps1），但那里面只有 core，
#     feature 包要另外装，所以实际最省事的是 nvvfx。
#
# 用法：
#   .\tools\build-vsr-bundle.ps1 -Source 'D:\path\to\nvvfx\libs'
#   .\tools\build-vsr-bundle.ps1            # 自动从常见位置找

param(
    [string]$Source = '',
    [string]$Dest = "$env:LOCALAPPDATA\LatentFolio\vsr"
)

$needed = @(
    'cudart64_12.dll',
    'NVCVImage.dll',
    'nvngxruntime.dll',
    'NVVideoEffects.dll',
    'nvVFXVideoSuperRes.dll',
    'nvngx_vsr.dll'
)

function Test-Bundle([string]$dir) {
    if (-not (Test-Path $dir)) { return $false }
    foreach ($f in $needed) {
        if (-not (Test-Path (Join-Path $dir $f))) { return $false }
    }
    return $true
}

if ($Source -eq '') {
    $candidates = @(
        "$env:LOCALAPPDATA\LatentFolio\vsr",
        'D:\Recreation\ComfyUI-aki-v3\python\Lib\site-packages\nvvfx\libs',
        'C:\ComfyUI\python\Lib\site-packages\nvvfx\libs'
    )
    foreach ($c in $candidates) {
        if (Test-Bundle $c) { $Source = $c; break }
    }
}

if ($Source -eq '' -or -not (Test-Bundle $Source)) {
    Write-Error "找不到完整的 DLL 集合。用 -Source 指定 nvvfx\libs 目录。"
    exit 1
}

New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$total = 0
foreach ($f in $needed) {
    Copy-Item (Join-Path $Source $f) $Dest -Force
    $size = (Get-Item (Join-Path $Dest $f)).Length
    $total += $size
    Write-Output ("  {0,-26} {1,10:N0} B" -f $f, $size)
}
Write-Output ("总计 {0:N1} MB -> {1}" -f ($total / 1MB), $Dest)
