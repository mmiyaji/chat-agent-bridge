# Captures the primary monitor work area (the recording region) to a PNG.
param([string]$Name = 'shot')
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class S1 { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[S1]::SetProcessDPIAware() | Out-Null
. (Join-Path $PSScriptRoot 'demo-env.ps1')
$b = New-Object Drawing.Bitmap 1920, 1032; $g = [Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen(0, 0, 0, 0, $b.Size); $b.Save((Join-Path $DemoData "$Name.png")); $g.Dispose(); $b.Dispose()
"saved $Name"
