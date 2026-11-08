param(
    [string]$Out = "$env:TEMP\lf-shot.png",
    [string]$ProcessName = "LatentFolio",
    [string]$Title = "",
    [switch]$FullScreen
)
Add-Type -AssemblyName System.Drawing

# DPI 无感知的话 GetWindowRect / CopyFromScreen 拿到的是被虚拟化的坐标，
# 125% 缩放时会截到窗口的左上角一块。先声明 PER_MONITOR_AWARE_V2。
# 另外用 PrintWindow 抓窗口自己的内容：不抢前台，不会把用户从别的程序里踢出来。
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

  public static IntPtr FindByTitle(uint pid, string title) {
    IntPtr hit = IntPtr.Zero;
    EnumWindows((h, p) => {
      if (!IsWindowVisible(h)) return true;
      uint wp; GetWindowThreadProcessId(h, out wp);
      if (wp != pid) return true;
      var sb = new StringBuilder(512);
      GetWindowTextW(h, sb, 512);
      if (sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) { hit = h; return false; }
      return true;
    }, IntPtr.Zero);
    return hit;
  }
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
if ($Title.Length -gt 0) {
    $h2 = [W]::FindByTitle([uint32]$p.Id, $Title)
    if ($h2 -ne [IntPtr]::Zero) { $hwnd = $h2 }
    else { Write-Error "找不到标题含「$Title」的窗口"; exit 5 }
}
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
