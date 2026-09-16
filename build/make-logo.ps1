# Renders the Moonlight Tunneling brand assets with WPF, so they can be
# regenerated from source instead of living only as binaries:
#   assets\app.ico        multi-size app/tray icon (16-256)
#   assets\logo.png       512px colour icon (README, release notes)
#   assets\app_logo.png   white mark for the purple window header
#   docs\banner.png       README banner
# Run with: powershell -STA -File build\make-logo.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path -Parent $PSScriptRoot

function Color([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush([string]$hex) { $b = New-Object System.Windows.Media.SolidColorBrush (Color $hex); $b.Freeze(); $b }
function Gradient([string]$from, [string]$to) {
    $g = New-Object System.Windows.Media.LinearGradientBrush((Color $from), (Color $to), (New-Object System.Windows.Point 0, 0), (New-Object System.Windows.Point 1, 1))
    $g.Freeze(); $g
}

# Four-point sparkle with concave sides.
function Sparkle([double]$cx, [double]$cy, [double]$r) {
    $g = New-Object System.Windows.Media.StreamGeometry
    $ctx = $g.Open()
    $c = New-Object System.Windows.Point $cx, $cy
    $ctx.BeginFigure((New-Object System.Windows.Point $cx, ($cy - $r)), $true, $true)
    $ctx.QuadraticBezierTo($c, (New-Object System.Windows.Point ($cx + $r), $cy), $true, $true)
    $ctx.QuadraticBezierTo($c, (New-Object System.Windows.Point $cx, ($cy + $r)), $true, $true)
    $ctx.QuadraticBezierTo($c, (New-Object System.Windows.Point ($cx - $r), $cy), $true, $true)
    $ctx.QuadraticBezierTo($c, (New-Object System.Windows.Point $cx, ($cy - $r)), $true, $true)
    $ctx.Close()
    $g.Freeze()
    $g
}

# Crescent moon: a disc with an offset disc cut out of it.
function Crescent([double]$x, [double]$y, [double]$s) {
    $outer = New-Object System.Windows.Media.EllipseGeometry((New-Object System.Windows.Point ($x + 0.46 * $s), ($y + 0.52 * $s)), (0.30 * $s), (0.30 * $s))
    $cut = New-Object System.Windows.Media.EllipseGeometry((New-Object System.Windows.Point ($x + 0.60 * $s), ($y + 0.41 * $s)), (0.255 * $s), (0.255 * $s))
    $g = New-Object System.Windows.Media.CombinedGeometry([System.Windows.Media.GeometryCombineMode]::Exclude, $outer, $cut)
    $g.Freeze()
    $g
}

# The mark (moon + sparkles) inside a square of side $s at ($x,$y).
function DrawMark($dc, [double]$x, [double]$y, [double]$s, $fill) {
    $dc.DrawGeometry($fill, $null, (Crescent $x $y $s))
    if ($s -ge 24) {
        $dc.DrawGeometry($fill, $null, (Sparkle ($x + 0.67 * $s) ($y + 0.37 * $s) (0.085 * $s)))
        $dc.DrawGeometry($fill, $null, (Sparkle ($x + 0.79 * $s) ($y + 0.58 * $s) (0.048 * $s)))
    }
}

function DrawIcon($dc, [double]$s) {
    $bg = Gradient "#B8A8FF" "#7A68DE"
    $dc.DrawRoundedRectangle($bg, $null, (New-Object System.Windows.Rect 0, 0, $s, $s), (0.23 * $s), (0.23 * $s))
    # soft top highlight
    $hl = New-Object System.Windows.Media.LinearGradientBrush((Color "#33FFFFFF"), (Color "#00FFFFFF"), 90)
    $dc.DrawRoundedRectangle($hl, $null, (New-Object System.Windows.Rect 0, 0, $s, ($s * 0.55)), (0.23 * $s), (0.23 * $s))
    DrawMark $dc 0 0 $s (Brush "#FFFFFF")
}

function Render([int]$w, [int]$h, [scriptblock]$draw) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    & $draw $dc
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $rtb.Freeze()
    $rtb
}

function SavePng($bitmap, [string]$path) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $fs = [System.IO.File]::Create($path)
    try { $enc.Save($fs) } finally { $fs.Dispose() }
}

