<#
.SYNOPSIS
  CI build of TuneShuttle (the Spotify Connect to AirPlay 2 bridge): build, test without the live categories, publish win-x64, build the container image.

.DESCRIPTION
  Steps (each can be skipped):
    1. build    dotnet build TuneShuttle.sln (warnings are errors in this solution, Directory.Build.props)
    2. privacy  tools/privacy-scan.ps1: no personal or household data in the tracked files (docs/DEVELOPMENT.md,
                "Privacy scan"); fails the build with the offending lines
    3. test     dotnet test with --filter "Category!=ReceiverLive&Category!=ShairportLive" (docs/DEVELOPMENT.md, "Live tests":
                live tests never run here; AIRPLAY_LIVE is cleared for the test step as a second guard).
                -ExcludeTimingTests also drops the 30 s pacer accuracy run (Category=Timing) on loaded runners.
                TRX files go to <Output>/test-results.
    3a. ffmpeg  tools/get-ffmpeg.ps1 into <Output>/ffmpeg: the FFmpeg 7.1 LGPL shared DLLs (avcodec, avutil, swresample) the AAC
                decoder loads (docs/airplay2/SPEC-receiver.md 7.2, Section 10). Runs before the tests, which find the DLLs
                through AIRPLAY_FFMPEG_PATH, and after publish copies them into <Output>/bridge next to bridge.exe.
                Windows runtimes only; -SkipFfmpeg skips it (the decode tests then report "skipped: libavcodec not found"
                and the bridge runs ALAC-only, SPEC-receiver.md R3).
    4. publish  the bridge (src/Bridge.Service, the web app included) to <Output>/bridge, the folder
                install/windows/install-service.ps1 expects, and the CLI (src/Airplay2.Sender.Cli) to <Output>/airplay2,
                both for -Runtime (win-x64), framework-dependent unless -SelfContained.
    5. container docker build -f docker/Dockerfile -t <ImageTag> . (skipped with a warning when docker is not
                available, unless -RequireContainer).

  Works in Windows PowerShell 5.1 and PowerShell 7. Run from anywhere; paths are relative to this script.

.EXAMPLE
  ./build.ps1                                   # everything, Release
.EXAMPLE
  ./build.ps1 -SkipContainer -ExcludeTimingTests
.EXAMPLE
  ./build.ps1 -SkipTests -SkipContainer -SelfContained   # just the Windows service bundle
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Output = (Join-Path $PSScriptRoot "artifacts"),
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [string]$ImageTag = "tune-shuttle:dev",
    [switch]$SkipBuild,
    [switch]$SkipPrivacyScan,
    [switch]$SkipTests,
    [switch]$SkipPublish,
    [switch]$SkipContainer,
    [switch]$SkipFfmpeg,
    [switch]$RequireContainer,
    [switch]$ExcludeTimingTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repo = $PSScriptRoot
$solution = Join-Path $repo "TuneShuttle.sln"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

function Invoke-Native {
    param([string]$Step, [string]$File, [string[]]$Arguments)
    Write-Host ""
    Write-Host "==> $Step" -ForegroundColor Cyan
    Write-Host "    $File $($Arguments -join ' ')" -ForegroundColor DarkGray
    $started = Get-Date
    & $File @Arguments
    $code = $LASTEXITCODE
    $elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    if ($code -ne 0) {
        throw "$Step failed with exit code $code after $elapsed s"
    }
    Write-Host "    $Step done in $elapsed s" -ForegroundColor Green
}

