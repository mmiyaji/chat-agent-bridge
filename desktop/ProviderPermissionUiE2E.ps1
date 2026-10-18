param([Parameter(Mandatory)][int]$AppPid, [Parameter(Mandatory)][string]$DataDirectory)
$ErrorActionPreference='Stop'
$dataPath=[IO.Path]::GetFullPath($DataDirectory)
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $dataPath.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath (Join-Path $dataPath '.cab-monkey-test'))) { throw 'Use an isolated marked monkey directory.' }
if ((Get-Process -Id $AppPid).ProcessName -ne 'ChatAgentBridge.Desktop') { throw 'Unexpected target process.' }
$output=Join-Path $artifacts ('provider-permission-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $output | Out-Null
$checks=[Collections.Generic.List[object]]::new()
function UI([string[]]$Arguments) {
    $result=& winapp ui @Arguments -a $AppPid --json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "UI operation failed: $($Arguments -join ' ')" }
    return $result
}
function Assert([bool]$Condition,[string]$Message) { if(-not $Condition){throw $Message} }
function Wait([string]$Id,[string]$Value,[string]$Property='') {
    $arguments=@('wait-for',$Id,'--value',$Value,'-t','5000')
    if($Property){$arguments+=@('-p',$Property)}
    UI $arguments | Out-Null
}
function View([string]$Id) { UI @('scroll-into-view',$Id) | Out-Null }
function Provider([string]$Id) {
    View 'AgentProvider'
    UI @('invoke','AgentProvider') | Out-Null
    UI @('invoke',$Id) | Out-Null
}
function Permission([string]$Label) {
    View 'AgentPermissionMode'
    UI @('invoke','AgentPermissionMode') | Out-Null
    $popup=@((UI @('list-windows')|ConvertFrom-Json)|Where-Object title -eq 'PopupHost')
    Assert ($popup.Count -eq 1) 'Expected one choice popup.'
    $scope=@('-w',"$($popup[0].hwnd)")
    $found=UI (@('search',$Label)+$scope)|ConvertFrom-Json
    $choice=@($found.matches|Where-Object {$_.type -eq 'ListItem' -and $_.name -eq $Label -and $_.isEnabled -and -not $_.isOffscreen}|Select-Object -ExpandProperty selector -Unique)
    Assert ($choice.Count -eq 1) "Expected one permission choice '$Label'."
    UI (@('invoke',$choice[0])+$scope) | Out-Null
    Wait 'AgentPermissionMode' $Label
}
function Server([bool]$Enabled) {
    View 'UseAppServer'
    $value=(UI @('get-value','UseAppServer')|ConvertFrom-Json).text
    $expected=if($Enabled){'On'}else{'Off'}
    if($value -ne $expected){UI @('invoke','UseAppServer')|Out-Null}
    Wait 'UseAppServer' $expected
}
function Save {
    UI @('invoke','SaveSettings') | Out-Null
    Wait 'Title' '設定を保存しました'
    return Get-Content (Join-Path $dataPath 'settings.json') -Raw | ConvertFrom-Json
}
try {
    UI @('invoke','NavOverview') | Out-Null
    Wait 'SettingsLocation' $dataPath
    UI @('invoke','NavAgentSettings') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $dataPath 'work') | Out-Null
    View 'WorkspaceRoot'
    UI @('set-value','WorkspaceRoot',(Join-Path $dataPath 'work'))|Out-Null
    Provider 'ProviderOptionCodex'
    Permission 'フォルダー内の編集を自動許可'
    Provider 'ProviderOptionCustom'
    View 'AgentPermissionMode'
    Wait 'AgentPermissionMode' 'CLI既定'
    Wait 'AgentPermissionMode' 'False' 'IsEnabled'
    UI @('wait-for','AgentPermissionStatus','--value','CLI','--contains','-t','3000')|Out-Null
    UI @('screenshot','--focus','-o',(Join-Path $output 'custom-permissions.png'))|Out-Null
    $saved=Save
    Assert ($saved.AgentProvider -eq 'custom' -and $saved.AgentPermissionMode -eq 'inherit') 'Custom provider retained unsupported permissions.'
    $checks.Add(@{name='Custom CLI resets to its own policy, disables unsupported choices, and saves';passed=$true})

    Provider 'ProviderOptionCodex'
    Server $true
    Permission '操作ごとに確認（Codex App Server）'
    $saved=Save
    Assert ($saved.AgentPermissionMode -eq 'ask' -and $saved.AgentMode -eq 'app-server') 'App Server ask could not be saved.'
    Server $false
    UI @('invoke','NavOverview')|Out-Null
    UI @('invoke','SetupReloadSettings')|Out-Null
    UI @('invoke','NavAgentSettings')|Out-Null
    View 'AgentPermissionMode'
    Wait 'AgentPermissionMode' '操作ごとに確認（Codex App Server）'
    $checks.Add(@{name='Reload preserves a valid saved App Server approval mode';passed=$true})
    Server $false
    View 'AgentPermissionMode'
    Wait 'AgentPermissionMode' '読み取り・計画のみ'
    UI @('wait-for','AgentPermissionStatus','--value','読み取り専用に変更','--contains','-t','3000')|Out-Null
    UI @('screenshot','--focus','-o',(Join-Path $output 'read-only-fallback.png'))|Out-Null
    $saved=Save
    Assert ($saved.AgentPermissionMode -eq 'read-only' -and $saved.AgentMode -eq 'exec') 'Leaving App Server retained ask or broadened permissions.'
    $checks.Add(@{name='Leaving App Server falls back to read-only and saves';passed=$true})

    Server $true
    Permission '操作ごとに確認（Codex App Server）'
    Provider 'ProviderOptionClaude'
    View 'AgentPermissionMode'
    Wait 'AgentPermissionMode' '読み取り・計画のみ'
    $saved=Save
    Assert ($saved.AgentProvider -eq 'claude' -and $saved.AgentPermissionMode -eq 'read-only') 'Claude retained unsupported ask.'
    $checks.Add(@{name='Claude selection keeps a supported conservative permission mode';passed=$true})

    Provider 'ProviderOptionCodex'
    Permission '読み取り・計画のみ'
    View 'AgentExecutable'
    UI @('set-value','AgentExecutable',(Join-Path $dataPath 'unrecognized-cli.exe'))|Out-Null
    $before=(Get-FileHash (Join-Path $dataPath 'settings.json')).Hash
    UI @('invoke','SaveSettings')|Out-Null
    Wait 'Title' '設定を保存できませんでした'
    $status=UI @('get-value','Message')|ConvertFrom-Json
    Assert ($status.text -match 'Codex|codex') 'Missing corrective CLI guidance.'
    Assert ((Get-FileHash (Join-Path $dataPath 'settings.json')).Hash -eq $before) 'Rejected CLI settings overwrote saved settings.'
    UI @('screenshot','--focus','-o',(Join-Path $output 'corrective-guidance.png'))|Out-Null
    $checks.Add(@{name='Unrecognized Codex executable gets corrective guidance without changing saved settings';passed=$true})
} catch {
    $checks.Add(@{name='Provider permissions';passed=$false;detail=$_.Exception.Message})
} finally {
    @{checks=@($checks);passed=(@($checks|Where-Object{-not $_.passed}).Count -eq 0)}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'results.json')
    Write-Output "Provider permission results: $output"
}
if(@($checks|Where-Object{-not $_.passed}).Count){exit 1}
