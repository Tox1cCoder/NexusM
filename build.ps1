param([switch]$Test)
$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
$cache = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache'
$version = '1.0.4258.31'
$package = Join-Path $cache ('webview2.' + $version)
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (!(Test-Path -LiteralPath $package)) {
    $archive = Join-Path $cache ('webview2.' + $version + '.zip')
    if (!(Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -UseBasicParsing ('https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/' + $version + '/microsoft.web.webview2.' + $version + '.nupkg') -OutFile $archive
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $package
}
$output = Join-Path $workspace 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:5', '/codepage:65001', ('/out:' + (Join-Path $output 'NexusM.exe')), ('/win32manifest:' + (Join-Path $workspace 'src\app.manifest')))
foreach ($assembly in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.dll','System.Web.Extensions.dll','System.Security.dll','System.Xml.dll','Microsoft.VisualBasic.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) {
    $source = Join-Path $package ('lib\net462\' + $assembly)
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $assembly) -Force
    $arguments += '/reference:' + $source
}
Copy-Item -LiteralPath (Join-Path $package 'runtimes\win-x64\native\WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\NexusM.exe.config') -Destination $output -Force
Copy-Item -Path (Join-Path $workspace 'ui') -Destination $output -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $package 'LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $package 'LICENSE.txt') -Destination (Join-Path $output 'WebView2-LICENSE.txt') -Force
}
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
$size = (Get-ChildItem -LiteralPath $output -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ('Built dist\NexusM.exe. Portable app size: {0:N2} MB.' -f ($size / 1MB))
if ($Test) {
    $report = Join-Path $workspace 'artifacts\self-test.json'
    New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
    $process = Start-Process -FilePath (Join-Path $output 'NexusM.exe') -ArgumentList @('--self-test', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
    $process.WaitForExit(60000) | Out-Null
    if (!$process.HasExited) { throw 'Self-test timed out.' }
    if (!(Test-Path -LiteralPath $report)) { throw 'Self-test report was not written.' }
    Get-Content -LiteralPath $report
    if ($process.ExitCode -ne 0) { throw 'Self-test failed.' }
}
