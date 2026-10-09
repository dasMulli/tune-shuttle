# AirPlay 2 sender specification: audio plane

Status: research draft, 2026-10-09; contradictions with the control plane resolved by the architect the same day
(see `SPEC.md`, which indexes both parts, holds the v1 decisions and the merged open-question list); review pass of
the same day re-verified every `[pyatv] airplayv2.py`, `hap_session.py`, `chacha20.py` and `channels.py` line number
against the b277a4c checkout (the `airplayv2.py` numbers had been taken from a different tree and are corrected),
added the control-port lookup rule (2.4) and the `GET_PARAMETER` body shape (6.2). Companion to
`docs/PLAN.md` (binding goals and decisions) and to `SPEC-control.md` (discovery, pairing, RTSP, event channel). This document
covers everything that moves audio and time: the realtime RTP stream, NTP-style timing, the control port, group
playback, the buffered stream and its PTP master, the ALAC encoder, volume on the wire, metadata timing, and the
sender-side pipeline that feeds all of this from librespot.

The audience is an implementer who must not have to re-derive anything. Every packet layout, formula and key
derivation is written out with a citation into the reference sources; where sources disagree the disagreement is
stated and a decision is recommended. Nothing in this document was verified against a real receiver beyond the two
unauthenticated probes in Appendix A; Section 9 lists what must be tested before the numbers here are trusted.

## 0. Conventions, sources and the one-paragraph summary

### 0.1 Citation prefixes

All sources are the clones recorded in `docs/REFERENCE-SOURCES.md`. Line numbers refer to those exact commits.

| Prefix | Repository (commit) | Path root | Licence | Use |
|---|---|---|---|---|
| `[pyatv]` | pyatv b277a4c | `reference/pyatv/pyatv/` | MIT | AirPlay 2 realtime sender (raw PCM), NTP timing server, control port, HAP channels |
| `[sps]` | shairport-sync 01078ad | `reference/shairport-sync/` | MIT | AirPlay 2 receiver: what it demands from a sender for both stream types, PTP consumer (via nqptp) |
| `[ap2r]` | airplay2-receiver 6c343d3 | `reference/airplay2-receiver/` | see repo | Python receiver with protocol notes; packet layouts, feature bits |
| `[ot]` | owntone-server 4fa5dcc | `reference/owntone-server/src/` | GPL-2.0 (`outputs/airplay.c`, `outputs/rtp_common.c`, `outputs/airplay_events.c`); **MIT** (`libairptp/`, file headers say "MIT License, Copyright (c) 2026 OwnTone") | Multi-receiver sender with ALAC, NTP **and** PTP (it ships its own PTP grandmaster, libairptp). GPL files: understanding only, never transcribe. libairptp: MIT per its own headers; confirm with the project before copying code, treat as "understanding" until then. |
| `[alac]` | macosforge/alac c38887c | `reference/alac/` | Apache-2.0 | ALAC encoder/decoder to port |
| `[lrs]` | librespot-dotnet (this repo) | `librespot-dotnet/src/` | MIT | The sink the bridge feeds |

Byte offsets are zero-based. "BE" means big-endian (network order). "frames" means sample frames (one left plus
one right sample); at 44 100 Hz one frame is 22.68 µs and 352 frames are 7.98 ms.

### 0.2 Summary

- The realtime stream (RTP payload type 96) is Classic-AirPlay RTP with two changes: the payload is encrypted with
  ChaCha20-Poly1305 under a 32-byte key `shk` that the sender puts into the stream `SETUP` plist, and the session
  is negotiated with binary plists instead of SDP. Timestamps, sync packets, timing requests and retransmissions are
  the RAOP ones. `[ot]` sends exactly this to Sonos, AirPort Express 2 and Apple devices and keeps N of them in sync
  from one master clock; `[sps]` accepts it when the session is PTP-timed and refuses NTP-timed AirPlay 2 sessions.
- With NTP timing the sender answers the receiver's timing requests from its own monotonic clock and sends one sync
  packet per second per receiver. With PTP timing the sender is the grandmaster (Announce/Sync/Follow_Up/Delay_Resp,
  unicast to each receiver) and the sync packet carries the PTP time and clock identity instead.
- A group is one RTP timeline (one sequence counter, one timestamp counter, one ALAC encoder output) sent to every
  receiver, with per-receiver encryption, per-receiver sync packets whose "now" frame may be shifted by a
  per-receiver offset, and one timing responder/PTP master shared by all. Adding a receiver mid-stream means: run
  its session setup, then send it an initial sync packet (first byte 0x90) and the next audio packet with the RTP
  marker bit set. Resync after a flush is the same two packets.
- The buffered stream (type 103) sends length-prefixed RTP packets over TCP, far ahead of real time, anchored with
  `SETRATEANCHORTIME` and trimmed with `FLUSHBUFFERED`. Its SSRC field names the codec; Apple senders use AAC-LC
  or ALAC 48 kHz/24-bit there, and `[sps]` had to invent a code for ALAC 44.1 kHz/16-bit. That is the main reason
  the plan starts with the realtime stream.
- ALAC: port Apple's encoder (about 900 lines of C for the 16-bit stereo path), run it with a 352-frame frame
  size, and validate it against the bit-exact vectors in `docs/airplay2/test-vectors/` generated today from the
  reference C++ code.

### 0.3 What the live probes changed

Appendix A has the raw results. The Test Speaker ABCD (192.0.2.26, `srcvers 366.0`, `sdk AirPlay;2.0.10`)
advertises features `0x1C340445F8A00`: AirPlay audio, redundancy, all three metadata types, PCM/ALAC/AAC-LC
receive formats, unencrypted audio, MFi auth bit 26, unified advertising, control-channel encryption, buffered
audio (bit 40), **PTP (bit 41)**, HomeKit pairing (46), peer management (47) and transient pairing (48). It does
**not** set bit 45 (`NTPClock`). Its `/info` response has no `supportedFormats`, no `audioLatencies` and no
`initialVolume`. This feature word is byte-for-byte the one `[ot]` lists for AirPort Express 2 and Sonos Symfonisk
(`[ot] outputs/airplay.c:3909,3919`), i.e. the Apple "p20" audio SDK profile, which is exactly the class of receiver
`[ot]` drove with NTP timing before it gained a PTP master. A Apple receiver ("Office", `AppleReceiver00,1`,
192.0.2.98) is also on the LAN but was not probed (not on the allowed list); it is the natural second test
device for M3 and for the buffered stream.

## 1. The realtime stream (type 96)

### 1.1 Stream negotiation parameters that the audio plane depends on

The second `SETUP` (the one with a `streams` array) carries one dict per stream. The keys below are the ones the
audio plane needs; the control-plane spec covers the request/response mechanics.

| Key | Value we send | Who reads it | Notes |
|---|---|---|---|
| `type` | `96` (0x60) | `[sps] rtsp.c:2865-2870`, `[ap2r] ap2/connections/stream.py:292,336` | Selects the realtime UDP stream. 103 is buffered (Section 4). |
| `ct` | `2` (ALAC) | `[sps] rtsp.c:2787-2796` (stored, decoding is fixed to ALAC anyway), `[ap2r] stream.py:367-368` | `[ot]` sends 2 (`[ot] outputs/airplay.c:2626`); `[pyatv]` sends 1 = raw PCM (`[pyatv] protocols/raop/protocols/airplayv2.py:134`). Sources disagree on the code table: `[ot] airplay.c:2626` says "1 LPCM, 2 ALAC, 3 AAC, 4 AAC ELD, 32 OPUS"; `[ap2r] stream.py:367` says "0x1 PCM, 0x2 ALAC, 0x4 AAC_LC, 0x8 AAC_ELD". Both agree ALAC = 2. |
| `audioFormat` | `0x40000` (bit 18, ALAC/44100/16/2) | `[sps]` reads it only for type 103 (`rtsp.c:2926-2933`); `[ap2r] stream.py:366` | Bit table: `[ot] airplay.c:2576-2611` and `[ap2r] ap2/connections/audio.py:320-351`. `[pyatv]` sends 0x800 (PCM/44100/16/2). |
| `spf` | `352` | `[sps] rtsp.c:2798-2807, 2888-2896` sets `frames_per_packet` | Frames per packet. Must match the ALAC frame length. |
| `sr` | `44100` | `[sps] rtsp.c:2877-2886` sets `input_rate` | |
| `shk` | 32 bytes | `[sps] rtsp.c:2766-2785` (rejects any length other than 32), `[ap2r] stream.py:369` | The audio payload key, Section 1.3. |
| `latencyMin` | `11025` | nobody in `[sps]`; `[ap2r] stream.py:378-383` sizes its jitter buffer from it | `[ot] airplay.c:2628-2629` and `[pyatv] airplayv2.py:136-137` both send 11025/88200. `[ot]` comment at `rtp_common.c:324-326`: for AirPlay 2 "the latency is communicated via latencyMin in the SETUP stream request". |
| `latencyMax` | `88200` | as above | |
| `controlPort` | our UDP control port | `[sps]` does not read it (it learns our control address from the first packet we send, `rtp.c:1674-1677`); `[ap2r] stream.py:375` | Where retransmit requests are sent (Section 2.4). |
| `audioMode` | `"default"` | | `[ot] airplay.c:2624`, `[pyatv] airplayv2.py:132`. |
| `isMedia` | `true` | | both senders. |
| `supportsDynamicStreamID` | `false` | | both senders. |
| `streamConnectionID` | our RTSP session id (uint64) | | both senders; "hopefully fine since we have one stream per session" (`[ot] airplay.c:2635`). |

The response's `streams[0]` gives `dataPort` (UDP, audio) and `controlPort` (UDP, sync/retransmit), and for the
buffered type also `audioBufferSize` (`[sps] rtsp.c:2911-2913, 2959-2963, 2981`; `[ot] airplay.c:3071-3091`;
`[pyatv] airplayv2.py:148-154`). `[sps]` starts its player thread on this `SETUP` for type 96 (`rtsp.c:2917`), so
audio may follow immediately after `RECORD`.

For an NTP-timed session the first `SETUP` (no `streams`) carries `timingProtocol: "NTP"` and `timingPort` (our
UDP port that answers timing requests): `[ot] airplay.c:2691-2716`, `[pyatv] airplayv2.py:51-74` (body at 56-74). `[sps]`
classifies the session by this string and refuses NTP ("Shairport Sync can not handle NTP streams",
`rtsp.c:2432-2438, 2683-2686`); it only handles `"PTP"` (Section 4.3) and `"None"` with `isRemoteControlOnly`.
`[ot]` warns that with `timingProtocol: "None"` an Apple TV 4 will not respond to the stream `SETUP`
(`airplay.c:2705`).

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** the session SETUP with `timingProtocol:
"NTP"` and our `timingPort` is accepted (answer `{eventPort, timingPort: 7001}`), and the SpeakerA sends 0xD2 timing
requests to our timing port from its port 7001 as soon as the stream SETUP is processed (3 within 2 s, then about every
3 s), before any audio. The stream SETUP answer has `dataPort`/`controlPort` and no `streamID`. See SPEC.md P1.

### 1.2 RTP audio packet layout

One packet per 352 frames. All multi-byte fields BE.

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 1 | V/P/X/CC | `0x80` (version 2, no padding, no extension, no CSRC). `[pyatv] protocols/raop/stream_client.py:582`, `[ot] outputs/rtp_common.c:161` |
| 1 | 1 | M + PT | `0x60` (PT 96). `0xE0` (marker set) on the first packet of a stream and on the first packet sent to a receiver that just joined or was just flushed. `[pyatv] stream_client.py:583`; `[ot] airplay.c:2100-2106` (sets the bit for sessions in state CONNECTED, i.e. just joined, then clears it) |
| 2 | 2 | sequence number | uint16, +1 per packet, wraps. Start value random (`[pyatv] protocols/raop/protocols/__init__.py:48`, `[ot] rtp_common.c:76`). |
| 4 | 4 | RTP timestamp | uint32 in frames, +352 per packet, wraps mod 2^32. Start value: Section 1.5. `[pyatv] stream_client.py:602`, `[ot] rtp_common.c:186` |
| 8 | 4 | SSRC | Any constant per session. `[pyatv]` uses its RTSP session id (`stream_client.py:586`, random 32-bit from `support/rtsp.py:193`); `[ot]` uses a random value for NTP sessions and 0 for PTP sessions (`rtp_common.c:78-82`). Receivers ignore it on the realtime stream (`[sps] rtp.c:281-282` commented out; `[sps] rtp.c:1609` decodes as `ALAC_44100_S16_2` unconditionally). |
| 12 | n | ciphertext | ALAC frame encrypted as in Section 1.3 (same length as plaintext) |
| 12+n | 16 | Poly1305 tag | |
| 28+n | 8 | nonce | the low 8 bytes of the 12-byte nonce, Section 1.3 |

