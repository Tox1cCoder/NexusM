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
    [IO.File]::WriteAllText($fixtureInfo, '[assembly: AssemblyVersion("1.5.1.0")][assembly: AssemblyFileVersion("1.5.1.0")]')
    $manual = Get-NexusMBuildVersion $fixtureInfo
    Assert-Version ($manual.Version -ceq '1.5.1' -and $manual.AssemblyVersion -ceq '1.5.1.0') 'Local and manual-tag builds preserve the source version'
    $first = Get-NexusMBuildVersion $fixtureInfo '6'
    $second = Get-NexusMBuildVersion $fixtureInfo '7'
    Assert-Version ($first.Version -ceq '1.5.1.6' -and $second.Version -ceq '1.5.1.7') 'Successive pushes have distinct versions without editing source'
    Assert-Version ((Get-NexusMBuildVersion $fixtureInfo '6').Version -ceq $first.Version) 'A workflow rerun retains its original release version'
    Assert-Version ((Get-NexusMBuildVersion $fixtureInfo '65534').Version -ceq '1.5.1.65534') 'The largest supported .NET build revision remains valid'
    foreach ($number in @('0', '-1', '06', '65535', '999999999999999999999', '6.1', 'x', ' 6')) {
        Assert-VersionRejected { Get-NexusMBuildVersion $fixtureInfo $number } ('Invalid automatic build number fails before compilation: ' + $number)
    }
    foreach ($value in @('1.5', '01.5.1', '1.5.1.65535', '65535.0.0', '1.5.1.x')) {
        Assert-VersionRejected { Get-NexusMVersion $value } ('Invalid app version fails before compilation: ' + $value)
    }
    [IO.File]::WriteAllText($fixtureInfo, '[assembly: AssemblyVersion("1.5.1.0")][assembly: AssemblyFileVersion("1.5.2.0")]')
    Assert-VersionRejected { Get-NexusMBuildVersion $fixtureInfo '6' } 'Conflicting source versions cannot silently produce a release'

    $sourceInfo = Join-Path $workspace 'src\AssemblyInfo.cs'
    $sourceIndex = Join-Path $workspace 'ui\index.html'
    $infoHash = (Get-FileHash -LiteralPath $sourceInfo -Algorithm SHA256).Hash
    $indexHash = (Get-FileHash -LiteralPath $sourceIndex -Algorithm SHA256).Hash
    $distribution = Join-Path $testRoot 'dist'
    & (Join-Path $workspace 'build.ps1') -AppVersion '1.5.1.42' -OutputDirectory $distribution
    $versionInfo = (Get-Item -LiteralPath (Join-Path $distribution 'NexusM.exe')).VersionInfo
    $builtVersion = '{0}.{1}.{2}.{3}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart, $versionInfo.FilePrivatePart
    Assert-Version ($builtVersion -ceq '1.5.1.42') 'The compiled executable carries the requested automatic build version'
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $distribution 'NexusM.exe')).Version.ToString()
    Assert-Version ($assemblyVersion -ceq $builtVersion) 'Assembly and Windows file versions agree'
    Assert-Version ([IO.File]::ReadAllText((Join-Path $distribution 'ui\index.html')).Contains('<span class="version">1.5.1.42</span>')) 'The copied Settings UI displays the same automatic version'
    Assert-Version ((Get-FileHash -LiteralPath $sourceInfo -Algorithm SHA256).Hash -ceq $infoHash -and (Get-FileHash -LiteralPath $sourceIndex -Algorithm SHA256).Hash -ceq $indexHash) 'Version stamping leaves tracked assembly and UI sources unchanged'
    $package = & (Join-Path $workspace 'tools\Package-Release.ps1') -DistributionDirectory $distribution -OutputDirectory (Join-Path $testRoot 'release') -Tag 'v1.5.1.42'
    Assert-Version ($package.Version -ceq '1.5.1.42' -and (Split-Path $package.Archive -Leaf) -ceq 'NexusM-1.5.1.42-windows-x64.zip') 'The release tag and ZIP retain the complete automatic version'
    Write-Host ('Passed ' + $script:checks + ' version checks.')
}
finally {
    $absolute = [IO.Path]::GetFullPath($testRoot)
    $temporary = Join-Path ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) 'NexusM-VersionTests-'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe version fixture cleanup path.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
