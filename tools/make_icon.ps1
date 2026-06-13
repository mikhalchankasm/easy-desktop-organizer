# Генерация app.ico: темная скругленная «коробка» с цветными элементами внутри.
# Размеры 16–256, PNG-сжатие внутри ICO (поддерживается Vista+).
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([Drawing.Color]::Transparent)

    # Корпус коробки
    $m = [Math]::Max(1, $s * 0.04)
    $box = New-RoundedPath $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.18)
    $bg = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 30, 36, 44))
    $g.FillPath($bg, $box)
    $penW = [Math]::Max(1, $s * 0.05)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 0, 200, 255)), $penW
    $g.DrawPath($pen, $box)

    # 2x2 «элементы» в коробке
    $cell = $s * 0.26
    $gap = $s * 0.10
    $start = ($s - (2 * $cell + $gap)) / 2
    $colors = @(
        [Drawing.Color]::FromArgb(255, 249, 168, 37),  # желтый
        [Drawing.Color]::FromArgb(255, 67, 160, 71),   # зеленый
        [Drawing.Color]::FromArgb(255, 229, 57, 53),   # красный
        [Drawing.Color]::FromArgb(255, 30, 136, 229)   # синий
    )
    for ($i = 0; $i -lt 4; $i++) {
        $cx = $start + ($i % 2) * ($cell + $gap)
        $cy = $start + [Math]::Floor($i / 2) * ($cell + $gap)
        $cellPath = New-RoundedPath $cx $cy $cell $cell ([Math]::Max(1, $cell * 0.25))
        $br = New-Object Drawing.SolidBrush $colors[$i]
        $g.FillPath($br, $cellPath)
        $br.Dispose()
        $cellPath.Dispose()
    }

    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
}

# Контейнер ICO
$out = Join-Path $PSScriptRoot "..\src\DesktopOrganizer\app.ico"
$fs = [IO.File]::Create($out)
$bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type: icon
$bw.Write([uint16]$sizes.Count)   # count
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width (0 = 256)
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height
    $bw.Write([byte]0)            # palette
    $bw.Write([byte]0)            # reserved
    $bw.Write([uint16]1)          # planes
    $bw.Write([uint16]32)         # bpp
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Dispose()
$fs.Dispose()
"Icon written: $out"
