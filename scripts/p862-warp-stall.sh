#!/usr/bin/env bash
#
# P862 — the sector-warp stall harness.
#
# Flies the REAL windowed client through an aleph into Alderan (1052) — the
# asteroid sector Domingo's 200v200 showcase dipped to ~18 fps entering — and
# records per-frame CPU and GPU time, the allocation burst and what the entry
# frames loaded. With live-proof's debug topology the side-0 pilot starts in
# Omega (1053), which has a direct aleph to Alderan and NO asteroids, so every
# rock model in the destination is new. Use --park Alderan with MCP bots: native
# discovery is side-wide, so friendly ships in the target sector are what make the
# client know its contents BEFORE the warp (as in the live showcase).
#
# It extends the existing proof paths rather than a new one:
#   * scripts/live-proof.sh boots the local server + windowed client (P572),
#   * the client's own --auto-ready/--auto-launch/--auto-goto automation (P572),
#   * the P643 per-frame CSV (ALLEGIANCE_CLIENT_PERF=1 + ALLEGIANCE_CLIENT_PERF_CSV),
#   * the P862 [sector-entry] / [sector-warm] log lines (Game1.SectorLoad.cs).
#
# Usage:
#   scripts/p862-warp-stall.sh <label> [options]
#
#   --out <dir>         output dir (default .scratch/p862/<label>-<ts>)
#   --no-prewarm        set ALLEGIANCE_SECTOR_PREWARM=0 (the A/B baseline: assets
#                       load lazily on first draw, exactly as before P862)
#   --hd                export ALLEGIANCE_HD_PACK_PATH (the private HD pack, when
#                       present) so the HD material path is exercised too
#   --bots <tool json>  hand to live-proof.sh --bots (e.g. a showcase spawn)
#   --bots-after <s>    live-proof.sh --bots-after
#   --map <map.igc>     map (default m2xspiderw.igc)
#   --goto <sector>     goto target (default Alderan; "" to skip)
#   --target <kind>     --auto-target kind after launch (aleph|enemy|station|...)
#   --park <sector>     --auto-park-bots: relocate the side's harness bots there
#                       before the warp (side-wide discovery → the pilot knows the
#                       destination sector, like the live 200v200 showcase)
#   --client-extra "<f>" extra client flags (appended)
#   --window <WxH>      client window (default 1280x720 — a small window keeps the
#                       CPU-side asset cost dominant; use 2560x1440 for GPU share)
#   --exit-after <s>    client exit deadline after launch (default 120)
#   --shots             also capture real-backbuffer frames around the change
#                       (ALLEGIANCE_SECTOR_SHOTS=<out>/shots; perturbs the frames
#                       it saves, so measurement runs leave it off)
#   --hud-settings <f>  hud.json for the client (Classic|Modern proof frames)
#   --port <port>       server port (default 6073)
#
# Prints the summary produced by scripts/p862-warp-summary.py.
set -uo pipefail

LABEL="${1:-run}"; shift || true
REPO="$(cd "$(dirname "$0")/.." && pwd)"
OUT=""
PREWARM=1
HD=0
BOTS=""
BOTS_AFTER=0
MAP="m2xspiderw.igc"
GOTO="Alderan"
TARGET=""
PARK=""
EXTRA_CLIENT=""
WIN="1280x720"
EXIT_AFTER=120
SHOTS=0
HUD_SETTINGS=""
PORT=6073

while [ "$#" -gt 0 ]; do
  case "$1" in
    --out) OUT="$2"; shift 2;;
    --no-prewarm) PREWARM=0; shift;;
    --hd) HD=1; shift;;
    --bots) BOTS="$2"; shift 2;;
    --bots-after) BOTS_AFTER="$2"; shift 2;;
    --map) MAP="$2"; shift 2;;
    --goto) GOTO="$2"; shift 2;;
    --target) TARGET="$2"; shift 2;;
    --park) PARK="$2"; shift 2;;
    --client-extra) EXTRA_CLIENT="$EXTRA_CLIENT $2"; shift 2;;
    --window) WIN="$2"; shift 2;;
    --exit-after) EXIT_AFTER="$2"; shift 2;;
    --shots) SHOTS=1; shift;;
    --hud-settings) HUD_SETTINGS="$2"; shift 2;;
    --port) PORT="$2"; shift 2;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done

if [ -f "$REPO/local.env" ]; then
  set -a
  # shellcheck disable=SC1091
  source "$REPO/local.env"
  set +a
fi
ART="${ALLEGIANCE_ARTWORK_PATH:-$REPO/../Artwork}"
if [ "$HD" -eq 1 ] && [ -z "${ALLEGIANCE_HD_PACK_PATH:-}" ]; then
  # The private HD pack normally sits next to the artwork checkout (see
  # docs/P601_MESHY_PRIVATE_PACK.md for the layout); an explicit
  # ALLEGIANCE_HD_PACK_PATH always wins.
  for cand in "$(dirname "$ART")/AllegianceCore-hd/pack/allegiance-hd-v1" \
              "$REPO/../AllegianceCore-hd/pack/allegiance-hd-v1"; do
    if [ -d "$cand/textures" ]; then export ALLEGIANCE_HD_PACK_PATH="$cand"; break; fi
  done
