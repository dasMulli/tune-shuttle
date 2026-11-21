# Documentation

| Document | For | What it holds |
|---|---|---|
| [`../README.md`](../README.md) | Everyone | What TuneShuttle is, its features and a quick start |
| [`INSTALL.md`](INSTALL.md), [`USER-GUIDE.md`](USER-GUIDE.md), [`CONFIGURATION.md`](CONFIGURATION.md), [`NETWORK.md`](NETWORK.md), [`CLI.md`](CLI.md), [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) | Users | Installing, using the web app and the AirPlay receiver, settings and the API, ports and firewall, the `airplay2` CLI, receiver quirks and common problems |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Everyone | Components and data flow, the group volume model, persistence, the receiver inventory and pruning, the full API, deployment shapes, design decisions |
| [`DEVELOPMENT.md`](DEVELOPMENT.md) | Contributors | Repository layout, build and test, live tests and the live-device rules, code conventions, test receivers, web app development, themes, translations, the container image |
| [`REFERENCE-SOURCES.md`](REFERENCE-SOURCES.md) | Contributors | The upstream projects the code was written against, their commits and licences, and how to clone them into `reference/` |

## AirPlay 2 (`airplay2/`)

| Document | What it holds |
|---|---|
| [`airplay2/SPEC.md`](airplay2/SPEC.md) | The index of the protocol specification: the decisions D1-D17, the contradictions between the sources and their resolution, the receiver findings with every live probe result, the resulting defaults, the status of the receiver probes RP1-RP12 (4.3), and a glossary of the names used in code |
| [`airplay2/SPEC-control.md`](airplay2/SPEC-control.md) | The control plane: discovery, RTSP and the encrypted channel, pairing, session setup, `SET_PARAMETER`, sequence diagrams |
| [`airplay2/SPEC-audio.md`](airplay2/SPEC-audio.md) | The audio plane: the realtime stream, NTP timing, sync and retransmits, group playback, the buffered stream and PTP, ALAC, volume and metadata on the wire, the sender pipeline |
| [`airplay2/SPEC-receiver.md`](airplay2/SPEC-receiver.md) | TuneShuttle as an AirPlay 2 receiver: decisions, advertising, control and audio plane in the receiver role, PTP slave, decoders, the source switch, API/web/deployment changes, tests, live probes, implementation waves |
| [`airplay2/TEST-RECEIVER.md`](airplay2/TEST-RECEIVER.md) | The shairport-sync test receiver in Docker: setup, what it can and cannot test, what it exercised |
| [`airplay2/MANUAL-TESTS.md`](airplay2/MANUAL-TESTS.md) | The checklist of tests that need a person (listening, Spotify, a second receiver, PIN pairing, real phones, deployments, and the AirPlay receiver probes RP1-RP12 with an iPhone or a Mac as the sender) with their last known results |
| [`airplay2/probes/`](airplay2/probes) | The scripts and raw outputs of the first live probes (mDNS browse, `GET /info`) and the vector generators |
| [`airplay2/test-vectors/`](airplay2/test-vectors) | ALAC, SRP and captured `/info` vectors the unit tests check against |

## Web app (`web/`)

| Document | What it holds |
|---|---|
| [`web/UI-SPEC.md`](web/UI-SPEC.md) | The web app reference: tokens and CSS, theme, language, layout and every page, mobile breakpoints, accessibility, the status pill, the file map and the complete copy catalog (de + en) |
| [`web/mockups/`](web/mockups) | The design mockups (desktop light/en, desktop dark/de, phone light/en) |

The librespot port documents itself: [`../librespot-dotnet/README.md`](../librespot-dotnet/README.md) and
[`../librespot-dotnet/docs/PORTING-NOTES.md`](../librespot-dotnet/docs/PORTING-NOTES.md).
