# Builds proper multi-resolution Windows .ico files from the Postarr source PNG.
# Uses uncompressed 32bpp DIB frames (System.Drawing.Icon and the WinForms NotifyIcon fail to
# parse PNG-compressed ICO entries), and downsamples with high-quality interpolation so the
# small sizes stay legible.
param(
  # Transparent master (mark on transparency, no black square). Regenerate this from the original
  # black-background art with scripts/make-transparent-icon.ps1 if the artwork ever changes.
  [string]$Source = (Join-Path $PSScriptRoot "..\src\Postarr\wwwroot\images\logo\postarr-icon.png")
)
Add-Type -AssemblyName System.Drawing

$targets = @(
  [pscustomobject]@{ Path = (Join-Path $PSScriptRoot "..\src\PostarrTray\app.ico");            Label = "tray/desktop icon" }
  [pscustomobject]@{ Path = (Join-Path $PSScriptRoot "..\src\Postarr\wwwroot\favicon.ico");    Label = "browser favicon" }
)
$sizes = 16,20,24,32,40,48,64,128,256

if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }
$src = New-Object System.Drawing.Bitmap($Source)
Write-Host "Source: $Source ($($src.Width)x$($src.Height))"

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
  $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)))
  $g.Dispose()
  $frames.Add((New-DibFrame $bmp $s))
  $bmp.Dispose()
}
$src.Dispose()

$outStream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($outStream)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $frames[$i].Length
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1);  $bw.Write([UInt16]32)
  $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
  $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
$bytes = $outStream.ToArray()
$bw.Dispose()

foreach ($t in $targets) {
  $p = [System.IO.Path]::GetFullPath($t.Path)
  New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($p)) | Out-Null
  [System.IO.File]::WriteAllBytes($p, $bytes)
  Write-Host ("Wrote {0}  ({1} KB, {2} sizes)  [{3}]" -f $p, [Math]::Round($bytes.Length/1KB,1), $sizes.Count, $t.Label)
}
