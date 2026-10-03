# Generates the Curatarr tray/app icon (app.ico) — the "sparkle monogram": a warm amber
# gradient rounded tile with a dark C arc and a small curation sparkle. Rendered at several
# sizes and packed into a single .ico using uncompressed 32bpp DIB frames (which
# System.Drawing.Icon and the WinForms NotifyIcon parse reliably).
Add-Type -AssemblyName System.Drawing

$OutPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\src\CuratarrTray\app.ico"))
$sizes   = 16,20,24,32,40,48,64,128,256

$c1   = [System.Drawing.Color]::FromArgb(246,201,111)  # #f6c96f
$c2   = [System.Drawing.Color]::FromArgb(221,139,40)   # #dd8b28
$dark = [System.Drawing.Color]::FromArgb(27,19,10)     # #1b130a

function New-RoundedPath([float]$x,[float]$y,[float]$w,[float]$h,[float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x,       $y,       $d, $d, 180, 90)
  $p.AddArc($x+$w-$d, $y,       $d, $d, 270, 90)
  $p.AddArc($x+$w-$d, $y+$h-$d, $d, $d,   0, 90)
  $p.AddArc($x,       $y+$h-$d, $d, $d,  90, 90)
  $p.CloseFigure()
  return $p
}

# Draw the whole mark in a 40x40 design space; the caller scales it to the target size.
function Draw-Mark([System.Drawing.Graphics]$g) {
  # Gradient rounded tile
  $tile  = New-RoundedPath 3 3 34 34 10
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(3,3)), (New-Object System.Drawing.PointF(37,37)), $c1, $c2)
  $g.FillPath($brush, $tile)

  # Dark C arc (opens to the right)
  $pen = New-Object System.Drawing.Pen($dark, 3.6)
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawArc($pen, 11.0, 11.5, 17.0, 17.0, 48, 264)

  # Curation sparkle (4-point star), top-right
  $pts = @(
    (New-Object System.Drawing.PointF(29.0, 8.0)),
    (New-Object System.Drawing.PointF(29.71,11.29)),
    (New-Object System.Drawing.PointF(33.0,12.0)),
    (New-Object System.Drawing.PointF(29.71,12.71)),
    (New-Object System.Drawing.PointF(29.0,16.0)),
    (New-Object System.Drawing.PointF(28.29,12.71)),
    (New-Object System.Drawing.PointF(25.0,12.0)),
    (New-Object System.Drawing.PointF(28.29,11.29))
  )
  $g.FillPolygon((New-Object System.Drawing.SolidBrush($dark)), $pts)
}

function New-DibFrame([System.Drawing.Bitmap]$bmp, [int]$s) {
  $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
  $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $data.Stride
  $buf = New-Object byte[] ($stride * $s)
  [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
  $bmp.UnlockBits($data)
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s*2))
  $bw.Write([UInt16]1);  $bw.Write([UInt16]32); $bw.Write([UInt32]0)
  $bw.Write([UInt32]0);  $bw.Write([Int32]0);  $bw.Write([Int32]0)
  $bw.Write([UInt32]0);  $bw.Write([UInt32]0)
  for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($buf, $y * $stride, $stride) }
  $maskRow = [int]([Math]::Floor(($s + 31) / 32)) * 4
  $zero = New-Object byte[] $maskRow
  for ($y = 0; $y -lt $s; $y++) { $bw.Write($zero, 0, $maskRow) }
  $bw.Flush()
  $out = $ms.ToArray(); $bw.Dispose()
  return $out
}

$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g   = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.ScaleTransform($s / 40.0, $s / 40.0)
  Draw-Mark $g
  $g.Dispose()
  $frames.Add((New-DibFrame $bmp $s))
  $bmp.Dispose()
}

$outStream = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter($outStream)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i=0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $frames[$i].Length
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1);  $bw.Write([UInt16]32)
  $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
  $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $outStream.ToArray())
$bw.Dispose()
Write-Host "Wrote $OutPath ($([Math]::Round((Get-Item $OutPath).Length/1KB,1)) KB, $($sizes.Count) sizes)"
