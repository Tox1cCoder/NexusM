param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$output = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache\ui-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$runner = Join-Path $output 'UiTests.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/langversion:5', '/main:NexusM.UiTests', ('/out:' + $runner))
foreach ($assembly in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Security.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll')) {
    $source = Join-Path $workspace ('dist\' + $assembly)
    if (!(Test-Path -LiteralPath $source)) { throw 'Run build.ps1 first to create the app distribution.' }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $assembly) -Force
    $arguments += '/reference:' + $source
}
Copy-Item -LiteralPath (Join-Path $workspace 'dist\WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\NexusM.exe.config') -Destination ($runner + '.config') -Force
$arguments += Join-Path $workspace 'src\Storage.cs'
$arguments += Join-Path $PSScriptRoot 'UiTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'UI fixture compilation failed.' }
$report = Join-Path $workspace 'artifacts\ui-tests.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
$process = Start-Process -FilePath $runner -ArgumentList @('"' + $workspace + '"', '"' + $report + '"') -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(60000)) { throw 'UI fixture checks timed out.' }
if (!(Test-Path -LiteralPath $report)) { throw 'UI fixture report was not created.' }
Get-Content -LiteralPath $report
if ($process.ExitCode -ne 0) { throw 'UI fixture checks failed.' }
