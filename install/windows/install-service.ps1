<#
.SYNOPSIS
  Registers TuneShuttle (the Spotify Connect to AirPlay 2 bridge) as a Windows Service and opens the firewall ports (docs/ARCHITECTURE.md, "Deployment").
.PARAMETER PublishDir
  Folder with the published bridge (dotnet publish src/Bridge.Service -c Release -r win-x64 -o <dir>).
.PARAMETER DataDir
  Where bridge.db and logs live.
.PARAMETER WebPort
  TCP port of the web app and API. Passed to the service as the Kestrel endpoint (it overrides appsettings.json) and
  opened in the firewall.
.NOTES
  Run as administrator. Uninstall with -Uninstall.
#>
param(
    [string]$PublishDir = "$PSScriptRoot\..\..\artifacts\bridge",
    [string]$DataDir = "$env:ProgramData\TuneShuttle",
    [int]$WebPort = 8080,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$serviceName = "TuneShuttle"
# Before 2026-10-10 the product was called "AirPlay Bridge" and the service "AirplayBridge". Remove that service and its
# firewall rules so it does not keep the ports; its data stays in %ProgramData%\AirplayBridge (pass it as -DataDir, or move it).
$legacyServiceName = "AirplayBridge"

function Remove-LegacyService {
    if (Get-Service $legacyServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $legacyServiceName -ErrorAction SilentlyContinue
        sc.exe delete $legacyServiceName | Out-Null
    }
    Get-NetFirewallRule -DisplayName "$legacyServiceName*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

if ($Uninstall) {
    if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
        Stop-Service $serviceName -ErrorAction SilentlyContinue
        sc.exe delete $serviceName | Out-Null
    }
    Get-NetFirewallRule -DisplayName "TuneShuttle*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Remove-LegacyService
    Write-Host "Removed $serviceName"
    exit 0
}

$exe = Join-Path (Resolve-Path $PublishDir) "bridge.exe"
if (-not (Test-Path $exe)) { throw "bridge.exe not found in $PublishDir; publish first." }
New-Item -ItemType Directory -Force $DataDir | Out-Null
Remove-LegacyService

if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    $binPath = "`"$exe`" --DataDirectory `"$DataDir`" --Kestrel:Endpoints:Http:Url http://0.0.0.0:$WebPort"
    New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName "TuneShuttle" -StartupType Automatic -Description "TuneShuttle: Spotify Connect to AirPlay 2 bridge" | Out-Null
}

# Firewall (docs/ARCHITECTURE.md, "Deployment"): the Zeroconf HTTP port and the sender's RTP/timing/control UDP ports are chosen at
# run time, so a per-program rule covers them; mDNS 5353 and the web app port are explicit.
New-NetFirewallRule -DisplayName "TuneShuttle (program)" -Program $exe -Direction Inbound -Action Allow -Profile Private,Domain | Out-Null
New-NetFirewallRule -DisplayName "TuneShuttle mDNS" -Protocol UDP -LocalPort 5353 -Direction Inbound -Action Allow -Profile Private,Domain | Out-Null
New-NetFirewallRule -DisplayName "TuneShuttle web" -Protocol TCP -LocalPort $WebPort -Direction Inbound -Action Allow -Profile Private,Domain | Out-Null

Start-Service $serviceName
Write-Host "$serviceName installed and started; web app on http://localhost:$WebPort, data in $DataDir"
