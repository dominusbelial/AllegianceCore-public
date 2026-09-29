# syntax=docker/dockerfile:1
#
# vizfight — containerized spectator/test-bot tool, for running it on the same
# box as a real deployed Allegiance.Server (so its web dashboard is reachable
# on that box's own LAN address instead of a developer workstation).
#
# Framework-dependent (not Native AOT, unlike Allegiance.Server's Dockerfile):
# vizfight is a dev/test tool, not a production deployable, and its ASP.NET
# Core Kestrel/WebSockets usage adds AOT constraints not worth taking on here.
#
# Needs, at runtime (not baked into the image):
#   (a) THE ARTWORK TREE — bind-mount and point ALLEGIANCE_ARTWORK_PATH at the
#       in-container path, e.g. -v /host/Artwork:/artwork:ro -e
#       ALLEGIANCE_ARTWORK_PATH=/artwork
#   (b) network reach to the real game server it attaches to (host networking
#       is the simplest way to reach a server also running with
#       network_mode: host on the same box).
#
# Usage (attach mode — see Program.cs's own usage text for the full CLI):
#   docker run --network host -v /host/Artwork:/artwork:ro \
#     -e ALLEGIANCE_ARTWORK_PATH=/artwork vizfight \
#     attach 127.0.0.1 6073 7799 15 15

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

COPY Directory.Build.props .
COPY tools/vizfight/vizfight.csproj tools/vizfight/
COPY src/Allegiance.Core/Allegiance.Core.csproj src/Allegiance.Core/
COPY src/Allegiance.Server/Allegiance.Server.csproj src/Allegiance.Server/
COPY src/Allegiance.Bot/Allegiance.Bot.csproj src/Allegiance.Bot/
COPY src/Allegiance.ClientCore/Allegiance.ClientCore.csproj src/Allegiance.ClientCore/
RUN dotnet restore tools/vizfight/vizfight.csproj

COPY tools/vizfight/ tools/vizfight/
COPY src/Allegiance.Core/ src/Allegiance.Core/
COPY src/Allegiance.Server/ src/Allegiance.Server/
COPY src/Allegiance.Bot/ src/Allegiance.Bot/
COPY src/Allegiance.ClientCore/ src/Allegiance.ClientCore/
# Allegiance.Server.csproj (a project reference) hard-codes PublishAot=true,
# which implies SelfContained — a self-contained referenced project cannot be
# referenced by a non-self-contained one (NETSDK1151). Override both globally
# for this publish; command-line -p: properties win over the referenced
# project's own <PropertyGroup> values.
RUN dotnet publish tools/vizfight/vizfight.csproj -c Release -o /app --no-restore \
    -p:PublishAot=false -p:SelfContained=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Documentation only — real ports come from the CLI args passed to `docker run`.
EXPOSE 7799

ENTRYPOINT ["dotnet", "vizfight.dll"]
