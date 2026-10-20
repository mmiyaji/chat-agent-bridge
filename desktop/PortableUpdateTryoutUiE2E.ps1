param([Parameter(Mandatory)][string]$TestArchive,[Parameter(Mandatory)][string]$UpdateArchive)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$demo=& (Join-Path $PSScriptRoot 'New-PortableUpdateTryout.ps1') -TestArchive $TestArchive -UpdateArchive $UpdateArchive
$app=Get-Process -Id $demo.ProcessId
$checks=[Collections.Generic.List[string]]::new()
function Assert([bool]$value,[string]$message){if(-not $value){throw $message}}
function UI([string[]]$commands){$out=winapp ui @commands -a $app.Id --json;if($LASTEXITCODE){throw "UI failed: $commands"};$out}
function Element([string]$id){$app.Refresh();[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Name([string]$id){$e=Element $id;if($e){$e.Current.Name}else{''}}
function Ready([string]$id){$e=Element $id;$e -and -not $e.Current.IsOffscreen -and $e.Current.IsEnabled}
function Wait([scriptblock]$condition,[string]$failure,[int]$seconds=30){$end=[DateTime]::UtcNow.AddSeconds($seconds);do{if(& $condition){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $end);throw $failure}
function Capture([string]$label){UI @('screenshot','-o',(Join-Path $demo.Root ($label+'.png')))|Out-Null}
function Pass([string]$label){$checks.Add($label);Write-Output "PASS $label"}
try{
 UI @('wait-for','NavAbout','-t','15000')|Out-Null
 UI @('invoke','NavAbout')|Out-Null
 UI @('scroll-into-view','PortableCheckUpdates')|Out-Null
 $before=(Name 'AboutVersion')
 $preserved=@{};foreach($name in @('language.json','appearance.json','update-retention-marker.txt')){$preserved[$name]=(Get-FileHash (Join-Path $demo.Data $name)).Hash}
 UI @('invoke','PortableUpdateIncludePrereleases')|Out-Null
 UI @('invoke','PortableCheckUpdates')|Out-Null
 Wait {(Ready 'PortableCheckUpdates') -and (Name 'PortableUpdateStatus') -match '見つかりませんでした'} 'Disabled preview releases still offered a preview'
 Pass 'Preview filter hides the local preview update'
 UI @('invoke','PortableUpdateIncludePrereleases')|Out-Null
 UI @('invoke','PortableCheckUpdates')|Out-Null
 Wait {Ready 'PortableDownloadUpdate'} 'Local update was not detected'
 Assert ((Name 'PortableUpdateStatus').Contains($demo.UpdateVersion)) 'Incorrect update version'
 Capture 'offered';Pass 'Older test version detects the local newer release'
 $asset=Get-ChildItem (Join-Path $demo.Fixture 'assets') -File|Select-Object -First 1
 $stream=[IO.File]::Open($asset.FullName,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{$original=$stream.ReadByte();$stream.Position=0;$stream.WriteByte($original -bxor 1)}finally{$stream.Dispose()}
 UI @('invoke','PortableDownloadUpdate')|Out-Null
 Wait {(Ready 'PortableDownloadUpdate') -and (Name 'PortableUpdateStatus') -match '検証できませんでした'} 'Corrupted download was not rejected'
 Assert ((Get-Content (Join-Path $demo.Install 'portable-release.json') -Raw|ConvertFrom-Json).releaseVersion -eq $demo.TestVersion) 'Integrity failure changed the installed version'
 Capture 'integrity-rejected';Pass 'Real SHA-256 validation rejects a corrupted download without replacing the app'
 $stream=[IO.File]::Open($asset.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$stream.WriteByte($original)}finally{$stream.Dispose()}
 UI @('invoke','PortableDownloadUpdate')|Out-Null
 Wait {Ready 'PortableApplyUpdate'} 'Valid download did not become ready' 60
 Capture 'downloaded';Pass 'Verified archive is staged for update'
 UI @('invoke','PortableApplyUpdate')|Out-Null
 UI @('wait-for','PortableUpdateApplyConfirmation','-t','5000')|Out-Null
 Capture 'confirmation';UI @('invoke','CloseButton')|Out-Null
 UI @('wait-for','PortableUpdateApplyConfirmation','--gone','-t','4000')|Out-Null
 Assert (-not $app.HasExited) 'Cancelling update closed the old app'
 Assert ((Get-Content (Join-Path $demo.Install 'portable-release.json') -Raw|ConvertFrom-Json).releaseVersion -eq $demo.TestVersion) 'Cancelling update replaced files'
 Pass 'Cancelling restart keeps the old app and staged download'
 UI @('invoke','PortableApplyUpdate')|Out-Null
 UI @('wait-for','PortableUpdateApplyConfirmation','-t','5000')|Out-Null
 UI @('invoke','PrimaryButton')|Out-Null
 Wait {$app.HasExited} 'Old app did not exit for update' 30
 Wait {($script:updated=Get-CimInstance Win32_Process -Filter "Name='ChatAgentBridge.Desktop.exe'"|Where-Object {$_.ExecutablePath -eq (Join-Path $demo.Install 'ChatAgentBridge.Desktop.exe')}|Select-Object -First 1)} 'Updated app did not restart' 60
 $app=Get-Process -Id $updated.ProcessId
 UI @('wait-for','NavAbout','-t','15000')|Out-Null
 UI @('invoke','NavAbout')|Out-Null
 $after=Name 'AboutVersion'
 Assert ($after -ne $before) 'Restarted app still shows the old binary version'
 Assert ((Get-Content (Join-Path $demo.Install 'portable-release.json') -Raw|ConvertFrom-Json).releaseVersion -eq $demo.UpdateVersion) 'Restarted app is not the target release'
 foreach($name in $preserved.Keys){Assert ((Get-FileHash (Join-Path $demo.Data $name)).Hash -eq $preserved[$name]) "Update changed $name"}
 Assert (-not [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $demo.Install 'ChatAgentBridge.Desktop.dll'))).Contains('PortableUpdateTestFeed')) 'Target contains the developer test feed'
 Capture 'updated';Pass "Real helper restarted the production app ($before -> $after), retained Data and removed the test transport"
}finally{
 if($app -and -not $app.HasExited){Stop-Process -Id $app.Id -Force}
 @{checks=$checks.ToArray();tryout=$demo}|ConvertTo-Json -Depth 7|Set-Content (Join-Path $demo.Root 'ui-results.json') -Encoding utf8
 Write-Output "Tryout UI artifacts: $($demo.Root)"
}
