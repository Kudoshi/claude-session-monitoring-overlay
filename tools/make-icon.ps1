# Generates app.ico (16–256 px, PNG frames): a dark rounded tile with three status rows
# (amber = needs input, blue = running, green = done), matching the overlay.
param([string]$Out = (Join-Path $PSScriptRoot "..\app.ico"))
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $s * 0.03)
    $tile = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x23, 0x22, 0x1F))), $tile)
    if ($s -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x3A, 0x39, 0x36)), ([Math]::Max(1, $s * 0.012))
        $g.DrawPath($pen, $tile)
    }

    $colors = @(
        [System.Drawing.Color]::FromArgb(255, 0xEF, 0x9F, 0x27),
        [System.Drawing.Color]::FromArgb(255, 0x37, 0x8A, 0xDD),
        [System.Drawing.Color]::FromArgb(255, 0x63, 0x99, 0x22))
    $rows = @(0.29, 0.5, 0.71)
    $barBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x6B, 0x69, 0x63))
    for ($i = 0; $i -lt 3; $i++) {
        $cy = $s * $rows[$i]
        $dr = if ($s -le 16) { $s * 0.11 } else { $s * 0.075 }
        $cx = if ($s -le 16) { $s * 0.5 } else { $s * 0.3 }
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $colors[$i]), [float]($cx - $dr), [float]($cy - $dr), [float]($dr * 2), [float]($dr * 2))
        if ($s -gt 16) {
            $bh = $s * 0.075
            $bw = if ($i -eq 1) { $s * 0.26 } else { $s * 0.33 }
            $bar = New-RoundRect ([float]($s * 0.42)) ([float]($cy - $bh / 2)) ([float]$bw) ([float]$bh) ([float]($bh / 2))
            $g.FillPath($barBrush, $bar)
        }
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset); $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

# Preview for review
$prev = New-Frame 256; $prev.Save([IO.Path]::ChangeExtension($Out, ".png")); $prev.Dispose()
"wrote $Out"
