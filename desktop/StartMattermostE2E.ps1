[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serverUrl = 'http://127.0.0.1:8070'
$containerName = 'cab-winui-e2e'
$composeFile = Join-Path $PSScriptRoot 'docker-compose.mattermost-e2e.yml'
$stateFile = Join-Path $PSScriptRoot 'artifacts/mattermost-e2e.json'
$expectedDigest = 'mattermost/mattermost-preview@sha256:90319a856c49f0aef1000ab473ad76be3a8a97bef196424764638ee132eb61bc'

function Invoke-Docker([string[]] $Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed (exit $LASTEXITCODE)." }
}

function Test-ContainerExists {
    & docker inspect $containerName *> $null
    return $LASTEXITCODE -eq 0
}

function Assert-ContainerImage {
    $imageId = (& docker inspect --format '{{.Image}}' $containerName 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($imageId)) {
        throw 'Could not inspect the dedicated Mattermost container image.'
    }
    $digestsJson = & docker image inspect --format '{{json .RepoDigests}}' $imageId 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the Mattermost image digest.' }
    $digests = @($digestsJson | ConvertFrom-Json)
    if ($digests -notcontains $expectedDigest) {
        throw 'The existing dedicated container does not use the pinned E2E image. It was left unchanged.'
    }
}

function Wait-ForMattermost {
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "$serverUrl/api/v4/system/ping" -TimeoutSec 4
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Seconds 2
    }
    throw 'Mattermost did not become ready within 3 minutes.'
}

function Invoke-MmApi {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST')][string] $Method,
        [Parameter(Mandatory)][string] $Path,
        [string] $Token,
        [object] $Body,
        [int[]] $AllowStatus = @()
    )

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::$Method, "$serverUrl$Path")
    $response = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($Token)) {
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token)
        }
        if ($null -ne $Body) {
            $json = ConvertTo-Json -InputObject $Body -Depth 10 -Compress
            $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
        }
        $response = $script:httpClient.SendAsync($request).GetAwaiter().GetResult()
        $request.Dispose()
        $status = [int]$response.StatusCode
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode -and $status -notin $AllowStatus) {
            throw "Mattermost API request failed ($Method $Path, HTTP $status)."
        }
        $data = if ([string]::IsNullOrWhiteSpace($text)) { $null } else { $text | ConvertFrom-Json }
        return [pscustomobject]@{ Status = $status; Data = $data; Response = $response }
    } catch {
        if ($response) { $response.Dispose() }
        if ($request.Content) { $request.Content.Dispose() }
        $request.Dispose()
        throw
    }
}

function Get-LoginToken([string] $Username, [string] $Password) {
    $result = Invoke-MmApi -Method POST -Path '/api/v4/users/login' -Body @{ login_id = $Username; password = $Password } -AllowStatus @(401, 403)
    try {
        if (-not $result.Response.IsSuccessStatusCode) { return $null }
        $values = $null
        if ($result.Response.Headers.TryGetValues('token', [ref]$values)) { return [string]$values[0] }
        return $null
    } finally {
        $result.Response.Dispose()
    }
}

function Test-ExistingState {
    if (-not (Test-Path -LiteralPath $stateFile)) { return $null }
    try {
        $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        if ($state.serverUrl.TrimEnd('/') -ne $serverUrl -or
            [string]::IsNullOrWhiteSpace($state.team.id) -or
            [string]::IsNullOrWhiteSpace($state.channel.id) -or
            [string]::IsNullOrWhiteSpace($state.bridgeUser.token) -or
            [string]::IsNullOrWhiteSpace($state.memberUser.token)) { return $null }

        foreach ($user in @($state.bridgeUser, $state.memberUser)) {
            $me = Invoke-MmApi -Method GET -Path '/api/v4/users/me' -Token $user.token
            try {
                if ($me.Data.id -ne $user.id) { return $null }
            } finally { $me.Response.Dispose() }
        }
        $channel = Invoke-MmApi -Method GET -Path "/api/v4/channels/$($state.channel.id)" -Token $state.bridgeUser.token
        try {
            if ($channel.Data.team_id -ne $state.team.id) { return $null }
        } finally { $channel.Response.Dispose() }
        return $state
    } catch {
        return $null
    }
}

function New-RandomPassword {
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', 'a').Replace('/', 'b')
}

function New-UniqueSuffix {
    return [Guid]::NewGuid().ToString('N').Substring(0, 10)
}

