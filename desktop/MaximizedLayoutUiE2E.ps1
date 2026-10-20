param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('ja','en')][string]$Language='ja',
    [switch]$CaptureOnly
)
$ErrorActionPreference='Stop'
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$payload=[IO.Path]::GetFullPath($AppDirectory)
if(-not $payload.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path (Join-Path $payload 'AppxManifest.xml'))){throw 'Use an isolated portable payload under desktop/artifacts.'}
$data=Join-Path $artifacts ('maximized-layout-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $data|Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class MaximizedLayoutNative {
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window,int command);
 [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$app=$null;$window=[IntPtr]::Zero;$measurements=[Collections.Generic.List[object]]::new()
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Write-Json([string]$Name,$Value){$Value|ConvertTo-Json -Depth 12|Set-Content (Join-Path $data $Name) -Encoding utf8}
function UI([string[]]$Arguments){$out=winapp ui @Arguments -a $app.Id --json;if($LASTEXITCODE){throw "UI failed: $($Arguments -join ' ')"};$out}
function Root{[Windows.Automation.AutomationElement]::FromHandle($window)}
function Element([string]$Id){(Root).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Navigate([string]$Id){$nav=Element $Id;if($null -eq $nav -or $nav.Current.IsOffscreen){UI @('invoke','TogglePaneButton')|Out-Null};UI @('invoke',$Id)|Out-Null;Start-Sleep -Milliseconds 220}
function Heading([string]$Name){
    $deadline=[DateTime]::UtcNow.AddSeconds(3)
    do {
    $found=(Root).FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)))
    $contentTop=(Root).Current.BoundingRectangle.Top+32*$scale
    $visible=@($found|Where-Object {-not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 -and $_.Current.BoundingRectangle.Top -ge $contentTop}|Sort-Object {$_.Current.BoundingRectangle.Left} -Descending)
    if($visible.Count -ge 1){return $visible[0].Current.BoundingRectangle}
    Start-Sleep -Milliseconds 100
    }while([DateTime]::UtcNow -lt $deadline)
    throw "Missing content heading: $Name"
}
try{
    Write-Json 'settings.json' @{SchemaVersion=1;WorkspaceRoot=$data;AgentExecutable='unused';AgentProvider='custom';AgentMode='exec';AgentArguments='';TrustedWorkspaceRoots=$data;AutoConnectProvider='none'}
    Write-Json 'onboarding.json' @{SchemaVersion=3;Step=4;Status='completed'}
    $policy=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert ([bool]$policy) 'Missing consent policy.'
    Write-Json 'consent.json' @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    Write-Json 'language.json' @{Language=$Language}
    Write-Json 'appearance.json' @{Theme=$(if($Language -eq 'ja'){'dark'}else{'light'})}
    Write-Json 'pricing-auto.json' @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'ChatAgentBridge.Desktop.exe'));$start.UseShellExecute=$false
    $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
    $app=[Diagnostics.Process]::Start($start)
    UI @('wait-for','NavOverview','-t','15000')|Out-Null
    $app.Refresh();$window=$app.MainWindowHandle;$scale=[MaximizedLayoutNative]::GetDpiForWindow($window)/96.0
    # Close the initial load banner via ordinary navigation before comparing page origins.
    Navigate 'NavWorkbench';Navigate 'NavOverview'
    $pages=@('NavOverview','NavWorkbench','NavAgentSettings','NavConnections','NavChat','NavHistory','NavAppearance','NavAbout')
    $labels=if($Language -eq 'ja'){@('Chat Agent Bridge','手動で実行','エージェント設定','接続設定','チャット受信','履歴と会話','外観','情報')}else{@('Chat Agent Bridge','Run manually','Agent settings','Connection settings','Chat inbox','History and conversations','Appearance','About')}
    $sizes=if($CaptureOnly){@('maximized')}else{@('maximized','1160','720','500','maximized-again')}
    foreach($size in $sizes){
        if($size -like 'maximized*'){[void][MaximizedLayoutNative]::ShowWindow($window,3)}else{[void][MaximizedLayoutNative]::ShowWindow($window,9);[void][MaximizedLayoutNative]::SetWindowPos($window,[IntPtr]::Zero,50,50,[int]([int]$size*$scale),[int](850*$scale),0x0014)}
        Start-Sleep -Milliseconds 400
        if($size -like 'maximized*'){Assert ([MaximizedLayoutNative]::IsZoomed($window)) 'Test window did not maximize.'}
        $baseline=$null
        for($index=0;$index -lt $pages.Count;$index++){
            Navigate $pages[$index]
            $heading=Heading $labels[$index];$bounds=(Root).Current.BoundingRectangle
            $left=($heading.Left-$bounds.Left)/$scale;$top=($heading.Top-$bounds.Top)/$scale
            $measurement=@{size=$size;page=$pages[$index];left=$left;top=$top}
            $measurements.Add($measurement)
            if($null -eq $baseline){$baseline=$measurement}
            if(-not $CaptureOnly){
                Assert ([Math]::Abs($left-$baseline.left) -le 2) "Heading left edge differs: $size $($pages[$index]) ($left vs $($baseline.left))"
                Assert ([Math]::Abs($top-$baseline.top) -le 2) "Heading top differs: $size $($pages[$index]) ($top vs $($baseline.top))"
                $toggle=(Element 'TogglePaneButton').Current.BoundingRectangle
                Assert (-not $heading.IntersectsWith($toggle)) 'Heading overlaps navigation toggle.'
                # WinUI's passive Grid/StackPanel peers report empty bounds. Measure
                # visible input controls and account for their containing card padding.
                if($index -in @(0,1,2,3,6)){
                    $fieldId=switch($index){0{'OverviewAgentTile'}1{'Prompt'}2{'WorkspaceRoot'}3{'MattermostServerUrl'}6{'DisplayLanguage'}}
                    # ComboBox automation bounds include its 4-DIP focus inset on each side.
                    $padding=switch($index){0{16}1{0}2{16}3{20}6{12}}
                    $field=(Element $fieldId).Current.BoundingRectangle
                    $bodyLeft=($field.Left-$bounds.Left)/$scale-$padding
                    $bodyWidth=$field.Width/$scale+2*$padding
                    if($index -eq 0){$bodyWidth=((Element 'OverviewConnectionTile').Current.BoundingRectangle.Right-$field.Left)/$scale+2*$padding}
                    $measurement.bodyLeft=$bodyLeft;$measurement.bodyWidth=$bodyWidth
                    Assert ([Math]::Abs($bodyLeft-$left) -le 2) "Content body is offset: $size $($pages[$index]) ($bodyLeft vs $left)."
                    Assert ($bodyWidth -le 1042) 'Readable content width exceeds its cap.'
                    Assert ($bounds.Left+($bodyLeft+$bodyWidth)*$scale -le $bounds.Right+2) 'Content extends outside the window.'
                    if($size -like 'maximized*'){Assert ([Math]::Abs($bodyWidth-1040) -le 3) "Settings content shrank unexpectedly: $($pages[$index]) $bodyWidth."}
                }
            }
            if($CaptureOnly -or $size -in @('maximized','720')){UI @('screenshot','-o',(Join-Path $data ($size+'-'+$pages[$index]+'.png')))|Out-Null}
        }
        Write-Output "PASS $size : measured all 8 tabs"
    }
}
finally{
    Write-Json 'measurements.json' $measurements.ToArray()
    if($app -and -not $app.HasExited){UI @('screenshot','-o',(Join-Path $data 'last-screen.png'))|Out-Null}
    if($app -and -not $app.HasExited){Stop-Process -Id $app.Id -Force}
    Write-Output "Layout artifacts: $data"
}
