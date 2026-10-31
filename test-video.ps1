$ErrorActionPreference = 'Stop'
$media = Join-Path $PSScriptRoot 'artifacts\seek-test.mp4'
New-Item -ItemType Directory -Force (Split-Path $media) | Out-Null
ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=320x180:rate=24' -t 30 -c:v libx264 -g 120 -pix_fmt yuv420p -y $media
if ($LASTEXITCODE -ne 0) { throw 'Video fixture generation failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'tests\VideoControls\VideoControls.csproj') -c Release -- $media
if ($LASTEXITCODE -ne 0) { throw 'Video regression checks failed.' }
