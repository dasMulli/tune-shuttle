# Local test receiver: evaluation and setup

Status: 2026-10-09. Question: can shairport-sync (AirPlay 2) run locally as a test receiver on this Windows host, and
what would it test? Short answer: it runs in Docker Desktop and is reachable **from inside the Docker VM**, not from
Windows; it refuses the NTP-timed realtime stream that v1 uses; so it is a secondary target for the pairing/RTSP/event
paths and for the PTP/buffered path, while the in-process fake receiver plus the SpeakerA speaker remain the primary
targets (SPEC.md D16).

## What was tried

| Step | Result |
|---|---|
| `docker pull mikebrady/shairport-sync:latest` | 5.5.2 AirPlay 2 build with nqptp, OpenSSL, Avahi, ALSA/PulseAudio/PipeWire and the `dummy`, `stdout`, `pipe` backends (`shairport-sync -V`). |
| `docker run -d --name sps-eval --net host --cap-add SYS_NICE mikebrady/shairport-sync:latest -a SPS-Eval -o dummy` | Starts dbus, avahi, nqptp and shairport-sync; no `/dev/snd` needed with `-o dummy`. |
| From the Windows host: `GET /info` to `127.0.0.1:7000`; mDNS browse on the LAN | **Connection refused**; the service is not advertised on the LAN (`mdns_browse.py` sees only the SpeakerA). Docker Desktop's host networking shares the Linux VM's namespace, which is NATed; its optional "host networking" bridge to Windows is not enabled on this machine and would still not carry mDNS multicast or inbound UDP to the VM. |
| From a second host-networked container in the VM (`alpine`, `ss -ltnup`; `nc 127.0.0.1 7000`) | TCP 7000 open, UDP 319/320 (nqptp) and 5353 (avahi) bound; `GET /info RTSP/1.0` answers `200 OK`, `Server: AirTunes/366.0`, a 1235-byte bplist (shairport's full `/info` with `initialVolume`, `supportedFormats`, `pk`). |
| From `mcr.microsoft.com/dotnet/sdk:10.0` with `--net host` | `dotnet 10.0.401` runs and reaches `127.0.0.1:7000`: the sender's test suite can run inside the VM against the receiver, with TCP, UDP and multicast all in one namespace. |
| WSL2 Ubuntu (26.04.1) | Running, NAT mode (`eth0` 172.27.x.x), no gcc/g++ installed, no shairport-sync. Switching to mirrored networking (`.wslconfig` `networkingMode=mirrored`) would expose a WSL-hosted receiver to the Windows host and the LAN, but it is a global setting that also changes Docker Desktop's networking, requires `wsl --shutdown`, and a native build of shairport-sync AirPlay 2 needs ~15 build dependencies plus nqptp with exclusive UDP 319/320. Not done without the user's decision. |

## What shairport-sync can and cannot test for this project

| Area | Usable? | Why |
|---|---|---|
| Transient and PIN pairing, pair-verify, encrypted control channel | yes (inside the VM) | pair_ap is the reference implementation; a 470/TLV error path can be provoked with `--password`. |
| Session SETUP, event channel (`updateInfo` on connect), RECORD, stream SETUP, SET_PARAMETER, GET_PARAMETER, FLUSH, TEARDOWN, feedback | yes | Full AirPlay 2 receiver; no `streamID` in the stream SETUP response (SPEC.md C11). |
| **Realtime stream with NTP timing (v1 path, SPEC.md D1)** | **no** | shairport-sync answers 501 to `timingProtocol: NTP` (SPEC-control 4.2). |
| Realtime stream under PTP (0xD7 syncs), buffered stream, PTP grandmaster | yes (inside the VM) | nqptp runs in the same namespace; UDP 319/320 reachable. |
| mDNS discovery from the Windows host | no | VM multicast does not reach the host or the LAN. |
| Retransmit requests, timing requests to the sender | yes inside the VM, no from Windows | UDP back to the sender only works in the shared namespace. |
| Audible output / latency measurement | no | `dummy` backend; use the SpeakerA with a microphone (P7). |

Conclusion: worth keeping as a **secondary** target for the control plane and for the PTP/buffered packages
(`ptp-master`, `buffered-stream`), run through the VM script below; not a substitute for the fake receiver (which tests the NTP
path and every quirk switch deterministically) or the SpeakerA (the real Apple-SDK behaviour, SPEC.md section 4).

## How to start it

```powershell
# start / stop the receiver (host networking inside the Docker VM, dummy output, verbose + statistics)
docker compose -f docker/shairport-sync/compose.yaml up -d
docker logs -f sps-test
docker compose -f docker/shairport-sync/compose.yaml down

# run the sender's live tests against it from inside the VM (mounts the repo into a host-networked SDK container)
./docker/shairport-sync/run-live-tests-in-vm.ps1            # Category=ShairportLive
./docker/shairport-sync/run-live-tests-in-vm.ps1 -Filter "FullyQualifiedName~Pairing"
```

Tests that target it read `SPS_HOST` (127.0.0.1) and `SPS_PORT` (7000), return early unless `AIRPLAY_LIVE=1` (the VM script sets it), carry `[Trait("Category", "ShairportLive")]`
and are skipped in the normal `dotnet test` run. A password-protected variant for the 470/PIN path:
`command: -a SPS-Test -o dummy --password 1234` in a second compose service.

## The Test Speaker ABCD and the fake receiver

- SpeakerA (192.0.2.26:7000, `srcvers 366.0`, features `0x1C340445F8A00`, flags `0x404`): the live target with the
  allowances of WORK-PLAN.md section 4 (discovery and `GET /info` any time; pairing and SETUP/TEARDOWN from wave 3;
  audio only as a 3-second quiet tone from wave 4; the LG TV never).
- `tests/Airplay2.FakeReceiver`: the in-process receiver every test project uses (WORK-PLAN package `fake-receiver`);
  it implements both NTP and the 501 refusal, both TEARDOWN forms, optional `streamID`, `updateInfo` timing, packet
  loss and retransmit requests, so each open question's branches can be exercised deterministically.
