# Manual tests

The checks that need a person: someone who logs in to Spotify, listens, presses buttons on a speaker, measures with a
microphone, enters a PIN, holds a phone, or decides about another person's device. Everything else is automated (see
[`docs/DEVELOPMENT.md`](../DEVELOPMENT.md)).

Each item says what to run, what to look for, and its **last known result**. When you run an item, update its result
here with the date, and record protocol findings in [`SPEC.md`](SPEC.md) section 4 as a dated "Result (date): ..."
line, as the probes so far have been (section 4.3 for the AirPlay receiver probes of section 9). Live logs go to `artifacts/live/` (git-ignored).

The live-device rules apply ([`docs/DEVELOPMENT.md`, "Live-device rules"](../DEVELOPMENT.md#live-device-rules)).
AirPlay wakes receivers, and they play at their current volume. Contacting receiver B (a TV receiver that requires PIN pairing,
srcvers 377.40.00) is the owner's decision, because it turns on. Receiver C (an Apple receiver) may be used only with
its owner's permission. Receiver A is a third-party AirPlay 2 speaker (srcvers 366.0, features 0x445F8A00,0x1C340). Start every listening test at a
low level (15-20) and raise it from the web app.

Section and item numbers are stable: `SPEC.md` section 4.1 refers to them (for example "2.6").

## 0. Preparation

- [ ] `./build.ps1 -SkipContainer` passes (Release build, offline tests, publish to `artifacts/bridge` and
      `artifacts/airplay2`).
- [ ] Firewall: install the service (step 1) or add an inbound allow rule for `artifacts\airplay2\airplay2.exe`
      (README, "Firewall and network"). Without the rule, receiver A answers the stream SETUP with `400` after about
      30 s, because its NTP timing requests are blocked.
- [ ] Note receiver A's current volume, so you can put it back afterwards.

## 1. Spotify through the bridge

Last known result (2026-10-09): the control part passed against receiver A without audio (`BridgeLiveTests`: mDNS,
level 15, connect as the single output, level change read back, disconnect with `TEARDOWN {}` answered 200; log
`artifacts/live/2026-10-09-bridge-connect.log`). Steps 3-10 (a real Spotify handoff and audio through the bridge) have
not been run yet. The librespot port on its own was verified end to end with the Spotify app on 2026-10-09
(`librespot-dotnet/README.md`).

1. [ ] Start the bridge: `dotnet run --project src/Bridge.Service -c Release`, or install it as a service with
       `install/windows/install-service.ps1` (elevated) and open `http://localhost:8080`.
2. [ ] In the web app, under **Receivers**, set receiver A's level to 15, then press *Connect*. Expect state
       *connected* within about 10 s. On a bad Wi-Fi day receiver A answers RTSP slowly; up to 60 s
       (`Bridge:ConnectTimeout`) is still fine.
3. [ ] In the Spotify app (phone, same LAN), open the device picker and choose "TuneShuttle". **Check**: the device
       appears and the handoff logs in (Settings shows "Connected as ...", and `GET /api/status` shows
       `spotifyConnected: true`).
4. [ ] Play a track. **Check**: it is audible on receiver A after about 2 s (L = 1.75 s plus the receiver's buffer).
       The **Player** page shows the title and the artwork.
5. [ ] Move the Spotify volume slider. **Check**: receiver A's level follows, and the web app's master level moves.
6. [ ] Pause, resume, seek within the track, and skip to the next track. **Check**: no stale audio from before the
       seek or skip, and no audio from the old track after a skip (probe P19's open half). Note how long a seek takes:
       FLUSH answers from receiver A took 2-30 s in the probes. If seeks feel slow, record the time from the tap to the
       new audio.
7. [ ] Restart the bridge (or the service). **Check**: the Spotify device comes back without a new handoff (cached
       credentials), and the selected output is reconnected.
8. [ ] Settings, *Forget Spotify login* (`DELETE /api/spotify`). **Check**: the device needs a new handoff afterwards.
9. [ ] Let a session run for at least 10 minutes with music, and once for 10 minutes paused. **Check**: no dropout and
       no disconnect. This also answers P8's open half: does an idle session drop without feedback or progress?
       Diagnostics shows when a member was lost.
10. [ ] Disconnect the receiver in the web app. **Check**: Spotify keeps playing (the audio is discarded) and nothing
        is heard.

## 2. Listening tests on receiver A

| # | Run | Listen for | Last known result |
|---|---|---|---|
| 2.1 | `airplay2 play tone:3 --to "<receiver>" --level 15 --i-know` | A clean 440 Hz tone for 3 s with soft start and end. No clicks, no gaps. | 2026-10-09: passed on the wire twice (659 packets, retransmits answered, exit 0; in the final run the `TEARDOWN` was not answered within 10 s and the connection was closed). Not yet checked by a listener. |
| 2.2 | `airplay2 play <a music WAV, 16-bit 44.1 kHz stereo> --to "<receiver>" --level 20 --i-know` (any length; the CLI warns above 3 s, which is expected with you present) | Correct pitch and speed. No crackle, which would point to an ALAC or byte-order problem. Stereo channels on the correct sides. | Not run. |
| 2.3 | `airplay2 probe P15 "<receiver>" --i-know` (plays 2 s with a random `shk`) | The tone plays. If it is silent, receiver A needs `shk` = the shared secret, so keep D4 as it is. | 2026-10-09: the random key is accepted on the wire and the session runs normally; audibility not checked. |
| 2.4 | `airplay2 probe P19 "<receiver>" --i-know` (tone, 2 s pause, resume, seek-style flush) | No old audio after the resume or after the flush. | 2026-10-09: resume without a new RECORD works on the wire; FLUSH answers took from 2 s to more than 30 s; stale audio not checked by ear. |
| 2.5 | `airplay2 probe P18 "<receiver>" --i-know` | Whether the 2 s gap without sync packets is audible. The 3/6/10 s gaps the probe was designed for are **not possible with the CLI**: probes are capped at 3 s of audio (`PlaybackProbeOptions.MaxAudio`). Lifting the cap for a session with you present is a small change to that constant, not yet made. | 2026-10-09: a 2 s gap had no effect on the wire; audibility and longer gaps not checked. |
| 2.6 | `airplay2 probe P4 "<receiver>" --seconds 60` (no audio). During the 60 s, press receiver A's volume buttons and change the volume in its companion app. | The probe logs every event-channel message, feedback body and `GET_PARAMETER` change. If only the poll sees the change, the 3 s poll (D10) stays the mechanism. | 2026-10-09: inconclusive; a 10 s run without button presses saw no event-channel message and no change. |
| 2.7 | During step 1.4, open receiver A's companion app | Does it show title, artist or artwork? DMAP text is answered with `500`, but artwork and progress get `200`. | Not checked. |

## 3. Latency measurement with a microphone (P7, P20)

Needed for `offset_frames` and to confirm the default `L`.

Last known result: not run. Receiver A publishes no latency (`Audio-Latency` absent, no `audioLatencies`), so only a
measurement can tell.

1. [ ] Make a click track: a 44.1 kHz/16-bit stereo WAV with a short click every second for 10 s.
2. [ ] Play it locally (the sender's own speaker) and on receiver A, with one microphone recording both. For example,
       run `airplay2 play click.wav --to "<receiver>" --level 20 --latency 77175 --i-know` and start local
       playback at the moment of the first sync. Or record only receiver A and compare against the log timestamp of
       the first packet (`Timeline anchored ... due now`) plus L.
3. [ ] Repeat with `--latency 88200` and `--latency 66150`. P7 also asks about sync flags 4 vs 7; `play` has no switch
       for that, but `airplay2 probe P7 <name> --latency N --sync-flags 4 --i-know` does (3 s cap).
4. [ ] Record the measured output delay per `L` and whether every `L` played without dropouts. If receiver A lags
       or leads a reference by a constant amount, store it as the receiver's offset in the web app (Receivers,
       *Details*, offset; in frames, 44.1 per ms).

## 4. A group with a second real receiver (P12, P20)

The code path is tested with two fake receivers (identical packets, per-receiver keys, sync cadence, joins
mid-stream, flush/resume). What a real second receiver adds is audible sync between rooms and the mDNS `gid`
behaviour.

Last known result: not run with two real receivers. A single receiver A kept its own `gid` (`gcgl=0`) during a session
with our `groupUUID` (2026-10-09).

1. [ ] Choose the second receiver. Options: another AirPlay 2 speaker you own; receiver C **only with its owner's
       permission**; receiver B (it turns on; the owner's call). Check what `airplay2 discover` says about
       its pairing mode and timing.
   - A receiver that refuses NTP sessions (answers the NTP session SETUP with 501, or with 200 and no body as
     shairport-sync does) makes the whole group run under PTP (`AirplaySender.ChooseTransport`). Receiver A accepts
     PTP sessions as well (P1 control part, P17), but it has not yet played audio under PTP.
2. [ ] Web app, **Groups**: create a group with receiver A and the second receiver, set each member's level, then
       *Activate*. Play music from Spotify.
3. [ ] **Check**: both play. The rooms are in sync (stand between them: no echo; a click track makes it measurable).
       Moving the master slider scales both levels and keeps their difference. Changing one receiver's level on the
       device itself leaves the other unchanged.
4. [ ] Remove a member and add it back while playing. **Check**: it rejoins in sync, with no catch-up burst (D3).
5. [ ] Run `airplay2 discover` during the session. **Check P12**: does either receiver publish our `groupUUID` as its
       `gid`, and is `gcgl` 0?
6. [ ] P20, only if receiver C is allowed: microphone measurement (section 3) with both, and store receiver C's
       `offset_frames` if the two differ.

## 5. PIN pairing on a receiver that requires it (P6 for the TV)

The PIN flow is tested against the fake receiver and against shairport-sync with `--password`. Not tested yet: an
Apple-SDK receiver that shows a PIN on its screen. Receiver B is the one receiver on the LAN that requires a PIN
(flag bit 9). It wakes when contacted, so this step is the owner's decision.

Last known result: not run on a real PIN receiver. P6 on receiver A (2026-10-09): M1 with method 0 and with method 1
are both accepted.

1. [ ] Web app, **Receivers**: the receiver shows "PIN needed". Press *Pair with PIN* (or *Connect*, which raises a
       pairing request). The receiver shows a 4-digit PIN.
2. [ ] Enter the PIN in the web app's dialog and press *Pair*. **Check**: the pairing is stored, and *Connect* now goes
       through pair-verify without a new PIN, also after a bridge restart.
3. [ ] If M1 is refused: P6 asks whether a receiver with feature bit 51 clear wants method byte 0 or 1. The library
       sends 0 (`ReceiverSessionOptions.PinPairingMethod`). Retrying with 1 needs a one-line code change, because
       neither the bridge's configuration, the web app nor the CLI exposes that option: in
       `src/Bridge.Service/Output/AirplaySenderProvider.cs`, add
       `Session = new ReceiverSessionOptions { PinPairingMethod = Airplay2.Sender.Tlv8.TlvMethod.PairSetupWithAuth },`
       to the `new AirplaySenderOptions { ... }` initializer, rebuild, and pair again. Record the result as P6 (TV).
4. [ ] Wrong PIN: enter a wrong one once. **Check**: the web app reports it and *Start again* works.
5. [ ] *Forget pairing*, then connect again. **Check**: the PIN flow starts again.

## 6. The web app on real devices

Last known result: the layout, touch and language checks that a headless browser can do pass (`tools/web-audit.ps1`
over every route at 360-1280 px in all seven languages, and an emulated-touch pass over the DevTools protocol at
390×844 and 844×390: 44px range inputs, 24px thumbs, 34px theme buttons, 16px inputs, the theme picker opening on the
first tap and selecting on the second). The items below: not yet run (needs the owner's devices).

- [ ] On a desktop browser and on a phone: Player, Receivers, Groups, Settings and Diagnostics render and update
      live. With the bridge restarted, the WebSocket reconnects by itself.
- [ ] Rename the Spotify device in Settings. **Check**: the Spotify app shows the new name, after a short delay.
- [ ] Diagnostics: the live log streams, and the ports shown match `netstat -ano` for the bridge process.
- [ ] On-screen keyboard over the pairing dialog on **iOS Safari** and **Android Chrome**, portrait and landscape: the
      PIN field stays visible, and in landscape the Pair and Cancel buttons beside it stay visible too.
- [ ] `env(safe-area-inset-*)` on a notched iPhone, in Safari and as a home-screen web app.
- [ ] Drag feel of the 44px slider band: the Player's master slider, the Receivers slider and the group editor's
      member sliders. A vertical page scroll that starts on a slider still scrolls the page.
- [ ] `<select>` picker sheets on iOS (language, stream type, log level, member choice): the sheet opens, and the
      change event fires once.
- [ ] The theme picker on a real Android phone and a real iPhone: after a choice the pill collapses at once.
- [ ] A coarse-pointer tablet at 1024px or wider (for example an iPad in landscape): the touch rules apply with 34px
      theme buttons, and the centred tabs do not collide with the tools in Portuguese. Known: in headless Edge they
      overlap by about 14-22px in pt and ru while the theme pill is open; the collapsed pill fits.
- [ ] The scroll lock behind the pairing dialog on iOS Safari (iOS honours `overflow` on the root from version 16;
      older versions may still rubber-band the page).

## 7. Deployment

Last known result (2026-10-09): the container image ran on Docker's NAT bridge network against shairport-sync,
found it and ran a full control-plane session through the REST API (connect under PTP, volume, disconnect). The
Windows Service reboot test and the macvlan deployment have not been recorded.

- [ ] Windows Service: `install/windows/install-service.ps1` (elevated). Reboot. **Check**: the service comes up,
      Spotify sees the device, and the output reconnects. Then `-Uninstall` removes the service and its firewall
      rules.
- [ ] Container on the Kubernetes cluster with a macvlan interface (README, "In a container"). **Check**: the bridge
      discovers the receivers, receiver A connects, and Spotify Zeroconf works through the macvlan interface.

## 8. After the checks

- [ ] Put receiver A's volume back.
- [ ] Update the results above and `SPEC.md` section 4. If a result changes a default (for example, an offset for a
      receiver, or PTP for a second receiver), add a rule keyed on its TXT `srcvers` (and `features` if needed) to
      `src/Airplay2.Sender/Session/KnownReceiverQuirks.cs`, and update
      the "Resulting defaults" table in `SPEC.md` section 4.2 and `QuirkDefaultsTests` along with it.

## 9. AirPlay receiver probes (RP1-RP12)

TuneShuttle as an AirPlay 2 **receiver**: an iPhone, iPad or Mac sends to the bridge, and the bridge plays to its
selected output ([`SPEC-receiver.md`](SPEC-receiver.md) 11.3). Everything up to the sender is automated: the receiver
loopback tests (`tests/Airplay2.Receiver.Tests`) and the end-to-end bridge test
(`tests/Bridge.Tests/Integration/AirplayEndToEndTests.cs`: our own sender streams buffered ALAC and then realtime ALAC
under PTP to the bridge's receiver, the bridge plays it to a fake receiver, and volume, metadata, TEARDOWN and the
Spotify take-over are checked; loopback only, part of `./build.ps1`). What only a person can check is how Apple's
senders treat the receiver. Record each result here (date, the sender by kind and OS version, never by name or
address) and in [`SPEC.md`](SPEC.md) section 4.3 as a dated "Result (date): ..." line. A result that answers an open
question (RQ1-RQ11, SPEC-receiver.md 12) names it and says whether its default stays.

Last known result: none of RP1-RP12 has been run.

Preparation for every item:

- [ ] `./build.ps1 -SkipContainer` passes, and `artifacts/bridge` contains the FFmpeg DLLs (`avcodec-61.dll`; RP11
      removes them on purpose).
- [ ] Firewall: install the service (step 1.1), or allow inbound TCP 7000 and UDP 319-320 for `bridge.exe` (README,
      "Firewall and network"). No other AirPlay receiver (shairport-sync, for example) runs on this machine (R13).
- [ ] Start the bridge with the receiver's log at Debug:
      `dotnet run --project src/Bridge.Service -c Release -- --Logging:LogLevel:Airplay2.Receiver=Debug --Logging:LogLevel:Bridge.Service.Airplay=Debug`
      (for the service, the same keys in `appsettings.json`). Keep the log as `artifacts/live/<date>-RP<n>.log`.
- [ ] Output: receiver A as the single output at level 15-20. The section 2 rules apply: never the TV, receiver C only
      with its owner's permission. The phone or Mac is on the same LAN as the bridge.
- [ ] Settings, **AirPlay receiver**: on, the default name, no password (RP9 sets one).

1. [ ] **RP1, discovery and connection** (RQ2, RQ9). On the iPhone, open Control Center, long-press the audio card and
       open the AirPlay list. **Check**: the receiver's name is listed within a few seconds, and choosing it connects.
       The log shows `GET /info`, `POST /fp-setup`, transient pair-setup and pair-verify, then a session SETUP with
       `timingProtocol: PTP`. Diagnostics, **AirPlay receiver** shows the state *connected* and the phone's name. Note
       whether a `timingProtocol: None` session appears ("remote-control-only session SETUP accepted", RQ9), and
       whether the phone stays connected after the `updateInfo` on the event channel (RQ2).
       *Last known result: not yet run.*
2. [ ] **RP2, stream type and format** (RQ3). Play a song in the Music app, then audio from a second app (a podcast
       app, a video in Safari). **Check**: the log line `stream <id> set up: type <96|103>, <format>` for each, and for
       a buffered stream (type 103) the SSRC of its first block (Debug log). Diagnostics shows the same stream type and
       format. Record which app used which. *Last known result: not yet run.*
3. [ ] **RP3, audio and delay** (RQ6, RQ7). Press play on the phone. **Check**: receiver A plays within 3 s of the
       tap, and the phone's progress and what you hear stay together within the expected delay (5.4: about 2 s for a
       buffered stream, plus the learnt offset `D`, about 0.45 s, for a realtime stream; Diagnostics shows `D` and the
       buffer). For RQ7, repeat once with the latency in Settings set to 22050 frames (0.5 s), then put it back.
       *Last known result: not yet run.*
4. [ ] **RP4, transport from the phone**. Pause, resume, seek forward and back, next track, previous track. **Check**:
       each acts within about 1 s, with no old audio after a seek or a skip and nothing stuck. The log shows the
       FLUSHBUFFERED forms (immediate for pause and seek, deferred for a gapless track change) and SETRATEANCHORTIME
       with rates 0 and 1. *Last known result: not yet run.*
5. [ ] **RP5, ten minutes with the passive PTP slave** (RQ4, RQ5). Let music play for 10 minutes. **Check**: no
       disconnect and no audible drift. Note the PTP offset and the late, missing and drift counters from Diagnostics,
       **AirPlay receiver** at the end. In the log, look for PTP messages from the phone that the slave leaves
       unanswered (Delay_Req, Signaling) and for "Anchor timeline ... differs from the grandmaster" (RQ4).
       *Last known result: not yet run.*
6. [ ] **RP6, volume from the phone**. Use the volume buttons and the slider in Control Center. **Check**: the master
       level in the web app follows (a level change with the source `airplay`), and so does receiver A's level. The
       phone's `GET_PARAMETER volume` is answered, and its slider does not jump back. *Last known result: not yet run.*
7. [ ] **RP7, metadata**. Skip through three tracks with artwork. **Check**: within 2 s of each track change the Player
       page shows "AirPlay · <sender>", the title, artist, album, artwork and progress; receiver A's companion app
       shows what it shows for Spotify (compare 2.7). *Last known result: not yet run.*
8. [ ] **RP8, take-over both ways** (R8). (a) Spotify plays through the bridge; start AirPlay from the phone.
       **Check**: Spotify pauses and the phone's audio plays. (b) While AirPlay plays, press play in the Spotify app.
       **Check**: the phone shows the speaker as disconnected and Spotify plays. (c) With a second phone of your own,
       start AirPlay while the first one plays. **Check**: the first phone is disconnected and the second one plays.
       *Last known result: not yet run.*
9. [ ] **RP9, password mode** (RQ1, RQ8). Settings, **AirPlay receiver**: set a password, then choose the receiver on
       the phone. **Check**: the phone asks for the password; a wrong one is refused (log: pair-setup M3 answered with
       `Error 2`); the right one connects (pair-setup M1-M6, then pair-verify). Disconnect and connect again: no second
       prompt (pair-verify only). Note whether the phone went on to regular pair-setup after the 470 on transient M1 or
       needed anything else (RQ8). Clear the password afterwards. *Last known result: not yet run.*
10. [ ] **RP10, a Mac as the sender** (RQ9, RQ10). On a Mac, use the Music app's AirPlay menu, then the system sound
        output (Control Center, Sound). **Check**: RP1-RP4 hold for both. Note the stream type the system output uses
        and whether its extra delay is acceptable (RQ10). *Last known result: not yet run.*
11. [ ] **RP11, without libavcodec** (RQ11). Start the bridge with `--Bridge:Airplay:FfmpegPath=<an empty folder>`.
        **Check**: the log says "No AAC decoder ... ALAC-only formats" and Diagnostics shows the AAC decoder as not loaded
        (ALAC only). Does the phone still list and use the receiver, and which stream type and format does it send
        (RP2's log line)? *Last known result: not yet run.*
12. [ ] **RP12, settings while the phone watches**. Keep the phone's AirPlay list open. In Settings, rename the
        receiver, then turn it off, then on again. **Check**: the list shows the new name, drops the receiver and lists
        it again, each within 10 s. *Last known result: not yet run.*

After the probes: put receiver A's volume back, clear the receiver password if one is left, and update the results
above and `SPEC.md` section 4.3. If a result changes a default (the feature word, a timing rule, the latency), change
`ReceiverFeatures`, the `PlayoutOptions` defaults or `AirplayOptions` together with SPEC-receiver.md and the tests that
pin them.
