# Builds the RelentlessMonitor.Client NuGet package into .\packages (this repo's own local feed).
#   .\pack.ps1                 -> version from Directory.Build.props (1.0.0)
#   .\pack.ps1 -Version 1.0.1  -> bump it; NuGet caches a version forever, so bump when you change the client
# Consuming apps: dotnet add package RelentlessMonitor.Client -s C:\Playground\RelentlessMonitor\packages
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'packages'
$props = @('-c', 'Release', '-o', $out)
if ($Version) { $props += "-p:Version=$Version" }
dotnet pack (Join-Path $root 'src\RelentlessMonitor.Client\RelentlessMonitor.Client.csproj') @props
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Get-ChildItem $out -Filter 'RelentlessMonitor.Client.*.nupkg' | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { "packed $($_.FullName)" }
