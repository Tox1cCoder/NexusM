param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$output = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache\queue-browser-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$runner = Join-Path $output 'QueueBrowserTests.exe'
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/main:NexusM.QueueBrowserTests', ('/out:' + $runner))
foreach ($assembly in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.dll','System.Web.Extensions.dll','System.Security.dll','System.Xml.dll','Microsoft.VisualBasic.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) {
    $source = Join-Path $workspace ('dist\' + $assembly)
    Copy-Item -LiteralPath $source -Destination $output -Force
    $arguments += '/reference:' + $source
}
Copy-Item -LiteralPath (Join-Path $workspace 'dist\WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\NexusM.exe.config') -Destination ($runner + '.config') -Force
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $PSScriptRoot 'QueueBrowserTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Queue browser test compilation failed.' }
$report = Join-Path $workspace 'artifacts\queue-browser-tests.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
& $runner $report
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $report; throw 'Queue browser checks failed.' }
Get-Content -LiteralPath $report
