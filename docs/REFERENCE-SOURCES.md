# Reference sources

Cloned into `reference/` (git-ignored). Re-create with the commands below.

| Project   | URL                                            | Commit                                     | License |
|-----------|------------------------------------------------|--------------------------------------------|---------|
| librespot | https://github.com/librespot-org/librespot.git | e023adbbf017ae1fc10d01531dbe50c409786f2d (v0.8.0, 2026-10-05) | MIT |

```
git clone https://github.com/librespot-org/librespot.git reference/librespot
git -C reference/librespot checkout e023adbbf017ae1fc10d01531dbe50c409786f2d
```

## AirPlay 2 sender references (cloned 2026-10-09)

| Project | URL | Commit | License | Use |
|---|---|---|---|---|
| pyatv | https://github.com/postlund/pyatv.git | b277a4c (2026-06-19) | MIT | HAP pairing, RAOP/AirPlay sender, primary code reference |
| shairport-sync | https://github.com/mikebrady/shairport-sync.git | 01078ad (2026-09-18) | MIT (see COPYING) | AirPlay 2 receiver: what a sender must send, PTP/NTP timing, buffered audio |
| airplay2-receiver | https://github.com/openairplay/airplay2-receiver.git | 6c343d3 (2023-10-03) | see repo | Python AP2 receiver with protocol notes, transient pairing |
| owntone-server | https://github.com/owntone/owntone-server.git | 4fa5dcc (2026-09-24) | GPL-2.0 | Read for understanding multi-receiver sync only. Do not transcribe code from it. |
| alac | https://github.com/macosforge/alac.git | c38887c (2016-05-11) | Apache-2.0 | ALAC encoder to port |

Test receivers on the LAN (2026-10-09): a third-party AirPlay 2 speaker (srcvers 366.0, features 0x445F8A00,0x1C340)
and a TV receiver that requires PIN pairing (srcvers 377.40.00). Agents must not target the TV: AirPlay wakes it.

## Native libraries and bindings (AirPlay receiver, SPEC-receiver.md R4, 7.2; checked 2026-10-26)

| Component | Version | Licence | How it is used |
|---|---|---|---|
| FFmpeg.AutoGen (NuGet) | 7.1.1, pinned in `Directory.Packages.props` | **LGPL-3.0** (the `LICENSE.txt` inside the 7.1.1 package) | P/Invoke bindings, `DynamicallyLoadedBindings`; targets FFmpeg 7.1 (`avcodec-61`, `avutil-59`, `swresample-5`) |
| FFmpeg libavcodec, libavutil, libswresample | 7.1 (Windows: BtbN/FFmpeg-Builds `autobuild-2026-07-31-14-10`, `ffmpeg-n7.1.5-12-g1fdbca85aa-win64-lgpl-shared-7.1.zip`, SHA-256 `0f376f96fb38554ccefb1b2ae9c7c6a7b351f0e60a372b38262c320e8392c5d0`) | FFmpeg is LGPL-2.1-or-later; this build is configured with `--enable-version3`, so it is distributed under **LGPL-3.0** (the archive's `LICENSE.txt`); the "lgpl" variant has no GPL components | loaded dynamically at run time, never linked or committed; `tools/get-ffmpeg.ps1` copies the three DLLs and the build's `LICENSE.txt` (as `ffmpeg-LICENSE.txt`) next to `bridge.exe`; Linux/container use the distribution's packages of the same major |

The FFmpeg.AutoGen repository relicensed later majors under MIT (9.0.1.1, current on nuget.org on 2026-10-26, ships an MIT
`LICENSE.txt`); 7.1.1 stays LGPL-3.0. Re-verify both licence notes whenever the pin or the FFmpeg major changes, and change
`tools/get-ffmpeg.ps1` (release, asset, hash, DLL names) together with the pin. The AAC test vector
(`docs/airplay2/test-vectors/aac-44100-stereo-1024.json`) was encoded with the ffmpeg CLI; the command and generator are next to it.
