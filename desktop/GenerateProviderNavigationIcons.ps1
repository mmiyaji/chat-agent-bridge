#Requires -Version 5.1
#Requires -PSEdition Desktop
param([string]$OutputPath = (Join-Path $PSScriptRoot 'ChatAgentBridge.Desktop/AgentSettings/ProviderNavigationIcons.xaml'))

# Development-time conversion only. WPF is never referenced by the WinUI app.
# Widen each existing outline into filled geometry because PathIcon has no Stroke.
Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$document = [IO.File]::ReadAllText((Join-Path $root 'desktop/ChatAgentBridge.Desktop/AgentSettings/SettingsScopeIcons.xaml'))
$names = [Xml.XmlNamespaceManager]::new($document.NameTable)
$names.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$names.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$result = @{}
foreach ($provider in @('slack','mattermost','discord')) {
    $template = $document.SelectSingleNode("//p:DataTemplate[@x:Key='SettingsScopeIcon_provider_$provider']", $names)
    if (-not $template) { throw "Missing provider outline: $provider" }
    $combined = [Windows.Media.Geometry]::Empty
    foreach ($node in $template.SelectNodes('.//p:Path', $names)) {
        $geometry = [Windows.Media.Geometry]::Parse($node.GetAttribute('Data'))
        $part = [Windows.Media.Geometry]::Empty
        if ($node.HasAttribute('Fill')) { $part = $geometry }
        if ($node.HasAttribute('Stroke')) {
            $pen = [Windows.Media.Pen]::new([Windows.Media.Brushes]::Black, [double]::Parse($node.GetAttribute('StrokeThickness'), [Globalization.CultureInfo]::InvariantCulture))
            $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
            if ($node.GetAttribute('StrokeStartLineCap') -eq 'Round') { $pen.StartLineCap = [Windows.Media.PenLineCap]::Round }
            if ($node.GetAttribute('StrokeEndLineCap') -eq 'Round') { $pen.EndLineCap = [Windows.Media.PenLineCap]::Round }
            $stroke = $geometry.GetWidenedPathGeometry($pen, 0.01, [Windows.Media.ToleranceType]::Absolute)
            $part = [Windows.Media.Geometry]::Combine($part, $stroke, [Windows.Media.GeometryCombineMode]::Union, $null)
        }
        $part = $part.Clone()
        $transforms = [Windows.Media.TransformGroup]::new()
        $rotation = $node.SelectSingleNode('.//p:RotateTransform', $names)
        if ($rotation) { $transforms.Children.Add([Windows.Media.RotateTransform]::new([double]$rotation.Angle, 12, 12)) }
        $transforms.Children.Add([Windows.Media.ScaleTransform]::new((16.0/24.0), (16.0/24.0)))
        $part.Transform = $transforms
        $combined = [Windows.Media.Geometry]::Combine($combined, $part, [Windows.Media.GeometryCombineMode]::Union, $null, 0.01, [Windows.Media.ToleranceType]::Absolute)
    }
    $data = $combined.ToString([Globalization.CultureInfo]::InvariantCulture)
    $data = [regex]::Replace($data, '-?\d+\.\d+', {param($m) ([math]::Round([double]::Parse($m.Value,[Globalization.CultureInfo]::InvariantCulture),5)).ToString('0.#####',[Globalization.CultureInfo]::InvariantCulture)})
    $parsed = [Windows.Media.Geometry]::Parse($data)
    if ($parsed.IsEmpty()) { throw "Empty provider outline: $provider" }
    if ($parsed.Bounds.Left -lt -0.01 -or $parsed.Bounds.Top -lt -0.01 -or $parsed.Bounds.Right -gt 16.01 -or $parsed.Bounds.Bottom -gt 16.01) { throw "Unbaked or out-of-bounds geometry for $provider : $($parsed.Bounds)" }
    $result[$provider] = $data
    Write-Host "$provider : $($data.Length) characters, bounds $($parsed.Bounds)"
}
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$writer = [Xml.XmlWriter]::Create([IO.Path]::GetFullPath($OutputPath), $settings)
try {
    $presentation = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    $xaml = 'http://schemas.microsoft.com/winfx/2006/xaml'
    $writer.WriteStartElement('ResourceDictionary', $presentation)
    $writer.WriteAttributeString('xmlns', 'x', $null, $xaml)
    $writer.WriteComment(' Generated from SettingsScopeIcons.xaml by desktop/GenerateProviderNavigationIcons.ps1. Do not edit the filled paths manually. ')
    foreach ($provider in @('slack','mattermost','discord')) {
        $writer.WriteStartElement('DataTemplate', $presentation)
        $writer.WriteAttributeString('x', 'Key', $xaml, "ConnectionProviderIcon_$provider")
        $writer.WriteStartElement('PathIcon', $presentation)
        $writer.WriteAttributeString('Width', '16')
        $writer.WriteAttributeString('Height', '16')
        $writer.WriteAttributeString('Data', $result[$provider])
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
}
finally { $writer.Dispose() }
