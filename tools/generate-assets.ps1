Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([System.Drawing.Graphics]$g, [float]$x, [float]$y, [float]$s) {
    $g.SmoothingMode = 'AntiAlias'
    $bgRect = New-Object System.Drawing.RectangleF($x, $y, $s, $s)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgRect, [System.Drawing.Color]::FromArgb(255, 32, 44, 60), [System.Drawing.Color]::FromArgb(255, 11, 15, 21), 60)
    $path = New-RoundedPath $x $y $s $s ($s * 0.22)
    $g.FillPath($bg, $path)
    $border = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 120, 160, 200), [float]($s * 0.012))
    $g.DrawPath($border, $path)

    $gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(28, 255, 255, 255), [float]($s * 0.008))
    foreach ($i in 1..3) {
        $gy = $y + $s * (0.2 + 0.2 * $i)
        $g.DrawLine($gridPen, $x + $s * 0.14, $gy, $x + $s * 0.86, $gy)
    }

    $pts = @(
        @(0.12, 0.62), @(0.28, 0.62), @(0.36, 0.40), @(0.46, 0.74), @(0.56, 0.24), @(0.66, 0.58), @(0.74, 0.50), @(0.88, 0.50)
    ) | ForEach-Object { New-Object System.Drawing.PointF([float]($x + $s * $_[0]), [float]($y + $s * $_[1])) }

    $lineBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF([float]($x + $s * 0.1), [float]$y)),
        (New-Object System.Drawing.PointF([float]($x + $s * 0.9), [float]$y)),
        [System.Drawing.Color]::FromArgb(255, 45, 212, 191), [System.Drawing.Color]::FromArgb(255, 76, 154, 255))
    $glow = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(50, 45, 212, 191), [float]($s * 0.07))
    $glow.LineJoin = 'Round'; $glow.StartCap = 'Round'; $glow.EndCap = 'Round'
    $g.DrawLines($glow, $pts)
    $pen = New-Object System.Drawing.Pen($lineBrush, [float]($s * 0.04))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLines($pen, $pts)

    $r = $s * 0.045
    $last = $pts[-1]
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), $last.X - $r, $last.Y - $r, $r * 2, $r * 2)
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Icon $g 0 0 $size
    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

# icon.png
$icon = New-IconBitmap 512
$icon.Save((Join-Path $out 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# icon.ico (PNG-compressed frames)
$sizes = 16, 32, 48, 64, 128, 256
$frames = New-Object System.Collections.Generic.List[byte[]]
foreach ($sz in $sizes) { $frames.Add([byte[]](Get-PngBytes (New-IconBitmap $sz))) }
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $out 'icon.ico'), $ms.ToArray())

# logo.png
$logo = New-Object System.Drawing.Bitmap(1200, 320, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($logo)
$g.Clear([System.Drawing.Color]::Transparent)
$g.TextRenderingHint = 'AntiAliasGridFit'
Draw-Icon $g 20 20 280
$titleFont = New-Object System.Drawing.Font('Segoe UI Semibold', 110, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subFont = New-Object System.Drawing.Font('Segoe UI', 44, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('PMON', $titleFont, [System.Drawing.Brushes]::White, 330, 48)
$g.DrawString('Process activity monitor & recorder', $subFont, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 45, 212, 191))), 336, 190)
$g.Dispose()
$logo.Save((Join-Path $out 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)

Write-Host "Assets written to $out"
