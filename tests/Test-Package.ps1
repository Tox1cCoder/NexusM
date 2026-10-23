param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NexusM-PackageTests-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $testRoot 'dist'
$packageScript = Join-Path $workspace 'tools\Package-Release.ps1'
$payload = @('NexusM.exe', 'NexusM.exe.config', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll', 'WebView2-LICENSE.txt', 'ui\index.html', 'ui\app.js', 'ui\app.css', 'ui\assets\app-icon.svg')
$checks = 0
function Assert-Package($condition, [string]$message) {
    if (!$condition) { throw $message }
    $script:checks++
    Write-Host ('PASS ' + $message)
}
function Assert-Rejected([scriptblock]$action, [string]$message) {
    $rejected = $false
    try { & $action | Out-Null } catch { $rejected = $true }
    Assert-Package $rejected $message
}
try {
    foreach ($relative in $payload) {
        $source = Join-Path $workspace ('dist\' + $relative)
        if (!(Test-Path -LiteralPath $source)) { throw ('Run build.ps1 first; missing ' + $relative) }
        $target = Join-Path $fixture $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
    foreach ($relative in @('api-key.bin', 'state.json', '.env', 'NexusM.pdb', 'ui\debug.json', 'ui\assets\browser-cookie.json')) {
        [IO.File]::WriteAllText((Join-Path $fixture $relative), 'synthetic fixture; never ship')
    }
    $versionInfo = (Get-Item -LiteralPath (Join-Path $fixture 'NexusM.exe')).VersionInfo
    $expectedVersion = '{0}.{1}.{2}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
    $output = Join-Path $testRoot 'release'
    $result = & $packageScript -DistributionDirectory $fixture -OutputDirectory $output -Tag ('v' + $expectedVersion)
    Assert-Package ((Split-Path $result.Archive -Leaf) -eq ('NexusM-' + $expectedVersion + '-windows-x64.zip')) 'Release archive is named using the built app version'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($result.Archive)
    try {
        $actual = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') } | Sort-Object)
        $expected = @(@($payload | ForEach-Object { $_.Replace('\', '/') }) + 'README.md' | Sort-Object)
        Assert-Package (@(Compare-Object $expected $actual).Count -eq 0) 'ZIP contains only the app, required UI/runtime files, license, and README'
        $readme = $archive.Entries | Where-Object { $_.FullName -eq 'README.md' }
        Assert-Package ($readme.Length -gt 0) 'Portable download includes end-user instructions'
    }
    finally { $archive.Dispose() }
    $expectedChecksum = (Get-FileHash -LiteralPath $result.Archive -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $result.Archive -Leaf)
    Assert-Package ((Get-Content -LiteralPath $result.Checksum -Raw).Trim() -eq $expectedChecksum) 'Published SHA-256 matches the actual ZIP bytes'
    $originalHash = (Get-FileHash -LiteralPath $result.Archive -Algorithm SHA256).Hash
    Assert-Rejected { & $packageScript -DistributionDirectory $fixture -OutputDirectory $output } 'Packaging refuses to overwrite an existing release archive'
    Assert-Package ((Get-FileHash -LiteralPath $result.Archive -Algorithm SHA256).Hash -eq $originalHash) 'An existing release remains unchanged after a rejected overwrite'
    $invalidOutput = Join-Path $testRoot 'invalid-tag'
    Assert-Rejected { & $packageScript -DistributionDirectory $fixture -OutputDirectory $invalidOutput -Tag 'v999.0.0' } 'A tag that does not match the app version is rejected'
    Assert-Package (!(Test-Path -LiteralPath $invalidOutput)) 'A mismatched tag creates no release output'
    Remove-Item -LiteralPath (Join-Path $fixture 'ui\app.js')
    $incompleteOutput = Join-Path $testRoot 'incomplete'
    Assert-Rejected { & $packageScript -DistributionDirectory $fixture -OutputDirectory $incompleteOutput } 'An incomplete app distribution cannot be packaged'
    Assert-Package (!(Test-Path -LiteralPath $incompleteOutput)) 'An incomplete distribution leaves no release output'
    Write-Host ('Passed ' + $checks + ' package checks.')
}
finally {
    $absolute = [IO.Path]::GetFullPath($testRoot)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\NexusM-PackageTests-'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package fixture cleanup path.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
