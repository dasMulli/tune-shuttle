# Firewall and network

| Port | For |
|---|---|
| UDP 5353 (multicast) | mDNS: Spotify and Apple devices find the bridge, the bridge finds the receivers |
| TCP 8080 (`Kestrel:Endpoints:Http:Url`) | The web app and the API |
| TCP, any free port (`Bridge:ZeroconfPort`) | Spotify Connect's Zeroconf handoff |
| TCP 7000 (`Bridge:Airplay:RtspPort`) | The AirPlay receiver: Apple devices connect here |
| UDP 319/320 (`Bridge:Airplay:PtpEventPort`, `PtpGeneralPort`) | PTP: the clock of Apple senders (receiver role) and of PTP receivers (sender role) |
| UDP and TCP, chosen at run time | The sender's timing, control and RTP ports; the receiver's per-session audio, control and event ports |

- **Same network.** Spotify finds the bridge, and the bridge finds the receivers, by mDNS (UDP 5353, multicast). Both
  need the same LAN or an mDNS reflector between VLANs. A receiver the bridge has seen before is reached at its last
  address even when mDNS is blocked.
- **Inbound UDP.** Receivers send NTP timing requests and retransmit requests to the bridge on UDP ports chosen at
  run time, and PTP receivers talk to its PTP clock on UDP 319/320. If the timing requests are blocked, an NTP
  receiver such as a third-party AirPlay 2 speaker (srcvers 366.0, features 0x445F8A00,0x1C340) answers the stream setup with `400` after about 30 s. The Windows install script adds a
  rule for `bridge.exe` that covers these ports; on Linux, allow the bridge's inbound UDP from the LAN.
- **The AirPlay receiver** needs TCP 7000 and UDP 319/320 reachable from the Apple devices, plus its per-session
  ports. The Windows install script adds "TuneShuttle AirPlay RTSP" and "TuneShuttle PTP" (the program rule covers
  the per-session ports); on Linux allow them from the LAN. If UDP 319/320 cannot be bound (another PTP daemon, or no
  `CAP_NET_BIND_SERVICE`), the receiver still plays, with anchor-only timing.
- **Spotify Zeroconf** needs its HTTP port reachable from the phones: any free port by default, fixed with
  `Bridge:ZeroconfPort`.
- **The web port** (8080 by default) for the browser.
- **The CLI** needs the same inbound UDP. On Windows, allow `airplay2.exe` inbound on the private profile (or run it
  with `dotnet run`):

  ```powershell
  New-NetFirewallRule -DisplayName "airplay2 CLI" -Program "$PWD\artifacts\airplay2\airplay2.exe" -Direction Inbound -Action Allow -Profile Private
  ```

- **Containers** need a macvlan interface (see [Installing and running](INSTALL.md)); NAT does not work.
