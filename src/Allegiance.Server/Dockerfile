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

# Native AOT prerequisites (https://aka.ms/nativeaot-prerequisites): the SDK
# image does not ship the Linux platform linker. Install clang + zlib headers
# so the AOT publish can compile and link the self-contained native binary.
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

# Restore first with only the project files (plus the root Directory.Build.props,
# which sets TargetFramework/Nullability) so dependency layers cache well.
# The restore step must resolve the SAME package graph the publish step below
# will need (P185): a restore with just -r linux-x64 skips
# Microsoft.NET.ILLink.Tasks entirely, and the later --no-restore publish then
# fails with NETSDK1064 ("package ... was not found") — confirmed live, first
# real build of this Dockerfile end-to-end. The publish step gets this via its
# --self-contained / -p:PublishAot=true CLI flags; `restore` (unlike
# `build`/`publish`) does NOT accept --self-contained as a CLI switch (MSB1001
# "Unknown switch" — also confirmed live), so it must be passed as the
# equivalent MSBuild property, -p:SelfContained=true, alongside -p:PublishAot=true.
COPY Directory.Build.props .
COPY src/Allegiance.Server/Allegiance.Server.csproj src/Allegiance.Server/
COPY src/Allegiance.Core/Allegiance.Core.csproj src/Allegiance.Core/
# P810 — the server self-update stages from the shared update library.
COPY src/Allegiance.Update/Allegiance.Update.csproj src/Allegiance.Update/
RUN dotnet restore src/Allegiance.Server/Allegiance.Server.csproj \
    -r linux-x64 -p:SelfContained=true -p:PublishAot=true

# Copy the sources and publish. Allegiance.Core is pulled in transitively.
# P185: Native AOT — self-contained linux-x64 single binary (no managed runtime
# needed at run time). The restore above pins linux-x64 so the RID runtime pack
# is available to --no-restore. Multi-arch (arm64 etc.) is out of scope: linux-x64
# only, matching the existing single-arch build.
COPY src/Allegiance.Server/ src/Allegiance.Server/
COPY src/Allegiance.Update/ src/Allegiance.Update/
COPY src/Allegiance.Core/ src/Allegiance.Core/
COPY data/showcase-plans/ data/showcase-plans/
RUN dotnet publish src/Allegiance.Server/Allegiance.Server.csproj \
    -c Release -r linux-x64 --self-contained -p:PublishAot=true -o /app --no-restore

# ---- final stage -----------------------------------------------------------
# Native runtime-deps image (NOT aspnet/runtime): the published artifact is a
# self-contained native AOT binary, so only the base OS-level libraries are
# needed — there is no managed .NET runtime to ship. The 10.0 tag floats on the
# .NET 10 release train (same family as the SDK above).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Documentation only — the real port is set by the mounted config's `port`.
EXPOSE 6073

# Requires a config file and artwork at runtime; see the header comment. Invokes
# the self-contained native AOT binary directly (P185) — no `dotnet` launcher.
ENTRYPOINT ["./Allegiance.Server"]