function PngBytes($bitmap) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    $ms.ToArray()
}

# Classic BMP-in-ICO frame (straight alpha, bottom-up, empty AND mask):
# every Windows icon loader, System.Drawing included, reads these.
function DibBytes($bitmap, [int]$s) {
    $conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $s * 4
    $pixels = New-Object byte[] ($stride * $s)
    $conv.CopyPixels($pixels, $stride, 0)
    $maskRow = [int](([Math]::Floor(($s + 31) / 32)) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($stride * $s)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($row = $s - 1; $row -ge 0; $row--) { $bw.Write($pixels, $row * $stride, $stride) }
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
    $ms.ToArray()
}

# ---- icon ----
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = Render $s $s { param($dc) DrawIcon $dc $s }
    $frames += , @($s, $(if ($s -ge 256) { PngBytes $bmp } else { DibBytes $bmp $s }))
}
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = [byte[]]$f[1]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$data.Length); $w.Write([int]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root "assets\app.ico"), $ico.ToArray())

# ---- colour logo ----
SavePng (Render 512 512 { param($dc) DrawIcon $dc 512 }) (Join-Path $root "assets\logo.png")

# ---- white header mark ----
SavePng (Render 256 256 { param($dc) DrawMark $dc 0 0 256 (Brush "#FFFFFF") }) (Join-Path $root "assets\app_logo.png")

# ---- README banner ----
$bw_ = 1600; $bh = 480
$banner = Render $bw_ $bh {
    param($dc)
    $dc.DrawRectangle((Gradient "#A996F7" "#6F5DD3"), $null, (New-Object System.Windows.Rect 0, 0, $bw_, $bh))
    $glow = New-Object System.Windows.Media.RadialGradientBrush((Color "#40FFFFFF"), (Color "#00FFFFFF"))
    $glow.Center = New-Object System.Windows.Point 0.22, 0.35
    $glow.GradientOrigin = $glow.Center
    $glow.RadiusX = 0.45; $glow.RadiusY = 0.9
    $dc.DrawRectangle($glow, $null, (New-Object System.Windows.Rect 0, 0, $bw_, $bh))
    $white = Brush "#FFFFFF"
    $faint = Brush "#99FFFFFF"
    foreach ($st in @(@(1180, 90, 10), @(1420, 150, 14), @(1320, 360, 8), @(1500, 300, 6), @(980, 400, 7), @(1080, 60, 5))) {
        $dc.DrawGeometry($faint, $null, (Sparkle $st[0] $st[1] $st[2]))
    }
    DrawMark $dc 110 90 300 $white
    $tf = New-Object System.Windows.Media.Typeface((New-Object System.Windows.Media.FontFamily "Segoe UI"), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::SemiBold, [System.Windows.FontStretches]::Normal)
    $tf2 = New-Object System.Windows.Media.Typeface("Segoe UI")
    $title = New-Object System.Windows.Media.FormattedText("Moonlight Tunneling", [Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $tf, 92, $white, 1.0)
    $sub = New-Object System.Windows.Media.FormattedText("Per-app split tunneling for Windows", [Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $tf2, 38, (Brush "#EDE8FF"), 1.0)
    $dc.DrawText($title, (New-Object System.Windows.Point 450, 160))
    $dc.DrawText($sub, (New-Object System.Windows.Point 456, 280))
}
New-Item -ItemType Directory -Force (Join-Path $root "docs") | Out-Null
SavePng $banner (Join-Path $root "docs\banner.png")
"assets written"
