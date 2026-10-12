# Ember's logo, drawn as vector shapes and rendered with WPF: logo.png (256 px) and ember.ico (16 to 256 px) in the repo root.
# Run from anywhere: powershell -ExecutionPolicy Bypass -File tools\logo.ps1 [-Preview <folder>]
param([string]$Preview)
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot
function Brush($top, $bottom) {
  $b = New-Object Windows.Media.LinearGradientBrush ([Windows.Media.ColorConverter]::ConvertFromString($top)), ([Windows.Media.ColorConverter]::ConvertFromString($bottom)), 90
  $b.Freeze(); $b
}
function Solid($c) { $b = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($c)); $b.Freeze(); $b }

# on a 256 grid: a warm dark tile, a flame with a bright core, and sparks rising (left out where they'd only be noise)
function Render([int]$size) {
  $v = New-Object Windows.Media.DrawingVisual
  $dc = $v.RenderOpen()
  $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($size / 256), ($size / 256)))
  $tile = New-Object Windows.Rect 8, 8, 240, 240
  $dc.DrawRoundedRectangle((Brush "#FF2B1B14" "#FF120C0A"), $null, $tile, 56, 56)
  $glow = New-Object Windows.Media.RadialGradientBrush ([Windows.Media.ColorConverter]::ConvertFromString("#80F06A28")), ([Windows.Media.ColorConverter]::ConvertFromString("#00F06A28"))
  $glow.Center = New-Object Windows.Point 0.5, 0.78; $glow.GradientOrigin = $glow.Center; $glow.RadiusX = 0.55; $glow.RadiusY = 0.45; $glow.Freeze()
  $dc.DrawRoundedRectangle($glow, $null, $tile, 56, 56)
  $outer = [Windows.Media.Geometry]::Parse("M128,218 C83,218 56,186 58,148 C60,114 84,96 94,64 C104,86 114,98 126,106 C128,78 142,54 162,36 C163,70 178,92 190,114 C202,136 203,156 198,170 C190,198 162,218 128,218 Z")
  $dc.DrawGeometry((Brush "#FFFFC35A" "#FFE2401C"), $null, $outer)
  $inner = [Windows.Media.Geometry]::Parse("M128,204 C104,204 90,186 92,164 C94,146 108,136 114,118 C121,132 128,138 137,142 C141,128 148,120 156,112 C163,132 170,146 168,166 C166,188 150,204 128,204 Z")
  $dc.DrawGeometry((Brush "#FFFFF6CF" "#FFFFB347"), $null, $inner)
  if ($size -ge 48) {
    foreach ($s in @(@(196, 70, 7), @(214, 46, 4.5), @(70, 92, 4))) { $dc.DrawEllipse((Solid "#FFFFC35A"), $null, (New-Object Windows.Point $s[0], $s[1]), $s[2], $s[2]) }
  }
  $dc.Pop(); $dc.Close()
  $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
  $bmp.Render($v)
  $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
  $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $ms = New-Object IO.MemoryStream; $enc.Save($ms); ,$ms.ToArray()     # the comma keeps it one byte[] (PowerShell would unroll it)
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @{}; foreach ($s in $sizes) { $pngs[$s] = Render $s }
[IO.File]::WriteAllBytes("$root\logo.png", $pngs[256])
# .ico with PNG images inside (Windows Vista and later): header, one directory entry per size, then the images
$ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
  $d = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$pngs[$s].Length); $w.Write([uint32]$offset); $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $w.Write([byte[]]$pngs[$s]) }
$w.Flush(); [IO.File]::WriteAllBytes("$root\ember.ico", $ico.ToArray())
if ($Preview) { foreach ($s in 16, 32, 48) { [IO.File]::WriteAllBytes("$Preview\logo_$s.png", $pngs[$s]) } }
"logo.png and ember.ico ($($sizes -join ', ') px) written to $root"
