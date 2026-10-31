$ErrorActionPreference = 'Stop'

function Get-NexusMVersion([string]$Value) {
    if ($Value -cnotmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw 'An app version must have three or four numeric components.' }
    $parts = $Value.Split('.')
    $parsed = [version]$Value
    if ($parsed.ToString($parts.Length) -cne $Value -or @($parts | Where-Object { [long]$_ -gt 65534 }).Count -gt 0) {
        throw 'App version components must be canonical numbers from 0 through 65534.'
    }
    $revision = if ($parsed.Revision -lt 0) { 0 } else { $parsed.Revision }
    $assembly = '{0}.{1}.{2}.{3}' -f $parsed.Major, $parsed.Minor, $parsed.Build, $revision
    $display = if ($revision -eq 0) { $parsed.ToString(3) } else { $assembly }
    [PSCustomObject]@{ Version = $display; AssemblyVersion = $assembly }
}

function Get-NexusMTaggedVersion([string]$Tag) {
    if ($Tag -cnotmatch '^v(\d+\.\d+\.\d+(?:\.\d+)?)$') { return }
    try { $version = Get-NexusMVersion $Matches[1] } catch { return }
    if (('v' + $version.Version) -ceq $Tag) { return $version }
}

function Get-NexusMBuildVersion {
    param([string]$AssemblyInfoPath, [switch]$Automatic, [string[]]$ExistingTags = @(), [string[]]$CommitTags = @())
    $source = [IO.File]::ReadAllText($AssemblyInfoPath)
    $assembly = [regex]::Match($source, '\[assembly:\s*AssemblyVersion\("([^"]+)"\)\]')
    $file = [regex]::Match($source, '\[assembly:\s*AssemblyFileVersion\("([^"]+)"\)\]')
    if (!$assembly.Success -or !$file.Success -or $assembly.Groups[1].Value -cne $file.Groups[1].Value) {
        throw 'The source assembly and file versions must be present and match.'
    }
    $base = Get-NexusMVersion $assembly.Groups[1].Value
    if (!$Automatic) { return $base }
    $sourceVersion = [version]$base.AssemblyVersion
    $sameCommit = @($CommitTags | ForEach-Object {
        $tagVersion = Get-NexusMTaggedVersion $_
        if ($null -ne $tagVersion -and $tagVersion.Version.Split('.').Length -eq 3) {
            $parsed = [version]$tagVersion.AssemblyVersion
            if ($parsed.Major -eq $sourceVersion.Major -and $parsed.Minor -eq $sourceVersion.Minor -and $parsed.Build -ge $sourceVersion.Build) { $tagVersion }
        }
    } | Sort-Object { [version]$_.AssemblyVersion } -Descending)
    # A tag is reserved before publishing, so both published and draft reruns
    # retain the version already assigned to this exact tested commit.
    if ($sameCommit.Count -gt 0) { return $sameCommit[0] }
    $lastPatch = $sourceVersion.Build - 1
    if ($sourceVersion.Revision -gt 0) { $lastPatch = $sourceVersion.Build }
    foreach ($tag in $ExistingTags) {
        $tagVersion = Get-NexusMTaggedVersion $tag
        if ($null -eq $tagVersion) { continue }
        $parsed = [version]$tagVersion.AssemblyVersion
        if ($parsed.Major -eq $sourceVersion.Major -and $parsed.Minor -eq $sourceVersion.Minor -and $parsed.Build -gt $lastPatch) { $lastPatch = $parsed.Build }
    }
    if ($lastPatch -ge 65534) { throw 'The patch version is exhausted. Increase the source major or minor version.' }
    Get-NexusMVersion ('{0}.{1}.{2}' -f $sourceVersion.Major, $sourceVersion.Minor, ($lastPatch + 1))
}
