# Installing and running

How to build, install and run TuneShuttle as a Windows Service, under systemd or in a container. Back to the [overview](../README.md).

## Requirements

- A **Spotify Premium** account (librespot, like every third-party Connect client, works only with Premium).
- One or more **AirPlay 2** receivers. AirPlay 1 is not supported. See [Receivers and troubleshooting](TROUBLESHOOTING.md) for what has been tested.
- A machine on the **same network** as the receivers and the phones running Spotify, with inbound UDP and mDNS
  allowed through its firewall (see [Firewall and network](NETWORK.md)). Windows (x64) or Linux (x64 or arm64).
- To build: the .NET SDK 10.0. To run a framework-dependent build: the ASP.NET Core 10 runtime (a self-contained
  build needs nothing). Docker for the container image.
- Optional, for AAC from Apple devices: the FFmpeg 7.1 libraries (see "The ffmpeg libraries" below). Without them the
  AirPlay receiver still works, with ALAC only.

## Build

Build the bundle from the repository root first:

```powershell
./build.ps1 -SkipTests -SkipContainer                 # framework-dependent, win-x64, into artifacts/bridge
./build.ps1 -SkipTests -SkipContainer -SelfContained  # if the target machine has no .NET 10 runtime
```

Then open `http://<host>:8080` in a browser. To just try it from a checkout: `dotnet run --project src/Bridge.Service`.

## As a Windows Service

```powershell
# elevated PowerShell, after ./build.ps1:
./install/windows/install-service.ps1 -WebPort 8080 -DataDir "$env:ProgramData\TuneShuttle"
./install/windows/install-service.ps1 -Uninstall
```

The script registers the service `TuneShuttle` (automatic start) from `artifacts/bridge` (`-PublishDir` to change),
passes the data directory and the web port on its command line, and adds inbound firewall rules for the Private and
Domain profiles: one for `bridge.exe` (it covers the ports chosen at run time), UDP 5353 for mDNS, the web port, and
for the AirPlay receiver "TuneShuttle AirPlay RTSP" (TCP 7000) and "TuneShuttle PTP" (UDP 319-320). A reinstall
replaces the rules instead of adding duplicates. The ffmpeg DLLs that `build.ps1` puts into `artifacts/bridge` are
installed with the rest.

An install from before the rename is removed with its firewall rules. Its data stays in
`%ProgramData%\AirplayBridge`: pass that folder as `-DataDir` (or move it) to keep receivers, groups, pairings and the
Spotify login.

## On Linux with systemd

```sh
dotnet publish src/Bridge.Service -c Release -r linux-x64 -o out     # or linux-arm64; add --self-contained without a .NET runtime
sudo useradd --system --home-dir /var/lib/tune-shuttle tune-shuttle
sudo mkdir -p /opt/tune-shuttle /var/lib/tune-shuttle && sudo cp -r out/. /opt/tune-shuttle/
sudo chown tune-shuttle /var/lib/tune-shuttle
sudo cp install/linux/tune-shuttle.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now tune-shuttle
```

The unit ([`install/linux/tune-shuttle.service`](../install/linux/tune-shuttle.service)) runs `/opt/tune-shuttle/bridge` as
the user `tune-shuttle` with `Type=notify`, keeps the data in `/var/lib/tune-shuttle` and listens on port 8080. It
grants `CAP_SYS_NICE` (the pacer's priority) and `CAP_NET_BIND_SERVICE` (UDP 319/320 for PTP are privileged ports).
For AAC install the FFmpeg 7.1 libraries (see "The ffmpeg libraries" below). Logs go to the journal
(`journalctl -u tune-shuttle`).

## In a container (Kubernetes)

```sh
docker build -f docker/Dockerfile -t tune-shuttle .
docker network create -d macvlan --subnet=192.0.2.0/24 --gateway=192.0.2.1 -o parent=eth0 lan
docker run -d --network lan --ip 192.0.2.60 -v bridge-data:/data tune-shuttle
```

The image runs `bridge.dll` on the ASP.NET Core 10 runtime on Debian 13 (trixie) with the FFmpeg 7.1 libraries for
AAC, keeps its data in `/data` (mount a volume there) and listens on 8080 (web) and on TCP 7000 and UDP 319/320 (the
AirPlay receiver). It builds for `amd64` and `arm64` (`docker buildx build --platform linux/arm64 ...`, see
[`docs/DEVELOPMENT.md`](DEVELOPMENT.md#container-image)).

**The container needs its own address on the receivers' LAN.** Give it a macvlan interface (in Kubernetes for
example with Multus); that makes mDNS and inbound UDP work without host networking. A NAT-ed bridge network does not
work, because receivers send UDP back to the bridge (NTP timing requests, retransmit requests, PTP), Spotify must
reach the Zeroconf port, and Apple devices must reach the AirPlay receiver.

## The ffmpeg libraries

Apple devices usually send AAC, which .NET cannot decode. TuneShuttle loads FFmpeg's `avcodec`, `avutil` and
`swresample` libraries of FFmpeg **7.1** (the major the FFmpeg.AutoGen 7.1.1 binding targets) at run time, first from
the folder of the bridge, then from the system's library path (or only from `Bridge:Airplay:FfmpegPath` when that is
set). Without them the receiver starts anyway, offers ALAC only and logs the reason once (Diagnostics: *Decoder*).

| Platform | How to get them |
|---|---|
| Windows | `./build.ps1` runs `tools/get-ffmpeg.ps1`, which downloads one pinned LGPL shared build, verifies its published checksum and copies `avcodec-61.dll`, `avutil-59.dll` and `swresample-5.dll` next to `bridge.exe` (`-SkipFfmpeg` leaves them out); the install script installs them with the rest. By hand: `./tools/get-ffmpeg.ps1 -Destination artifacts/bridge`. |
| Debian 13 (trixie), Ubuntu 25.04 or later | `sudo apt-get install --no-install-recommends libavcodec61 libavutil59 libswresample5`. Ubuntu 24.04 ships only FFmpeg 6.1 (`libavcodec60`), which does not fit; there the receiver runs ALAC-only unless you provide an FFmpeg 7.1 build. |
| Container | Already in the image (the Debian 13 packages). |
| Elsewhere | Any FFmpeg 7.1 shared build; set `Bridge:Airplay:FfmpegPath` to the directory that holds the libraries. |
