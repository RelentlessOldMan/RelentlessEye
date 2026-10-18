# Publishes the hub, the monitor and reye into .\dist (framework-dependent, net8).
#   dist\RelentlessEye.Hub.exe          background hub (--console to see its log)
#   dist\RelentlessEye.exe              the WPF monitor
#   dist\reye.exe                           command line
#   dist\client\RelentlessEye.Client.dll + relentless_eye.py
#                                           for apps that feed the monitor optionally (examples\optional)
#   .\publish.ps1 -InstallClient            also copies dist\client to %LOCALAPPDATA%\RelentlessEye\client,
#                                           where the optional bridges look for it
param([switch]$InstallClient)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
foreach ($p in 'RelentlessEye.Hub', 'RelentlessEye.Ui', 'RelentlessEye.Cli') {
    dotnet publish (Join-Path $root "src\$p\$p.csproj") -c Release -o $dist --nologo
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

$client = Join-Path $dist 'client'
New-Item -ItemType Directory -Force $client | Out-Null
Copy-Item (Join-Path $dist 'RelentlessEye.Client.dll') $client
Copy-Item (Join-Path $root 'clients\python\relentless_eye.py') $client
"published to $dist"

if ($InstallClient) {
    $target = Join-Path $env:LOCALAPPDATA 'RelentlessEye\client'
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $client '*') $target -Force
    "client installed to $target"
}
