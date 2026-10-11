# Generuje ikonę programu: kontur białej słuchawki na niebieskim tle.
# Uruchomienie (Windows PowerShell 5.1):  powershell -STA -File tools\make-icon.ps1
# Wynik: Source\Assets\app.ico (wiele rozmiarów) oraz docs\images\icon.png.
param([string]$OutDir = (Join-Path $PSScriptRoot '..'))

Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'

function New-Brush([byte]$r, [byte]$g, [byte]$b) {
    $br = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb($r, $g, $b))
    $br.Freeze(); return $br
}

# Rysunek w układzie 100 x 100 (skalowany do każdego rozmiaru osobno, żeby małe ikony były ostre).
function Render-Icon([int]$size) {
    $k = $size / 100.0
    $blue = New-Brush 0x1F 0x6F 0xB8
    $white = New-Brush 255 255 255
    $outline = 4.5     # grubość białego konturu (w układzie 100 x 100)
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    $dc.DrawRoundedRectangle($blue, $null, ([System.Windows.Rect]::new(0, 0, $size, $size)), 22 * $k, 22 * $k)

    # Słuchawka: gruby łuk (uchwyt) z dwiema zaokrąglonymi "muszlami" na końcach, zwróconymi do środka łuku.
    $R = 36.0; $bodyW = 18.0; $padW = 27.0; $padLen = 12.0
    $a1 = 95.0 * [Math]::PI / 180; $a2 = 175.0 * [Math]::PI / 180      # kąty łuku (ekran: y rośnie w dół)
    # środek okręgu dobieramy tak, żeby cała słuchawka (z muszlami) leżała w środku kwadratu
    $xs = @(); $ys = @()
    for ($d = 95; $d -le 175; $d += 5) { $ang = $d * [Math]::PI / 180; $xs += $R * [Math]::Cos($ang); $ys += $R * [Math]::Sin($ang) }
    foreach ($an in @($a1, $a2)) { $xs += ($R - $padLen) * [Math]::Cos($an); $ys += ($R - $padLen) * [Math]::Sin($an) }
    $w = [Math]::Max($padW, $bodyW) / 2
    $cx = 50.0 - (($xs | Measure-Object -Minimum).Minimum + ($xs | Measure-Object -Maximum).Maximum) / 2
    $cy = 50.0 - (($ys | Measure-Object -Minimum).Minimum + ($ys | Measure-Object -Maximum).Maximum) / 2
    $p1 = [System.Windows.Point]::new(($cx + $R * [Math]::Cos($a1)) * $k, ($cy + $R * [Math]::Sin($a1)) * $k)
    $p2 = [System.Windows.Point]::new(($cx + $R * [Math]::Cos($a2)) * $k, ($cy + $R * [Math]::Sin($a2)) * $k)

    $geo = New-Object System.Windows.Media.StreamGeometry
    $ctx = $geo.Open()
    $ctx.BeginFigure($p2, $false, $false)
    $ctx.ArcTo($p1, ([System.Windows.Size]::new($R * $k, $R * $k)), 0, $false, [System.Windows.Media.SweepDirection]::Counterclockwise, $true, $false)
    $ctx.Close(); $geo.Freeze()

    # Kontur: najpierw cała słuchawka białym (grubsze pędzle), potem węższymi pędzlami w kolorze tła – zostaje biała obwódka.
    foreach ($pass in 0, 1) {
        $brush = if ($pass -eq 0) { $white } else { $blue }
        $shrink = if ($pass -eq 0) { 0.0 } else { 2 * $outline }
        $body = [System.Windows.Media.Pen]::new($brush, ($bodyW - $shrink) * $k)
        $body.StartLineCap = 'Round'; $body.EndLineCap = 'Round'
        $dc.DrawGeometry($null, $body, $geo)

        # muszle: krótkie, grubsze odcinki od końca łuku w stronę środka okręgu
        $pad = [System.Windows.Media.Pen]::new($brush, ($padW - $shrink) * $k)
        $pad.StartLineCap = 'Round'; $pad.EndLineCap = 'Round'
        foreach ($a in @($a1, $a2)) {
            $from = [System.Windows.Point]::new(($cx + $R * [Math]::Cos($a)) * $k, ($cy + $R * [Math]::Sin($a)) * $k)
            $to = [System.Windows.Point]::new(($cx + ($R - $padLen) * [Math]::Cos($a)) * $k, ($cy + ($R - $padLen) * [Math]::Sin($a)) * $k)
            $dc.DrawLine($pad, $from, $to)
        }
    }
    $dc.Close()

    $rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    $pixels = New-Object byte[] ($size * $size * 4)
    $rtb.CopyPixels($pixels, $size * 4, 0)
    return @{ Size = $size; Png = $ms.ToArray(); Pixels = $pixels }
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @(foreach ($s in $sizes) { Render-Icon $s })

# Mały obraz jako klasyczny DIB (BMP 32-bit z kanałem alfa + maska AND) – najlepiej obsługiwany przez wszystkie programy.
function ConvertTo-Dib($img) {
    $n = $img.Size
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([UInt32]40); $w.Write([Int32]$n); $w.Write([Int32](2 * $n))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($n * $n * 4)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    $px = $img.Pixels
    for ($y = $n - 1; $y -ge 0; $y--) {          # DIB zapisuje wiersze od dołu
        for ($x = 0; $x -lt $n; $x++) {
            $i = ($y * $n + $x) * 4
            $a = $px[$i + 3]
            if ($a -gt 0 -and $a -lt 255) {          # Pbgra32 ma kolory przemnożone przez alfę
                $px[$i] = [byte][Math]::Min(255, [Math]::Round($px[$i] * 255.0 / $a))
                $px[$i + 1] = [byte][Math]::Min(255, [Math]::Round($px[$i + 1] * 255.0 / $a))
                $px[$i + 2] = [byte][Math]::Min(255, [Math]::Round($px[$i + 2] * 255.0 / $a))
            }
            $w.Write($px[$i]); $w.Write($px[$i + 1]); $w.Write($px[$i + 2]); $w.Write($a)
        }
    }
    $mask = New-Object byte[] ((([int][Math]::Ceiling($n / 32.0)) * 4) * $n)
    $w.Write($mask)
    $w.Flush()
    return , $ms.ToArray()
}
foreach ($img in $images) {
    if ($img.Size -ge 256) { $img.Data = [byte[]]$img.Png } else { $img.Data = [byte[]](ConvertTo-Dib $img) }
}

# Plik .ico: obrazy do 128 px jako BMP, 256 px jako PNG.
$assets = Join-Path $OutDir 'Source\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$fs = [System.IO.File]::Create((Join-Path $assets 'app.ico'))
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$img.Data.Length); $bw.Write([UInt32]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) { $bw.Write($img.Data) }
$bw.Close(); $fs.Close()

$docs = Join-Path $OutDir 'docs\images'
New-Item -ItemType Directory -Force $docs | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $docs 'icon.png'), ($images | Where-Object { $_.Size -eq 256 }).Png)
Write-Host "Zapisano: $assets\app.ico oraz $docs\icon.png"
