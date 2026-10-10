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
RUN dotnet restore src/Bridge.Service/Bridge.Service.csproj -a $TARGETARCH
RUN dotnet publish src/Bridge.Service/Bridge.Service.csproj -c Release -a $TARGETARCH --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The Kestrel endpoint is set through the bridge's own configuration (BRIDGE_ prefix, same key appsettings.json uses),
# because a Kestrel:Endpoints entry overrides ASPNETCORE_URLS.
ENV BRIDGE_DataDirectory=/data \
    BRIDGE_Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
VOLUME ["/data"]
EXPOSE 8080/tcp
# mDNS 5353/udp, the Zeroconf HTTP port and the sender's RTP/timing/control ports are dynamic on the pod interface.
ENTRYPOINT ["dotnet", "bridge.dll"]
