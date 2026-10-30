$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NexusM-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $library = Join-Path $testRoot 'NexusM.Backend.dll'
    $runner = Join-Path $testRoot 'NexusM.BackendTests.exe'
    $sources = @('Models.cs', 'NexusApi.cs', 'DownloadService.cs') | ForEach-Object { Join-Path $project ('src\' + $_) }
    & $compiler /nologo /target:library "/out:$library" /r:System.Net.Http.dll /r:System.Web.dll /r:System.Web.Extensions.dll @sources
    if ($LASTEXITCODE -ne 0) { throw 'Backend compilation failed.' }
    & $compiler /nologo "/out:$runner" "/r:$library" /r:System.Net.Http.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'BackendTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Backend test compilation failed.' }
    & $runner
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\NexusM-tests-'
    if (-not $resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the NexusM test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
