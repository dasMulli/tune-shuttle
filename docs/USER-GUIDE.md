# Using TuneShuttle

The web app and the AirPlay receiver. Back to the [overview](../README.md).

## Using the web app

The web app works on phones, tablets and desktops. Its pages: **Player**, **Receivers**, **Groups** and **Settings**
(with **Diagnostics**).

**Spotify.** In the Spotify app on the same network, open the device picker and choose "TuneShuttle" (rename it under
Settings). The first time, this hands the login over to the bridge (Spotify's Zeroconf handoff). The login is cached,
so after a restart the device is back without another handoff; *Forget Spotify login* under Settings removes it.
Spotify's play, pause, skip, seek and volume act on the speakers, and the Player page shows the track and has its own
transport buttons. While no speaker is selected, the bridge accepts playback and discards the audio.

**Receivers** lists every AirPlay receiver the bridge finds on the network, with its state, its level and, under
*Details*, its model, address and a latency offset (in frames; use it when one speaker plays ahead of or behind the
others). *Connect* makes a receiver the output on its own; *Disconnect* stops it.

**Pairing.** Most speakers pair without any input (transient pairing). A receiver that requires a PIN shows "PIN
needed": press *Pair with PIN* (or *Connect*), the speaker shows a 4-digit PIN, and the web app opens a dialog to enter
it. The pairing is stored, so later connects need no PIN; *Forget pairing* removes it. A wrong PIN can be retried with
*Start again*.

**Forgetting receivers.** An offline receiver says when it was last seen. *Forget* removes it with its pairing, group
memberships and settings (after a confirmation), and *Forget all offline receivers* does that for every offline one.
Online receivers cannot be forgotten, because discovery would bring them back within seconds. Offline receivers that
carry nothing of yours (no pairing, no group, not the output, default level and offset) are forgotten automatically
after 3 days; Settings changes that (0 = never).

**Player and output.** The Player page shows the track, the transport buttons, the master volume and the **Output**
card, where you pick what plays: one receiver, a group, or nothing (*Stop output*).

**Groups.** A group plays to several receivers in sync. Create one (*New group*), add members, set each member's level,
then *Activate* it. The group has a master level G and each member a level r_i. Moving the master scales every member
by the same factor, so the balance between rooms stays as you set it (like Apple devices); a member that reaches 100
stays there, and the balance comes back when you turn down again. The Spotify volume slider drives the master level.
Changing a member's level, in the web app or on the speaker itself, changes only that member.

**Settings** holds the Spotify device name, the sender latency L (how far ahead the receivers get the audio; default
77 175 frames = 1.75 s), the stream type (Realtime, or Buffered for receivers that support it), the receiver volume
poll, the "forget offline receivers after" days, and **Appearance**: the theme (light, dark or system) and the language
(Deutsch, English, Français, Español, Русский, Українська, Português (Brasil); the browser's language until you choose
one). The theme and the language are stored in the browser; the top bar has a theme picker and, on wider screens, a
language select. The **AirPlay receiver** card turns the receiver on or off and sets its name and password (see
"AirPlay receiver"). **Diagnostics** shows the status, the bridge's addresses and ports, packet statistics and a live log.

## AirPlay receiver

TuneShuttle advertises itself on the network as one AirPlay 2 speaker. On an iPhone or iPad (iOS 17 or later) or a
Mac (macOS 14 or later) on the same network, pick "TuneShuttle" in Control Center's AirPlay menu or behind an app's
AirPlay button: what the device plays comes out of the output selected on the Player page (one receiver or a
synchronized group), as with Spotify.

- **Volume, pause, seek and track changes** are made on the Apple device; its volume drives the master level of the
  output. The Player page shows the title, artist, album, artwork and progress the device sends, with the chip
  "AirPlay · *device name*". Its transport buttons are disabled while AirPlay plays: the Apple device is the remote.
- **The newest source wins.** When an Apple device starts streaming, Spotify is paused and AirPlay takes the speakers.
  When someone then presses play in Spotify, the AirPlay session ends (the Apple device shows the speaker as
  disconnected). A second Apple device replaces the first one the same way. Spotify paused by AirPlay stays paused
  until someone presses play.
- **Settings > AirPlay receiver**: turn the receiver on or off, set its name (by default the Spotify device name) and
  a password. Without a password anyone on the network can play; with one, Apple devices ask for it once and remember
  the pairing. A name or password change reaches the AirPlay menu within seconds and does not interrupt a session.
- **Diagnostics > AirPlay receiver** shows the state, the connected device, the stream type and format, the buffer,
  late and missing frames, the PTP offset and whether the AAC decoder loaded.
- **Not supported:** AirPlay 1 (iTunes for Windows), screen mirroring and video, 48 kHz or surround formats (the
  Apple device converts), controlling the Apple device from the web app or a speaker's buttons, and playing in step
  with other speakers of an iOS multi-speaker selection (TuneShuttle plays, but a fraction of a second behind them).

**One AirPlay receiver per machine.** The receiver uses TCP 7000 and UDP 319/320, and Apple devices are confused by two
AirPlay 2 receivers at one address; do not run another AirPlay receiver (shairport-sync, for example) on the same
host. TuneShuttle's own sender shares the PTP ports with the receiver.
