param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$output = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache\folder-cleanup-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$runner = Join-Path $output 'FolderCleanupTests.exe'
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/main:NexusM.FolderCleanupTests', ('/out:' + $runner))
foreach ($assembly in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'Microsoft.VisualBasic.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
$arguments += Join-Path $workspace 'src\DownloadFolderCleanup.cs'
$arguments += Join-Path $PSScriptRoot 'FolderCleanupTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Folder cleanup test compilation failed.' }
$report = Join-Path $workspace 'artifacts\folder-cleanup-tests.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
& $runner $report
if (!(Test-Path -LiteralPath $report)) { throw 'Folder cleanup report was not created.' }
Get-Content -LiteralPath $report
if ($LASTEXITCODE -ne 0) { throw 'Folder cleanup tests failed.' }
