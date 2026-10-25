#!/usr/bin/env bash
#
# Fly ONE experiment in a local match and record the session (docs/EXPERIMENTS.md).
#
#   scripts/fly-experiment.sh <experiment> [options]
#
# Experiments (each is off by default everywhere else):
#   baseline   nothing switched on — the control flight
#   smoothing  P813b  client --remoteHermite (remote-ship Hermite smoothing)
#   reconnect  P813c  server reconnect grace + client auto-reconnect (ALLEGIANCE_RECONNECT_GRACE_SECONDS)
#   tiers      P813a  server broadcast.interestTiers (distance-tiered ship updates, big-change promotion)
#   reliable   P813d  server transport.reliableOneShot (one-shot messages ahead of position streams)
#   gotoplan   P817   server sim.nativeGotoPlan (native miner/builder travel)
#   gunners    P814b  bot host ALLEGIANCE_GUNNERS=on (bots crew turret seats; gunship slot)
#
# Options:
#   --bots <N>        bots per side via the MCP driver (default 6; gunners/gotoplan want some)
#   --map <file>      map file (default m2xspiderw.igc)
#   --map-gen <TYPE> --map-seed <N>   generated map instead (reproducible with the seed)
#   --video           also record the screen (GNOME Shell screencast, .webm in the run folder)
#   --no-build        reuse existing Release binaries
#
# Flow: writes the run folder .scratch/experiments/<UTC time>-<experiment>/ (git-ignored), starts the dev stack
# (server + lobby + bots) with exactly one experiment on, launches the client, and waits for you to close the client.
# Then it collects the recording — the client's session-performance.json (P789 recorder), the server/lobby/bot logs,
# the config and the git commit — tears the stack down, and appends a row to docs/experiments/FLIGHT_LOG.md.
# Write your impressions into the run folder's notes.md (or tell Claude) and fill in the row's verdict.
set -uo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-$(command -v dotnet)}"; [ -z "$DOTNET" ] && DOTNET="$HOME/.dotnet/dotnet"
if [ -f "$REPO/local.env" ]; then set -a; . "$REPO/local.env"; set +a; fi
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"

EXP="${1:-}"; shift || true
BOTS=6; MAP="m2xspiderw.igc"; MAP_GEN=""; MAP_SEED=""; VIDEO=0; NO_BUILD=""
while [ $# -gt 0 ]; do
  case "$1" in
    --bots) BOTS="$2"; shift 2;;
    --map) MAP="$2"; shift 2;;
    --map-gen) MAP_GEN="$2"; shift 2;;
    --map-seed) MAP_SEED="$2"; shift 2;;
    --video) VIDEO=1; shift;;
    --no-build) NO_BUILD="--no-build"; shift;;
    *) echo "unknown option: $1"; exit 2;;
  esac
done

# Per-experiment switches: a server.json fragment, environment for server/bots/client, client arguments.
FRAGMENT=""; CLIENT_ARGS=""; FLAGS=""
case "$EXP" in
  baseline)  FLAGS="(none)";;
  smoothing) CLIENT_ARGS="--remoteHermite"; FLAGS="client --remoteHermite";;
  reconnect) FRAGMENT='"reconnect": { "enabled": true, "graceSeconds": 5 },'
             export ALLEGIANCE_RECONNECT_GRACE_SECONDS=5
             FLAGS="server reconnect.enabled=true graceSeconds=5; ALLEGIANCE_RECONNECT_GRACE_SECONDS=5";;
  tiers)     FRAGMENT='"broadcast": { "interestTiers": true, "promotionEpsilon": 200 },'
             FLAGS="server broadcast.interestTiers=true promotionEpsilon=200";;
  reliable)  FRAGMENT='"transport": { "reliableOneShot": true },'
             FLAGS="server transport.reliableOneShot=true";;
  gotoplan)  FRAGMENT='"sim": { "nativeGotoPlan": true },'
             FLAGS="server sim.nativeGotoPlan=true";;
  gunners)   export ALLEGIANCE_GUNNERS=on; FLAGS="bot host ALLEGIANCE_GUNNERS=on";;
  ""|-h|--help) sed -n 2,30p "$0" | sed 's/^# \{0,1\}//'; exit 0;;
  *) echo "unknown experiment '$EXP' (try --help)"; exit 2;;
esac

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
RUN="$REPO/.scratch/experiments/$STAMP-$EXP"
mkdir -p "$RUN"
COMMIT="$(git -C "$REPO" rev-parse --short HEAD)$(git -C "$REPO" diff --quiet || echo '+dirty')"
NAME="fly-$EXP"

# Server config: the dev-stack defaults plus exactly one experiment fragment.
if [ -n "$MAP_GEN" ]; then
  seed_json=""; [ -n "$MAP_SEED" ] && seed_json="\"seed\": $MAP_SEED,"
  GAME="{ \"name\": \"Flight $EXP\", \"mapGen\": { \"type\": \"$MAP_GEN\", $seed_json }, \"teams\": 2, \"civIds\": [ 1, 3 ], \"sideNames\": [ \"Alpha\", \"Bravo\" ] }"
