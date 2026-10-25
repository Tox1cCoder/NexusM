param(
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Commit = $env:GITHUB_SHA,
    [string]$PackageDirectory = (Join-Path (Split-Path $PSScriptRoot) 'release'),
    [string]$ExpectedTag = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Version.ps1')

function Get-NexusMReleasePackage([string]$Directory, [string]$Tag = '') {
    $files = @(Get-ChildItem -LiteralPath $Directory -File)
    $archives = @($files | Where-Object { $_.Name -like '*.zip' })
    if ($archives.Count -ne 1 -or $files.Count -ne 2) { throw 'The verified release must contain exactly one app ZIP and its checksum.' }
    $archive = $archives[0]
    if ($archive.Name -cnotmatch '^NexusM-(\d+\.\d+\.\d+(?:\.\d+)?)-windows-x64\.zip$') { throw 'The app ZIP has an invalid release filename.' }
    $version = $Matches[1]
    if ((Get-NexusMVersion $version).Version -cne $version) { throw 'The app version must use three or four canonical numeric components.' }
    $releaseTag = 'v' + $version
    if ($Tag -and $Tag -cne $releaseTag) { throw 'The requested release tag does not match the verified app package.' }
    $checksum = Join-Path $Directory ($archive.Name + '.sha256')
    if (!(Test-Path -LiteralPath $checksum -PathType Leaf)) { throw 'The app ZIP checksum is missing.' }
    $line = (Get-Content -LiteralPath $checksum -Raw).Trim()
    if ($line -cnotmatch '^([a-fA-F0-9]{64})  (NexusM-\d+\.\d+\.\d+(?:\.\d+)?-windows-x64\.zip)$' -or $Matches[2] -cne $archive.Name) {
        throw 'The checksum must name exactly the verified app ZIP.'
    }
    $hash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $Matches[1].ToLowerInvariant()) { throw 'The app ZIP failed SHA-256 verification.' }
    [PSCustomObject]@{ Tag = $releaseTag; Version = $version; Archive = $archive.FullName; Checksum = [IO.Path]::GetFullPath($checksum); SHA256 = $hash }
}

