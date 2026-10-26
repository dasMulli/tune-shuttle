<#
.SYNOPSIS
  Puts the FFmpeg libraries the AAC decoder needs (avcodec, avutil, swresample of the pinned FFmpeg major) into a folder,
  for the Windows bundle and for the decode tests (docs/airplay2/SPEC-receiver.md 7.2, Section 10).

.DESCRIPTION
  FFmpeg.AutoGen 7.1.1 (Directory.Packages.props) binds FFmpeg 7.1: avcodec-61.dll, avutil-59.dll, swresample-5.dll.
  This script downloads one pinned LGPL *shared* win64 build of FFmpeg 7.1 from the BtbN/FFmpeg-Builds GitHub releases
  (a month-end autobuild, which that project keeps), checks the release's published checksums.sha256 against the hash
  pinned below, verifies the downloaded archive against it, and extracts only:

    avcodec-61.dll, avutil-59.dll, swresample-5.dll   (bin/ of the archive)
    ffmpeg-LICENSE.txt                                 (the archive's LICENSE.txt: the LGPL terms travel with the DLLs)
    ffmpeg-build.txt                                   (marker: source URL and SHA-256; makes reruns no-ops)

  No executables, no GPL components (the "lgpl" build variant), nothing else from the archive. The archive is cached in
  -CacheDirectory so a rerun with a different -Destination does not download again.

  Idempotent: when -Destination already holds the three DLLs and a marker for the pinned build, nothing is downloaded
  or written. -Force re-extracts.

  Works in Windows PowerShell 5.1 and PowerShell 7. Run from anywhere; default paths are relative to the repository.

.PARAMETER Destination
  Folder that receives the DLLs. Default: artifacts/ffmpeg (git-ignored; the decode tests look there).

.PARAMETER CacheDirectory
  Folder for the downloaded archive. Default: artifacts/ffmpeg-cache (git-ignored).

.PARAMETER Force
  Re-extract even when the destination is up to date.

.EXAMPLE
  ./tools/get-ffmpeg.ps1                                  # into artifacts/ffmpeg, for the tests
.EXAMPLE
  ./tools/get-ffmpeg.ps1 -Destination artifacts/bridge    # what build.ps1 does after publish
#>
[CmdletBinding()]
param(
    [string]$Destination,
    [string]$CacheDirectory,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Windows PowerShell 5.1 does not set $PSScriptRoot for parameter defaults, so the defaults are resolved here.
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $repo
if (-not $Destination) { $Destination = Join-Path $repo "artifacts/ffmpeg" }
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $repo "artifacts/ffmpeg-cache" }

# The pinned build. Change all four together, and re-check the FFmpeg major against the FFmpeg.AutoGen pin
# (ffmpeg.LibraryVersionMap) and the licence note in docs/REFERENCE-SOURCES.md.
$release = "autobuild-2026-07-31-14-10"
$asset = "ffmpeg-n7.1.5-12-g1fdbca85aa-win64-lgpl-shared-7.1.zip"
$sha256 = "0f376f96fb38554ccefb1b2ae9c7c6a7b351f0e60a372b38262c320e8392c5d0"
$libraries = @("avcodec-61.dll", "avutil-59.dll", "swresample-5.dll")

$baseUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$release"
$assetUrl = "$baseUrl/$asset"
$checksumsUrl = "$baseUrl/checksums.sha256"
$marker = "ffmpeg-build.txt"
$markerText = "FFmpeg 7.1 LGPL shared build for TuneShuttle (docs/airplay2/SPEC-receiver.md 7.2)`r`nsource: $assetUrl`r`nsha256: $sha256`r`nfiles: $($libraries -join ', '), ffmpeg-LICENSE.txt`r`n"

function Test-UpToDate {
    param([string]$Folder)
    $markerPath = Join-Path $Folder $marker
    if (-not (Test-Path $markerPath)) { return $false }
    if ((Get-Content -Raw $markerPath) -ne $markerText) { return $false }
    foreach ($library in $libraries) {
        if (-not (Test-Path (Join-Path $Folder $library))) { return $false }
    }
    return $true
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

if (-not $Force -and (Test-UpToDate $Destination)) {
    Write-Host "get-ffmpeg: $Destination is up to date ($asset)"
    return
}

# Windows PowerShell 5.1 defaults to TLS 1.0/1.1, which GitHub refuses; the progress bar makes downloads very slow there.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$ProgressPreference = "SilentlyContinue"

New-Item -ItemType Directory -Force $CacheDirectory | Out-Null
$CacheDirectory = (Resolve-Path $CacheDirectory).Path
$archive = Join-Path $CacheDirectory $asset

# 1. The published checksum must equal the pinned one: a re-uploaded asset under the same name fails here.
$checksums = (Invoke-WebRequest -UseBasicParsing -Uri $checksumsUrl).Content
if ($checksums -is [byte[]]) { $checksums = [Text.Encoding]::ASCII.GetString($checksums) }
$published = $null
foreach ($line in ($checksums -split "`n")) {
    $parts = $line.Trim() -split "\s+", 2
    if ($parts.Count -eq 2 -and $parts[1].TrimStart("*") -eq $asset) {
        $published = $parts[0].ToLowerInvariant()
    }
}
if ($null -eq $published) {
    throw "get-ffmpeg: $asset is not listed in $checksumsUrl"
}
if ($published -ne $sha256) {
    throw "get-ffmpeg: the published checksum of $asset ($published) differs from the pinned one ($sha256)"
}

# 2. Download (or reuse the cached archive) and verify it.
if ((Test-Path $archive) -and ((Get-Sha256 $archive) -ne $sha256)) {
    Remove-Item -Force $archive
}
if (-not (Test-Path $archive)) {
    Write-Host "get-ffmpeg: downloading $assetUrl"
    $partial = "$archive.partial"
    Invoke-WebRequest -UseBasicParsing -Uri $assetUrl -OutFile $partial
    $actual = Get-Sha256 $partial
    if ($actual -ne $sha256) {
        Remove-Item -Force $partial
        throw "get-ffmpeg: checksum mismatch for $asset (got $actual, expected $sha256)"
    }
    Move-Item -Force $partial $archive
}

# 3. Extract only the three libraries and the licence.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $wanted = @{}
    foreach ($library in $libraries) { $wanted[$library] = $library }
    $found = @{}
    foreach ($entry in $zip.Entries) {
        $segments = $entry.FullName -split "/"
        $name = $segments[-1]
        $target = $null
        if ($segments.Count -eq 3 -and $segments[1] -eq "bin" -and $wanted.ContainsKey($name)) {
            $target = $name
        }
        elseif ($segments.Count -eq 2 -and $name -eq "LICENSE.txt") {
            $target = "ffmpeg-LICENSE.txt"
        }
        if ($null -ne $target) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $Destination $target), $true)
            $found[$target] = $true
        }
    }
    foreach ($required in ($libraries + "ffmpeg-LICENSE.txt")) {
        if (-not $found.ContainsKey($required)) {
            throw "get-ffmpeg: $required is missing from $asset"
        }
    }
}
finally {
    $zip.Dispose()
}

[IO.File]::WriteAllText((Join-Path $Destination $marker), $markerText)
Write-Host "get-ffmpeg: $($libraries -join ', ') -> $Destination"
