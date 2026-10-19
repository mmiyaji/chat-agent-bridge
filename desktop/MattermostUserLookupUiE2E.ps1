param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [Parameter(Mandatory)][string]$DataDir,
    [ValidateSet('ja','en')][string]$Language='ja',
    [switch]$LayoutOnly
)
$ErrorActionPreference='Stop'
$proc=Get-Process -Id $AppProcessId
$layout=Split-Path (Split-Path $proc.Path)
[xml]$manifest=Get-Content (Join-Path $layout 'AppxManifest.xml') -Raw
if(-not ([string]$manifest.Package.Identity.Name).StartsWith('CAB.Review.',[StringComparison]::Ordinal)){throw 'Use an isolated review package.'}
$windowDeadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
do{
    $window=winapp ui list-windows -a $AppProcessId --json 2>$null|ConvertFrom-Json|Where-Object title -eq 'Chat Agent Bridge'|Select-Object -First 1
    if($window){break}
    Start-Sleep -Milliseconds 100
}while([DateTimeOffset]::UtcNow -lt $windowDeadline)
if(-not $window){throw 'Review app window did not appear.'}
. "$PSScriptRoot/ActivityUiE2E.ps1" -AppProcessId $AppProcessId -DataDir $DataDir -Language $Language -OnlyHelpers
$settings=Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json
$baseUri=[uri]$settings.MattermostServerUrl
if($baseUri.Host -ne '127.0.0.1' -or $baseUri.Scheme -ne 'http' -or $settings.AutoConnectProvider -ne 'none'){throw 'Use an isolated loopback fixture with automatic connection disabled.'}
$output=Join-Path $dataPath ('mattermost-lookup-review-'+$Language)
New-Item -ItemType Directory $output -Force|Out-Null
$testId='abcdefghijklmnopqrstuvwx12'
$server=Start-Job -ArgumentList $baseUri.Port -ScriptBlock {
    param($Port)
    $listener=[Net.HttpListener]::new()
    $listener.IgnoreWriteExceptions=$true
    $listener.Prefixes.Add("http://127.0.0.1:$Port/")
    $listener.Start()
    try{
        while($listener.IsListening){
            $pending=$listener.GetContextAsync()
            while(-not $pending.Wait(100)){}
            $ctx=$pending.GetAwaiter().GetResult()
            $name=[uri]::UnescapeDataString(($ctx.Request.Url.AbsolutePath -split '/')[-1])
            $status=200
            $body=@{id='abcdefghijklmnopqrstuvwx12';username=$name;first_name='Lookup';last_name='Test User';nickname='Test user';is_bot=$false;delete_at=0}
            if($name -eq 'slow'){Start-Sleep -Seconds 2}
            if($name -eq 'missing'){$status=404;$body=@{message='Synthetic not found'}}
            if($name -eq 'authfail'){$status=401;$body=@{message='Do not expose this response'}}
            if($name -eq 'robot'){$body.is_bot=$true}
            if($name -eq 'inactive'){$body.delete_at=123456}
            if($name -eq 'fixture'){$body.first_name='Long display name for a person used to verify wrapping in a narrow settings panel'}
            if($name -ne 'ping' -and ($ctx.Request.HttpMethod -ne 'GET' -or $ctx.Request.Headers['Authorization'] -ne 'Bearer synthetic-lookup-token')){$status=401}
            $ctx.Response.StatusCode=$status
            $ctx.Response.ContentType='application/json'
            $bytes=[Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Compress))
            try{$ctx.Response.OutputStream.Write($bytes,0,$bytes.Length);$ctx.Response.Close()}catch{}
        }
    }finally{$listener.Stop();$listener.Close()}
}
function Ready-Server{
    $until=[DateTimeOffset]::UtcNow.AddSeconds(10)
    do{
        try{$null=Invoke-WebRequest ($baseUri.AbsoluteUri.TrimEnd('/')+'/ping') -TimeoutSec 1;return}catch{}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $until)
    throw 'Synthetic lookup server did not start.'
}
function Text([string]$Id){return [string](Element $Id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value}
function Set-Text([string]$Id,[string]$Value){(Element $Id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Value)}
function Enabled([string]$Id){$e=Element $Id;return ($e -and $e.Current.IsEnabled)}
function Lookup([string]$Name){
    Scroll-To 'MattermostUsernameLookupInput'
    Set-Text 'MattermostUsernameLookupInput' $Name
    Scroll-To 'LookupMattermostUserButton'
    UI @('invoke','LookupMattermostUserButton')|Out-Null
    UI @('wait-for','LookupMattermostUserButton','-p','IsEnabled','--value','True','-t','10000')|Out-Null
    if($Name.TrimStart('@') -eq 'fixture'){Wait-Text 'MattermostUsernameLookupResult' $testId}
}
function Fits([string]$Id,[int]$Width){
    Scroll-To $Id
    $e=Element $Id
    if($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button){
        $minimumHeight=30*[ActivityLayoutNative]::GetDpiForWindow($script:hwnd)/96.0
        $sp=(Element 'MainContentScroll').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
        for($i=0;$i -lt 20 -and (Element $Id).Current.BoundingRectangle.Height -lt $minimumHeight;$i++){
            $sp.SetScrollPercent(-1,[Math]::Min(100,$sp.Current.VerticalScrollPercent+2))
            Start-Sleep -Milliseconds 100
        }
        Assert ((Element $Id).Current.BoundingRectangle.Height -ge $minimumHeight) "$Id is only partially visible"
    }
    $r=(Element $Id).Current.BoundingRectangle
    $w=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
    Assert ($r.Width -gt 0 -and $r.Left -ge $w.Left -and $r.Right -le $w.Right) "$Id clips at $Width DIP"
}
function WizardScroll-To([string]$Id){
    $scroll=Element 'WizardScroll'
    $sp=$scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    if($sp.Current.VerticallyScrollable){$sp.SetScrollPercent(-1,0)}
    for($i=0;$i -lt 60;$i++){
        $e=Element $Id
        if($e -and (Visible $e)){
            $r=$e.Current.BoundingRectangle;$v=$scroll.Current.BoundingRectangle
            $bottom=[Math]::Min($v.Bottom,(Element 'OnboardingPrevious').Current.BoundingRectangle.Top-16)
            # UIA clips partially visible controls to the viewport: a 2px strip
            # must not count as a fully visible 32-DIP action.
            $minimumHeight=30*[ActivityLayoutNative]::GetDpiForWindow($script:hwnd)/96.0
            if($r.Height -ge $minimumHeight -and $r.Top -ge $v.Top -and $r.Bottom -le $bottom){return}
        }
        if(-not $sp.Current.VerticallyScrollable -or $sp.Current.VerticalScrollPercent -ge 100){break}
        $sp.SetScrollPercent(-1,[Math]::Min(100,$sp.Current.VerticalScrollPercent+2))
        Start-Sleep -Milliseconds 70
    }
    throw "Cannot expose wizard control $Id"
}
try{
    Ready-Server
    Resize 1160;Go 'NavConnections'
    if(-not $LayoutOnly){
        Lookup '@fixture'
        Wait-Text 'MattermostUsernameLookupResult' $testId
        Assert ((Text 'MattermostAllowedUserIds') -eq '') 'Lookup itself granted permission'
        Assert (Enabled 'AddMattermostUserButton') 'Found account cannot be added'
        Scroll-To 'AddMattermostUserButton';UI @('invoke','AddMattermostUserButton')|Out-Null
        Assert ((Text 'MattermostAllowedUserIds').Trim() -eq $testId) 'Add did not insert the resolved ID'
        if(Enabled 'AddMattermostUserButton'){UI @('invoke','AddMattermostUserButton')|Out-Null}
        Assert (((Text 'MattermostAllowedUserIds') -split '[\s,;]+'|Where-Object {$_ -eq $testId}).Count -eq 1) 'Repeated Add duplicated the ID'
        Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $settingsHash) 'Lookup or Add implicitly saved settings'
        Pass 'Exact username lookup accepts @, requires explicit Add, deduplicates, and does not auto-save'

        Lookup 'fixture'
        Set-Text 'MattermostUsernameLookupInput' 'missing'
        Assert (-not (Enabled 'AddMattermostUserButton')) 'Editing username left an old account available'
        foreach($name in @('missing','robot','inactive','authfail')){
            Lookup $name
            Assert (-not (Enabled 'AddMattermostUserButton')) "Rejected account $name can be added"
            Assert (-not (Value 'MattermostUsernameLookupStatus').Contains('Do not expose this response')) 'Raw service errors were exposed'
        }
        Pass 'Not found, bot, inactive account and authentication failures cannot grant permission'

        Lookup 'fixture'
        UI @('set-value','MattermostServerUrl',($baseUri.AbsoluteUri.TrimEnd('/')+'/changed'))|Out-Null
        Assert (-not (Enabled 'AddMattermostUserButton')) 'Changing server retained a lookup authority'
        UI @('set-value','MattermostServerUrl',$baseUri.AbsoluteUri.TrimEnd('/'))|Out-Null
        Lookup 'fixture'
        UI @('set-value','MattermostToken','changed-synthetic-token')|Out-Null
        Assert (-not (Enabled 'AddMattermostUserButton')) 'Changing token retained a lookup authority'
        UI @('set-value','MattermostToken','')|Out-Null
        Assert (-not (Enabled 'LookupMattermostUserButton')) 'Empty token left lookup enabled'
        UI @('set-value','MattermostToken','synthetic-lookup-token')|Out-Null
        Assert (Enabled 'LookupMattermostUserButton') 'Filling the token last did not enable lookup'
        Pass 'Changing the entered server URL or token invalidates the result'

        Scroll-To 'MattermostUsernameLookupInput';Set-Text 'MattermostUsernameLookupInput' 'slow'
        Scroll-To 'LookupMattermostUserButton';UI @('invoke','LookupMattermostUserButton')|Out-Null
        Assert (-not (Enabled 'LookupMattermostUserButton')) 'Lookup button was not guarded during the request'
        Set-Text 'MattermostUsernameLookupInput' 'fixture'
        Start-Sleep -Milliseconds 2300
        Assert (-not (Enabled 'AddMattermostUserButton')) 'Stale async response restored an addable result'
        Pass 'Busy lookup cannot be repeated and stale responses cannot restore a previous account'

        Lookup 'fixture'
        UI @('invoke','SaveConnections')|Out-Null
        $until=[DateTimeOffset]::UtcNow.AddSeconds(5)
        do{
            $saved=Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json
            if($saved.MattermostAllowedUserIds -eq $testId){break}
            Start-Sleep -Milliseconds 100
        }while([DateTimeOffset]::UtcNow -lt $until)
        Assert ($saved.MattermostAllowedUserIds -eq $testId) 'Explicit Save did not persist the resolved ID'
        Pass 'Explicit Save persists the allowed internal ID'
    }
    Lookup 'fixture'
    foreach($theme in @('light','dark')){
        Go 'NavAppearance';Scroll-To $(if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'})
        UI @('invoke',$(if($theme -eq 'light'){'ThemeLight'}else{'ThemeDark'}))|Out-Null
        foreach($width in @(1160,500)){
            Resize $width;Go 'NavConnections'
            foreach($id in @('MattermostUsernameLookupInput','LookupMattermostUserButton','MattermostUsernameLookupResult','AddMattermostUserButton')){Fits $id $width}
            Capture "lookup-$theme-$width.png"
        }
    }
    Pass 'Localized lookup controls and wrapped account details fit Light/Dark at 500 and 1160 DIP'
    if(-not $LayoutOnly){
        Resize 1160;Go 'NavOverview';Scroll-To 'StartOnboarding';UI @('invoke','StartOnboarding')|Out-Null
        Wait-Text 'OnboardingTitle' '[1/5]'
        foreach($id in @('WizardAcceptTerms','WizardAcceptPrivacy')){WizardScroll-To $id;UI @('invoke',$id)|Out-Null}
        UI @('invoke','OnboardingNext')|Out-Null;Wait-Text 'OnboardingTitle' '[2/5]'
        UI @('invoke','OnboardingNext')|Out-Null;Wait-Text 'OnboardingTitle' '[3/5]'
        WizardScroll-To 'CheckCli';UI @('invoke','CheckCli')|Out-Null
        UI @('wait-for','CheckCli','-p','IsEnabled','--value','True','-t','15000')|Out-Null
        UI @('invoke','OnboardingNext')|Out-Null;Wait-Text 'OnboardingTitle' '[4/5]'
        WizardScroll-To 'WizardMattermostUsernameLookupInput'
        Set-Text 'WizardMattermostUsernameLookupInput' 'missing'
        WizardScroll-To 'WizardLookupMattermostUserButton';UI @('invoke','WizardLookupMattermostUserButton')|Out-Null
        UI @('wait-for','WizardLookupMattermostUserButton','-p','IsEnabled','--value','True','-t','10000')|Out-Null
        Assert (-not (Enabled 'WizardAddMattermostUserButton')) 'Wizard allows a missing account'
        WizardScroll-To 'WizardMattermostUsernameLookupInput';Set-Text 'WizardMattermostUsernameLookupInput' 'fixture'
        WizardScroll-To 'WizardLookupMattermostUserButton';UI @('invoke','WizardLookupMattermostUserButton')|Out-Null
        Wait-Text 'WizardMattermostUsernameLookupResult' $testId
        WizardScroll-To 'WizardAddMattermostUserButton';UI @('invoke','WizardAddMattermostUserButton')|Out-Null
        Assert ((Text 'WizardMattermostAllowedUserIds').Trim() -eq $testId) 'Wizard did not preserve the one resolved ID'
        foreach($width in @(1160,500)){
            Resize $width;WizardScroll-To 'WizardAddMattermostUserButton';Capture "wizard-lookup-$width.png"
            foreach($id in @('WizardMattermostUsernameLookupInput','WizardLookupMattermostUserButton','WizardMattermostUsernameLookupResult','WizardAddMattermostUserButton')){
                $r=(Element $id).Current.BoundingRectangle;$w=([Windows.Automation.AutomationElement]::FromHandle($script:hwnd)).Current.BoundingRectangle
                Assert ($r.Left -ge $w.Left -and $r.Right -le $w.Right) "$id clips in wizard"
            }
        }
        UI @('invoke','OnboardingDefer')|Out-Null
        Pass 'The wizard supports the same exact lookup, safe Add, duplicate guard and narrow layout'
    }
    Assert ((Get-FileHash $historyFile).Hash -eq $historyHash) 'User lookup modified execution history'
}catch{
    $checks.Add(@{Name='Mattermost user lookup UI';Status='FAIL';Detail="$($_.Exception.Message)"})
    throw
}finally{
    Stop-Job $server;Remove-Job $server
    $resultFile=if($LayoutOnly){'results-layout.json'}else{'results.json'}
    $checks|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output $resultFile)
    $checks|Format-Table -AutoSize
}
