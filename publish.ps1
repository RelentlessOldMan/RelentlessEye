# Publishes the hub, the monitor and rmon into .\dist (framework-dependent, net8).
#   dist\RelentlessMonitor.Hub.exe          background hub (--console to see its log)
#   dist\RelentlessMonitor.exe              the WPF monitor
#   dist\rmon.exe                           command line
#   dist\client\RelentlessMonitor.Client.dll + relentless_monitor.py
#                                           for apps that feed the monitor optionally (examples\optional)
#   .\publish.ps1 -InstallClient            also copies dist\client to %LOCALAPPDATA%\RelentlessMonitor\client,
#                                           where the optional bridges look for it
param([switch]$InstallClient)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
foreach ($p in 'RelentlessMonitor.Hub', 'RelentlessMonitor.Ui', 'RelentlessMonitor.Cli') {
    dotnet publish (Join-Path $root "src\$p\$p.csproj") -c Release -o $dist --nologo
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

$client = Join-Path $dist 'client'
New-Item -ItemType Directory -Force $client | Out-Null
Copy-Item (Join-Path $dist 'RelentlessMonitor.Client.dll') $client
Copy-Item (Join-Path $root 'clients\python\relentless_monitor.py') $client
"published to $dist"

if ($InstallClient) {
    $target = Join-Path $env:LOCALAPPDATA 'RelentlessMonitor\client'
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $client '*') $target -Force
    "client installed to $target"
}
