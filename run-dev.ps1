param([switch]$Build)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$output = Join-Path $repo 'bin\Release\net10.0\win-x64'
if ($Build -or -not (Test-Path (Join-Path $output 'FerrumPix.exe'))) {
    dotnet build (Join-Path $repo 'FerrumPix.vbproj') -c Release -r win-x64 --self-contained false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'FerrumPix build failed.' }
}
foreach ($name in @('libmpv-2.dll', 'libraw.dll', 'libheif.dll', 'jxl.dll', 'THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $repo "local-runtime\$name") -Destination $output
}
$previousProfile = $env:FERRUMPIX_PROFILE_ROOT
try {
    $env:FERRUMPIX_PROFILE_ROOT = Join-Path $repo 'local-data\profile'
    Start-Process -FilePath (Join-Path $output 'FerrumPix.exe') -WorkingDirectory $repo
} finally {
    $env:FERRUMPIX_PROFILE_ROOT = $previousProfile
}
