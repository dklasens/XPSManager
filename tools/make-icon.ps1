# Renders the XPS Manager mark to PNGs and a multi-size .ico using WPF's vector renderer.
# The mark: graphite tile, a cobalt bar crossed and cut by a light bar - a layered "X".
# Geometry lives in a 256-unit box and must stay in sync with Themes/Tokens.xaml (XpsMarkDrawing).
#   pwsh -File tools/make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root   = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'src/XpsManager.App/Assets'
$sizes  = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Brush($hex) { $b = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
$graphite = Brush '#161B22'   # oklch(22% 0.016 260)
$light    = Brush '#EBEFF4'   # oklch(95% 0.008 250)
$cobalt   = Brush '#61A6FF'   # oklch(72% 0.15 256)

function Bar($brush, $thickness, $x1, $y1, $x2, $y2) {
    $pen = [Windows.Media.Pen]::new($brush, $thickness)
    $pen.StartLineCap = $pen.EndLineCap = 'Flat'
    [Windows.Media.GeometryDrawing]::new($null, $pen, [Windows.Media.LineGeometry]::new([Windows.Point]::new($x1, $y1), [Windows.Point]::new($x2, $y2)))
}

$group = [Windows.Media.DrawingGroup]::new()
$tile  = [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new(8, 8, 240, 240), 44, 44)
$group.Children.Add([Windows.Media.GeometryDrawing]::new($graphite, $null, $tile))
$group.Children.Add((Bar $cobalt   38 204 52 52 204))   # rising stroke, underneath
$group.Children.Add((Bar $graphite 62 52 52 204 204))   # knockout: cuts the cobalt bar
$group.Children.Add((Bar $light    38 52 52 204 204))   # falling stroke, on top
$group.Freeze()

$pngs = foreach ($s in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($s / 256, $s / 256))
    $dc.DrawDrawing($group)
    $dc.Pop(); $dc.Close()
    $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new($s, $s, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = [IO.MemoryStream]::new(); $enc.Save($ms)
    [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
}

# ICO container with PNG-compressed entries (supported since Vista).
$ico = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$p.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $w.Write($p.Bytes) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $outDir 'XpsManager.ico'), $ico.ToArray())
[IO.File]::WriteAllBytes((Join-Path $outDir 'XpsManager.png'), ($pngs | Where-Object Size -eq 256).Bytes)
"Wrote XpsManager.ico ($($sizes -join ', ') px) and XpsManager.png"
