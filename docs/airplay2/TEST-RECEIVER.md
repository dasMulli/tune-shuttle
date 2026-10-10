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
# the script's default filter (Category=ShairportLive) also selects PtpLiveTests and BufferedLiveTests, which fail on the
# host network (nqptp owns UDP 319/320, see the corrections below); exclude them here and run them with
# run-ptp-live-tests-in-vm.ps1
./docker/shairport-sync/run-live-tests-in-vm.ps1 -Filter "Category=ShairportLive&FullyQualifiedName!~PtpLiveTests&FullyQualifiedName!~BufferedLiveTests"
./docker/shairport-sync/run-live-tests-in-vm.ps1 -Filter "FullyQualifiedName~Pairing"
```

Correction (2026-10-09, wave 4 gate, from `ptp-master`'s live runs): row "Realtime stream under PTP" holds only when our
grandmaster is **not** in the receiver's namespace. With `--net host` (`run-live-tests-in-vm.ps1`) nqptp already owns UDP
319/320, so `PtpMaster` cannot bind them (`PtpMasterException`). The PTP tests run in a bridge-networked SDK container
that reaches shairport-sync at the bridge gateway (`SPS_HOST=172.17.0.1`):
`./docker/shairport-sync/run-ptp-live-tests-in-vm.ps1` (default filter `FullyQualifiedName~PtpLiveTests&Category=ShairportLive`).
Known limitation of both VM scripts: the build output lives in the container (`/tmp/artifacts`), so `SessionLiveLog` does
not find the repository root and the `artifacts/live/*.log` files of those runs stay in the container; the test output
and `docker logs sps-test` are the record.

Tests that target it read `SPS_HOST` (127.0.0.1) and `SPS_PORT` (7000), return early unless `AIRPLAY_LIVE=1` (the VM script sets it), carry `[Trait("Category", "ShairportLive")]`
and are skipped in the normal `dotnet test` run. A password-protected variant for the 470/PIN path:
`command: -a SPS-Pin -o dummy --password 1234` in a second compose service (`shairport-sync-pin`, profile `pin`; see
the corrections below).

## The Test Speaker ABCD and the fake receiver

- SpeakerA (192.0.2.26:7000, `srcvers 366.0`, features `0x1C340445F8A00`, flags `0x404`): the live target with the
  allowances of WORK-PLAN.md section 4 (discovery and `GET /info` any time; pairing and SETUP/TEARDOWN from wave 3;
  audio only as a 3-second quiet tone from wave 4; the LG TV never).
- `tests/Airplay2.FakeReceiver`: the in-process receiver every test project uses (WORK-PLAN package `fake-receiver`);
  it implements both NTP and the 501 refusal, both TEARDOWN forms, optional `streamID`, `updateInfo` timing, packet
  loss and retransmit requests, so each open question's branches can be exercised deterministically.

## What shairport-sync actually exercised (2026-10-09, final integration)

By the end of wave 8, every ShairportLive test had run against shairport-sync 5.5.2 in the Docker VM, and the final
integration ran all of them again. The table gives the complete list. Logs:
`artifacts/live/2026-10-09-final-shairport-*.log`, `-final-bridge-container-shairport.log` (git-ignored, local only).

| What | Test / run | How it was run | Result |
|---|---|---|---|
| `GET /info` bare and with the stage-1 qualifier | `RtspLiveTests.Shairport_GET_info_bare_and_with_qualifier` | `run-live-tests-in-vm.ps1` (host network) | 200; a 1235-byte bplist with `initialVolume`, `supportedFormats` and `pk` |
| Transient pair-setup M1-M4, encrypted `GET /info` | `PairingLiveTests.Shairport_transient_pairing_then_encrypted_get_info` | host network | Pairing completes; the encrypted `GET /info` answers 200 |
| PIN pair-setup M1-M6 with `--password`, pair-verify on the same and on a new connection | `PairingLiveTests.Shairport_password_pin_pairing_then_verify` | Second, password-protected container on the default bridge network (see below) | Transient is refused (TLV `Authentication`); PIN M1-M6 succeeds and the M6 signature verifies; pair-verify succeeds twice. The connection stays plaintext after verify, which is a shairport-sync gap (SPEC-control.md 3.7 note), not ours. |
| PTP session start-up: session SETUP with `timingPeerInfo`, event channel (`updateInfo` on connect), RECORD, SETPEERS, stream SETUP without `streamID`, `GET_PARAMETER` with the leading blank line, `TEARDOWN {}` with `Connection: close` | `SessionLiveTests.Shairport_ptp_session_startup` | host network | Passes |
| NTP session refusal | (session-streams, wave 3) | host network | `200` with an empty body, not `501` (SPEC-control.md 4.2 note). The sender treats it as a refusal, and `KnownReceiverQuirks` puts shairport-sync on PTP. |
| Our PTP grandmaster as nqptp's master; 0xD7-synced realtime stream (silence into the dummy backend) | `PtpLiveTests.Shairport_follows_our_clock_and_plays_ptp_synced_silence`, `.Shairport_plays_silence_through_the_realtime_transport_under_ptp` | `run-ptp-live-tests-in-vm.ps1` (bridge network) | Pass. nqptp sends two Announces and then nothing (P17); `PtpMaster` keeps the silent peer. |
| Buffered stream (type 103): run-ahead push, flush and re-anchor; through the group player with pause/resume | `BufferedLiveTests.Shairport_plays_a_run_ahead_buffered_stream_with_flush_and_reanchor`, `.Shairport_plays_the_buffered_stream_through_the_group_player` | `run-ptp-live-tests-in-vm.ps1 -Filter "FullyQualifiedName~BufferedLiveTests&Category=ShairportLive"` | Pass. shairport-sync drops one block after each immediate flush (SPEC-audio.md 4.5 note). |
| **The bridge from its container image**: mDNS discovery of shairport-sync, then through the REST API: connect (transient pairing, PTP session with our grandmaster, event channel, RECORD, stream SETUP), receiver volume 20 → 35, disconnect (`TEARDOWN` 200 with `Connection: close`) | Manual, `docker run tune-shuttle:dev` on the default bridge network with `BRIDGE_Bridge__EnableSpotify=false`, then `curl` against `/api/receivers/{id}/connect`, `/volume`, `/disconnect` | Default bridge network | All steps 200; state *Connected*, then *Available*. Both the image's own mDNS browser and shairport-sync's Avahi are on the bridge network, so discovery works there. |

**Not exercised by shairport-sync:** the realtime stream under NTP (shairport-sync refuses it; the SpeakerA and the
fake receiver cover it); audible output and latency (dummy backend); and retransmit-request handling, which these runs
did not check (the fake receiver and the SpeakerA, probe P14, cover it).

**Running the tests: corrections to the sections above.**

- `PtpLiveTests` and `BufferedLiveTests` must run in **separate** `dotnet test` invocations. xunit runs test classes
  in parallel, and both start a `PtpMaster` on UDP 319/320 in the same process; a combined filter fails with
  `PtpMasterException: ... UDP 319 ... in use`. The host-network script must exclude both classes, because there
  nqptp owns 319/320:
  `run-live-tests-in-vm.ps1 -Filter "Category=ShairportLive&FullyQualifiedName!~PtpLiveTests&FullyQualifiedName!~BufferedLiveTests"`.
- The password variant runs as a second container on the default bridge network. That container has its own
  network namespace, so its nqptp and port 7000 do not clash with `sps-test`. The test container reaches it by its
  bridge address. Docker 29 removed the top-level `.NetworkSettings.IPAddress`; read the address per network.
  Since the final review this is automated: compose.yaml has the service `shairport-sync-pin` (container `sps-pin`,
  `--password 1234`, compose profile `pin`), and `run-live-tests-in-vm.ps1` starts it, reads its address with
  `docker inspect` and passes `SPS_PIN_HOST`, `SPS_PIN_PORT` and `SPS_PASSWORD`. Without them
  `Shairport_password_pin_pairing_then_verify` returns early, is **reported as passed** (xunit 2.9 has no dynamic
  skip) and writes `SKIPPED (returned early ...)` to its test output; the script prints a warning when it cannot
  resolve `sps-pin`. Count the receiver's connections in `docker logs sps-test` / `docker logs sps-pin` to see what a
  green run really exercised. Stop both with `docker compose -f docker/shairport-sync/compose.yaml --profile pin down`.
  The manual equivalent:

  ```bash
  docker run -d --name sps-pin --cap-add SYS_NICE mikebrady/shairport-sync:latest -a SPS-Pin -o dummy --password 1234
  IP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' sps-pin)
  docker run --rm -v "<repo>:/repo" -v airplay2-nuget:/root/.nuget/packages -e AIRPLAY_LIVE=1 \
    -e SPS_PIN_HOST=$IP -e SPS_PASSWORD=1234 -w /repo mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet test tests/Airplay2.Sender.Tests/Airplay2.Sender.Tests.csproj \
    --filter "FullyQualifiedName~Shairport_password_pin_pairing_then_verify" \
    -p:UseArtifactsOutput=true -p:ArtifactsPath=/tmp/artifacts
  docker stop sps-pin && docker container remove sps-pin
  ```
- Row "mDNS discovery from the Windows host: no" still holds for the Windows host. Containers on Docker's bridge
  network, however, do see shairport-sync's mDNS announcement (at the gateway address `172.17.0.1`), which is how
  the bridge image found it.
