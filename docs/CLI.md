# The CLI

`airplay2` drives the sender library without Spotify (published by `build.ps1` to `artifacts/airplay2/`, or run with
`dotnet run --project ../src/Airplay2.Sender.Cli -- <command>`):

```powershell
airplay2 discover
airplay2 info "Kitchen"
airplay2 pair "Living Room"                          # the PIN flow if the receiver asks for one
airplay2 play music.wav --to "Kitchen" --to "Living Room" --level 30 --i-know
airplay2 play tone:3 --to "Kitchen" --level 15 --i-know
airplay2 volume "Kitchen" 25
```

| Command | Does |
|---|---|
| `discover [--seconds 5]` | Browses `_airplay._tcp` and `_raop._tcp` and prints every receiver with its decoded features, pairing mode, stream types and timing protocol. |
| `info <name or IP> [--port 7000] [--qualifier]` | Unauthenticated `GET /info`. |
| `pair <name> [--pin 1234] [--forget]` | Pairs (transient check, or the PIN flow; asks for the PIN when `--pin` is missing) and stores the keys in `airplay2-pairings.json` next to the executable; `--forget` deletes them. |
| `play <file.wav or tone:<s>> --to <name> [--to ...] [--level 15] [--latency 77175] [--no-metadata] --i-know` | Plays a 16-bit 44.1 kHz stereo WAV, or a quiet 440 Hz tone of `<s>` seconds, to one receiver or to several in sync. |
| `volume <name> [level]` | Reads or sets a receiver's volume. |
| `probe <P1..P21> <name> [--i-know] [--seconds] [--level] [--latency] [--sync-flags] [--withhold]` | Runs one of the protocol probes of [`docs/airplay2/SPEC.md`](airplay2/SPEC.md) section 4 and appends its log to `artifacts/live/`. |

Global options: `--verbose` / `-v` (Debug log on the console, including the decrypted wire exchange) and
`--log-file <path>`.

**`--i-know` is required for any audio.** Without it, `play` and every probe that sends audio exit with code 2 before
they open a socket, because AirPlay wakes receivers and plays at their current volume. Exit codes: 0 ok, 1 failure,
2 audio refused, 3 probe not available, 130 interrupted (after Ctrl+C the CLI still sends `TEARDOWN`, for up to 60 s).
