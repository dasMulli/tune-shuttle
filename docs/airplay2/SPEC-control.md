# AirPlay 2 sender specification, part 1: control plane

Status: research draft, 2026-10-09; contradictions with the audio plane resolved by the architect the same day
(see `SPEC.md`, which indexes both parts, holds the v1 decisions and the merged open-question list); review pass of
the same day re-verified every pyatv line number against the b277a4c checkout (the pyatv `auth/__init__.py`,
`hap_session.py`, `chacha20.py`, `hap_transient.py`, `channels.py` and `dmap/tags.py` citations had been taken from a
different tree and are corrected below), added the `GET_PARAMETER` body shape (4.11), the receive-side frame size
(2.5), the `DACP-ID` wording (2.2) and the `/auth-setup` request (3.10). Written against the reference checkouts
listed in `docs/REFERENCE-SOURCES.md`. Companion to `SPEC-audio.md` (the data-plane
specification: RTP, ALAC, timing packets and the audio encryption). This document ends where the UDP/TCP audio sockets begin; it
describes everything that travels over the RTSP control connection and the event channel, plus the
discovery data that decides how a receiver must be approached.

Binding decisions from `docs/PLAN.md` that shape this document: single receivers and groups from v1, sender
is the clock master, transient pairing first then full HomeKit pairing with a PIN entered in the web app,
realtime stream type (96) first with the buffered type (103) kept pluggable, keys persisted in SQLite.

## 0. Conventions and sources

Citations are `[short:file:line]` or `[short:file:line-line]`, pointing into `reference/`:

| Short | Repository and role | License note |
|---|---|---|
| `pyatv` | `reference/pyatv` (b277a4c). Sender: HAP pairing, RAOP/AirPlay v2 streaming client, remote control session. Primary code reference. | MIT |
| `sps` | `reference/shairport-sync` (01078ad). AirPlay 2 receiver; `pair_ap/` is the HomeKit pairing library (client and server side, both usable as reference). | MIT |
| `ap2r` | `reference/airplay2-receiver` (6c343d3). Python receiver with protocol notes, transient and regular pairing, both stream types. | see repo |
| `owntone` | `reference/owntone-server` (4fa5dcc). Sender that drives several AirPlay 2 receivers in sync. **Read for understanding only; never transcribe its code.** Cited so an implementer can confirm behaviour, not copy it. | GPL-2.0 |
| `probe` | Live results captured on 2026-10-09 against the LAN receivers (sections 1.7 and 1.8). Scripts and raw output under `docs/airplay2/probes/` (see appendix B). | n/a |

Terminology: "sender" is us (the controller, the "iOS device" in HAP wording); "receiver" is the speaker
(the "accessory"). "bplist" is Apple's binary property list (`bplist00` magic); Python's `plistlib`
`FMT_BINARY` is the encoder pyatv uses [pyatv:pyatv/support/rtsp.py:284-289]. All multi-byte integers in
bplists are big-endian by the bplist format; all integers inside the HAP encryption framing are
little-endian (section 2.5). Byte strings in this document are written as hex unless quoted.

Where sources disagree, the disagreement is called out in a block starting with **Disagreement**. Where a
fact is only known from one source and could not be cross-checked it is marked **(single source)**.

---

## 1. Discovery

### 1.1 Service types and instance names

An AirPlay 2 receiver advertises two DNS-SD services on the same host and (usually) the same TCP port 7000:

- `_airplay._tcp.local` with the instance name equal to the user-visible name, e.g.
  `Test Speaker ABCD._airplay._tcp.local` [probe]. This is the service a sender uses for AirPlay 2
  (HAP pairing, bplist SETUP). pyatv scans for it [pyatv:pyatv/protocols/airplay/__init__.py:1-55 (scan
  handlers)], airplay2-receiver registers it with `properties=mdns_props` and port 7000
  [ap2r:ap2-receiver.py:1192-1202].
- `_raop._tcp.local` with the instance name `<deviceid without colons>@<name>`, e.g.
  `021A2B3C4D5E@Test Speaker ABCD._raop._tcp.local` [probe]; pyatv splits on `@` to get the name
  [pyatv:pyatv/protocols/raop/__init__.py:438-441]. This is the AirPlay 1 / RAOP service; its TXT record
  carries the audio-capability keys (`et`, `cn`, `md`, `tp`, `sr`, `ss`, `ch`).

Both records share `pk` (the receiver's long-term Ed25519 public key, hex) and the features bitmask
(`features=` on `_airplay`, `ft=` on `_raop`) [probe; sps:bonjour_strings.c:85-107,155-165].

The SRV target hostname and A/AAAA records give the address; on our LAN the SpeakerA answers with an IPv4
address and two IPv6 addresses, the LG TV with IPv4 only [probe]. The sender must keep the address family
it will use consistent across the RTSP connection, the `timingPeerInfo.Addresses` it sends, and the
`SETPEERS` list (section 4.6), because receivers match timing peers by address.

The librespot port already has an mDNS packet parser/encoder (`librespot-dotnet/src/Librespot.Discovery/Mdns/DnsPacket.cs`:
`DnsMessage.Parse`, `DnsRecordData.Ptr/Srv/Txt/A/Aaaa`, `Txt.Strings()`); the browser needs only a PTR
query for both service types and a cache keyed by instance name. mDNS answers may arrive from a reflector
(on our LAN every answer came from 203.0.113.1 even though the receivers live on 192.0.2.x and
198.51.100.x [probe]); the sender must take the address from the A/AAAA records, never from the UDP source.

**Note (2026-10-09, mdns-browser live run).** What the reflector actually sends, which the section above did not
cover: a PTR query sent *from port 5353* is answered by multicast with normal TTLs (PTR and TXT 4500 s, SRV/A/AAAA
120 s on the SpeakerA; 4500 s for everything on the HomePod), the cache-flush bit on the unique records, all records in
the answer section, plus NSEC (type 47) records. The same query sent *from an ephemeral port* (what
`probes/mdns_browse.py` does) is a legacy unicast query (RFC 6762 6.7): the reflector answers by unicast with every TTL
capped at **10 s**, records in the additional section, and no cache-flush bits; in that run the SpeakerA did not answer
the ephemeral-port query at all while the LG TV and the HomePod did. Consequences in `MdnsReceiverBrowser`: it sends
every query both ways (5353 multicast socket plus one ephemeral socket per interface), raises the TTL of
legacy-unicast records to two query intervals so they do not expire between browses, and treats a goodbye (TTL 0) as
"expires in one second" (RFC 6762 10.1) because a responder that announces on several interfaces/families can deliver
a stale answer after its first goodbye. The live browse also returned a third SpeakerA AAAA address
(`2001:db8:2:4235:1a2b:3cff:fe4d:5e01`) and the TV's name decodes as `Living room TV` (U+2019, valid UTF-8 in
the label; the `�` in `probes/browse.json` comes from how the probe output was saved, not from the receiver).

### 1.2 `_airplay._tcp` TXT keys

Observed on the LAN [probe], documented by pyatv [pyatv:docs/documentation/protocols.md:1218-1238] and
generated by shairport-sync [sps:bonjour_strings.c:155-165; sps:rtsp.c:999-1104] and airplay2-receiver
[ap2r:ap2-receiver.py:231-254].

| Key | Example (SpeakerA) | Meaning, and what we do with it |
|---|---|---|
| `features` | `0x445F8A00,0x1C340` | 64-bit feature bitmask, printed as `0x<low 32 bits>,0x<high 32 bits>`; a single `0x...` value means the high word is zero [pyatv:pyatv/protocols/airplay/utils.py:352-366; sps:shairport.c:1622-1626]. Full bit table in 1.3. Decides AirPlay 2 vs 1, pairing style, stream types, metadata. |
| `flags` | `0x404` | Status flags bitmask (hex). Same value appears as `statusFlags` (integer) in `GET /info` and as `sf=` on `_raop` [probe]. Table in 1.4. Decides PIN / password requirement. |
| `pk` | `REDACTED-RECEIVER-KEY` (64 hex chars) | Receiver's long-term Ed25519 public key (32 bytes). Identical in both services. Can be compared against the LTPK learned in regular pair-setup M6 (3.6) to detect a factory reset. |
| `pi` | `02:1A:2B:3C:4D:5E` | "Pairing identifier" / public ID. SpeakerA uses its MAC, Apple devices a UUID [pyatv:docs/documentation/protocols.md:1226]. airplay2-receiver uses it as the HAP accessory pairing ID that appears in TLV `Identifier` [ap2r:ap2-receiver.py:34-36,276]. Persist it with the pairing. |
| `psi` | (absent on SpeakerA; `00000000-0000-0000-0000-024C47000001` on LG) | "Public AirPlay pairing identifier" [pyatv:docs/documentation/protocols.md:1229]; shairport sets it from config [sps:rtsp.c:1082-1085]. Informational. |
| `gid` | `02:1A:2B:3C:4D:5E` | Group UUID the receiver currently belongs to. Equals `pi` when the receiver is not in a group [sps:rtsp.c:1040-1044 (falls back to pi)]. A receiver updates it to the `groupUUID` from a session SETUP [sps:rtsp.c:2501-2511,2663-2669]. We read it only for display. |
| `gcgl` | `0` | "Group contains group leader" (0/1) [pyatv:docs/documentation/protocols.md:1236; sps:rtsp.c:1050-1061]. |
| `igl` | (absent) | "Is group leader" [pyatv:docs/documentation/protocols.md:1223]. |
| `srcvers` | `366.0` | AirPlay source (SDK) version string. Receivers key behaviour off it: airplay2-receiver notes that iOS senders use PTP + buffered audio when `srcvers >= 355`, realtime + NTP when `<= 355`, no `shk` when `<= 350`, ANNOUNCE (AirPlay 1) when 200 [ap2r:ap2-receiver.py:58-67]. shairport-sync advertises `366.0` [sps:shairport.c:569]. |
| `model` | `Test Speaker ABCD` | Model string. pyatv uses `AudioAccessory*` / `AppleTV*` prefixes to guess remote-control support [pyatv:pyatv/protocols/airplay/utils.py:413-428] and refuses `Mac\d+,\d+` [utils.py:282]. |
| `manufacturer` | `Lautsprecher SpeakerA GmbH` | Informational. |
| `serialNumber` | `00000000-...` | Informational. |
| `deviceid` | `02:1A:2B:3C:4D:5E` | MAC-style device identifier; mirrored as `deviceID` in `/info` [probe]. |
| `acl` | `0` | Access control level: 0 = everyone on the LAN, 1 = HomeKit home members, 2 = admins [ap2r:ap2-receiver.py:1080-1084]. pyatv marks `acl=1` as "pairing disabled" [pyatv:pyatv/protocols/airplay/utils.py:514-517]. |
| `act` | (absent) | Access control type; pyatv treats `2` ("Current User") as unsupported [pyatv:pyatv/protocols/airplay/utils.py:394-405]. |
| `rsf` | `0x0` (SpeakerA), `0x3` (LG) | "Required sender features" bitmask [ap2r:ap2-receiver.py:250-251]. Meaning of bits unknown. **(single source)** |
| `fex` | (absent on SpeakerA; `0Ip/AEbPiwNACA` on LG) | "features extended": shairport encodes its 64-bit features little-endian into 8 bytes and base64s them without padding [sps:shairport.c:1663-1679]. |
| `fv` | `p20.1.1.36.0x8b4c480` | Firmware version (non-Apple) [pyatv:docs/documentation/protocols.md:1200]. |
| `protovers` | `1.1` | Protocol version. Also `protocolVersion` in `/info`. |
| `at` | (LG: `0x1`) | Unknown. **(single source: probe)** |
| `integrator` | (LG: `LG Electronics`) | Informational. |
| `btaddr` | (absent) | Bluetooth address [pyatv:docs/documentation/protocols.md:1238]. |
| `vv` | (absent in TXT; shairport sends `vv=2`) | Unknown version field, also in `/info` [sps:rtsp.c:1100]. |
| `osvers` | (absent) | OS version (Apple devices) [sps:rtsp.c:1096]. |
| `pw` | (absent) | `true` when a password is needed (AirPlay 1 style) [pyatv:pyatv/protocols/airplay/utils.py:369-384]. |

### 1.3 The `features` bitmask

Bit numbering is from the 64-bit value (bit 0 = LSB of the first hex word). The names come from three
lists that mostly agree: pyatv's `AirPlayFlags` [pyatv:pyatv/protocols/airplay/utils.py:303-346] (imported
from emanuelecozzi.net), airplay2-receiver's `FeatureFlags` with field notes from experiments
[ap2r:ap2/bitflags.py:59-150], and owntone's `features_map` with the conditions Apple's sender code checks
[owntone:src/outputs/airplay.c:393-437]. Column "We" says what the sender does with the bit.

