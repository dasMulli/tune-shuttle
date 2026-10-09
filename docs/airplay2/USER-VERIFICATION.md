# User verification checklist

Status: 2026-10-09, written by `final-integration` (WORK-PLAN.md wave 8). The agents ran everything that does not need
a person in the room: the build, the offline tests (735 after the final review fixes: 601 sender, 134 bridge), every
shairport-sync live test, Sender M1 against the Test Speaker ABCD (a 3-second quiet tone), the bridge's
connect/volume/disconnect against the SpeakerA without audio and against shairport-sync from the container image. The results are in `SPEC.md` section 4. This list holds what is left: steps
that need someone to log in to Spotify, listen, press buttons on a speaker, measure with a microphone, enter a PIN, or
give permission for another person's device.

Each item says what to run, what to look for, and where to record the result. Record outcomes in `SPEC.md`
section 4 as a dated "Result (date): ..." line, as the probes so far have been. Live logs go to `artifacts/live/`.

Live-device rules still apply. AirPlay wakes receivers, and they play at their current volume. Contacting the LG TV
is your decision, because it will turn on. The Apple receiver "Office" may be used only with its owner's permission.
Start every listening test at a low level (15-20) and raise it from the web app.

## 0. Preparation

- [ ] `./build.ps1 -SkipContainer` passes (Release build, offline tests, publish to `artifacts/bridge` and
      `artifacts/airplay2`).
- [ ] Firewall: install the service (step 1) or add an inbound allow rule for `artifacts\airplay2\airplay2.exe`
      (README, "Firewall"). Without the rule, the SpeakerA answers the stream SETUP with `400` after about 30 s, because
      its NTP timing requests are blocked.
- [ ] Note the SpeakerA's current volume, so you can put it back afterwards.

## 1. Bridge M1: Spotify login through the bridge (PLAN.md milestone 4)

1. [ ] Start the bridge: `dotnet run --project src/Bridge.Service -c Release`, or install it as a service with
       `install/windows/install-service.ps1` (elevated) and open `http://localhost:8080`.
2. [ ] In the web app, under **Receivers**, set the SpeakerA's level to 15, then press *Connect*. Expect state
       *Connected* within about 10 s. On a bad Wi-Fi day the SpeakerA answers RTSP slowly; up to 60 s
       (`Bridge:ConnectTimeout`) is still fine.
3. [ ] In the Spotify app (phone, same LAN), open the device picker and choose "AirPlay Bridge". **Check**: the device
       appears, the handoff logs in (Diagnostics shows the Spotify user, and Status shows `spotifyConnected: true`).
