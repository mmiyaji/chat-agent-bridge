[CmdletBinding()]
param()

# Adds isolated actors and a second channel to the ignored Mattermost E2E fixture.
# Credentials stay in desktop/artifacts/mattermost-e2e.json and are never printed.
$ErrorActionPreference = 'Stop'
$statePath = Join-Path $PSScriptRoot 'artifacts/mattermost-e2e.json'
if (-not (Test-Path -LiteralPath $statePath)) { throw 'Mattermost E2E fixture is missing.' }
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ($state.serverUrl -notmatch '^http://(127\.0\.0\.1|localhost):8070/?$') {
    throw 'Workflow E2E is restricted to the isolated localhost:8070 fixture.'
}
$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(15)

function Invoke-MmApi([string]$Method, [string]$Path, [string]$Token, $Body = $null) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::$Method, "$($state.serverUrl)$Path")
    $response = $null
    try {
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token)
        if ($null -ne $Body) {
            $json = ConvertTo-Json -InputObject $Body -Depth 8 -Compress
            $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
        }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "Mattermost E2E setup failed (HTTP $([int]$response.StatusCode))." }
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return $text | ConvertFrom-Json
    } catch {
        if ($_.Exception.Message -like 'Mattermost E2E setup failed*') { throw }
        throw 'Mattermost E2E setup request failed.'
    } finally {
        if ($response) { $response.Dispose() }
        if ($request.Content) { $request.Content.Dispose() }
        $request.Dispose()
    }
}

try {
    $script:state = $state
    $thirdUserReady = $false
    if ($state.unauthorizedUser -and $state.unauthorizedUser.token -and $state.unauthorizedUser.id) {
        try {
            $me = Invoke-MmApi 'GET' '/api/v4/users/me' $state.unauthorizedUser.token
            $thirdUserReady = $me.id -eq $state.unauthorizedUser.id
        } catch { $thirdUserReady = $false }
    }
    if (-not $thirdUserReady) {
        $suffix = [guid]::NewGuid().ToString('N').Substring(0, 10)
        $username = "cab-e2e-$suffix-outsider"
        $passwordBytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
        $password = [Convert]::ToBase64String($passwordBytes).TrimEnd('=').Replace('+', 'a').Replace('/', 'b')
        $null = Invoke-MmApi 'POST' '/api/v4/users' $state.bridgeUser.token @{
            username = $username
            email = "$username@example.test"
            password = $password
        }
        # Login response carries no bearer token in the JSON body; retrieve the token header.
        $login = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$($state.serverUrl)/api/v4/users/login")
        $login.Content = [System.Net.Http.StringContent]::new((ConvertTo-Json -Compress @{ login_id = $username; password = $password }), [System.Text.Encoding]::UTF8, 'application/json')
        $loginResponse = $http.SendAsync($login).GetAwaiter().GetResult()
        try {
            if (-not $loginResponse.IsSuccessStatusCode) { throw 'Mattermost E2E setup login failed.' }
            $values = $null
            if (-not $loginResponse.Headers.TryGetValues('token', [ref]$values)) { throw 'Mattermost E2E setup login returned no token.' }
            $token = [string]$values[0]
        } finally {
            $loginResponse.Dispose(); $login.Dispose()
        }
        $me = Invoke-MmApi 'GET' '/api/v4/users/me' $token
        $state | Add-Member -Force -NotePropertyName unauthorizedUser -NotePropertyValue @{ id = $me.id; username = $me.username; password = $password; token = $token }
        $null = Invoke-MmApi 'POST' "/api/v4/teams/$($state.team.id)/members" $state.bridgeUser.token @{ team_id = $state.team.id; user_id = $me.id }
        $null = Invoke-MmApi 'POST' "/api/v4/channels/$($state.channel.id)/members" $state.bridgeUser.token @{ channel_id = $state.channel.id; user_id = $me.id }
    }

    $otherChannelReady = $false
    if ($state.otherChannel -and $state.otherChannel.id) {
        try {
            $channel = Invoke-MmApi 'GET' "/api/v4/channels/$($state.otherChannel.id)" $state.memberUser.token
            $otherChannelReady = $channel.team_id -eq $state.team.id
        } catch { $otherChannelReady = $false }
    }
    if (-not $otherChannelReady) {
        $suffix = [guid]::NewGuid().ToString('N').Substring(0, 10)
        $channel = Invoke-MmApi 'POST' '/api/v4/channels' $state.bridgeUser.token @{
            team_id = $state.team.id
            name = "cab-e2e-$suffix-other"
            display_name = "CAB Workflow E2E Other $suffix"
            type = 'O'
        }
        $null = Invoke-MmApi 'POST' "/api/v4/channels/$($channel.id)/members" $state.bridgeUser.token @{ channel_id = $channel.id; user_id = $state.memberUser.id }
        $null = Invoke-MmApi 'POST' "/api/v4/channels/$($channel.id)/members" $state.bridgeUser.token @{ channel_id = $channel.id; user_id = $state.unauthorizedUser.id }
        $state | Add-Member -Force -NotePropertyName otherChannel -NotePropertyValue @{ id = $channel.id; name = $channel.name }
    }

    $temporaryPath = "$statePath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = ConvertTo-Json -InputObject $state -Depth 8
        [System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $statePath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
    Write-Output 'Mattermost workflow fixture is ready (three users, two channels). Credentials remain in the ignored state file.'
} finally { $http.Dispose() }
