param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\docs\assets\overview'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Format-SvgNumber([double]$Value) {
    return $Value.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-SvgPathData([Drawing.Drawing2D.GraphicsPath]$Path, [double]$OffsetX, [double]$OffsetY) {
    $points = $Path.PathPoints
    $types = $Path.PathTypes
    $segments = [System.Collections.Generic.List[string]]::new()
    $i = 0
    while ($i -lt $points.Length) {
        $type = $types[$i] -band 0x07
        $x = $points[$i].X + $OffsetX
        $y = $points[$i].Y + $OffsetY
        if ($type -eq 0) {
            $segments.Add("M$(Format-SvgNumber $x) $(Format-SvgNumber $y)")
            $i++
        } elseif ($type -eq 1) {
            $segments.Add("L$(Format-SvgNumber $x) $(Format-SvgNumber $y)")
            $i++
        } elseif ($type -eq 3 -and $i + 2 -lt $points.Length) {
            $p1 = $points[$i + 1]
            $p2 = $points[$i + 2]
            $segments.Add("C$(Format-SvgNumber $x) $(Format-SvgNumber $y) $(Format-SvgNumber ($p1.X + $OffsetX)) $(Format-SvgNumber ($p1.Y + $OffsetY)) $(Format-SvgNumber ($p2.X + $OffsetX)) $(Format-SvgNumber ($p2.Y + $OffsetY))")
            $i += 3
        } else {
            throw "Unsupported GDI+ path point type $type at index $i."
        }

        if (($types[$i - 1] -band 0x80) -ne 0) {
            $segments.Add('Z')
        }
    }
    return ($segments -join ' ')
}

function Convert-TextToOutline([System.Xml.XmlElement]$TextElement) {
    $text = $TextElement.InnerText
    if ([string]::IsNullOrWhiteSpace($text)) { return }

    $fontSize = [double]::Parse($TextElement.GetAttribute('font-size'), [Globalization.CultureInfo]::InvariantCulture)
    $weight = $TextElement.GetAttribute('font-weight')
    $fontStyle = if ($weight -eq '700' -or $weight -eq 'bold') { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }
    $fontName = 'Yu Gothic UI'
    $family = [Drawing.FontFamily]::new($fontName)
    $outline = $null
    $format = $null
    try {
        $outline = [Drawing.Drawing2D.GraphicsPath]::new()
        $format = [Drawing.StringFormat]::GenericTypographic.Clone()
        $format.FormatFlags = $format.FormatFlags -bor [Drawing.StringFormatFlags]::NoWrap
        $outline.AddString($text, $family, [int]$fontStyle, [single]$fontSize, [Drawing.PointF]::new(0, 0), $format)

        # SVG y is a baseline. GDI+ AddString starts from the font cell top.
        $ascent = $family.GetCellAscent($fontStyle)
        $emHeight = $family.GetEmHeight($fontStyle)
        $baselineOffset = ($ascent / $emHeight) * $fontSize
        $x = [double]::Parse($TextElement.GetAttribute('x'), [Globalization.CultureInfo]::InvariantCulture)
        $y = [double]::Parse($TextElement.GetAttribute('y'), [Globalization.CultureInfo]::InvariantCulture)

        $pathElement = $TextElement.OwnerDocument.CreateElement('path', 'http://www.w3.org/2000/svg')
        foreach ($attribute in $TextElement.Attributes) {
            if ($attribute.Name -notin @('x', 'y', 'font-family', 'font-size', 'font-weight')) {
                $pathElement.SetAttribute($attribute.Name, $attribute.Value)
            }
        }
        $pathElement.SetAttribute('d', (Get-SvgPathData $outline $x ($y - $baselineOffset)))
        $pathElement.SetAttribute('fill-rule', 'evenodd')
        [void]$TextElement.ParentNode.ReplaceChild($pathElement, $TextElement)
    } finally {
        if ($outline) { $outline.Dispose() }
        if ($format) { $format.Dispose() }
        $family.Dispose()
    }
}

