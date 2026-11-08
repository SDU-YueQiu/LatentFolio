param(
    [string]$Out = "$env:TEMP\lf-shot.png",
    [string]$ProcessName = "LatentFolio",
    [int]$DelayMs = 0,
    [switch]$FullScreen
)
Add-Type -AssemblyName System.Drawing

# 进程自己是 DPI 无感知的话，GetWindowRect / CopyFromScreen 拿到的是被虚拟化的坐标，
# 125% 缩放时会截到窗口的左上角一块。先声明 PER_MONITOR_AWARE_V2 再量。
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
try { [W]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { }

if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }

if ($FullScreen) {
    $w = [W]::GetSystemMetrics(0)
    $h = [W]::GetSystemMetrics(1)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "$Out  ${w}x${h} (full)"
    exit 0
}

$p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Error "进程 $ProcessName 没有主窗口"; exit 2 }

$h = $p.MainWindowHandle
[W]::ShowWindow($h, 9) | Out-Null
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 500

$r = New-Object W+RECT
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top
if ($w -le 0 -or $hh -le 0) { Write-Error "窗口尺寸异常 $w x $hh"; exit 3 }

$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "$Out  ${w}x${hh}"