4. [ ] Play a track. **Check**: it is audible on the SpeakerA after about 2 s (L = 1.75 s plus the receiver's buffer).
       **Now Playing** shows the title and the artwork.
5. [ ] Move the Spotify volume slider. **Check**: the SpeakerA's level follows, and the web app's master level moves.
6. [ ] Pause, resume, seek within the track, and skip to the next track. **Check**: no stale audio from before the
       seek or skip, and no audio from the old track after a skip (probe P19's open half). Note how long a seek takes:
       FLUSH answers from the SpeakerA took 2-30 s in the probes. If seeks feel slow, record the time from the tap to the
       new audio.
7. [ ] Restart the bridge (or the service). **Check**: the Spotify device comes back without a new handoff (cached
       credentials), and the selected output is reconnected.
8. [ ] Settings, *Forget Spotify login* (`DELETE /api/spotify`). **Check**: the device needs a new handoff afterwards.
9. [ ] Let a session run for at least 10 minutes with music, and once for 10 minutes paused. **Check**: no dropout and
       no disconnect. This also answers P8's open half: does an idle session drop without feedback or progress?
       Diagnostics shows when a member was lost.
10. [ ] Disconnect the receiver in the web app. **Check**: Spotify keeps playing (the audio is discarded) and nothing
        is heard.

Record the outcome as "Bridge M1 Result" in SPEC.md section 4.

## 2. Listening tests on the SpeakerA (Sender M1/M2 audibility and the open probe halves)

| # | Run | Listen for | Records |
|---|---|---|---|
| 2.1 | `airplay2 play tone:3 --to "Test Speaker ABCD" --level 15 --i-know` | A clean 440 Hz tone for 3 s with soft start and end. No clicks, no gaps. | Sender M1 audibility (SPEC.md section 4, "Sender M1") |
| 2.2 | `airplay2 play <a music WAV, 16-bit 44.1 kHz stereo> --to "Test Speaker ABCD" --level 20 --i-know` (any length; the CLI warns above 3 s, which is expected with you present) | Correct pitch and speed. No crackle, which would point to an ALAC or byte-order problem. Stereo channels on the correct sides. | Sender M1 with music |
| 2.3 | `airplay2 probe P15 "Test Speaker ABCD" --i-know` (plays 2 s with a random `shk`) | The tone plays. If it is silent, the SpeakerA needs `shk` = the shared secret, so keep D4 as it is. | P15 audio half |
| 2.4 | `airplay2 probe P19 "Test Speaker ABCD" --i-know` (tone, 2 s pause, resume, seek-style flush) | No old audio after the resume or after the flush. | P19 open half |
| 2.5 | `airplay2 probe P18 "Test Speaker ABCD" --i-know` | Whether the 2 s gap without sync packets is audible. The 3/6/10 s gaps the probe was designed for are **not possible with the CLI**: probes are capped at 3 s of audio (`PlaybackProbeOptions.MaxAudio`). Lifting the cap for a session with you present is a small code change to that constant, not yet made. | P18 open half |
| 2.6 | `airplay2 probe P4 "Test Speaker ABCD" --seconds 60` (no audio). During the 60 s, press the SpeakerA's volume buttons and change the volume in the SpeakerA app. | The probe logs every event-channel message, feedback body and `GET_PARAMETER` change. If only the poll sees the change, D10's 3 s poll stays the mechanism. | P4 (settles D10) |
| 2.7 | During step 1.4, open the SpeakerA's companion app | Does it show title, artist or artwork? DMAP text is answered with `500`, but artwork and progress get `200`. | P9 app half |

## 3. Latency measurement with a microphone (P7, P20)

Needed for `offset_frames` and to confirm the default `L`.

1. [ ] Make a click track: a 44.1 kHz/16-bit stereo WAV with a short click every second for 10 s.
2. [ ] Play it locally (sender's own speaker) and on the SpeakerA, with one microphone recording both. For example, run
       `airplay2 play click.wav --to "Test Speaker ABCD" --level 20 --latency 77175 --i-know` and start local playback
       at the moment of the first sync. Or record only the SpeakerA and compare against the log timestamp of the
       first packet (`Timeline anchored ... due now`) plus L.
3. [ ] Repeat with `--latency 88200` and `--latency 66150`. P7 also asks about sync flags 4 vs 7; the CLI `play` has
       no switch for that, but `airplay2 probe P7 <name> --latency N --sync-flags 4 --i-know` does (3 s cap).
4. [ ] Record the measured output delay per `L` and whether every `L` played without dropouts. If the SpeakerA lags
       or leads a reference by a constant amount, store it as the receiver's offset in the web app (Receivers,
       offset field; in frames, 44.1 per ms).

## 4. A group with a second real receiver (Sender M3, P12, P20)

The code path is tested with two fake receivers (identical packets, per-receiver keys, sync cadence, joins
mid-stream, flush/resume). What a real second receiver adds is audible sync between rooms and the mDNS `gid`
behaviour.

1. [ ] Choose the second receiver. Options: another AirPlay 2 speaker you own; the Apple receiver "Office" **only with
       its owner's permission**; the LG TV (it turns on; your call). Check what `airplay2 discover` says about its
       pairing mode and timing.
   - A receiver that refuses NTP sessions (answers the NTP session SETUP with 501, or with 200 and no body as
     shairport-sync does) makes the whole group run under PTP (`AirplaySender.ChooseTransport`). The SpeakerA accepts
     PTP sessions as well (P1 control part, P17), but it has not yet played audio under PTP.
2. [ ] Web app, **Groups**: create a group with the SpeakerA and the second receiver, set each member's level, then
       *Activate*. Play music from Spotify.
3. [ ] **Check**: both play. The rooms are in sync (stand between them: no echo; a click track makes it measurable).
       Moving the master slider scales both levels and keeps their difference. Changing one receiver's level on the
       device itself leaves the other unchanged.
4. [ ] Remove a member and add it back while playing. **Check**: it rejoins in sync, with no catch-up burst (D3).
5. [ ] Run `airplay2 discover` during the session. **Check P12**: does either receiver publish our `groupUUID` as its
       `gid`, and is `gcgl` 0? The SpeakerA alone kept its own `gid`.
6. [ ] P20, only if the Apple receiver is allowed: microphone measurement (section 3) with both, and store the HomePod's
       `offset_frames` if the two differ.

Record as "Sender M3 Result" and P12/P20 results in SPEC.md section 4.

## 5. PIN pairing on a receiver that requires it (Sender M2, P6 for the TV)

The PIN flow is tested against the fake receiver and against shairport-sync with `--password`. Not tested yet: an
Apple-SDK receiver that shows a PIN on its screen. The LG TV is the one receiver on the LAN that requires a PIN
(flag bit 9). It wakes when contacted, so this step is your decision.

1. [ ] Web app, **Receivers**: the receiver shows "PIN" pairing. Press *Pair with PIN* (or *Connect*, which raises a
       pairing request). The receiver shows a 4-digit PIN.
2. [ ] Enter the PIN in the web app's dialog and press *Pair*. **Check**: the pairing is stored (pairing state *Paired*), and *Connect*
       now goes through pair-verify without a new PIN, also after a bridge restart.
3. [ ] If M1 is refused: P6 asks whether a receiver with feature bit 51 clear wants method byte 0 or 1. The library
       sends 0 (`ReceiverSessionOptions.PinPairingMethod`). Retrying with 1 needs a one-line code change, because neither
       the bridge's configuration, the web app nor the CLI exposes that option: in
       `src/Bridge.Service/Output/AirplaySenderProvider.cs`, add
       `Session = new ReceiverSessionOptions { PinPairingMethod = Airplay2.Sender.Tlv8.TlvMethod.PairSetupWithAuth },` to the
       `new AirplaySenderOptions { ... }` initializer, rebuild, and pair again. Record the result as P6 (TV).
4. [ ] Wrong PIN: enter a wrong one once. **Check**: the web app reports it and *Start again* works.
5. [ ] *Forget pairing*, then connect again. **Check**: the PIN flow starts again.

## 6. Web M1 walkthrough (PLAN.md milestone 5)

- [ ] On a desktop browser and on a phone: Receivers, Groups, Now Playing, Settings and Diagnostics render and update
      live. With the bridge restarted, the WebSocket reconnects by itself.
- [ ] Rename the Spotify device in Settings. **Check**: the Spotify app shows the new name, after a short delay.
- [ ] Diagnostics: the live log streams, and the ports shown match `netstat -ano` for the bridge process.

## 7. Deployment

- [ ] Windows Service: `install/windows/install-service.ps1` (elevated). Reboot. **Check**: the service comes up,
      Spotify sees the device, and the output reconnects. Then `-Uninstall` removes the service and its firewall
      rules.
- [ ] Container on the Kubernetes cluster with a macvlan interface (README, "In a container"). **Check**: the bridge
      discovers the receivers, the SpeakerA connects, and Spotify Zeroconf works through the macvlan interface. The image
      was smoke-tested only on Docker's NAT bridge network, where it found the shairport-sync test receiver and ran a
      full control-plane session with it.

## 8. After the checks

- [ ] Put the SpeakerA's volume back.
- [ ] Update SPEC.md section 4 with every result above. If a result changes a default (for example, an offset for a
      model, or PTP for a second model), add it to `src/Airplay2.Sender/Session/KnownReceiverQuirks.cs`, and update the
      "Resulting defaults" table in SPEC.md section 4 and `QuirkDefaultsTests` along with it.
