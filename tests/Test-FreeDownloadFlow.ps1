param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$distribution = Join-Path $workspace 'dist'
if (!(Test-Path -LiteralPath (Join-Path $distribution 'Microsoft.Web.WebView2.Core.dll'))) { throw 'Run build.ps1 first to create the app distribution.' }
$output = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache\free-download-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$obsoleteProbe = Join-Path $output 'FreeDownloadProbe.exe'
if (Test-Path -LiteralPath $obsoleteProbe) {
    & $compiler /nologo /target:winexe /platform:x64 /codepage:65001 ('/out:' + $obsoleteProbe) (Join-Path $PSScriptRoot 'RetiredDownloadProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not retire the old diagnostic probe. Close its window and try again.' }
}
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/langversion:5', '/codepage:65001', ('/out:' + (Join-Path $output 'FreeDownloadFlowTests.exe')))
foreach ($assembly in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) {
    $source = Join-Path $distribution $assembly
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $assembly) -Force
    $arguments += '/reference:' + $source
}
Copy-Item -LiteralPath (Join-Path $distribution 'WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\NexusM.exe.config') -Destination (Join-Path $output 'FreeDownloadFlowTests.exe.config') -Force
$arguments += Join-Path $workspace 'src\FreeDownloadFlow.cs'
$arguments += Join-Path $PSScriptRoot 'FreeDownloadFlowTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Free-download test compilation failed.' }
$report = Join-Path $workspace 'artifacts\free-download-tests.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
$process = $null
try {
    $process = Start-Process -FilePath (Join-Path $output 'FreeDownloadFlowTests.exe') -ArgumentList @('"' + $report + '"') -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(45000)) { throw 'Free-download fixture checks timed out.' }
    if (!(Test-Path -LiteralPath $report)) { throw 'Free-download test report was not created.' }
    Get-Content -LiteralPath $report
    if ($process.ExitCode -ne 0) { throw 'Free-download fixture checks failed.' }
}
finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}
