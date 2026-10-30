param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$output = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache\queue-restart-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$runner = Join-Path $output 'QueueRestartTests.exe'
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/main:NexusM.QueueRestartTests', ('/out:' + $runner))
foreach ($assembly in @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Web.dll', 'System.Web.Extensions.dll', 'System.Security.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($source in @('Models.cs', 'NexusApi.cs', 'DownloadService.cs', 'Storage.cs')) {
    $arguments += Join-Path $workspace ('src\' + $source)
}
$arguments += Join-Path $PSScriptRoot 'QueueRestartTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Forced restart fixture compilation failed.' }
Copy-Item -LiteralPath (Join-Path $workspace 'src\NexusM.exe.config') -Destination ($runner + '.config') -Force
$report = Join-Path $workspace 'artifacts\queue-restart-tests.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
& $runner $report
if (!(Test-Path -LiteralPath $report)) { throw 'Forced restart fixture report was not created.' }
Get-Content -LiteralPath $report
if ($LASTEXITCODE -ne 0) { throw 'Forced restart fixture checks failed.' }
