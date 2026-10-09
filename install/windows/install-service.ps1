<#
.SYNOPSIS
  Registers the AirPlay bridge as a Windows Service and opens the firewall ports (docs/PLAN.md "Deployment").
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
    [string]$DataDir = "$env:ProgramData\AirplayBridge",
    [int]$WebPort = 8080,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$serviceName = "AirplayBridge"

if ($Uninstall) {
    if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
        Stop-Service $serviceName -ErrorAction SilentlyContinue
        sc.exe delete $serviceName | Out-Null
    }
    Get-NetFirewallRule -DisplayName "AirplayBridge*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "Removed $serviceName"
    exit 0
}

$exe = Join-Path (Resolve-Path $PublishDir) "bridge.exe"
if (-not (Test-Path $exe)) { throw "bridge.exe not found in $PublishDir; publish first." }
New-Item -ItemType Directory -Force $DataDir | Out-Null

if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    $binPath = "`"$exe`" --DataDirectory `"$DataDir`" --Kestrel:Endpoints:Http:Url http://0.0.0.0:$WebPort"
    New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName "AirPlay Bridge" -StartupType Automatic -Description "Spotify Connect to AirPlay 2 bridge" | Out-Null
}

# Firewall (PLAN.md "Deployment"): the Zeroconf HTTP port and the sender's RTP/timing/control UDP ports are chosen at
# run time, so a per-program rule covers them; mDNS 5353 and the web app port are explicit.
New-NetFirewallRule -DisplayName "AirplayBridge (program)" -Program $exe -Direction Inbound -Action Allow -Profile Private,Domain | Out-Null
New-NetFirewallRule -DisplayName "AirplayBridge mDNS" -Protocol UDP -LocalPort 5353 -Direction Inbound -Action Allow -Profile Private,Domain | Out-Null
New-NetFirewallRule -DisplayName "AirplayBridge web" -Protocol TCP -LocalPort $WebPort -Direction Inbound -Action Allow -Profile Private,Domain | Out-Null

Start-Service $serviceName
Write-Host "$serviceName installed and started; web app on http://localhost:$WebPort, data in $DataDir"
