param(
    [int]$X,
    [int]$Y,
    [ValidateSet('left', 'right', 'double')]
    [string]$Button = 'left',
    [int]$WaitMs = 800
)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class M {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
try { [M]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { }

$p = Get-Process -Name LatentFolio -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($p) { [M]::SetForegroundWindow($p.MainWindowHandle) | Out-Null }
Start-Sleep -Milliseconds 250

[M]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds 200

$DOWN_LEFT = 0x0002; $UP_LEFT = 0x0004
$DOWN_RIGHT = 0x0008; $UP_RIGHT = 0x0010
if ($Button -eq 'right') {
    [M]::mouse_event($DOWN_RIGHT, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [M]::mouse_event($UP_RIGHT, 0, 0, 0, [IntPtr]::Zero)
}
elseif ($Button -eq 'double') {
    foreach ($i in 1..2) {
        [M]::mouse_event($DOWN_LEFT, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 30
        [M]::mouse_event($UP_LEFT, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }
}
else {
    [M]::mouse_event($DOWN_LEFT, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [M]::mouse_event($UP_LEFT, 0, 0, 0, [IntPtr]::Zero)
}
Start-Sleep -Milliseconds $WaitMs
Write-Output "clicked $Button at $X,$Y"
