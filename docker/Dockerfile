# Linux container image of TuneShuttle (docs/ARCHITECTURE.md, "Deployment"). Build from the repository root:
#   docker build -f docker/Dockerfile -t tune-shuttle .
# Cross-build for arm64, e.g. a Raspberry Pi (the SDK stage runs natively on the build host and cross-compiles;
# only the runtime stage is pulled for the target platform, so nothing runs under QEMU):
#   docker buildx build --platform linux/arm64 -f docker/Dockerfile -t registry.example.com/tune-shuttle:latest --push .
# Run with a macvlan interface so mDNS and inbound UDP work (Kubernetes: a macvlan/multus pod interface):
#   docker network create -d macvlan --subnet=192.0.2.0/24 --gateway=192.0.2.1 -o parent=eth0 lan
#   docker run -d --network lan --ip 192.0.2.60 -v bridge-data:/data tune-shuttle
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props .editorconfig TuneShuttle.sln ./
COPY librespot-dotnet/ librespot-dotnet/
COPY src/ src/
# Airplay2.Receiver embeds the captured /info template (SPEC-receiver.md 3.2); .dockerignore lets only this file of docs/ in.
COPY docs/airplay2/test-vectors/info-response.bplist docs/airplay2/test-vectors/
RUN dotnet restore src/Bridge.Service/Bridge.Service.csproj -a $TARGETARCH
RUN dotnet publish src/Bridge.Service/Bridge.Service.csproj -c Release -a $TARGETARCH --no-restore -o /app

# The ASP.NET Core 10 runtime, taken from Microsoft's image for the target platform (used below by COPY --from).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS aspnet

# Runtime stage on Debian 13 (trixie) instead of the aspnet image itself (SPEC-receiver.md 7.2, Section 10). Verified
# 2026-10-26 by running the images: `docker run --rm mcr.microsoft.com/dotnet/aspnet:10.0 cat /etc/os-release` is
# Ubuntu 24.04.5 LTS (noble), whose `apt-cache policy` offers only FFmpeg 6.1 (libavcodec60 7:6.1.1-3ubuntu5,
# libavutil58, libswresample4), not the FFmpeg 7.1 that FFmpeg.AutoGen 7.1.1 binds (Directory.Packages.props). On
# debian:trixie-slim (Debian GNU/Linux 13) `apt-cache policy` offers libavcodec61, libavutil59 and libswresample5 at
# 7:7.1.5-0+deb13u1, the same majors as tools/get-ffmpeg.ps1's avcodec-61/avutil-59/swresample-5 DLLs on Windows.
# The .NET runtime is copied from the aspnet image; the other packages are the .NET runtime dependencies (no ICU:
# globalization is invariant below). Without libavcodec the receiver runs ALAC-only (SPEC-receiver.md R3, R4).
FROM debian:trixie-slim AS runtime
RUN apt-get update     && apt-get install -y --no-install-recommends         ca-certificates libc6 libgcc-s1 libssl3t64 libstdc++6 tzdata zlib1g         libavcodec61 libavutil59 libswresample5     && rm -rf /var/lib/apt/lists/*
COPY --from=aspnet /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
WORKDIR /app
COPY --from=build /app .
# The Kestrel endpoint is set through the bridge's own configuration (BRIDGE_ prefix, same key appsettings.json uses),
# because a Kestrel:Endpoints entry overrides ASPNETCORE_URLS.
ENV BRIDGE_DataDirectory=/data     BRIDGE_Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080     DOTNET_RUNNING_IN_CONTAINER=true     DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
VOLUME ["/data"]
EXPOSE 8080/tcp
# The AirPlay receiver (SPEC-receiver.md 10): RTSP on Bridge:Airplay:RtspPort and PTP on UDP 319/320, shared by the
# receiver's PTP slave and the sender's grandmaster. The image runs as root, so the privileged PTP ports bind.
EXPOSE 7000/tcp 319/udp 320/udp
# mDNS 5353/udp, the Zeroconf HTTP port, the sender's RTP/timing/control ports and the receiver's per-session data,
# control and event ports are dynamic on the pod interface.
ENTRYPOINT ["dotnet", "bridge.dll"]
