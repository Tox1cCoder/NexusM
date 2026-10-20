param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$distribution = Join-Path $workspace 'dist'
$output = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache\controller-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/main:NexusM.ControllerTests', ('/out:' + (Join-Path $output 'ControllerTests.exe')))
foreach ($assembly in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.dll','System.Web.Extensions.dll','System.Security.dll','System.Xml.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) {
    $source = Join-Path $distribution $assembly
    if (!(Test-Path -LiteralPath $source)) { throw 'Run build.ps1 first to create the app distribution.' }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $assembly) -Force
    $arguments += '/reference:' + $source
}
Copy-Item -LiteralPath (Join-Path $distribution 'WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\NexusM.exe.config') -Destination (Join-Path $output 'ControllerTests.exe.config') -Force
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $PSScriptRoot 'ControllerTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Controller/storage test compilation failed.' }
$report = Join-Path $workspace 'artifacts\controller-tests.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
& (Join-Path $output 'ControllerTests.exe') $report
if (!(Test-Path -LiteralPath $report)) { throw 'Controller/storage test report was not created.' }
Get-Content -LiteralPath $report
if ($LASTEXITCODE -ne 0) { throw 'Controller/storage checks failed.' }
