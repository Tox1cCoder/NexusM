param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$distribution = Join-Path $workspace 'dist'
if (!(Test-Path -LiteralPath (Join-Path $distribution 'Microsoft.Web.WebView2.Core.dll'))) { throw 'Run build.ps1 first to create the app distribution.' }
$output = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache\history-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/langversion:5', ('/out:' + (Join-Path $output 'HistoryImportTests.exe')))
foreach ($assembly in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) {
    $source = Join-Path $distribution $assembly
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $assembly) -Force
    $arguments += '/reference:' + $source
}
Copy-Item -LiteralPath (Join-Path $distribution 'WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\NexusM.exe.config') -Destination (Join-Path $output 'HistoryImportTests.exe.config') -Force
$arguments += Join-Path $workspace 'src\BrowserImport.cs'
$arguments += Join-Path $PSScriptRoot 'HistoryImportTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'History test compilation failed.' }
$report = Join-Path $workspace 'artifacts\history-import-tests.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
$process = Start-Process -FilePath (Join-Path $output 'HistoryImportTests.exe') -ArgumentList @('"' + $report + '"') -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(60000)) { throw 'History import fixture checks timed out.' }
if (!(Test-Path -LiteralPath $report)) { throw 'History import test report was not created.' }
Get-Content -LiteralPath $report
if ($process.ExitCode -ne 0) { throw 'History import fixture checks failed.' }
