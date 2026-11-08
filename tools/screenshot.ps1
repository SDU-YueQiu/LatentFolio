param(
    [string]$Out = "$env:TEMP\lf-shot.png",
    [string]$ProcessName = "LatentFolio",
    [switch]$FullScreen
)
Add-Type -AssemblyName System.Drawing

# DPI 无感知的话 GetWindowRect / CopyFromScreen 拿到的是被虚拟化的坐标，
# 125% 缩放时会截到窗口的左上角一块。先声明 PER_MONITOR_AWARE_V2。
# 另外用 PrintWindow 抓窗口自己的内容：不抢前台，不会把用户从别的程序里踢出来。
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@
try { [W]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { }

if ($FullScreen) {
    $w = [W]::GetSystemMetrics(0); $h = [W]::GetSystemMetrics(1)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Output "$Out  ${w}x${h} (full)"
    exit 0
}

$p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Error "找不到 $ProcessName 的主窗口"; exit 2 }

$hwnd = $p.MainWindowHandle
if (-not [W]::IsWindowVisible($hwnd)) { Write-Error "窗口不可见"; exit 3 }

$r = New-Object W+RECT
[W]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { Write-Error "窗口尺寸异常 $w x $h"; exit 4 }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT = 2，WPF 的硬件加速内容也能抓到
$ok = [W]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

if (-not $ok) {
    # 退回去抓屏幕（需要窗口在前台）
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size); $g2.Dispose()
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "$Out  ${w}x${h}  printwindow=$ok"
