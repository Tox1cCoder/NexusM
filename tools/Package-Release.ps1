param(
    [string]$Tag = '',
    [string]$DistributionDirectory = (Join-Path (Split-Path $PSScriptRoot) 'dist'),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot) 'artifacts\release')
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$distribution = [IO.Path]::GetFullPath($DistributionDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
# Explicit payload prevents local state, credentials, debug files, and stale build files from shipping.
$payload = @('NexusM.exe', 'NexusM.exe.config', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll', 'WebView2-LICENSE.txt', 'ui\index.html', 'ui\app.js', 'ui\app.css', 'ui\assets\app-icon.svg')
foreach ($relative in $payload) {
    if (!(Test-Path -LiteralPath (Join-Path $distribution $relative) -PathType Leaf)) { throw ('Required app file is missing: ' + $relative + '. Run build.ps1 first.') }
}
$readme = Join-Path $workspace 'README.md'
if (!(Test-Path -LiteralPath $readme -PathType Leaf)) { throw 'The end-user README is missing.' }
$versionInfo = (Get-Item -LiteralPath (Join-Path $distribution 'NexusM.exe')).VersionInfo
$version = '{0}.{1}.{2}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
if ($Tag -and $Tag -cne ('v' + $version)) { throw ('Release tag must match the built app version: v' + $version) }
$archive = Join-Path $output ('NexusM-' + $version + '-windows-x64.zip')
$checksum = $archive + '.sha256'
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $checksum)) { throw 'Release output already exists. Choose a new output directory; existing release files are never overwritten.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output ('.stage-' + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($relative in $payload) {
        $target = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $distribution $relative) -Destination $target
    }
    Copy-Item -LiteralPath $readme -Destination (Join-Path $stage 'README.md')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksum, ($hash + '  ' + (Split-Path $archive -Leaf) + "`n"), (New-Object Text.UTF8Encoding($false)))
    [PSCustomObject]@{ Version = $version; Archive = $archive; Checksum = $checksum }
}
finally {
    $absoluteStage = [IO.Path]::GetFullPath($stage)
    if (!$absoluteStage.StartsWith($output.TrimEnd('\') + '\.stage-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package staging cleanup path.' }
    if (Test-Path -LiteralPath $absoluteStage) { Remove-Item -LiteralPath $absoluteStage -Recurse -Force }
}