else
  GAME="{ \"name\": \"Flight $EXP\", \"map\": \"$MAP\", \"teams\": 2, \"civIds\": [ 1, 3 ], \"sideNames\": [ \"Alpha\", \"Bravo\" ] }"
fi
cat > "$RUN/server.json" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": 6073,
  "updatesPerSecond": 20,
  $FRAGMENT
  "games": [ $GAME ],
  "liveSettings": {
    "testHarness": { "enabled": true, "characterNamePrefix": "mcp-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    "debugMode": true
  }
}
EOF
python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$RUN/server.json" || { echo "bad server.json"; exit 1; }

cat > "$RUN/run.json" <<EOF
{ "experiment": "$EXP", "flags": "$FLAGS", "commit": "$COMMIT", "startedUtc": "$STAMP",
  "botsPerSide": $BOTS, "map": "${MAP_GEN:-$MAP}", "mapSeed": "${MAP_SEED}", "video": $VIDEO }
EOF
printf '# Flight %s — %s\n\nCommit %s · flags: %s\n\n## What I noticed\n\n- \n\n## Verdict (keep / drop / needs work)\n\n- \n' \
  "$STAMP" "$EXP" "$COMMIT" "$FLAGS" > "$RUN/notes.md"

echo "[fly] experiment=$EXP flags: $FLAGS"
echo "[fly] run folder: $RUN"

# Stack (server + lobby + bots) — the client is launched below so it can take the experiment's arguments.
STACK_ARGS=(up --name "$NAME" --config "$RUN/server.json" $NO_BUILD)
[ "$BOTS" -gt 0 ] && STACK_ARGS+=(--bots "$BOTS")
"$REPO/scripts/dev-stack.sh" "${STACK_ARGS[@]}" || { echo "[fly] dev-stack failed"; exit 1; }
STACK_LOGS="$REPO/.scratch/dev-stack/$NAME.logs"

CLIENT_EXE="$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame"
if [ -z "$NO_BUILD" ]; then
  "$DOTNET" build -c Release -m:8 -v:m "$REPO/src/Allegiance.ClientCore.MonoGame/Allegiance.ClientCore.MonoGame.csproj" \
    > "$RUN/build-client.log" 2>&1 || { echo "[fly] client build failed"; "$REPO/scripts/dev-stack.sh" down --name "$NAME"; exit 1; }
fi

PERF="${XDG_CONFIG_HOME:-$HOME/.config}/AllegianceCore/session-performance.json"
START_EPOCH=$(date +%s)
if [ "$VIDEO" -eq 1 ]; then
  gdbus call --session --dest org.gnome.Shell.Screencast --object-path /org/gnome/Shell/Screencast \
    --method org.gnome.Shell.Screencast.Screencast "$RUN/flight" "{}" > "$RUN/video.log" 2>&1 \
    && echo "[fly] recording the screen to $RUN/flight.webm" || echo "[fly] screen recording unavailable (see video.log)"
fi

echo "[fly] launching the client — fly, then close the client window to finish and record the flight"
export ALLEGIANCE_ARTWORK_PATH="$ART"
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLIENT_EXE" 127.0.0.1 6073 "pilot" --auto-ready --window 1280x720 $CLIENT_ARGS > "$RUN/client.out" 2>&1
CLIENT_RC=$?

# Collect the recording.
[ "$VIDEO" -eq 1 ] && gdbus call --session --dest org.gnome.Shell.Screencast --object-path /org/gnome/Shell/Screencast \
  --method org.gnome.Shell.Screencast.StopScreencast >> "$RUN/video.log" 2>&1
if [ -f "$PERF" ] && [ "$(stat -c %Y "$PERF")" -ge "$START_EPOCH" ]; then
  cp "$PERF" "$RUN/session-performance.json"
else
  echo "(no match ended during this flight — the P789 recorder saves at match end)" > "$RUN/session-performance.missing"
fi
[ -d "$STACK_LOGS" ] && cp -r "$STACK_LOGS" "$RUN/stack-logs"
"$REPO/scripts/dev-stack.sh" down --name "$NAME" > /dev/null 2>&1
grep -hE "^\[(update-age|reliable|reconnect|gunner|boot)\]" "$RUN/stack-logs/server.out" 2>/dev/null | tail -200 > "$RUN/experiment-telemetry.log"

LOG="$REPO/docs/experiments/FLIGHT_LOG.md"
mkdir -p "$(dirname "$LOG")"
printf '| %s | %s | %s | %s | %s | `.scratch/experiments/%s` | _pending_ |\n' \
  "$(date -u '+%Y-%m-%d %H:%M')" "$EXP" "$COMMIT" "$BOTS" "$( [ -f "$RUN/session-performance.json" ] && echo yes || echo no )" \
  "$STAMP-$EXP" >> "$LOG"
echo "[fly] done (client exit $CLIENT_RC). Recorded in $RUN"
echo "[fly] add your impressions to $RUN/notes.md and the verdict in docs/experiments/FLIGHT_LOG.md (or tell Claude)"
