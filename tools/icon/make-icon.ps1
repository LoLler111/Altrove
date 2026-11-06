# Draws the Windows icon from the Android launcher icon (android/app/src/main/res/drawable/ic_launcher_foreground.xml):
# two screens in the accent colour on the app's dark background, as a rounded square.
# Writes src/Altrove.App/Assets/Altrove.ico (16-256 px, PNG entries) and Altrove-64.png for the window.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path (Split-Path $PSScriptRoot)
$assets = Join-Path $root 'src\Altrove.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$background = [Windows.Media.Color]::FromRgb(0x15, 0x16, 0x1B)
$accent = [Windows.Media.Color]::FromRgb(0x7F, 0xA7, 0xF5)

function Render([int]$size) {
    # The Android viewport is 108 units; the drawing sits in 26..84, shown here in a 72-unit square around it.
    $scale = $size / 72.0
    $offset = 18.0
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $radius = $size * 0.22
    $dc.DrawRoundedRectangle((New-Object Windows.Media.SolidColorBrush $background), $null,
        (New-Object Windows.Rect 0, 0, $size, $size), $radius, $radius)
    $brush = New-Object Windows.Media.SolidColorBrush $accent
    function Box($x1, $y1, $x2, $y2) {
        New-Object Windows.Rect (($x1 - $offset) * $scale), (($y1 - $offset) * $scale), (($x2 - $x1) * $scale), (($y2 - $y1) * $scale)
    }
    # The screen you have: an outline (at least one pixel wide when tiny).
    $stroke = [Math]::Max(1.0, 3 * $scale)
    $dc.DrawRectangle($null, (New-Object Windows.Media.Pen $brush, $stroke), (Box 30 40 60 60))
    # Its copy, further away, filled, and its stand.
    $dc.DrawRectangle($brush, $null, (Box 54 50 80 68))
    $dc.DrawRectangle($brush, $null, (Box 62 71 72 74))
    $dc.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(Render $s) }

# ICO: header, one 16-byte entry per image, then the PNG data.
$ico = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ico
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offsetData = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offsetData)
    $offsetData += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'Altrove.ico'), $ico.ToArray())
[IO.File]::WriteAllBytes((Join-Path $assets 'Altrove-64.png'), (Render 64))
Write-Host "Icona: $assets\Altrove.ico ($($sizes -join ', ') px)"
