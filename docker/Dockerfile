# Linux container image of the bridge (docs/PLAN.md "Deployment"). Build from the repository root:
#   docker build -f docker/Dockerfile -t airplay2-bridge .
# Run with a macvlan interface so mDNS and inbound UDP work (Kubernetes: a macvlan/multus pod interface):
#   docker network create -d macvlan --subnet=192.0.2.0/24 --gateway=192.0.2.1 -o parent=eth0 lan
#   docker run -d --network lan --ip 192.0.2.60 -v bridge-data:/data airplay2-bridge
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props .editorconfig AirplayBridge.sln ./
COPY librespot-dotnet/ librespot-dotnet/
COPY src/ src/
RUN dotnet restore src/Bridge.Service/Bridge.Service.csproj
RUN dotnet publish src/Bridge.Service/Bridge.Service.csproj -c Release -o /app --no-restore

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
