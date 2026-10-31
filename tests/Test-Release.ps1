param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
. (Join-Path $workspace 'tools/Publish-Release.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NexusM-ReleaseTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:checks = 0
$commit = '1111111111111111111111111111111111111111'
$otherCommit = '2222222222222222222222222222222222222222'
function Assert-Release($condition, [string]$name) {
    if (!$condition) { throw $name }
    $script:checks++
    Write-Host ('PASS ' + $name)
}
function Assert-ReleaseRejected([scriptblock]$action, [string]$name) {
    $rejected = $false
    try { & $action | Out-Null } catch { $rejected = $true }
    Assert-Release $rejected $name
}
function New-Package([string]$name, [string]$version = '1.5.0') {
    $directory = Join-Path $testRoot $name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $archive = Join-Path $directory ('NexusM-' + $version + '-windows-x64.zip')
    [IO.File]::WriteAllBytes($archive, [byte[]](80, 75, 3, 4, 78, 101, 120, 117, 115, 77))
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($archive + '.sha256'), ($hash + '  ' + (Split-Path $archive -Leaf) + "`n"), (New-Object Text.UTF8Encoding($false)))
    return $directory
}
function New-Server {
    return @{ Calls = (New-Object 'Collections.Generic.List[object]'); Published = $false; Ref = $null; Release = $null; Latest = $null; Assets = @(); EnumerateAssets = $false; Annotated = @{}; FailUpload = $false; BadUpload = $false }
}
function Request-For($server) {
    return {
        param($method, $path, $body, $file, $mime)
        $server.Calls.Add([PSCustomObject]@{ Method = $method; Path = $path; Body = $body; File = $file; Mime = $mime })
        if ($method -eq 'GET' -and $path -like '*/releases/latest') { return $server.Latest }
        if ($method -eq 'GET' -and $path -like '*/releases/tags/*') { return $server.Release }
        if ($method -eq 'GET' -and $path -like '*/git/ref/tags/*') { return $server.Ref }
        if ($method -eq 'GET' -and $path -like '*/git/tags/*') { return $server.Annotated[($path.Split('/')[-1])] }
        if ($method -eq 'POST' -and $path -like '*/git/refs') { $server.Ref = @{ object = @{ type = 'commit'; sha = $body.sha } }; return $server.Ref }
        if ($method -eq 'POST' -and $path -like '*/releases') { $server.Release = @{ id = 42; draft = $true; html_url = 'https://github.com/fixture/repo/releases/tag/v1.5.0' }; return $server.Release }
        if ($method -eq 'GET' -and $path -like '*/releases/42/assets?per_page=100') {
            if ($server.EnumerateAssets) { return $server.Assets }
            # Invoke-RestMethod sends JSON arrays as one pipeline object, including [].
            Write-Output -NoEnumerate $server.Assets
            return
        }
        if ($method -eq 'POST' -and $path.StartsWith('https://uploads.github.com/')) {
            if ($server.FailUpload) { throw 'Fixture upload failed.' }
            $asset = @{ name = (Split-Path $file -Leaf); size = (Get-Item -LiteralPath $file).Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
            if ($server.BadUpload) { $asset.digest = 'sha256:wrong' }
            $server.Assets += $asset
            return $asset
        }
        if ($method -eq 'PATCH' -and $path -like '*/releases/42') { $server.Published = $true; $server.Release.draft = $false; return $server.Release }
        throw ('Unexpected fixture request: ' + $method + ' ' + $path)
    }.GetNewClosure()
}
function Publish-With($server, [string]$directory, [string]$sha = $commit, [string]$tag = 'v1.5.0') {
    Invoke-NexusMRelease -Repository 'fixture/repo' -Commit $sha -PackageDirectory $directory -ExpectedTag $tag -Request (Request-For $server)
}
function Fixture-Asset([string]$path) {
    return @{ name = (Split-Path $path -Leaf); size = (Get-Item -LiteralPath $path).Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
try {
    $directory = New-Package 'valid'
    $package = Get-NexusMReleasePackage $directory 'v1.5.0'
    Assert-Release ($package.Tag -ceq 'v1.5.0' -and $package.Version -ceq '1.5.0') 'The release version comes from the verified package filename'
    Assert-ReleaseRejected { Get-NexusMReleasePackage $directory 'v1.5.1' } 'A manual tag mismatch fails before publishing'
    $bad = New-Package 'tampered'; [IO.File]::AppendAllText((Join-Path $bad 'NexusM-1.5.0-windows-x64.zip'), 'changed')
    $server = New-Server
    Assert-ReleaseRejected { Publish-With $server $bad } 'A checksum mismatch prevents every GitHub request'
    Assert-Release ($server.Calls.Count -eq 0) 'An unverified package cannot touch tags or releases'
    $bad = New-Package 'wrong-checksum-name'; $checksum = Join-Path $bad 'NexusM-1.5.0-windows-x64.zip.sha256'
    $line = [IO.File]::ReadAllText($checksum).Replace('NexusM-1.5.0-windows-x64.zip', '../NexusM-1.5.0-windows-x64.zip'); [IO.File]::WriteAllText($checksum, $line)
    Assert-ReleaseRejected { Get-NexusMReleasePackage $bad } 'Checksum paths cannot select another file'
    $bad = New-Package 'extra-payload'; [IO.File]::WriteAllText((Join-Path $bad 'private.txt'), 'synthetic fixture')
    Assert-ReleaseRejected { Get-NexusMReleasePackage $bad } 'Unexpected package files cannot be uploaded'
    Assert-ReleaseRejected { Invoke-NexusMRelease -Repository 'fixture/repo/extra' -Commit $commit -PackageDirectory $directory -Request (Request-For (New-Server)) } 'Malformed repositories are rejected before networking'
    Assert-ReleaseRejected { Invoke-NexusMRelease -Repository 'fixture/repo' -Commit 'main' -PackageDirectory $directory -Request (Request-For (New-Server)) } 'Release tags must target an exact tested commit rather than a moving branch'

    $server = New-Server; $result = Publish-With $server $directory
    Assert-Release ($result.Status -eq 'Published' -and $server.Published -and $server.Assets.Count -eq 2) 'An empty REST asset array permits a new version to publish its ZIP and checksum'
    $tagCall = @($server.Calls | Where-Object { $_.Method -eq 'POST' -and $_.Path -like '*/git/refs' })[0]
    Assert-Release ($tagCall.Body.ref -eq 'refs/tags/v1.5.0' -and $tagCall.Body.sha -eq $commit) 'The automatic version tag points at the tested commit'
    $create = @($server.Calls | Where-Object { $_.Method -eq 'POST' -and $_.Path -like '*/releases' })[0]
    Assert-Release ($create.Body.draft -eq $true -and $create.Body.generate_release_notes -eq $true) 'A release stays draft while its assets upload'
    $last = $server.Calls[$server.Calls.Count - 1]
    Assert-Release ($last.Method -eq 'PATCH' -and $last.Body.draft -eq $false) 'Publishing is the final operation after asset verification'
    $server.Calls.Clear(); $result = Publish-With $server $directory $otherCommit
    Assert-Release ($result.Status -eq 'Already published' -and $server.Calls.Count -eq 1 -and $server.Calls[0].Method -eq 'GET') 'Further pushes at the same app version leave its release and tag unchanged'

    foreach ($autoVersion in @('1.5.3', '1.5.4', '1.5.1.6')) {
        $automatic = New-Package ('automatic-' + $autoVersion) $autoVersion
        $server = New-Server
        $result = Publish-With $server $automatic $commit ('v' + $autoVersion)
        Assert-Release ($result.Status -eq 'Published' -and $result.Tag -ceq ('v' + $autoVersion)) ('A plain or legacy automatic version publishes with its exact tag: ' + $autoVersion)
        $create = @($server.Calls | Where-Object { $_.Method -eq 'POST' -and $_.Path -like '*/releases' })[0]
        Assert-Release ($create.Body.name -ceq ('NexusM ' + $autoVersion)) 'The release title uses the same version as its ZIP and tag'
        $last = $server.Calls[$server.Calls.Count - 1]
        Assert-Release ($last.Body.make_latest -ceq 'true') 'A new automatic build becomes the Latest download'
        $server.Calls.Clear(); $result = Publish-With $server $automatic $commit ('v' + $autoVersion)
        Assert-Release ($result.Status -eq 'Already published' -and $server.Calls.Count -eq 1) 'Rerunning an automatic build never replaces its published files'
    }
    $server = New-Server; $server.Latest = @{ tag_name = 'v1.5.1.99' }
    $automatic = New-Package 'delayed-automatic' '1.5.1.8'
    $result = Publish-With $server $automatic $commit 'v1.5.1.8'
    Assert-Release ($result.Status -eq 'Published' -and $server.Calls[$server.Calls.Count - 1].Body.make_latest -ceq 'false') 'Recovering an older failed draft does not move Latest behind a newer build'
    $plain = New-Package 'plain-patch' '1.5.3'
    $server = New-Server; $server.Latest = @{ tag_name = 'v1.5.2.11' }
    $result = Publish-With $server $plain $commit 'v1.5.3'
    Assert-Release ($result.Status -eq 'Published' -and $server.Calls[$server.Calls.Count - 1].Body.make_latest -ceq 'true') 'A plain patch release advances Latest beyond legacy four-part builds'
    $server = New-Server; $server.Latest = @{ tag_name = 'v1.5.4' }
    $result = Publish-With $server $plain $commit 'v1.5.3'
    Assert-Release ($result.Status -eq 'Published' -and $server.Calls[$server.Calls.Count - 1].Body.make_latest -ceq 'false') 'Recovering an older plain release preserves the newer Latest download'
    foreach ($invalidVersion in @('01.5.1', '1.5.1.0', '1.5.1.65535')) {
        $invalid = New-Package ('invalid-' + $invalidVersion) $invalidVersion
        Assert-ReleaseRejected { Get-NexusMReleasePackage $invalid } ('Noncanonical or unsupported app versions cannot publish: ' + $invalidVersion)
    }

    $server = New-Server; $server.Ref = @{ object = @{ type = 'commit'; sha = $otherCommit } }
    Assert-ReleaseRejected { Publish-With $server $directory } 'An unpublished version tag on another commit is never moved'
    Assert-Release (@($server.Calls | Where-Object { $_.Method -ne 'GET' }).Count -eq 0) 'A tag collision causes no GitHub writes'
    $server = New-Server; $server.Ref = @{ object = @{ type = 'tag'; sha = '3333333333333333333333333333333333333333' } }
    $server.Annotated['3333333333333333333333333333333333333333'] = @{ object = @{ type = 'commit'; sha = $commit } }
    $result = Publish-With $server $directory
    Assert-Release ($result.Status -eq 'Published' -and @($server.Calls | Where-Object { $_.Path -like '*/git/refs' }).Count -eq 0) 'A manually pushed annotated version tag is resolved and reused'
    $server = New-Server; $server.Ref = @{ object = @{ type = 'tag'; sha = '3333333333333333333333333333333333333333' } }
    $server.Annotated['3333333333333333333333333333333333333333'] = $server.Ref
    Assert-ReleaseRejected { Publish-With $server $directory } 'Cyclic annotated tags cannot hang the publisher'

    $server = New-Server; $server.FailUpload = $true
    Assert-ReleaseRejected { Publish-With $server $directory } 'Upload failures leave the new release unpublished'
    Assert-Release (!$server.Published -and $server.Release.draft -eq $true) 'A failed upload retains a recoverable draft'
    $server = New-Server; $server.BadUpload = $true
    Assert-ReleaseRejected { Publish-With $server $directory } 'An uploaded digest mismatch cannot publish a release'
    Assert-Release (!$server.Published) 'Unverified uploaded bytes remain hidden in the draft'
    $server = New-Server; $server.Ref = @{ object = @{ type = 'commit'; sha = $commit } }; $server.Release = @{ id = 42; draft = $true; html_url = 'fixture' }
    $server.Assets = @((Fixture-Asset $package.Archive)); $result = Publish-With $server $directory
    Assert-Release ($result.Status -eq 'Published' -and @($server.Calls | Where-Object { $_.Path.StartsWith('https://uploads.github.com/') }).Count -eq 1) 'A singleton REST asset array reuses the verified asset and uploads only the missing checksum'
    $server = New-Server; $server.Ref = @{ object = @{ type = 'commit'; sha = $commit } }; $server.Release = @{ id = 42; draft = $true; html_url = 'fixture' }
    $server.Assets = @((Fixture-Asset $package.Archive), (Fixture-Asset $package.Checksum)); $result = Publish-With $server $directory
    Assert-Release ($result.Status -eq 'Published' -and @($server.Calls | Where-Object { $_.Method -eq 'POST' }).Count -eq 0) 'A complete REST asset array publishes the verified draft without uploading duplicates'
    $server = New-Server; $server.EnumerateAssets = $true; $server.Ref = @{ object = @{ type = 'commit'; sha = $commit } }; $server.Release = @{ id = 42; draft = $true; html_url = 'fixture' }
    $server.Assets = @((Fixture-Asset $package.Archive), (Fixture-Asset $package.Checksum)); $result = Publish-With $server $directory
    Assert-Release ($result.Status -eq 'Published') 'An already enumerated asset response remains supported'
    foreach ($scenario in @('unexpected', 'duplicate')) {
        $server = New-Server; $server.Ref = @{ object = @{ type = 'commit'; sha = $commit } }; $server.Release = @{ id = 42; draft = $true }
        $asset = Fixture-Asset $package.Archive
        if ($scenario -eq 'unexpected') { $asset.name = 'unexpected.zip'; $server.Assets = @($asset) }
        else { $server.Assets = @($asset, (Fixture-Asset $package.Archive)) }
        Assert-ReleaseRejected { Publish-With $server $directory } ($scenario + ' REST asset names prevent publishing')
        Assert-Release (!$server.Published -and @($server.Calls | Where-Object { $_.Method -ne 'GET' }).Count -eq 0) ($scenario + ' draft assets are left unchanged')
    }
    $server = New-Server; $server.Ref = @{ object = @{ type = 'commit'; sha = $commit } }; $server.Release = @{ id = 42; draft = $true }; $asset = Fixture-Asset $package.Archive; $asset.digest = 'sha256:wrong'; $server.Assets = @($asset)
    Assert-ReleaseRejected { Publish-With $server $directory } 'Existing draft assets are never overwritten with different bytes'
    Assert-Release (!$server.Published -and @($server.Calls | Where-Object { $_.Method -ne 'GET' }).Count -eq 0) 'A mismatched draft is left unchanged'

    $workflow = Get-Content -LiteralPath (Join-Path $workspace '.github/workflows/ci.yml') -Raw
    Assert-Release ($workflow -match 'branches: \[main\]' -and $workflow -match "tags: \['v\*'\]" -and $workflow -match 'needs: \[build, release-tests\]') 'Main pushes and manual version tags require both the verified build and Linux publisher tests'
    Assert-Release ($workflow -match '(?s)  release-tests:.*?runs-on: ubuntu-latest.*?shell: pwsh.*?run:.*?tests/Test-Release.ps1') 'Publisher fixtures also run under the production Linux PowerShell runtime'
    Assert-Release ($workflow -match "if: github.event_name == 'push' && \(github.ref == 'refs/heads/main'" -and $workflow -match 'persist-credentials: false') 'Pull requests cannot run the publishing job or retain checkout credentials'
    Assert-Release ($workflow -match "cancel-in-progress: \`$\{\{ github.event_name == 'pull_request' \}\}" -and $workflow -match 'group: publish-\$\{\{ needs.build.outputs.release_tag \}\}') 'Main releases cannot be cancelled halfway through publishing and duplicate versions are serialized'
    Assert-Release ($workflow -match "AUTOMATIC_RELEASE: \`$\{\{ github.event_name == 'push' && github.ref == 'refs/heads/main' \}\}" -and $workflow -match 'fetch-depth: 0' -and $workflow -match 'git tag --list' -and $workflow -match 'git tag --points-at HEAD' -and $workflow -match 'build.ps1 -AppVersion') 'Main pushes allocate a plain version from fetched tags before tests and packaging'
    Assert-Release ($workflow -match 'group: windows-ci-\$\{\{ github.workflow \}\}-\$\{\{ github.ref \}\}' -and $workflow -notmatch 'GITHUB_RUN_NUMBER') 'The main workflow serializes tag allocation through publishing without exposing run numbers'
    Write-Host ('Passed ' + $script:checks + ' release automation checks; no real GitHub requests were made.')
}
finally {
    $absolute = [IO.Path]::GetFullPath($testRoot)
    $temporary = Join-Path ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) 'NexusM-ReleaseTests-'
    if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe release fixture cleanup path.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
