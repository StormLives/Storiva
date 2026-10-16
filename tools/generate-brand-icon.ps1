# Generates the original Storiva application icon (multi-size .ico + preview PNG).
# Design: an "S" built from three storage shelves on a rounded teal square.
# Usage (Windows PowerShell 5.1 or PowerShell 7 on Windows):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\generate-brand-icon.ps1
param(
  [string]$OutDir = (Join-Path $PSScriptRoot '..\src\Storiva.App\Assets\Brand')
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object Drawing.Drawing2D.GraphicsPath
  $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
  if ($d -le 0.5) { $p.AddRectangle((New-Object Drawing.RectangleF $x, $y, $w, $h)); return $p }
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

function New-BrandBitmap([int]$s) {
  $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
  $inset = [Math]::Max(0.5, $s * 0.03)
  $bg = New-RoundedRect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.22)
  $top = [Drawing.Color]::FromArgb(255, 0x13, 0x86, 0x82)     # teal 600-ish
  $bottom = [Drawing.Color]::FromArgb(255, 0x0B, 0x5C, 0x5A)  # teal 800-ish
  $brush = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.RectangleF 0, 0, $s, $s), $top, $bottom, 90
  $g.FillPath($brush, $bg)

  # "S" from three shelves joined by two short uprights.
  $t  = [Math]::Max(2.0, $s * 0.125)   # shelf thickness
  $x0 = $s * 0.25; $x1 = $s * 0.75
  $yTop = $s * 0.235; $yMid = ($s - $t) / 2; $yBot = $s * 0.765 - $t
  $r = $t / 2
  $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
  foreach ($y in $yTop, $yMid, $yBot) { $g.FillPath($white, (New-RoundedRect $x0 $y ($x1 - $x0) $t $r)) }
  $g.FillRectangle($white, $x0, $yTop + $r, $t, ($yMid + $r) - ($yTop + $r))
  $g.FillRectangle($white, $x1 - $t, $yMid + $r, $t, ($yBot + $r) - ($yMid + $r))

  # Amber status dot on the top shelf (only where it stays legible).
  if ($s -ge 32) {
    $dot = $t * 0.42
    $amber = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 0xF2, 0xB1, 0x3D))
    $g.FillEllipse($amber, $x1 - $t * 0.5 - $dot, $yTop + ($t - $dot * 2) / 2, $dot * 2, $dot * 2)
  }
  $g.Dispose()
  return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $b = New-BrandBitmap $s; $ms = New-Object IO.MemoryStream
  $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
  ,$ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'storiva.ico'), $ico.ToArray())
[IO.File]::WriteAllBytes((Join-Path $OutDir 'storiva-256.png'), $pngs[-1])
"Wrote $(Join-Path $OutDir 'storiva.ico') ($($sizes -join ', ') px)"
