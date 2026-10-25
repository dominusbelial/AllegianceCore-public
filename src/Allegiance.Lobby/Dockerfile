# syntax=docker/dockerfile:1
#
# Allegiance.Lobby — directory and persistent identity issuer. No external
# asset tree, no required config file (LobbySettings knobs — MaxEntries,
# SweepIntervalSeconds, MaxAgeSeconds — are bound from the optional "Lobby"
# config section / env vars and have baked-in defaults; see LobbySettings.cs).
# The one deployment knob that matters is which URL Kestrel binds, controlled
# by the ASPNETCORE_URLS env var below.

# ---- build stage -----------------------------------------------------------
# Same pinned SDK as Allegiance.Server (documented dev version 10.0.400).
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

COPY Directory.Build.props .
COPY src/Allegiance.Lobby/Allegiance.Lobby.csproj src/Allegiance.Lobby/
COPY src/Allegiance.Core/Allegiance.Core.csproj src/Allegiance.Core/
# P810 — the lobby's /api/update/info types live in the shared update library.
COPY src/Allegiance.Update/Allegiance.Update.csproj src/Allegiance.Update/
RUN dotnet restore src/Allegiance.Lobby/Allegiance.Lobby.csproj

COPY src/Allegiance.Update/ src/Allegiance.Update/
COPY src/Allegiance.Lobby/ src/Allegiance.Lobby/
COPY src/Allegiance.Core/ src/Allegiance.Core/
# Web-SDK publish pulls in the ASP.NET Core shared framework reference and the
# wwwroot/ static content automatically.
RUN dotnet publish src/Allegiance.Lobby/Allegiance.Lobby.csproj \
    -c Release -o /app --no-restore

# ---- final stage -----------------------------------------------------------
# ASP.NET Core runtime image (NOT the base runtime) — this project uses the Web
# SDK (Sdk="Microsoft.NET.Sdk.Web"), so it needs Microsoft.AspNetCore.App at
# runtime. Pinned to the 10.0.x patch shipping with the SDK above.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS final
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

# Bind Kestrel to all interfaces on port 8080. Program.cs sets no URL itself
# (no UseUrls / no hardcoded ASPNETCORE_URLS), so without this the framework
# default (http://localhost:5000) would bind loopback-only and the container
# would be unreachable from outside. Override on the command line / compose
# environment if you want a different port.
ENV ASPNETCORE_URLS=http://+:8080

# Documentation only (the real mapping is `-p` / compose `ports`).
EXPOSE 8080

ENTRYPOINT ["dotnet", "Allegiance.Lobby.dll"]
