param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
. (Join-Path $workspace 'tools\Build-Version.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NexusM-VersionTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:checks = 0
function Assert-Version($condition, [string]$message) {
    if (!$condition) { throw $message }
    $script:checks++
    Write-Host ('PASS ' + $message)
}
function Assert-VersionRejected([scriptblock]$action, [string]$message) {
    $rejected = $false
    try { & $action | Out-Null } catch { $rejected = $true }
    Assert-Version $rejected $message
}
try {
    $fixtureInfo = Join-Path $testRoot 'AssemblyInfo.cs'
    [IO.File]::WriteAllText($fixtureInfo, '[assembly: AssemblyVersion("1.5.2.0")][assembly: AssemblyFileVersion("1.5.2.0")]')
    $manual = Get-NexusMBuildVersion $fixtureInfo
    Assert-Version ($manual.Version -ceq '1.5.2' -and $manual.AssemblyVersion -ceq '1.5.2.0') 'Local and manual-tag builds preserve the plain source version'
    Assert-Version ((Get-NexusMBuildVersion $fixtureInfo -Automatic).Version -ceq '1.5.2') 'The first automatic build starts at the source version floor'
    $tags = @('v1.3.0', 'v1.5.1', 'v1.5.1.6', 'v1.5.2.8', 'v1.5.2.11')
    $first = Get-NexusMBuildVersion $fixtureInfo -Automatic -ExistingTags $tags
    $second = Get-NexusMBuildVersion $fixtureInfo -Automatic -ExistingTags ($tags + 'v1.5.3')
    Assert-Version ($first.Version -ceq '1.5.3' -and $second.Version -ceq '1.5.4') 'Successive pushes advance the patch without exposing a workflow run number'
    Assert-Version ($first.AssemblyVersion -ceq '1.5.3.0') 'Windows receives the same version with a zero revision'
    $rerun = Get-NexusMBuildVersion $fixtureInfo -Automatic -ExistingTags ($tags + 'v1.5.3' + 'v1.5.4') -CommitTags @('v1.5.3')
    Assert-Version ($rerun.Version -ceq $first.Version) 'A published or draft workflow rerun retains the version reserved for its commit'
    Assert-Version ((Get-NexusMBuildVersion $fixtureInfo -Automatic -ExistingTags @('v1.5.2') -CommitTags @('v1.5.2')).Version -ceq '1.5.2') 'A plain named tag on the current commit is reused'
    $noise = @('notes', 'v01.5.9', 'v1.5.9.0', 'v1.5.65535', 'v1.6.900', 'v2.5.900', 'v1.5.9-preview')
    Assert-Version ((Get-NexusMBuildVersion $fixtureInfo -Automatic -ExistingTags ($tags + $noise) -CommitTags @('v1.5.1', 'v2.5.900')).Version -ceq '1.5.3') 'Unrelated, malformed, preview, and noncanonical tags do not change patch allocation'
    Assert-Version ((Get-NexusMBuildVersion $fixtureInfo -Automatic -ExistingTags @('v1.5.65533')).Version -ceq '1.5.65534') 'The largest supported Windows patch version remains valid'
    Assert-VersionRejected { Get-NexusMBuildVersion $fixtureInfo -Automatic -ExistingTags @('v1.5.65534') } 'Patch exhaustion fails before compilation instead of wrapping or reusing a version'
    [IO.File]::WriteAllText($fixtureInfo, '[assembly: AssemblyVersion("1.5.20.0")][assembly: AssemblyFileVersion("1.5.20.0")]')
    Assert-Version ((Get-NexusMBuildVersion $fixtureInfo -Automatic -ExistingTags $tags).Version -ceq '1.5.20') 'A planned source patch above prior tags becomes the next release'
    [IO.File]::WriteAllText($fixtureInfo, '[assembly: AssemblyVersion("1.5.2.11")][assembly: AssemblyFileVersion("1.5.2.11")]')
    Assert-Version ((Get-NexusMBuildVersion $fixtureInfo -Automatic).Version -ceq '1.5.3') 'A legacy four-part source advances to a newer plain patch'
    foreach ($value in @('1.5', '01.5.1', '1.5.1.65535', '65535.0.0', '1.5.1.x')) {
        Assert-VersionRejected { Get-NexusMVersion $value } ('Invalid app version fails before compilation: ' + $value)
    }
    [IO.File]::WriteAllText($fixtureInfo, '[assembly: AssemblyVersion("1.5.1.0")][assembly: AssemblyFileVersion("1.5.2.0")]')
    Assert-VersionRejected { Get-NexusMBuildVersion $fixtureInfo -Automatic } 'Conflicting source versions cannot silently produce a release'

    $sourceInfo = Join-Path $workspace 'src\AssemblyInfo.cs'
    $sourceIndex = Join-Path $workspace 'ui\index.html'
    $infoHash = (Get-FileHash -LiteralPath $sourceInfo -Algorithm SHA256).Hash
    $indexHash = (Get-FileHash -LiteralPath $sourceIndex -Algorithm SHA256).Hash
    $distribution = Join-Path $testRoot 'dist'
    & (Join-Path $workspace 'build.ps1') -AppVersion '1.5.3' -OutputDirectory $distribution
    $versionInfo = (Get-Item -LiteralPath (Join-Path $distribution 'NexusM.exe')).VersionInfo
    $builtVersion = '{0}.{1}.{2}.{3}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart, $versionInfo.FilePrivatePart
    Assert-Version ($builtVersion -ceq '1.5.3.0') 'The compiled executable carries the requested plain build version'
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $distribution 'NexusM.exe')).Version.ToString()
    Assert-Version ($assemblyVersion -ceq $builtVersion) 'Assembly and Windows file versions agree'
    Assert-Version ([IO.File]::ReadAllText((Join-Path $distribution 'ui\index.html')).Contains('<span class="version">1.5.3</span>')) 'Settings displays the plain version without a build suffix'
    Assert-Version ((Get-FileHash -LiteralPath $sourceInfo -Algorithm SHA256).Hash -ceq $infoHash -and (Get-FileHash -LiteralPath $sourceIndex -Algorithm SHA256).Hash -ceq $indexHash) 'Version stamping leaves tracked assembly and UI sources unchanged'
    $package = & (Join-Path $workspace 'tools\Package-Release.ps1') -DistributionDirectory $distribution -OutputDirectory (Join-Path $testRoot 'release') -Tag 'v1.5.3'
    Assert-Version ($package.Version -ceq '1.5.3' -and (Split-Path $package.Archive -Leaf) -ceq 'NexusM-1.5.3-windows-x64.zip') 'The release tag and ZIP use the same plain version as Settings'
    Write-Host ('Passed ' + $script:checks + ' version checks.')
}
finally {
    $absolute = [IO.Path]::GetFullPath($testRoot)
    $temporary = Join-Path ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) 'NexusM-VersionTests-'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe version fixture cleanup path.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