| Bit | pyatv name | airplay2-receiver name / note | We |
|---|---|---|---|
| 0 | SupportsAirPlayVideoV1 | Ft00Video | ignore (video) |
| 1 | SupportsAirPlayPhoto | Ft01Photo | ignore |
| 2 | - | Ft02VideoFairPlay | ignore |
| 3 | - | Ft03VideoVolumeCtrl | ignore |
| 4 | - | Ft04VideoHTTPLiveStreaming | ignore |
| 5 | SupportsAirPlaySlideShow | Ft05Slideshow | ignore |
| 7 | SupportsAirPlayScreen | Ft07ScreenMirroring ("seems to need NTP") | ignore |
| 8 | - | Ft08ScreenRotate | ignore |
| 9 | SupportsAirPlayAudio | Ft09AirPlayAudio ("necessary for iPhones/Music: audio") | **required**; owntone skips devices without it [owntone:airplay.c:4047-4051] |
| 11 | AudioRedundant | Ft11AudioRedundant | informational |
| 12 | - | Ft12FPSAPv2p5_AES_GCM | ignore |
| 13 | - | Ft13MFiHardware | ignore |
| 14 | Authentication_4 | Ft14MFiSoft_FairPlay ("Music on iPhones needs this") | FairPlay key exchange via `/fp-setup`; we do not use it (3.10) |
| 15 | MetadataFeatures_0 | Ft15AudioMetaCovers | artwork via SET_PARAMETER is wanted (5.3) |
| 16 | MetadataFeatures_1 | Ft16AudioMetaProgress | progress via SET_PARAMETER is wanted (5.4) |
| 17 | MetadataFeatures_2 | Ft17AudioMetaTxtDAAP | DMAP text metadata is wanted (5.2) |
| 18 | AudioFormats_0 | Ft18ReceiveAudioPCM ("macOS needs 18 to pair") | PCM accepted |
| 19 | AudioFormats_1 | Ft19ReceiveAudioALAC | ALAC accepted (our codec) |
| 20 | AudioFormats_2 | Ft20ReceiveAudioAAC_LC | AAC-LC accepted |
| 21 | AudioFormats_3 | Ft21Unknown | (AAC-ELD per owntone's audioFormat table, 4.4) |
| 22 | - | Ft22AudioUnencrypted | receiver accepts unencrypted RTP; we always encrypt with `shk` anyway |
| 23 | Authentication_1 | Ft23RSA_Auth | RSA (AirPlay 1); ignore |
| 25 | - | Ft25iTunes4WEncryption | ignore |
| 26 | Authentication_8 | Ft26Audio_AES_Mfi | MFi via `/auth-setup`; owntone sets `supports_auth_setup` from it but has the step compiled out [owntone:airplay.c:73,4064-4065]. Not needed with HAP (3.10) |
| 27 | SupportsLegacyPairing | Ft27LegacyPairing | AirPlay 1 pairing; ignore for AP2 |
| 29 | - | Ft29plistMetaData | ignore |
| 30 | HasUnifiedAdvertiserInfo | Ft30UnifiedAdvertisingInfo | informational |
| 32 | IsCarPlay (pyatv: "SupportsVolume?") | Ft32CarPlay | ignore |
| 33 | SupportsAirPlayVideoPlayQueue | | ignore |
| 34 | SupportsAirPlayFromCloud | | ignore |
| 35 | SupportsTLS_PSK | | ignore |
| 37 | - | Ft37CarPlayControl | ignore |
| 38 | SupportsUnifiedMediaControl | Ft38ControlChannelEncrypt ("implicit with other flags") | one of the AirPlay-2 markers (1.6) |
| 40 | SupportsBufferedAudio | Ft40BufferedAudio ("absence requires ANNOUNCE") | stream type 103 available |
| 41 | SupportsPTP | Ft41_PTPClock | receiver has a PTP clock; owntone then uses PTP [owntone:airplay.c:4066-4067]; see 4.2 |
| 42 | SupportsScreenMultiCodec | | ignore |
| 43 | SupportsSystemPairing | Ft43SystemPairing | pyatv: transient credentials possible [pyatv:utils.py / auth/__init__.py:125-132] |
| 44 | IsAPValeriaScreenSender | | ignore |
| 45 | - | Ft45_NTPClock ("macOS won't connect, iOS will, but dies on play; 45 triggers stream type 96 without 41") | receiver prefers NTP realtime. Neither LAN receiver sets it |
| 46 | SupportsHKPairingAndAccessControl | Ft46HomeKitPairing | regular HomeKit pair-setup possible |
| 47 | - | Ft47PeerManagement ("for PTP") | SETPEERS supported |
| 48 | SupportsCoreUtilsPairingAndEncryption | Ft48TransientPairing | **transient pairing supported**; owntone requires it for AP2 at all [owntone:airplay.c:4052-4056]; pyatv: AirPlay 2 marker and transient credentials [pyatv:utils.py:489-504; auth/__init__.py:125-132] |
| 49 | SupportsAirPlayVideoV2 | | ignore |
| 50 | MetadataFeatures_3 | Ft50NowPlayingInfo | metadata via `POST /command` bplists instead of DMAP; shairport: when set, *no* DMAP/progress/artwork arrives the classic way [sps:shairport.c:1639-1648]. Neither LAN receiver sets it |
| 51 | SupportsUnifiedPairSetupandMFi | Ft51MfiPairSetup ("iOS fails at pair-setup [2/5]"; MFi) | if set, the receiver may expect `Method = PairSetupWithAuth (0x01)`; airplay2-receiver treats M1 with method 1 + transient flag as "MFi setup, not possible" [ap2r:ap2/pairing/hap.py:521-530]. LG sets it; see open question 7.6 |
| 52 | SupportsSetPeersExtendedMessage | Ft52PeersExtendedMessage | SETPEERSX allowed (4.6) |
| 54 | SupportsAPSync | | ignore |
| 55, 56 | SupportsWoL, SupportsWoL2 | | wake-on-LAN; **never** rely on it for the LG TV |
| 58 | SupportsHangdogRemoteControl | | ignore |
| 59 | SupportsAudioStreamConnectionSetup | Ft59AudioStreamConnectionSetup | `streamConnections` dict in stream SETUP (4.4, not used) |
| 60 | SupportsAudioMetadataControl | Ft60AudioMediaDataControl | ignore |
| 61 | SupportsRFC2198Redundancy | Ft61RFC2198Redundant | ignore |

**Disagreement**: pyatv's docstring warns that the two public bit lists "seem to have some
inconsistencies" [pyatv:utils.py:298-302]. The only bit where the three sources assign materially different
meanings is 32 (`IsCarPlay` vs `SupportsVolume`); owntone lists both names [owntone:airplay.c:413-414]. We do not
use bit 32.

Reference values: shairport-sync's default is `0x00018340405C4A00` plus bits 15/16/17 or 50 depending on
metadata configuration [sps:shairport.c:1628-1660]; airplay2-receiver's default is
`0x1C340405F8A00` = bits 9,11,15,16,17,18,19,20,22,30,38,40,41,46,47,48 [ap2r:ap2/bitflags.py:152-164].

### 1.4 `flags` / `statusFlags` / `sf`

Same bitmask under three names (TXT `flags=`, `/info` `statusFlags`, RAOP TXT `sf=`) [probe;
sps:rtsp.c:1036,1126; sps:bonjour_strings.c:98,165]. Names from airplay2-receiver `StatusFlags`
[ap2r:ap2/bitflags.py:177-204] and owntone's `AIRPLAY_FLAG_*` [owntone:airplay.c:181-195]; pyatv only
knows bits 3, 7 and 9 [pyatv:pyatv/protocols/airplay/utils.py:272-275].

| Bit | Value | airplay2-receiver name | owntone name | Effect on the sender |
|---|---|---|---|---|
| 0 | 0x1 | ProblemsExist | PROBLEM_DETECTED | show as warning |
| 1 | 0x2 | Not_yet_configured | NOT_CONFIGURED | receiver in setup mode |
| 2 | 0x4 | AudioLink ("audio cable attached, all is well") | AUDIO_CABLE_ATTACHED | normal; shairport always sets it [sps:shairport.c:1686] |
| 3 | 0x8 | PINmode | PIN_REQUIRED | pyatv: pairing mandatory [pyatv:utils.py:273,399-400]; owntone: PIN start, force new pairing [owntone:airplay.c:3324-3332] |
| 4 | 0x10 | PINentry | | |
| 5 | 0x20 | PINmatch | | |
| 6 | 0x40 | SupportsAirPlayFromCloud | SUPPORTS_FROM_CLOUD | ignore |
| 7 | 0x80 | PasswordNeeded | PASSWORD_REQUIRED | pyatv: password required [pyatv:utils.py:274,381-382]; owntone: the password is used as the SRP PIN in a regular pair-setup [owntone:airplay.c:3334-3351, 2901-2905]; shairport sets it when configured with a password [sps:shairport.c:1687-1689] |
| 8 | 0x100 | StatusUnknown_08 | | |
| 9 | 0x200 | PairingPIN_aka_OTP ("need PIN to pair; client will request PIN based auth") | ONE_TIME_PAIRING_REQUIRED | pyatv: pairing mandatory (`LEGACY_PAIRING_BIT`) [pyatv:utils.py:275,399]; owntone: regular HomeKit pairing with a PIN shown by the receiver, re-used via pair-verify afterwards [owntone:airplay.c:3310-3323] |
| 10 | 0x400 | Enable_HK_Access_Control ("prevents adding to HomeKit when set") | SETUP_HK_ACCESS_CTRL | receiver was configured for HomeKit access control; the `acl` TXT key says who may connect. shairport sets it after `POST /configure` with `Enable_HK_Access_Control` [sps:rtsp.c:2014-2018] |
| 11 | 0x800 | RemoteControlRelay | SUPPORTS_RELAY | ignore |
| 12 | 0x1000 | SilentPrimary | SILENT_PRIMARY | ignore |
| 13 | 0x2000 | TightSyncIsGroupLeader | TIGHT_SYNC_IS_GRP_LEADER | ignore |
| 14 | 0x4000 | TightSyncBuddyNotReachable | | ignore |
| 15 | 0x8000 | IsAppleMusicSubscriber | | ignore |
| 16 | 0x10000 | iCloudLibraryIsOn | | ignore |
| 17 | 0x20000 | ReceiverSessionIsActive | RECEIVER_IS_BUSY | someone is streaming; airplay2-receiver sets it on stream SETUP and clears it on TEARDOWN [ap2r:ap2-receiver.py:564,631,794]. AirPlay 2 "always allows interruption" [sps:rtsp.c:2484-2486], so we may still connect |
| 19 | 0x80000 | | | shairport comment: "seems to control whether individual volume controls are shown" [sps:shairport.c:1692-1693] |

### 1.5 `_raop._tcp` TXT keys

From the probe and pyatv's table [pyatv:docs/documentation/protocols.md:1192-1214]; parsing in
[pyatv:pyatv/protocols/raop/parsers.py:38-96].

| Key | SpeakerA | Meaning |
|---|---|---|
| `et` | `0,4` | Encryption types: 0 = none, 1 = RSA (AirPort Express), 3 = FairPlay, 4 = MFi-SAP, 5 = FairPlay SAPv2.5 [parsers.py:49-72]. pyatv sends `/auth-setup` only when 4 is present *and* `am` starts with `AirPort` [pyatv:pyatv/protocols/raop/stream_client.py:354-363]. |
| `cn` | `0,1` | Codecs: 0 = PCM, 1 = ALAC, 2 = AAC, 3 = AAC-ELD, 4 = OPUS. ALAC must be present for our stream. |
| `md` | `0,1,2` | Metadata types: 0 = text, 1 = artwork, 2 = progress [parsers.py:75-96]. pyatv gates SET_PARAMETER metadata on these; owntone gates on features bits 15/16/17 instead [owntone:airplay.c:4058-4063]. Use the union. |
| `tp` | `UDP` | Transports for media; `UDP` is what stream type 96 needs. |
| `ft` | `0x445F8A00,0x1C340` | Same as `features`. |
| `sf` | `0x404` | Same as `flags`. |
| `vs` | `366.0` | Same as `srcvers`. |
| `vn` | `65537` | Version number 1.1 as uint16.uint16. |
| `am` | `Test Speaker ABCD` | Apple model name (same as `model`). |
| `da` | `true` | Digest authentication supported (AirPlay 1 password). |
| `fv` | `p20.1.1.36.0x8b4c480` | Firmware version. |
| `pk` | same as `_airplay` | Ed25519 LTPK. |
| `ss`, `sr`, `ch` | absent on SpeakerA; shairport sends `ss=16`, `sr=44100`, `ch=2` [sps:bonjour_strings.c:133-140] | Sample size, rate, channels; defaults 16/44100/2 when absent [parsers.py:8-10,38-46]. |
| `txtvers`, `sv`, `sm`, `pw`, `ov` | absent | TXT version, software volume, software mute, password, OS version. |

### 1.6 Deciding how to talk to a receiver

Parse `features` into a 64-bit integer `F` and `flags` into `S`. Then:

1. **AirPlay 2 or not.** pyatv: AP2 if bit 38 or bit 48 is set [pyatv:utils.py:489-504]. owntone: AP2 only if
   bit 9 *and* bit 48 are set, otherwise it falls back to RAOP [owntone:airplay.c:4047-4056]. We require bit 9
   and bit 48 (both LAN receivers have both). Everything else in this document assumes AP2.
2. **Pairing style.**
   - If `S & 0x200` (bit 9) or `S & 0x8` (bit 3): a PIN shown by the receiver is required; do the regular
     HomeKit pair-setup (3.6) once, then pair-verify (3.7) on every connection. pyatv: `PairingRequirement.Mandatory`
     [pyatv:utils.py:399-400]; owntone: `PIN_START` sequence [owntone:airplay.c:3310-3333].
   - Else if `S & 0x80` (bit 7): a password is required; owntone runs the regular pair-setup with the password
     as the SRP password instead of a displayed PIN [owntone:airplay.c:3334-3351, 2901-2905] **(single source)**.
   - Else if `F` bit 48 (or 43): transient pair-setup (3.5) on every connection, no persistence. pyatv
     returns `TRANSIENT_CREDENTIALS` for bit 43 or 48 [pyatv:auth/__init__.py:125-132].
   - If the transient attempt is answered with `470 Connection Authorization Required`, fall back to the PIN flow
     (owntone saw this on an Apple TV 4) [owntone:airplay.c:3435-3445, 3696-3697].
   - `acl=1` means only HomeKit home members may connect: treat as "pairing disabled / show explanation in UI"
     [pyatv:utils.py:514-517]. `act=2` is unsupported [pyatv:utils.py:402-405]. `S & 0x400` alone (as on both LAN
     receivers, with `acl=0`) does not block transient pairing according to the receivers that set it
     themselves [sps:rtsp.c:2014-2018], but see open question 7.2.
3. **Stream support.** Bit 40 → type 103 possible; bit 41 → receiver has a PTP clock; bit 45 → receiver prefers
   NTP realtime (rare). Type 96 (realtime) is implied by bit 9 + `tp=UDP`. Codec: `cn` must contain `1` and
   bit 19 must be set for ALAC.
4. **Metadata.** Text if bit 17 or `md` has 0; artwork if bit 15 or `md` has 1; progress if bit 16 or `md` has 2.
   If bit 50 is set, the receiver wants `POST /command` bplist metadata and may ignore DMAP
   [sps:shairport.c:1639-1648]; not implemented here (open question 7.9).

### 1.7 The two LAN receivers, decoded

Test Speaker ABCD [probe]: `features=0x445F8A00,0x1C340` → `F = 0x0001C340445F8A00`, bits
9, 11, 15, 16, 17, 18, 19, 20, 22, 26, 30, 38, 40, 41, 46, 47, 48. `flags=0x404` → bits 2 and 10.
`srcvers=366.0`, `acl=0`, `rsf=0x0`, `gid=pi=deviceid=02:1A:2B:3C:4D:5E`, `gcgl=0`. RAOP: `et=0,4`, `cn=0,1`,
`md=0,1,2`, `tp=UDP`, `vs=366.0`, `sf=0x404`. Conclusions: AirPlay 2; transient pairing supported (48) and
regular HomeKit pairing supported (46); no PIN or password required (bits 3, 7, 9 clear); audio (9) with PCM,
ALAC and AAC-LC (18-20); both stream types (40 realtime-implied + buffered, PTP clock 41, peer management 47);
all three classic metadata kinds (15-17, `md=0,1,2`); MFi auth bit 26 set but with HAP it is not needed
(3.10); no bit 50, so DMAP metadata is the only metadata channel; `srcvers 366.0` means an iOS sender would
pick PTP + buffered, but the receiver's behaviour with an NTP session SETUP is untested (7.1). Same feature
set as the AirPort Express value shairport-sync quotes (`0x0001C340445D0A00`) plus bits 15 and 17
[sps:shairport.c:1634].

LG TV TV "Living room TV" [probe; do not connect to it]: `features=0x7F8AD0,0x38BCF46` →
`F = 0x038BCF46007F8AD0`, bits 4, 6, 7, 9, 11, 15, 16, 17, 18, 19, 20, 21, 22, 33, 34, 38, 40, 41, 42, 43, 46, 47,
48, 49, 51, 55, 56, 57. `flags=0x644` → bits 2, 6, 9, 10. `srcvers=377.40.00`, `rsf=0x3`, `fex=0Ip/AEbPiwNACA`,
`at=0x1`, `psi` and `gid` = `00000000-0000-0000-0000-024C47000001`. Conclusions: AirPlay 2 with video and
mirroring; **flag bit 9 set → a PIN shown on the TV is required**, i.e. regular HomeKit pair-setup with a
displayed PIN, then persistent credentials; transient pairing (48) and system pairing (43) are advertised but
the PIN flag wins; bit 51 (unified pair-setup and MFi) is set, so the M1 method byte question (7.6) matters for
this device; wake-on-LAN bits 55/56 explain why AirPlay traffic wakes the TV; both stream types and PTP. No
`_raop._tcp` record was seen for the TV in the 5 s browse window [probe].

### 1.8 Live probe: unauthenticated `GET /info` against the SpeakerA

Three requests were sent on fresh TCP connections to 192.0.2.26:7000, each with the headers pyatv's
`RtspSession.exchange` adds (`CSeq`, `User-Agent: AirPlay/550.10`, `DACP-ID`, `Active-Remote`, `Client-Instance`)
[pyatv:pyatv/support/rtsp.py:268-273, 101-110]. Results [probe, `docs/airplay2/probes/get_info_SpeakerA.txt`]:

| Variant | Request line / body | Response |
|---|---|---|
| A (pyatv `RtspSession.info()`) | `GET /info RTSP/1.0`, no body | `RTSP/1.0 200 OK`, `Content-Type: application/x-apple-binary-plist`, `Server: AirTunes/366.0`, `CSeq: 0`, 525-byte bplist |
| B | `GET /info HTTP/1.1`, no body | `HTTP/1.1 200 OK`, identical headers and body |
| C (iOS stage 1) | `GET /info RTSP/1.0` with `Content-Type: application/x-apple-binary-plist` and body `{"qualifier": ["txtAirPlay"]}` | `RTSP/1.0 200 OK`, 909-byte bplist = same dict plus `txtAirPlay` |

The plist (variant A/B), keys in the order the receiver emitted them:

```
sdk                      "AirPlay;2.0.10"
pi                       "02:1A:2B:3C:4D:5E"
firmwareRevision         "1.1.36.0x8b4c480"
OSInfo                   "AirPlay;2.0.10"
manufacturer             "Lautsprecher SpeakerA GmbH"
keepAliveLowPower        true
firmwareBuildDate        ""
model                    "Test Speaker ABCD"
nameIsFactoryDefault     false
hardwareRevision         "L2"
keepAliveSendStatsAsBody true
statusFlags              1028            (= 0x404, matches TXT flags)
deviceID                 "02:1A:2B:3C:4D:5E"
build                    "53.0"
PTPInfo                  "AirPlay;2.0.10"
protocolVersion          "1.1"
sourceVersion            "366.0"
features                 496155769145856 (= 0x1C340445F8A00, matches TXT features)
name                     "Test Speaker ABCD"
```

Variant C adds `txtAirPlay` as a 363-byte `data` value: a concatenation of Pascal strings (1 length byte +
text) that reproduces the `_airplay` TXT record exactly (`acl=0`, `deviceid=...`, `features=0x445F8A00,0x1C340`,
`rsf=0x0`, `fv=...`, `flags=0x404`, `model=...`, `manufacturer=...`, `serialNumber=...`, `protovers=1.1`,
`srcvers=366.0`, `pi=...`, `gid=...`, `gcgl=0`, `pk=...`). This is the same encoding shairport-sync builds with
`add_pstring_to_malloc` [sps:rtsp.c:962-993, 999-1104].

Things the SpeakerA does **not** return that other receivers do: `initialVolume` (shairport adds the
receiver's current volume in dB [sps:rtsp.c:1137]; airplay2-receiver only in a "second stage" dict
[ap2r:ap2-receiver.py:203-205]; pyatv reads it to seed its volume [pyatv:pyatv/protocols/raop/__init__.py:384-391]),
`supportedFormats` (shairport: `audioStream`/`bufferStream` format masks [sps:rtsp.c:1140-1165]), `pk` as a
data value (shairport [sps:rtsp.c:1131-1132]), `senderAddress`, `macAddress`, `vv`, `volumeControlType`,
`playbackCapabilities`, `receiverHDRCapability` (all in shairport's template plist
[sps:plists/get_info_response.xml:87-137]). The bplist keys `keepAliveLowPower` and `keepAliveSendStatsAsBody`
are also sent by airplay2-receiver [ap2r:ap2-receiver.py:176-177]; their effect on `POST /feedback` is unknown
(7.8).

Note on what the request did and did not need: no `X-Apple-*` header was required for `/info`, and the receiver
echoes the request's protocol token in the status line. shairport-sync distinguishes "stage 1" (body with
`qualifier`) from "stage 2" (no body) but returns the same plist for both [sps:rtsp.c:1170-1292]; airplay2-receiver
answers 404 for a body without `qualifier.txtAirPlay` [ap2r:ap2-receiver.py:1139-1179].

---

## 2. The RTSP-over-HTTP transport

### 2.1 Connection and framing

One TCP connection to the port from the `_airplay._tcp` SRV record (7000 on both LAN receivers) carries
every control message of a session: pairing POSTs, `GET /info`, RTSP methods, keep-alives. It must be a raw
socket because after pair-verify (or transient pair-setup) the same socket switches to the ChaCha20-Poly1305
framing of 2.5, which no HTTP client library will do [pyatv:pyatv/support/http.py:326-357 (send/receive
processors on the connection); sps:rtsp.c:549-579 (receiver switches `read` to `read_encrypted` once a cipher
context exists)].

Requests are HTTP/1.1-shaped text:

```
<METHOD> <uri> <protocol>\r\n
<Header>: <value>\r\n
...
\r\n
<body, Content-Length bytes>
```

pyatv's formatter writes the request line, then `User-Agent` (unless given), `Content-Type` (if any),
`Content-Length` (only when there is a body), then the caller's headers, then `\r\n\r\n` and the body
[pyatv:pyatv/support/http.py:50-80]. Responses are parsed by splitting at the first `\r\n\r\n`, reading
`Content-Length` from the headers (default 0) and waiting until that many body bytes have arrived; the status
line is `<protocol>/<version> <code> <message>` [pyatv:http.py:110-140,170-187]. Header names are matched
case-insensitively [http.py:120]. Bodies whose `Content-Type` does not start with `application` are decoded as
UTF-8 text when possible [http.py:128-133]; keep the raw bytes for everything else.

