# Builds the RelentlessEye.Client NuGet package into .\packages (this repo's own local feed).
#   .\pack.ps1                 -> version from Directory.Build.props (VersionPrefix)
#   .\pack.ps1 -Version 1.0.1  -> bump it; NuGet caches a version forever, so bump when you change the client
# Consuming apps: dotnet add package RelentlessEye.Client -s C:\Playground\RelentlessEye\packages
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'packages'
$props = @('-c', 'Release', '-o', $out)
if ($Version) { $props += "-p:Version=$Version" }
dotnet pack (Join-Path $root 'src\RelentlessEye.Client\RelentlessEye.Client.csproj') @props
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Get-ChildItem $out -Filter 'RelentlessEye.Client.*.nupkg' | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { "packed $($_.FullName)" }
