# Minimal Chrome DevTools Protocol helper for the isolated demo browser (port 9333).
$script:cdpId = 0
function Connect-Cdp([string]$UrlPart = '8070') {
    $targets = Invoke-RestMethod 'http://127.0.0.1:9333/json'
    $page = $targets | Where-Object { $_.type -eq 'page' -and $_.url -like "*$UrlPart*" } | Select-Object -First 1
    if (-not $page) { throw 'Demo browser page not found on port 9333.' }
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $null = $ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    return $ws
}
function Invoke-Cdp($ws, [string]$Method, $Params = @{}) {
    $script:cdpId++
    $id = $script:cdpId
    $json = @{ id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $null = $ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $buffer = [byte[]]::new(1048576)
    while ($true) {
        $sb = [Text.StringBuilder]::new()
        do {
            $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            [void]$sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
        } while (-not $r.EndOfMessage)
        $msg = $sb.ToString() | ConvertFrom-Json
        if ($msg.id -eq $id) { return $msg }
    }
}
function Invoke-Js($ws, [string]$Expression) {
    $r = Invoke-Cdp $ws 'Runtime.evaluate' @{ expression = $Expression; awaitPromise = $true; returnByValue = $true }
    return $r.result.result.value
}