function Ensure-User([string] $Username, [string] $Email, [string] $Password) {
    $create = Invoke-MmApi -Method POST -Path '/api/v4/users' -Body @{
        username = $Username
        email = $Email
        password = $Password
    }
    $create.Response.Dispose()
    $token = Get-LoginToken -Username $Username -Password $Password
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Mattermost user login did not return an authentication token.' }
    $me = Invoke-MmApi -Method GET -Path '/api/v4/users/me' -Token $token
    try {
        if ([string]::IsNullOrWhiteSpace($me.Data.id)) { throw 'Mattermost user identity could not be resolved.' }
        return [pscustomobject]@{ Id = $me.Data.id; Username = $me.Data.username; Password = $Password; Token = $token }
    } finally { $me.Response.Dispose() }
}

function Add-TeamMember([string] $Token, [string] $TeamId, [string] $UserId) {
    $result = Invoke-MmApi -Method POST -Path "/api/v4/teams/$TeamId/members" -Token $Token -Body @{ team_id = $TeamId; user_id = $UserId }
    $result.Response.Dispose()
}

function Add-ChannelMember([string] $Token, [string] $ChannelId, [string] $UserId) {
    $result = Invoke-MmApi -Method POST -Path "/api/v4/channels/$ChannelId/members" -Token $Token -Body @{ channel_id = $ChannelId; user_id = $UserId }
    $result.Response.Dispose()
}

function New-E2EState {
    $suffix = New-UniqueSuffix
    $bridgeName = "cab-e2e-$suffix-bridge"
    $memberName = "cab-e2e-$suffix-member"
    $bridgePassword = New-RandomPassword
    $memberPassword = New-RandomPassword
    $bridge = Ensure-User -Username $bridgeName -Email "$bridgeName@example.test" -Password $bridgePassword
    $team = Invoke-MmApi -Method POST -Path '/api/v4/teams' -Token $bridge.Token -Body @{
        name = "cab-e2e-$suffix"
        display_name = "Chat Agent Bridge E2E $suffix"
        type = 'O'
    }
    try { $teamData = $team.Data } finally { $team.Response.Dispose() }
    if ([string]::IsNullOrWhiteSpace($teamData.id)) { throw 'Mattermost team creation returned no identity.' }
    Add-TeamMember -Token $bridge.Token -TeamId $teamData.id -UserId $bridge.Id
    $channel = Invoke-MmApi -Method POST -Path '/api/v4/channels' -Token $bridge.Token -Body @{
        team_id = $teamData.id
        name = "cab-e2e-$suffix"
        display_name = "Bridge E2E $suffix"
        type = 'O'
    }
    try { $channelData = $channel.Data } finally { $channel.Response.Dispose() }
    if ([string]::IsNullOrWhiteSpace($channelData.id)) { throw 'Mattermost channel creation returned no identity.' }
    Add-ChannelMember -Token $bridge.Token -ChannelId $channelData.id -UserId $bridge.Id
    $member = Ensure-User -Username $memberName -Email "$memberName@example.test" -Password $memberPassword
    Add-TeamMember -Token $bridge.Token -TeamId $teamData.id -UserId $member.Id
    Add-ChannelMember -Token $bridge.Token -ChannelId $channelData.id -UserId $member.Id

    return [pscustomobject]@{
        serverUrl = $serverUrl
        team = @{ id = $teamData.id; name = $teamData.name }
        channel = @{ id = $channelData.id; name = $channelData.name }
        bridgeUser = @{ id = $bridge.Id; username = $bridge.Username; password = $bridge.Password; token = $bridge.Token }
        memberUser = @{ id = $member.Id; username = $member.Username; password = $member.Password; token = $member.Token }
    }
}

$script:httpClient = [System.Net.Http.HttpClient]::new()
$script:httpClient.Timeout = [TimeSpan]::FromSeconds(15)
try {
    if (Test-ContainerExists) {
        Assert-ContainerImage
        $status = (& docker inspect --format '{{.State.Status}}' $containerName 2>$null).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the dedicated Mattermost container state.' }
        if ($status -ne 'running') {
            Invoke-Docker @('start', $containerName)
        }
    } else {
        Invoke-Docker @('compose', '-p', 'mattermost-e2e', '-f', $composeFile, 'up', '-d')
    }

    Wait-ForMattermost
    $state = Test-ExistingState
    if ($null -eq $state) {
        $state = New-E2EState
        $directory = Split-Path -Parent $stateFile
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $temporaryFile = "$stateFile.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            $json = ConvertTo-Json -InputObject $state -Depth 8
            [System.IO.File]::WriteAllText($temporaryFile, $json, [System.Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temporaryFile -Destination $stateFile -Force
        } finally {
            if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
        }
    }
    Write-Output "Mattermost E2E is ready at $serverUrl. State saved in ignored file: $stateFile"
} finally {
    $script:httpClient.Dispose()
}
