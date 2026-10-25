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

function Get-NexusMBuildVersion([string]$AssemblyInfoPath, [string]$RunNumber = '') {
    $source = [IO.File]::ReadAllText($AssemblyInfoPath)
    $assembly = [regex]::Match($source, '\[assembly:\s*AssemblyVersion\("([^"]+)"\)\]')
    $file = [regex]::Match($source, '\[assembly:\s*AssemblyFileVersion\("([^"]+)"\)\]')
    if (!$assembly.Success -or !$file.Success -or $assembly.Groups[1].Value -cne $file.Groups[1].Value) {
        throw 'The source assembly and file versions must be present and match.'
    }
    $base = Get-NexusMVersion $assembly.Groups[1].Value
    if ($RunNumber -eq '') { return $base }
    if ($RunNumber -cnotmatch '^[1-9]\d{0,4}$' -or [long]$RunNumber -gt 65534) {
        throw 'The automatic build number must be a whole number from 1 through 65534.'
    }
    $version = [version]$base.AssemblyVersion
    Get-NexusMVersion ('{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, $version.Build, $RunNumber)
}
