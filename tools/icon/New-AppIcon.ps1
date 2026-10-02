<#
.SYNOPSIS
    Generates the FerretSharp app icon: "FS" monogram with angular letters (F dark, S in the app blue) on a light
    rounded tile. Own letter shapes, no font involved.

.DESCRIPTION
    Geometry on a 256-unit canvas, rendered anti-aliased per size. Writes a multi-size .ico (16–48 px as 32-bit
    bitmaps, 256 px as PNG, as Windows expects) and a 256 px PNG preview.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/icon/New-AppIcon.ps1
#>
param(
    [string]$IconPath = (Join-Path $PSScriptRoot '..\..\src\FerretSharp.App\Assets\ferretsharp.ico'),
    [string]$PreviewPath = (Join-Path $PSScriptRoot 'ferretsharp-256.png')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$tileColor = [System.Drawing.Color]::FromArgb(255, 0xf9, 0xf9, 0xfb)
$borderColor = [System.Drawing.Color]::FromArgb(255, 0xd0, 0xd0, 0xd8)
$fColor = [System.Drawing.Color]::FromArgb(255, 0x1d, 0x1d, 0x22)   # --text (light theme), like "Ferret" in the brand
$sColor = [System.Drawing.Color]::FromArgb(255, 0x3b, 0x6f, 0xe0)   # --accent (light theme), like "Sharp"

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Polygon([float[]]$xy) {
    $points = for ($i = 0; $i -lt $xy.Count; $i += 2) { New-Object System.Drawing.PointF $xy[$i], $xy[$i + 1] }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon([System.Drawing.PointF[]]$points)
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0
    $g.ScaleTransform($k, $k)

    # tile: rounded square; the border stays about one device pixel wide at small sizes
    $border = [math]::Max(3.0, 1.0 / $k)
    $tile = New-RoundedRect (4 + $border / 2) (4 + $border / 2) (248 - $border) (248 - $border) 54
    $g.FillPath((New-Object System.Drawing.SolidBrush $tileColor), $tile)
    $g.DrawPath((New-Object System.Drawing.Pen $borderColor, $border), $tile)

    $w = 30; $c = 22; $top = 58; $bottom = 198

    # F: stem and two bars, top-left corner chamfered
    $f = New-Polygon @((44 + $c), $top, 122, $top, 122, ($top + $w), (44 + $w), ($top + $w), (44 + $w), 114, 108, 114, 108, 142,
        (44 + $w), 142, (44 + $w), $bottom, 44, $bottom, 44, ($top + $c))
    $g.FillPath((New-Object System.Drawing.SolidBrush $fColor), $f)

    # S: five straight strokes plus short terminals that turn the ends (without them it reads as "5");
    # outer corners chamfered, rotationally symmetric
    $l = 140; $r = 226; $tt = 16
    $s = New-Polygon @(
        ($r - $c), $top, ($l + $c), $top, $l, ($top + $c),
        $l, (142 - $c), ($l + $c), 142,
        ($r - $w), 142, ($r - $w), (198 - $w),
        ($l + $w), (198 - $w), ($l + $w), (198 - $w - $tt),
        $l, (198 - $w - $tt), $l, ($bottom - $c),
        ($l + $c), $bottom, ($r - $c), $bottom, $r, ($bottom - $c),
        $r, (114 + $c), ($r - $c), 114,
        ($l + $w), 114, ($l + $w), ($top + $w),
        ($r - $w), ($top + $w), ($r - $w), ($top + $w + $tt),
        $r, ($top + $w + $tt), $r, ($top + $c))
    $g.FillPath((New-Object System.Drawing.SolidBrush $sColor), $s)

    $g.Dispose()
    return $bmp
}

# 32-bit DIB as stored in .ico files: BITMAPINFOHEADER (height doubled), bottom-up BGRA rows, empty AND mask
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $maskRow = [int]([math]::Ceiling($size / 32.0) * 4)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $maskRow * $size)); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $p = $bmp.GetPixel($x, $y)
            $writer.Write([byte]$p.B); $writer.Write([byte]$p.G); $writer.Write([byte]$p.R); $writer.Write([byte]$p.A)
        }
    }
    $writer.Write((New-Object byte[] ($maskRow * $size)))
    $writer.Flush()
    return $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    if ($size -ge 256) {
        $png = New-Object System.IO.MemoryStream
        $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $png.ToArray()
        $bmp.Save([System.IO.Path]::GetFullPath($PreviewPath), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        $data = Get-DibBytes $bmp
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $data }
}

# ICONDIR + ICONDIRENTRY per image, then the image data
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dim = $(if ($image.Size -ge 256) { 0 } else { $image.Size })
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]$image.Data.Length); $writer.Write([int]$offset)
    $offset += $image.Data.Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image.Data) }
$writer.Flush()

$target = [System.IO.Path]::GetFullPath($IconPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $target ($($sizes -join ', ') px)"
