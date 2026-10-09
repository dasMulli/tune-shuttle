<#
.SYNOPSIS
  Runs the live shairport-sync tests from inside Docker Desktop's Linux VM, where the host-networked shairport-sync
  container is reachable (it is not reachable from the Windows host; see docs/airplay2/TEST-RECEIVER.md).
.DESCRIPTION
  Starts the receiver (docker compose) if needed, then runs `dotnet test` in a host-networked .NET SDK container with
  the repository mounted. Tests opt in with [Trait("Category", "ShairportLive")], return early unless AIRPLAY_LIVE=1
  (WORK-PLAN.md section 1; this script sets it) and read SPS_HOST / SPS_PORT.
.PARAMETER Filter
  xunit filter; default selects the ShairportLive category.
#>
param(
    [string]$Filter = "Category=ShairportLive",
    [string]$Project = "tests/Airplay2.Sender.Tests/Airplay2.Sender.Tests.csproj"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path

docker compose -f "$PSScriptRoot\compose.yaml" up -d | Out-Host

# Separate build output for the Linux build so it does not collide with the Windows bin/obj trees. The artifacts layout
# gives every project its own folder under /tmp/artifacts (a global BaseIntermediateOutputPath/OutputPath would make all
# referenced projects share one obj/bin and overwrite each other's project.assets.json).
docker run --rm --net host `
    -v "${repo}:/repo" `
    -v "airplay2-nuget:/root/.nuget/packages" `
    -e AIRPLAY_LIVE=1 `
    -e SPS_HOST=127.0.0.1 -e SPS_PORT=7000 `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    -w /repo `
    mcr.microsoft.com/dotnet/sdk:10.0 `
    dotnet test $Project --filter $Filter -p:UseArtifactsOutput=true -p:ArtifactsPath=/tmp/artifacts -nologo