fi

if [ -z "$OUT" ]; then
  OUT="$REPO/.scratch/p862/${LABEL}-$(date +%Y%m%d-%H%M%S)"
fi
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
LOG="$OUT/client.out"
CSV="$OUT/perf.csv"

export ALLEGIANCE_CLIENT_PERF=1
export ALLEGIANCE_CLIENT_PERF_CSV="$CSV"
# P862 — an occluded Xwayland window gets its frame callbacks throttled to ~1 Hz,
# which pins the measured "frame time" to 1 s and hides everything else. The
# harness measures real cost, so vsync is off unless the caller says otherwise.
export ALLEGIANCE_NOVSYNC="${ALLEGIANCE_NOVSYNC:-1}"
# A vsync-less loop otherwise runs at thousands of fps (the server SHIP_RESETs such
# a pose stream); cap it at a realistic cadence so per-frame cost is measurable.
export ALLEGIANCE_FRAME_CAP="${ALLEGIANCE_FRAME_CAP:-60}"
if [ "$PREWARM" -eq 0 ]; then export ALLEGIANCE_SECTOR_PREWARM=0; else unset ALLEGIANCE_SECTOR_PREWARM; fi
if [ "$SHOTS" -eq 1 ]; then
  mkdir -p "$OUT/shots"
  export ALLEGIANCE_SECTOR_SHOTS="$OUT/shots"
else
  unset ALLEGIANCE_SECTOR_SHOTS
fi

echo "[p862] label=$LABEL prewarm=$PREWARM hd=${ALLEGIANCE_HD_PACK_PATH:-none}"
echo "[p862] map=$MAP goto=$GOTO window=$WIN exit_after=${EXIT_AFTER}s out=$OUT"
echo "[p862] perf csv=$CSV"

# The map must be visible to the pilot for a scripted goto to route: native
# bShowMap/bShowHomeSector (Igc/igc.h:1123/:1148) default false (a side scouts its
# alephs), which leaves a scripted autopilot with no warp to fly to. This is the
# showcase/test-world setting, not a client change.
SERVER_CFG="$OUT/server.json"
cat > "$SERVER_CFG" <<EOF
{
  "artPath": "$ART",
  "staticCore": "PCore001.igc",
  "port": $PORT,
  "updatesPerSecond": 20,
  "games": [
    { "name": "P862 warp stall", "map": "$MAP", "teams": 2, "civIds": [ 1, 3 ],
      "sideNames": [ "Alpha", "Bravo" ], "showMap": true, "showHomeSector": true }
  ],
  "liveSettings": {
    "testHarness": { "enabled": true, "characterNamePrefix": "mcp-", "money": 500000, "ignoreTechGate": true },
    "testUnlock": { "enabled": true, "allTech": true, "money": 500000 },
    "adminCheats": { "enabled": true, "allowNames": [ "mcp-*", "proof*" ] },
    "debugMode": true
  }
}
EOF

CLIENT_EXTRA="--auto-launch"
[ -n "$GOTO" ] && CLIENT_EXTRA="$CLIENT_EXTRA --auto-goto $GOTO"
[ -n "$TARGET" ] && CLIENT_EXTRA="$CLIENT_EXTRA --auto-target $TARGET"
[ -n "$PARK" ] && CLIENT_EXTRA="$CLIENT_EXTRA --auto-park-bots $PARK"
CLIENT_EXTRA="$CLIENT_EXTRA$EXTRA_CLIENT"
LIVE_ARGS=(--out "$OUT/live" --map "$MAP" --auto-side 0 --autoshots "$((EXIT_AFTER + 5))"
           --exit-after "$EXIT_AFTER" --window "$WIN" --port "$PORT" --chase
           --server-config "$SERVER_CFG" --client-extra "$CLIENT_EXTRA")
[ -n "$BOTS" ] && LIVE_ARGS+=(--bots "$BOTS" --bots-after "$BOTS_AFTER")
[ -n "$HUD_SETTINGS" ] && LIVE_ARGS+=(--hud-settings "$HUD_SETTINGS")

bash "$REPO/scripts/live-proof.sh" "${LIVE_ARGS[@]}" > "$OUT/harness.log" 2>&1
# live-proof keeps the client log under its own --out; lift the two artifacts
cp -f "$OUT/live/client.out" "$LOG" 2>/dev/null || true
tail -3 "$OUT/harness.log"

if [ ! -s "$CSV" ]; then echo "[p862] FAILED: no perf csv ($CSV)"; exit 1; fi
python3 "$REPO/scripts/p862-warp-summary.py" "$LOG" "$CSV" --label "$LABEL" --json "$OUT/summary.json"
echo "[p862] artifacts: $OUT (client.out, perf.csv, summary.json, live/)"
