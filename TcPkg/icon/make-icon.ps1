<#
Draws the package icon (icon.png, 256x256): a data file with value rows on a dark blue tile and a
teal badge with a cycle arrow (values restored to the PLC). Re-run after changing colors or shapes:
  powershell -ExecutionPolicy Bypass -File TcPkg\icon\make-icon.ps1
#>
param([string] $Out = (Join-Path $PSScriptRoot 'icon.png'), [int] $Size = 256)
Add-Type -AssemblyName System.Drawing

$s = $Size / 256.0   # all coordinates below are for 256 px
function Pt([double]$x, [double]$y) { New-Object Drawing.PointF ([float]($x * $s)), ([float]($y * $s)) }
function Rect([double]$x, [double]$y, [double]$w, [double]$h) { New-Object Drawing.RectangleF ([float]($x * $s)), ([float]($y * $s)), ([float]($w * $s)), ([float]($h * $s)) }
function RoundRect([Drawing.RectangleF]$r, [double]$radius) {
    $d = [float]($radius * 2 * $s); $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90); $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90); $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}
function Color([string]$hex) { [Drawing.ColorTranslator]::FromHtml($hex) }

$bmp = New-Object Drawing.Bitmap $Size, $Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)

# Tile: dark blue with a subtle vertical gradient.
$tile = RoundRect (Rect 8 8 240 240) 44
$grad = New-Object Drawing.Drawing2D.LinearGradientBrush (Pt 0 8), (Pt 0 248), (Color '#24497A'), (Color '#152B4A')
$g.FillPath($grad, $tile)

# Data file with a folded corner.
$doc = New-Object Drawing.Drawing2D.GraphicsPath
$doc.AddLines([Drawing.PointF[]]@((Pt 56 40), (Pt 148 40), (Pt 192 84), (Pt 192 212), (Pt 56 212)))
$doc.CloseFigure()
$g.FillPath((New-Object Drawing.SolidBrush (Color '#F4F7FB')), $doc)
$fold = New-Object Drawing.Drawing2D.GraphicsPath
$fold.AddLines([Drawing.PointF[]]@((Pt 148 40), (Pt 148 84), (Pt 192 84))); $fold.CloseFigure()
$g.FillPath((New-Object Drawing.SolidBrush (Color '#C9D6E8')), $fold)

# Value rows: name bar (grey) + value bar (blue), like the variables tree.
$nameBrush = New-Object Drawing.SolidBrush (Color '#9AABC2')
$valueBrush = New-Object Drawing.SolidBrush (Color '#2F6DB5')
foreach ($row in @(@(104, 40, 52), @(130, 52, 40), @(156, 34, 58), @(182, 46, 30))) {
    $y = $row[0]
    $g.FillPath($nameBrush, (RoundRect (Rect 74 $y $row[1] 12) 6))
    $g.FillPath($valueBrush, (RoundRect (Rect (82 + $row[1]) $y $row[2] 12) 6))
}

# Badge: teal circle with a white cycle arrow (restore to the PLC).
$cx = 186; $cy = 186; $rad = 50
$g.FillEllipse((New-Object Drawing.SolidBrush (Color '#152B4A')), (Rect ($cx - $rad - 7) ($cy - $rad - 7) (2 * $rad + 14) (2 * $rad + 14)))
$g.FillEllipse((New-Object Drawing.SolidBrush (Color '#1FA89A')), (Rect ($cx - $rad) ($cy - $rad) (2 * $rad) (2 * $rad)))
$pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([float](11 * $s))
$pen.StartCap = 'Round'; $pen.EndCap = 'Flat'
$ar = 26
$g.DrawArc($pen, (Rect ($cx - $ar) ($cy - $ar) (2 * $ar) (2 * $ar)), 200, 250)
# Arrow head at the end of the arc (angle 200 + 250 = 450 -> 90 deg, i.e. bottom of the circle).
$end = [Math]::PI / 180 * 90
$ex = $cx + $ar * [Math]::Cos($end); $ey = $cy + $ar * [Math]::Sin($end)
$head = New-Object Drawing.Drawing2D.GraphicsPath
$head.AddLines([Drawing.PointF[]]@((Pt ($ex + 4) ($ey - 15)), (Pt ($ex + 4) ($ey + 15)), (Pt ($ex - 17) $ey))); $head.CloseFigure()
$g.FillPath([Drawing.Brushes]::White, $head)

$g.Dispose()
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Icon written to $Out"
