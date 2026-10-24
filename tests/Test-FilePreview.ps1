param([switch]$Live)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NexusM-preview-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $library = Join-Path $testRoot 'NexusM.Preview.dll'
    $runner = Join-Path $testRoot 'NexusM.FilePreviewTests.exe'
    $sources = @('Models.cs', 'NexusApi.cs') | ForEach-Object { Join-Path $project ('src\' + $_) }
    & $compiler /nologo /langversion:5 /target:library "/out:$library" /r:System.Net.Http.dll /r:System.Web.dll /r:System.Web.Extensions.dll @sources
    if ($LASTEXITCODE -ne 0) { throw 'File preview API compilation failed.' }
    & $compiler /nologo /langversion:5 "/out:$runner" "/r:$library" /r:System.Net.Http.dll /r:System.Security.dll (Join-Path $PSScriptRoot 'FilePreviewTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'File preview test compilation failed.' }
    if ($Live) { & $runner --live } else { & $runner }
    if ($LASTEXITCODE -ne 0) { throw 'File preview tests failed.' }
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\NexusM-preview-tests-'
    if (-not $resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the NexusM preview test directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
