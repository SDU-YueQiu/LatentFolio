Add-Type -AssemblyName System.Drawing
$s = [System.Drawing.Bitmap]::FromFile('D:\Projects\LatentFolio\docs\shots\main.jpg')
"size $($s.Width)x$($s.Height)"
# 网格滚动条：右边缘附近的竖条
$x = $s.Width - 60
for ($y = 90; $y -lt 1290; $y += 3) {
    $c = $s.GetPixel($x, $y)
    if ($c.R -lt 235) { "x=$x y=$y  #{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B }
}
$s.Dispose()
