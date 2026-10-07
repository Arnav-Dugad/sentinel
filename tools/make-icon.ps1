# Generates src/Sentinel.App/Assets/Sentinel.ico (multi-resolution) and Sentinel.png.
# Windows PowerShell 5.1: uses System.Drawing. Re-run after changing the design.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Sentinel.App\Assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    # Rounded square with a deep blue-to-teal gradient.
    $r = 56 * $s; $m = 8 * $s; $w = $size - 2 * $m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size),
        ([System.Drawing.Color]::FromArgb(255, 30, 58, 138)), ([System.Drawing.Color]::FromArgb(255, 13, 148, 136))
    $g.FillPath($brush, $path)
    # Subtle inner ring (the "observer").
    $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([Math]::Max(1.0, 10 * $s))
    $g.DrawEllipse($ringPen, 52 * $s, 52 * $s, 152 * $s, 152 * $s)
    # Pulse line.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.6, 18 * $s))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $pts = @(
        (New-Object System.Drawing.PointF (40 * $s), (132 * $s)),
        (New-Object System.Drawing.PointF (92 * $s), (132 * $s)),
        (New-Object System.Drawing.PointF (112 * $s), (92 * $s)),
        (New-Object System.Drawing.PointF (140 * $s), (172 * $s)),
        (New-Object System.Drawing.PointF (162 * $s), (120 * $s)),
        (New-Object System.Drawing.PointF (216 * $s), (120 * $s)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = New-Frame $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256) { $bmp.Save((Join-Path $OutDir 'Sentinel.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create((Join-Path $OutDir 'Sentinel.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $pngs[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($d in $pngs) { $bw.Write([byte[]]$d) }
$bw.Close()
Write-Output "Wrote $(Join-Path $OutDir 'Sentinel.ico')"
