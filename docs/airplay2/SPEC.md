# AirPlay 2 sender specification: index, decisions and receiver findings

The index of the AirPlay 2 sender specification, consolidated on 2026-10-09 from two research parts (citations
re-verified against the reference checkouts the same day). It records the decisions the implementation follows (D1-D17),
the contradictions found between the two parts and how they were resolved, every receiver question with its probe and
the recorded live result, and the defaults that follow from them. How the code is organised around this is in
[`../ARCHITECTURE.md`](../ARCHITECTURE.md); the local test receiver in [`TEST-RECEIVER.md`](TEST-RECEIVER.md); the checks
that still need a person in [`MANUAL-TESTS.md`](MANUAL-TESTS.md).

## 1. The parts

| Document | Covers | Read it for |
|---|---|---|
| [`SPEC-control.md`](SPEC-control.md) | Discovery (mDNS TXT records, feature/flag bits, the decision procedure 1.6), the RTSP-over-TCP transport and HAP channel encryption (2), pairing (3: transient, PIN/password, pair-verify, persistence), session establishment (4: SETUP, event channel, RECORD, SETPEERS, stream SETUP, FLUSH, TEARDOWN, feedback), SET_PARAMETER (5: volume, DMAP, artwork, progress), sequence diagrams (6), receiver questions 7.1-7.15 | Everything that travels over the control connection and the event channel |
| [`SPEC-receiver.md`](SPEC-receiver.md) | TuneShuttle as an AirPlay 2 **receiver** (written 2026-10-26): decisions R1-R16, advertising and `/info`, the control plane and audio plane in the receiver role, the shared PTP port and the slave, the ALAC and AAC decoders, the playout rule, the source switch between Spotify and AirPlay, API/web/store/deployment changes, tests, live probes RP1-RP12, open questions RQ1-RQ11, implementation waves | Everything about accepting audio from Apple devices |
| [`SPEC-audio.md`](SPEC-audio.md) | The realtime stream (1: SETUP keys, RTP layout, payload encryption, ALAC framing, timestamps and latency), NTP timing, sync and control packets (2), group playback (3), the buffered stream and PTP (4), the ALAC encoder port and its vectors (5), volume on the wire (6), metadata timing (7), the sender pipeline fed by librespot (8), receiver questions Q1-Q13 | Everything that moves audio and time |
| `test-vectors/` | `alac-44100-s16-2ch-352.json` (bit-exact ALAC packets from Apple's reference encoder), `srp-3072-sha512.json` (SRP-6a with HomeKit parameters, generated with an independent implementation), `info-response.bplist` and `info-response-stage1.bplist` (raw GET /info bodies captured from receiver A, a third-party AirPlay 2 speaker (srcvers 366.0, features 0x445F8A00,0x1C340), with name, model and manufacturer replaced by synthetic values of the same length: `Test Speaker ABCD`, `Example Audio Maker GmbH`), generator sources | What the unit tests check against |
| `probes/` | `mdns_browse.py`, `get_info.py`, `gen_srp_vectors.py`, raw outputs | How the live data was obtained |

Reference sources and their licences: `docs/REFERENCE-SOURCES.md`. Citations inside the parts point into `reference/`.

## 2. Decisions for v1

Numbered so the code can refer to them (`SPEC.md D4`).

| # | Decision | Why | Where it is specified |
|---|---|---|---|
| D1 | **Realtime stream (type 96), NTP timing, ALAC 44.1 kHz/16-bit/stereo, 352 frames per packet, sender latency `L = 77175` frames.** The stream layer is pluggable in *two* dimensions: stream type (96 realtime / 103 buffered) and timing protocol (NTP / PTP): `RealtimeStreamTransport` sends 0xD4 syncs and answers 0xD2 without an `IPtpMaster`, and 0xD7 syncs with one. The gate was the **control-only part of probe P1** (session SETUP with NTP accepted with 200 + `eventPort`, and 0xD2 requests arriving at `timingPort` within 2 s of the stream SETUP, `ITimingResponder.RepliesSent > 0`), which needs no audio; receiver A passed it, so it runs under NTP. A receiver that refuses NTP (501, or 200 with an empty body like shairport-sync) runs the realtime stream under PTP (0xD7 syncs, `SETPEERS`, the sender as grandmaster, `PtpMaster`). The buffered stream stays the second implementation (needs AAC-LC or ALAC 48/24 for Apple-SDK receivers, A-Q7). | The project chose realtime + NTP. Both parts found that the project plan's rationale ("OwnTone syncs via NTP") describes OwnTone before `libairptp`; current OwnTone uses PTP for any receiver with feature bit 41, which both LAN receivers set, and shairport-sync refuses NTP sessions. The decision stood because NTP needs no PTP daemon and pyatv proves it against Apple receivers; the gate makes the risk explicit. `L = 77175` is what Apple senders put on the wire and what shairport-sync expects (+11025 receiver-side = 2.0 s). | SPEC-audio 1.1, 1.5, 2, 3.4, 4.4; SPEC-control 4.2, 7.1 |
| D2 | **Transient pairing first, PIN pairing through the web app, pair-verify on every reconnect with persisted keys.** M1 method byte 0 (`PairSetup`); M6 accessory signature verified (pyatv skips it, pair_ap enforces it); `X-Apple-HKP` sent on every pairing POST; one controller identity (Ed25519 seed + UUID) per installation, stored in SQLite; a 470 or TLV error on transient M1 falls back to the PIN flow; a pair-verify failure deletes the stored pairing. | The web app is where a person can enter a PIN; receiver A advertises bit 48 with no PIN flag, receiver B (the PIN TV) requires a PIN (flag bit 9). | SPEC-control 3, 6.1, 6.2 |
| D3 | **Groups from v1, sender is the single master clock.** One `Timeline` per group (one sequence/timestamp counter, one ALAC encoder output, one retransmit ring per receiver), one timing responder and one control socket per process, per-receiver encryption keys and nonce counters, per-receiver `offset_frames` applied only to that receiver's sync packets. A stable `groupUUID` per group goes into every member's session SETUP with `groupContainsGroupLeader: false`. Joining mid-track: initial `0x90` sync + marker packet, no catch-up. | Groups are the point of the project; SPEC-audio 3 (OwnTone's model). | SPEC-audio 3, SPEC-control 4.2 |
| D4 | **`shk` = the first 32 bytes of the pairing shared secret** (64-byte SRP key for transient, 32-byte X25519 secret for verified). Random 32 bytes remain a per-session option for probe A-Q4. | Resolves contradiction C1 below; OwnTone-proven against Apple-SDK receivers. | SPEC-audio 1.3, SPEC-control 4.4 |
| D5 | **Start-up order**: `GET /info` → pairing → session `SETUP` → connect event channel → `RECORD` → (`SETPEERS`, PTP only) → stream `SETUP` → `SET_PARAMETER volume` → first `0x90` sync + marker packet + audio → DMAP text, artwork, progress → feedback every 2 s and progress every 25 s. | iOS order with OwnTone's volume placement (Sonos ignores an earlier volume). Probe P3 verifies the alternatives. | SPEC-control 4, 6.1 |
| D6 | **Wire hygiene**: `RTSP/1.0` on every request (receiver A echoes either token); `CSeq`, `User-Agent: AirPlay/550.10`, `DACP-ID`, `Active-Remote`, `Client-Instance` on every request; one request in flight per connection; responses matched by CSeq with FIFO fallback; 25 s connect timeout, 10 s request timeout. | SPEC-control 2.1-2.2 and the probe (one 5 s connect to receiver A timed out, 25 s succeeded). | SPEC-control 2 |
| D7 | **TEARDOWN always carries a bplist**: per-stream `{streams:[{streamID,type}]}` when a `streamID` was returned, then `{}`; then close the event channel and the socket. | pyatv's body-less TEARDOWN is a no-op on shairport-sync. | SPEC-control 4.9, 6.4 |
| D8 | **Retransmit replies**: wrapped `0xD6` on the control port by default; raw packet on the data port as a per-receiver quirk. History 1000 packets per receiver, cleared on TEARDOWN not FLUSH. | What iOS does and what every reference receiver parses; OwnTone's amplifier case is the quirk. | SPEC-audio 2.4 |
| D9 | **RTP payload nonce** = 4 zero bytes + 64-bit little-endian packet counter starting at 0, never reset under one key; AAD = header bytes 4..11; retransmissions byte-identical. Control/event channel frames use the same nonce rule with per-direction counters and the 2-byte length as AAD; **sent** frames carry at most 1024 bytes of plaintext (fixed), **received** frames may carry up to 65 535. | SPEC-audio 1.3 (OwnTone's 16-bit sequence number would reuse nonces every 8.7 minutes); SPEC-control 2.5 (pyatv's note that receivers use any frame size that fits 16 bits). | SPEC-audio 1.3, SPEC-control 2.5 |
| D10 | **Volume**: level 0..100 → `-30 + 0.3 * level` dB, level 0 → `-144.0`; sent after the stream SETUP and on every change (coalesced to ≤ 10/s); receiver-side changes are taken from any event-channel message that carries a level, with a `GET_PARAMETER volume` poll every 3 s as the fallback until probe P4 shows what receiver A sends. | The group volume model (`../ARCHITECTURE.md`); no reference source shows a receiver-initiated volume message. | SPEC-control 5.1, SPEC-audio 6 |
| D11 | **Sync packets**: type `0xD4`, flags field `0x0007`, one per second, `now_frame = next_ts - L (- offset_i)`, the first one after start/join/flush has byte 0 = `0x90` and precedes the first audio packet, which carries the marker bit. | SPEC-audio 2.3; A-Q9 checks the flags value. | SPEC-audio 2.3, 3.2 |
| D12 | **Metadata**: DMAP `mlit {minm, asar, asal, astm}` + JPEG artwork ≤ 200 KiB + `progress: start/current/end`, all with `RTP-Info: rtptime=<track start frame>`, sent as soon as the first packet of a track is queued; re-sent on track change; progress alone every 25 s. Bit-50 (`POST /command` now-playing) receivers are out of scope for v1. | SPEC-control 5, SPEC-audio 7. | SPEC-control 5, SPEC-audio 7 |
| D13 | **Crypto implementation**: BCL `ChaCha20Poly1305`, `HKDF`, `SHA512`, `BigInteger` (SRP-6a hand-written); BouncyCastle.Cryptography 2.7.0 only for X25519 and Ed25519, which .NET 10 lacks; ChaCha20-Poly1305 falls back to BouncyCastle where the OS lacks it. Verified against RFC 7748/8032/8439/5869 vectors and SRP vectors from an independent implementation. | .NET 10 has no X25519/Ed25519 type (`../ARCHITECTURE.md`, "Design decisions"). | `src/Airplay2.Sender/Crypto` |
| D14 | **bplist**: hand-written reader/writer (Apple-style object table, ordered dictionaries), verified against receiver A's raw responses and Python's plistlib in both directions. No XML plist support. | Receivers only accept `bplist00`. | `src/Airplay2.Sender/Plist` |
| D15 | **ALAC**: Apple's encoder ported bit-exact (search loops included), decoder ported for tests and the fake receiver; the magic cookie is a protocol constant. | SPEC-audio 5. | `src/Airplay2.Sender/Alac` |
| D16 | **Test receivers**: the in-process fake receiver (receiver side of pairing, RTSP, events, UDP, ALAC decode) is the primary test target; receiver A is the live target under the live-device rules (`../DEVELOPMENT.md`); shairport-sync in Docker is a secondary target for pairing/RTSP/PTP paths only (it refuses NTP sessions and is unreachable from the Windows host, see `TEST-RECEIVER.md`); receiver B (the PIN TV) is never contacted. | TEST-RECEIVER.md. | TEST-RECEIVER.md |
| D17 | **Features we do not use**: `/auth-setup` (MFi), `/fp-setup` (FairPlay), `/pair-setup-pin` (AirPlay 1), AirPlay 1 fallback, RFC 2198 redundancy, SETPEERSX, `POST /command` now-playing. `/auth-setup` stays behind the quirk flag `ReceiverQuirks.SendAuthSetup` (off by default), implemented as `IReceiverSession.AuthSetupAsync` from the request spelled out in SPEC-control 3.10. | SPEC-control 3.10, 1.3. | SPEC-control 3.10 |

## 3. Contradictions between the parts and their resolution

| # | Where | What disagreed | Resolution (edits applied to the parts) |
|---|---|---|---|
| C1 | SPEC-control 4.4 (`shk` row) vs SPEC-audio 1.3 | "32 random bytes chosen by the sender" vs "first 32 bytes of the shared secret" | D4: shared secret. The control row now states it and references D4; random stays the A-Q4 test option. |
| C2 | SPEC-control 4.5 vs SPEC-audio 1.5/3.3 | RECORD's `Audio-Latency`: "add it to the latency budget" vs "treat published output latency as a per-receiver offset" | Per-receiver `offset_frames`, never a change of `L`. Control 4.5 reworded; audio 1.5 now names `Audio-Latency` explicitly. |
| C3 | Both parts vs the project plan's rationale | The plan: "OwnTone synchronizes AirPlay 2 receivers this way (NTP)"; current OwnTone uses PTP for bit-41 receivers | D1: the plan's decision stands, its rationale is corrected, the timing protocol is made pluggable and gated by probe P1. Both parts carry a "Resolution" note. |
| C4 | SPEC-control 7.7 vs SPEC-audio Q1 | The latency probe in 7.7 varies `latencyMin` (11025 / 22050); Q1 varies the sender latency `L` (77175 / 88200 / 66150); no receiver enforces `latencyMin`/`latencyMax` | Merged as P7 below: vary `L`, keep `latencyMin/Max` at 11025/88200, log the receiver's reaction to a `latencyMin` change as a side observation. |
| C5 | SPEC-control 6.1 diagram vs SPEC-audio 2.3 | The diagram shows the optional pyatv `FLUSH` after the first audio; the audio part uses `FLUSH` only for pause/seek/track change and says the first sync must precede the first audio packet | D5/D11: no start-up `FLUSH`; the diagram's "optional" step is dropped from the implementation. |
| C6 | SPEC-control 4.9 vs pyatv | Body-less TEARDOWN | D7. |
| C7 | SPEC-control 3.6 vs pyatv | M6 signature verification skipped by pyatv | D2: enforced. |
| C8 | SPEC-control 2.1 | `HTTP/1.1` (pyatv pairing POSTs) vs `RTSP/1.0` (owntone) | D6: `RTSP/1.0`; both accepted in responses. |
| C9 | SPEC-control 4.4, SPEC-audio 4.1 | `ct` for AAC: 3 (owntone) vs 4 (airplay2-receiver) | Irrelevant for v1 (`ct = 2`, ALAC). The buffered stream sends `audioFormat` as the authoritative selector and probes `ct` 3 vs 4 when AAC is added (P14). |
| C10 | SPEC-control 1.3 | Feature bit 32: `IsCarPlay` vs `SupportsVolume` | Not used; enum name `CarPlay` with a comment. |
| C11 | SPEC-control 4.4 | shairport-sync returns no `streamID` | Per-stream TEARDOWN only when a `streamID` was returned (D7); the fake receiver has a switch for both. |
| C12 | SPEC-audio 6.2 vs the project plan | The plan: device-side volume changes "are read back via the event channel"; no reference source shows such a message | D10: event channel first, poll fallback; P4 settles it. The code implements both paths. |

No other material disagreement was found: packet layouts, key derivations, nonce rules, the start-up order, the
volume mapping and the group model are described consistently in both parts.

## 4. Receiver findings and quirks: the probes and their results

Ids: `P<n>` here; the parts' own numbers are kept (`C-7.x` = SPEC-control section 7, `A-Qx` = SPEC-audio section 9).
Receivers: A is a third-party AirPlay 2 speaker (srcvers 366.0, features 0x445F8A00,0x1C340; "the srcvers-366 speaker",
at 192.0.2.26 in the examples), B a TV receiver that requires PIN pairing (srcvers 377.40.00; never used), C an Apple
receiver (only once its owner allows it).
What may be run against which device is in `../DEVELOPMENT.md` ("Live-device rules"). Each probe is a CLI command
(`airplay2 probe <id> <name>`) or a live test; its results are recorded below the table.

| P | Parts | Question | Probe | Decides |
|---|---|---|---|---|
| P1 | C-7.1, A-Q3 | Does receiver A accept `timingProtocol: NTP` for a type-96 stream and send 0xD2 timing requests? | **Control part (decisive, no audio)**: after transient pairing: session SETUP with NTP + `timingPort`; expect 200 with `eventPort`; event channel; RECORD; stream SETUP; wait 2 s and count the 0xD2 requests at `timingPort` (`ITimingResponder.RepliesSent > 0`); TEARDOWN. Repeat with `PTP` + minimal `timingPeerInfo` and record the response. **Audio part (confirmation, `--i-know`)**: 3 s quiet tone after one `0x90` sync under the protocol the control part chose. | D1 gate: NTP stays, or the realtime stream runs under PTP |
| P2 | C-7.2 | Does transient pairing work with flags bit 10 set and `acl=0`? | `POST /pair-pin-start` (HKP 4) + transient M1; expect M2 with Salt/PublicKey; 470 or TLV error means PIN flow by default. | Default pairing path for receiver A |
| P3 | C-7.3 | Start-up order: RECORD before vs after the stream SETUP; must volume be last? | Run both orders; compare `Audio-Latency` and audio start; send volume before and after the stream SETUP; read back with `GET_PARAMETER`. | D5 confirmation / quirk flags |
| P4 | C-7.4, A-Q5 | How does the receiver report a volume change made on its own buttons? | During a session press receiver A's buttons; log every event-channel message and every feedback body; poll `GET_PARAMETER volume` each second and diff. | D10: event-driven or poll |
| P5 | C-7.5 | Which `streamID` comes back, and do both TEARDOWN forms stop cleanly? | Inspect the stream SETUP response; send per-stream then session TEARDOWN; check `Connection: close`; start the next session, expect no 451/453. | D7 confirmation |
| P6 | C-7.6 | Does M1 method 1 (`PairSetupWithAuth`) get rejected by a bit-51-clear receiver? (TV case deferred.) | Against receiver A only: M1 with method 0 and 1 on two connections, compare M2. | PIN flow M1 byte for the TV, later |
| P7 | C-7.7, A-Q1, A-Q2, A-Q9 | Which `L` (77175 / 88200 / 66150) plays stably, what is the real output delay, does `Audio-Latency`/`audioLatencies` appear after pairing, does sync flags 4 vs 7 move the latency? | Click track (`play click.wav --latency N`) + microphone against a locally played click; measure for each `L` and for flags 4/7; dump SETUP responses and RECORD headers and grep `Latency`. | `L` default, `offset_frames` seeding |
| P8 | C-7.8, A-Q13 | What does `POST /feedback` return, does `keepAliveSendStatsAsBody` want a body, how long until an idle session drops without feedback/progress? | Feedback every 2 s, log bodies; try bodies `{}` and `{streams:[]}`; 10-minute sessions with (a) neither, (b) feedback only, (c) progress only; time the socket closure. | Keep-alive design |
| P9 | C-7.9 | Is DMAP metadata displayed by receiver A's companion app (TV deferred)? | Send `mlit{minm,asar,asal}` + JPEG; look at receiver A's app. | Artwork effort |
| P10 | C-7.10 | Does `GET /info` carry `initialVolume` once paired? | Repeat `GET /info` inside the encrypted channel; then `GET_PARAMETER volume`. | Start-up level seeding |
| P11 | C-7.11 | Is `X-Apple-HKP` required? | Transient M1 without the header on one connection. | Header hygiene |
| P12 | C-7.12 | Does `gid` follow `groupUUID`; does a second member need `isMultiSelectAirPlay` / `groupContainsGroupLeader`? | Two sessions with the same `groupUUID`; browse mDNS for `gid`/`gcgl`; toggle the booleans. | Group SETUP fields |
| P13 | C-7.13 | Event-channel timing: `updateInfo` on connect or after RECORD; does a late 200 close the channel? | Timestamp the first event message relative to SETUP/RECORD; delay our answer by 5/20/40 s. | Event reader design |
| P14 | C-7.14, A-Q6 | Does receiver A send 0xD5 requests and accept wrapped 0xD6 answers (vs raw on the data port)? Anything else on the control port? | Drop every 50th RTP packet (test flag); capture the control port; count re-requests of the same sequence for each reply form. | D8 confirmation / quirk |
| P15 | A-Q4 | Is `shk` free or must it be the shared secret? | Same session twice with `shk = SS[0..31]` and random bytes; audio must play in both. | D4 |
| P16 | A-Q7 | Buffered stream with ALAC 44.1/16: which SSRC format code (`0x0000FACE`, `0x15000000` with 48/24, `0x16000000` AAC) does an Apple-SDK receiver accept? | Only when the buffered stream exists: three short streams. | Buffered codec choice |
| P17 | A-Q8 | Does receiver A's PTP slave (and nqptp) send Delay_Req and need Delay_Resp? | Capture UDP 319/320 during a PTP session (Wireshark on the sender; nqptp in the Docker VM first). | PTP master scope |
| P18 | A-Q10 | Tolerance to missed sync packets. | Stop syncs for 3/6/10 s mid-track; observe. | Sync cadence / recovery |
| P19 | A-Q11 | Does mid-stream FLUSH + `0x90` sync + marker packet resume cleanly without a new RECORD? | Pause 5 s and resume; seek within a track; check for stale audio. | Pause/seek design |
| P20 | A-Q12 | Mixed group (receiver A + receiver C): same `L` for both, per-receiver offset? | Two-receiver microphone measurement; store `offset_frames`. | Group tuning |
| P21 | C-7.15 | Does receiver A accept an encrypted control-channel frame larger than 1024 bytes from us, and how large are the frames it sends? | No audio: one throw-away connection sends `GET /info` as a single 2048-byte-padded frame (test-only switch, never in the library's send path); log the length field of every received frame in a normal session. | Confirms the receive-side rule (accept up to 65 535); the 1024 send cap stays fixed either way |

The results follow as dated "Result (date): ..." entries; add new ones the same way. Where a question is still open
the code keeps both branches the parts name (NTP/PTP, event/poll, wrapped/raw).

- Discovery (no P id) Result (2026-10-09): `MdnsReceiverBrowser` found receiver A (192.0.2.26, port
  7000, two ULA and one link-local AAAA), receiver C and receiver B, all via the reflector 203.0.113.1, the
  first two with their `_raop` record merged (none for the TV in that run); addresses taken from A/AAAA records only. Port-5353 queries get multicast answers with
  normal TTLs, ephemeral-port queries get legacy unicast answers with TTL 10 (receiver A answered only the former).
  Details in SPEC-control 1.1 (note) and `artifacts/live/2026-10-09-discover.log`.
- `GET /info` (no P id) Result (2026-10-09): bare and with the `txtAirPlay` qualifier body against the
  receiver A, both `RTSP/1.0 200`, `Server: AirTunes/366.0`; the bodies equal `info-response.bplist` (525 bytes) and
  `info-response-stage1.bplist` (909 bytes) byte for byte, apart from the identifiers the vectors replace. Re-run later the same day with the same result.
  Log: `artifacts/live/2026-10-09-rtsp-get-info-live-receiver.log`.
- Pairing against shairport-sync 5.5.2 (no P id, Docker VM) Result (2026-10-09): transient M1-M4 succeeds and
  an encrypted `GET /info` answers 200; with `--password` transient is refused in M4 (TLV Error 2), PIN pair-setup M1-M6
  and pair-verify succeed, but shairport-sync does not switch to encryption after pair-verify (SPEC-control 3.7 note).
  P2, P6 and P11 against receiver A ran later (see below).
- **P1 (control part, decisive for D1)** Result (2026-10-09, receiver A, no audio): after transient
  pairing the NTP session SETUP is answered `200` with `{eventPort: 7006, timingPort: 7005}`; event channel, RECORD and
  stream SETUP succeed, and **3 timing requests (0xD2) were answered within 2 s of the stream SETUP, the first while it
  was being answered**. A PTP session SETUP with a minimal `timingPeerInfo` is accepted as well (`200`, `timingPeerInfo`
  with receiver A's IPv4, two ULA and one link-local address). **Decision: D1 stands, receiver A's realtime stream runs
  under NTP** (`ReceiverQuirks.TimingProtocol = Ntp` for TXT `srcvers` 366.0, `KnownReceiverQuirks`; the `ShairportSync` model rule is checked first because shairport-sync also reports srcvers 366.0). shairport-sync
  5.5.2 refuses NTP with `200` and an empty body (not 501, SPEC-control 4.2 note), so it stays on PTP. Log:
  `artifacts/live/2026-10-09-P1-timing-protocol.log`, `-session-startup.log`.
- P3 Result (2026-10-09, receiver A, no audio): both orders are accepted (session SETUP, event channel, RECORD before or
  after the stream SETUP, all `200`, no `Audio-Latency` header). A volume sent **before** the stream SETUP is answered
  `200` but not applied (`GET_PARAMETER` keeps the old value); after the stream SETUP it is applied: D5 (volume last)
  confirmed. Log: `-P3-startup-order.log`.
- P4 Result (2026-10-09, receiver A): not conclusive yet: a 10 s session without button presses (user not present) showed no
  event-channel message and no `GET_PARAMETER` change. To repeat with the user pressing the buttons
  (`P4_SECONDS=60`). Log: `-P4-device-volume.log`.
- P5 Result (2026-10-09, receiver A): **no `streamID`** in the stream SETUP answer (`{type: 96, dataPort, controlPort}`), so
  only `TEARDOWN {}` is sent; it is answered `200` **without `Connection: close`**, the control connection stays open,
  receiver A closes the event channel; the next session on a new connection starts with `200`. Log: `-P5-teardown.log`.
- P8 Result (2026-10-09, receiver A): `POST /feedback` without body, with `{}` and with `{streams: []}` is always answered
  `200 {streams: [{type: 96, sr: 44100}]}`; the idle-drop timing (10-minute runs) is still open. Log: `-P8-feedback.log`.
- P9 Result (2026-10-09, receiver A, no audio): DMAP is answered **`500`** in all three forms (`mlit{minm,asar,asal,astm}`,
  pyatv's minimal item, and with `RTP-Info: seq=;rtptime=`), artwork (JPEG) and progress `200`; DMAP with audio
  playing is in the P1 audio entry below; the app check is MANUAL-TESTS.md 2.7. The session sends metadata best-effort. Log: `-P9-metadata.log`.
- P10 Result (2026-10-09, receiver A): the paired `GET /info` (bare and stage 1) has **no `initialVolume`**; `GET_PARAMETER
  volume` answers `volume: -24.000000\r\n` (no leading blank line) after the session SETUP already; the first session
  ever read `-30`, later ones the last level set. Start-up seeding must use `GET_PARAMETER`. Log: `-P10-initial-volume.log`.
- P13 Result (2026-10-09, receiver A): receiver A sends **no event-channel request at all** (no `updateInfo` on connect or
  after RECORD; 15 s observed with the answer delay at 5 s, the channel stayed open). The late-answer case cannot be
  provoked on receiver A. Log: `-P13-event-timing.log`.
- P15 (control part) Result (2026-10-09, receiver A): stream SETUP is accepted with `shk = SS[0..31]` and with 32 random
  bytes; the audio part (playback with each key) is below. Log: `-P15-stream-key.log`.
- P21 Result (2026-10-09, receiver A): a `GET /info` sent as **one 2048-byte HAP frame** makes receiver A **close the
  connection** without an answer; in normal sessions its own frames never exceed 1024 bytes. The 1024-byte send cap is
  required for receiver A; the receive rule stays. Log: `-P21-large-frame.log`.
- **P17** Result (2026-10-09; receiver A: P1-PTP control probe without audio, 20 s; shairport-sync 5.5.2 in
  the Docker VM with 3-25 s of silence): receiver A's slave sends a Signaling (802.1AS interval request: timeSync -3)
  after SETPEERS, ~1 s of its own Sync/Follow_Up/Announce after the stream SETUP, then **Delay_Req ~8/s for the whole
  session**, all answered by our Delay_Resp. **nqptp sends only two Announces at the start and nothing after (no
  Delay_Req)**, yet follows our clock and plays the 0xD7-synced realtime stream (also through `RealtimeStreamTransport`
  with `PtpMaster`). Because of nqptp, `PtpMaster` keeps silent peers (reported after 15 s) instead of libairptp's 15 s
  drop (`PtpMasterOptions.DropSilentPeers` restores it). Details: SPEC-audio.md 4.4 note. Logs:
  `artifacts/live/2026-10-09-P17-ptp-capture.log`, `-ptp-shairport.log`, `-ptp-shairport-transport.log`,
  `-ptp-shairport-droprule-25s.log`, `-ptp-shairport-silent-peer-20s.log`.
- **P1 (audio confirmation)** with P14, P9 (audio half), P12 (gid) and P7 (latency fields) Result (2026-10-09,
  receiver A, 3 s tone at -24 dBFS, level 15 = -25.5 dB, through `GroupPlayer` +
  `RealtimeStreamTransport` under NTP; the CLI `play` did not exist yet, so the ReceiverLive test
  `PlaybackLiveTests.Live_tone_3s_P1_P14` stood in for `airplay2 play tone:3 ... --i-know`): the initial `0x90` sync +
  marker packet started the stream, timing requests kept coming (5 in the session), feedback and `GET_PARAMETER volume`
  (-25.5) answered 200. **P14: every one of the 13 withheld packets (every 50th) was requested with an 8-byte 0xD5 from
  receiver A's announced control port and none was re-requested after our wrapped 0xD6 answer**: D8's default form works
  on receiver A; nothing else arrived on the control port. P9: DMAP is answered **500 also while audio flows**, artwork
  and progress 200. P12: receiver A's mDNS `gid` stayed its device id (`gcgl=0`) during the session, it does not
  republish our `groupUUID` (single receiver; the two-member case is final integration). P7: the session SETUP answer
  has only `eventPort`/`timingPort`, RECORD `Audio-Latency` is absent (0): no latency is published, the output delay
  needs the microphone measurement. Audibility was not checked by a listener. Log: `-P1-audio-tone.log`.
- P19 Result (2026-10-09, receiver A, 0.7 s of tone in total): pause = pacer stopped + `FLUSH` with the next seq/rtptime,
  answered `200` **after 2.0 s**; resume after 2 s with a new `0x90` sync + marker packet at the announced values and
  **no new RECORD** was accepted (timing, feedback, volume carried on; no retransmit requests); a seek-style `FLUSH`
  while streaming was answered `200` **after 3.0 s**, then `0x90` + marker again. Stale audio after the flush needs a
  listener. Receiver A's slow FLUSH answer makes a seek/skip cost FLUSH time + L before new audio is heard
  (SPEC-control.md 4.8 note). Log: `-P19-pause-resume.log`.
- P15 (audio part) Result (2026-10-09, receiver A, 2 s tone): with **32 random bytes as `shk`** the stream is accepted and
  runs like the shared-secret key (no error, no retransmit request, session normal); the receiver gives no decrypt
  feedback, so "plays in both cases" needs a listener. D4 unchanged. Log: `-P15-audio-random-key.log`.
- P18 Result (2026-10-09, receiver A, 3 s tone): periodic syncs stopped for 2 s mid-tone (the 3-second audio rule allows no
  longer gap with sound): no reaction on the wire, timing requests continued, the stream went on; 3 genuine Wi-Fi
  losses were requested and answered meanwhile. The 3/6/10 s gaps and the audible effect remain for a session with the
  user present. Log: `-P18-sync-gap.log`.
- Timeouts (no P id, receiver A on Wi-Fi): pair-setup answers took up to 7.9 s, RECORD once 9.9 s, and one
  M2 did not come within 10 s: D6's 10 s request timeout is too tight; the probes use 30 s (SPEC-control 2.1 note).
- Re-run of the audio tests (2026-10-09, after the review fixes; receiver A on a degraded Wi-Fi day: ICMP dropped, RTSP answers
  1-9 s): `Live_tone_3s_P1_P14` passed (3 s tone, 64 withheld packets all requested and re-sent, 626 retransmit
  requests in total, 0 send errors); SET_PARAMETER metadata answers were so slow that the session streamed ~22 s of
  silence after the tone, and **TEARDOWN got no answer within `GroupPlayerOptions.TeardownTimeout` (10 s)**, so the
  connection was closed instead. `Live_P19_pause_resume` passed: the pause FLUSH was answered after **16.7 s**, the
  seek-style FLUSH **not within the 30 s RTSP timeout**; the group re-anchored anyway (`0x90` + marker, Streaming), as the
  fixed resume path intends, and TEARDOWN was answered 200. The FLUSH answer time is not bounded by receiver A (2-3 s on a
  good day, see P19). P17 control probe repeated: 166 Delay_Resp (now domain 0 / correction 0 as libairptp) answered,
  Delay_Req kept coming at the same rate, TEARDOWN 200. PTP ShairportLive tests (bridge-network script
  `docker/shairport-sync/run-ptp-live-tests-in-vm.ps1`): 2/2 passed, nqptp took our clock as master.

- **Sender M1 through the CLI** Result (2026-10-09, receiver A, 3 s tone at -24 dBFS, level 15):
  `airplay2 play tone:3 --to "<receiver>" --level 15 --i-know` (run as the ReceiverLive test `CliLiveTests.Live_sender_M1_play_tone_3s`,
  which calls the CLI's `Program.RunAsync` with real mDNS) found receiver A, paired transiently, ran D5 through the
  `AirplaySender` facade (realtime stream, NTP by the P1 quirk) and played the tone: stream SETUP answered 150 ms after 3
  timing replies, 659 packets (284 of them silence while draining L), 5 retransmit requests answered wrapped, DMAP 500,
  TEARDOWN 200, exit 0. The first attempt from the `airplay2.exe` binary failed: an inbound Windows Firewall **block**
  rule for the new executable kept receiver A's 0xD2 requests out, and receiver A answered the stream SETUP with **400
  after ~32 s** (no audio was sent; SPEC-control.md 4.4 note). Running `airplay2.exe` against NTP receivers therefore
  needs an inbound UDP allow rule for it (the install script's job for the service). Log: `-sender-M1-play.log`.
- **P2** Result (2026-10-09, `airplay2 probe P2`, receiver A): transient M1-M4 (`/pair-pin-start` HKP 4, Flags 0x10, PIN 3939)
  is accepted with flags bit 10 set and `acl=0`; the encrypted `GET /info` answers 200 (525 bytes, the same 19 keys as
  unencrypted). Transient stays receiver A's default pairing. Log: `-P2.log`.
- **P11** Result (2026-10-09, `airplay2 probe P11`, receiver A): `/pair-pin-start` and transient M1 **without `X-Apple-HKP`**
  are answered **`403 Forbidden`** (no TLV body): the header is required; the library sends it on every pairing POST (D2).
  Log: `-P11.log`.
- **P6** Result (2026-10-09, `airplay2 probe P6`, receiver A; no `/pair-pin-start`, no PIN entry): M1 with method 0
  (`PairSetup`) and with method 1 (`PairSetupWithAuth`) on two connections **5 s apart** both get `200` with
  `State=2, Salt(16), PublicKey(384)`: the bit-51-clear receiver A does not reject method 1. With the second M1 only ~50 ms
  after the first connection closed, it answered `Error=BackOff` with a `RetryDelay` item instead (the abandoned SRP
  session was still open), so the probe now waits 5 s between them. The TV case stays deferred. Log: `-P6.log`.
- CLI probe dispatch: `airplay2 probe <P1..P21> <name>` runs the methods of `SessionProbes` (P1 control
  part, P3, P4, P5, P8, P9, P10, P13, P15 control part, P21) and `PlaybackProbes` (P1 audio part, P7, P12, P14, P15 audio
  part, P18, P19), and implements P2, P6, P11 and P17 itself. P7, P12, P14, P18 and P19 exit with code 2 without
  `--i-know`; P1 and P15 then run only their control part. The CLI's audio probes run under NTP only. P16 (buffered
  stream) and P20 (mixed group, microphone) exit with code 3. Each run appends its Debug log to
  `artifacts/live/<date>-<id>.log`.

- **P16** Result (2026-10-09, receiver A, two 3 s tone runs at level 15 plus three probes without audio):
  receiver A accepts the type-103 stream SETUP (`dataPort` TCP, `controlPort`, `audioBufferSize 8388608`, no `streamID`)
  and the TCP data connection, but answers `SETRATEANCHORTIME rate 1` with **400** whenever the data connection is open:
  empty, or after 3 s of **ALAC 44.1/16 with SSRC `0x0000FACE`** or **ALAC 48/24 (uncompressed) with `0x15000000`**; rate 0
  is answered 200, and without a data connection every anchor body variant is answered 200 (SPEC-audio.md 4.3 note). So
  neither ALAC form plays buffered on receiver A; AAC-LC (`0x16000000`) needs an encoder and is untested. The buffered
  stream works end to end on shairport-sync 5.5.2 (run-ahead push with an explicit anchor, and the group player's pacer
  path with pause/resume: rate 0 + FLUSHBUFFERED + rate 1). The realtime stream stays the default (D1). Logs:
  `artifacts/live/2026-10-09-P16-buffered-alac44-face.log`, `-P16-buffered-alac48-24.log`, `-P16-anchor-body.log`,
  `-buffered-shairport.log`.

- **Bridge M1, control part** Result (2026-10-09, receiver A, no audio; `BridgeLiveTests`): the bridge
  found receiver A by mDNS. Through the REST API it set level 15, connected it as the single output (transient
  pairing, NTP session, event channel, RECORD without `Audio-Latency`, stream SETUP without `streamID`, volume
  `-25.5`), changed the level (`GET_PARAMETER` read back `-26.4`), and disconnected it (`TEARDOWN {}` answered 200).
  The pacer never started. The Spotify handoff and audio through the bridge need a person (MANUAL-TESTS.md
  section 1). Log: `-bridge-connect.log`.
- **Final integration** Result (2026-10-09):
  - Sender M1 confirmation (receiver A, the one allowed run: `CliLiveTests.Live_sender_M1_play_tone_3s`, 3 s tone at
    -24 dBFS, level 15): passed. 659 packets (284 silence while draining L), 3 retransmit requests answered wrapped,
    DMAP `500`, exit 0. Receiver A was slow that day: connect, `GET /info` and transient pairing took 14 s, session
    SETUP to event channel 7 s, stream SETUP 3 s. **The `TEARDOWN {}` was sent but not answered within `GroupPlayerOptions.TeardownTimeout`
    (10 s)**, so the event channel and connection were closed, as in the earlier re-run. An unauthenticated
    `GET /info` straight afterwards answered 200, so receiver A had released the session. The log is appended to
    `-sender-M1-play.log` (15:33-15:34).
  - shairport-sync 5.5.2, all ShairportLive tests (8/8, each class in its own run) plus the bridge's container image
    against it through the REST API (connect under PTP with our grandmaster, volume, disconnect): all passed.
    Details: TEST-RECEIVER.md, "What shairport-sync actually exercised". Logs: `-final-shairport-*.log`,
    `-final-bridge-container-shairport.log`.
  - Offline: `build.ps1` (Release build, tests without the live categories, win-x64 publish of the bridge and the CLI,
    container image) passes. No `TODO(impl:` marker remains in `src/`, `tests/`, `docker/`, `install/` or `build.ps1`
    (`RepositoryHygieneTests` keeps it that way).

### 4.1 Probe status

"Settled" means that the default below follows from the probe. "Open" items need a person; each is a step in
[`MANUAL-TESTS.md`](MANUAL-TESTS.md) (section in brackets).

| P | Status | Outcome in one line | Manual test |
|---|---|---|---|
| P1 | Settled | Receiver A: NTP accepted, timing requests arrive, audio streams under NTP | audibility (2.1) |
| P2 | Settled | Transient pairing works with flags bit 10 and `acl=0` | none |
| P3 | Settled | Both orders accepted; a volume sent before the stream SETUP is not applied (D5 confirmed) | none |
| P4 | Open | No event-channel message and no change in 10 s without button presses | button presses during a 60 s probe (2.6) |
| P5 | Settled | No `streamID`; `TEARDOWN {}` answered 200 without `Connection: close`; next session fine | none |
| P6 | Settled for receiver A | Methods 0 and 1 both accepted (5 s apart; `BackOff` if immediate) | TV with PIN (5.3), the owner's call |
| P7 | Partly | No latency published (`Audio-Latency` absent, no `audioLatencies`) | microphone measurement of L (3) |
| P8 | Partly | `/feedback` always answered `{streams:[{type:96, sr:44100}]}` | idle-drop timing, 10 min (1.9) |
| P9 | Partly | DMAP text `500` with and without audio; artwork and progress 200 | companion app display (2.7) |
| P10 | Settled | No `initialVolume` in the paired `/info`; `GET_PARAMETER volume` gives the level | none |
| P11 | Settled | `X-Apple-HKP` required (403 without) | none |
| P12 | Partly | A single receiver A keeps its own `gid`, `gcgl=0` | a two-receiver group (4.5) |
| P13 | Settled | Receiver A sends no event-channel requests | none |
| P14 | Settled | Every withheld packet re-requested once with an 8-byte 0xD5; the wrapped 0xD6 answer is taken | none |
| P15 | Partly | Random `shk` and `SS[0..31]` both accepted on the wire | audibility with a random key (2.3) |
| P16 | Settled (negative) | Buffered ALAC 44.1/16 (`0xFACE`) and 48/24 (`0x15000000`): rate-1 anchor answered 400; AAC untested | none (needs an AAC encoder) |
| P17 | Settled | Receiver A: Signaling + Delay_Req ~8/s, answered; nqptp: two Announces, then silent | none |
| P18 | Partly | A 2 s sync gap had no effect on the wire | 3/6/10 s gaps need the probes' 3 s cap lifted (2.5) |
| P19 | Partly | FLUSH + `0x90` + marker resumes without RECORD; FLUSH answers took from 2 s to more than 30 s | stale audio by ear (1.6, 2.4) |
| P20 | Open | Not run (receiver C only with its owner's permission) | (4.6) |
| P21 | Settled | A 2048-byte frame makes receiver A close the connection; its own frames are at most 1024 bytes | none |

### 4.2 Resulting defaults

What the code does by default because of the probes. `QuirkDefaultsTests` (tests/Airplay2.Sender.Tests/Integration)
and `ReleaseConsistencyTests` (tests/Bridge.Tests/Integration) pin these values; change the table, the code and the
tests together.

| Default | Value | From | Where in code |
|---|---|---|---|
| Timing protocol, srcvers 366.0 (receiver A) | NTP | P1 | `KnownReceiverQuirks` (srcvers 366.0) |
| Timing protocol, shairport-sync | PTP | NTP session refused with 200 and an empty body (SPEC-control.md 4.2 note) | `KnownReceiverQuirks` (model `ShairportSync`, checked before srcvers: shairport-sync also reports 366.0) |
| Timing protocol, any other receiver | NTP (D1); a group with any PTP member runs entirely under PTP | D1, D3 | `AirplaySenderOptions.DefaultTimingProtocol`, `AirplaySender.ChooseTransport` |
| Stream type | Realtime (type 96) | D1; P16 (buffered does not play on receiver A) | `AirplaySenderOptions.PreferredStream`, `Bridge:PreferredStream` |
| Pairing | Transient where advertised, otherwise PIN; `X-Apple-HKP` on every pairing POST | P2, P11 | `HapPairingClient`, `ReceiverSession.PairAsync` |
| PIN M1 method byte | 0 (`PairSetup`) | P6 (receiver A takes both) | `ReceiverSessionOptions.PinPairingMethod` |
| RTSP request timeout | 30 s in the facade and the probes (D6's 10 s stays the bare `RtspConnectionOptions` default) | Receiver A answers up to ~10 s ("Timeouts" above) | `AirplaySenderOptions.Connection` |
| Encrypted frame size we send | 1024 bytes of plaintext, a `const` | P21 | `HapFrameCipher.MaxSendFramePlaintext` |
| Volume ordering | After the stream SETUP, on every change, coalesced | P3 | D5 order in `GroupPlayer`, `ReceiverQuirks.VolumeAfterStreamSetup` |
| Device-side volume read-back | `GET_PARAMETER volume` poll every 3 s while playing, plus any event-channel level | P4 (open), P10, P13 | `GroupPlayerOptions.VolumePollInterval`, `Bridge:PollReceiverVolumeSeconds` |
| Retransmit answer | Wrapped 0xD6 on our control port | P14 | `ReceiverQuirks.RetransmitOnDataPort = false` |
| Stream key `shk` | First 32 bytes of the pairing secret (D4; random also accepted) | P15 | `PairingSession.DefaultStreamKey` |
| TEARDOWN | `{}` (per-stream form only with a `streamID`); at most 10 s, then the connection is closed | P5; the audio re-run and the final M1 run (unanswered TEARDOWN) | `ReceiverSession.TeardownAsync`, `GroupPlayerOptions.TeardownTimeout` |
| Metadata | Best effort: a DMAP `500` is logged and ignored, and after the first non-2xx answer the session sends no more DMAP text; artwork and progress are sent | P9 | `ReceiverSession.SetMetadataAsync`, `ReceiverSession.DmapRefused`; CLI `play --no-metadata` |
| PTP peers that go silent | Kept (reported after 15 s) | P17 (nqptp) | `PtpMasterOptions.DropSilentPeers = false` |
| Sender latency L | 77 175 frames | D1; P7 open | `RealtimeStreamConstants.DefaultLatencyFrames`, `Bridge:LatencyFrames` |
| Per-receiver offset | 0 frames | P7/P20 open | `ReceiverQuirks.OffsetFrames`, `receivers.offset_frames` |
| `/auth-setup` | Off | D17 | `ReceiverQuirks.SendAuthSetup` |

Two candidate quirks were **not** switched on (2026-10-09, final integration), each with its reason:

- *No DMAP text for receiver A.* It answers DMAP with `500`, sometimes only after ~10 s (P9 log: 10.3 s; the final
  review's tone run: 5.5 s). The control connection carries one request at a time, so a volume change or a FLUSH
  queued behind it waits that long. A per-model default is the wrong tool: the in-process fake receiver serves the
  receiver A's recorded `/info`, so it would stop every fake-receiver test from seeing DMAP. Implemented instead (final
  review, 2026-10-09) as a per-session rule: after one non-2xx answer to DMAP, `ReceiverSession` sends no more DMAP
  text on that session (logged once at Information; `DmapRefused`), so at most the first track's DMAP costs the wait.
  The fake receiver reproduces the refusal with `FakeReceiverOptions.DmapStatus`.
- *No buffered stream for receiver A.* It advertises bit 40, so `ChooseTransport` would pick the buffered stream for
  it if `PreferredStream = Buffered`, and playback would then fail at `SETRATEANCHORTIME` (P16). Today the realtime
  default keeps it off. A `ReceiverQuirks` switch for this would be an additive contract change and is left for when
  AAC-LC is added.

## 5. Glossary of the names used in code

| Spec term | Code |
|---|---|
| shared secret `SS` | `PairingSession.SharedSecret` |
| control keys / event keys | `HapChannelKeys.ForControl` / `ForEvents` (swapped) |
| HAP frame | `HapFrameCipher` |
| `shk` | `StreamSetupRequest.StreamKey`, `PairingSession.DefaultStreamKey` |
| `L`, `now_frame`, `next_ts` | `Timeline.LatencyFrames`, `Timeline.NowFrame`, `Timeline.NextTimestamp` |
| `offset_frames` | `ReceiverQuirks.OffsetFrames`, `receivers.offset_frames` |
| `r_i`, `G` | `GroupVolumeModel.Levels`, `GroupVolumeModel.MasterLevel` |
| timing responder, control port | `ITimingResponder`, `IControlPort` |
| stream layer | `IStreamTransport` (`RealtimeStreamTransport` with or without an `IPtpMaster`, `BufferedStreamTransport`); its one clock is `IStreamTransport.Clock` |
| PTP grandmaster, clock id | `IPtpMaster`, `IPtpMaster.ClockId` (`ulong`; `long` only inside the plist) |
| accessory (receiver) side of pairing | `HapAccessoryPairing` (internal, used by the fake receiver) |
| `/auth-setup` quirk | `ReceiverQuirks.SendAuthSetup`, `IReceiverSession.AuthSetupAsync` |
| features / flags | `AirplayFeatures`, `AirplayStatusFlags`, `ReceiverCapabilities` |
