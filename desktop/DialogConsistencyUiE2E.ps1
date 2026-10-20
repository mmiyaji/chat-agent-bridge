param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateSet('ja','en','both')][string]$Language='both',
    [ValidateSet('light','dark','both')][string]$Theme='both'
)
$ErrorActionPreference='Stop'
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$payload=[IO.Path]::GetFullPath($AppDirectory)
if(-not $payload.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path (Join-Path $payload 'AppxManifest.xml'))){throw 'Use an isolated portable test payload under desktop/artifacts.'}
$run=Join-Path $artifacts ('dialog-consistency-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $run|Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class DialogConsistencyNative {
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
}
'@
$app=$null;$window=[IntPtr]::Zero;$checks=[Collections.Generic.List[object]]::new();$measurements=[Collections.Generic.List[object]]::new()
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function UI([string[]]$Arguments){$out=winapp ui @Arguments -a $app.Id --json;if($LASTEXITCODE){throw "UI failed: $($Arguments -join ' ')"};$out}
function Root{[Windows.Automation.AutomationElement]::FromHandle($window)}
function Find($Parent,[string]$Id){$Parent.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Element([string]$Id){Find (Root) $Id}
function Visible([string]$Id){$e=Element $Id;return $e -and -not $e.Current.IsOffscreen -and $e.Current.BoundingRectangle.Height -gt 0}
function Go([string]$Id){if(-not (Visible $Id)){UI @('invoke','TogglePaneButton')|Out-Null};UI @('invoke',$Id)|Out-Null;Start-Sleep -Milliseconds 120}
function Scroll([string]$Id){UI @('scroll-into-view',$Id)|Out-Null}
function Json([string]$Name,$Value){$Value|ConvertTo-Json -Depth 12|Set-Content (Join-Path $data $Name) -Encoding utf8}
function Resize([int]$Width){$scale=[DialogConsistencyNative]::GetDpiForWindow($window)/96.0;[void][DialogConsistencyNative]::SetWindowPos($window,[IntPtr]::Zero,40,40,[int]($Width*$scale),[int](740*$scale),0x0014);Start-Sleep -Milliseconds 180}
function Capture([string]$Name){UI @('screenshot','-o',(Join-Path $data ($Name+'.png')))|Out-Null}
function Dismiss([string]$Id,[int]$Buttons,[string]$Name){
    UI @('wait-for',$Id,'-t','6000')|Out-Null
    $dialog=Element $Id;$bounds=(Root).Current.BoundingRectangle
    $close=Find $dialog 'CloseButton'
    Assert ($close -and -not $close.Current.IsOffscreen -and $close.Current.IsEnabled) "$Id has no usable native CloseButton"
    $right=$close.Current.BoundingRectangle;$count=0
    foreach($action in @('PrimaryButton','SecondaryButton','CloseButton')){
        $button=Find $dialog $action
        if(-not $button -or $button.Current.IsOffscreen -or $button.Current.BoundingRectangle.Width -le 0){continue}
        $count++;$r=$button.Current.BoundingRectangle
        Assert ($r.Left -ge $bounds.Left -and $r.Right -le $bounds.Right -and $r.Top -ge $bounds.Top -and $r.Bottom -le $bounds.Bottom) "$Id $action is clipped"
        if($action -ne 'CloseButton'){
            Assert ($r.Right -le $right.Left -and [Math]::Abs($r.Bottom-$right.Bottom) -le 2) "$Id safe action is not at the far right of the footer"
        }
        $measurements.Add(@{locale=$locale;theme=$color;width=$width;dialog=$Id;button=$action;label=$button.Current.Name;left=$r.Left;right=$r.Right;top=$r.Top;bottom=$r.Bottom})
    }
    Assert ($count -eq $Buttons) "$Id has $count buttons instead of $Buttons"
    Capture $Name
    $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    UI @('wait-for',$Id,'--gone','-t','4000')|Out-Null
    Assert (-not $app.HasExited) "Dismissing $Id stopped the app"
    $checks.Add(@{locale=$locale;theme=$color;width=$width;dialog=$Id;passed=$true})
}
function Select-Language([string]$Label){
    Scroll 'DisplayLanguage';UI @('invoke','DisplayLanguage')|Out-Null
    $tree=UI @('inspect','DisplayLanguage','-d','6')|ConvertFrom-Json
    function Choice($nodes){foreach($node in $nodes){if($node.type -eq 'ListItem' -and $node.name -eq $Label){$node.selector};if($node.children){Choice $node.children}}}
    $selector=@($tree.windows|ForEach-Object {Choice $_.elements})|Select-Object -First 1
    Assert ([bool]$selector) "Language choice not found: $Label"
    UI @('invoke',$selector)|Out-Null
}
function History-Selection {
    Go 'NavHistory';UI @('invoke','SavedConversationsTab')|Out-Null
    $back=Element 'BackToSessionsButton';if($back -and -not $back.Current.IsOffscreen){UI @('invoke','BackToSessionsButton')|Out-Null}
    $list=Element 'SavedSessionsList'
    $item=$list.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
    Assert ($null -ne $item) 'Isolated session fixture did not appear'
    $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    UI @('wait-for','RenameSession','-t','4000')|Out-Null
}
try{
    $policy=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/OnboardingConsentStore.cs') -Raw),'CurrentPolicyVersion\s*=\s*"([^"]+)"').Groups[1].Value
    Assert ([bool]$policy) 'Missing current consent policy'
    $locales=if($Language -eq 'both'){@('ja','en')}else{@($Language)}
    $colors=if($Theme -eq 'both'){@('light','dark')}else{@($Theme)}
    foreach($locale in $locales){foreach($color in $colors){
        $data=Join-Path $run ($locale+'-'+$color);New-Item -ItemType Directory $data|Out-Null
        Json 'settings.json' @{SchemaVersion=1;WorkspaceRoot=$data;AgentExecutable='unused';AgentProvider='custom';AgentMode='exec';AgentArguments='';TrustedWorkspaceRoots=$data;AutoConnectProvider='none';RequireApproval=$true}
        Json 'onboarding.json' @{SchemaVersion=3;Step=4;Status='completed'}
        Json 'consent.json' @{PolicyVersion=$policy;AcceptedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
        Json 'language.json' @{Language=$locale};Json 'appearance.json' @{Theme=$color}
        Json 'background.json' @{CloseToTray=$false}
        Json 'pricing-auto.json' @{schemaVersion=1;autoUpdateEnabled=$false;catalogJson=(Get-Content (Join-Path $PSScriptRoot 'ChatAgentBridge.Core/Pricing/model-prices.json') -Raw)}
        Json 'pricing-manual.json' @{schemaVersion=1;models=@(@{model='dialog-fixture';input=@{min=1;max=1};cachedInput=@{min=0.1;max=0.1};cacheWrite=@{min=1;max=1};output=@{min=2;max=2}})}
        Json 'agent-connections.json' @{SchemaVersion=1;Profiles=@(@{Provider='claude';Executable='unused';Arguments='';Mode='exec';Model=$null;ReasoningEffort=$null;PermissionMode='inherit';AllowNetwork=$false;CliVersion='offline dialog fixture';CheckedAt=[DateTimeOffset]::UtcNow.ToString('o')})}
        Json 'sessions.json' @{SchemaVersion=1;Bindings=@(@{Key=@{Provider='slack';ChannelId='C00000001';ThreadId='dialog-thread';UserId='U00000001';Origin='slack'};WorkspaceRoot=$data;AgentExecutable='unused';AgentArguments='exec --json -';SessionId=[guid]::NewGuid().ToString('N');DisplayName='Dialog fixture';UpdatedAt=[DateTimeOffset]::UtcNow.ToString('o')});Workspaces=@()}
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'ChatAgentBridge.Desktop.exe'));$start.UseShellExecute=$false
        $start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
        $app=[Diagnostics.Process]::Start($start)
        UI @('wait-for','NavOverview','-t','15000')|Out-Null;$app.Refresh();$window=$app.MainWindowHandle
        Go 'NavWorkbench';Go 'NavOverview'
        foreach($width in @(1160,500)){
            Resize $width
            foreach($kind in @('Terms','Privacy')){
                Go 'NavOverview';Scroll ('Overview'+$kind);UI @('invoke',('Overview'+$kind))|Out-Null
                Dismiss 'PolicyDialog' 1 "$width-policy-$kind"
            }
            Go 'NavWorkbench';UI @('set-value','Prompt','Dialog fixture: never execute')|Out-Null;Scroll 'RunAgent';UI @('invoke','RunAgent')|Out-Null
            Dismiss 'RunConfirmation' 2 "$width-run"
            Go 'NavAbout'
            Scroll 'AboutExit';UI @('invoke','AboutExit')|Out-Null
            Dismiss 'AboutExitConfirmation' 2 "$width-about-exit"
            [void][DialogConsistencyNative]::PostMessageW($window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
            Dismiss 'WindowCloseConfirmation' 3 "$width-window-close"
            Go 'NavOverview'
            Select-Language $(if($locale -eq 'ja'){'English'}else{'日本語 (Japanese)'})
            Dismiss 'LanguageRestartDialog' 2 "$width-language-later"
            Select-Language $(if($locale -eq 'ja'){'日本語 (Japanese)'}else{'English'})
            Dismiss 'LanguageRestartDialog' 2 "$width-language-restore"
            Write-Output "PASS $locale $color $width : dialog footer ordering, bounds and dismiss actions"
        }
        # Conversation dialogs share the same footer while preserving cancelled edits.
        foreach($pair in @(@('RenameSession','RenameSessionDialog'),@('EditSessionModel','SessionModelDialog'),@('EditSessionExecutionPolicy','SessionExecutionPolicyDialog'),@('SwitchSessionAgent','AgentSwitchDialog'),@('DetachSession','DetachSessionConfirmation'))){
            History-Selection;Scroll $pair[0];UI @('invoke',$pair[0])|Out-Null
            Dismiss $pair[1] 2 ('500-history-'+$pair[0])
        }
        $saved=Get-Content (Join-Path $data 'sessions.json') -Raw|ConvertFrom-Json
        Assert ($saved.Bindings.Count -eq 1 -and $saved.Bindings[0].DisplayName -eq 'Dialog fixture') 'Cancel changed or removed the saved conversation'
        Assert (-not (Test-Path (Join-Path $data 'history.json'))) 'Cancel started an agent execution'

        Go 'NavHistory';UI @('invoke','ActivityTab')|Out-Null
        Scroll 'PricingSettings'
        (Element 'PricingSettings').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Scroll 'PricingKnownModel'
        $picker=Element 'PricingKnownModel';$expand=$picker.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern);$expand.Expand()
        $model=(Root).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'dialog-fixture'),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
        Assert ($null -ne $model) 'Manual pricing fixture was not listed'
        $model.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();$expand.Collapse()
        Scroll 'PricingRemoveManual';UI @('invoke','PricingRemoveManual')|Out-Null
        Dismiss 'PricingRemoveConfirmation' 2 '500-pricing-remove'
        Assert ((Get-Content (Join-Path $data 'pricing-manual.json') -Raw|ConvertFrom-Json).models.Count -eq 1) 'Cancel removed the manual model price'

        Go 'NavOverview';Scroll 'StartOnboarding';UI @('invoke','StartOnboarding')|Out-Null
        UI @('wait-for','OnboardingPrevious','-t','6000')|Out-Null
        foreach($width in @(1160,500)){
            Resize $width
            $back=(Element 'OnboardingPrevious').Current.BoundingRectangle;$next=(Element 'OnboardingNext').Current.BoundingRectangle;$w=(Root).Current.BoundingRectangle
            Assert ($back.Left -lt $next.Left -and $back.Bottom -le $w.Bottom -and $next.Bottom -le $w.Bottom -and [Math]::Abs($back.Bottom-$next.Bottom) -le 2) 'Wizard Back and Next lost their lower left/right placement'
            UI @('scroll-into-view','WizardTerms')|Out-Null;UI @('invoke','WizardTerms')|Out-Null
            Dismiss 'PolicyDialog' 1 "$width-wizard-policy"
            Capture "$width-wizard-footer"
            $checks.Add(@{locale=$locale;theme=$color;width=$width;dialog='WizardFooter';passed=$true})
        }
        Scroll 'WizardAcceptTerms';(Element 'WizardAcceptTerms').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
        Scroll 'WizardAcceptPrivacy';(Element 'WizardAcceptPrivacy').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
        UI @('invoke','OnboardingNext')|Out-Null;Start-Sleep -Milliseconds 150
        Assert ((Element 'OnboardingPrevious').Current.IsEnabled) 'Wizard Back did not enable on the next step'
        UI @('invoke','OnboardingPrevious')|Out-Null
        Assert (-not (Element 'OnboardingPrevious').Current.IsEnabled) 'Wizard Back did not return to the first step'
        UI @('invoke','OnboardingDefer')|Out-Null
        Write-Output "PASS $locale $color : conversation cancellation and wizard Back navigation"
        Stop-Process -Id $app.Id -Force;$app.WaitForExit();$app=$null
    }}
}
catch{$checks.Add(@{passed=$false;detail=$_.Exception.Message});throw}
finally{
    if($app -and -not $app.HasExited){Capture 'last-screen';Stop-Process -Id $app.Id -Force}
    $checks.ToArray()|ConvertTo-Json -Depth 8|Set-Content (Join-Path $run 'results.json') -Encoding utf8
    $measurements.ToArray()|ConvertTo-Json -Depth 8|Set-Content (Join-Path $run 'measurements.json') -Encoding utf8
    Write-Output "Dialog consistency artifacts: $run"
}
