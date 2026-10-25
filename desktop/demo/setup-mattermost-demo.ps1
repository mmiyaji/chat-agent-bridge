# Creates a friendly-named demo team, channel and users on the isolated local Mattermost (127.0.0.1:8070).
# Passwords are generated here and saved only in the ignored artifacts folder; nothing is printed.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'demo-env.ps1')
$server = 'http://127.0.0.1:8070'
$statePath = $MattermostState
$http = [System.Net.Http.HttpClient]::new()

function Api([string]$Method, [string]$Path, [string]$Token, $Body = $null, [int[]]$Allow = @()) {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::$Method, "$server$Path")
    if ($Token) { $req.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token) }
    if ($null -ne $Body) { $req.Content = [System.Net.Http.StringContent]::new((ConvertTo-Json $Body -Depth 8 -Compress), [Text.Encoding]::UTF8, 'application/json') }
    $res = $http.SendAsync($req).GetAwaiter().GetResult()
    $text = $res.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $res.IsSuccessStatusCode -and [int]$res.StatusCode -notin $Allow) { throw "Mattermost $Method $Path failed: HTTP $([int]$res.StatusCode) $text" }
    $token = $null; $values = $null
    if ($res.Headers.TryGetValues('token', [ref]$values)) { $token = [string]$values[0] }
    [pscustomobject]@{ Status = [int]$res.StatusCode; Data = $(if ($text) { $text | ConvertFrom-Json } else { $null }); Token = $token }
}
function NewPassword { [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)).Replace('+','a').Replace('/','b').TrimEnd('=') + 'A1!' }
function EnsureUser($name, $display, $state) {
    if ($state.$name) {
        $login = Api POST '/api/v4/users/login' $null @{ login_id = $name; password = $state.$name.password } @(401)
        if ($login.Token) { return [pscustomobject]@{ id = $login.Data.id; username = $name; password = $state.$name.password; token = $login.Token } }
    }
    $password = NewPassword
    $created = Api POST '/api/v4/users' $null @{ username = $name; email = "$name@example.test"; password = $password; first_name = $display; nickname = $display } @(400)
    if ($created.Status -eq 400) {
        # The user exists from an interrupted run; the local E2E admin resets its password.
        $e2e = Get-Content $E2EState -Raw | ConvertFrom-Json
        $existing = Api GET "/api/v4/users/username/$name" $e2e.bridgeUser.token
        $null = Api PUT "/api/v4/users/$($existing.Data.id)/password" $e2e.bridgeUser.token @{ new_password = $password }
    }
    $login = Api POST '/api/v4/users/login' $null @{ login_id = $name; password = $password }
    [pscustomobject]@{ id = $login.Data.id; username = $name; password = $password; token = $login.Token }
}

$state = if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$alice = EnsureUser 'alice' 'Alice' $state
$bot = EnsureUser 'cab-bot' 'CAB Bot' $state

$team = Api GET '/api/v4/teams/name/demo' $alice.token $null @(404)
if ($team.Status -eq 404) { $team = Api POST '/api/v4/teams' $alice.token @{ name = 'demo'; display_name = 'Demo'; type = 'O' } }
$teamId = $team.Data.id
# Alice created the team and is its admin; she adds the bot.
$null = Api POST "/api/v4/teams/$teamId/members" $alice.token @{ team_id = $teamId; user_id = $bot.id }
$channel = Api GET "/api/v4/teams/$teamId/channels/name/ai-requests" $alice.token $null @(404)
if ($channel.Status -eq 404) { $channel = Api POST '/api/v4/channels' $alice.token @{ team_id = $teamId; name = 'ai-requests'; display_name = 'ai-requests'; type = 'O'; purpose = 'Chat Agent Bridge デモ' } }
$channelId = $channel.Data.id
$null = Api POST "/api/v4/channels/$channelId/members" $alice.token @{ channel_id = $channelId; user_id = $bot.id }

$out = [pscustomobject]@{
    serverUrl = $server; teamId = $teamId; channelId = $channelId
    alice = [pscustomobject]@{ id = $alice.id; password = $alice.password }
    'cab-bot' = [pscustomobject]@{ id = $bot.id; password = $bot.password; token = $bot.token }
}
[IO.File]::WriteAllText($statePath, ($out | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
"demo ready: team=demo channel=ai-requests users=alice,cab-bot (credentials in ignored mattermost-demo.json)"
