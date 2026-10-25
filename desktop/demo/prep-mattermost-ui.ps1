# Switches alice's Mattermost UI to Japanese, shows thread replies inline, and closes the notification banner.
param([ValidateSet('ja', 'en')][string]$Lang = 'ja')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'demo-env.ps1')
. (Join-Path $PSScriptRoot 'cdp.ps1')
$mm = Get-Content $MattermostState -Raw | ConvertFrom-Json
$ws = Connect-Cdp
$uid = $mm.alice.id
$js = @"
(async () => {
  const csrf = (document.cookie.match(/MMCSRF=([^;]+)/) || [])[1] || '';
  const h = {'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest', 'X-CSRF-Token': csrf};
  const a = await fetch('/api/v4/users/me/patch', {method: 'PUT', headers: h, body: JSON.stringify({locale: '$Lang'})});
  const b = await fetch('/api/v4/users/me/preferences', {method: 'PUT', headers: h, body: JSON.stringify([
    {user_id: '$uid', category: 'display_settings', name: 'collapsed_reply_threads', value: 'off'},
    {user_id: '$uid', category: 'display_settings', name: 'message_display', value: 'clean'}])});
  return a.status + '/' + b.status;
})()
"@
"patch: $(Invoke-Js $ws $js)"
$null = Invoke-Cdp $ws 'Page.reload' @{}
Start-Sleep 6
$closed = Invoke-Js $ws "(() => { const b = document.querySelector('.announcement-bar__close, .announcement-bar .close, [class*=announcement] button[aria-label]'); if (b) { b.click(); return 'closed'; } return 'none'; })()"
"banner: $closed"