function Get-ArrowPath([System.Xml.XmlElement]$PathElement) {
    $d = $PathElement.GetAttribute('d')
    $strokeWidth = [double]::Parse($PathElement.GetAttribute('stroke-width'), [Globalization.CultureInfo]::InvariantCulture)
    # SVG's default markerUnits is strokeWidth; the marker's 12x12 viewBox is
    # mapped into its 6x6 viewport, then scaled by the line's stroke width.
    $scale = $strokeWidth / 2
    if ($d -match '^M\s*([\d.]+)\s+([\d.]+)\s+H\s*([\d.]+)$') {
        $x1 = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
        $y = [double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)
        $x2 = [double]::Parse($Matches[3], [Globalization.CultureInfo]::InvariantCulture)
        $endX = $x2
        $endY = $y
        $direction = if ($x2 -gt $x1) { 1 } else { -1 }
        $tipX = $endX + ($direction * $scale)
        $baseX = $endX - ($direction * 9 * $scale)
        $halfHeight = 5 * $scale
        return "M$(Format-SvgNumber $tipX) $(Format-SvgNumber $endY) L$(Format-SvgNumber $baseX) $(Format-SvgNumber ($endY - $halfHeight)) L$(Format-SvgNumber $baseX) $(Format-SvgNumber ($endY + $halfHeight)) Z"
    }
    if ($d -match '^M\s*([\d.]+)\s+([\d.]+)\s+V\s*([\d.]+)$') {
        $x = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
        $y1 = [double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)
        $y2 = [double]::Parse($Matches[3], [Globalization.CultureInfo]::InvariantCulture)
        $direction = if ($y2 -gt $y1) { 1 } else { -1 }
        $tipY = $y2 + ($direction * $scale)
        $baseY = $y2 - ($direction * 9 * $scale)
        $halfWidth = 5 * $scale
        return "M$(Format-SvgNumber $x) $(Format-SvgNumber $tipY) L$(Format-SvgNumber ($x - $halfWidth)) $(Format-SvgNumber $baseY) L$(Format-SvgNumber ($x + $halfWidth)) $(Format-SvgNumber $baseY) Z"
    }
    throw "Arrow path is not a supported horizontal or vertical segment: $d"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.IndentChars = '  '
$settings.OmitXmlDeclaration = $true
$settings.Encoding = [Text.UTF8Encoding]::new($false)

foreach ($sourceFile in Get-ChildItem -LiteralPath $SourceDirectory -Filter 'OverviewArchitecture*.svg' -File) {
    [xml]$document = Get-Content -LiteralPath $sourceFile.FullName -Raw
    $manager = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $manager.AddNamespace('svg', 'http://www.w3.org/2000/svg')

    foreach ($textElement in @($document.SelectNodes('//svg:text', $manager))) {
        Convert-TextToOutline $textElement
    }

    foreach ($arrowPath in @($document.SelectNodes('//svg:path[@marker-end]', $manager))) {
        $marker = $arrowPath.GetAttribute('marker-end')
        $arrowPath.RemoveAttribute('marker-end')
        $triangle = $document.CreateElement('path', 'http://www.w3.org/2000/svg')
        $triangle.SetAttribute('d', (Get-ArrowPath $arrowPath))
        $triangle.SetAttribute('fill', $(if ($marker -eq 'url(#arrowBlue)') { '#2864A5' } else { '#526579' }))
        [void]$arrowPath.ParentNode.InsertAfter($triangle, $arrowPath)
    }

    $defs = $document.SelectSingleNode('/svg:svg/svg:defs', $manager)
    if ($defs) { [void]$defs.ParentNode.RemoveChild($defs) }
    $outputName = [IO.Path]::GetFileName($sourceFile.FullName)
    $outputFile = Join-Path $OutputDirectory $outputName
    $writer = [System.Xml.XmlWriter]::Create($outputFile, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
    Write-Output "Generated $outputFile"
}
