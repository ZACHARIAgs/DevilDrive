<#
.SYNOPSIS
    Rebuilds the app icon files from one square PNG (transparent background, 512x512 or larger).

.DESCRIPTION
    Writes assets\vault.png (512x512), which the SolidWorks add-in embeds and scales for the task
    pane, and assets\vault.ico (16-256 px), which is the desktop app's icon.

.EXAMPLE
    .\Make-AppIcon.ps1 -Source C:\path\to\new-icon.png
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Source)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Resize-Image([System.Drawing.Image]$image, [int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($image, 0, 0, $size, $size)
    $g.Dispose()
    return $bitmap
}

function Get-PngBytes([System.Drawing.Bitmap]$bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $stream.ToArray()
}

function Get-DibBytes([System.Drawing.Bitmap]$bitmap) {
    # Icon-style DIB: BITMAPINFOHEADER (height doubled), bottom-up BGRA pixels, then a 1-bit AND mask.
    $w = $bitmap.Width; $h = $bitmap.Height
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([UInt32]40); $writer.Write([Int32]$w); $writer.Write([Int32]($h * 2))
    $writer.Write([UInt16]1); $writer.Write([UInt16]32); $writer.Write([UInt32]0)
    $writer.Write([UInt32]($w * $h * 4 + $maskStride * $h))
    $writer.Write([Int32]0); $writer.Write([Int32]0); $writer.Write([UInt32]0); $writer.Write([UInt32]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $writer.Write([Byte]$c.B); $writer.Write([Byte]$c.G); $writer.Write([Byte]$c.R); $writer.Write([Byte]$c.A)
        }
    }
    $writer.Write((New-Object Byte[] ($maskStride * $h)))  # alpha channel does the masking
    $writer.Flush()
    return , $stream.ToArray()
}

$image = [System.Drawing.Image]::FromFile((Resolve-Path $Source).Path)
try {
    $master = Resize-Image $image 512
    $master.Save((Join-Path $assets 'vault.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $master.Dispose()

    # Classic 32-bit bitmaps below 256 px and PNG at 256 px. Some parts of Windows (e.g. the
    # large preview in Start search) show a generic icon when smaller entries are PNG.
    $sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
    $images = foreach ($size in $sizes) {
        $bitmap = Resize-Image $image $size
        if ($size -ge 256) { , (Get-PngBytes $bitmap) } else { , (Get-DibBytes $bitmap) }
        $bitmap.Dispose()
    }

    $out = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($out)
    $writer.Write([UInt16]0)              # reserved
    $writer.Write([UInt16]1)              # type: icon
    $writer.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $dim = if ($size -ge 256) { 0 } else { $size }
        $writer.Write([Byte]$dim)         # width (0 = 256)
        $writer.Write([Byte]$dim)         # height
        $writer.Write([Byte]0)            # palette
        $writer.Write([Byte]0)            # reserved
        $writer.Write([UInt16]1)          # color planes
        $writer.Write([UInt16]32)         # bits per pixel
        $writer.Write([UInt32]$images[$i].Length)
        $writer.Write([UInt32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write($bytes) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $assets 'vault.ico'), $out.ToArray())
    $writer.Dispose()
}
finally {
    $image.Dispose()
}

Write-Host "Wrote $assets\vault.png and $assets\vault.ico"
