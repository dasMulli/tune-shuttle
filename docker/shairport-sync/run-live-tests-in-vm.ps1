<#
.SYNOPSIS
  Runs the live shairport-sync tests from inside Docker Desktop's Linux VM, where the host-networked shairport-sync
  container is reachable (it is not reachable from the Windows host; see docs/airplay2/TEST-RECEIVER.md).
.DESCRIPTION
  Starts the receivers (docker compose, including the password-protected sps-pin of profile "pin") if needed, then runs
  `dotnet test` in a host-networked .NET SDK container with the repository mounted. Tests opt in with
  [Trait("Category", "ShairportLive")], return early unless AIRPLAY_LIVE=1 (docs/DEVELOPMENT.md, "Live tests"; this script sets it)
  and read SPS_HOST / SPS_PORT; the PIN pairing test reads SPS_PIN_HOST / SPS_PASSWORD, which this script resolves from
  the sps-pin container. A test that returns early is reported as passed, so the script names any test that cannot run.
.PARAMETER Filter
  xunit filter; default selects the ShairportLive category.
.PARAMETER PinPassword
  The password the sps-pin service in compose.yaml is started with.
#>
param(
    [string]$Filter = "Category=ShairportLive",
    [string]$Project = "tests/Airplay2.Sender.Tests/Airplay2.Sender.Tests.csproj",
    [string]$PinPassword = "1234"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path

docker compose -f "$PSScriptRoot\compose.yaml" --profile pin up -d | Out-Host

# sps-pin is on Docker's default bridge network (its own namespace, so its nqptp and port 7000 do not clash with
# sps-test); the host-networked test container reaches it at its bridge address. Docker 29 has no top-level
# .NetworkSettings.IPAddress, so read it per network.
$pinHost = (docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' sps-pin 2>$null | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($pinHost)) {
    Write-Warning "sps-pin has no address: PairingLiveTests.Shairport_password_pin_pairing_then_verify will return early (SKIPPED, reported as passed)."
    $pinHost = ""
} else {
    Write-Host "sps-pin (password receiver) at ${pinHost}:7000"
}

$pinArgs = @()
if ($pinHost) {
    $pinArgs = @("-e", "SPS_PIN_HOST=$pinHost", "-e", "SPS_PIN_PORT=7000", "-e", "SPS_PASSWORD=$PinPassword")
}

# Separate build output for the Linux build so it does not collide with the Windows bin/obj trees. The artifacts layout
# gives every project its own folder under /tmp/artifacts (a global BaseIntermediateOutputPath/OutputPath would make all
# referenced projects share one obj/bin and overwrite each other's project.assets.json).
docker run --rm --net host `
    -v "${repo}:/repo" `
    -v "airplay2-nuget:/root/.nuget/packages" `
    -e AIRPLAY_LIVE=1 `
    -e SPS_HOST=127.0.0.1 -e SPS_PORT=7000 `
    @pinArgs `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    -w /repo `
    mcr.microsoft.com/dotnet/sdk:10.0 `
    dotnet test $Project --filter $Filter -p:UseArtifactsOutput=true -p:ArtifactsPath=/tmp/artifacts -nologo
