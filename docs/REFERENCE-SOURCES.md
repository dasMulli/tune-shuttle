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
