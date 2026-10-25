# Erzeugt assets/icon.ico aus assets/icon-source.png (nur .NET/WPF, keine Pakete).
# Aufruf: powershell -STA -NoProfile -File tools\make-icon.ps1 [-Preview <png>]
# Gross (48..256): Original zugeschnitten und verkleinert. Klein (16, 24, 32): vereinfachte Zeichnung (kein Leuchten,
# hellerer Hintergrund, feiner heller Rand, dicke Grundlinie, bei 16 ohne Grundlinie).
param([string]$Preview = '')
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$srcPath = Join-Path $root 'assets\icon-source.png'
$icoPath = Join-Path $root 'assets\icon.ico'
function C($hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function G($d) { $g = [System.Windows.Media.Geometry]::Parse($d); $g.Freeze(); $g }

# --- Quelle laden und auf das abgerundete Quadrat zuschneiden ---
$dec = [System.Windows.Media.Imaging.BitmapDecoder]::Create([uri]$srcPath, 'None', 'None')
$src = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($dec.Frames[0], [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
$w = $src.PixelWidth; $h = $src.PixelHeight
$px = New-Object byte[] ($w * $h * 4); $src.CopyPixels($px, $w * 4, 0)
$minx = $w; $maxx = 0; $miny = $h; $maxy = 0
for ($y = 0; $y -lt $h; $y++) { for ($x = 0; $x -lt $w; $x++) {
    if ($px[($y * $w + $x) * 4 + 3] -gt 200) {
        if ($x -lt $minx) { $minx = $x }; if ($x -gt $maxx) { $maxx = $x }
        if ($y -lt $miny) { $miny = $y }; if ($y -gt $maxy) { $maxy = $y } } } }
$side = [Math]::Max($maxx - $minx, $maxy - $miny) + 1
$cx = ($minx + $maxx + 1) / 2.0; $cy = ($miny + $maxy + 1) / 2.0
$x0 = [int][Math]::Round($cx - $side / 2.0); $y0 = [int][Math]::Round($cy - $side / 2.0)
Write-Host "Koerper $minx,$miny - $maxx,$maxy  Quadrat $side bei $x0,$y0"
$crop = New-Object System.Windows.Media.Imaging.CroppedBitmap($src, (New-Object System.Windows.Int32Rect($x0, $y0, $side, $side)))
$crop.Freeze()

# --- Zeichnung der kleinen Variante (Koordinaten wie im Original, 0..$side nach Abzug von $x0,$y0) ---
$glyph = G ('M 330,290 L 488,290 L 627,476 L 767,290 L 925,290 L 700,600 L 700,717 L 788,717 L 627,893 L 466,717 L 554,717 L 554,600 Z')
$bar = G 'M 440,925 L 815,925 L 815,1010 L 440,1010 Z'

function Draw-Small($dc, $s) {
    $r = $s * 0.225
    $bg = New-Object System.Windows.Media.LinearGradientBrush((C '#3D8BFF'), (C '#1550D6'), 90)
    $edge = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush((C '#8CFFFFFF'))), 1)
    $dc.DrawRoundedRectangle($bg, $edge, (New-Object System.Windows.Rect(0.5, 0.5, ($s - 1), ($s - 1))), $r, $r)
    $withBar = $s -ge 24
    # Glyph in die Mitte, groesser als im Original
    $gs = if ($withBar) { 1.12 } else { 1.30 }
    $cyGlyph = if ($withBar) { 650 } else { 586 }
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform(($s / $side), ($s / $side))))
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform(($side / 2.0), ($side / 2.0))))
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($gs, $gs)))
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform((-627), (-$cyGlyph))))
    $fill = New-Object System.Windows.Media.LinearGradientBrush((C '#FFFFFF'), (C '#CDEBFF'), 90)
    $pen = New-Object System.Windows.Media.Pen($fill, 26); $pen.LineJoin = 'Round'
    $dc.DrawGeometry($fill, $pen, $glyph)
    if ($withBar) { $dc.DrawGeometry($fill, $pen, $bar) }
    $dc.Pop(); $dc.Pop(); $dc.Pop(); $dc.Pop()
}
function Draw-Large($dc, $s) {
    $dc.DrawImage($crop, (New-Object System.Windows.Rect(0, 0, $s, $s)))
}
# Feiner heller Rand innen entlang der Kante (aus dem Alphakanal, folgt also der echten Form), damit das dunkle Original
# auf dunklem Grund nicht verschwindet. Das Original-PNG bleibt unveraendert.
function Add-Border($bmp, $s) {
    $t = [Math]::Max(1.0, $s / 128.0)
    $r = [int][Math]::Ceiling($t)
    $stride = $s * 4
    $p = New-Object byte[] ($stride * $s); $bmp.CopyPixels($p, $stride, 0)
    $o = $p.Clone()
    $offs = New-Object System.Collections.Generic.List[int[]]
    for ($dy = -$r; $dy -le $r; $dy++) { for ($dx = -$r; $dx -le $r; $dx++) {
        if (($dx * $dx + $dy * $dy) -le ($t * $t + 0.25)) { $offs.Add(@($dx, $dy)) } } }
    $k = 0.65; $cr = 190.0; $cg = 215.0; $cb = 255.0 # helles Blau
    for ($y = 0; $y -lt $s; $y++) { for ($x = 0; $x -lt $s; $x++) {
        $i = $y * $stride + $x * 4; $a = $p[$i + 3]
        if ($a -eq 0) { continue }
        $min = 255
        foreach ($d in $offs) {
            $nx = $x + $d[0]; $ny = $y + $d[1]
            $na = if ($nx -lt 0 -or $ny -lt 0 -or $nx -ge $s -or $ny -ge $s) { 0 } else { $p[$ny * $stride + $nx * 4 + 3] }
            if ($na -lt $min) { $min = $na; if ($min -eq 0) { break } }
        }
        $wgt = $k * (1.0 - $min / 255.0)
        if ($wgt -le 0) { continue }
        $af = $a / 255.0
        $o[$i] = [byte][Math]::Min(255, $p[$i] * (1 - $wgt) + $cb * $af * $wgt)
        $o[$i + 1] = [byte][Math]::Min(255, $p[$i + 1] * (1 - $wgt) + $cg * $af * $wgt)
        $o[$i + 2] = [byte][Math]::Min(255, $p[$i + 2] * (1 - $wgt) + $cr * $af * $wgt)
    } }
    $res = [System.Windows.Media.Imaging.BitmapSource]::Create($s, $s, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32, $null, $o, $stride)
    $res.Freeze(); $res
}
function Render($s) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($dv, 'Fant')
    $dc = $dv.RenderOpen()
    if ($s -le 32) { Draw-Small $dc $s } else { Draw-Large $dc $s }
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($s, $s, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv); $rtb.Freeze()
    if ($s -gt 32) { Add-Border $rtb $s } else { $rtb }
}
function PngBytes($bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream; $enc.Save($ms); , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$bmps = @{}; $pngs = @{}
foreach ($s in $sizes) { $bmps[$s] = Render $s; $pngs[$s] = PngBytes $bmps[$s] }

# --- ICO schreiben (PNG-Eintraege) ---
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $len = $pngs[$s].Length
    $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($s in $sizes) { $bw.Write([byte[]]$pngs[$s]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
Write-Host "geschrieben: $icoPath ($($ms.Length) Bytes)"

# --- Symbole der Browser-Erweiterung (gleiche Bilder wie in der ICO: 16/32 vereinfacht, 48/128 Original) ---
$extIcons = Join-Path $root 'extension\icons'
foreach ($s in 16, 32, 48, 128) { [System.IO.File]::WriteAllBytes((Join-Path $extIcons "$s.png"), [byte[]]$pngs[$s]) }
Write-Host "geschrieben: $extIcons (16, 32, 48, 128)"

# --- Vorschau: hell und dunkel, echte Groesse und vergroessert (ohne Glaettung) ---
if ($Preview -ne '') {
    function Magnify($bmp, $k) {
        $n = $bmp.PixelWidth * $k
        $dv = New-Object System.Windows.Media.DrawingVisual
        [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($dv, 'NearestNeighbor')
        $dc = $dv.RenderOpen(); $dc.DrawImage($bmp, (New-Object System.Windows.Rect(0, 0, $n, $n))); $dc.Close()
        $r = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($n, $n, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $r.Render($dv); $r
    }
    $W = 1240; $bandH = 300
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $bands = @(@{ bg = '#F2F2F5'; y = 0 }, @{ bg = '#1E1E22'; y = $bandH }, @{ bg = '#0C0C0E'; y = 2 * $bandH })
    foreach ($b in $bands) {
        $dc.DrawRectangle((New-Object System.Windows.Media.SolidColorBrush((C $b.bg))), $null, (New-Object System.Windows.Rect(0, $b.y, $W, $bandH)))
        $x = 20; $base = $b.y + 20 + 256
        foreach ($s in $sizes) { $dc.DrawImage($bmps[$s], (New-Object System.Windows.Rect($x, ($base - $s), $s, $s))); $x += $s + 20 }
        $x += 40
        foreach ($s in 16, 24, 32) { $k = 6; $m = Magnify $bmps[$s] $k; $n = $s * $k
            $dc.DrawImage($m, (New-Object System.Windows.Rect($x, ($base - $n), $n, $n))); $x += $n + 16 }
    }
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($W, (3 * $bandH), 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $fs = [System.IO.File]::Create($Preview); $enc.Save($fs); $fs.Close()
    Write-Host "Vorschau: $Preview"
}
