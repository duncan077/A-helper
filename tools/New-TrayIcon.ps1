<#
.SYNOPSIS
    Generates src/AcerHelper.App/Assets/tray.ico.

.DESCRIPTION
    Draws a fan glyph at 16/24/32/48 px and serialises a multi-size ICO.

    The disc is coloured rather than the glyph, because a tray icon has to stay
    legible on both light and dark taskbars - a white-on-transparent glyph
    disappears on a light theme.

    Everything is built inline in one buffer. PowerShell unrolls a byte[]
    returned from a function into the pipeline, which silently produced a 74-byte
    file when this used helper functions.

    Run once; the .ico is committed. Only re-run if the artwork changes.
#>

[CmdletBinding()]
param([string]$OutFile)

Add-Type -AssemblyName System.Drawing

if (-not $OutFile) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutFile = Join-Path $repoRoot 'src\AcerHelper.App\Assets\tray.ico'
}

$sizes = @(16, 24, 32, 48)
$blobs = New-Object 'System.Collections.Generic.List[byte[]]'

foreach ($size in $sizes) {

    # ---- draw -------------------------------------------------------------
    $bmp = New-Object System.Drawing.Bitmap($size, $size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $disc  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 214, 48, 49))
    $glyph = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))

    $g.FillEllipse($disc, 0.0, 0.0, [float]($size - 1), [float]($size - 1))

    $c = $size / 2.0
    $bw_ = $size * 0.22
    $bh_ = $size * 0.40
    for ($i = 0; $i -lt 3; $i++) {
        $state = $g.Save()
        $g.TranslateTransform([float]$c, [float]$c)
        $g.RotateTransform([float]($i * 120))
        $g.FillEllipse($glyph, [float](-$bw_ / 2), [float](-$bh_), [float]$bw_, [float]$bh_)
        $g.Restore($state)
    }
    $hub = $size * 0.20
    $g.FillEllipse($glyph, [float]($c - $hub / 2), [float]($c - $hub / 2), [float]$hub, [float]$hub)

    $g.Dispose(); $disc.Dispose(); $glyph.Dispose()

    # ---- serialise: BITMAPINFOHEADER + bottom-up BGRA + AND mask ----------
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)

    $w.Write([uint32]40)                    # biSize
    $w.Write([int32]$size)                  # biWidth
    $w.Write([int32]($size * 2))            # biHeight - XOR plus AND mask
    $w.Write([uint16]1)                     # biPlanes
    $w.Write([uint16]32)                    # biBitCount
    $w.Write([uint32]0)                     # biCompression = BI_RGB
    $w.Write([uint32]($size * $size * 4))   # biSizeImage
    $w.Write([int32]0); $w.Write([int32]0)
    $w.Write([uint32]0); $w.Write([uint32]0)

    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $px = $bmp.GetPixel($x, $y)
            $w.Write([byte]$px.B)
            $w.Write([byte]$px.G)
            $w.Write([byte]$px.R)
            $w.Write([byte]$px.A)
        }
    }

    # Unused with a 32bpp alpha channel, but the format requires it present.
    $maskRow = [int][math]::Ceiling($size / 8.0)
    if ($maskRow % 4 -ne 0) { $maskRow += 4 - ($maskRow % 4) }
    $zeroRow = New-Object byte[] $maskRow
    for ($y = 0; $y -lt $size; $y++) { $w.Write($zeroRow, 0, $maskRow) }

    $w.Flush()
    $blobs.Add([byte[]]$ms.ToArray())
    $w.Dispose(); $ms.Dispose(); $bmp.Dispose()
}

# ---- ICONDIR + ICONDIRENTRY table + image data ---------------------------
$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

$fs = [System.IO.File]::Create($OutFile)
$w = New-Object System.IO.BinaryWriter($fs)

$w.Write([uint16]0)                 # reserved
$w.Write([uint16]1)                 # type = icon
$w.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $w.Write([byte]$sizes[$i])
    $w.Write([byte]$sizes[$i])
    $w.Write([byte]0)               # palette colours
    $w.Write([byte]0)               # reserved
    $w.Write([uint16]1)             # planes
    $w.Write([uint16]32)            # bit count
    $w.Write([uint32]$blobs[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $blobs[$i].Length
}
for ($i = 0; $i -lt $sizes.Count; $i++) { $w.Write($blobs[$i], 0, $blobs[$i].Length) }

$w.Flush(); $w.Dispose(); $fs.Dispose()

$resolved = (Resolve-Path $OutFile).Path
Write-Host "Wrote $resolved ($((Get-Item $resolved).Length) bytes, sizes: $($sizes -join ', '))" -ForegroundColor Green
