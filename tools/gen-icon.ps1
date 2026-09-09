# 芯跳 CoreBeat —— 应用/托盘图标生成器
# 用法: powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\gen-icon.ps1
# 产出: src\CoreBeat\Assets\CoreBeat.ico （多尺寸 PNG 条目，Win Vista+ 支持）
# 设计: 深色圆角底 + 呼吸青绿发光心电波形

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outFile = Join-Path $PSScriptRoot '..\src\CoreBeat\Assets\CoreBeat.ico'
$sizes = @(16, 24, 32, 48, 64, 256)

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return ,$p
}

# 心电波形（两个 QRS 复合波），画布 256x256，基线 y=150
$pts = @(
    [System.Drawing.PointF]::new(16,150), [System.Drawing.PointF]::new(26,150),
    [System.Drawing.PointF]::new(34,132), [System.Drawing.PointF]::new(40,150),
    [System.Drawing.PointF]::new(44,150), [System.Drawing.PointF]::new(48,164),
    [System.Drawing.PointF]::new(56,44),  [System.Drawing.PointF]::new(64,178),
    [System.Drawing.PointF]::new(72,150), [System.Drawing.PointF]::new(86,126),
    [System.Drawing.PointF]::new(98,150), [System.Drawing.PointF]::new(102,150),
    [System.Drawing.PointF]::new(112,150), [System.Drawing.PointF]::new(120,132),
    [System.Drawing.PointF]::new(126,150), [System.Drawing.PointF]::new(130,150),
    [System.Drawing.PointF]::new(134,164), [System.Drawing.PointF]::new(142,44),
    [System.Drawing.PointF]::new(150,178), [System.Drawing.PointF]::new(158,150),
    [System.Drawing.PointF]::new(172,126), [System.Drawing.PointF]::new(184,150),
    [System.Drawing.PointF]::new(240,150)
)

function Render-BaseIcon {
    $bmp = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        # 深色渐变圆角底
        $clip = New-RoundedRectPath 0 0 256 256 56
        $bgRect = New-Object System.Drawing.RectangleF 0, 0, 256, 256
        $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
            ($bgRect, [System.Drawing.Color]::FromArgb(255, 30, 58, 95),
                      [System.Drawing.Color]::FromArgb(255, 9, 15, 27),
                      [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillPath($bgBrush, $clip)

        # 顶部高光（玻璃感）
        $gloss = New-RoundedRectPath 4 4 248 110 40
        $glossBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
            (New-Object System.Drawing.RectangleF 0, 0, 256, 120),
            ([System.Drawing.Color]::FromArgb(26, 255, 255, 255)),
            ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)),
            ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillPath($glossBrush, $gloss)

        # 波形发光层 + 主线
        $glowPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(58, 62, 246, 203)), 15
        $glowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $glowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $glowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawLines($glowPen, $pts)

        $mainPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 64, 244, 204)), 7
        $mainPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $mainPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $mainPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawLines($mainPen, $pts)

        # 底部文字描边小辉光（寓意“beat”的圆点）
        $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 64, 244, 204))
        $g.FillEllipse($dotBrush, 204, 218, 10, 10)
    }
    finally { $g.Dispose() }
    return $bmp
}

$base = Render-BaseIcon

# 渲染各尺寸 PNG 字节
$pngs = @{}
foreach ($s in $sizes) {
    $b = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($base, 0, 0, $s, $s)
    }
    finally { $g.Dispose() }
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    $ms.Dispose()
    $b.Dispose()
}
$base.Dispose()

# 打包 ICO（PNG 条目）
$dir = Split-Path -Parent $outFile
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$fs = [System.IO.File]::Create($outFile)
$bw = New-Object System.IO.BinaryWriter ($fs, [System.Text.Encoding]::ASCII)
try {
    $bw.Write([UInt16]0)                       # reserved
    $bw.Write([UInt16]1)                       # type: icon
    $bw.Write([UInt16]$sizes.Count)            # count

    $headerSize = 6 + 16 * $sizes.Count
    $offset = $headerSize
    foreach ($s in $sizes) {
        $bytes = $pngs[$s]
        $w = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([Byte]$w)                    # width (0 = 256)
        $bw.Write([Byte]$w)                    # height
        $bw.Write([Byte]0)                     # palette
        $bw.Write([Byte]0)                     # reserved
        $bw.Write([UInt16]1)                   # planes
        $bw.Write([UInt16]32)                  # bpp
        $bw.Write([UInt32]$bytes.Length)       # size
        $bw.Write([UInt32]$offset)             # offset
        $offset += $bytes.Length
    }
    foreach ($s in $sizes) {
        $bw.Write($pngs[$s])
    }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}
Write-Output ("已生成: " + $outFile + "  (" + $sizes.Count + " sizes)")
