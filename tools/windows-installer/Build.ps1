param([string]$Compiler)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
if (-not $Compiler) { $Compiler = Join-Path $repo 'artifacts\inno\ISCC.exe' }
if (-not (Test-Path $Compiler)) { throw 'Specify the path to Inno Setup ISCC.exe using -Compiler.' }
$payload = Join-Path $repo 'artifacts\installer-payload'
$launcher = Join-Path $repo 'artifacts\installer-launcher'
$scripts = Join-Path $repo 'artifacts\installer-script'
New-Item -ItemType Directory -Force $scripts | Out-Null
dotnet publish (Join-Path $repo 'FerrumPix.vbproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
dotnet publish (Join-Path $PSScriptRoot 'Launcher\Open.csproj') -c Release -r win-x64 -o $launcher
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
Copy-Item -LiteralPath (Join-Path $launcher 'FerrumPix.Open.exe') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'FerrumPix_Icon.ico') -Destination $payload
foreach ($name in @('libmpv-2.dll','libraw.dll','libheif.dll','jxl.dll','THIRD-PARTY-NOTICES.txt','LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $repo "local-runtime\$name") -Destination $payload
}
$extensions = @()
foreach ($spec in @(@('Services\MediaFileTypes.vb','Displayable'), @('Services\RawPreviewService.vb','SupportedExtensions'))) {
    $source = Get-Content (Join-Path $repo $spec[0]) -Raw
    $block = [regex]::Match($source, $spec[1] + ' As String\(\) = \{([\s\S]*?)\}').Groups[1].Value
    $extensions += [regex]::Matches($block, '"(\.[a-z0-9]+)"') | ForEach-Object { $_.Groups[1].Value }
}
$extensions = @($extensions | Sort-Object -Unique)
if ($extensions.Count -lt 24) { throw 'Could not read supported media types.' }
$registration = foreach ($extension in $extensions) {
    'Root: HKCU; Subkey: "Software\Classes\Applications\FerrumPix.Open.exe\SupportedTypes"; ValueType: string; ValueName: "' + $extension + '"; ValueData: ""'
    'Root: HKCU; Subkey: "Software\Classes\' + $extension + '\OpenWithProgids"; ValueType: string; ValueName: "FerrumPixDev.Media"; ValueData: ""; Flags: uninsdeletevalue'
    'Root: HKCU; Subkey: "Software\FerrumPixDev\Capabilities\FileAssociations"; ValueType: string; ValueName: "' + $extension + '"; ValueData: "FerrumPixDev.Media"'
}
$registration | Set-Content (Join-Path $scripts 'Extensions.iss') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Installer.iss') -Destination $scripts
$version = (Get-Content (Join-Path $repo 'VERSION') -Raw).Trim()
& $Compiler "/DRepoRoot=$repo" "/DPayload=$payload" "/DBuildVersion=$version" (Join-Path $scripts 'Installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
