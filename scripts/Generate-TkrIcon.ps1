Add-Type -AssemblyName System.Drawing

function New-TkrIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # Background gradient rounded rectangle
    $scale = $size / 100.0
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $radius = 22.0 * $scale

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.PointF 0, 0), `
        (New-Object System.Drawing.PointF $size, $size), `
        ([System.Drawing.Color]::FromArgb(255, 30, 64, 175)), `
        ([System.Drawing.Color]::FromArgb(255, 37, 99, 235))

    # Create rounded rectangle path
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $g.FillPath($brush, $path)

    # Monitor outline
    # Screen rect: x=25, y=28, w=50, h=36
    $screenPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (5.5 * $scale)
    $screenPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $screenPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sX = 25 * $scale
    $sY = 28 * $scale
    $sW = 50 * $scale
    $sH = 36 * $scale
    $sR = 6 * $scale
    $sD = $sR * 2
    $screenPath.AddArc($sX, $sY, $sD, $sD, 180, 90)
    $screenPath.AddArc($sX + $sW - $sD, $sY, $sD, $sD, 270, 90)
    $screenPath.AddArc($sX + $sW - $sD, $sY + $sH - $sD, $sD, $sD, 0, 90)
    $screenPath.AddArc($sX, $sY + $sH - $sD, $sD, $sD, 90, 90)
    $screenPath.CloseFigure()
    $g.DrawPath($screenPen, $screenPath)

    # Monitor Stand: vertical stem + horizontal base
    $standPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (5.5 * $scale)
    $standPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $standPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    # Stem: 50,64 to 50,74
    $g.DrawLine($standPen, [float](50 * $scale), [float](64 * $scale), [float](50 * $scale), [float](74 * $scale))
    # Base: 38,74 to 62,74
    $g.DrawLine($standPen, [float](38 * $scale), [float](74 * $scale), [float](62 * $scale), [float](74 * $scale))

    # Cyan glowing dot / connection pulse inside screen: center 50, 46, radius 7.5
    $cyanBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 56, 189, 248))
    $dotR = 7.5 * $scale
    $g.FillEllipse($cyanBrush, [float](50 * $scale - $dotR), [float](46 * $scale - $dotR), [float]($dotR * 2), [float]($dotR * 2))

    # Cyan pulse halo
    $haloPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 56, 189, 248)), (2 * $scale)
    $haloR = 12.0 * $scale
    $g.DrawEllipse($haloPen, [float](50 * $scale - $haloR), [float](46 * $scale - $haloR), [float]($haloR * 2), [float]($haloR * 2))

    $g.Dispose()
    return $bmp
}

# Sizes for standard ICO
$sizes = @(256, 128, 64, 48, 32, 24, 16)
$pngDataList = @()

foreach ($sz in $sizes) {
    $bmp = New-TkrIcon $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngDataList += ,@($sz, $ms.ToArray())
    $bmp.Dispose()
    $ms.Dispose()
}

# Save 256x256 as master PNG
$masterPng = New-TkrIcon 256
$targetAssetsDir = "C:\Users\HP\Documents\ChatGPT\Pc remote wifi\native\ApnaRemote.Windows\Assets"
$masterPngPath = Join-Path $targetAssetsDir "tkrdesk-icon.png"
$masterPng.Save($masterPngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# Also save to website
$webDir = "c:\Users\HP\Desktop\tkr\website"
$masterPng.Save((Join-Path $webDir "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$masterPng.Dispose()

# Build multi-resolution ICO file
$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $icoStream

# Header: reserved(0), type(1=icon), count
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$pngDataList.Count)

$offset = 6 + ($pngDataList.Count * 16)

# Write Directory Entries
foreach ($item in $pngDataList) {
    $sz = $item[0]
    $data = $item[1]
    $wByte = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
    $hByte = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }

    $writer.Write($wByte)            # Width
    $writer.Write($hByte)            # Height
    $writer.Write([byte]0)           # Color count
    $writer.Write([byte]0)           # Reserved
    $writer.Write([uint16]1)         # Color planes
    $writer.Write([uint16]32)        # Bits per pixel
    $writer.Write([uint32]$data.Length) # Image bytes
    $writer.Write([uint32]$offset)   # Image offset

    $offset += $data.Length
}

# Write Image Data (PNG streams)
foreach ($item in $pngDataList) {
    $writer.Write($item[1])
}

$writer.Flush()
$icoBytes = $icoStream.ToArray()
$writer.Close()
$icoStream.Close()

# Save ICO to Assets and Website
$icoPath = Join-Path $targetAssetsDir "tkrdesk.ico"
[System.IO.File]::WriteAllBytes($icoPath, $icoBytes)
[System.IO.File]::WriteAllBytes((Join-Path $webDir "favicon.ico"), $icoBytes)

Write-Host "Icons generated successfully:"
Write-Host "  $masterPngPath ($( (Get-Item $masterPngPath).Length ) bytes)"
Write-Host "  $icoPath ($( (Get-Item $icoPath).Length ) bytes)"
Write-Host "  $webDir\favicon.ico"
Write-Host "  $webDir\logo.png"
