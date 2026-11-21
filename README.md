# TuneShuttle

**Spotify Connect and AirPlay 2, in both directions, for every speaker in the house.**

TuneShuttle shows up in the Spotify app as a Connect device and plays what you send it on one AirPlay 2 speaker or on a
group of them, in sync. It is also an AirPlay 2 speaker itself: pick "TuneShuttle" in the AirPlay menu of an iPhone,
iPad or Mac and the sound comes out of the same speakers. Finding speakers, pairing, building groups and setting
volumes all happen in a web app that TuneShuttle serves itself.

It is written in C# (.NET 10) and runs as a Windows Service, under systemd on Linux, or in a container.

## Features

- **Spotify Connect to AirPlay 2.** Play, pause, skip, seek and volume from the Spotify app act on the speakers
  (Spotify Premium required).
- **Multi-room groups.** Play to several receivers in sync, with a master volume and a level per room.
- **An AirPlay 2 receiver.** Apple devices can stream to TuneShuttle (ALAC and, with FFmpeg, AAC) and it passes the
  audio on to the selected speakers. The newest source wins: starting Spotify or an Apple device takes over.
- **A web app** for phones, tablets and desktops: player, receivers, groups, settings and live diagnostics, with light
  and dark themes and seven languages.
- **A REST and WebSocket API** for your own tools.
- **Reusable parts.** The AirPlay 2 sender and receiver libraries, an `airplay2` command-line tool and a C# port of
  librespot work on their own.

## How it works

```
 Spotify app ──Connect──┐                           ┌──> AirPlay 2 speaker
                        ├──> TuneShuttle ──AirPlay 2┤
 iPhone / iPad / Mac ───┘      (web app)             └──> group of speakers, in sync
      AirPlay 2
```

TuneShuttle decodes what it receives and re-sends it to the speakers you chose, with a shared clock so rooms stay in
step. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the components and the data flow.

## Quick start

You need a Spotify Premium account, one or more AirPlay 2 speakers on the same network, and the .NET SDK 10.0.

```powershell
./build.ps1 -SkipTests -SkipContainer      # builds into artifacts/bridge
dotnet run --project src/Bridge.Service    # or just try it from the checkout
```

Open `http://localhost:8080`, then pick "TuneShuttle" in the Spotify app. Installing it as a service or in a container,
the firewall and the network setup are in the [installation guide](docs/INSTALL.md).

## What is in the repository

| Part | Where |
|---|---|
| Bridge service and web app | [`src/Bridge.Service`](src/Bridge.Service), [`src/Bridge.Web`](src/Bridge.Web) |
| AirPlay 2 sender library | [`src/Airplay2.Sender`](src/Airplay2.Sender) |
| AirPlay 2 receiver library | [`src/Airplay2.Receiver`](src/Airplay2.Receiver) |
| `airplay2` CLI | [`src/Airplay2.Sender.Cli`](src/Airplay2.Sender.Cli) |
| librespot-dotnet, a C# port of librespot | [`librespot-dotnet/`](librespot-dotnet) |

## Documentation

| Document | What it holds |
|---|---|
| [`docs/INSTALL.md`](docs/INSTALL.md) | Requirements, building, running as a Windows Service, under systemd or in a container, the FFmpeg libraries |
| [`docs/USER-GUIDE.md`](docs/USER-GUIDE.md) | Using the web app, pairing, groups and volumes, the AirPlay receiver |
| [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) | Settings and the REST and WebSocket API |
| [`docs/NETWORK.md`](docs/NETWORK.md) | Ports, firewall and network requirements |
| [`docs/CLI.md`](docs/CLI.md) | The `airplay2` command-line tool |
| [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) | Receiver quirks and common problems |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) | How it is built, and how to work on it |
| [`docs/README.md`](docs/README.md) | All documents, including the protocol specifications |

## Licences

The original code in this repository is released under the MIT licence ([`LICENSE`](LICENSE)). Parts that are ports of
other code retain the licence of the original source: [`librespot-dotnet/`](librespot-dotnet) is a port of librespot
and carries its MIT licence ([`librespot-dotnet/LICENSE`](librespot-dotnet/LICENSE)). The ALAC codec in
`src/Airplay2.Sender/Alac` is a port of Apple's ALAC (Apache-2.0), and the ported files keep that notice.
owntone-server (GPL-2.0) was read only to understand how it synchronizes receivers; none of its code was copied.
The AAC decoder loads FFmpeg's libraries (LGPL-2.1 or later) dynamically through the FFmpeg.AutoGen 7.x bindings
(LGPL-3.0); no FFmpeg code is compiled in and no GPL components are used. The reference checkouts and their licences
are listed in [`docs/REFERENCE-SOURCES.md`](docs/REFERENCE-SOURCES.md).

TuneShuttle is an independent project and is not affiliated with or endorsed by Spotify or Apple.
