param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\docs\assets\app-icon\chat-agent-bridge-icon.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop\Assets'
$masterPath = Join-Path $repoRoot 'docs\assets\app-icon\chat-agent-bridge-icon.png'

function New-ScaledPng {
    param(
        [System.Drawing.Image]$Source,
        [int]$Width,
        [int]$Height,
        [string]$Path,
        [bool]$Fit = $false
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        if ($Fit) {
            $scale = [Math]::Min($Width / [double]$Source.Width, $Height / [double]$Source.Height)
            $drawWidth = [int][Math]::Round($Source.Width * $scale)
            $drawHeight = [int][Math]::Round($Source.Height * $scale)
            $x = [int][Math]::Floor(($Width - $drawWidth) / 2.0)
            $y = [int][Math]::Floor(($Height - $drawHeight) / 2.0)
            $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new($x, $y, $drawWidth, $drawHeight))
        } else {
            $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, $Width, $Height))
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-PngIcon {
    param([System.Drawing.Image]$Source, [string]$Path)
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $frame = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($frame)
        $memory = [System.IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            $frame.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($memory.ToArray())
        } finally {
            $memory.Dispose()
            $graphics.Dispose()
            $frame.Dispose()
        }
    }

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$sizes.Count)
        $offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $size = $sizes[$i]
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frames[$i].Length)
            $writer.Write([UInt32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frameBytes in $frames) { $writer.Write($frameBytes) }
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "Icon source not found: $SourcePath"
}
$source = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $SourcePath).Path)
try {
    if ($source.Width -ne $source.Height) { throw "Icon source must be square; got $($source.Width)x$($source.Height)." }
    if (-not [string]::Equals((Resolve-Path -LiteralPath $SourcePath).Path, (Join-Path $repoRoot 'docs\assets\app-icon\chat-agent-bridge-icon.png'), [System.StringComparison]::OrdinalIgnoreCase)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $masterPath) | Out-Null
        $source.Save($masterPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } elseif (-not (Test-Path -LiteralPath $masterPath)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $masterPath) | Out-Null
        $source.Save($masterPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }

    New-PngIcon $source (Join-Path $assetsDir 'AppIcon.ico')
    New-ScaledPng $source 50 50 (Join-Path $assetsDir 'StoreLogo.png')
    New-ScaledPng $source 88 88 (Join-Path $assetsDir 'Square44x44Logo.scale-200.png')
    New-ScaledPng $source 24 24 (Join-Path $assetsDir 'Square44x44Logo.targetsize-24_altform-unplated.png')
    New-ScaledPng $source 48 48 (Join-Path $assetsDir 'Square44x44Logo.targetsize-48_altform-lightunplated.png')
    New-ScaledPng $source 300 300 (Join-Path $assetsDir 'Square150x150Logo.scale-200.png')
    New-ScaledPng $source 620 300 (Join-Path $assetsDir 'Wide310x150Logo.scale-200.png') -Fit $true
    New-ScaledPng $source 1240 600 (Join-Path $assetsDir 'SplashScreen.scale-200.png') -Fit $true
    New-ScaledPng $source 48 48 (Join-Path $assetsDir 'LockScreenLogo.scale-200.png')
} finally {
    $source.Dispose()
}

Write-Output "Exported transparent icon derivatives from $SourcePath"
