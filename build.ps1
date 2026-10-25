param(
    [switch]$Test,
    [string]$AppVersion = '',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
. (Join-Path $workspace 'tools\Build-Version.ps1')
$sourceAssemblyInfo = Join-Path $workspace 'src\AssemblyInfo.cs'
$appVersionInfo = if ($AppVersion) { Get-NexusMVersion $AppVersion } else { Get-NexusMBuildVersion $sourceAssemblyInfo }
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
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:5', '/codepage:65001', ('/out:' + (Join-Path $output 'NexusM.exe')), ('/win32manifest:' + (Join-Path $workspace 'src\app.manifest')))
$arguments += '/win32icon:' + (Join-Path $workspace 'src\app.ico')
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
$builtIndex = Join-Path $output 'ui\index.html'
$indexContent = [IO.File]::ReadAllText($builtIndex)
if ([regex]::Matches($indexContent, '<span class="version">[^<]+</span>').Count -ne 1) { throw 'The Settings version label is missing or ambiguous.' }
$indexContent = [regex]::Replace($indexContent, '<span class="version">[^<]+</span>', ('<span class="version">' + $appVersionInfo.Version + '</span>'))
[IO.File]::WriteAllText($builtIndex, $indexContent, (New-Object Text.UTF8Encoding($false)))
if (Test-Path -LiteralPath (Join-Path $package 'LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $package 'LICENSE.txt') -Destination (Join-Path $output 'WebView2-LICENSE.txt') -Force
}
$generatedAssemblyInfo = Join-Path $cache ('AssemblyInfo-' + [Guid]::NewGuid().ToString('N') + '.cs')
try {
    $assemblyContent = [IO.File]::ReadAllText($sourceAssemblyInfo)
    $assemblyContent = [regex]::Replace($assemblyContent, '(\[assembly:\s*Assembly(?:File)?Version\(")[^"]+("\)\])', ('${1}' + $appVersionInfo.AssemblyVersion + '${2}'))
    [IO.File]::WriteAllText($generatedAssemblyInfo, $assemblyContent, (New-Object Text.UTF8Encoding($false)))
    $arguments += @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Filter '*.cs' | Where-Object { $_.Name -ne 'AssemblyInfo.cs' } | ForEach-Object { $_.FullName })
    $arguments += $generatedAssemblyInfo
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
}
finally { if (Test-Path -LiteralPath $generatedAssemblyInfo) { Remove-Item -LiteralPath $generatedAssemblyInfo -Force } }
$size = (Get-ChildItem -LiteralPath $output -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ('Built NexusM {0} at {1}. Portable app size: {2:N2} MB.' -f $appVersionInfo.Version, (Join-Path $output 'NexusM.exe'), ($size / 1MB))
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
