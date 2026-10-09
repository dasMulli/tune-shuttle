<#
.SYNOPSIS
  Runs the ptp-master ShairportLive tests (PtpLiveTests) inside Docker Desktop's Linux VM, in a bridge-networked
  .NET SDK container.
.DESCRIPTION
  run-live-tests-in-vm.ps1 uses --net host, where nqptp (inside the shairport-sync container) already owns UDP 319/320,
  so our PTP grandmaster cannot bind them there. This script runs the SDK container on Docker's default bridge network
  instead: the container has its own 319/320 for PtpMaster, and shairport-sync (host-networked in the VM) is reached at
  the bridge gateway 172.17.0.1, where nqptp's answers come back to us as well. Tests opt in with
  [Trait("Category", "ShairportLive")], return early unless AIRPLAY_LIVE=1 (this script sets it) and read SPS_HOST / SPS_PORT.
  See the PtpLiveTests summary (tests/Airplay2.Sender.Tests/Streams/PtpLiveTests.cs).
.PARAMETER Filter
  xunit filter; default selects the PtpLiveTests ShairportLive tests.
.PARAMETER SpsHost
  shairport-sync's address as seen from the bridge network; the VM side of docker0 by default.
#>
param(
    [string]$Filter = "FullyQualifiedName~PtpLiveTests&Category=ShairportLive",
    [string]$Project = "tests/Airplay2.Sender.Tests/Airplay2.Sender.Tests.csproj",
    [string]$SpsHost = "172.17.0.1"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path

docker compose -f "$PSScriptRoot\compose.yaml" up -d | Out-Host

# Separate Linux build output (see run-live-tests-in-vm.ps1). No --net host: the default bridge network.
docker run --rm `
    -v "${repo}:/repo" `
    -v "airplay2-nuget:/root/.nuget/packages" `
    -e AIRPLAY_LIVE=1 `
    -e SPS_HOST=$SpsHost -e SPS_PORT=7000 `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    -w /repo `
    mcr.microsoft.com/dotnet/sdk:10.0 `
    dotnet test $Project --filter $Filter -p:UseArtifactsOutput=true -p:ArtifactsPath=/tmp/artifacts -nologo
