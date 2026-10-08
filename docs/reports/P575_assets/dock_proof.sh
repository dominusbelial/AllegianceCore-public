#!/usr/bin/env bash
# P575 — real-window player autopilot dock proof via the new native bay approach.
# Boots the 221 server, launches the WINDOWED MonoGame client, auto-launches the
# player ship, then --auto-dock-after engages the autopilot to the home station's
# landing bay (DockApproach) and real 2560x1440 backbuffer shots capture the
# line-up -> entry -> hangar sequence. Exactly the P572 real-window discipline
# (never --capture/offscreen).
set -uo pipefail
DOTNET=/home/domingo/.dotnet/dotnet
REPO=/home/domingo/Development/AllegianceCore
ART=/home/domingo/Development/Artwork
PORT=${1:-6111}
OUT=${2:-/home/domingo/Development/AllegianceCore/docs/reports/P575_assets/dock_samesector}
MAP=m2xspiderw.igc
AFTER=${3:-5}          # seconds after launch before engaging the dock autopilot
SHOTS=${4:-"10,13,16,19,22,25,28,31,34"}
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
echo "[p575] out=$OUT after=${AFTER}s shots=$SHOTS"

SRV=$REPO/src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server
CLI=$REPO/src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame
[ -x "$SRV" ] || { echo "no release server"; exit 2; }

cat > "$OUT/server.json" <<EOF
{ "artPath": "$ART", "staticCore": "PCore001.igc", "port": $PORT, "updatesPerSecond": 20,
  "games": [ { "name": "P575", "map": "$MAP", "teams": 2, "civIds": [ 1, 3 ], "sideNames": [ "Alpha", "Bravo" ] } ],
  "liveSettings": { "testHarness": { "enabled": true, "characterNamePrefix": "proof-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    "adminCheats": { "enabled": true, "allowNames": [ "proof-*" ] }, "debugMode": true } }
EOF

PIDS=()
cleanup(){ for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done; sleep 1; for p in "${PIDS[@]}"; do kill -9 "$p" 2>/dev/null; done; }
trap cleanup EXIT INT TERM

"$SRV" --config "$OUT/server.json" > "$OUT/server.out" 2>&1 &
SRV_PID=$!; PIDS+=("$SRV_PID")
for i in $(seq 1 60); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && { exec 3>&- 3<&-; break; }; sleep 1; done
echo "[p575] server up :$PORT"

export ALLEGIANCE_ARTWORK_PATH="$ART"; export MONOGAME_GL_PROFILE=OpenGL
DISPLAY="${DISPLAY:-:0}" WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}" \
  "$CLI" 127.0.0.1 "$PORT" proof \
  --auto-side 0 --auto-ready --auto-launch "Adv Scout" --auto-dock-after "$AFTER" \
  --camera chase --autoshots "$SHOTS" \
  --autoshot-dir "$OUT" --exit-after 55 --window 2560x1440 > "$OUT/client.out" 2>&1 &
CLIENT_PID=$!; PIDS+=("$CLIENT_PID")
echo "[p575] client pid $CLIENT_PID (log: $OUT/client.out)"
for i in $(seq 1 90); do kill -0 "$CLIENT_PID" 2>/dev/null || break; sleep 1; done
echo "[p575] client exited; PNGs:"
ls -la "$OUT"/*.png 2>/dev/null | awk '{print $9, $5}'
echo "[p575] autopilot/dock log lines:"
grep -iE "autopilot|dock|arrived|hangar" "$OUT/client.out" | tail -30