function Invoke-NexusMRelease {
    param([string]$Repository, [string]$Commit, [string]$PackageDirectory, [string]$ExpectedTag = '', [scriptblock]$Request = $null)
    if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'A GitHub owner/repository is required.' }
    if ($Commit -notmatch '^(?:[a-f0-9]{40}|[a-f0-9]{64})$') { throw 'The tested commit SHA is required.' }
    $package = Get-NexusMReleasePackage $PackageDirectory $ExpectedTag
    if ($null -eq $Request) {
        if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GitHub Actions must provide its release token.' }
        $headers = @{ Authorization = 'Bearer ' + $env:GH_TOKEN; Accept = 'application/vnd.github+json'; 'User-Agent' = 'NexusM-release'; 'X-GitHub-Api-Version' = '2022-11-28' }
        $Request = {
            param($method, $path, $body, $file, $mime)
            $uri = if ($path.StartsWith('https://uploads.github.com/')) { $path } else { 'https://api.github.com' + $path }
            $arguments = @{ Uri = $uri; Method = $method; Headers = $headers; ErrorAction = 'Stop' }
            if ($file) { $arguments.InFile = $file; $arguments.ContentType = $mime }
            elseif ($null -ne $body) { $arguments.Body = ConvertTo-Json -InputObject $body -Depth 10 -Compress; $arguments.ContentType = 'application/json' }
            try { Invoke-RestMethod @arguments }
            catch {
                if ($method -eq 'GET' -and $null -ne $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }
                throw
            }
        }.GetNewClosure()
    }
    $base = '/repos/' + $Repository
    $release = & $Request 'GET' ($base + '/releases/tags/' + $package.Tag) $null $null $null
    if ($null -ne $release -and !$release.draft) {
        # Reruns and explicit version tags must never replace a published release.
        return [PSCustomObject]@{ Status = 'Already published'; Tag = $package.Tag; Url = $release.html_url }
    }
    $reference = & $Request 'GET' ($base + '/git/ref/tags/' + $package.Tag) $null $null $null
    if ($null -ne $reference) {
        $target = $reference.object
        $seen = New-Object 'Collections.Generic.HashSet[string]'
        for ($depth = 0; $target.type -eq 'tag'; $depth++) {
            if ($depth -ge 8 -or !$seen.Add([string]$target.sha)) { throw 'The existing release tag could not be resolved safely.' }
            $annotated = & $Request 'GET' ($base + '/git/tags/' + $target.sha) $null $null $null
            $target = $annotated.object
        }
        if ($target.type -ne 'commit' -or $target.sha -ne $Commit) { throw 'This version tag already belongs to a different commit. Bump the app version; published tags are never moved.' }
    }
    else {
        & $Request 'POST' ($base + '/git/refs') @{ ref = 'refs/tags/' + $package.Tag; sha = $Commit } $null $null | Out-Null
    }
    if ($null -eq $release) {
        $notes = @'
Download the **NexusM Windows x64 ZIP**, extract it, and open `NexusM.exe`. Keep all extracted files together.

Requirements: .NET Framework 4.8 (or compatible newer version) and Microsoft Edge WebView2 Runtime. See the included README for setup, Nexus API keys, and everyday use.

The accompanying `.sha256` file verifies the ZIP download. This portable build does not bundle runtimes or personal account data.
'@
        $release = & $Request 'POST' ($base + '/releases') @{ tag_name = $package.Tag; target_commitish = $Commit; name = 'NexusM ' + $package.Version; body = $notes; draft = $true; prerelease = $false; generate_release_notes = $true } $null $null
    }
    if ($null -eq $release -or !$release.draft -or [long]$release.id -le 0) { throw 'GitHub did not provide a draft release for the verified app.' }
    # REST JSON arrays arrive as one pipeline object. Enumerate the assigned value
    # before validating individual assets; an empty draft must contain zero assets.
    $assetResponse = & $Request 'GET' ($base + '/releases/' + $release.id + '/assets?per_page=100') $null $null $null
    $assets = @($assetResponse | Where-Object { $null -ne $_ })
    $expected = @{}
    foreach ($path in @($package.Archive, $package.Checksum)) {
        $file = Get-Item -LiteralPath $path
        $expected[$file.Name] = @{ Path = $file.FullName; Size = $file.Length; Digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    foreach ($asset in $assets) {
        if (!$expected.ContainsKey([string]$asset.name) -or @($assets | Where-Object { $_.name -eq $asset.name }).Count -ne 1) { throw 'The existing draft contains unexpected release assets; it was left unchanged.' }
        $wanted = $expected[[string]$asset.name]
        if ($asset.size -ne $wanted.Size -or $asset.digest -ne $wanted.Digest) { throw 'The existing draft asset differs from the verified package; it was not overwritten.' }
    }
    foreach ($name in $expected.Keys) {
        if (@($assets | Where-Object { $_.name -eq $name }).Count -gt 0) { continue }
        $wanted = $expected[$name]
        $mime = if ($name.EndsWith('.zip')) { 'application/zip' } else { 'text/plain' }
        $upload = & $Request 'POST' ('https://uploads.github.com' + $base + '/releases/' + $release.id + '/assets?name=' + [Uri]::EscapeDataString($name)) $null $wanted.Path $mime
        if ($upload.name -ne $name -or $upload.size -ne $wanted.Size -or $upload.digest -ne $wanted.Digest) { throw 'GitHub could not verify an uploaded asset; the release remains a draft.' }
    }
    $latest = & $Request 'GET' ($base + '/releases/latest') $null $null $null
    $makeLatest = 'true'
    if ($null -ne $latest -and $latest.tag_name -cmatch '^v(\d+\.\d+\.\d+(?:\.\d+)?)$') {
        $latestVersion = Get-NexusMVersion $Matches[1]
        if ([version]$latestVersion.AssemblyVersion -gt [version](Get-NexusMVersion $package.Version).AssemblyVersion) { $makeLatest = 'false' }
    }
    $published = & $Request 'PATCH' ($base + '/releases/' + $release.id) @{ draft = $false; make_latest = $makeLatest } $null $null
    if ($null -eq $published -or $published.draft) { throw 'GitHub did not publish the verified draft release.' }
    [PSCustomObject]@{ Status = 'Published'; Tag = $package.Tag; Url = $published.html_url }
}

# Dot-sourcing exposes the same tested publishing logic to isolated API fixtures.
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-NexusMRelease -Repository $Repository -Commit $Commit -PackageDirectory $PackageDirectory -ExpectedTag $ExpectedTag | Format-List
}