Receiver-side evidence for the same layout: `[sps] rtp.c:1558-1619` (seq at +2, timestamp at +4, AAD = 8 bytes at
+4, ciphertext from +12, nonce = last 8 bytes), `[ap2r] ap2/connections/audio.py:20-41` (`nonce = data[-8:]`,
`tag = data[-24:-8]`, `aad = data[4:12]`, `payload = data[12:-24]`). `[sps]` drops any datagram of 36 bytes or
fewer (`rtp.c:1803`), so a packet must carry at least one ciphertext byte; an ALAC frame is never empty.

Sequence-number rules: the receiver uses the sequence number to detect loss and order packets, and the timestamp
to schedule them. `[sps]` syncs its ring buffer to whatever sequence number arrives first (`player.c:1500-1507`)
and requests retransmission of gaps (Section 2.4). Never reuse a sequence number for different audio within the
backlog window; after a flush continue counting (`[ot]` sends `FLUSH` with the next `seq`/`rtptime`,
`airplay.c:2401-2417`).

Redundancy: `[ap2r] audio.py:42-135` documents RFC 2198 redundant blocks (payload type 97, feature bit 61). The
SpeakerA does not advertise bit 61 and no sender in the reference set emits them. Not used.

### 1.3 Payload encryption

Algorithm: ChaCha20-Poly1305 IETF (RFC 8439, 12-byte nonce, 16-byte tag), one AEAD operation per packet.

