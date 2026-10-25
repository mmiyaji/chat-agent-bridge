# Real-environment check for the switch auto-approval setting: toggle availability, warning dialog, cancel and accept.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'demo-lib.ps1')
& (Join-Path $PSScriptRoot 'stage.ps1')
function Toggle($id) { (Find-Cab $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern) }
function State($id) { (Toggle $id).Current.ToggleState }
function Shot($name) { Start-Sleep -Milliseconds 800; & (Join-Path $PSScriptRoot 'shot.ps1') $name | Out-Null }
# Auto-save asks to review sensitive changes; capture the dialog, then confirm it.
function Confirm-Review($name) {
    Start-Sleep 3
    try { $null = Find-Cab 'SensitiveSettingsReview' 1 } catch { }
    Shot $name
    try { Click-CabButtonByName '確認して保存' 8; "review confirmed ($name)" } catch { "no review dialog ($name)" }
    Start-Sleep 2
}
try {
    Invoke-Cab 'NavConnections'; Start-Sleep 2
    $toggle = Find-Cab 'AutoApproveSwitchCommands'
    "enabled with first: $($toggle.Current.IsEnabled)"
    # Choose automatic approval.
    $combo = Find-Cab 'ChatApprovalMode'
    $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep 1
    $auto = Find-Cab 'chatAgentBridgeDesktopMainPageComboBoxItem008'
    $auto.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
    try { $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
    Confirm-Review 'review-approval-mode'
    "enabled with auto: $((Find-Cab 'AutoApproveSwitchCommands').Current.IsEnabled)"
    # Turn on, then cancel the warning.
    $r = (Find-Cab 'AutoApproveSwitchCommands').Current.BoundingRectangle
    Move-Cursor ($r.X + 30) ($r.Y + $r.Height - 15)
    (Toggle 'AutoApproveSwitchCommands').Toggle(); Start-Sleep 1
    Shot 'switch-warning'
    Click-CabButtonByName 'キャンセル'; Start-Sleep 1
    "after cancel: $(State 'AutoApproveSwitchCommands')"
    # Turn on and accept.
    (Toggle 'AutoApproveSwitchCommands').Toggle(); Start-Sleep 1
    Click-CabButtonByName '理解して有効にする'
    Confirm-Review 'review-switch-approval'
    "after accept: $(State 'AutoApproveSwitchCommands')"
    Shot 'switch-accepted'
    # Leaving automatic approval turns the option off again.
    $combo = Find-Cab 'ChatApprovalMode'
    $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep 1
    (Find-Cab 'chatAgentBridgeDesktopMainPageComboBoxItem007').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
    try { $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
    Confirm-Review 'review-back-to-first'
    "after first: $(State 'AutoApproveSwitchCommands'), enabled: $((Find-Cab 'AutoApproveSwitchCommands').Current.IsEnabled)"
}
finally { & (Join-Path $PSScriptRoot 'stage.ps1') -Off }
