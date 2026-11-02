param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'Invoke-TestProcess.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NexusM-TestProcessTests-' + [Guid]::NewGuid().ToString('N'))
$fixtureDirectory = Join-Path $testRoot 'fixture with spaces'
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runner = Join-Path $fixtureDirectory 'TestProcessFixture.exe'
$report = Join-Path $fixtureDirectory 'fresh report.json'
$suiteReport = Join-Path $workspace 'artifacts\test-process-tests.json'
$checks = New-Object 'Collections.Generic.List[object]'
$sentinel = $null
function Assert-TestProcess($condition, [string]$message) { if (!$condition) { throw $message } }
function Check-TestProcess([string]$name, [scriptblock]$action) {
    try { & $action; $checks.Add([pscustomobject]@{name = $name; passed = $true}); Write-Host ('PASS ' + $name) }
    catch { $checks.Add([pscustomobject]@{name = $name; passed = $false; error = $_.Exception.Message}); Write-Host ('FAIL ' + $name + ': ' + $_.Exception.Message) }
}
function Expect-TestProcessFailure([string]$mode, [string]$expected) {
    $failure = ''
    try { Invoke-NexusMTestProcess -FilePath $runner -ArgumentList @($mode, $report) -ReportPath $report -TimeoutSeconds 5 | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-TestProcess ($failure -like $expected) ('Expected ' + $mode + ' to fail with ' + $expected + '; got: ' + $failure)
}
try {
    & $compiler /nologo /target:exe /langversion:5 ('/out:' + $runner) /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'TestProcessFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test process fixture compilation failed.' }
    Check-TestProcess 'A fresh passing report and zero exit code complete successfully' {
        $argument = 'quoted "value" with a trailing slash\'
        $result = Invoke-NexusMTestProcess -FilePath $runner -ArgumentList @('success', $report, $argument) -ReportPath $report -TimeoutSeconds 5
        Assert-TestProcess ($result.passed -and $result.argument -ceq $argument) 'Raw argument quoting must preserve spaces, quotes, and trailing backslashes.'
    }
    Check-TestProcess 'A failing process cannot pass by writing a successful report' { Expect-TestProcessFailure 'nonzero' '*exited with code 7*' }
    Check-TestProcess 'A failing or unfinished report cannot pass with a zero exit code' {
        Expect-TestProcessFailure 'failed' '*did not confirm successful completion*'
        Expect-TestProcessFailure 'unfinished' '*did not confirm successful completion*'
    }
    Check-TestProcess 'Malformed and missing reports are rejected' {
        Expect-TestProcessFailure 'malformed' '*invalid JSON report*'
        Expect-TestProcessFailure 'missing' '*did not create a fresh report*'
    }
    Check-TestProcess 'A stale successful report is removed before the fixture starts' {
        [IO.File]::WriteAllText($report, '{"passed":true,"marker":"stale"}')
        $result = Invoke-NexusMTestProcess -FilePath $runner -ArgumentList @('success', $report) -ReportPath $report -TimeoutSeconds 5
        Assert-TestProcess $result.staleWasRemoved 'The new process must not observe the previous report.'
        [IO.File]::WriteAllText($report, '{"passed":true,"marker":"stale"}')
        Expect-TestProcessFailure 'missing' '*did not create a fresh report*'
    }
    Check-TestProcess 'A timeout surfaces the active stage and kills only the fixture process tree' {
        $script:timeoutFailure = ''
        $script:sentinel = Start-Process -FilePath $runner -ArgumentList 'child' -WindowStyle Hidden -PassThru
        $null = $sentinel.Handle
        $diagnostics = (& {
            try { Invoke-NexusMTestProcess -FilePath $runner -ArgumentList @('hang', $report) -ReportPath $report -TimeoutSeconds 3 | Out-Null }
            catch { $script:timeoutFailure = $_.Exception.Message }
        } 6>&1 | Out-String)
        Assert-TestProcess ($script:timeoutFailure -like '*timed out*') 'A hung process must fail within the configured outer deadline.'
        Assert-TestProcess ($diagnostics.Contains('hung-fixture') -and $diagnostics.Contains('stalled child')) 'Timeout diagnostics must include the latest report and its current check.'
        $hung = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        Assert-TestProcess ($null -eq (Get-Process -Id $hung.processId -ErrorAction SilentlyContinue)) 'The timed-out fixture must be terminated.'
        Assert-TestProcess ($null -eq (Get-Process -Id $hung.childProcessId -ErrorAction SilentlyContinue)) 'The timed-out fixture child must be terminated.'
        Assert-TestProcess (!$sentinel.HasExited) 'An unrelated process with the same executable name must remain running.'
        $sentinel.Kill(); $sentinel.WaitForExit(5000) | Out-Null; $sentinel.Dispose(); $script:sentinel = $null
    }
}
catch {
    $checks.Add([pscustomobject]@{name = 'Test process harness'; passed = $false; error = $_.Exception.Message})
    throw
}
finally {
    if ($null -ne $sentinel) { if (!$sentinel.HasExited) { $sentinel.Kill(); $sentinel.WaitForExit(5000) | Out-Null }; $sentinel.Dispose() }
    # A failed regression must not leave behind one of its own artificial hung children.
    if (Test-Path -LiteralPath $report -PathType Leaf) {
        try {
            $remaining = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            foreach ($fixtureProcessId in @($remaining.processId, $remaining.childProcessId)) {
                if (!$fixtureProcessId) { continue }
                $fixtureProcess = Get-Process -Id $fixtureProcessId -ErrorAction SilentlyContinue
                if ($null -ne $fixtureProcess -and $fixtureProcess.Path -eq $runner) { Stop-Process -Id $fixtureProcessId -Force }
            }
        } catch { }
    }
    New-Item -ItemType Directory -Path (Split-Path $suiteReport) -Force | Out-Null
    $failed = @($checks | Where-Object { !$_.passed }).Count
    [IO.File]::WriteAllText($suiteReport, ([pscustomobject]@{passed = $failed -eq 0; checks = $checks.ToArray()} | ConvertTo-Json -Depth 6))
    $absolute = [IO.Path]::GetFullPath($testRoot)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\NexusM-TestProcessTests-'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test process fixture cleanup path.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
if (@($checks | Where-Object { !$_.passed }).Count) { throw 'Test process launcher checks failed.' }
Write-Host ('Passed ' + $checks.Count + ' test process launcher checks.')