$summary = New-Object System.Collections.Generic.List[string]
Push-Location $repo
try {
    Invoke-Native "dotnet --version" "dotnet" @("--version")

    if (-not $SkipBuild) {
        Invoke-Native "build ($Configuration)" "dotnet" @("build", $solution, "-c", $Configuration, "-nologo")
        $summary.Add("build: $Configuration, 0 errors")
    }

    if (-not $SkipPrivacyScan) {
        # Same PowerShell edition as this script (Windows PowerShell 5.1 or pwsh).
        $shell = (Get-Process -Id $PID).Path
        Invoke-Native "privacy scan" $shell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repo "tools/privacy-scan.ps1"))
        $summary.Add("privacy scan: clean")
    }

    # FFmpeg DLLs for the AAC decoder (SPEC-receiver.md 7.2): fetched once before the tests, copied into the bundle after publish.
    $ffmpegOut = Join-Path $Output "ffmpeg"
    $withFfmpeg = (-not $SkipFfmpeg) -and ($Runtime -like "win-*")
    if ($withFfmpeg -and -not ($SkipTests -and $SkipPublish)) {
        $shell = (Get-Process -Id $PID).Path
        Invoke-Native "ffmpeg (get-ffmpeg.ps1)" $shell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repo "tools/get-ffmpeg.ps1"), "-Destination", $ffmpegOut, "-CacheDirectory", (Join-Path $Output "ffmpeg-cache"))
        $summary.Add("ffmpeg: $ffmpegOut")
    }

    if (-not $SkipTests) {
        $filter = "Category!=ReceiverLive&Category!=ShairportLive"
        if ($ExcludeTimingTests) {
            $filter += "&Category!=Timing"
        }

        $results = Join-Path $Output "test-results"
        New-Item -ItemType Directory -Force $results | Out-Null
        $testArgs = @("test", $solution, "-c", $Configuration, "--filter", $filter, "--logger", "trx", "--results-directory", $results, "-nologo")
        if (-not $SkipBuild) {
            $testArgs += "--no-build"
        }

        # Second guard next to the filter: live tests return early unless AIRPLAY_LIVE=1 (docs/DEVELOPMENT.md, "Live tests").
        $savedLive = $env:AIRPLAY_LIVE
        $savedAudio = $env:AIRPLAY_AUDIO_I_KNOW
        $savedFfmpeg = $env:AIRPLAY_FFMPEG_PATH
        if ($withFfmpeg) { $env:AIRPLAY_FFMPEG_PATH = $ffmpegOut }
        Remove-Item Env:AIRPLAY_LIVE -ErrorAction SilentlyContinue
        Remove-Item Env:AIRPLAY_AUDIO_I_KNOW -ErrorAction SilentlyContinue
        try {
            Invoke-Native "test ($filter)" "dotnet" $testArgs
        }
        finally {
            if ($null -ne $savedLive) { $env:AIRPLAY_LIVE = $savedLive }
            if ($null -ne $savedAudio) { $env:AIRPLAY_AUDIO_I_KNOW = $savedAudio }
            if ($null -ne $savedFfmpeg) { $env:AIRPLAY_FFMPEG_PATH = $savedFfmpeg } else { Remove-Item Env:AIRPLAY_FFMPEG_PATH -ErrorAction SilentlyContinue }
        }

        $summary.Add("test: passed ($filter), TRX in $results")
    }

    if (-not $SkipPublish) {
        $sc = if ($SelfContained) { "true" } else { "false" }
        $bridgeOut = Join-Path $Output "bridge"
        $cliOut = Join-Path $Output "airplay2"
        # Publish always uses Release: Debug builds of the bridge reference the test-only fake receiver (Bridge:FakeReceivers).
        Invoke-Native "publish bridge ($Runtime)" "dotnet" @("publish", (Join-Path $repo "src/Bridge.Service/Bridge.Service.csproj"), "-c", "Release", "-r", $Runtime, "--self-contained", $sc, "-o", $bridgeOut, "-nologo")
        Invoke-Native "publish CLI ($Runtime)" "dotnet" @("publish", (Join-Path $repo "src/Airplay2.Sender.Cli/Airplay2.Sender.Cli.csproj"), "-c", "Release", "-r", $Runtime, "--self-contained", $sc, "-o", $cliOut, "-nologo")
        # The apphosts the install scripts start (install/windows: bridge.exe, install/linux/tune-shuttle.service: bridge).
        $exe = if ($Runtime -like "win-*") { ".exe" } else { "" }
        foreach ($required in @((Join-Path $bridgeOut "bridge$exe"), (Join-Path $bridgeOut "wwwroot/index.html"), (Join-Path $cliOut "airplay2$exe"))) {
            if (-not (Test-Path $required)) {
                throw "publish output incomplete: $required is missing"
            }
        }

        if ($withFfmpeg) {
            Invoke-Native "ffmpeg into the bundle (get-ffmpeg.ps1)" $shell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repo "tools/get-ffmpeg.ps1"), "-Destination", $bridgeOut, "-CacheDirectory", (Join-Path $Output "ffmpeg-cache"))
            if (-not (Test-Path (Join-Path $bridgeOut "avcodec-61.dll"))) {
                throw "publish output incomplete: avcodec-61.dll is missing from $bridgeOut"
            }
        }

        $summary.Add("publish: $bridgeOut (bridge, self-contained $sc), $cliOut (CLI)")
    }

    if (-not $SkipContainer) {
        $docker = Get-Command docker -ErrorAction SilentlyContinue
        $dockerUp = $false
        if ($docker) {
            # Windows PowerShell 5.1 turns redirected native stderr into a terminating error under "Stop", so a stopped
            # daemon ("cannot connect to the Docker daemon" on stderr) would abort here instead of skipping the step.
            $savedPreference = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            try {
                & docker version --format "{{.Server.Version}}" *> $null
                $dockerUp = ($LASTEXITCODE -eq 0)
            }
            catch {
                $dockerUp = $false
            }
            finally {
                $ErrorActionPreference = $savedPreference
            }
        }

        if ($dockerUp) {
            Invoke-Native "container ($ImageTag)" "docker" @("build", "-f", (Join-Path $repo "docker/Dockerfile"), "-t", $ImageTag, $repo)
            $summary.Add("container: $ImageTag")
        }
        elseif ($RequireContainer) {
            throw "docker is not available; the container step was required (-RequireContainer)"
        }
        else {
            Write-Warning "docker is not available: container step skipped"
            $summary.Add("container: skipped (no docker)")
        }
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Build summary" -ForegroundColor Cyan
foreach ($line in $summary) {
    Write-Host "  $line"
}
