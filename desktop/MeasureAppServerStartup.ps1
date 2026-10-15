param(
    [Parameter(Mandatory)][string]$Executable,
    [ValidateRange(1, 10)][int]$Samples = 3
)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$measurements = @()
for ($sample = 1; $sample -le $Samples; $sample++) {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executablePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    foreach ($argument in @('app-server', '--listen', 'stdio://')) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) { throw 'App Server did not start.' }
        $processStartMs = $watch.Elapsed.TotalMilliseconds
        $stderrDrain = $process.StandardError.ReadToEndAsync()
        # No thread/start or turn/start: this measures only process + protocol initialization.
        $process.StandardInput.WriteLine('{"id":1,"method":"initialize","params":{"clientInfo":{"name":"cab-startup-measure","version":"1.0.0"}}}')
        $process.StandardInput.Flush()
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
        $initialized = $false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $remaining = $deadline - [DateTimeOffset]::UtcNow
            if ($remaining -le [TimeSpan]::Zero) { break }
            $line = $process.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
            if ($null -eq $line) { throw 'App Server closed before initialization.' }
            try { $response = $line | ConvertFrom-Json } catch { throw 'App Server returned invalid protocol data.' }
            if ($response.id -eq 1) {
                if ($null -ne $response.error) { throw 'App Server initialization failed.' }
                $initialized = $true
                break
            }
        }
        if (-not $initialized) { throw 'App Server initialization timed out.' }
        $readyMs = $watch.Elapsed.TotalMilliseconds
        $process.StandardInput.WriteLine('{"method":"initialized"}')
        $process.StandardInput.Flush()
        $measurements += [pscustomobject]@{
            sample = $sample
            processStartMilliseconds = [math]::Round($processStartMs, 1)
            initializeMilliseconds = [math]::Round($readyMs - $processStartMs, 1)
            totalReadyMilliseconds = [math]::Round($readyMs, 1)
        }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(3000) }
        $process.Dispose()
    }
}
[pscustomobject]@{ inferenceRequests = 0; scope = 'native-process-and-initialize-only'; samples = $measurements } | ConvertTo-Json -Depth 4
