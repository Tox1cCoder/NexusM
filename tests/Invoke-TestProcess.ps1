# Shared launcher for isolated Windows fixtures. ArgumentList contains raw, unquoted arguments.
function Invoke-NexusMTestProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 120
    )
    $fixturePath = [IO.Path]::GetFullPath($FilePath)
    $fixtureReportPath = [IO.Path]::GetFullPath($ReportPath)
    if (!(Test-Path -LiteralPath $fixturePath -PathType Leaf)) { throw ('Fixture executable is missing: ' + $fixturePath) }
    if (Test-Path -LiteralPath $fixtureReportPath -PathType Container) { throw 'The fixture report path must identify a file.' }
    New-Item -ItemType Directory -Path (Split-Path $fixtureReportPath) -Force | Out-Null
    if (Test-Path -LiteralPath $fixtureReportPath) { Remove-Item -LiteralPath $fixtureReportPath -Force }
    # Start-Process joins ArgumentList into a Windows command line. Preserve spaces,
    # quotes, and backslashes rather than requiring every caller to quote its paths.
    $quotedArguments = @($ArgumentList | ForEach-Object {
        $escaped = [regex]::Replace([string]$_, '(\\*)"', '$1$1\"')
        $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
        '"' + $escaped + '"'
    })
    $fixture = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lastProgress = ''
    $lastOutput = 0.0
    $readReport = {
        # The fixture updates its report while running. Permit concurrent reads and
        # writes; incomplete snapshots are retried until the process exits.
        $stream = [IO.File]::Open($fixtureReportPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            $reader = New-Object System.IO.StreamReader -ArgumentList $stream
            try { $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    try {
        $start = @{ FilePath = $fixturePath; WindowStyle = 'Hidden'; PassThru = $true }
        if ($quotedArguments.Count) { $start.ArgumentList = $quotedArguments }
        $fixture = Start-Process @start
        # Keep the process handle open until cleanup; it also makes ExitCode reliable
        # after the short WaitForExit polling calls on Windows PowerShell 5.1.
        $null = $fixture.Handle
        while (!$fixture.WaitForExit(250)) {
            $progress = $null
            if (Test-Path -LiteralPath $fixtureReportPath -PathType Leaf) {
                try { $progress = & $readReport | ConvertFrom-Json -ErrorAction Stop } catch { }
            }
            $phase = if ($null -ne $progress -and $progress.phase) { [string]$progress.phase } else { 'waiting for fixture progress' }
            $currentCheck = if ($null -ne $progress -and $progress.currentCheck) { ' / ' + $progress.currentCheck } else { '' }
            $signature = $phase + $currentCheck
            if ($signature -ne $lastProgress -or $watch.Elapsed.TotalSeconds - $lastOutput -ge 10) {
                $runtime = if ($null -ne $progress -and $progress.runtimeVersion) { ' [WebView2 ' + $progress.runtimeVersion + ']' } else { '' }
                Write-Host ('[{0:N1}s] {1}: {2}{3}' -f $watch.Elapsed.TotalSeconds, (Split-Path $fixturePath -Leaf), $signature, $runtime)
                $lastProgress = $signature
                $lastOutput = $watch.Elapsed.TotalSeconds
            }
            if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw ('Fixture process timed out after {0}s during {1}.' -f $TimeoutSeconds, $signature) }
        }
        $fixture.WaitForExit()
        if ($fixture.ExitCode -ne 0) { throw ('Fixture process exited with code ' + $fixture.ExitCode + '.') }
        if (!(Test-Path -LiteralPath $fixtureReportPath -PathType Leaf)) { throw 'Fixture process did not create a fresh report.' }
        try { $result = & $readReport | ConvertFrom-Json -ErrorAction Stop }
        catch { throw ('Fixture process wrote an invalid JSON report: ' + $_.Exception.Message) }
        if ($null -eq $result -or $result.passed -isnot [bool] -or !$result.passed -or $result.running -eq $true) { throw 'Fixture report did not confirm successful completion.' }
        return $result
    }
    catch {
        Write-Host ('Fixture failure: ' + $_.Exception.Message)
        if (Test-Path -LiteralPath $fixtureReportPath -PathType Leaf) {
            Write-Host ('Current fixture report (' + $fixtureReportPath + '):')
            try { Write-Host (& $readReport) } catch { Write-Host ('Could not read the current report: ' + $_.Exception.Message) }
        } else { Write-Host 'No fixture report was written.' }
        throw
    }
    finally {
        $watch.Stop()
        if ($null -ne $fixture) {
            try {
                if (!$fixture.HasExited) {
                    # Target only this still-running fixture and its descendants.
                    # Never kill by executable name: a developer may be running the app.
                    # Launch taskkill separately so an already-exited race cannot turn
                    # native stderr into a terminating PowerShell error during cleanup.
                    $treeKiller = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\taskkill.exe') -ArgumentList @('/PID', [string]$fixture.Id, '/T', '/F') -WindowStyle Hidden -PassThru
                    try {
                        $null = $treeKiller.Handle
                        if (!$treeKiller.WaitForExit(5000)) { $treeKiller.Kill(); $treeKiller.WaitForExit(5000) | Out-Null }
                    }
                    finally { $treeKiller.Dispose() }
                    if (!$fixture.WaitForExit(5000)) {
                        try { $fixture.Kill() } catch { if (!$fixture.HasExited) { throw } }
                        if (!$fixture.WaitForExit(5000)) { throw ('Could not terminate fixture process ' + $fixture.Id + '.') }
                    }
                }
            }
            finally { $fixture.Dispose() }
        }
    }
}
