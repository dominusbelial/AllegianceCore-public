# syntax=docker/dockerfile:1
#
# Allegiance.Server — containerized self-hosted game server.
#
# Two things this image does NOT bake in, by design — a real deployment must
# supply them at runtime via bind-mounts:
#
#   (a) THE ARTWORK TREE. The .igc/.cvh corpus is a large, separate, external
#       asset checkout that is deliberately NOT in the git repo (it is never
#       needed to build or publish, only to actually run). `ServerConfig.ArtPath`
#       must point at an existing directory at Boot() time (`ValidatePaths`
#       throws if it does not), so the image ships with NO artwork and you must
#       bind-mount it:
#
#           docker run ... \
#             -v /host/path/to/Artwork:/artwork:ro \
#             -e ... allegianceserver \
#             --config /run/secrets/server.json
#
#       and your config's `artPath` must be the in-container path, e.g.:
#           "artPath": "/artwork"
#
#   (b) A CONFIG FILE. The app resolves `server.json` relative to its working
#       directory (Program.cs), overridable with `--config <path>`. We ship NO
#       config in the image (src/Allegiance.Server/server.json is the maintainer's
#       personal dev file; server.lobby-example.json is the sample to copy from),
#       so mount one (or pass --config) and set its `artPath` to the mounted
#       artwork path and its `lobby` block to point at your Allegiance.Lobby.
#
# The transport binds 0.0.0.0 (all interfaces), so no transport code change is
# needed for the container to be reachable from outside. The port the server
# listens on is CONFIG-DRIVEN (the `port` field, default 6073). EXPOSE below is
# documentation only — it does not enforce anything. If your config uses a
# different port, map the host port with `-p` accordingly.

# ---- build stage -----------------------------------------------------------
# SDK pinned to the exact documented dev version (10.0.400), not a floating
# 10.0 tag, for reproducible builds.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

# Restore first with only the project files (plus the root Directory.Build.props,
# which sets TargetFramework/Nullability) so dependency layers cache well.
COPY Directory.Build.props .
COPY src/Allegiance.Server/Allegiance.Server.csproj src/Allegiance.Server/
COPY src/Allegiance.Core/Allegiance.Core.csproj src/Allegiance.Core/
RUN dotnet restore src/Allegiance.Server/Allegiance.Server.csproj

# Copy the sources and publish. Allegiance.Core is pulled in transitively.
COPY src/Allegiance.Server/ src/Allegiance.Server/
COPY src/Allegiance.Core/ src/Allegiance.Core/
RUN dotnet publish src/Allegiance.Server/Allegiance.Server.csproj \
    -c Release -o /app --no-restore

# ---- final stage -----------------------------------------------------------
# Plain .NET runtime image (NOT aspnet) — this is a plain console app (Sdk=
# Microsoft.NET.Sdk, OutputType Exe); no ASP.NET Core needed. Pinned to the
# 10.0.x patch that ships with the SDK above.
FROM mcr.microsoft.com/dotnet/runtime:10.0.11 AS final
WORKDIR /app
COPY --from=build /app .

# Documentation only — the real port is set by the mounted config's `port`.
EXPOSE 6073

# Requires a config file and artwork at runtime; see the header comment.
ENTRYPOINT ["dotnet", "Allegiance.Server.dll"]
