# Receivers and troubleshooting

## Known receiver quirks

Receivers are told apart by what their AirPlay stack advertises in the mDNS TXT record (`srcvers`, `features`), not by
product name. The rules live in `src/Airplay2.Sender/Session/KnownReceiverQuirks.cs`: keyed on TXT `srcvers` (`model`
only for shairport-sync), first match wins; anything else takes the defaults.

| Receiver (TXT) | Status |
|---|---|
| shairport-sync 5.5.2 (`model=ShairportSync`, also `srcvers=366.0`) | Test receiver for transient and password/PIN pairing, PTP sessions, the realtime stream under PTP and the buffered stream. It refuses NTP sessions, so it always runs under PTP. |
| `srcvers=366.0`, `features=0x445F8A00,0x1C340` (receiver A, a third-party speaker) | The live target. Transient pairing, the realtime stream under NTP, retransmits, volume, artwork and progress work. Quirks below. |
| Apple devices (HomePod, Apple TV), TVs, others | Not tested yet. A receiver that refuses NTP needs PTP; add a rule to `KnownReceiverQuirks.cs`. |

Quirks of receiver A (`srcvers=366.0`):

- **No track titles.** It answers the DMAP text metadata with `500`, sometimes only after about 10 s. The bridge sends
  the title once per session and then stops; artwork and progress are accepted.
- **Slow flushes.** `FLUSH` (pause, seek, skip) and `TEARDOWN` answers take 2-30 s on Wi-Fi, sometimes longer, so a
  seek or skip can take that long before the new audio is heard. A `TEARDOWN` that is not answered within 10 s is
  given up and the connection closed.
- **No buffered stream.** It accepts the buffered stream's setup but rejects playback (`SETRATEANCHORTIME` `400`), so
  keep the stream type at Realtime.
- **No volume reports.** It does not report level changes made with its own buttons; the volume poll picks them up.

How each of these was found: [`docs/airplay2/SPEC.md`](airplay2/SPEC.md) section 4.

## Troubleshooting

| Symptom | What to check |
|---|---|
| The device does not appear in Spotify | The phone and the bridge are on the same network; UDP 5353 inbound is allowed; the Zeroconf port is reachable (fix it with `Bridge:ZeroconfPort` and open it). Diagnostics shows whether Spotify is connected. |
| A receiver is not listed | The speaker is on and in the same network; mDNS is not blocked between them. `airplay2 discover` on the same machine shows what mDNS sees. |
| *Connect* fails after about 30 s with `400` | Inbound UDP to the bridge is blocked (NTP timing requests). Add the firewall rule (see [Firewall and network](NETWORK.md)). |
| *Connect* takes long or times out | Slow Wi-Fi receivers can need tens of seconds; `Bridge:ConnectTimeout` (1 minute) is the limit. |
| Seeking or skipping is slow | The receiver answers `FLUSH` slowly (receiver A takes 2-30 s); nothing to fix on the bridge. |
| No title on the receiver | It rejects DMAP text (receiver A does); artwork and progress still go out. |
| Nothing plays with the Buffered stream type | Switch Settings back to Realtime; not every receiver plays the buffered stream. |
| The rooms of a group are out of step | Set a per-receiver offset under Receivers > Details. |
| TuneShuttle is not in the AirPlay menu | The Apple device is on the same network; Settings > AirPlay receiver is on; UDP 5353 and TCP 7000 inbound are allowed. State *Error* in Diagnostics: the log says why, usually TCP 7000 taken by another AirPlay receiver on the same machine (only one per host). |
| The Apple device connects, but nothing plays | Diagnostics > AirPlay receiver > *Decoder*: without the FFmpeg 7.1 libraries the receiver is ALAC-only, and the log names the library that did not load. Install them (see [Installing and running](INSTALL.md#the-ffmpeg-libraries)) and restart. |
| Diagnostics shows "PTP port not bound (anchor-only timing)" | UDP 319/320 are taken (another PTP daemon such as nqptp, or another AirPlay receiver) or may not be bound (systemd without `CAP_NET_BIND_SERVICE`). AirPlay still plays with anchor-only timing; free the ports for exact timing. |
| AirPlay stops when someone presses play in Spotify | The "newest source wins" rule (see [Using TuneShuttle](USER-GUIDE.md#airplay-receiver)). |
| "Bridge:FakeReceivers is set, but this build has no fake receiver" in the log | Fake receivers exist only in Debug builds from the repository. |

The logs: the Diagnostics page (live), `<DataDirectory>/logs` on Windows, `journalctl -u tune-shuttle` under systemd,
`docker logs` in a container. `Logging:LogLevel:Airplay2=Debug` adds the decrypted protocol exchange.