`<uri>`:
- RTSP methods (`SETUP`, `RECORD`, `FLUSH`, `FLUSHBUFFERED`, `SETRATEANCHORTIME`, `SETPEERS`, `SET_PARAMETER`,
  `GET_PARAMETER`, `TEARDOWN`, `OPTIONS`) use the session URI `rtsp://<local ip>/<session id>` where
  `<session id>` is a random 32-bit decimal chosen per connection [pyatv:pyatv/support/rtsp.py:87,91-94;
  owntone uses the same shape and `rtsp://[v6]/...` for IPv6, owntone:airplay.c:1534-1537]. OPTIONS may use `*`
  [pyatv:docs/documentation/protocols.md:1255; owntone:airplay.c:909].
- "HTTP-style" endpoints use a path: `GET /info`, `POST /pair-setup`, `POST /pair-verify`,
  `POST /pair-pin-start`, `POST /feedback`, `POST /command`, `POST /auth-setup`, `POST /fp-setup`.

`<protocol>`: pyatv sends `RTSP/1.0` for everything that goes through `RtspSession.exchange` (including
`GET /info` and `POST /feedback`) [pyatv:rtsp.py:254-263] but `HTTP/1.1` for the pairing POSTs that go through
`HttpConnection.post` directly [pyatv:pyatv/support/http.py:436-447; pyatv:pyatv/protocols/airplay/auth/hap.py:52-56]
and for `/auth-setup` [pyatv:rtsp.py:112-123]. owntone's evrtsp sends `RTSP/1.0` for everything
[owntone:airplay_events.c:43 (RTSP_VERSION), airplay.c sequence table 3640-3703]. Receivers: the SpeakerA answered
`GET /info` with either token and echoed it back [probe]; airplay2-receiver rewrites `RTSP/1.0` to `HTTP/1.1`
before parsing and answers with `RTSP/1.0` regardless [ap2r:ap2-receiver.py:370-376, 389]. **Decision: send
`RTSP/1.0` on every request** (owntone-proven against Sonos, HomePod, Apple TV per its comments) and accept
either token in responses.

Pipelining: pyatv matches responses to requests by `CSeq` with a fallback to FIFO order [pyatv:rtsp.py:291-330;
http.py:385-404]. Receivers answer in order. The sender should still send one request at a time per connection
and treat a response whose `CSeq` does not match as belonging to the oldest outstanding request.

