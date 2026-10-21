param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$workspace = Split-Path $PSScriptRoot
[xml]$source = Get-Content -LiteralPath (Join-Path $workspace 'ui\assets\app-icon.svg') -Raw
$tile = $source.svg.rect
$mark = $source.svg.polygon
$points = [System.Drawing.PointF[]]@($mark.points.Split(' ') | ForEach-Object {
    $pair = $_.Split(',')
    [System.Drawing.PointF]::new([float]$pair[0], [float]$pair[1])
})
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($tile.fill))
    $foreground = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($mark.fill))
    $png = [System.IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 64.0, $size / 64.0)
        $diameter = [float]$tile.rx * 2
        $extent = [float]$tile.width - $diameter
        $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
        $path.AddArc($extent, 0, $diameter, $diameter, 270, 90)
        $path.AddArc($extent, $extent, $diameter, $diameter, 0, 90)
        $path.AddArc(0, $extent, $diameter, $diameter, 90, 90)
        $path.CloseFigure()
        $graphics.FillPath($background, $path)
        $graphics.FillPolygon($foreground, $points)
        # Use DIB frames at small sizes for .NET Framework icon compatibility.
        # Windows uses the compressed 256px frame for large Explorer views.
        if ($size -eq 256) {
            $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        } else {
            $data = $bitmap.LockBits([System.Drawing.Rectangle]::new(0, 0, $size, $size), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $pixels = [byte[]]::new($size * $size * 4)
            try { [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length) } finally { $bitmap.UnlockBits($data) }
            $frameWriter = [System.IO.BinaryWriter]::new($png)
            try {
                $frameWriter.Write([uint32]40); $frameWriter.Write([int32]$size); $frameWriter.Write([int32]($size * 2))
                $frameWriter.Write([uint16]1); $frameWriter.Write([uint16]32)
                $frameWriter.Write([uint32]0); $frameWriter.Write([uint32]$pixels.Length)
                1..4 | ForEach-Object { $frameWriter.Write([uint32]0) }
                for ($row = $size - 1; $row -ge 0; $row--) { $frameWriter.Write($pixels, $row * $size * 4, $size * 4) }
                $maskStride = [int][Math]::Ceiling($size / 32.0) * 4
                for ($row = $size - 1; $row -ge 0; $row--) {
                    $mask = [byte[]]::new($maskStride)
                    for ($column = 0; $column -lt $size; $column++) {
                        if ($pixels[($row * $size + $column) * 4 + 3] -eq 0) {
                            $maskByte = [int][Math]::Floor($column / 8.0)
                            $mask[$maskByte] = $mask[$maskByte] -bor (128 -shr ($column % 8))
                        }
                    }
                    $frameWriter.Write($mask)
                }
            } finally { $frameWriter.Dispose() }
        }
        $frames += ,$png.ToArray()
    } finally {
        $png.Dispose(); $foreground.Dispose(); $background.Dispose()
        $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}
$file = [System.IO.File]::Create((Join-Path $workspace 'src\app.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = [byte]($sizes[$index] % 256)
        $writer.Write($dimension); $writer.Write($dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
Write-Host 'Updated src\app.ico from ui\assets\app-icon.svg.'