- Key: the 32-byte `shk` from the stream `SETUP`. Receivers use it as-is: `[sps] rtp.c:1576-1597`
  (`crypto_aead_chacha20poly1305_ietf_decrypt(... conn->session_key)`), `[ap2r] audio.py:545-548`.
  How senders choose it:
  - `[ot]`: the first 32 bytes of the pair-verify shared secret (`airplay.c:2630` sends
    `session->shared_secret, AIRPLAY_AUDIO_KEY_LEN`; `airplay.c:96-98`: "for transient pairing the key_len will
    be 64 bytes, but only 32 are used"; `airplay.c:1438-1481` builds the packet cipher from the same bytes;
    `airplay.c:3548-3573` obtains them from `pair_verify_result`).
  - `[pyatv]`: derives a key from the event-channel HKDF output and admits it is arbitrary
    (`airplayv2.py:117-125`: "It doesn't really matter what the key is (could be hardcoded) as it's merely a
    security feature").
  - Decision: do what `[ot]` does (shared secret bytes 0..31) because it is the proven choice against Apple-SDK
    receivers; the `SETUP` travels inside the encrypted control channel, so secrecy of `shk` holds either way.
    Test random bytes once (Section 9, Q4). SPEC-control.md 4.4 originally said "32 random bytes"; it now records
    this decision (SPEC.md D4).
- Nonce: 12 bytes = 4 zero bytes || 8-byte counter, little-endian in both senders. `[pyatv]
  support/chacha20.py:76-106` (`Struct("<LQ").pack(0, counter)`, counter +1 per packet at 57-59); `[ot] airplay.c:1919-1931`
  (zeroed 12 bytes, `memcpy(nonce + 4, &pkt->seqnum, 2)`, i.e. the 16-bit sequence number in host order, which is
  little-endian on every platform OwnTone runs on). The 8 bytes at nonce[4..11] are appended verbatim to the
  packet; the receiver front-pads them with 4 zero bytes (`[sps] rtp.c:1577-1583`, `[ap2r] audio.py:546` passes
  the 8-byte nonce to PyCryptodome, which treats an 8-byte ChaCha20-Poly1305 nonce the same way).
  Decision: a 64-bit packet counter starting at 0, LE, never reset within a session key. `[ot]`'s 16-bit
  sequence number repeats every 65 536 packets (8.7 minutes), which is a nonce reuse under one key; receivers
  do not check, but there is no reason to copy it.
- Associated data: the 8 bytes at packet offset 4..11 (RTP timestamp and SSRC). `[pyatv] airplayv2.py:194`
  (`aad = rtp_header[4:12]`), `[ot] airplay.c:1937-1938` ("Timestamp and SSRC are used as AAD = pkt->header + 4,
  len 8"), `[sps] rtp.c:1594-1595`, `[ap2r] audio.py:32`.
- Output: `header(12) || ciphertext(n) || tag(16) || nonce[4..11](8)`: `[pyatv] airplayv2.py:183-208` (packet assembled at 204: `rtp_header + audio + nonce[-8:]`),
  `[ot] airplay.c:1933-1949`.

Retransmissions must be byte-identical to the original packet (same nonce, same ciphertext). Keep the fully
built packet in the history buffer (`[pyatv] stream_client.py:596-599` stores the encrypted packet; `[ot]`
re-encrypts with the sequence-number nonce so the result is identical, `airplay.c:1929-1930`).

Worked example, packet with seq 0x1234, timestamp 0x00015888, SSRC 0xDEADBEEF, counter 7:

```
80 60 12 34  00 01 58 88  DE AD BE EF   header; AAD = 00 01 58 88 DE AD BE EF
(ciphertext, same length as the ALAC frame)
(16-byte Poly1305 tag)
07 00 00 00 00 00 00 00                 nonce[4..11]; full nonce = 00 00 00 00 07 00 00 00 00 00 00 00
```

### 1.4 ALAC framing of each packet

Each RTP payload is exactly one ALAC frame produced by `ALACEncoder::Encode` with the encoder frame size set to
352 (`[alac] codec/ALACEncoder.cpp:1023-1162`). The bitstream of a stereo frame is:

| Bits | Field | Source |
|---|---|---|
| 3 | element type `ID_CPE` = 1 | `ALACEncoder.cpp:1036-1040`; element codes `ALACBitUtilities.h:60-71` |
| 4 | element instance tag, 0 | `ALACEncoder.cpp:1040` |
| 12 | unused, 0 | `ALACEncoder.cpp:449` |
| 1 | partial-frame flag (1 if fewer than 352 frames) | `ALACEncoder.cpp:313, 450` |
| 2 | bytes shifted (0 for 16-bit) | `ALACEncoder.cpp:303-308, 450` |
| 1 | escape flag (0 = compressed, 1 = raw PCM) | `ALACEncoder.cpp:437-446, 530-536` |
| 32 (only if partial) | sample count | `ALACEncoder.cpp:451-452` |
| 8 + 8 | mixBits, mixRes (stereo decorrelation) | `ALACEncoder.cpp:453-454` |
| 8, 8, 16 x numU | mode/denShift, pbFactor/numU, predictor coefficients (channel U) | `ALACEncoder.cpp:460-463` |
| 8, 8, 16 x numV | same for channel V | `ALACEncoder.cpp:465-468` |
| var | adaptive-Golomb coded residuals, U then V | `ALACEncoder.cpp:500, 515`; `ag_enc.c:250` |
| 3 | `ID_END` = 7 | `ALACEncoder.cpp:1141` |
| pad | zero bits to a byte boundary | `ALACEncoder.cpp:1144` |

Sizes: silence encodes to 32 bytes, music to roughly 400-900 bytes, white noise escapes to the raw size
(1412 bytes; see the vectors). Upper bound used by the encoder for its output buffer:
`352 * 2 * ((10 + 32) / 8) + 1 = 3521` bytes (`ALACEncoder.cpp:1280-1283`); for 16-bit input the practical maximum
is the escape frame, 1412 bytes, so a full packet is 12 + 1412 + 24 = 1448 bytes of UDP payload, under a
1500-byte MTU.

The receiver decodes with a decoder initialised from the "magic cookie" or equivalently the SDP `fmtp` line
(Section 5.2). AirPlay 2 has no `ANNOUNCE`, so the receiver assumes the standard parameters:
`[sps] rtp.c:1609` passes `ALAC_44100_S16_2`, `[sps] player.c:549-554` sets 44 100 Hz / 352 frames from that.

The last packet of a track may be shorter than 352 frames (partial frame, allowed by the bitstream) but keep
the pipeline simple: always pad to 352 frames with zeros (`[pyatv] stream_client.py:576-579` does the same).
Encoder state (last mixRes, predictor coefficients) persists across frames by design and improves compression
(`ALACEncoder.cpp:292-298, 375, 1307-1315`); this does not matter for interoperability (every frame is
self-describing) but it matters for the bit-exact test vectors (Section 5.4).

### 1.5 Timestamps, latency and the first-packet anchor

Definitions (all in frames at 44 100 Hz):

- `ts(k)` = RTP timestamp of packet k = `ts0 + 352*k` (mod 2^32).
- The sync packet (Section 2.3) says: "at wall-clock time `T`, the frame that should be leaving the loudspeaker is
  `now_frame`". The receiver therefore schedules packet k for `T + (ts(k) - now_frame) / 44100` seconds.
- `L` = the sender-chosen latency = `ts(next packet to send) - now_frame` at the moment a sync packet is built.
  The packet sent "now" plays `L/44100` seconds after the sync time. The receiver may add its own fixed
  output delay on top (`[sps]` adds 11 025 frames in several code paths, see below).

What the three implementations do:

| | `[pyatv]` | `[ot]` (NTP) | Apple senders as observed by `[sps]` (PTP) |
|---|---|---|---|
| `L` | 66 150 (`22050 + 44100`, `protocols/raop/protocols/__init__.py:28,51`) | `output_buffer_samples - input_buffer_samples` where `output_buffer_samples = (start_buffer_ms - 250) * 44.1`; the documented example gives 88 200 at start (`airplay.c:1196-1207, 2119-2155`) | 77 175 (`rtp.c:1696-1698` "Stream-specified latency ... normally it is 77175") |
| first `ts0` | `latency` (`rtptime` property is `head_ts - (start_ts - latency)` with `head_ts == start_ts`, `__init__.py:55-57`) | random (`rtp_common.c:75`) | n/a |
| `now_frame` in the first sync | `ts0 - L` (`stream_client.py:115`) | `ts0 - 88200` (`airplay.c:2119-2127`) | `ts0 - 77175` |
| what the receiver does with 11 025 | - | - | `[sps] rtp.c:1699, 1728`: adds 11 025 to the latency and anchors `now_frame - 11025` at `T`, so the packet sent "now" plays `(77175 + 11025)/44100 = 2.000 s` after `T` |

Decision for this sender: `L = 77175` frames (1.75 s on the wire, 2.0 s at an `[sps]` receiver) by default,
configurable per session between `latencyMin` and `latencyMax`. It matches what Apple senders put on the wire and
keeps `[sps]`'s "normally it is 77175" path silent. `[ot]`'s 88 200 works too (Sonos, AirPort Express, Apple TV
and HomePod are in its user base) and only costs 0.25 s more delay. Measure the real output delay on the SpeakerA
(Section 9, Q1) before freezing the number.

Anchoring at start: choose `ts0` (random, like `[ot]`, or any value); take `T0` = current master-clock time; the
first sync packet carries `now_frame = ts0 - L`, `T0`, and `ts0`; the first audio packet carries `ts0` with the
marker bit; subsequent packets are sent so that packet k leaves the sender at `T0 + 352*k/44100` (paced by the
master clock, not by the arrival of PCM from librespot). The receiver does not care whether packets arrive a
little early; it cares that the packet with timestamp `ts(k)` arrives before `T + (ts(k) - now_frame)/44100`
minus its own buffering margin. `[sps]` drops a packet whose play time has already passed and treats
`conn->latency` as "the time window within which packets can be accepted without being too late"
(`rtp.c:1727`).

`latencyMin`/`latencyMax` (11 025 / 88 200): no reference receiver enforces them on the realtime stream;
`[sps]`'s AirPlay 1 path clamps the sync-derived latency to the SDP `a=min-latency`/`a=max-latency`
(`rtp.c:441-444`, parsed at `rtsp.c:3554-3559`), which is the Classic-AirPlay equivalent of these keys. Keep `L`
inside the range you advertise.

`audioLatencyMs` / `audioLatencies` (and `Audio-Latency` from RECORD, SPEC-control.md 4.5, same treatment):
`[ap2r] ap2-receiver.py:155-166` shows the `/info` key `audioLatencies`
(`inputLatencyMicros`, `outputLatencyMicros`, optional `type`/`audioType`) that Apple-SDK receivers may publish;
the SpeakerA does not publish it (Appendix A). The AirPlay 2 `RECORD` response carries `Audio-Latency: 0`
(`[sps] rtsp.c:903-910`, `[ap2r] ap2-receiver.py:727-731`), unlike Classic AirPlay's `Audio-Latency: 11025`
(`[sps] rtsp.c:924-933`). Treat any published output latency as a per-receiver offset to subtract from that
receiver's `now_frame` (Section 3.3); with none published, assume 0 and tune by ear (Section 9, Q2).

### 1.6 Receiver behaviours that constrain the sender (from `[sps]`)

- The first control-port packet of a session must have bit 0x10 set in byte 0 (`0x90`, the "sentinel"):
  packets without it arriving within 2 ms of the control thread's start are discarded as leftovers
  (`rtp.c:1650-1666`). This is the "extension bit on the first sync packet" rule; `[pyatv]` and `[ot]` both
  send `0x90` on the first sync (`stream_client.py:112`, `airplay.c:2185`).
- The control thread learns our control address from the source of the first control packet and sends
  retransmit requests there (`rtp.c:1672-1677`): send the first sync from the socket that will answer
  retransmit requests.
- If no control packet arrives for 7 seconds on a realtime stream, the anchor is reset and the receiver waits
  for a new sentinel sync (`rtp.c:1757-1766`). One sync per second (Section 2.3) is comfortably inside this.
- `[sps]` can decode the realtime stream only as ALAC 44.1 kHz/16-bit (`rtp.c:1609`); `ct`, `sr`, `spf`,
  `audioFormat` do not change that.
- A `SET_PARAMETER` volume before `RECORD` is accepted; `[ot]` sends it last in its start sequence because a Sonos
  Symfonisk ignores it otherwise (`airplay.c:3655-3656`).

## 2. Timing in NTP mode, sync packets and the control port

### 2.1 Clocks and the NTP timestamp format

The 64-bit NTP timestamp is `seconds(32) || fraction(32)`, both BE, seconds since 1900-01-01 (Unix time
plus 0x83AA7E80 = 2 208 988 800), fraction in units of 2^-32 s.

- `[pyatv] protocols/raop/timing.py:11-16`: `ntp_now()` from the wall clock (`time_ns()`):
  `((sec + 0x83AA7E80) << 32) | ((usec << 32) / 1e6)`.
- `[ot] outputs/airplay.c:522-560`: `timespec_to_ntp` from `CLOCK_MONOTONIC` plus the same epoch delta;
  `frac = nsec * 1e-9 * 2^32`. Also `rtp_common.c:44-56`.
- Receiver conversion: `[sps] rtp.c:789-806`: `ns = sec * 1e9 + ((frac * 1e9) >> 32)`.

The absolute epoch does not matter to anyone: the receiver only measures offsets between its own clock and the
values we put into timing replies and sync packets (`[sps] rtp.c:779-885`), and the sync packet's time is
compared with the timing replies. What matters is that **one** clock feeds both the timing responder and the
sync packets, that it never jumps (no wall-clock corrections), and that its rate is the rate at which we pace
audio packets. Use a monotonic clock (`Stopwatch.GetTimestamp()`, 100 ns resolution on Windows, 1 ns on Linux
.NET) with an arbitrary fixed epoch chosen at process start (for example current Unix time plus the NTP delta
captured once, then advanced by monotonic elapsed time). This is `[ot]`'s choice, not `[pyatv]`'s.

Conversions used by `[pyatv]` (`timing.py:24-41`): `ntp2ts(ntp, rate) = ((ntp >> 16) * rate) >> 16`,
`ts2ntp(ts, rate) = (int((ts << 16) / rate)) << 16`. These are convenient but lose the low 16 bits of the
fraction (15 µs); use 64-bit integer arithmetic on nanoseconds instead.

### 2.2 Timing request and reply (receiver -> sender -> receiver)

The receiver sends timing requests to the `timingPort` we announced in the session `SETUP`. Both packets are 32
bytes, all fields BE. Layout (request, type 0x52; reply, type 0x53):

| Offset | Size | Request (receiver -> us) | Reply (us -> receiver) |
|---|---|---|---|
| 0 | 1 | `0x80` | `0x80` |
| 1 | 1 | `0xD2` (0x80 \| 0x52) | `0xD3` (0x80 \| 0x53) |
| 2 | 2 | sequence, `[sps]` sends `htons(7)` | copy of the request's sequence (`[ot]` copies only byte 2, leaving byte 3 zero, `airplay.c:2307`; `[pyatv]` writes 7, `protocols/__init__.py:130`) |
| 4 | 4 | zero ("filler") | zero |
| 8 | 8 | origin timestamp, zero in `[sps]` (`rtp.c:632-633`) | **reference** timestamp = the request's transmit timestamp (bytes 24..31 of the request): `[ot] airplay.c:2310`, `[pyatv] __init__.py:132-134` |
| 16 | 8 | receive timestamp, zero | our receive time (NTP) |
| 24 | 8 | transmit timestamp: the receiver's send time, zero in `[sps]` (`rtp.c:633`) but used by `[ot]`'s copy | our transmit time (NTP), taken after the receive time (`[ot] airplay.c:2318-2336`; `[pyatv]` uses the same value for both, `__init__.py:135-138`) |

Receiver evidence: `[sps] rtp.c:595-664` (sender thread: `leader 0x80, type 0xd2, seqno htons(7), filler 0,
origin/receive/transmit 0`; the first three requests 300 ms apart, then one every 3 s) and `rtp.c:779-885`
(reply processing: `distant_receive_time` from bytes 16..23, `distant_transmit_time` from 24..31; the round trip
is measured locally and the reply is ignored if it exceeds 200 ms; remote processing time
`transmit - receive` is subtracted from the round trip; `remote_time = distant_transmit + return_time/2`; the
sample with the lowest dispersion in a history window is used; a least-squares fit over chosen samples estimates
the drift `local_to_remote_time_gradient`, used at `rtp.c:169-187`).

Reply from the same socket to the source address of the request; answer immediately (the receiver subtracts our
processing time, but only if transmit >= receive, `rtp.c:810-815`). Take the receive timestamp as early as
possible (`[ot]` reads the clock before `recvfrom`, `airplay.c:2273-2281`, which is slightly early rather than
late; either is fine at this precision).

Note that `[sps]`'s timing requester exists only in its AirPlay 1 path; its AirPlay 2 path has no timing socket at
all (`rtsp.c:2809-2821, 2898-2910` open only a control and a data port, and the `SETUP` reply's `timingPort` is a
dummy 0, `rtsp.c:2654`) because it uses PTP. An AirPlay 2 receiver in NTP mode (Apple-SDK devices talking to
`[ot]`) sends the 0xD2 requests to the `timingPort` of the session `SETUP`; `[ap2r]` does not implement the
exchange at all (its "ntp" listener only logs, `ap2-receiver.py:536-538`, `ap2/connections/event.py`).

### 2.3 Sync packets (sender -> receiver control port), type 0x54

Sent to the receiver's `controlPort` from our control socket. 20 bytes, all fields BE.

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 1 | V/P/X | `0x90` on the first sync of a stream and on the first sync after a join or flush (`[pyatv] stream_client.py:112`, `[ot] airplay.c:2185`); `0x80` afterwards |
| 1 | 1 | type | `0xD4` (0x80 \| 0x54) |
| 2 | 2 | "length"/flags | `0x0007`. `[pyatv] stream_client.py:114`, `[ot] rtp_common.c:327-328`. `[sps] rtp.c:415-440` treats 7 as "iTunes, add 11 025 frames of fixed latency" and 4 as "AirPlay, do not"; `[ot]` says that for AirPlay 2 the field seems unused and the latency comes from `latencyMin`. `[ap2r] control.py:22-30` has seen 4 and 7. Send 7. |
| 4 | 4 | `now_frame` | RTP timestamp that should be at the loudspeaker at time T (`[pyatv]`: `rtptime - latency`; `[ot]`: `cur_stamp.pos` from `airplay.c:2129-2155`, i.e. `pos + input_buffer_samples - output_buffer_samples`; `[sps] rtp.c:403` reads it as `rtp_timestamp_less_latency`) |
| 8 | 4 | NTP seconds of T | the sender clock time this sync describes (`[pyatv]`: `ts2ntp(head_ts)`; `[ot]`: the player timestamp of the last write) |
| 12 | 4 | NTP fraction of T | |
| 16 | 4 | `now_frame + L` | the RTP timestamp of the next packet to be sent (`[pyatv]`: `rtptime`; `[ot]`: `session->pos`, "must match the start rtptime communicated via RTSP, otherwise some devices will fail e.g. Eufy Genie", `rtp_common.c:340-343`; `[sps] rtp.c:402` reads it as `sync_rtp_timestamp`) |

Receiver semantics (`[sps] rtp.c:389-546`): `la = sync_rtp_timestamp - rtp_timestamp_less_latency` (+ 11 025 when
flags == 7 or for certain sender versions), clamp, then `anchor_rtptime = sync_rtp_timestamp - la` and
`anchor_time = T`. In other words frame `now_frame` plays at `T` plus the receiver's own fixed delay, and every
other frame is scheduled by linear extrapolation at 44 100 frames per second of **sender** time, converted to
local time through the offset and drift measured in Section 2.2 (`rtp.c:1131-1156`). The receiver also derives
its notion of the stream's frame rate from successive syncs (`rtp.c:497-508`), so the sync time T and
`now_frame` must advance consistently: `now_frame(n+1) - now_frame(n) = (T(n+1) - T(n)) * 44100` exactly, up to
rounding. Build both from the same frame counter and the same clock: `T(n) = T0 + (now_frame(n) - now_frame(0))
/ 44100` or, equivalently, compute `now_frame` from the clock.

Cadence: one per second (`[pyatv] stream_client.py:133` sleeps 1.0 s; `[ot] rtp_common.c:90-94, 228-238` sends
one every `sample_rate` frames of audio, i.e. every 44 100 frames). `[ot]` additionally sends an initial `0x90`
sync to every session in state CONNECTED on every write until that session becomes STREAMING
(`airplay.c:2182-2191, 4269-4282`), so a newly joined receiver always gets a sync before or with its marker
packet.

Timing of the first sync relative to the first audio packet: `[ot]` sends sync first, then the audio packet
(`airplay_write`: `packets_sync_send` then `packets_send`, `airplay.c:4249-4265`); `[pyatv]` starts the sync task
before `RECORD` and streams after `FLUSH` (`stream_client.py:396-453`). `[sps]` ignores syncs that arrive before
it has completed a timing exchange (`rtp.c:389, 547-548`, "Sync packet received before we got a timing packet
back"), and it does not start buffering audio until it can compute the play time of the first packet
(`player.c:2361-2463`), so in NTP mode the first second of audio may be discarded if the receiver's timing
requests have not completed. Mitigation: send the initial sync, then start audio; the receiver requests timing
300 ms apart at start (`rtp.c:656-657`), and the 2 s latency window covers that.

AirPlay 2 with PTP timing uses a different, 28-byte sync packet (type 0xD7) on the same port; it is specified in
Section 4.4 because it belongs with PTP.

### 2.4 Control port: retransmit requests (0x55) and replies (0x56)

Retransmit request, receiver -> sender control port, 8 bytes (`[sps] rtp.c:1183-1272`, `[ot]
airplay.c:2347-2395`, `[pyatv] packets.py:33-35`, `[ap2r] control.py:88-95, 109-124`):

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 1 | | `0x80` |
| 1 | 1 | type | `0xD5` (0x80 \| 0x55). `[sps]` sends `0xD5` for AirPlay 2 and `0x55 \| 0x80` (the same byte) for AirPlay 1, `rtp.c:1194-1203` |
| 2 | 2 | sequence | `[sps]` sends 1; `[ap2r]` sends the packet length in 32-bit words; ignore |
| 4 | 2 | first missing sequence number | |
| 6 | 2 | count of consecutive missing packets | |
| 8 | 8 | optional NTP timestamp of the first missing packet | 16-byte variant seen by `[ap2r] control.py:93-95, 118-122`; `[sps]` and `[ot]` use the 8-byte form. Accept both lengths. |

`[sps]` backs off for 300 ms after a send error (`rtp.c:1211-1214`) and sends requests from its AirPlay 2 control
socket, i.e. the socket bound to the `controlPort` it announced in the stream `SETUP` response (`rtp.c:1224-1230`
`sendto(conn->ap2_control_socket, ...)`, bound at `rtsp.c:2809-2818`); `[ot]` matches an incoming request to a
session **by source address only** (`airplay.c:2380`, `session_find_by_address`), so a receiver whose requests come
from a port other than the announced one is still served. **Lookup rule for our control port** (`IControlPort`):
match registered receivers by source address first; compare the source port with the announced `controlPort` only
to disambiguate several registered receivers behind one address (two sessions on one host, two fake receivers on
loopback). The reply goes to the request's source endpoint (wrapped form) or to the registered data endpoint
(raw form).

Reply, sender -> receiver. Two accepted forms:

1. Over the control port, wrapped (type 0x56): `80 D6 <seq of original, 2 bytes> <original packet, 12-byte header
   + ciphertext + tag + nonce>`. `[pyatv] stream_client.py:155-170`. The receiver strips 4 bytes and feeds the
   original packet: `[sps] rtp.c:1735-1739` (AirPlay 2 control receiver, `decipher_player_put_packet(packet + 6)`
   = 4 bytes wrapper + 2 bytes into the original header), `rtp.c:550-570` (AirPlay 1), `[ap2r]
   control.py:98-100, 233-238`.
2. Over the data port, the original packet verbatim: `[ot] airplay.c:2039-2078, 1954-1977` (`packets_resend`
   calls `packet_send`, which writes to `session->server_fd`, the audio socket). `[sps]`'s AirPlay 2 data
   receiver does not look at the type byte (`rtp.c:1826-1828`), so this also works; its AirPlay 1 receiver
   additionally accepts a wrapped `0x56` on the data port (`rtp.c:244-250`). `[ot]` notes (`airplay.c:2052-2063`)
   that iOS replies with the wrapped form on the control port and that one of its amplifiers does not
   understand that, so it uses the data port.

Decision: reply with form 1 on the control port (it is what Apple's senders do and what every receiver in the
reference set parses), and make form 2 a per-receiver option to switch on if a device keeps re-requesting.

History buffer: 1000 packets in both senders (`[pyatv] stream_client.py:44`, `[ot] airplay.c:101`), i.e. 8 s
of audio at 352 frames per packet; it must cover at least the latency `L` plus the receiver's retransmit
reaction time. Index by sequence number modulo the buffer size, store the complete encrypted packet, and drop
requests outside `[next_seq - 1000, next_seq)` (`[ot] rtp_common.c:190-226` shows the wrap-safe range check).
Clear it on `TEARDOWN`, not on `FLUSH` (`[ot]` keeps the ring across flushes: `rtp_session_flush` only resets the
length counter, `rtp_common.c:111-116`; receivers will not ask for pre-flush packets because the flush discards
them).

Unknown control packets: `[sps]` logs and ignores anything other than 0xD4/0xD6 (AirPlay 1) or 0xD7/0xD6
(AirPlay 2) on its control port; log and ignore anything other than 0xD5 on ours.

## 3. Multi-receiver (group) playback with the realtime stream

This section describes how `[ot]` keeps N receivers in sync (`outputs/airplay.c`, GPL: described, not
transcribed) and turns it into requirements for `Airplay2.Sender`.

### 3.1 One timeline, N sessions

`[ot]` has one "master session" per audio quality and timing mode (`airplay.c:211-238, 1147-1221`): it owns the
RTP session (one sequence counter, one timestamp counter `pos`, one retransmit ring), the ALAC encoder and the
input buffer. Every receiver session points at the master session (`airplay.c:1627`). On each write from the
player (`airplay_write`, `airplay.c:4233-4283`):

1. `timestamp_set` records the player's timestamp for this write and computes `cur_stamp.pos`, the frame that
   should be at the loudspeakers now (`airplay.c:2129-2155`).
2. `packets_sync_send` sends an initial sync (`0x90`) to every session in state CONNECTED and, if a second of
   audio has elapsed, a periodic sync (`0x80`) to every session in state STREAMING (`airplay.c:2157-2198`). The
   sync packet is built once per session from the shared `cur_stamp`, minus that session's `offset_samples`
   (`airplay.c:2177-2180`).
3. For every full 352-frame block, `packets_send` encodes **once** and sends the same RTP packet to every
   session; each session encrypts with its own key (`packet_encrypt` per session, `airplay.c:2081-2117`), and
   sessions in CONNECTED get the marker bit (`airplay.c:2100-2106`). Then the packet is committed to the ring and
   the counters advance (`rtp_common.c:176-188`).
4. Sessions that were CONNECTED are promoted to STREAMING (`airplay.c:4269-4282`).

Requirements for us:

- Exactly one `Timeline` object per group: master clock, `ts0`, `next_seq`, `next_ts`, the encoded-packet
  ring (plaintext ALAC frames plus per-receiver encrypted copies, or re-encrypt on retransmit with the stored
  nonce), and the pacer that emits one packet every 352/44100 s.
- One `TimingResponder` (NTP) or one `PtpMaster` (PTP) per process, shared by all sessions: `[ot]` uses a
  single global timing socket and a single control socket (`airplay.c:461-465`) and maps incoming retransmit
  requests to sessions by source address (`airplay.c:2380-2386`, `session_find_by_address`). Per-receiver
  timing *state* lives on the receiver (it computes its own offset to our clock); we only answer.
- Per receiver: encryption key and nonce counter, data/control destination addresses, state
  (CONNECTED/STREAMING), `offset_frames` (Section 3.3), and the event channel.

Because every receiver computes the play time of frame `x` as `T_sync + (x - now_frame)/44100` relative to the
same sender clock, and all receivers get the same `(now_frame, T_sync)` pairs (up to `offset_frames`), they play
the same frame at the same instant to within their individual clock-offset measurement error. Nothing else is
needed for "sync"; the sender does not need to know the receivers' clocks.

### 3.2 Adding and removing a receiver mid-stream

Add (`[ot] airplay_device_start`, `airplay.c:4160-4172`, sequence `AIRPLAY_SEQ_START_PLAYBACK`,
`airplay.c:3645-3657`): `SETUP` (session) -> `RECORD` -> `SETPEERS` (PTP only) -> `SETUP` (stream) ->
`SET_PARAMETER volume`. The session is then CONNECTED and the next write gives it an initial sync, the marker
packet and then the regular packets; it starts playing `L` frames later, in sync with the others, because its
sync packet carries the group's `now_frame` at the group's `T`. The new receiver's first packet is simply the
next packet of the timeline; there is no catch-up, and the ring buffer is not replayed to it. (`[sps]` would
treat anything older as late and drop it, `rtp.c:1727`; `player.c:2365-2463`.)

Remove: `TEARDOWN` for that session (`AIRPLAY_SEQ_STOP`, `airplay.c:3665`, payload `airplay.c:2422-2452`: an empty
plist closes the connection, a plist with `streams` tears down only the stream, `[sps] rtsp.c:2295-2326`), then
free the session; the master session lives on while any session uses it (`airplay.c:1118-1145`).

Resync after a flush (`[ot] airplay_device_flush`, `airplay.c:4186-4199`, `AIRPLAY_SEQ_FLUSH`, `airplay.c:3662`):
send `FLUSH` with `RTP-Info: seq=<next_seq>;rtptime=<next_ts>` (`airplay.c:2401-2417`), set the session back to
CONNECTED (`airplay.c:3252-3256`), so the next write re-sends an initial sync and a marker packet. The timeline
itself is not reset (`rtp_session_flush` keeps `pos` and `seqnum`, `rtp_common.c:111-116`). On the receiver,
`FLUSH` discards buffered packets with timestamps before `rtptime` (`[sps] rtsp.c:2329-2363, player.c:5252-5277`:
"flush all packets up to, but not including, this one"), and the next marker packet plus the sync re-anchor
playback (`player.c:2238-2320` resets `first_packet_timestamp` on resync).

### 3.3 Per-receiver latency compensation

`[ot]` subtracts a per-device `offset_samples` (user configuration `offset_ms`) from `now_frame` in that device's
sync packets only (`airplay.c:282-284, 2177-2180`). Subtracting `d` frames tells the receiver that frame
`now_frame - d` is at the speaker at `T`, i.e. everything plays `d` frames **later** on that receiver. Use a
positive `offset_frames` for a receiver whose output chain is faster than the others, derived from
`audioLatencies.outputLatencyMicros` when published (Section 1.5) plus a user trim stored per receiver in the
`receivers` table. The audio packets themselves are identical for all receivers; only the sync packet differs.

### 3.4 The hard parts

- Clock drift: each receiver measures our clock against its own (NTP: `[sps] rtp.c:893-968`, least-squares
  slope over chosen timing samples after a 60 s settling time; PTP: nqptp "smooths the offset by clamping any
  decreases to a small value", `nqptp-shm-structures.h:127-135`) and resamples or drops/inserts frames to
  follow us. We must (a) pace packets from the same clock that stamps syncs and timing replies, (b) never let
  the wall clock correct that clock, and (c) accept that librespot delivers audio at the Spotify decoder's rate,
  which is the same nominal 44 100 Hz but arrives in bursts; the ring buffer in Section 8 absorbs the bursts, the
  pacer drains it at exactly 352 frames per 7.98 ms of master-clock time.
- Receivers with different latencies: the `L` in the sync packet is the same for everyone; per-receiver
  `offset_frames` moves individual receivers. A receiver that needs **more** buffering than `L` provides (slow
  DAC, Bluetooth output) cannot be fixed by the sender except by raising `L` for the whole group; `[sps]`
  warns and clamps when its backend wants more than the stream latency (`rtp.c:1704-1726`).
- Re-anchoring after seek, pause or skip: do not rewind timestamps. Keep `next_ts` monotonic, `FLUSH` with
  the next values, send a new `0x90` sync with the current `(now_frame, T)` and a marker packet. A receiver that
  missed the sync (UDP) will still start on the next periodic sync one second later; a receiver that missed the
  marker does not care (the marker is advisory, `[sps]` ignores it).
- Joining receivers and the 2 s window: a receiver that joins mid-track plays its first frame `L` after its
  first sync; the group is not paused for it (`[ot]` does the same). The web app should show "joining" for ~2 s.
- Group leader fields: `groupUUID`, `groupContainsGroupLeader`, `isMultiSelectAirPlay`,
  `isTightSyncGroupLeader` are session `SETUP` keys (`[ap2r] ap2/connections/session_properties.py:9-36`,
  `[sps] rtsp.c:2501-2526`, `[ot] airplay.c:2755-2756`, `[pyatv] airplayv2.py:62-63`). `[sps]` only stores
  them and republishes `gid`/`gcgl` in its TXT record (`rtsp.c:1040-1061, 2663-2669`). Send a constant
  `groupUUID` per group and `groupContainsGroupLeader: false`; nothing in the audio plane depends on them.
- PTP instead of NTP: with `[ot]`'s current code, any receiver advertising feature bit 41 (`SupportsPTP`, with
  `srcvers >= 366`) is driven with PTP unless configured otherwise (`airplay.c:420, 4066-4067`); NTP is only the
  fallback when the PTP daemon cannot be started (`airplay.c:4336-4342`). The SpeakerA advertises bit 41 and not
  bit 45. `docs/PLAN.md` assumes NTP realtime is "the path a third-party multi-room sender has already proven";
  that was true of OwnTone releases before `libairptp` landed and remains true for `ptp_disable`d devices, but
  the current default is PTP. Keep the stream layer pluggable as planned and treat the NTP-vs-PTP choice as
  Section 9, Q3. **Resolution** (SPEC.md D1): NTP first as PLAN.md says, gated by probe P1; the timing protocol is
  a pluggable part of the stream layer, so a refusal moves the realtime stream to PTP (0xD7 syncs, Section 4.4)
  without touching the ALAC/RTP/group code.

## 4. The buffered stream (type 103) and the PTP master

The buffered stream is what Apple's own senders use for music. It differs from the realtime stream in
transport (TCP, length-prefixed), clocking (PTP, with the sender as grandmaster; no sync packets), anchoring
(`SETRATEANCHORTIME` instead of the sync packet), flushing (`FLUSHBUFFERED` with sequence ranges) and in
how far ahead the sender may run (up to the receiver's advertised buffer). Everything in this section is
written so a `BufferedStream` can be added behind the same `IStream` interface later.

### 4.1 Negotiation differences

| Key / item | Buffered value | Source |
|---|---|---|
| session `SETUP` `timingProtocol` | `"PTP"` plus `timingPeerInfo`/`timingPeerList` (Section 4.3) | `[ot] airplay.c:2718-2770`, `[sps] rtsp.c:2423-2431, 2482-2662` |
| stream `type` | `103` | `[sps] rtsp.c:2921-2970`, `[ap2r] stream.py:293` |
| `audioFormat` | `0x40000` ALAC/44100/16/2, `0x400000` AAC-LC/44100/2, `0x80000` ALAC/44100/24/2, `0x200000` ALAC/48000/24/2, `0x800000` AAC-LC/48000/2 | bit table `[ot] airplay.c:2576-2611`; `[sps] rtsp.c:2926-2933` ("seems to be only the initial format -- it seems as if it can change dynamically") |
| `ct` | 2 (ALAC) or 4 (AAC-LC, per `[ap2r]`) / 3 (AAC, per `[ot]`) | disagreement noted in Section 1.1 |
| `spf` | 352 (ALAC) or 1024 (AAC-LC, `[ap2r] audio.py:425` default, `[sps] player.c:545` sets 1024 for non-ALAC) | |
| `shk` | 32 bytes | same as realtime |
| response `dataPort` | **TCP** port to connect to | `[sps] rtsp.c:2935-2961` (`SOCK_STREAM`, `listen`), `[ap2r] stream.py:345` |
| response `audioBufferSize` | bytes the receiver will buffer; `[sps]`: 8 MiB (`rtsp.c:4175`, `2962-2963`) | `[ap2r] stream.py:421-423` |
| response `controlPort` | UDP, still opened for 0xD5/0xD6 and for 0xD7 syncs if the sender sends any | `[sps] rtsp.c:2809-2821, 2981` |
| `/info` `supportedFormats.bufferStream` | bitmask of accepted `audioFormat`s; `[sps]` advertises AAC-LC 44.1/48, ALAC 44.1/16, ALAC 48/24 and optionally 5.1/7.1 | `[sps] rtsp.c:1140-1165`; absent from the SpeakerA's `/info` (Appendix A) |

`RECORD` is sent after the session `SETUP` as for realtime (`[sps] rtsp.c:903-910` replies `Audio-Latency: 0`).
`[sps]` starts the buffered reader on the stream `SETUP` (`rtsp.c:2954-2957`) but does not play until
`SETRATEANCHORTIME` with `rate = 1` arrives (`rtsp.c:1460-1492`; `ap2_buffered_audio_processor.c:222-237`).

### 4.2 Packet layout on the TCP data connection

Each packet is preceded by a 2-byte BE length that **includes the two length bytes**: `[sps]
ap2_buffered_audio_processor.c:242-260` (`data_len = ntohs(...)`, then reads `data_len - 2` bytes),
`[ap2r] audio.py:948-954` (same).

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 1 | | `0x80` |
| 1 | 3 | marker + 23-bit sequence number | byte 1 bit 7 is the RTP marker, "always set"; the remaining 23 bits are the sequence number: `[sps] ap2_buffered_audio_processor.c:273-278` (`nctohl(&packet[0]) & 0x7FFFFF`), `[ap2r] audio.py:138-143` (takes bytes 1..3 as a 24-bit integer, i.e. including the marker). Send `0x80 \| (seq >> 16)`, `seq >> 8`, `seq`. All `FLUSHBUFFERED` sequence values are masked with 0x7FFFFF too (`[sps] rtsp.c:1349, 1376-1378`). |
| 4 | 4 | RTP timestamp | frames; +352 (ALAC) or +1024 (AAC) per packet; `[sps]` checks continuity against the previous block's length (`ap2_buffered_audio_processor.c:314-320`) |
| 8 | 4 | SSRC = **format code** | the receiver selects its decoder from this field per packet: `[sps] player.h:141-152`: `ALAC_48000_S24_2 = 0x15000000`, `AAC_44100_F24_2 = 0x16000000`, `AAC_48000_F24_2 = 0x17000000`, `AAC_48000_F24_5P1 = 0x27000000`, `AAC_48000_F24_7P1 = 0x28000000`, and `ALAC_44100_S16_2 = 0x0000FACE` with the comment "this is made up". Unknown SSRCs are not decoded (`ap2_buffered_audio_processor.c:287-297, 517, 676-677`). |
| 12 | n | ciphertext | ChaCha20-Poly1305 exactly as Section 1.3: AAD = bytes 4..11, ciphertext from 12, 16-byte tag, 8-byte nonce suffix (`[sps] ap2_buffered_audio_processor.c:523-549`, `[ap2r] audio.py:20-33, 545-548` "c.update(rtp.aad) necessary at least for RTP type 103") |

The SSRC finding matters: Apple senders evidently never send ALAC 44.1 kHz/16-bit in buffered mode (otherwise
`[sps]` would have a real code for it), so a buffered implementation of this project would either need an AAC-LC
encoder (44.1 kHz, 1024 frames per packet, `AAC_44100_F24_2`) or ALAC at 48 kHz/24-bit (resample 44.1 -> 48 and
pad 16 -> 24 bit, `ALAC_48000_S24_2`). Against `[sps]` specifically, `0x0000FACE` with ALAC 44.1/16 works.
Which SSRC an Apple-SDK receiver accepts for ALAC 44.1/16 is Section 9, Q7.

AAC blocks are raw AAC (no ADTS); `[sps]` prepends a 7-byte ADTS header itself for its decoder
(`ap2_buffered_audio_processor.c:41-90, 559-577`) and mutes the first AAC block after a start or discontinuity
(`:578-583, 606-613`). The receiver's TCP window is the only flow control: `[sps]` reads into an 8 MiB ring and
decodes a block only when its play time is within `audio_decoded_buffer_desired_length + 0.1 s`
(`ap2_buffered_audio_processor.c:502-506, 683-694`), sleeping two packet durations otherwise; blocks whose play
time has passed are not even decrypted (`:521, 660-665`), blocks older than the last played are skipped
(`:617-648`). A sender can therefore push the whole `audioBufferSize` worth of packets immediately and then top
up as the socket accepts writes.

### 4.3 SETRATEANCHORTIME and FLUSHBUFFERED

`SETRATEANCHORTIME` (binary plist body, `[sps] rtsp.c:1410-1500`, `[ap2r] ap2-receiver.py:734-763`,
`[ap2r] ap2/connections/session_properties.py:81-110`):

| Key | Type | Meaning |
|---|---|---|
| `networkTimeSecs` | uint | whole seconds of the anchor time on the PTP timeline |
| `networkTimeFrac` | uint64 | fraction where the MSB is 1/2 ("Units are (1/2^64)", `session_properties.py:97`; `[sps] rtsp.c:1437-1442` converts via `(frac >> 32) * 1e9 >> 32`) |
| `networkTimeTimelineID` | uint64 | the PTP clock identity of the timeline; must equal the receiver's current master clock or it waits (`[sps] rtsp.c:1420-1428, rtp.c:1396-1434`) |
| `networkTimeFlags` | uint | no reference implementation reads it; Apple senders include it. Send 0. |
| `rtpTime` | uint32 | the RTP timestamp that plays at the anchor time (`[sps] rtp.c:1456-1457, 1510-1526`: frame `x` plays at `anchor_time + (x - rtpTime)/rate`) |
| `rate` | uint | 1 = play, 0 = pause (`[sps] rtsp.c:1460-1492`: 0 resets the anchor and sends "P" to nqptp; `[ap2r] ap2-receiver.py:745-752`) |

`[sps]` applies the anchor only after the master clock has been master for 400 ms (`rtp.c:1366-1369,
1436-1439`), and handles a master-clock change by re-deriving the anchor from its last local anchor
(`rtp.c:1403-1434`). The anchor can be re-sent at any time to re-time the stream (seek: send `FLUSHBUFFERED`,
then a new anchor with the new `rtpTime`/time pair; pause: `rate 0`; resume: `rate 1` with a fresh anchor).

`FLUSHBUFFERED` (binary plist, `[sps] rtsp.c:1294-1395`, `[ap2r] ap2-receiver.py:463-488`):

| Keys | Effect in `[sps]` |
|---|---|
| `flushUntilSeq`, `flushUntilTS` only | immediate flush: discard every buffered block with sequence number before `flushUntilSeq` (23-bit modular compare, `ap2_buffered_audio_processor.c:343-401`), stop playing, reset the anchor; playback resumes only with a new `SETRATEANCHORTIME rate 1` |
| `flushFromSeq`, `flushFromTS`, `flushUntilSeq`, `flushUntilTS` | deferred flush: when block `flushFromSeq` arrives, discard blocks up to but excluding `flushUntilSeq` without stopping playback (`:407-460`); used by Apple senders to splice tracks |

`flushUntilTS` is the timestamp of the first block to keep; `[sps]` stores it but decides by sequence number.

### 4.4 PTP: what the sender must provide as grandmaster

With `timingProtocol: "PTP"`, the sender runs an IEEE 1588-2008 grandmaster on UDP ports 319 (event) and 320
(general) and the receiver runs a slave (`[sps]` via nqptp, `AIRPLAY2.md:59-69`: "NQPTP must have exclusive
access to ports 319 and 320"). The receiver learns which clock to follow from the session `SETUP`:
`[sps] rtsp.c:2528-2560` collects `timingPeerInfo.Addresses` from our `SETUP` (our IP first) into a `T <ip>
...` command for nqptp, deferred until the stream `SETUP` (`rtsp.c:2756-2758`, `set_client_as_ptp_clock` then
`"B"`), and nqptp follows "the first IP number" (`nqptp-shm-structures.h:112-117`). `[sps]` replies with its own
`timingPeerInfo` (`Addresses` = its interfaces, `ID` = its IP, `rtsp.c:2570-2624`) and `timingPort: 0`
(`:2654`). `[ot]` additionally sends `SETPEERS` with `[receiver, our v4, our v6]` (`airplay.c:2659-2688`,
content type `/peer-list-changed`, `airplay.c:3653`) and adds the receiver's address from the response's
`timingPeerInfo.Addresses` as a unicast PTP peer (`airplay.c:3123-3178`). `[sps]` accepts and ignores `SETPEERS`
(`rtsp.c:2207-2250`).

Session `SETUP` body for PTP (`[ot] airplay.c:2718-2770`): `name`, `deviceID`, `sessionUUID`, `timingProtocol:
"PTP"`, `macAddress`, `groupUUID`, `groupContainsGroupLeader: false`, `timingPeerInfo: {ID: <uuid>,
DeviceType: 0, ClockID: <int64 clock id>, SupportsClockPortMatchingOverride: false, Addresses: [<our v4>,
<our v6>]}`, `timingPeerList: [<same dict>]`. `ClockID` is signed in the plist (`airplay.c:2741`); in code the
clock identity is an unsigned 64-bit value everywhere (`IPtpMaster.ClockId`, `SessionSetupRequest.PtpClockId`,
`SetRateAnchorTimeAsync(timelineId)`, `PtpSyncPacket.ClockId`) and is cast to `long` only when the plist is written.

What libairptp sends, which is the subset nqptp and Apple receivers demonstrably accept
(`[ot] libairptp/src/ptp_msg_handle.c`, `ptp_definitions.h`, `airptp_internal.h`, `airptp.c`):

| Item | Value | Source |
|---|---|---|
| Transport | unicast UDP to each peer; Sync and Pdelay_Resp to port 319, Announce, Follow_Up, Delay_Resp, Signaling to port 320; bound locally to 319/320 | `ptp_msg_handle.c:878-908, 946-951, 569-574`; `airptp.c:103-144` |
| Common header (34 bytes) | `messageType \| 0x10` ("transportSpecific = 1 which is expected by nqptp"), `versionPTP = 2`, `messageLength` BE, `domainNumber = 0`, `flags` BE, `correctionField = 0`, `sourcePortIdentity = clockId(8, BE) \|\| 0x80 0x05` ("same as iOS"), `sequenceId` BE per message type, `controlField = 0` (5 for Signaling), `logMessageInterval` | `ptp_msg_handle.c:232-255`, `ptp_definitions.h:56-71` |
| Clock identity | `0xFFFF << 48 \| 48-bit seed` (non-EUI-64 form, 7.5.2.2.3) | `airptp.c:156-165` |
| Timestamp (10 bytes) | `seconds_hi(16) \|\| seconds_low(32) \|\| nanoseconds(32)` BE from `CLOCK_MONOTONIC` | `ptp_definitions.h:48-54`, `ptp_msg_handle.c:99-110` |
| Announce | every 1 s (`logMessageInterval 0`), flags `0x0408` (UNICAST \| TIMESCALE), `originTimestamp = 0` ("iOS just sends 0"), `currentUtcOffset 0`, `grandmasterPriority1 128`, `grandmasterClockQuality = 0x0621436A` (class 6, accuracy 0x21, variance 0x436A), `grandmasterPriority2 128`, `grandmasterIdentity = clock id`, `stepsRemoved 0`, `timeSource 0x20` (GPS), then a PATH_TRACE TLV (type 0x0008, length 8, the clock id) | `ptp_msg_handle.c:297-323, 910-921`; `airptp_internal.h:27-31` ("ATV uses -2 ... my amp uses 0, as does nqptp") |
| Sync (two-step) | every 125 ms (`logMessageInterval -3`), flags `0x0608` (UNICAST \| TIMESCALE \| TWO_STEP), `originTimestamp = 0`; the send time `t1` is captured right before sending | `ptp_msg_handle.c:365-374, 935-954`; `airptp_internal.h:32-34` ("Both iOS, ATV, amp and nqptp use -3") |
| Follow_Up | same `sequenceId`, flags `0x0408`, `preciseOriginTimestamp = t1`, then two TLVs: ORGANIZATION_EXTENSION (0x0003) with org `00-80-C2` subtype `00-00-01` (802.1AS Follow_Up information: 28 bytes, all zero: cumulativeScaledRateOffset 0, gmTimeBaseIndicator 0, lastGmPhaseChange 0, scaledLastGmFreqChange 0) and ORGANIZATION_EXTENSION with Apple org `00-0D-93` subtype `00-00-04` carrying the 8-byte clock id; sent ~100 µs after the Sync | `ptp_msg_handle.c:376-422, 948-951`, `ptp_definitions.h:103-110` |
| Delay_Req -> Delay_Resp | on receipt of a Delay_Req: reply on port 320 with flags `0x0608`, the requester's `sequenceId`, `receiveTimestamp = now`, `requestingPortIdentity = requester's sourcePortIdentity` | `ptp_msg_handle.c:424-436, 548-575` |
| Pdelay_Req -> Pdelay_Resp + Pdelay_Resp_Follow_Up | guessed ("Haven't seen these messages from iOS") | `ptp_msg_handle.c:438-464, 641-674` |
| Signaling | every 1 s, `logMessageInterval -128`, `controlField 5`, `targetPortIdentity` zero (TODO: iOS sets it), two Apple ORGANIZATION_EXTENSION TLVs (subtypes `00-00-01` and `00-00-05`) with value `00 00 03 01` padded to 22 and 32 bytes | `ptp_msg_handle.c:325-363, 923-933` |
| Other clocks' Announce | ignored ("we always expect to become master clock") | `ptp_msg_handle.c:577-581` |
| Peer liveness | a peer is dropped after 15 s without any packet from it | `airptp_internal.h:17`, `ptp_msg_handle.c:885-891` |

Timing relationships the slave relies on: nqptp computes the offset from `t1` (Follow_Up) and its receive time
of the Sync, assuming small and symmetric network delay, and clamps decreases ("any decreases in the offset
between the local and remote clocks are due to delays in the network", `[sps] nqptp-shm-structures.h:127-135`);
whether nqptp sends Delay_Req at all is Section 9, Q8. The realtime stream can also run under PTP; then the
sync packet is the 28-byte type 0xD7 (`[ot] rtp_common.c:240-291`, `[sps] rtp.c:1679-1734`, `[ap2r]
control.py:44-52, 79-85`):

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | `0x90` first, then `0x80` |
| 1 | 1 | `0xD7` |
| 2 | 2 | `0x0006` |
| 4 | 4 | `now_frame` (frame at the loudspeaker at T) |
| 8 | 8 | T in nanoseconds on the PTP timeline (`CLOCK_MONOTONIC` of the grandmaster), BE uint64 |
| 16 | 4 | `now_frame + L` where `[ot]` uses `pos - 11025` so that `L = 88200 - 11025 = 77175` (`rtp_common.c:281-287`); `[sps]` expects `frame_2 - frame_1 = 77175` (`rtp.c:1696-1698`) |
| 20 | 8 | the grandmaster clock identity, BE |

`[sps]` anchors `now_frame - 11025` at T (`rtp.c:1728`) and does not need 0xD2/0xD3 timing in this mode.

### 4.5 Pause, seek and flush on the buffered stream

- Pause: `SETRATEANCHORTIME {rate: 0}`; stop writing. (`[sps]` stops the player and tells nqptp "P".)
- Resume: `SETRATEANCHORTIME {rate: 1, networkTime*: now + lead, rtpTime: next timestamp}`; continue writing.
- Seek/skip: `FLUSHBUFFERED {flushUntilSeq: <first new seq>, flushUntilTS: <first new ts>}`, then
  `SETRATEANCHORTIME rate 1` with the new pair; the deferred form (`flushFrom*`) lets a track change be spliced
  without a gap when the next track's packets are already queued.
- Stop: `TEARDOWN` with `streams`, then `TEARDOWN` with an empty plist (`[ot] airplay.c:2419-2421`).

## 5. ALAC

### 5.1 Encoder parameters

| Parameter | Value | Where it is fixed |
|---|---|---|
| sample rate | 44 100 Hz | `[sps] player.c:549-554`; receivers assume it for the realtime stream |
| bit depth | 16 | `ALACEncoder::InitializeEncoder` with `mFormatFlags = 1` (`[alac] ALACEncoder.cpp:1257-1261`) |
| channels | 2 (stereo, L then R, interleaved) | `[alac] ReadMe.txt` channel orders; `ALACEncoder.cpp:1036-1046` writes `ID_CPE` |
| frame size | 352 frames | `SetFrameSize(352)` before `InitializeEncoder` (`ALACEncoder.h`, `ALACEncoder.cpp:1276`); the default is 4096 (`ALACAudioTypes.h:157`) |
| input layout | native-endian signed 16-bit interleaved, `mBytesPerPacket = 4` | `ALACEncoder.cpp:1031` (`numFrames = ioNumBytes / mBytesPerPacket`), `mix16` reads `int16_t` (`matrix_enc.c:60`) |
| fast mode | off (`mFastMode = 0`, the default) | `ALACEncoder.cpp:104, 1043-1046` |
| tuning constants | `PB0 = 40`, `MB0 = 10`, `KB0 = 14`, `MAX_RUN_DEFAULT = 255`, `DENSHIFT_DEFAULT`, `kDefaultMixBits = 2`, `kMaxRes = 4`, `kDefaultNumUV = 8`, `kMinUV = 4`, `kMaxUV = 8` | `aglib.h`, `dplib.h`, `ALACEncoder.cpp:47-55` |

### 5.2 Magic cookie and the fmtp line

The 24-byte `ALACSpecificConfig` (all BE; `[alac] ALACMagicCookieDescription.txt`, `ALACAudioTypes.h:162-176`,
`ALACEncoder.cpp:1187-1200`) produced by the reference encoder for these parameters is

```
00 00 01 60  frameLength = 352
00           compatibleVersion = 0
10           bitDepth = 16
28           pb = 40
0a           mb = 10
0e           kb = 14
02           numChannels = 2
00 ff        maxRun = 255
00 00 00 00  maxFrameBytes = 0 (unknown)
00 00 00 00  avgBitRate = 0 (unknown)
00 00 ac 44  sampleRate = 44100
```

(hex `000001600010280a0e0200ff00000000000000000000ac44`, as emitted by the generator in Section 5.4).

The Classic-AirPlay SDP line `a=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100` (`[pyatv] support/rtsp.py:131-141`)
is the same structure in text: index 0 is the payload type, then frameLength, compatibleVersion, bitDepth, pb,
mb, kb, numChannels, maxRun, maxFrameBytes, avgBitRate, sampleRate. `[sps] apple_alac.cpp:45-67` builds the
cookie from `fmtp[2..11]` exactly in that order; `[sps] alac.c:91-131` reads the same bytes as
`max_samples_per_frame, 7a, sample_size, rice_historymult (40), rice_initialhistory (10), rice_kmodifier (14),
7f (channels), 80 (maxRun), 82, 86, 8a_rate`; `[ap2r] audio.py:354-372` wraps it in a 36-byte QuickTime `alac`
atom for FFmpeg (`hist_mult 40, init_hist 10, rice_lmt 14`). The names differ, the bytes do not.

AirPlay 2 never transmits the cookie; both sides assume it. The 352/16/2/44100 values are therefore protocol
constants, not negotiable parameters.

### 5.3 Port plan for Apple's ALACEncoder

Files and sizes in `[alac] codec/` (lines, `wc -l`):

| File | Lines | Needed for 16-bit stereo encode | Notes |
|---|---|---|---|
| `ALACEncoder.cpp` / `.h` | 1425 / 92 | `Encode` (1023-1162), `EncodeStereo` (266-546), `EncodeStereoEscape` (731-793), `InitializeEncoder` (1251-1319), `GetConfig`/`GetMagicCookie` (1187-1245), constructor/destructor; skip `EncodeStereoFast`, `EncodeMono`, the multichannel loop, `AddFiller` | about 600 lines to port |
| `ag_enc.c` / `aglib.h` | 370 / 81 | all of it: `dyn_comp` (250), `dyn_code`, `dyn_code_32bit`, `dyn_jam_noDeref`, `dyn_jam_noDeref_large`, `lead`, `lg3a`, `abs_func`, `read32bit`; `set_ag_params` lives in `aglib.h` | adaptive Golomb-Rice coder; the bit-twiddling must be ported exactly (unsigned 32-bit semantics) |
| `dp_enc.c` / `dplib.h` | 386 / 61 | `init_coefs`, `copy_coefs`, `pc_block` (the FIR predictor with sign-adaptive coefficient update) | integer arithmetic with explicit 32-bit wraparound; the "31 coefficients, nil coefs" call (`ALACEncoder.cpp:496, 511`) is the first-order fallback |
| `matrix_enc.c` / `matrixlib.h` | 342 / 80 | `mix16` only (60-96) | stereo decorrelation `u = (l*(2^mixbits - mixres) + r*mixres) >> mixbits`, `v = l - r` |
| `ALACBitUtilities.c` / `.h` | 260 / 104 | `BitBufferInit`, `BitBufferWrite`, `BitBufferByteAlign`, `BitBufferGetPosition`, `BitBufferReset`, the `BitBuffer` struct | `BitBufferWrite` writes up to 32 bits; the read functions are only for the decoder |
| `EndianPortable.c` / `.h` | 175 / 59 | not needed in C# (use `BinaryPrimitives`) | |
| `ALACAudioTypes.h` | 197 | `ALACSpecificConfig`, element IDs, constants | |

Total to port for the encoder: roughly 900 lines of C/C++ (the escape path and the brute-force `mixRes` /
coefficient search loops included). Plan the C# as `Airplay2.Sender/Alac/AlacEncoder.cs` (frame driver),
`AdaptiveGolomb.cs`, `Predictor.cs`, `Matrix.cs`, `BitWriter.cs`, with `Span<byte>`/`Span<int>` buffers
allocated once per encoder (the C++ allocates `mMixBufferU/V`, `mPredictorU/V`, `mShiftBufferUV`,
`mWorkBuffer` in `InitializeEncoder`, `ALACEncoder.cpp:1285-1297`). Keep the search loops (`mixRes` 0..4 over a
dilated 1/8 of the frame, then `numUV` 4..8 over the same, `ALACEncoder.cpp:315-435`): they are what makes the
output bit-identical to the vectors and cost about 10 µs per 352-frame block on a desktop CPU, far below the
8 ms budget per packet.

Decoder for tests, two options:

- Port `ALACDecoder.cpp` (730 lines; the stereo path is `Decode` for `ID_CPE`, `FillElement`,
  `DataStreamElement`), `ag_dec.c` (362), `dp_dec.c` (381), `matrix_dec.c` (390, `unmix16` only): about
  700 lines for 16-bit stereo. Same licence as the encoder.
- Or port `[sps] alac.c` (1010 lines, MIT, Hammerton's decoder, C only, `alac_create` / `alac_set_info` /
  `alac_decode_frame`, `alac.h:37-39`): self-contained but less readable.

Recommendation: port Apple's decoder (same structure as the encoder, shared `BitBuffer` reader) and keep it in
the test project only.

### 5.4 Tests and the generated vectors

Vectors were generated today from the reference C++ code (compiled unchanged in a Linux container,
`alpine:3.20` + `g++`, because no C compiler is installed on the Windows host or in WSL):

- `docs/airplay2/test-vectors/alac-44100-s16-2ch-352.json`: the magic cookie and six 352-frame packets
  (`silence`, `dc_plus_1000_minus_1000`, `sine_440_1k`, `ramp_full_scale`, `white_noise_lcg_0x12345678`,
  `sine_440_1k_second_packet`), each with the input PCM (little-endian hex), the encoder output (hex), its
  length and `roundtrip_ok` (all true, verified with `ALACDecoder` in the same program). Sizes: 32, 75, 456,
  433, 1412 (escape), 487 bytes.
- `docs/airplay2/test-vectors/gen_alac_vectors.cpp`: the generator, so the set can be regenerated or extended
  (`docker run --rm -v <alac/codec>:/src:ro -v <dir>:/work alpine:3.20 sh -c 'apk add g++ && ...'`, see the
  file header for the exact build line).

The packets must be encoded **in the listed order from a freshly initialised encoder** to reproduce the hex,
because `mLastMixRes` and the predictor coefficients carry over (`ALACEncoder.cpp:292-298, 375`); the
`sine_440_1k_second_packet` case exists to pin that state carry-over. Tests to write:

1. Bit-exact: encode each vector in order, compare bytes with `alac_hex`.
2. Round trip: encode arbitrary PCM (sine sweeps, impulses, full-scale square, random) with the ported encoder,
   decode with the ported decoder, compare PCM exactly; include a partial last frame (fewer than 352 frames).
3. Cookie: `GetMagicCookie` equals `magic_cookie_hex`.
4. Escape path: white noise must produce the escape frame of 1412 bytes (`escapeBits = numSamples*16*2 + 16`,
   `ALACEncoder.cpp:442`, plus the 7-bit element header, 12 unused bits, flags, `ID_END` and padding).
5. Property: output length never exceeds 1412 for any 16-bit stereo input of 352 frames.

Python has no pure-Python ALAC encoder; the Docker path above is the way to get more vectors.

## 6. Volume semantics on the wire

### 6.1 Sender -> receiver

`SET_PARAMETER` with `Content-Type: text/parameters` and body `volume: <value>\r\n`, where `<value>` is a
decimal number in dB-like units: `0.0` is full scale, `-30.0` is the quietest audible setting, `-144.0` is mute.

- `[ot] airplay.c:1804-1822, 2454-2477`: maps 0..100 % to `-30 + 30 * pct/100` (scaled by an optional
  per-device `max_volume`), `-144.0` for "off", formatted `"-%d.%06d"` with a trailing `\r\n`.
- `[pyatv] protocols/airplay/utils.py:29-32, 281-291`: `DBFS_MIN = -30.0`, `DBFS_MAX = 0.0`, linear map from
  0..100, and `-144.0` when the level is 0. The body is `f"{parameter}: {value}"` without a trailing CRLF
  (`support/rtsp.py:300-306`); receivers accept both.
- Receiver side: `[sps] rtsp.c:3230-3258` parses `atof` after `"volume: "` and stores it as
  `own_airplay_volume`; `[sps] common.c:1546-1605` (`vol2attn`) accepts `0..-30` and `-144`, anything else is
  treated as minimum; `[ap2r] ap2-receiver.py:680-685` parses the same. The receiver maps the value onto its own
  attenuator with a curve of its choosing (`[sps]` offers three profiles, `player.c:5137-5149`), so the wire
  value is a position on a 30-dB "slider", not a guaranteed acoustic gain.

Per `docs/PLAN.md`, the per-device level `r_i` (0..100) therefore maps to the wire as `-30 + 30 * r_i / 100`,
and `r_i = 0` is sent as `-144.0` (mute). The group master level `G` never reaches the wire; only the resulting
`r_i` do.

Ordering: `[ot]` sends the volume as the **last** step of its start sequence, after the stream `SETUP`,
because "some devices (e.g. Sonos Symfonisk) don't register the volume if it isn't last" (`airplay.c:3655-3656`);
`[pyatv]` sends it after `FLUSH`, right before streaming, or defers it to after streaming starts if the receiver
rejects it earlier (`protocols/raop/__init__.py:380-399`, `stream_client.py:450-451`).

### 6.2 Receiver -> sender: what the sources show

- `GET /info` carries `initialVolume` (float): `[sps] rtsp.c:1137` (`suggested_volume(conn)` = the last volume
  this client set, else the configured default), `[pyatv] server_auth.py:469` (`-27.0` in its fake receiver),
  `[ap2r] ap2-receiver.py:204`. `[pyatv]` uses it as the starting level when the user has not set one
  (`protocols/raop/__init__.py:384-391`). The SpeakerA does not include it (Appendix A).
- `GET_PARAMETER` with body `volume\r\n` returns a `text/parameters` body containing a `volume: <value>` line:
  `[sps]` prefixes it with an empty line, `"\r\nvolume: %.6f\r\n"` (`rtsp.c:3327-3332`); `[ap2r]` sends
  `volume: <value>\r\n` (`ap2-receiver.py:636-666`). Skip leading CRLF and match the `volume:` line anywhere
  (SPEC-control.md 4.11). A polling fallback for reading the receiver's current level.
- Event channel (the reverse RTSP connection to the receiver's `eventPort`, encrypted with
  HKDF-SHA512(salt `"Events-Salt"`, info `"Events-Write-Encryption-Key"` / `"Events-Read-Encryption-Key"`, 32
  bytes each, from the pair-verify shared secret; `[pyatv] airplayv2.py:21-23`, `auth/hap_srp.py:32-41`,
  `protocols/airplay/ap2_session.py:139-148` for the read/write reversal; framed as HAP 2-byte LE length +
  ciphertext + 16-byte tag, `auth/hap_session.py:17-18, 31-66`; frames from the receiver may exceed 1024 bytes,
  SPEC-control.md 2.5): the receiver sends RTSP-style requests and expects
  `RTSP/1.0 200 OK` back. Observed request types:
  - `POST /command` with plist `{type: "updateInfo", value: <the /info plist incl. initialVolume and
    txtAirPlay>}` sent by `[sps]` once when the event channel connects (`ap2_event_message_handler.c:109-139`,
    `ap2_event_receiver.c:219`).
  - `{type: "sendMediaRemoteCommand", value: "paus" | "play" | "nitm" | "pitm"}` handled by `[ot]
    airplay_events.c:326-360` (play/pause toggle, next, previous).
  - `[pyatv]` replies `200 OK` with `Audio-Latency: 0` to everything and ignores the content
    (`protocols/airplay/channels.py:60-97`).

No reference source shows a receiver reporting a *volume change made on the receiver itself* over the event
channel, and `[sps]` does not send one when its volume changes via D-Bus. The plan's requirement ("Changes
made on a receiver itself ... are read back via the event channel") therefore rests on behaviour that must be
observed on real devices (Section 9, Q5). Design for both outcomes: parse every event-channel plist, treat a
`type` of `updateInfo` as a possible `initialVolume` update, log unknown types verbatim, and as a fallback poll
`GET_PARAMETER volume` every few seconds while a session is active.

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** `GET_PARAMETER volume` answers
`volume: <dB, six decimals>\r\n` without a leading blank line; the event channel carried no message at all during the
sessions (no `updateInfo`), so the poll fallback of D10 is the only receiver-side channel seen so far (P4 with button
presses is still open).

### 6.3 Recommendation for the group volume model

Store `r_i` as 0..100; send `-30 + 0.3 * r_i` with six decimals and `\r\n`; send `-144.0` for 0; send volume
after the stream `SETUP` and again whenever the slider moves (coalesce to at most ~10 updates per second per
receiver; each is an RTSP round trip). When a receiver reports a level `v` (via any of the three channels),
set `r_i = clamp(round((v + 30) / 0.3), 0, 100)`, treat `v <= -30` as 0, and leave the other receivers and `G`
alone, as the plan says.

## 7. Metadata, progress and artwork relative to the RTP timeline

All three are `SET_PARAMETER` requests on the control connection, each carrying `RTP-Info: rtptime=<frame>`
(plus `seq=` in `[pyatv]`). The receiver uses `rtptime` to decide **when** the item applies: `[sps]` forwards
each item with the rtptime as a bracket (`mdst`/`mden` around text, `pcst`/`pcen` around a picture,
`rtsp.c:3353-3412`; "the rtptime of the message is used as a kind of an ID that can be used to link items of
metadata, including pictures, that refer to the same entity", `rtsp.c:3353-3360`) and consumers display an item
when playback reaches that frame; `[ap2r]` logs them (`ap2-receiver.py:686-711`). Receivers advertise what they
want with the TXT `md=0,1,2` key (0 text, 1 artwork, 2 progress; `[pyatv] protocols/raop/parsers.py:75-96`,
`[ot] airplay.c:4058-4063` from feature bits 15/16/17, which the SpeakerA sets).

| Item | Content-Type | Body | `rtptime` semantics | Sources |
|---|---|---|---|---|
| text | `application/x-dmap-tagged` | DMAP `mlit` container with `minm` (title), `asal` (album), `asar` (artist) | the frame at which the new track starts | `[pyatv] support/rtsp.py:308-332`, `[ot] airplay.c:2549-2573, 1679-1720`, `[sps] rtsp.c:3282-3314` parses the tags after an 8-byte container header |
| artwork | `image/jpeg` or `image/png` | the image bytes | same frame as the text item it belongs to | `[pyatv] rtsp.py:334-350`, `[ot] airplay.c:2505-2547` |
| progress | `text/parameters` | `progress: <start>/<current>/<end>\r\n`, all three in frames | `start` = frame of the track start, `current` = frame playing now (never before `start`), `end` = `start + duration_frames` | `[pyatv] stream_client.py:400-404` (`start = now = rtptime of the first packet`, `end = start + duration * 44100`), `[ot] airplay.c:937-979, 2479-2503`, `[sps] rtsp.c:3263-3271` (`rtpstampstart/rtpstampnow/rtpstampend 44100 per second`) |

`[ot]`'s frame bookkeeping (`airplay.c:937-979`, GPL) is described here so that its **behaviour** can be
reimplemented from this description; the pseudo-code below is the specification, not a transcription target (do not
copy owntone's code or identifiers, WORK-PLAN.md section 1):

```
start   = cur_pos - elapsed_frames           # cur_pos: frame at the speaker now; elapsed: position in track
display = start - 15360 (startup) or start - 30720 (track switch)   # 0.35 s / 0.70 s early, "so the display is ready"
pos     = max(cur_pos, start)
end     = start + duration_frames (or pos if the duration is unknown)
RTP-Info rtptime = start; progress body = display/pos/end
```

Because the receiver plays `L` frames behind the sender, send the metadata for a new track as soon as the
first packet of that track is queued (its `rtptime` is known then), not when it starts playing; the receiver
holds it until the timeline gets there. Spotify track changes arrive from librespot's `PlayerEvent.Changed` /
`TrackChanged` before the first samples of the new track reach the sink; the bridge must map the event to the
sink's frame counter at that moment (Section 8.4).

Keep-alive: `[ot]` resends the progress item every 25 s because Apple TVs and HomePods otherwise disconnect
(`airplay.c:109-113, 1891-1915`), and `[pyatv]` sends `POST /feedback` every 2 s (`airplayv2.py:25, 167-181`,
`ap2_session.py:28-29` "This is what iOS uses"); `[sps]` answers feedback with `{streams: [{type, sr}]}` while
playing (`rtsp.c:2058-2079`). Do both: feedback every 2 s, progress every 25 s.

## 8. Sender audio pipeline: design constraints

### 8.1 What librespot delivers

- The sink interface is `ISink` (`[lrs] Librespot.Playback/AudioBackend/AudioBackend.cs:62-76`): `Start()`,
  `Stop()`, `Write(AudioPacket packet, Converter converter)`. Samples arrive as `AudioPacket.Samples` of
  interleaved `double`s (`Decoder/Decoder.cs:40-47`); `Converter.F64ToS16` gives the 16-bit PCM the ALAC encoder
  needs (`AudioBackend.cs:117-118` shows the conversion the byte-sink helper uses). Sample rate is 44 100 Hz
  stereo for all Spotify formats.
- Pacing: the player thread writes packets as fast as the sink accepts them (`Player.cs:1693-1724`,
  `HandlePacket` -> `_sink.Write`, `Player.cs:2005-2026`). The only clock in the system is the sink's
  backpressure: the NAudio sink blocks `Write` while more than 500 ms is queued (`AudioBackend/NAudio.cs:34-41`,
  upstream "waits while more than 26 chunks are queued"). The AirPlay sink must block the same way; if it does
  not, librespot decodes the whole track into memory at CPU speed.
- `Start()` is called when playback begins or resumes (`Player.cs:1858-1876`), `Stop()` on pause and at track
  end/stop (`Player.cs:1878-1909, 1988-1991`). A seek does **not** touch the sink: `HandleCommandSeek` only
  seeks the decoder (`Player.cs:2465-2500`), so the sink sees a sample-level discontinuity with no signal. The
  bridge must subscribe to `PlayerEvent.Seeked`, `Changed`/`Started`, `Paused`, `Playing`, `Stopped` and the
  volume events (`Player.cs:70-150`) and correlate them with the sink's frame counter.

### 8.2 Buffering and run-ahead

The receiver plays `L` frames (1.75 s) after a packet is sent. The sink therefore needs:

```
librespot thread --Write(f64)--> [convert to S16] --> ring buffer (PCM, >= 1 s) --> pacer thread
                                                                                        |
pacer: every 352 frames of master-clock time: take 352 frames (or silence), ALAC-encode once,
       encrypt per receiver, send to each receiver's dataPort, store packet in the retransmit ring;
       every 44100 frames: build sync packets per receiver (now_frame = next_ts - L - offset_i) and send;
timing responder thread: answers 0xD2 on the timing socket; control thread: answers 0xD5 from the ring.
```

- `Write` blocks when the ring holds more than `HighWater` (say 1.0 s) and returns immediately otherwise; the
  ring must hold at least one packet beyond the pacer's next deadline at all times to avoid underruns, so
  `Start()` should pre-fill (do not start the pacer until, for example, 200 ms are buffered or 100 ms have
  elapsed, whichever comes first).
- The pacer's deadline for packet k is `T0 + 352*k/44100` in master-clock time; sleep until then (`Stopwatch`
  plus a short spin for the last millisecond; Windows timer resolution must be raised with
  `timeBeginPeriod(1)` or `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`; on Linux `nanosleep` is fine). If the pacer
  wakes late by more than one packet, send the missed packets immediately (`[pyatv]` does the same, up to 3,
  `stream_client.py:41, 497-512`); the timestamps are what matter, not the send instants, as long as packets
  arrive before their play time.
- Latency budget from Spotify to the loudspeaker: librespot decode-ahead (Spotify's own buffering) + ring
  (<= 1 s) + `L` (1.75 s) + receiver fixed delay (0.25 s on `[sps]`). The user-visible effect is that a pause
  or seek takes `L + ring` to reach the speaker unless the bridge flushes (Section 8.4).

### 8.3 Underrun

If the ring is empty at a deadline (Spotify stalled, network hiccup on the Spotify side), send a packet of
silence with the next timestamp and keep going; `[pyatv]` pads with zeros to keep the sync packets accurate
(`stream_client.py:563-579`). This keeps every receiver's timeline continuous and the group in sync. Only if
silence has been sent for longer than a threshold (for example 5 s) treat it as a pause: `FLUSH`, stop the
pacer, and re-anchor on resume. Never let the timeline stall without a flush: a receiver that stops getting
packets drains its buffer and `[sps]` resets its anchor after 7 s without control packets (`rtp.c:1757-1766`).

### 8.4 Pause, seek, skip and track change

| Spotify event | Sink/bridge action on the realtime stream |
|---|---|
| `Paused` (then `Stop()`) | Stop the pacer at the next packet boundary. For every receiver: `FLUSH` with `RTP-Info: seq=<next_seq>;rtptime=<next_ts>` so the ~1.75 s already sent is discarded (`[ot] airplay.c:2401-2417`; `[sps]` discards packets with timestamps before `rtptime`, `player.c:5252-5277`); mark the session CONNECTED. Keep the RTSP session, event channel and timing responder alive; keep sending `POST /feedback`. Drop the ring. |
| `Playing` after pause (then `Start()`) | New anchor: `T0 = now`, keep `next_ts` (monotonic), send a `0x90` sync with `now_frame = next_ts - L` and the next audio packet with the marker bit; sessions go back to STREAMING. |
| `Seeked` | The player keeps feeding samples; the bridge gets the event asynchronously. Record the ring position at the moment the event arrives (samples written after it belong to the new position; librespot's seek is synchronous on the player thread before the next `Write`), drop everything in the ring from that mark backwards, `FLUSH` all receivers with the next values and re-anchor as above. Audio before the mark that is already in flight is discarded by the flush. The result is a gap of at most the pacer's send-ahead plus one packet, which is what Apple devices show too. |
| Track change (`Changed`/`Started`) | No flush needed when the previous track ended normally (continuous timeline); send text, artwork and progress with `rtptime = next_ts` at the moment the first samples of the new track enter the ring (Section 7). A manual skip arrives as `Changed` plus (usually) `Stop()`/`Start()` around the load; treat it like a seek: flush and re-anchor. |
| `Stopped` / no receiver connected | `TEARDOWN` only when the receiver is disconnected by the user; while "connected but idle" keep the session and discard audio (the plan: "audio is silently discarded while no receiver is connected"). |
| Volume | `SET_PARAMETER volume` per receiver with the group model's `r_i` (Section 6). |

The buffered-stream equivalents are in Section 4.5.

### 8.5 Threads and clocks

- One master clock per process (`MasterClock`): monotonic, returns nanoseconds; the NTP timing responder,
  the sync builder, the pacer and (later) the PTP grandmaster all read it. Expose `NtpNow()` and `PtpNow()` as
  pure functions of it. The stream layer publishes it as `IStreamTransport.Clock` and the group player's
  `Timeline` is built from that property, never from a clock of its own, so the pacer and the timing responder
  or PTP grandmaster can never run on different clocks (SPEC.md D3).
- Threads: pacer (highest priority, real-time if available), timing/control UDP receivers (one socket each,
  shared by all receivers), per-receiver RTSP/event tasks (async, not time-critical), librespot's player thread
  (calls `Write`).
- Containers: the pacer needs UDP sockets bound on the macvlan interface; mDNS and the timing responder bind
  per interface as planned. Timer resolution inside Linux containers is fine (`CLOCK_MONOTONIC`,
  `timerfd`/`nanosleep`).

## 9. Open questions to settle against real receivers

Each question names the disagreement or gap, the receiver to test and a concrete probe. "SpeakerA" is the MOTIV
HOME (192.0.2.26); "HomePod" is the Apple receiver "Office" (192.0.2.98), which may only be used once the
harness owner allows it; the LG TV is never used.

| # | Question | Why it is open | Probe |
|---|---|---|---|
| Q1 | Which `L` (77 175 vs 88 200 vs 66 150) gives correct, stable playback on Apple-SDK receivers, and what is the real output delay? | `[sps]` expects 77 175 and adds 11 025; `[ot]` sends 88 200 to the same class of device; `[pyatv]` sends 66 150. No receiver source enforces `latencyMin`/`latencyMax`. | Play a click track from the CLI (`play click.wav --to SpeakerA --latency 77175`) while recording the room with a microphone synchronised to the sender clock (a second click played locally); measure sender-send-to-loudspeaker delay and jitter for 77 175 and 88 200. Watch for dropouts at 66 150. |
| Q2 | Does the SpeakerA publish `audioLatencies` anywhere (session `SETUP` response, `/info` after pairing, `RECORD` reply `Audio-Latency`)? | `/info` without pairing had none (Appendix A); `[ap2r]` shows the key exists. | Dump the full `SETUP` responses and `RECORD` headers once pairing works; grep for `Latency`. |
| Q3 | NTP or PTP for the first implementation against the SpeakerA? | The SpeakerA advertises bit 41 (PTP) but not bit 45 (NTP); `[ot]` (current) picks PTP for it; older `[ot]` drove the same feature profile with NTP; `[sps]` refuses NTP sessions outright. | Send the session `SETUP` with `timingProtocol: "NTP"` and `timingPort`; check for a 200 with `eventPort`, then whether 0xD2 timing requests arrive at our port within ~2 s of the stream `SETUP` (`ITimingResponder.RepliesSent > 0`). This needs **no audio** and is run in wave 3 (SPEC.md P1, control part); it decides the default timing protocol before the audio pipeline is written. If none arrive, the realtime stream runs under PTP: the `ptp-master` package (Section 4.4) and the 0xD7 sync (`RealtimeStreamTransport` with an `IPtpMaster`). |
| Q4 | Is the `shk` value free, or must it be the pair-verify shared secret? | `[ot]` uses the shared secret, `[pyatv]` uses an unrelated derived key, `[sps]` accepts anything of 32 bytes. | Run the same session twice with `shk = sharedSecret[0:32]` and `shk = random 32 bytes`; audio must play in both cases. |
| Q5 | Does a receiver report its own volume changes, and how? | No reference source shows a receiver-initiated volume message; the plan assumes one over the event channel. | With a session up and the event channel decrypting, press the SpeakerA's volume buttons (and change the HomePod's volume from the Home app); log every event-channel request and every `POST /feedback` response body; also poll `GET_PARAMETER volume` once per second and diff. |
| Q6 | Retransmit reply form: wrapped 0xD6 on the control port, or raw packet on the data port? | `[pyatv]`/iOS use the wrapped form; `[ot]` uses the data port because an amplifier kept re-requesting with the wrapped form. | Drop every 50th packet deliberately (test flag), count incoming 0xD5 requests and whether the same sequence number is requested again after our reply, for both forms. |
| Q7 | Buffered stream with ALAC 44.1 kHz/16-bit: which SSRC code, if any, do Apple-SDK receivers accept? | `[sps]` invented `0x0000FACE`; Apple senders use AAC-LC 44.1 (`0x16000000`) or ALAC 48/24 (`0x15000000`). | Only when the buffered stream is implemented: send three short streams with SSRC `0x0000FACE`, `0x15000000` (with 48 kHz/24-bit ALAC) and `0x16000000` (AAC-LC, needs an encoder) and see which one plays on the SpeakerA and the HomePod. |
| Q8 | Does nqptp (and the SpeakerA's PTP slave) send Delay_Req, and is a Delay_Resp required? | libairptp answers Delay_Req but the nqptp consumer code only reads offsets from shared memory; nqptp's own source is not in the reference tree. | Capture UDP 319/320 during a PTP session (Wireshark on the sender); note message types from the receiver, their rate, and whether sync holds if Delay_Resp is not sent. |
| Q9 | Sync packet flags field (`0x0007` vs `0x0004`) on an Apple-SDK receiver: does it change the latency by 11 025 frames? | `[sps]` adds 11 025 for 7; `[ot]` says the field is unused in AirPlay 2. | Repeat Q1's measurement with 4 and 7. |
| Q10 | Minimum sync cadence and tolerance to missed syncs. | `[sps]` resets after 7 s without control packets; Apple-SDK behaviour unknown. | Stop sending syncs for 3, 6, 10 s mid-track and observe whether playback continues, drifts or stops. |
| Q11 | Does `FLUSH` mid-stream (pause/seek) require a new `RECORD`, and does the SpeakerA accept a `0x90` sync + marker packet to resume? | `[ot]`'s sequence (flush -> CONNECTED -> init sync + marker) is the only sender evidence; `[sps]` resumes on any packet. | Pause for 5 s and resume; seek within a track; measure the gap and check for stale audio after the flush. |
| Q12 | Group with mixed receivers (SpeakerA + HomePod): does the same `L` work for both, and what per-receiver offset is needed? | Unknown output delays. | Q1's microphone setup with both receivers; measure the inter-receiver offset and store it as `offset_frames`. |
| Q13 | Does the SpeakerA require `POST /feedback` or periodic progress to keep the session alive? | `[ot]` needs progress every 25 s for Apple TV/HomePod; `[pyatv]` sends feedback every 2 s. | Run a 10-minute session with (a) neither, (b) feedback only, (c) progress only and watch for disconnects. |

## Appendix A. Live probe results (2026-10-09, sender at 203.0.113.150)

mDNS browse (`_airplay._tcp` and `_raop._tcp`, unicast-response queries; replies came via the router
203.0.113.1, which reflects mDNS between VLANs; scripts in the scratchpad, not committed):

```
Test Speaker ABCD._airplay._tcp.local  SRV 7000 speakera-021a2b3c4d5d.local
  A 192.0.2.26   AAAA 2001:db8:1:30:1a2b:3cff:fe4d:5e01
  TXT acl=0 deviceid=02:1A:2B:3C:4D:5E features=0x445F8A00,0x1C340 rsf=0x0 fv=p20.1.1.36.0x8b4c480
      flags=0x404 model=Test Speaker ABCD manufacturer=Lautsprecher SpeakerA GmbH
      serialNumber=00000000-0000-0000-0000-000000000000 protovers=1.1 srcvers=366.0
      pi=02:1A:2B:3C:4D:5E gid=02:1A:2B:3C:4D:5E gcgl=0
      pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a
021A2B3C4D5E@Test Speaker ABCD._raop._tcp.local  SRV 7000 (same host)
  TXT cn=0,1 da=true et=0,4 ft=0x445F8A00,0x1C340 fv=p20.1.1.36.0x8b4c480 md=0,1,2
      am=Test Speaker ABCD sf=0x404 tp=UDP vn=65537 vs=366.0 pk=(same)

Office._airplay._tcp.local (Apple receiver, not probed)  SRV 7000 Office.local  A 192.0.2.98
  TXT model=AppleReceiver00,1 features=0x4A7FCA00,0x3C354BD0 flags=0x80404 srcvers=980.77.2 osvers=27.0
      igl=1 gcgl=1 gid=00000001-...+00000002-... (stereo pair / group leader)
02AABBCC0001@Office._raop._tcp.local  TXT cn=0,1,2,3 et=0,3,5 md=0,1,2 am=AppleReceiver00,1 vs=980.77.2

Living room TV._airplay._tcp.local (LG, never targeted)  A 198.51.100.200
  TXT features=0x7F8AD0,0x38BCF46 srcvers=377.40.00 model=TVMODEL0000.DEUQDJP
```

SpeakerA feature word `0x1C340445F8A00` decoded (bit names from `[ap2r] ap2/bitflags.py:59-147` and
`[ot] airplay.c:393-437`): 9 AirPlayAudio, 11 AudioRedundant, 15 AudioMetaCovers, 16 AudioMetaProgress,
17 AudioMetaTxtDAAP, 18 ReceiveAudioPCM, 19 ReceiveAudioALAC, 20 ReceiveAudioAAC_LC, 22 AudioUnencrypted,
26 Audio_AES_Mfi (`Authentication_8`), 30 UnifiedAdvertisingInfo, 38 ControlChannelEncrypt,
40 BufferedAudio, 41 PTPClock, 46 HomeKitPairing, 47 PeerManagement, 48 TransientPairing. Not set: 45 NTPClock,
52 PeersExtendedMessage, 61 RFC2198Redundant, 43 SystemPairing, 51 MfiPairSetup. `flags=0x404` = bit 2
(audio link / "all is well") + bit 10 (`SETUP_HK_ACCESS_CTRL`, `[ot] airplay.c:188`). RAOP `et=0,4` = unencrypted
or MFi-SAP, `cn=0,1` = PCM or ALAC, `md=0,1,2` = text, artwork, progress, `tp=UDP`.

`GET /info` (RTSP/1.0, no pairing), both without a body and with the stage-1 `{qualifier: ["txtAirPlay"]}`
body, returned `200 OK`, `Server: AirTunes/366.0`, binary plist:

```
OSInfo 'AirPlay;2.0.10'   PTPInfo 'AirPlay;2.0.10'   sdk 'AirPlay;2.0.10'   build '53.0'
deviceID '02:1A:2B:3C:4D:5E'   features 0x1C340445F8A00   firmwareRevision '1.1.36.0x8b4c480'
hardwareRevision 'L2'   keepAliveLowPower true   keepAliveSendStatsAsBody true
manufacturer 'Lautsprecher SpeakerA GmbH'   model 'Test Speaker ABCD'   name 'Test Speaker ABCD'
nameIsFactoryDefault false   pi '02:1A:2B:3C:4D:5E'   protocolVersion '1.1'   sourceVersion '366.0'
statusFlags 1028 (0x404)
txtAirPlay (stage-1 reply only): the _airplay TXT strings listed above
```

Absent: `supportedFormats`, `audioLatencies`, `initialVolume`, `pk`, `senderAddress`, `volumeControlType`,
`vv`. The connect/response round trip over Wi-Fi was 0.2-1.2 s; a 5 s connect timeout once failed and a 25 s
one succeeded, so the sender must not use short timeouts against this receiver.

`PTPInfo 'AirPlay;2.0.10'` (the Apple SDK's own PTP implementation) together with bit 41 and the absence of
bit 45 is the strongest hint that this device expects to be a PTP slave; see Q3.