Timeouts pyatv uses: 4 s for an RTSP exchange [pyatv:rtsp.py:316], 10 s for a plain HTTP exchange (pairing)
[pyatv:http.py:446], 25 s for a sleeping receiver to wake [pyatv:http.py:36-39]. Also see 4.10.

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** over Wi-Fi the
SpeakerA answered transient pair-setup M2 after 0.5-3.4 s, M4 after 0.3-7.9 s, and RECORD once after 9.9 s; one M2 did not
come within 10 s (a new connection 3 s after the previous session's TEARDOWN; `ping` showed up to 740 ms). The 10 s
request timeout of D6 is too tight for this receiver: the session probes use 30 s
(`SessionProbeOptions.Connection`), and the facade should do the same (`RtspConnectionOptions.RequestTimeout`, owned by
`rtsp-transport`). Logs: `artifacts/live/2026-10-09-P5-teardown.log`, `-P10-initial-volume.log`.

### 2.2 Headers

| Header | Value | Who sends it | Notes |
|---|---|---|---|
| `CSeq` | decimal counter starting at 0, +1 per request | every request [pyatv:rtsp.py:265-269; owntone:airplay.c:896-899] | Receivers echo it [sps:rtsp.c:4210-4212; ap2r:ap2-receiver.py:403]. pyatv's pairing POSTs (via `HttpConnection.post`) do **not** carry `CSeq` [pyatv:hap.py:19-24]; the SpeakerA still answered a bodyless `GET /info` that had one. Send it always. |
| `User-Agent` | `AirPlay/550.10` (pyatv RTSP) [pyatv:rtsp.py:22], `AirPlay/320.20` (pyatv pairing) [pyatv:hap.py:20], configurable in owntone [owntone:airplay.c:901] | every request | shairport logs the version after `AirPlay/` [sps:rtsp.c:1175-1183]. Use one value, e.g. `AirPlay/550.10`. |
| `DACP-ID` | upper-case hex of a random 64-bit value | every RTSP request [pyatv:rtsp.py:88,270] | pyatv formats it `f"{randrange(2**64):X}"`, i.e. **unpadded** (1-16 digits) [pyatv:rtsp.py:88]; owntone uses 16 upper-case hex digits of its device id [owntone:airplay.c:2907-2909]. No source shows a receiver requiring a fixed width; we zero-pad to 16 digits, which both receivers accept (shairport stores the string verbatim [sps:rtsp.c:1207-1224, 2845-2862]). Identifies the sender's DACP remote-control server (AirPlay 1 heritage). |
| `Active-Remote` | random 32-bit decimal | every RTSP request [pyatv:rtsp.py:89,271] | owntone notes a device that cannot handle more than 32 bits [owntone:airplay.c:922-925]. shairport stores it [sps:rtsp.c:1187-1205]. |
| `Client-Instance` | same value as `DACP-ID` | every RTSP request [pyatv:rtsp.py:272; owntone:airplay.c:918-920] | |
| `Content-Type` | see 2.3 | with a body | |
| `Content-Length` | byte length | with a body | pyatv omits it for empty bodies [http.py:69-70]; receivers always send it, even `0` [sps:rtsp.c:845-857; probe]. |
| `X-Apple-HKP` | `3` (regular HomeKit pairing) or `4` (transient) | the pairing POSTs (`/pair-pin-start`, `/pair-setup`, `/pair-verify`) [pyatv:hap.py:22, hap_transient.py:26; owntone:airplay.c:2835-2838, 2886-2890 ("Required!!")] | Values seen by airplay2-receiver: 0 unauthenticated, 2, 3 system pairing (bit 43), 4 transient, 6 HomeKit, 7 HomeKit administration [ap2r:ap2-receiver.py:75-84]. shairport ignores it. |
| `X-Apple-Client-Name` | sender's display name | owntone on every request [owntone:airplay.c:902] | shairport reads it in the pairing handlers for logging only [sps:rtsp.c:1686-1691, 1753-1757, 1775-1779]. Optional. |
| `X-Apple-ProtocolVersion` | `1` | pyatv on `/play` only [pyatv:pyatv/protocols/raop/protocols/airplayv2.py:30] | Not needed for audio. |
| `X-Apple-Session-ID` | lower-case UUID | pyatv on `/play` only [airplayv2.py:31] | Video path; not sent on the audio RTSP session by any reference sender. |
| `X-Apple-Stream-ID` / `X-Apple-StreamID` | `1` | pyatv `/play` [airplayv2.py:32]; owntone compiled out [owntone:airplay.c:71,927-929] | Not needed. |
| `Session` | RTSP session id from an AirPlay 1 SETUP response | pyatv on FLUSH / SET_PARAMETER metadata / TEARDOWN [pyatv:rtsp.py:221,240,252; stream_client.py:441-448] | For AirPlay 2 pyatv's value is `0` (never set) and owntone sends none; shairport-sync ignores it. Omit. |
| `RTP-Info` | `seq=<n>;rtptime=<t>` or `rtptime=<t>` | FLUSH, SET_PARAMETER metadata/artwork/progress (section 4.8, 5) | |
| `Range` | `npt=0-` | pyatv FLUSH [stream_client.py:442]; RECORD in AirPlay 1 [protocols.md:1374] | Harmless; shairport ignores. |
| `Authorization` | Digest | only when a 401 with `WWW-Authenticate` was received (AirPlay 1 password) [pyatv:rtsp.py:65-73,275-279; owntone:airplay.c:904-916] | Not expected with HAP; see 2.4. |

Receiver response headers to expect: `Server: AirTunes/<srcvers>` [sps:rtsp.c:4214-4217; ap2r:ap2-receiver.py:392-393;
probe], `CSeq` echo, `Content-Length`, `Content-Type`, `Audio-Latency` on RECORD (4.5), `Connection: close` on
a session-ending TEARDOWN (4.9), `Public` on OPTIONS, `Date` on Apple devices [protocols.md:1265-1270].

### 2.3 Bodies

| Content-Type | Used by | Encoding |
|---|---|---|
| `application/x-apple-binary-plist` | SETUP (session and stream), FLUSHBUFFERED, SETRATEANCHORTIME, SETPEERS/SETPEERSX, TEARDOWN, `GET /info` stage 1, `/feedback` response, `/command`, event-channel `POST /command` | bplist00. pyatv: `plistlib.dumps(..., fmt=FMT_BINARY)` [pyatv:rtsp.py:284-289]. Values used: dict, array, string, bool, integer (uint and signed int64, e.g. `ClockID` and `seed`), real (`sr`, `initialVolume`), data (`shk`, `txtAirPlay`, `pk`). A .NET implementation must write and read the binary form; the XML form is not accepted (receivers call `plist_from_memory` on the raw bytes and look for `bplist00`, owntone:airplay_events.c:238-251). |
| `application/octet-stream` | pair-setup, pair-verify, pair-add/list/remove, `/auth-setup`, `/fp-setup` bodies and responses | TLV8 (3.2) or raw bytes. airplay2-receiver also names `application/pairing+tlv8` [ap2r:ap2-receiver.py:74] but all senders use octet-stream [pyatv:hap.py:23; owntone:airplay.c:3686-3698]. |
| `text/parameters` | SET_PARAMETER volume/progress, GET_PARAMETER | lines `name: value\r\n` (5.1, 5.4) |
| `application/x-dmap-tagged` | SET_PARAMETER metadata | DMAP (5.2) |
| `image/jpeg`, `image/png` | SET_PARAMETER artwork | raw image (5.3) |
| `/peer-list-changed`, `/peer-list-changed-x` | SETPEERS, SETPEERSX | bplist array (4.6); the "content type" is really a sub-path label [owntone:airplay.c:3653; ap2r:ap2-receiver.py:800,824] |
| `application/sdp` | ANNOUNCE | AirPlay 1 only; not used |

### 2.4 Status codes

| Code | Meaning in practice | Source |
|---|---|---|
| 200 | OK | |
| 400 | bad request (e.g. `/info` stage 1 without `qualifier`) | [sps:rtsp.c:1271-1273 (user_fail)] |
| 401 | password required: `WWW-Authenticate: Digest realm="..." nonce="..."` → retry with `Authorization` (AirPlay 1 flow) | [pyatv:rtsp.py:155-168; http.py:485-489; owntone:airplay.c:3187-3202] |
| 403 | not authenticated / forbidden; pyatv raises `AuthenticationError` | [pyatv:http.py:482-483] |
| 404 | endpoint unknown | [ap2r:ap2-receiver.py:363-368] |
| 451 | shairport: "Unavailable"/invalid arguments (RECORD or FLUSH without a player, SETUP with wrong state, pair-add failures) | [sps:rtsp.c:807-816, 956-957, 2359-2361, 3073] |
| 453 | shairport: could not become the principal connection | [sps:rtsp.c:2681] |
| 470 | "Connection Authorization Required": pairing rejected. On transient M1 this means "use the PIN flow" | [sps:rtsp.c:813, 1721, 1806; owntone:airplay.c:3435-3445] |
| 500 | internal error; pyatv retries `/play` on 500 | [sps:rtsp.c:1712, 1797; pyatv:player.py:377-385] |
| 501 | not implemented (unknown method; shairport's default before a handler runs, and its answer to an NTP session SETUP) | [sps:rtsp.c:4198, 2396, 2683-2686] |

pyatv treats any 2xx as success, 401/403 as authentication errors and everything else as `HttpError`
[pyatv:http.py:482-499].

### 2.5 The encrypted control channel

After pair-verify M4 (regular pairing) or after transient pair-setup M4 (3.5) every byte on the socket is
wrapped. The receiver's M4 response itself is still plaintext; encryption starts with the next request
[sps:rtsp.c:1811-1817 ("prepare encryption, but don't activate yet, the response to this request is still
plaintext"), 556-557 (activated on the next read)]. pyatv installs the processors right after `verify_credentials`
returns, before the next request [pyatv:pyatv/protocols/airplay/auth/__init__.py:100-117 (`verify_connection`)].

**Keys.** Two 32-byte keys from HKDF-SHA-512 (RFC 5869 extract + expand, output length 32) over the shared
secret `SS` (what `SS` is: 3.5 for transient, 3.7 for regular):

| Direction (sender view) | salt | info | Sources |
|---|---|---|---|
| sender → receiver ("write") | `Control-Salt` | `Control-Write-Encryption-Key` | [pyatv:auth/__init__.py:36-38 (constants), 106-115 (`output_key` = Write, `input_key` = Read, installed as send/receive processors); sps:pair_ap/pair_homekit.c:109-110, 2952-2955 (channel 0 = client control); ap2r:ap2/pairing/hap.py:1362-1364 (receiver's *outgoing* is the Read key)] |
| receiver → sender ("read") | `Control-Salt` | `Control-Read-Encryption-Key` | same |

HKDF detail: `PRK = HMAC-SHA512(key = salt, msg = SS)`, `OKM = HMAC-SHA512(key = PRK, msg = info || 0x01)[0:32]`
[sps:pair_homekit.c:923-946 (gcrypt branch spells it out); pyatv:hap_srp.py:32-41]. Salt and info are the ASCII
strings without terminator.

**Framing.** Each frame: `len (2 bytes, little-endian, length of the plaintext chunk) || ciphertext (len bytes)
|| Poly1305 tag (16 bytes)` [pyatv:pyatv/auth/hap_session.py:31-66 (`decrypt` 31-51, `encrypt` 53-66);
sps:pair_homekit.c:3007-3039; ap2r:hap.py:1486-1506; pyatv:docs/documentation/protocols.md:1550-1556]. The AEAD is
ChaCha20-Poly1305 (IETF, 96-bit nonce) with:
- key: the direction's key;
- nonce: 12 bytes = `00 00 00 00 || counter (8 bytes, little-endian)`, counter starting at 0 and incremented
  per frame, independently for each direction [pyatv:pyatv/support/chacha20.py:76-106 (`Chacha20Cipher8byteNonce`:
  `Struct("<LQ").pack(0, counter)`), 57-59 and 69-70 (per-direction counters, +1 per call); sps:pair_homekit.c:3022,3077
  (counter memcpy'd into bytes 4..11 on a little-endian host, enforced at 1259-1263); ap2r:hap.py:1467,1496
  (`struct.pack("Q")` right-justified in 12)];
- AAD: the 2-byte little-endian length field [pyatv:hap_session.py:48 (decrypt), 63-64 (encrypt);
  sps:pair_homekit.c:3026 (`&block_len` as AAD); ap2r:hap.py:1471,1499].

**Frame size, two different rules for the two directions.**
- *Sending*: at most 1024 bytes (`0x400`) of plaintext per frame; larger writes are split into consecutive frames,
  each with its own counter value [pyatv:hap_session.py:17 (`FRAME_LENGTH = 1024`), 58-66; sps:pair_homekit.c:56,3010;
  ap2r:hap.py:1359,1493]. This cap is **not configurable** in our sender (`HapFrameCipher.MaxSendFramePlaintext`):
  every reference receiver is known to accept 1024-byte frames and nothing is known about larger ones (7.15).
- *Receiving*: accept any frame whose plaintext length fits the 16-bit field, i.e. up to 65 535 bytes. pyatv's
  protocol notes state that "HomeKit mandates a maximum block size of 1024 bytes. This is not the case here as any
  block size (that fits length tag) can be used" [protocols.md:1555-1556], and pyatv's own reader decodes whatever
  the length field announces [hap_session.py:39-51]. An Apple-SDK receiver may therefore send, e.g., its `updateInfo`
  plist on the event channel in one frame larger than 1024 bytes; a reader with a fixed 1024-byte buffer would turn
  that into a bogus authentication failure and drop the connection. The receive buffer is sized by the length field
  (`HapFrameCipher.MaxReceiveFramePlaintext = 65535`).

Reading: buffer bytes, decode frames whole (need `2 + len + 16` bytes), concatenate plaintext, then parse HTTP
messages from the plaintext stream; a frame boundary never has to align with a message boundary
[pyatv:hap_session.py:36-51; sps:rtsp.c:483-518]. A decryption failure is fatal for the connection (pair_ap rolls
back the counter and returns -1 [sps:pair_homekit.c:3080-3086]); close the socket and re-run pair-verify on a new one.

The same framing, with other key pairs, is used for the event channel (4.3) and the remote-control data
channel (salt `DataStream-Salt<seed>`, infos `DataStream-Output-Encryption-Key` / `DataStream-Input-Encryption-Key`
[pyatv:pyatv/protocols/airplay/ap2_session.py:35-37, 155-187; sps:pair_homekit.c:116-122]; not used by this project).

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** (probe P21) a `GET /info`
sent as **one 2048-byte frame** after transient pairing made the SpeakerA close the connection without an answer: the
SpeakerA does **not** accept frames above 1024 bytes from the sender, so the fixed 1024-byte send cap is required, not just
cautious. In normal sessions every frame it sent had at most 1024 bytes of plaintext (it splits like HAP); the receive
rule (up to 65 535) stays for other receivers. Log: `artifacts/live/2026-10-09-P21-large-frame.log`.

---

## 3. Pairing

### 3.1 Overview

Three HomeKit (HAP) procedures exist over `POST` to the control connection, all carrying TLV8 bodies with
`Content-Type: application/octet-stream` and an `X-Apple-HKP` header:

| Procedure | Endpoint(s) | When | Result |
|---|---|---|---|
| Transient pair-setup (3.5) | `POST /pair-pin-start` (empty), then `POST /pair-setup` twice (M1→M2, M3→M4) | every connection to a receiver with feature bit 48 (or 43) and no PIN/password flag | shared secret `SS` = 64-byte SRP session key; no persistence; channel encryption starts immediately |
| Regular pair-setup (3.6) | `POST /pair-pin-start`, then `POST /pair-setup` three times (M1→M2, M3→M4, M5→M6) | once per receiver that requires a PIN (flags bit 9/3) or password (bit 7), or after a 470 to transient | long-term keys to persist; **no** encryption yet; must be followed by pair-verify |
| Pair-verify (3.7) | `POST /pair-verify` twice (M1→M2, M3→M4) | every connection when persisted keys exist | `SS` = 32-byte X25519 shared secret; encryption starts |

HAP wording maps as: "iOS device" / "controller" = sender, "accessory" = receiver. The receiver's step
numbers (M2, M4, M6) are responses to our M1, M3, M5.

`POST /pair-pin-start`: empty body, headers `User-Agent`, `Connection: keep-alive`, `X-Apple-HKP: 3|4`,
`Content-Type: application/octet-stream` (pyatv sends the content type even without a body, and because its formatter
writes `Content-Length` only for a non-empty body the request has a `Content-Type` but no `Content-Length`
[pyatv:http.py:64-70]) [pyatv:hap.py:20-25,52; hap_transient.py:23-28,49]. Our codec rule (`RtspRequest`): a request
with a `ContentType` and an empty body is written with `Content-Type` **and** `Content-Length: 0`; a request with
neither has no such headers; `Content-Length` is never omitted when `Content-Type` is present (receivers always send
both themselves, 2.2). pyatv and owntone send pin-start before both the transient and the regular flow
[pyatv:hap.py:52; hap_transient.py:49; owntone:airplay.c:2831-2841 (only before PIN pairing, with a log line
telling the user to enter the PIN)]. The receiver answers `200 OK` with an empty body [sps:rtsp.c:1751-1771;
ap2r:ap2-receiver.py:987-994]; on a PIN-requiring receiver this is what makes it display the PIN
("This method will show the expected PIN on screen" [pyatv:hap.py:46-49]).

### 3.2 TLV8

A sequence of `type (1 byte) || length (1 byte) || value (length bytes)`. A value longer than 255 bytes is split
into consecutive items with the same type and lengths 255, 255, ..., remainder; a reader concatenates consecutive
items of the same type [pyatv:pyatv/auth/hap_tlv8.py:77-123; sps:pair_ap/pair-tlv.c:116-220; ap2r:ap2/pairing/hap.py:94-128].
The SRP public keys (384 bytes) are the values that need this. A zero-length value is written as `type 00`.

Types [pyatv:hap_tlv8.py:13-34; sps:pair_ap/pair-tlv.h:10-31; ap2r:hap.py:75-91]:

| Type | Name | Content |
|---|---|---|
| 0x00 | Method | 1 byte: 0 PairSetup, 1 PairSetupWithAuth, 2 PairVerify, 3 AddPairing, 4 RemovePairing, 5 ListPairings [pyatv:hap_tlv8.py:55-63] |
| 0x01 | Identifier | UTF-8 pairing identifier (ours or the receiver's) |
| 0x02 | Salt | 16 bytes SRP salt (client rejects other sizes [sps:pair_homekit.c:1465-1470]) |
| 0x03 | PublicKey | SRP public key (≤ 384 bytes), X25519 public key (32) or Ed25519 public key (32), depending on the message |
| 0x04 | Proof | SRP M1 / M2 (64 bytes, SHA-512) |
| 0x05 | EncryptedData | ChaCha20-Poly1305 ciphertext with the 16-byte tag appended |
| 0x06 | State (pyatv: SeqNo) | 1 byte: M1..M6 = 1..6 |
| 0x07 | Error | 1 byte: 1 Unknown, 2 Authentication, 3 BackOff, 4 MaxPeers, 5 MaxTries, 6 Unavailable, 7 Busy [pyatv:hap_tlv8.py:43-52] |
| 0x08 | BackOff / RetryDelay | seconds, little-endian |
| 0x09 | Certificate | unused |
| 0x0A | Signature | Ed25519 signature (64 bytes) |
| 0x0B | Permissions | unused by us |
| 0x0C, 0x0D | FragmentData, FragmentLast | unused (pair_ap numbers them 13/14, pyatv 12/13; neither is used) |
| 0x11 | Name | pyatv only: OPACK-encoded `{"name": <display name>}` added to M5 [pyatv:hap_srp.py:192-197] (Apple internal; optional, shairport ignores unknown types) |
| 0x13 | Flags | 1 byte (pyatv writes it big-endian single byte): 0x10 = transient pairing [pyatv:hap_tlv8.py:37-40; sps:pair_homekit.c:134-136]. airplay2-receiver also knows 0x01000000 "split" [ap2r:hap.py:55-58] |
| 0xFF | Separator | unused |

Items may appear in any order; readers index by type. A response containing `Error` must abort the procedure
with the mapped message [sps:pair_homekit.c:860-877].

### 3.3 SRP-6a parameters

Identical in all four implementations:

- Group: 3072-bit prime from RFC 5054 appendix A with generator `g = 5` [sps:pair_homekit.c:219-233;
  pyatv:hap_srp.py:140-146 (`constants.PRIME_3072`, `PRIME_3072_GEN`)].
- Hash `H` = SHA-512 [sps:pair_homekit.c:1265; pyatv:hap_srp.py:145].
- Username `I` = `"Pair-Setup"` [sps:pair_homekit.c:52; pyatv:hap_srp.py:141; ap2r:hap.py:909-911].
- Password `P` = the PIN as ASCII digits: `"3939"` for transient [pyatv:hap_transient.py:30,70 (`TRANSIENT_PIN = 3939`, `str(pin)` in hap_srp.py:142); sps:pair_homekit.c:1199-1200,
  2022-2023; ap2r:hap.py:911]; for regular pairing the 4-digit PIN shown by the receiver, zero-padded to 4 digits
  [pyatv:pyatv/protocols/airplay/pairing.py:570-573], or the configured password. HAP's `XXX-XX-XXX` 8-digit
  format with dashes [ap2r:hap.py:889-891] has not been seen on AirPlay receivers; treat the user's input
  verbatim.
- Client private `a`: 256 random bits [sps:pair_homekit.c:447]; `A = g^a mod N`, transmitted big-endian without
  leading-zero padding [sps:pair_homekit.c:455-468].
- `k = H(N || PAD(g))`, `u = H(PAD(A) || PAD(B))` where `PAD` left-pads to the 384-byte length of `N`
  [sps:pair_homekit.c:490,500 (`H_nn_pad`)].
- `x = H(s || H(I || ":" || P))` [sps:pair_homekit.c:279-292].
- `S = (B - k * g^x)^(a + u*x) mod N`; abort if `B mod N == 0` or `u == 0` [sps:pair_homekit.c:506-516].
- Session key `K = H(S)`, 64 bytes [sps:pair_homekit.c:518-519]. This `K` is the "SRP shared secret" fed to every
  HKDF in pair-setup and, for transient pairing, the channel-key `SS` [sps:pair_homekit.c:1524-1537; pair.h:65-66
  ("32 (normal) or 64 (transient)"); pyatv:hap_transient.py:91-99 (`encryption_keys`: `unhexlify(self.srp.shared_key)` fed to `hkdf_expand`)].
- Client proof `M1 = H( (H(N) xor H(g)) || H(I) || s || A || B || K )`, 64 bytes [sps:pair_homekit.c:294-324].
- Server proof `M2 = H(A || M1 || K)`; compare constant-time, abort on mismatch [sps:pair_homekit.c:326-338, 541-546;
  pyatv:hap_srp.py:157-158 ("proofs do not match")].

### 3.4 HKDF salts and infos used by pairing

All HKDF-SHA-512, 32-byte output, applied to the key material named in the row [sps:pair_homekit.c:90-123;
pyatv:hap_srp.py:90-92, 171-181; ap2r:hap.py:1046-1047, 1083-1084, 1136-1137, 1239-1240].

| Step | Input key | salt | info | Purpose |
|---|---|---|---|---|
| pair-setup M5/M6 encryption | SRP `K` | `Pair-Setup-Encrypt-Salt` | `Pair-Setup-Encrypt-Info` | ChaCha key for `EncryptedData` in M5 and M6 |
| pair-setup M5 signature | SRP `K` | `Pair-Setup-Controller-Sign-Salt` | `Pair-Setup-Controller-Sign-Info` | `iOSDeviceX` prefix of the signed blob |
| pair-setup M6 signature check | SRP `K` | `Pair-Setup-Accessory-Sign-Salt` | `Pair-Setup-Accessory-Sign-Info` | `AccessoryX` prefix of the receiver's signed blob |
| pair-verify M2/M3 encryption | X25519 shared secret | `Pair-Verify-Encrypt-Salt` | `Pair-Verify-Encrypt-Info` | ChaCha key for `EncryptedData` in M2 and M3 |
| control channel | `SS` | `Control-Salt` | `Control-Write-Encryption-Key` / `Control-Read-Encryption-Key` | 2.5 |
| event channel | `SS` | `Events-Salt` | `Events-Write-Encryption-Key` / `Events-Read-Encryption-Key` | 4.3 |
| data channel | `SS` | `DataStream-Salt<seed decimal>` | `DataStream-Output-Encryption-Key` / `DataStream-Input-Encryption-Key` | not used |

Nonces for the pairing AEAD operations are 12 bytes: `00 00 00 00 || "PS-Msg05"`, `"PS-Msg06"`, `"PV-Msg02"`,
`"PV-Msg03"` (the 8 ASCII bytes right-aligned) with **no AAD** [sps:pair_homekit.c:97-98,104-105,1406,1602,1804,1902;
pyatv:hap_srp.py:95,124,203,210 (`Chacha20Cipher8byteNonce` pads 8-byte nonces with 4 leading zero bytes,
chacha20.py:49-51 (`_pad_nonce`), 60-62 and 71-73 (explicit nonce path), 76-106); ap2r:hap.py:1048,1171,1242,1302
(PyCryptodome with an 8-byte nonce, equivalent)].

### 3.5 Transient pair-setup (M1-M4)

Preconditions: feature bit 48 (or 43), no PIN/password flag. Headers on every POST: `X-Apple-HKP: 4`,
`Content-Type: application/octet-stream`, `User-Agent`, `Connection: keep-alive` [pyatv:hap_transient.py:23-28].

1. `POST /pair-pin-start` (empty) → 200.
2. **M1** `POST /pair-setup`, body TLV8:
   `00 01 00` (Method = PairSetup) `06 01 01` (State = M1) `13 01 10` (Flags = transient)
   [pyatv:hap_transient.py:51-60; sps:pair_homekit.c:1272-1280]. pyatv puts Method first, then State, then Flags;
   pair_ap puts State first. Order is irrelevant.
3. **M2** response TLV8: `State = 02`, `Salt` (16 bytes), `PublicKey` (= SRP `B`, up to 384 bytes, split into
   255-byte items) [sps:pair_homekit.c:1444-1486; ap2r:hap.py:907-919]. An `Error` item instead means the receiver
   refuses transient pairing; HTTP 470 means the same (go to 3.6).
4. Compute `A`, `K`, `M1` as in 3.3 with `P = "3939"`.
5. **M3** `POST /pair-setup`, body: `State = 03`, `PublicKey = A`, `Proof = M1` [pyatv:hap_transient.py:73-80;
   sps:pair_homekit.c:1314-1322].
6. **M4** response: `State = 04`, `Proof = M2`. Verify `M2` [sps:pair_homekit.c:1503-1520]. pyatv verifies inside
   `step2` before sending M3 (srptools computes the expected server proof) [pyatv:hap_srp.py:157-158] and never
   looks at the M4 body.
7. `SS = K` (64 bytes). Derive the control keys (2.5) and switch the socket to encrypted mode **after** the M4
   response has been fully read [sps:rtsp.c:1810-1833].

The transient secret is not persisted; a new connection repeats M1-M4. The receiver's identity is not
verified (anyone can answer SRP with PIN 3939); this is by design of the transient mode.

### 3.6 Regular (HomeKit) pair-setup (M1-M6)

Preconditions: flags bit 9 or 3 (PIN), bit 7 (password), or a 470 to transient M1. Headers: `X-Apple-HKP: 3`
[pyatv:hap.py:20-25; owntone:airplay.c:2835-2836].

Sender state to prepare once (and keep): an Ed25519 long-term key pair (`LTSK`, `LTPK`; pyatv generates a fresh
32-byte seed per pairing [pyatv:hap_srp.py:66-76]; pair_ap generates a libsodium key pair [sps:pair_homekit.c:1217])
and a pairing identifier `ourId` (pyatv: a UUID4 string [pyatv:hap_srp.py:50]; owntone: 16 upper-case hex digits of
its 64-bit device id [owntone:airplay.c:2907-2909]). Both are opaque to the receiver; the identifier must stay the
same for pair-verify later. Use one `ourId` and one key pair for the whole installation (one "controller").

1. `POST /pair-pin-start` → the receiver shows its PIN. Ask the user for it through the web app. The receiver keeps
   the SRP session open across the user's typing delay; pyatv splits the flow into `start_pairing` (steps 1-3) and
   `finish_pairing` (steps 4-7) [pyatv:hap.py:45-61, 63-94].
2. **M1** `POST /pair-setup`: `Method = 00`, `State = 01` (no Flags) [pyatv:hap.py:54-57; sps:pair_homekit.c:1272-1276].
   See 7.6 for receivers with feature bit 51 that may want `Method = 01`.
3. **M2**: `State = 02`, `Salt`, `PublicKey = B` (as in 3.5).
4. With the user's PIN as `P`, compute `A`, `K`, `M1`; **M3**: `State = 03`, `PublicKey = A`, `Proof = M1`.
5. **M4**: `State = 04`, `Proof = M2`; verify. An `Error = 02` here means wrong PIN; the receiver may also return
   `BackOff`/`MaxTries` [ap2r:hap.py:844-855].
6. **M5** `POST /pair-setup`: `State = 05`, `EncryptedData = ChaCha20-Poly1305(key = HKDF(K, "Pair-Setup-Encrypt-Salt",
   "Pair-Setup-Encrypt-Info"), nonce = 0000 0000 "PS-Msg05", aad = none, plaintext = subTLV) || tag` where
   `subTLV = Identifier(ourId) || PublicKey(LTPK) || Signature(sig)` and
   `sig = Ed25519_sign(LTSK, iOSDeviceX || ourId || LTPK)` with `iOSDeviceX = HKDF(K, "Pair-Setup-Controller-Sign-Salt",
   "Pair-Setup-Controller-Sign-Info")` [pyatv:hap_srp.py:165-205; sps:pair_homekit.c:1342-1442, 1115-1142;
   ap2r:hap.py:1061-1117 (receiver verification)]. pyatv additionally appends `Name (0x11)` with the OPACK name;
   skip it unless a receiver turns out to need it.
7. **M6**: `State = 06`, `EncryptedData`. Decrypt with the same key and nonce `0000 0000 "PS-Msg06"`; the plaintext
   is `Identifier(accessoryId) || PublicKey(accessoryLTPK, 32 bytes) || Signature(64 bytes)`. Verify the signature
   with `accessoryLTPK` over `AccessoryX || accessoryId || accessoryLTPK`, `AccessoryX = HKDF(K,
   "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info")` [sps:pair_homekit.c:1547-1665;
   ap2r:hap.py:1119-1174]. **Disagreement**: pyatv skips this verification (`# TODO: verify signature here`
   [pyatv:hap_srp.py:229]); pair_ap enforces it. Enforce it.
8. Persist (3.8). The connection is **not** encrypted after M6. Continue with pair-verify (3.7) on the same
   connection (owntone does `PAIR_SETUP` then `PAIR_VERIFY` back-to-back [owntone:airplay.c:3686-3693];
   pyatv closes the pairing connection and verifies on the next one [pyatv:pairing.py:522-526]). Either works.

`accessoryId` should equal the TXT `pi` value for airplay2-receiver [ap2r:ap2-receiver.py:34-36,276] and
shairport's `config.airplay_device_id` [sps:rtsp.c:1794]; store what M6 says, do not assume.

### 3.7 Pair-verify (M1-M4)

Requires persisted `LTSK`, `ourId`, `accessoryLTPK`, `accessoryId`. Headers `X-Apple-HKP: 3`,
`Content-Type: application/octet-stream` [pyatv:hap.py:140-145].

1. Generate an ephemeral X25519 key pair `(e, E)` [pyatv:hap_srp.py:77-82; sps:pair_homekit.c:1738].
2. **M1** `POST /pair-verify`: `State = 01`, `PublicKey = E` (32 bytes) [pyatv:hap.py:114-121; sps:pair_homekit.c:1750-1751].
3. **M2**: `State = 02`, `PublicKey = E_acc` (32 bytes), `EncryptedData`.
   `shared = X25519(e, E_acc)` (32 bytes).
   `vkey = HKDF(shared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info")`.
   Decrypt `EncryptedData` with `vkey`, nonce `0000 0000 "PV-Msg02"`, no AAD → `Identifier(accessoryId') ||
   Signature(sig_acc)`. Check `accessoryId' == accessoryId` (pyatv raises "incorrect device response"
   [pyatv:hap_srp.py:100-101]) and `Ed25519_verify(accessoryLTPK, sig_acc, E_acc || accessoryId' || E)`
   [pyatv:hap_srp.py:84-109; sps:pair_homekit.c:1842-1947; ap2r:hap.py:1176-1249 (receiver side)].
4. **M3**: `State = 03`, `EncryptedData = AEAD(vkey, nonce 0000 0000 "PV-Msg03", subTLV) || tag` with
   `subTLV = Identifier(ourId) || Signature(Ed25519_sign(LTSK, E || ourId || E_acc))` [pyatv:hap_srp.py:111-124;
   sps:pair_homekit.c:1771-1840].
5. **M4**: `State = 04` and nothing else on success; `Error = 02` if the receiver does not know `ourId` or the
   signature fails [ap2r:hap.py:1251-1343]. pyatv does not check the M4 body (`# TODO: check status code`
   [pyatv:hap.py:136-138]); do check for `Error`, and treat HTTP 470 as "pairing lost → delete persisted keys and
   re-pair" (owntone clears `auth_key` on any pair-verify failure [owntone:airplay.c:3522-3545, 3575-3586]).
6. `SS = shared` (32 bytes). Derive control keys (2.5); encrypt from the next request on.

**Note (2026-10-09, `pairing` package, live against shairport-sync 5.5.2 in Docker, not the SpeakerA):** transient
M1-M4 followed by an encrypted `GET /info` works as in 3.5. With `--password 1234`, transient M3 is answered with
`Error = 2` (pair_ap builds the transient SRP verifier from the password as well [sps:rtsp.c:1793-1794;
sps:pair_ap/pair_homekit.c:2015-2023], so "TLV error on transient" includes M4, not only M2); regular M1-M6 with
method 0 and the password succeeds and the M6 accessory signature verifies; pair-verify M1-M4 completes on the
pairing connection and on a new one. **But shairport-sync does not switch the connection to encryption after
pair-verify**: the cipher set-up in `handle_pair_verify` is commented out [sps:rtsp.c:1725-1741] (only transient M4
activates it, [sps:rtsp.c:1810-1833]); an encrypted request after verify gets no answer ("can't find an RTSP
header") while plaintext requests keep working. This is a receiver gap, not a protocol change: Apple receivers per
pyatv and owntone encrypt after verify, and the sender keeps step 6. Consequence for testing: shairport-sync covers the
encrypted channel only after transient pairing; PIN pairing plus pair-verify plus encryption is exercised by the fake
receiver (and later the SpeakerA), not by shairport-sync.

### 3.8 Persistence and reuse

Per receiver, store (PLAN.md's `pairings` table):

| Field | Size / format | Source |
|---|---|---|
| our Ed25519 seed (`LTSK`) | 32 bytes (pyatv `ltsk`) or the 64-byte libsodium secret key (pair_ap `client_private_key`; the public key is its last 32 bytes) | [pyatv:hap_pairing.py:265-280; sps:pair_homekit.c:1667-1686, 1700-1717] |
| our pairing identifier `ourId` | string | [pyatv:hap_pairing.py:273; owntone:airplay.c:2907] |
| receiver `accessoryLTPK` | 32 bytes | M6 |
| receiver `accessoryId` | string | M6 |
| receiver identity for matching | `pi`/`deviceid`/`pk` from TXT | 1.2 |

pyatv serialises the four values as `hex(ltpk):hex(ltsk):hex(atv_id):hex(client_id)` [pyatv:hap_pairing.py:310-319,
360-380]; pair_ap as `hex(client_private_key 64) || hex(server_public_key 32)` and separately the device id
[sps:pair_homekit.c:1667-1686]. The credential type is "transient" when no keys are stored and the receiver
advertises bit 48/43 [pyatv:hap_pairing.py:282-302, 356-357; auth/__init__.py:120-134].

Reuse: on every new connection to a receiver with stored keys run pair-verify (3.7) and nothing else; on
`Error`/470 drop the stored pairing and show the PIN flow again. On a receiver without stored keys run the
transient flow (3.5), or the PIN flow if the flags demand it. A receiver's `pk` changing in TXT indicates a
reset; expect pair-verify to fail then.

Regular pairings are also what the Home app manages via `POST /pair-add`, `/pair-list`, `/pair-remove`
(methods 3-5, HKP 6/7) [sps:rtsp.c:1607-1683; ap2r:ap2-receiver.py:1027-1055; pair.h:240-252]; a music sender does
not need them.

### 3.9 Error handling summary

- HTTP 470 on transient M1 → PIN flow [owntone:airplay.c:3435-3445].
- TLV `Error` in any response → abort; map codes per 3.2. `Busy (7)` and `BackOff (3)` are retryable later
  [sps:pair_homekit.c:860-877].
- pair-verify failure → delete stored pairing [owntone:airplay.c:3575-3586].
- Proof mismatch in M4 → wrong PIN (regular) or receiver not in transient mode; do not retry transient more than
  once per connection.

### 3.10 Legacy `/auth-setup`, `/fp-setup`, `/pair-setup-pin`: are they needed?

`POST /auth-setup` (MFi-SAP, `et=4` / feature bit 26): body is `0x01` (unencrypted) followed by a 32-byte
Curve25519 public key; the response (~1076 bytes: public key, MFi certificate, signature) is ignored
[pyatv:rtsp.py:37-49, 112-123; protocols.md:1468-1505]. pyatv sends it only for `am=AirPort*` devices
[pyatv:stream_client.py:354-363], owntone has it compiled out and explains it was needed for AirPlay **1**
ANNOUNCE on Sonos Beam / AirPort Express 7.8 [owntone:airplay.c:73, 2781-2819]. airplay2-receiver answers it with a
canned handler [ap2r:ap2-receiver.py:940-985]. **Not required for an AirPlay 2 session that uses HAP pairing**;
keep the 33-byte request implemented behind a per-receiver quirk flag in case a third-party receiver returns
403 to SETUP without it.

The request, for the quirk (`ReceiverQuirks.SendAuthSetup`, implemented by `IReceiverSession.AuthSetupAsync` in the
`session-streams` package, SPEC.md D17):

- When: after pairing and before the session `SETUP`, inside the encrypted channel when one exists (owntone's
  sequence placed it exactly there [owntone:airplay.c:3643-3650]; pyatv sends it before its `SETUP` too
  [pyatv:stream_client.py:330-338]).
- Request line `POST /auth-setup RTSP/1.0` (pyatv uses `HTTP/1.1` for this one request [pyatv:rtsp.py:117-123]; we
  keep the D6 rule, the SpeakerA echoes either token), standard identity headers, `Content-Type:
  application/octet-stream`, `Content-Length: 33`.
- Body: `01` followed by 32 bytes of X25519 public key. pyatv sends a fixed key (`CURVE25519_PUB_KEY`,
  [pyatv:rtsp.py:40-49], taken from owntone's RAOP code) because nothing is ever verified; we send the public key
  of a fresh ephemeral `X25519KeyPair` for the same reason.
- Response: any 2xx is accepted and the body discarded; a non-2xx is logged at Warning and ignored (pyatv passes
  `allow_error`-style best effort [pyatv:stream_client.py:354-363 decides, rtsp.py:112-123 sends]). Nothing derived
  from it is used later.
- The fake receiver answers it with 200 and a 1076-byte zero body (option `AnswerAuthSetup`, default on).

`POST /fp-setup` (FairPlay v3, feature bit 14): a 16-byte `FPLY` request plus a second 164-byte request, answered
with canned 142-byte blobs; it establishes the FairPlay key that iOS senders use to wrap the audio key
(`ekey`/`eiv` in the session SETUP) [sps:rtsp.c:1843-1953; ap2r:ap2-receiver.py:943-985; ap2r:ap2/connections/session_properties.py:129-132].
We send `shk` in plaintext inside the (already encrypted) stream SETUP instead, as pyatv and owntone do; **not needed**.

`POST /pair-setup-pin` + the 2048-bit SRP in `pyatv/protocols/airplay/srp.py` is the AirPlay **1** / tvOS
device-verification pairing (`SupportsLegacyPairing`, bit 27) [pyatv:legacy.py:25-69; srp.py:151-195]; **not
needed** for AirPlay 2 receivers.

---

## 4. Session establishment

The reference senders order the start-up differently; all three orders are accepted by both reference
receivers, because shairport-sync starts playing a type-96 stream at the stream SETUP [sps:rtsp.c:2915-2919] and
treats RECORD as a no-op that returns `Audio-Latency: 0` [sps:rtsp.c:903-911], while airplay2-receiver also
ignores RECORD's position [ap2r:ap2-receiver.py:717-732].

| Step | pyatv (NTP, type 96) [pyatv:airplayv2.py:51-156; stream_client.py:287-338, 375-453] | owntone (PTP or NTP, type 96) [owntone:airplay.c:3643-3657] | iOS per pyatv's capture (remote control) [protocols.md:1571-1597] |
|---|---|---|---|
| 1 | `GET /info` | `GET /info` | |
| 2 | pair-verify / transient pair-setup | pair (transient, verify, or PIN setup + verify) | |
| 3 | `SETUP` session | (`/auth-setup`, compiled out) `SETUP` session | `SETUP` session |
| 4 | connect event channel | connect event channel | connect event channel |
| 5 | `SETUP` stream | `RECORD` | `GET /info` |
| 6 | SET_PARAMETER progress / metadata / artwork | `SETPEERS` (PTP only) | `RECORD` |
| 7 | start feedback loop | `SETUP` stream | `SETUP` stream |
| 8 | `RECORD` | `SET_PARAMETER volume` ("some devices, e.g. Sonos Symfonisk, don't register the volume if it isn't last") | feedback loop |
| 9 | `FLUSH` (`RTP-Info: seq=…;rtptime=…`) | then metadata (text, artwork, progress) | |
| 10 | `SET_PARAMETER volume` | start RTP, sync packet with marker 0x90 | |

**Decision for our sender** (iOS order, with owntone's volume placement): `GET /info` → pair → `SETUP` session →
connect event channel → `RECORD` → (`SETPEERS` if PTP) → `SETUP` stream → `SET_PARAMETER volume` → start the
UDP sync/audio sender (first sync packet with marker bit) → metadata/artwork/progress → feedback loop. Open
question 7.3 covers verifying this against the SpeakerA.

### 4.1 `GET /info` after pairing

Same request as 1.8, now inside the encrypted channel. pyatv issues it before pairing
[pyatv:stream_client.py:330] and uses `initialVolume` (a float in dB) from the answer as the starting volume
when the user has not set one [pyatv:pyatv/protocols/raop/__init__.py:383-391]; owntone uses `statusFlags` from it
to choose the pairing path [owntone:airplay.c:3273-3356]. The SpeakerA does not send `initialVolume` (1.8), so the
sender must start from its own stored level. Send it once per connection; it is also the cheapest "are you
still there" probe (owntone's `PROBE` sequence [owntone:airplay.c:3658-3660]).

### 4.2 Session-level `SETUP`

`SETUP rtsp://<local ip>/<session id> RTSP/1.0`, `Content-Type: application/x-apple-binary-plist`, body a bplist
dict **without** a `streams` key; the receiver distinguishes the session SETUP from a stream SETUP by that
absence [sps:rtsp.c:2407-2415; ap2r:ap2-receiver.py:579-595]. shairport reads `timingProtocol` first and refuses
anything it does not know [sps:rtsp.c:2418-2439, 2729-2737].

Fields, with who sends them:

| Key | Type | pyatv (NTP) [airplayv2.py:56-74] | owntone NTP [airplay.c:2701-2705] / PTP [2748-2759] | Receiver use |
|---|---|---|---|---|
| `timingProtocol` | string | `"NTP"` | `"NTP"` / `"PTP"` | shairport: `PTP` → full session; `NTP` → **501, "Shairport Sync can not handle NTP streams"** [sps:rtsp.c:2432-2438, 2683-2686]; `None` + `isRemoteControlOnly` → remote-control session [2439-2470]. airplay2-receiver stores it [session_properties.py:111-112] and accepts any value. owntone comment: `"None"` makes an Apple TV 4 ignore the stream SETUP [airplay.c:2705] |
| `timingPort` | uint | sender's NTP timing UDP port | (NTP) sender's timing port | NTP only: the receiver sends timing requests there (data-plane spec). shairport ignores it |
| `sessionUUID` | string | upper-case UUID4 | UUID | logged; airplay2-receiver keeps it [session_properties.py:107-108] |
| `deviceID` | string | `"AA:BB:CC:DD:EE:FF"` (fake) | sender MAC-like id with colons | shairport forwards as metadata [sps:rtsp.c:2673] |
| `macAddress` | string | same | real local MAC | same [sps:rtsp.c:2675] |
| `name` | string | `"pyatv"` | configured client name | shairport uses it as `ap2_client_name` for logs [sps:rtsp.c:2400-2405] |
| `model` | string | `"iPhone14,3"` | (absent) | logged [sps:rtsp.c:2674] |
| `osName`, `osVersion`, `osBuildVersion` | string | `"iPhone OS"`, `"16.5"`, `"20F66"` | (absent) | informational |
| `sourceVersion` | string | `"690.7.1"` | (absent) | informational |
| `isMultiSelectAirPlay` | bool | `true` | (absent) | airplay2-receiver stores [session_properties.py:96-97]; meaning: the sender is streaming to several receivers at once |
| `groupContainsGroupLeader` | bool | `false` | (PTP) `false` ("iOS Music app sets this to false") | shairport stores it and advertises `gcgl=` [sps:rtsp.c:2516-2526, 1050-1061] |
| `groupUUID` | string | (absent) | (PTP) one UUID per sender session, shared by all receivers of the group | shairport sets its `gid=` TXT value to it [sps:rtsp.c:2501-2511, 2663-2669] |
| `senderSupportsRelay` | bool | `false` | (absent) | stored [session_properties.py:105-106] |
| `statsCollectionEnabled` | bool | `false` | (absent) | unknown |
| `timingPeerInfo` | dict | (absent) | (PTP) `{ID: <uuid string>, DeviceType: 0, ClockID: <int64>, SupportsClockPortMatchingOverride: false, Addresses: [<local ip>, …]}` | shairport adds every address to its PTP peer list (the sender's connection address first) [sps:rtsp.c:2528-2569] |
| `timingPeerList` | array of the above | (absent) | (PTP) `[timingPeerInfo]` | airplay2-receiver stores [session_properties.py:144-150] |
| `isRemoteControlOnly` | bool | (absent) | (absent) | pyatv remote-control session sends `true` with `timingProtocol: "None"` [ap2_session.py:119-133] |
| `ekey`, `eiv` | data | (absent) | (absent) | FairPlay-wrapped AES key/IV from iOS senders [session_properties.py:129-132]; we never send them |
| `isGroupPlayback`, `isGroupleader`, `isTightSyncGroupLeader`, `tightSyncUUID`, `HTGroupUUID`, `isScreenMirroringSession` | | (absent) | (absent) | known to airplay2-receiver only [session_properties.py:85-110] |

Response (bplist dict):

| Key | Type | Meaning | Sources |
|---|---|---|---|
| `eventPort` | uint | TCP port on the receiver for the event channel (4.3). Mandatory; owntone aborts without it [owntone:airplay.c:3216-3227] | [sps:rtsp.c:2652-2653; ap2r:ap2-receiver.py:581-584; pyatv:airplayv2.py:78] |
| `timingPort` | uint | shairport sends `0` ("dummy"); airplay2-receiver `0`; for NTP the receiver's timing port is not needed because the receiver initiates timing requests to *our* port | [sps:rtsp.c:2654; ap2r:ap2-receiver.py:220] |
| `timingPeerInfo` | dict | PTP only: `{Addresses: [<receiver ips>], ID: <receiver ip>}`; owntone adds the first address of the right family as a PTP slave [owntone:airplay.c:3122-3178] | [sps:rtsp.c:2572-2624; ap2r:ap2-receiver.py:221-224] |

**Disagreement / decision point for PLAN.md**: the plan chooses realtime + NTP "because OwnTone synchronizes
AirPlay 2 receivers this way". owntone actually uses `timingProtocol: PTP` (with its own PTP daemon) whenever the
receiver advertises feature bit 41 and falls back to NTP only otherwise [owntone:airplay.c:4066-4067, 2772-2779];
both LAN receivers advertise bit 41, and shairport-sync answers 501 to an NTP session SETUP. pyatv's NTP + type 96
path works against Apple receivers and airplay2-receiver. Whether the SpeakerA accepts an NTP session is open
question 7.1 and decides whether the data-plane spec needs PTP for M1. **Resolution** (SPEC.md D1): PLAN.md's choice
of the realtime stream with NTP timing stands for v1, its rationale is corrected (that was OwnTone's behaviour before
libairptp), and the timing protocol is part of the pluggable stream layer: if probe P1 shows the SpeakerA refuses NTP,
the realtime stream runs under PTP (0xD7 syncs) with the PTP-master package pulled forward, and nothing else changes.

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** the NTP session SETUP
(pyatv's key set plus `isMultiSelectAirPlay`) is answered `200` with `{eventPort: 7002, timingPort: 7001}`; unlike the
references, the SpeakerA's `timingPort` is **not** 0 (the 0xD2 timing requests come from that port). A PTP session SETUP
with a minimal `timingPeerInfo` is answered `200` with `{eventPort, timingPort, timingPeerInfo: {Addresses: [192.0.2.26,
two ULA, one link-local IPv6], ...}}`. Probe P1 settles D1: NTP is accepted and timing requests arrive (see SPEC.md
section 4). **Note (2026-10-09, `session-streams`, live against shairport-sync 5.5.2 in Docker):** the NTP session SETUP
is answered **`200 OK` with `Content-Length: 0`**, not `501` as read from the source above (the `501` set at [sps:rtsp.c:2396]
does not reach the wire for this path; the NTP branch only warns "Shairport Sync can not handle NTP streams"
[sps:rtsp.c:2683-2686]); a sender must
treat a 2xx without a plist / `eventPort` as a refusal (`ReceiverSession.SetupSessionAsync` throws
`InvalidDataException`). The PTP session SETUP works there (event channel, `updateInfo` on connect, RECORD, stream SETUP
without `streamID`, `GET_PARAMETER` with the leading blank line, `TEARDOWN {}` with `Connection: close`).

### 4.3 The event channel

After the session SETUP response, open a TCP connection from the sender to `<receiver ip>:<eventPort>`
[pyatv:airplayv2.py:80-105; ap2_session.py:135-148; owntone:airplay.c:3229-3234]. airplay2-receiver may not be
listening yet when it answers, so retry a refused connect up to 5 times, 1 s apart [pyatv:airplayv2.py:80-103].
shairport listens before answering [sps:rtsp.c:2626-2638].

Encryption: the 2.5 framing with keys derived from `SS` and salt `Events-Salt`, **swapped**: the sender encrypts
with the `Events-Read-Encryption-Key` key and decrypts with `Events-Write-Encryption-Key`, because the channel is
logically the receiver's ("Note: Read/Write info reversed here as connection originates from receiver!")
[pyatv:ap2_session.py:137-148; airplayv2.py:88-96; sps:pair_homekit.c:2956-2959 (client channel 1), 2968-2971
(server channel 4); sps:rtsp.c:1824-1827; protocols.md:1753-1765]. Counters start at 0 on both sides.

Traffic on it is RTSP-shaped and flows **receiver → sender**. Messages seen:

| Message | Body | When | Sources |
|---|---|---|---|
| `POST /command RTSP/1.0` with `Content-Length`, `Content-Type: application/x-apple-binary-plist`, no `CSeq` (shairport) or `CSeq: 0` (Apple TV) | `{type: "updateInfo", value: <the GET /info dict plus txtAirPlay>}` | shairport: immediately after accepting the event connection [sps:ap2_event_receiver.c:73-80, ap2_event_message_handler.c:109-139]; Apple TV: after RECORD [protocols.md:1767-1784] | |
| `POST /command` | `{type: "sendMediaRemoteCommand", value: "play" \| "paus" \| "nitm" \| "pitm"}` | the receiver's own play/pause/next/previous buttons; owntone maps them to player actions [owntone:airplay_events.c:343-360, 377-402] | **(single source: owntone)** |
| (declared, not implemented in the checkout) `ap2_event_send_unit_volume_notification`, `ap2_event_send_modern_media_remote_command` | | shairport header only [sps:ap2_event_message_handler.h:8-10] | the volume-changed-on-device event the PLAN's volume model relies on has **no implementation in any reference**; open question 7.4 |

The sender must answer every request on the channel, otherwise the receiver closes it after about 30 s
[protocols.md:1786-1795]. pyatv's answer: `<protocol>/<version> 200 OK`, `Content-Length: 0`, `Audio-Latency: 0`,
`Server` echoed if present, `CSeq` echoed if present, blank line [pyatv:pyatv/protocols/airplay/channels.py:60-97 (`EventChannel.handle_received`)].
owntone's: `RTSP/1.0 200 OK`, `Server: <name>/1.0`, `CSeq` if non-zero [owntone:airplay_events.c:214-226]. shairport
reads the answer synchronously right after writing a command [sps:ap2_event_message_handler.c:45-67], so answer
promptly. Messages may be split across frames; parse with the same `\r\n\r\n` + `Content-Length` rule and find
the bplist by its `bplist` magic if the headers are odd [owntone:airplay_events.c:237-285].

Closing the event channel ends the receiver's view of the session: shairport's event-receiver thread cleanup
drops the group id and `Active-Remote` [sps:ap2_event_receiver.c:37-59]. Keep it open for the life of the control
connection and close it after TEARDOWN.

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** the SpeakerA sent
**nothing** on the event channel: no `updateInfo` on connect and none after RECORD, in every session (probe P13 waited
15 s after RECORD with the answer delay set to 5 s; probe P4 kept a session for 10 s). It closes the event channel
right after the session `TEARDOWN {}`. The channel connected on the first attempt every time.

### 4.4 Stream-level `SETUP`

`SETUP` with a bplist `{streams: [ {…} ]}`; one stream per request (shairport only looks at index 0
[sps:rtsp.c:2759]). The connection must already be encrypted (the `shk` travels in the clear inside it).

Fields for **type 96 (realtime)** as sent by pyatv [airplayv2.py:127-147] and owntone [airplay.c:2622-2635]:

| Key | Type | Value | Receiver use |
|---|---|---|---|
| `type` | uint | `96` (`0x60`) | selects the realtime path: UDP audio, UDP control [sps:rtsp.c:2869-2920; ap2r:stream.py:379-380, 410-437] |
| `ct` | uint | `2` = ALAC (pyatv sends `1` = raw PCM with `audioFormat 0x800`) | compression type: 1 PCM, 2 ALAC, 3 AAC-LC, 4 AAC-ELD, 32 OPUS [owntone:airplay.c:2626]; airplay2-receiver: "0x1 PCM, 0x2 ALAC, 0x4 AAC_LC, 0x8 AAC_ELD; largely implied by audioFormat" [ap2r:stream.py:404-405]. **Disagreement** on the numbering (owntone 3 vs ap2r 4 for AAC-LC); for ALAC both say 2. shairport stores it but decodes by `type` [sps:rtsp.c:2787-2796] |
| `audioFormat` | uint | `0x40000` (bit 18: ALAC/44100/16/2); pyatv `0x800` (bit 11: PCM/44100/16/2) | bitmask table in owntone [airplay.c:2576-2611]: bits 2-17 PCM variants (…, 10 PCM/44100/16/1, 11 PCM/44100/16/2, 12-13 24-bit 44100, 14-17 48000), 18 ALAC/44100/16/2, 19 ALAC/44100/24/2, 20 ALAC/48000/16/2, 21 ALAC/48000/24/2, 22 AAC-LC/44100/2, 23 AAC-LC/48000/2, 24-27 AAC-ELD, 28-30 OPUS, 31-32 AAC-ELD mono. Only shairport's buffered path reads it [sps:rtsp.c:2926-2933] |
| `spf` | uint | `352` samples per frame (packet) | shairport `frames_per_packet` [sps:rtsp.c:2799-2807, 2888-2896]; must match the ALAC encoder frame size |
| `sr` | uint | `44100` | sample rate [sps:rtsp.c:2877-2886] |
| `shk` | data | the first 32 bytes of the pairing shared secret `SS` (SPEC.md decision D4) | the ChaCha20-Poly1305 key for RTP payloads. shairport rejects any length other than 32 [sps:rtsp.c:2766-2785]; airplay2-receiver uses it as given [ap2r:stream.py:406]. pyatv derives it from the events key for convenience and documents that "it doesn't really matter what the key is" [airplayv2.py:117-125]; owntone uses the first 32 bytes of `SS` [airplay.c:2630, 1450-1460]. **Resolved**: an earlier draft of this row said "generate 32 random bytes"; SPEC-audio.md 1.3 chooses `SS[0..31]` (the owntone-proven value) and that is the decision; random bytes remain a per-session test option for open question A-Q4 |
| `controlPort` | uint | the sender's UDP control port | the receiver sends retransmit requests (0xd5) there; the sender sends sync packets (0xd4/0xd7) from it to the receiver's `controlPort` |
| `latencyMin` | uint | `11025` (0.25 s at 44.1 kHz) | airplay2-receiver sizes its jitter buffer from it [ap2r:stream.py:415-420]; owntone: "AIRPLAY_AUDIO_LATENCY_MS in samples" [airplay.c:2629] |
| `latencyMax` | uint | `88200` (2 s) | [airplay.c:2628 "TODO how do these latencies work?"] |
| `audioMode` | string | `"default"` | alternatives `moviePlayback` [ap2r:stream.py:333]; `POST /audioMode` exists too [sps:rtsp.c:2169-2174] |
| `isMedia` | bool | `true` | unknown; pyatv/owntone send it |
| `supportsDynamicStreamID` | bool | `false` | stored [ap2r:stream.py:377] |
| `streamConnectionID` | uint64 | pyatv: the RTSP session id; owntone: same | stored [ap2r:stream.py:375]; used with `streamConnections` (bit 59) which we do not send |
| `shiv` | data | (absent) | AES IV for FairPlay/RSA streams only [ap2r:stream.py:411] |

Fields for **type 103 (buffered)** differ: TCP data port, `audioFormat` is honoured, no `latencyMin/Max` in
shairport's reading, `audioBufferSize` comes back in the response [sps:rtsp.c:2921-2970; ap2r:stream.py:441-463].
Keep the same `ct`/`spf`/`sr`/`shk` keys. Type 103 requires `SETRATEANCHORTIME` (4.7) and `FLUSHBUFFERED` (4.8)
and a PTP timeline; it is out of scope for M1.

Response: `{streams: [ {type, dataPort, controlPort, streamID?, audioBufferSize?} ]}`:

| Key | shairport [sps:rtsp.c:2911-2913, 2959-2963, 2981-2984] | airplay2-receiver [ap2r:stream.py:431-437, 454-461] | Use |
|---|---|---|---|
| `type` | echoed | echoed | |
| `dataPort` | receiver UDP (96) / TCP (103) port | same | destination for RTP audio |
| `controlPort` | receiver UDP port | same | destination for sync packets; source of retransmit requests |
| `streamID` | **absent** | monotonically increasing uint32 | needed for a per-stream TEARDOWN (4.9); fall back to the empty-dict TEARDOWN when absent |
| `audioBufferSize` | 103 only: 8 MiB default | both types: 8 MiB | buffered-stream flow control |

owntone requires both ports non-zero and then "connects" its UDP socket to `dataPort` [owntone:airplay.c:3071-3098].

The audio packet that the `shk` protects (data-plane detail, stated here because the key is negotiated here):
`RTP header (12 bytes: 80, 60|E0 for the first packet, seq16, timestamp32, ssrc32) || ChaCha20-Poly1305(key = shk,
nonce = 00000000 || counter64 LE, aad = header[4:12] (timestamp + ssrc), plaintext = ALAC frame) || tag (16) ||
counter (8 bytes, little-endian)` [pyatv:airplayv2.py:183-208; stream_client.py:581-587]. pyatv notes that sending a
zero nonce "worked with Apple but other vendors might care" [airplayv2.py:195-198], so increment it.

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** the stream SETUP answer is
`{streams: [{type: 96, dataPort: 7003, controlPort: 7004}]}`: **no `streamID`** (as shairport-sync), so only the session
TEARDOWN is sent. Both `shk` = `SS[0..31]` and 32 random bytes are accepted (probe P15, control part). The SpeakerA's
0xD2 timing requests start while the stream SETUP is being answered.

### 4.5 `RECORD`

`RECORD rtsp://…/… RTSP/1.0` with the standard headers and **no body**. pyatv sends none [pyatv:rtsp.py:178-184;
stream_client.py:438]; owntone comments "Mysterious empty request, but iOS sends it" [owntone:airplay.c:2652-2657];
iOS per capture sends only `CSeq` and `User-Agent` [protocols.md:1650-1655]. AirPlay 1 senders add `Range: npt=0-`,
`Session` and `RTP-Info: seq=…;rtptime=…` [protocols.md:1366-1377]; shairport's AirPlay 2 handler ignores all of
that and replies `200 OK` with `Audio-Latency: 0` [sps:rtsp.c:903-911]; its AirPlay 1 handler would reply
`Audio-Latency: 11025` and flush to the `RTP-Info` time [sps:rtsp.c:913-958]. airplay2-receiver ignores the body
and replies `Audio-Latency: 0` [ap2r:ap2-receiver.py:717-732]. Read `Audio-Latency` (samples); a non-zero value is
treated as that receiver's output delay, i.e. folded into its per-receiver `offset_frames` (SPEC-audio.md 1.5, 3.3),
never as a change of the group latency `L` (resolution recorded in SPEC.md).

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** RECORD (no body) is answered
`200` **without an `Audio-Latency` header**; the session reads that as 0. RECORD before and after the stream SETUP are
both accepted (probe P3).

### 4.6 `SETPEERS` and `SETPEERSX`

PTP sessions only (owntone skips it for NTP [owntone:airplay.c:2666-2667]). `SETPEERS rtsp://…/… RTSP/1.0`,
`Content-Type: /peer-list-changed`, body a bplist **array of strings**: the receiver's own address first, then the
sender's IPv4 and IPv6 addresses [owntone:airplay.c:2659-2688, 3653; protocols.md:1347-1359]. shairport's handler
is a no-op returning 200 (it already took the peers from `timingPeerInfo`) [sps:rtsp.c:2207-2244];
airplay2-receiver logs it [ap2r:ap2-receiver.py:796-817].

`SETPEERSX` (feature bit 52): `Content-Type: /peer-list-changed-x`, body an array of dicts
`{Addresses: [...], ClockID: <int>, ClockPorts: {<guid>: <port>}, DeviceType: 0, ID: <guid>,
SupportsClockPortMatchingOverride: <bool>}` [ap2r:ap2-receiver.py:819-848]. shairport answers 200 without acting
[sps:rtsp.c:2245-2250]. Not needed for M1; for groups under PTP, `SETPEERS` with all receivers' addresses is what
lets receivers see each other's clocks.

### 4.7 `SETRATEANCHORTIME`

Buffered (103) streams and PTP sessions only; shairport registers the method name truncated to 15 characters
(`SETRATEANCHORTI`) because of its parser [sps:rtsp.c:3703]. Body bplist:

| Key | Type | Meaning | Sources |
|---|---|---|---|
| `rate` | uint | `1` start/resume at the anchor, `0` pause (shairport resets its anchor and stops) | [sps:rtsp.c:1460-1494; ap2r:ap2-receiver.py:744-752] |
| `rtpTime` | uint32 | RTP timestamp that is to be played at the network time below | [sps:rtsp.c:1449-1457] |
| `networkTimeSecs` | uint | whole seconds of the sender's PTP/network clock | |
| `networkTimeFrac` | uint64 | fraction of a second in units of 2^-64 (shairport shifts it down 32 bits before scaling) | [sps:rtsp.c:1433-1445; ap2r:session_properties.py:171-175] |
| `networkTimeTimelineID` | uint64 | clock identity of the sender's PTP clock the times refer to | [sps:rtsp.c:1420-1428] |

Response 200, no body. For realtime (96) streams the equivalent information travels in the UDP sync packets
instead (data-plane spec; owntone's PTP sync packet carries `rtptime`, 64-bit nanoseconds and the clock id
[owntone:rtp_common.c:258-291], its NTP sync packet carries `rtptime - latency`, NTP seconds/fraction and `rtptime`
[rtp_common.c:308-344]).

### 4.8 `FLUSH` and `FLUSHBUFFERED`

`FLUSH` (type 96): `FLUSH rtsp://…/… RTSP/1.0` with header `RTP-Info: seq=<next seq>;rtptime=<next rtptime>`
[pyatv:stream_client.py:440-448 (also `Range: npt=0-`, `Session: 0`); owntone:airplay.c:2400-2417]. The receiver
discards everything before that timestamp: shairport parses only `rtptime=` and calls `player_flush`
[sps:rtsp.c:2329-2363; AirPlay 1 shairport returns 451 when it is not the playing connection]; airplay2-receiver
parses both values [ap2r:ap2-receiver.py:850-868]. Response 200, no body. pyatv sends one FLUSH right after RECORD
as the "start here" marker. After a FLUSH the sender continues (or restarts) RTP with the announced sequence and
timestamp and sends a fresh sync packet with the marker bit (owntone drops its session to `CONNECTED` on the
FLUSH response so the next sync packet is a `0x90` "start" packet [owntone:airplay.c:3251-3256, 2182-2191]). This is
the track-change / seek / pause mechanism for realtime streams: flush, then resume with a jump in `rtptime`.

`FLUSHBUFFERED` (type 103): body bplist `{flushUntilSeq: <uint>, flushUntilTS: <uint>}` for an immediate flush
("discard everything up to and including this packet, stop playing until the next SETRATEANCHORTIME rate=1"), or
`{flushFromSeq, flushFromTS, flushUntilSeq, flushUntilTS}` for a deferred flush of a range that may not have
arrived yet [sps:rtsp.c:1294-1395; ap2r:ap2-receiver.py:463-488]. shairport masks the sequence numbers with
`0x7fffff` [sps:rtsp.c:1312, 1349, 1376-1378]. Response 200.

**Note (2026-10-09, `audio-pipeline-group-sync`, live against the Test Speaker ABCD, quiet tone):** (probe P19) `FLUSH`
with `RTP-Info: seq=<next>;rtptime=<next>` is answered `200`, but only **after 2.0 s** (pause: the pacer had stopped) and
**3.0 s** (seek while streaming); a new `0x90` sync + marker packet at the announced values resumes the stream without a
new `RECORD`. The group player waits for the answers before it re-anchors (this section and 6.3 order: FLUSH, 200, then
sync and RTP), so on the SpeakerA a seek or skip is silent for the FLUSH round trip plus `L`.

### 4.9 `TEARDOWN`

Two forms, both `TEARDOWN rtsp://…/… RTSP/1.0` with a bplist body:

- **Per stream**: `{streams: [ {streamID: <id>, type: <96|103>} ]}`. shairport stops the player but keeps the
  connection [sps:rtsp.c:2302-2311]; airplay2-receiver tears down the matching stream objects and terminates its
  event process only when no streams remain and the plist is empty [ap2r:ap2-receiver.py:765-795].
- **Whole session**: `{}` (an empty dict). shairport replies `Connection: close` and ends the connection
  [sps:rtsp.c:2312-2320]; owntone sends exactly this, noting "iOS seems to send first a teardown with stream (to
  close that), then with an empty plist" [owntone:airplay.c:2419-2452].

**Disagreement**: pyatv sends TEARDOWN with a `Session` header and **no body** [pyatv:rtsp.py:250-252]; shairport
logs "has no plist -- nothing done" for that and keeps everything running [sps:rtsp.c:2322-2324]. Always send a
bplist (SPEC.md D7). Graceful stop: per-stream TEARDOWN (if a `streamID` is known) → session TEARDOWN → close the event channel
and the control socket. Response 200 (shairport adds `Connection: close` to the session form).

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** (probe P5) the session
`TEARDOWN {}` is answered `200` **without `Connection: close`** and the control connection stays open (still open 2 s
later); the SpeakerA closes the event channel. The next session on a new connection starts with `200` (no 451/453).

### 4.10 Keep-alive: `POST /feedback`, timeouts

`POST /feedback RTSP/1.0` with the standard headers and no body. iOS sends it every 2 s
[pyatv:ap2_session.py:28-29, 84-108 (`heartbeater`); airplayv2.py:25, 167-181; protocols.md:1722-1749]; owntone has a
`FEEDBACK` sequence [owntone:airplay.c:3700-3702] and additionally re-sends the progress parameter every 25 s as a
keep-alive for Apple TVs [owntone:airplay.c:113, 1891-1910, 1750, 4276]; pyatv's AirPlay 1 path probes once and then
sends every 25 s only if the first answer was 200 [pyatv:airplayv1.py:289, 360-382].

Responses: shairport answers 200 with a bplist `{streams: [ {type: <96|103>, sr: <44100.0|48000.0 real>} ]}`
while playing and an empty body otherwise [sps:rtsp.c:2033-2081]; airplay2-receiver answers `{streams: [<stream
descriptors>]}` or `{}` [ap2r:ap2-receiver.py:907-938]; an Apple TV answers `{streams: []}` [protocols.md:1740-1747].
Treat any 2xx as alive; pyatv treats failures as best-effort and does not abort [airplayv2.py:172-181] while its
AP2 remote-control session declares the connection lost on a failed feedback [ap2_session.py:84-108]. Use 2 s and
declare the receiver gone after 3 consecutive failures or a transport error.

Receiver-side timeouts to respect: an unanswered event-channel request closes the event channel after ~30 s
[protocols.md:1786-1787]; shairport resets the realtime anchor after 7 s without control packets
[sps:rtp.c:1757-1766] and uses TCP keepalive on the control socket with `config.timeout` (default 60 s, minimum
60) [sps:rtsp.c:4556-4590; sps:shairport.c:356, 503-505]. `keepAliveLowPower` / `keepAliveSendStatsAsBody` in the
`/info` plist (both `true` on the SpeakerA) hint at feedback bodies with statistics; no sender in the references
sends one (7.8).

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** (probe P8) `POST /feedback` is
answered `200` with `{streams: [{type: 96, sr: 44100}]}` (integer `sr`) after a stream SETUP, whether the request has no
body, `{}` or `{streams: []}`. The idle-drop timing (10-minute runs) has not been measured.

### 4.11 Other control-plane messages

- `OPTIONS * RTSP/1.0` → `Public: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, POST,
  GET, PUT` [sps:rtsp.c:2284-2293; ap2r:ap2-receiver.py:398-416]. Not required; useful as a probe. AirPlay 1
  receivers answer `Apple-Challenge` here [ap2r:ap2-receiver.py:405-409].
- `GET_PARAMETER rtsp://…/… RTSP/1.0`, `Content-Type: text/parameters`, body exactly `volume\r\n` (shairport
  requires `Content-Length` to equal `strlen("volume\r\n")` = 8 and the body to start with `volume`
  [sps:rtsp.c:3318-3325]). Response `200 OK` with a `text/parameters` body whose shape differs per receiver:
  shairport-sync answers **with a leading blank line**, `"\r\nvolume: %.6f\r\n"` [sps:rtsp.c:3327-3332
  (`snprintf(p, 128, "\r\nvolume: %.6f\r\n", suggested_volume(conn))`)]; airplay2-receiver answers
  `volume: <value>\r\n` with no leading line (or `volume: 0\r\n` when its volume management is disabled)
  [ap2r:ap2-receiver.py:636-665]. Parser rule: split the body into lines, skip empty ones (leading CRLF), find the
  first line that starts with `volume:` (case-insensitive) anywhere in the body, parse the rest as an invariant
  float; absent → "no value" (the receiver does not support it). `VolumeMapping.ParseParameter` implements this.
  The only way to read the receiver's current volume on demand.
- `POST /command` (sender → receiver, bplist `{type: "updateMRSupportedCommands", params: {mrSupportedCommandsFromSender:
  [<bplist data>…]}}`) is the Now-Playing/MediaRemote path used when feature bit 50 is set [sps:rtsp.c:2083-2167;
  ap2r:ap2-receiver.py:885-905]. Out of scope.
- `POST /audioMode` [sps:rtsp.c:2169-2174], `POST /configure` (HomeKit setup) [sps:rtsp.c:1969-2031], `SETMAGICCOOKIE`
  (iOS 17) [ap2r:ap2-receiver.py:870-883], `PUT /setProperty`, `POST /rate`, `/play`, `/playback-info` (video)
  [pyatv:airplayv2.py:210-273; player.py]. Out of scope.

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** `GET_PARAMETER volume` is answered
`volume: -24.000000\r\n` with **no leading blank line** (the airplay2-receiver shape); before any volume was set in the
first session it reported `-30.000000`, later sessions reported the last value set (the level persists across sessions).
The paired `GET /info` (bare and stage 1) still has no `initialVolume` (probe P10).

### 4.12 What crosses to the data plane

From the control plane the data-plane needs: receiver `dataPort` and `controlPort` (4.4 response), our
`controlPort` and `timingPort` (chosen before the SETUPs, since they are sent in the SETUP bodies), `shk`, `spf`,
`sr`, the initial `seq`/`rtptime` announced in FLUSH, `Audio-Latency` from RECORD, `latencyMin`, and for PTP the
clock id / peer addresses. Receivers send on the control port: 0xd5 retransmit requests
(`80 d5 <len16> <first seq16> <count16>` [ap2r:ap2/connections/control.py:32-35, 88-95; pyatv:stream_client.py:146-170
(answers with `80 d6 <orig seq16> || original packet`)]) and nothing else in the references. shairport's AP2
control receiver only understands 0xd7 (PTP time announce) and 0xd6 [sps:rtp.c:1678-1750], so with an NTP session
it would ignore 0xd4 sync packets (moot: it refuses NTP sessions at SETUP).

---

## 5. `SET_PARAMETER`

`SET_PARAMETER rtsp://…/… RTSP/1.0` with the standard headers; the `Content-Type` selects the handler
[sps:rtsp.c:3340-3431; ap2r:ap2-receiver.py:667-715]. Response is always `200 OK` with no body
[sps:rtsp.c:3430]; a receiver that does not want a metadata kind silently drops it.

### 5.1 Volume

`Content-Type: text/parameters`, body `volume: <value>\r\n` [pyatv:rtsp.py:194-200; owntone:airplay.c:2454-2477].
The value is a decimal in **dB**, range `-30.0` (quietest audible) to `0.0` (full), and exactly `-144.0` means
mute [pyatv:utils.py:277-280, 529-550; owntone:airplay.c:1805-1822, 1868-1870]. Formatting: owntone prints
`-%d.%06d` to avoid locale commas and to keep `-0.3` negative [owntone:airplay.c:2463-2466]; pyatv `str(float)`.
shairport parses with `atof` after the literal prefix `volume: ` [sps:rtsp.c:3230-3231]; airplay2-receiver splits on
`:` [ap2r:ap2-receiver.py:677-685]. Always use `.` as decimal separator and a leading `-`.

Mapping from the 0..100 scale of PLAN.md's volume model: pyatv `dB = -30 + 30 * pct / 100` for `pct > 0`, `-144`
for 0 [pyatv:utils.py:529-539]; back: `pct = (dB + 30) / 30 * 100`, anything below -30 → 0 [utils.py:542-550].
owntone identical, with an optional per-device `max_volume` (1..11) scaling the top of the range
[owntone:airplay.c:1785-1822]. The receiver's own curve maps this dB value to its hardware (shairport documents its
transfer function in `documents/Shairport Volume Control Transfer Function.pdf`).

When: owntone sends it last in the start-up sequence because "some devices (e.g. Sonos Symfonisk) don't
register the volume if it isn't last" [owntone:airplay.c:3655-3656], i.e. after the stream SETUP; pyatv sends it
after RECORD + FLUSH and, if an attempt before streaming failed, retries after the stream is up
[pyatv:stream_client.py:450-451; raop/__init__.py:393-399]. shairport applies it only when the sending
connection is the one playing [sps:rtsp.c:3240-3258]. Rule: send after the stream SETUP, repeat on every user
change, and re-send after any FLUSH/re-SETUP. Reading it back: `GET_PARAMETER volume` (4.11); unsolicited
receiver-side changes: open question 7.4.

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** (probe P3) a `SET_PARAMETER volume`
sent **before** the stream SETUP is answered `200` but **not applied** (`GET_PARAMETER` kept the previous value, also after
the stream SETUP); after the stream SETUP it is applied. This confirms the "volume last" rule of D5 for the SpeakerA.

### 5.2 Metadata (DMAP)

`Content-Type: application/x-dmap-tagged`, header `RTP-Info: rtptime=<t>` (pyatv: `seq=<s>;rtptime=<t>`), body a
DMAP container [pyatv:rtsp.py:202-226; owntone:airplay.c:2549-2573, 981-1002].

DMAP encoding [pyatv:pyatv/protocols/dmap/tags.py:39-88 (`uint8_tag`..`uint64_tag` 39-64, `string_tag` 77-83, `container_tag` 86-88 = `raw_tag`)]: each item is `tag (4 ASCII bytes) || length (4 bytes,
big-endian) || value`. Integers are big-endian with the length giving the width (1, 2, 4, 8); strings UTF-8
without terminator; a container's value is the concatenation of its items. The body is one container `mlit`
(`dmap.listingitem`); shairport skips the 8-byte outer header unconditionally and then walks `tag/len/value`
items until the body ends [sps:rtsp.c:3282-3314], so the outer tag must be present and its length is not
checked.

Tags and types (from the DMAP content codes airplay2-receiver documents as what iOS and Music send
[ap2r:ap2/dxxp.py:77-101], plus pyatv's three):

| Tag | Name | Type | Note |
|---|---|---|---|
| `mlit` | dmap.listingitem | container | outer wrapper (pyatv, airplay2-receiver) |
| `minm` | dmap.itemname | string | title [pyatv:rtsp.py:212] |
| `asar` | daap.songartist | string | artist [rtsp.py:216] |
| `asal` | daap.songalbum | string | album [rtsp.py:214] |
| `ascp` | daap.songcomposer | string | |
| `asgn` | daap.songgenre | string | |
| `astm` | daap.songtime | uint32 | duration in ms |
| `astn`, `astc` | daap.songtracknumber, daap.songtrackcount | uint16 | |
| `asdn`, `asdc` | daap.songdiscnumber, daap.songdisccount | uint16 | |
| `ascr` | daap.songcontentrating | uint8 | 0 none, 1 advisory, 2 clean |
| `asdk` | daap.songdatakind | uint8 | 0 local file, 1 remote stream |
| `caps` | dacp.playerstate | uint8 | 1 playing, 2 paused, 3 stopped, 4 fast-forward, 5 rewind [dxxp.py:16-25] |
| `miid` | dmap.itemid | uint32 | |
| `mper` | dmap.persistentid | uint64 | |
| `aeSI` | com.apple.itunes.itms-songid | uint32 | |
| `mstt` | dmap.status | uint32 | |

Minimum that works everywhere: `mlit { minm, asar, asal }` (pyatv) [rtsp.py:210-225]. owntone adds the rest
through its DMAP builder (not read here). shairport forwards every `core` tag to its metadata pipe without
interpretation [sps:rtsp.c:3305-3309], so unknown tags are harmless.

`RTP-Info` semantics: the `rtptime` ties the metadata, the artwork and the progress to the stream position where
the item starts; shairport uses it "as a kind of an ID that can be used to link items of metadata, including
pictures, that refer to the same entity" and brackets each item with `mdst`/`mden` or `pcst`/`pcen` markers
carrying that time [sps:rtsp.c:3352-3412]. owntone computes it as the RTP timestamp of the first sample of the
track (`start`), see 5.4 [owntone:airplay.c:936-1002]. Send the same `rtptime` in metadata, artwork and
progress for one track.

**Note (2026-10-09, `session-streams`, live against the Test Speaker ABCD 192.0.2.26, no audio):** (probe P9) in a session **without
audio** the SpeakerA answers every DMAP item with **`500 Internal Server Error`** (after ~10 s for the first one): the
D12 form `mlit{minm, asar, asal, astm}`, pyatv's minimal `mlit{minm, asar, asal}`, and the latter with `RTP-Info:
seq=0;rtptime=`. Artwork (634-byte JPEG) and progress are answered `200`. Whether DMAP is accepted while audio plays is
for wave 4; the session sends metadata best-effort (non-2xx logged, not thrown).

**Note (2026-10-09, `audio-pipeline-group-sync`, live against the Test Speaker ABCD, quiet tone):** (probe P9, audio half)
DMAP (`mlit{minm, asar, asal, astm}`) is answered **`500` also while audio is streaming**; artwork and progress `200`.
The SpeakerA does not take DMAP text in this form; the group player keeps sending it best effort.

### 5.3 Artwork

`Content-Type: image/jpeg` or `image/png` (shairport warns the type "isn't reliable" and sniffs the bytes
[sps:rtsp.c:3390-3415]), header `RTP-Info: rtptime=<t>`, body the raw image [pyatv:rtsp.py:228-244 (jpeg only);
owntone:airplay.c:2505-2547 (jpeg or png by detected format)]. Send only if the receiver wants artwork (feature bit
15 or `md` contains 1). Size: shairport allocates 256 KiB for requests with cover art [sps:rtsp.c:594-598]; keep
artwork under ~200 KiB (scale to 600x600 JPEG). The whole request goes through the 1024-byte encryption frames of
2.5 (the send cap is fixed; larger frames are never sent, see 7.15), which is fine but means a 200 KiB image is
~200 frames; do not interleave other requests while it is being written.

### 5.4 Progress

`Content-Type: text/parameters`, body `progress: <start>/<current>/<end>\r\n` where all three are RTP timestamps
(32-bit, sample units at `sr`) [pyatv:stream_client.py:399-404; owntone:airplay.c:2479-2503; sps:rtsp.c:3263-3271
(forwards the string, comment: "rtpstampstart/rtpstampnow/rtpstampend 44100 per second")]. airplay2-receiver
forwards it to its audio process [ap2r:ap2-receiver.py:686-694].

owntone's computation [owntone:airplay.c:936-979], the only one that handles mid-track joins: with `start` = the RTP
timestamp of the track's first sample (derived from the current play position and the track's elapsed time),
`display = start - 15360` on a fresh start or `start - 30720` on a track switch (so the receiver's display does
not run ahead while the buffer fills), `current = max(position_now, start)`, `end = start + len_ms * sr / 1000`
(or `current` if the length is unknown). pyatv sends `start = current = rtptime_at_start`, `end = start +
duration * sr` [stream_client.py:401-404].

When: pyatv sends progress, then metadata, then artwork before RECORD [stream_client.py:399-428]; owntone sends
volume first, then text, artwork, progress after the stream SETUP (`response_handler_volume_start` →
`airplay_metadata_startup_send`) [owntone:airplay.c:3110-3120, 1738-1748, 1721-1736] and re-sends all three on
every track change plus progress alone every 25 s as a keep-alive [owntone:airplay.c:1750-1754, 1891-1910]. For the
bridge: on each track change (after the FLUSH that starts the new track's timeline) send metadata, artwork and
progress with the new track's `start` rtptime; on seek/resume re-send progress; every 25 s re-send progress when
the receiver advertises it (keeps Apple TVs from dropping the session [owntone:airplay.c:4276]).

---

## 6. Sequence diagrams

Notation: `S` = sender (us), `R` = receiver, `[enc]` = inside the ChaCha20-Poly1305 control framing, `[ev]` =
on the event channel (encrypted with the Events keys), `(udp)` = data plane, out of scope here. Headers other
than those that matter are omitted.

### 6.1 First connection with transient pairing (realtime stream, NTP timing as in pyatv)

```
 S                                                          R
 |  mDNS PTR/SRV/TXT _airplay._tcp, _raop._tcp              |
 |<-------------------------------------------------------- |  features bit 48, flags no 0x200/0x8/0x80
 |  TCP connect :7000                                       |
 |--------------------------------------------------------->|
 |  GET /info RTSP/1.0  (CSeq 0, DACP-ID, Active-Remote)    |
 |--------------------------------------------------------->|
 |  200 OK  bplist {statusFlags, features, initialVolume?}  |
 |<---------------------------------------------------------|
 |  POST /pair-pin-start  X-Apple-HKP: 4                    |
 |--------------------------------------------------------->|
 |  200 OK                                                  |
 |<---------------------------------------------------------|
 |  POST /pair-setup  TLV{Method 0, State 1, Flags 0x10}    |  M1
 |--------------------------------------------------------->|
 |  200 OK  TLV{State 2, Salt(16), PublicKey B(384)}        |  M2   (470 here -> PIN flow, 6.2 style)
 |<---------------------------------------------------------|
 |  POST /pair-setup  TLV{State 3, PublicKey A, Proof M1}   |  M3   PIN "3939"
 |--------------------------------------------------------->|
 |  200 OK  TLV{State 4, Proof M2}                          |  M4   verify M2; SS = K (64 B)
 |<---------------------------------------------------------|
 |  ===== control keys = HKDF(SS, Control-Salt, ...) =====  |
 |  [enc] GET /info                                         |  (optional repeat)
 |--------------------------------------------------------->|
 |  [enc] SETUP {timingProtocol NTP, timingPort, sessionUUID, deviceID, macAddress, name, model, os*, ...}
 |--------------------------------------------------------->|
 |  [enc] 200 OK {eventPort, timingPort 0}                  |
 |<---------------------------------------------------------|
 |  TCP connect :eventPort  (retry on refuse, <= 5 x 1 s)   |
 |--------------------------------------------------------->|
 |  [ev] POST /command {type updateInfo, value {...}}       |  (shairport: immediately; Apple TV: after RECORD)
 |<---------------------------------------------------------|
 |  [ev] RTSP/1.0 200 OK  Content-Length: 0  Audio-Latency: 0
 |--------------------------------------------------------->|
 |  [enc] RECORD                                            |
 |--------------------------------------------------------->|
 |  [enc] 200 OK  Audio-Latency: 0                          |
 |<---------------------------------------------------------|
 |  [enc] SETUP {streams:[{type 96, ct 2, spf 352, sr 44100, audioFormat 0x40000, shk(32), controlPort, latencyMin 11025, latencyMax 88200, audioMode default, isMedia, supportsDynamicStreamID false, streamConnectionID}]}
 |--------------------------------------------------------->|
 |  [enc] 200 OK {streams:[{type 96, dataPort, controlPort, streamID?}]}
 |<---------------------------------------------------------|
 |  [enc] SET_PARAMETER text/parameters "volume: -15.000000"|
 |--------------------------------------------------------->|
 |  [enc] 200 OK                                            |
 |<---------------------------------------------------------|
 |  (udp) sync packet 0x90 0xd4 ... ; RTP audio seq0/ts0 ...|
 |--------------------------------------------------------->|
 |  [enc] FLUSH  RTP-Info: seq=<seq0>;rtptime=<ts0>         |  (pyatv's "start here"; optional)
 |--------------------------------------------------------->|
 |  [enc] 200 OK                                            |
 |<---------------------------------------------------------|
 |  [enc] SET_PARAMETER application/x-dmap-tagged  RTP-Info: rtptime=<start>   mlit{minm, asar, asal}
 |--------------------------------------------------------->|
 |  [enc] SET_PARAMETER image/jpeg  RTP-Info: rtptime=<start>
 |--------------------------------------------------------->|
 |  [enc] SET_PARAMETER text/parameters "progress: <display>/<current>/<end>"
 |--------------------------------------------------------->|
 |  (udp) receiver timing requests -> our timingPort; retransmit requests 0xd5 -> our controlPort
 |<---------------------------------------------------------|
 |  [enc] POST /feedback   every 2 s                        |
 |--------------------------------------------------------->|
 |  [enc] 200 OK {streams:[...]}                            |
 |<---------------------------------------------------------|
```

Sources: pyatv flow [pyatv:airplayv2.py:51-156; stream_client.py:287-453], owntone start sequence
[owntone:airplay.c:3643-3657], shairport handlers cited in section 4.

### 6.2 First connection with a receiver that requires a PIN (regular pairing), then reconnection

```
 S                                                          R  (flags bit 9 set, e.g. the LG TV)
 |  GET /info ... 200 OK {statusFlags 0x644}                |
 |<-------------------------------------------------------->|
 |  POST /pair-pin-start  X-Apple-HKP: 3                    |
 |--------------------------------------------------------->|
 |  200 OK                                   (R shows PIN)  |
 |<---------------------------------------------------------|
 |  POST /pair-setup  TLV{Method 0, State 1}                |  M1
 |--------------------------------------------------------->|
 |  200 OK  TLV{State 2, Salt, PublicKey B}                 |  M2
 |<---------------------------------------------------------|
 |          ... web app asks the user for the PIN ...       |
 |  POST /pair-setup  TLV{State 3, PublicKey A, Proof M1}   |  M3   P = PIN
 |--------------------------------------------------------->|
 |  200 OK  TLV{State 4, Proof M2}                          |  M4   (Error 2 = wrong PIN)
 |<---------------------------------------------------------|
 |  POST /pair-setup  TLV{State 5, EncryptedData(PS-Msg05){Identifier ourId, PublicKey LTPK, Signature}}
 |--------------------------------------------------------->|
 |  200 OK  TLV{State 6, EncryptedData(PS-Msg06){Identifier accId, PublicKey accLTPK, Signature}}
 |<---------------------------------------------------------|
 |  persist {LTSK, ourId, accLTPK, accId}                   |
 |  --- pair-verify on the same connection ---              |
 |  POST /pair-verify  TLV{State 1, PublicKey E}            |  M1
 |--------------------------------------------------------->|
 |  200 OK  TLV{State 2, PublicKey E_acc, EncryptedData(PV-Msg02){Identifier accId, Signature}}
 |<---------------------------------------------------------|
 |  POST /pair-verify  TLV{State 3, EncryptedData(PV-Msg03){Identifier ourId, Signature}}
 |--------------------------------------------------------->|
 |  200 OK  TLV{State 4}                                    |  SS = X25519 shared (32 B)
 |<---------------------------------------------------------|
 |  ===== control keys from SS; continue as in 6.1 from the session SETUP =====
```

**Reconnection with a persisted pairing** is the same diagram starting at "pair-verify" (no
`/pair-pin-start`, no `/pair-setup`): `GET /info` → `POST /pair-verify` M1/M3 → encrypted → `SETUP` session
→ event channel → `RECORD` → `SETUP` stream → volume → metadata → feedback. If M2/M4 carries `Error` or the
HTTP status is 470, delete the stored pairing and fall back to the PIN flow.

Sources: [pyatv:hap.py:48-96, 103-137; sps:pair_homekit.c:1242-1442 (requests), 1444-1665 (responses),
1724-1968 (verify); owntone:airplay.c:3686-3693, 3522-3586].

### 6.3 Track change with flush (realtime stream)

```
 S                                                          R
 |  (udp) RTP ... seq=n, ts=t            (old track)        |
 |--------------------------------------------------------->|
 |  Spotify: next track / seek / pause                      |
 |  stop RTP; choose seq' = n+1, ts' = t + gap (or keep)    |
 |  [enc] FLUSH  RTP-Info: seq=<seq'>;rtptime=<ts'>         |  receiver drops buffered audio before ts'
 |--------------------------------------------------------->|
 |  [enc] 200 OK                                            |
 |<---------------------------------------------------------|
 |  (udp) sync packet with marker (0x90) for ts'            |  re-anchors the timeline
 |--------------------------------------------------------->|
 |  (udp) RTP seq', ts' ... (new track)                     |
 |--------------------------------------------------------->|
 |  [enc] SET_PARAMETER dmap  RTP-Info: rtptime=<ts'>       |  new title/artist/album
 |--------------------------------------------------------->|
 |  [enc] SET_PARAMETER image/jpeg  RTP-Info: rtptime=<ts'> |
 |--------------------------------------------------------->|
 |  [enc] SET_PARAMETER "progress: <ts'-30720>/<ts'>/<ts'+len>"
 |--------------------------------------------------------->|
 |  (pause: FLUSH then no RTP; resume: new 0x90 sync + RTP; progress re-sent)
```

Sources: [pyatv:stream_client.py:440-448; owntone:airplay.c:2400-2417, 3251-3256, 2182-2191, 962-975;
sps:rtsp.c:2329-2363]. For buffered streams the equivalents are `FLUSHBUFFERED {flushUntilSeq, flushUntilTS}` and
`SETRATEANCHORTIME {rate 0}` / `{rate 1, rtpTime, networkTime*}` [sps:rtsp.c:1294-1499].

### 6.4 Graceful teardown

```
 S                                                          R
 |  (udp) stop sending RTP and sync packets                 |
 |  [enc] TEARDOWN  bplist {streams:[{streamID <id>, type 96}]}   (only if a streamID was returned)
 |--------------------------------------------------------->|
 |  [enc] 200 OK                                            |  receiver stops the player, keeps the session
 |<---------------------------------------------------------|
 |  [enc] TEARDOWN  bplist {}                               |
 |--------------------------------------------------------->|
 |  [enc] 200 OK  Connection: close                         |
 |<---------------------------------------------------------|
 |  close event channel TCP; close control TCP              |
 |  close UDP sockets (control, timing, audio)              |
```

Sources: [sps:rtsp.c:2295-2326; owntone:airplay.c:2419-2452, 3665; ap2r:ap2-receiver.py:765-795]. A dropped
TCP connection without TEARDOWN is also handled by receivers (shairport: event-receiver cleanup
[sps:ap2_event_receiver.c:37-59]), but the next connection may then get 453/451 until the receiver notices.

### 6.5 Receiver-initiated events

```
 S                                                          R
 |  [ev] POST /command {type updateInfo, value {statusFlags, ...}}      (any time; e.g. renamed, flags changed)
 |<---------------------------------------------------------|
 |  [ev] 200 OK                                             |
 |--------------------------------------------------------->|
 |  [ev] POST /command {type sendMediaRemoteCommand, value "paus"|"play"|"nitm"|"pitm"}   (buttons on the device)
 |<---------------------------------------------------------|
 |  [ev] 200 OK                                             |  bridge maps to Spotify pause/play/next/prev
 |--------------------------------------------------------->|
 |  volume changed on the device: NO reference shows an unsolicited event (7.4);
 |  poll [enc] GET_PARAMETER "volume\r\n" -> "volume: -18.0\r\n" if needed
 |  receiver-initiated teardown: the receiver closes the event channel and/or the control TCP connection
 |  (another sender took over, power off). Detect via EOF on either socket or a failed POST /feedback,
 |  then close everything and mark the receiver disconnected.
```

Sources: [sps:ap2_event_message_handler.c:109-139; owntone:airplay_events.c:287-372, 445-506;
protocols.md:1767-1797; sps:rtsp.c:3318-3338 (GET_PARAMETER)].

---

## 7. Open questions, each with a concrete probe

All probes target the Test Speaker ABCD (192.0.2.26) unless stated; never the LG TV. Probes that go beyond
`GET /info` (pairing, SETUP, audio) are outside the allowance of this research stage and belong to M1
implementation testing.

| # | Question | Why it matters | Probe |
|---|---|---|---|
| 7.1 | Does the SpeakerA accept a session `SETUP` with `timingProtocol: "NTP"` and then a type-96 stream, i.e. the pyatv path? shairport-sync answers 501 to NTP [sps:rtsp.c:2683-2686]; owntone uses PTP whenever bit 41 is set [owntone:airplay.c:4066-4067]; airplay2-receiver's notes say iOS uses PTP for `srcvers >= 355` [ap2r:ap2-receiver.py:58-67] and the SpeakerA reports 366.0. | Decides whether M1 can ship with NTP-style timing or needs a PTP clock (and `SETPEERS`) from the start; also whether shairport-sync can be a test receiver at all. | **Control part (decisive, no audio, wave 3)**: after transient pairing, send the session SETUP (4.2) with `timingProtocol: "NTP"` and our `timingPort`; expect 200 with `eventPort`; connect the event channel, RECORD, stream SETUP; then wait 2 s and count the 0xD2 timing requests that arrived at `timingPort` (`ITimingResponder.RepliesSent > 0`). A 501 (shairport's answer) or zero requests within 2 s of the stream SETUP means "no NTP": the realtime stream runs under PTP (SPEC.md D1). Repeat with `timingProtocol: "PTP"` and a minimal `timingPeerInfo` and record the response. **Audio part (confirmation only, wave 4, `--i-know`)**: 3 s quiet tone after one `0x90` sync; must be audible under the protocol the control part chose. |
| 7.2 | Does transient pairing work while `flags` bit 10 (`Enable_HK_Access_Control`) is set with `acl=0`? Both LAN receivers have it. | If the bit alone blocks transient pairing, the PIN/HomeKit flow becomes the default even for the SpeakerA. | Send `POST /pair-pin-start` (HKP 4) and transient M1; expect 200 + M2 with Salt/PublicKey. A 470 or TLV Error confirms the block. |
| 7.3 | Exact start-up order the SpeakerA needs: does `RECORD` before the stream `SETUP` (iOS/owntone) and after it (pyatv) both work, and must the volume be last (Sonos quirk)? | Avoids a per-vendor quirk table. | Run both orders; compare `Audio-Latency` and whether audio starts. Send volume before and after the stream SETUP and read it back with `GET_PARAMETER volume`. |
| 7.4 | How does a receiver report a volume change made on its own buttons? No reference sender receives such an event; shairport only declares `ap2_event_send_unit_volume_notification` [sps:ap2_event_message_handler.h:8]. | PLAN.md's volume model reads device-side changes back via the event channel. | During a session press the SpeakerA's volume buttons; log every message on the event channel and poll `GET_PARAMETER volume` every second to see whether the dB value changes. If nothing arrives, the bridge must poll. |
| 7.5 | Which `streamID` does the SpeakerA return in the stream SETUP response, and does a per-stream TEARDOWN with it (or an empty dict) stop playback cleanly? shairport returns none [sps:rtsp.c:2911-2913]. | Needed for clean track-source switching and for the teardown diagram. | Inspect the SETUP response; send `TEARDOWN {streams:[{streamID,type}]}` then `TEARDOWN {}`; check `Connection: close` and that the next session starts without 451/453. |
| 7.6 | Does a receiver with feature bit 51 (`SupportsUnifiedPairSetupAndMFi`, set on the LG TV) require `Method = 1` (PairSetupWithAuth) in M1, and does the SpeakerA (bit 51 clear) reject it? airplay2-receiver treats method 1 + transient as MFi [ap2r:hap.py:521-530]; pyatv and pair_ap always send method 0 [pyatv:hap.py:54; sps:pair_homekit.c:1272]. | Determines the M1 byte for the PIN flow on the TV (cannot be tested now). | Against the SpeakerA only: send M1 with method 0 and method 1 on two connections and compare M2. Document the TV case for later with the user present. |
| 7.7 | Does the SpeakerA honour `latencyMin`/`latencyMax` and `Audio-Latency`, and what end-to-end delay results for type 96 vs 103? | Group sync and the "~2 s latency" assumption in PLAN.md. | Measure time from first RTP packet to audible output with a click track for `latencyMin` 11025 and 22050; compare with a 103 stream later. |
| 7.8 | What does the SpeakerA return to `POST /feedback` (body or empty) and does `keepAliveSendStatsAsBody: true` mean it expects a bplist body from us? How long until it drops an idle session without feedback? | Keep-alive design. | Send feedback every 2 s and log the body; then stop sending and measure when the control or event socket closes. Try a feedback body `{}` and `{streams:[]}`. |
| 7.9 | Is DMAP metadata (bit 17) actually displayed by the SpeakerA (it has no screen) and does the LG TV, which also lacks bit 50, show DMAP text/artwork on its AirPlay overlay? | Decides whether to invest in artwork scaling. | Send `mlit{minm,asar,asal}` + JPEG and look at the SpeakerA app / Spotify-like companion UI. TV check deferred. |
| 7.10 | Does the receiver answer `GET /info` with `initialVolume` once paired (it does not unauthenticated, 1.8), or must the sender seed the level from `GET_PARAMETER volume`? | Start-up volume for the volume model. | Repeat `GET /info` inside the encrypted channel; then `GET_PARAMETER volume`. |
| 7.11 | Does the SpeakerA require `X-Apple-HKP` on the pairing POSTs (shairport ignores it; owntone says "Required!!" [owntone:airplay.c:2886-2890])? | Header hygiene. | Send transient M1 without the header on one connection; compare. |
| 7.12 | Group behaviour: after a PTP session SETUP with `groupUUID`, does the SpeakerA change its `gid` TXT value (as shairport does [sps:rtsp.c:2663-2669]) and does a second receiver in the same group need `isMultiSelectAirPlay`/`groupContainsGroupLeader`? | M3 groups. | Start two sessions with the same `groupUUID`; browse mDNS and compare `gid`/`gcgl`; test with and without the two booleans. |
| 7.13 | Event-channel timing: does the SpeakerA send `updateInfo` on connect (shairport) or after RECORD (Apple TV), and does it close the channel if our 200 is late? | Event-channel reader design. | Log timestamps of the first event-channel message relative to SETUP/RECORD; delay the answer by 5, 20, 40 s. |
| 7.14 | Does the SpeakerA send retransmit requests (0xd5) on the control port and accept 0xd6 answers, and does it ever send anything else there? | Control-port UDP handler scope. | Drop every 50th RTP packet deliberately and capture the control port. |
| 7.15 | Does the SpeakerA accept an encrypted control-channel frame larger than 1024 bytes from the sender (2.5), and how large are the frames it sends us (event channel `updateInfo`, `/info` reply)? pyatv's notes say any size that fits 16 bits is used in practice [protocols.md:1555-1556]; no source shows what Apple-SDK receivers accept on *read*. | Tells how strict receivers are about the cap and confirms that our receive path must accept large frames; the 1024-byte send cap stays fixed whatever the outcome (an unknown receiver may well enforce it). | Wave 3, no audio: on a throw-away connection after pairing, send one `GET /info` encrypted as a single 2048-byte-padded frame (a test-only switch on `HapFrameCipher`, never in the library's send path) and record whether a 200 or a dropped connection follows; log the length field of every frame received during a normal session. |

---

## Appendix A. Message index

| Message | Direction | Section |
|---|---|---|
| mDNS `_airplay._tcp`, `_raop._tcp` | R → S | 1.1-1.5 |
| `GET /info` (stage 1 with qualifier, stage 2 bare) | S → R | 1.8, 4.1 |
| `POST /pair-pin-start` | S → R | 3.1 |
| `POST /pair-setup` M1/M3/M5 | S → R | 3.5, 3.6 |
| `POST /pair-verify` M1/M3 | S → R | 3.7 |
| `POST /auth-setup`, `POST /fp-setup`, `POST /pair-setup-pin` | S → R | 3.10 (not needed) |
| `SETUP` (session) | S → R | 4.2 |
| event channel connect, `POST /command` | R → S | 4.3, 6.5 |
| `SETUP` (stream 96/103) | S → R | 4.4 |
| `RECORD` | S → R | 4.5 |
| `SETPEERS`, `SETPEERSX` | S → R | 4.6 |
| `SETRATEANCHORTIME` | S → R | 4.7 |
| `FLUSH`, `FLUSHBUFFERED` | S → R | 4.8 |
| `TEARDOWN` (stream, session) | S → R | 4.9 |
| `POST /feedback` | S → R | 4.10 |
| `OPTIONS`, `GET_PARAMETER`, `POST /command`, `POST /audioMode`, `/configure` | S → R | 4.11 |
| `SET_PARAMETER` volume / DMAP / image / progress | S → R | 5 |

## Appendix B. Live probe scripts (2026-10-09)

Both ran with Python 3.14 standard library only (no `zeroconf`, no `dns-sd` on the host):

- `mdns_browse.py <local ip> <seconds>`: multicasts one PTR query for `_airplay._tcp.local` and
  `_raop._tcp.local` to 224.0.0.251:5353 (twice, 300 ms apart), collects PTR/SRV/TXT/A/AAAA answers for the
  given time and prints them as JSON. Output saved as `docs/airplay2/probes/browse.json`.
- `get_info.py <ip> [port]`: three `GET /info` variants (RTSP/1.0 bare, HTTP/1.1 bare, RTSP/1.0 with the
  `qualifier` bplist) on separate TCP connections, decoded with `plistlib`. Output saved as
  `docs/airplay2/probes/get_info_SpeakerA.txt` and reproduced in 1.8.

No other request was sent to any receiver. The LG TV was only observed passively through mDNS.

## Appendix C. Known gaps in this document

- PTP itself (clock sync packets on ports 319/320, `ClockID`/`ClockPorts` semantics) is data-plane and not
  described; only the control-plane fields that carry PTP parameters are.
- The `POST /command` bplist metadata path (feature bit 50) is not described; neither LAN receiver needs it.
- The remote-control data channel (stream type 130) is referenced for its key derivation only.
- Password-protected receivers (flags bit 7) are described from owntone's reading only.
