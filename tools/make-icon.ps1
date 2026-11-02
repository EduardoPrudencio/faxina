# Gera src/Faxina/app.ico (16 a 256 px) desenhando a mesma marca da barra lateral:
# quadrado arredondado com gradiente índigo → verde-água e uma vassoura branca.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Faxina\app.ico'))

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Fundo
    $inset = [single]([math]::Max(0.5, $size * 0.03))
    $box = New-RoundedRect $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) ([single]($size * 0.24))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size),
        [System.Drawing.Color]::FromArgb(255, 91, 91, 214), [System.Drawing.Color]::FromArgb(255, 43, 179, 154))
    $g.FillPath($grad, $box)

    # Brilho sutil no topo
    if ($size -ge 32) {
        $shine = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF 0, -1), (New-Object System.Drawing.PointF 0, ($size + 1)),
            [System.Drawing.Color]::FromArgb(40, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
        $g.FillPath($shine, $box)
    }

    # Vassoura: coordenadas no espaço 32×32 da marca SVG, centralizadas e escaladas
    $scale = $size / 32.0 * 0.78
    $off = ($size - 32 * $scale) / 2 + $size * 0.01
    function P([single]$x, [single]$y) { New-Object System.Drawing.PointF ([single]($off + $x * $scale)), ([single]($off + $y * $scale)) }
    $w = [single]([math]::Max(1.6, $size * 0.075))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'

    $g.DrawLine($pen, (P 19.5 3.5), (P 13.5 16.5))                             # cabo
    $head = [System.Drawing.PointF[]]@((P 8.5 17.5), (P 22.5 17.5), (P 25 28.5), (P 6 28.5))
    $g.FillPolygon([System.Drawing.Brushes]::White, $head)                       # cabeça cheia: lê melhor em 16 px
    $g.DrawPolygon($pen, $head)
    if ($size -ge 24) {
        # cerdas recortadas na cabeça
        $cut = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 70, 140, 190)), ([single]([math]::Max(1, $size * 0.045)))
        $cut.StartCap = 'Round'; $cut.EndCap = 'Round'
        foreach ($x in 11.8, 15.5, 19.2) { $g.DrawLine($cut, (P $x 22), (P ($x + 0.15) 28)) }
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Render $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# Monta o .ico com as imagens PNG embutidas (formato aceito desde o Windows Vista)
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) | Join-Path -ChildPath (Split-Path $Out -Leaf)))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()

# PNG usado como favicon da página
$preview = Render 256
$preview.Save((Join-Path $PSScriptRoot '..\src\Faxina.Core\wwwroot\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

if ($env:FAXINA_ICON_PREVIEW) {
    # Folha de conferência: 16, 24, 32 e 48 px ampliados 4× sem suavizar, lado a lado
    $sheet = New-Object System.Drawing.Bitmap 520, 200
    $gs = [System.Drawing.Graphics]::FromImage($sheet)
    $gs.Clear([System.Drawing.Color]::FromArgb(255, 32, 34, 40))
    $gs.InterpolationMode = 'NearestNeighbor'; $gs.PixelOffsetMode = 'Half'
    $x = 10
    foreach ($s in 16, 24, 32, 48) { $b = Render $s; $gs.DrawImage($b, $x, 10, $s * 4, $s * 4); $x += $s * 4 + 12; $b.Dispose() }
    $gs.Dispose(); $sheet.Save($env:FAXINA_ICON_PREVIEW, [System.Drawing.Imaging.ImageFormat]::Png); $sheet.Dispose()
}
"ok: $Out"
