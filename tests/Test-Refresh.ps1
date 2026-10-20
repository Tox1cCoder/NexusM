param([switch]$Live)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$distribution = Join-Path $workspace 'dist'
$output = Join-Path $env:LOCALAPPDATA 'NexusM-BuildCache\refresh-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$executable = Join-Path $output 'RefreshTests.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/langversion:5', '/main:NexusM.RefreshTests', ('/out:' + $executable))
foreach ($assembly in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Web.dll','System.Web.Extensions.dll','System.Security.dll','System.Xml.dll','Microsoft.VisualBasic.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) {
    $source = Join-Path $distribution $assembly
    if (!(Test-Path -LiteralPath $source)) { throw 'Run build.ps1 first to create the app distribution.' }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $assembly) -Force
    $arguments += '/reference:' + $source
}
Copy-Item -LiteralPath (Join-Path $distribution 'WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $workspace 'src\NexusM.exe.config') -Destination ($executable + '.config') -Force
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $PSScriptRoot 'RefreshTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Refresh fixture compilation failed.' }
$report = Join-Path $workspace 'artifacts\refresh-tests.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }
$process = $null
try {
    $processArguments = @('"' + $report + '"')
    if ($Live) { $processArguments += '--live' }
    $process = Start-Process -FilePath $executable -ArgumentList $processArguments -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($(if ($Live) { 180 } else { 45 }))
    while (!$process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Refresh fixture checks timed out.' }
    }
    if (!(Test-Path -LiteralPath $report)) { throw 'Refresh fixture report was not created.' }
    Get-Content -LiteralPath $report
    if ($process.ExitCode -ne 0) { throw 'Refresh fixture checks failed.' }
}
finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}
