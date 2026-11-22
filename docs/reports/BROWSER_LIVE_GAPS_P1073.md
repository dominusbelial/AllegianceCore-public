# P1073 — browser gaps on the live perpetual battle

Status: implemented and gated in the working tree; the browser harness is written and
syntax-checked but not executed on this lane (no Chromium). Final test counts, pack sizes and the
workstation commands are below.

## Scope and method

The live 2026-11-22 perpetual battle (P1071 preset) exercised the browser against **generated** maps and showcase
`techMode: "research"` for the first time. This lane addresses the five gaps in `P1073_BRIEF.md`:

1. research briefing dropped on every join (fmid 992);
2. generated-map aleph/planet textures rejected by the browser pack store;
2b. invisible station types and one white (untextured) asteroid;
3. the hangar Launch pane falling back to text because `animlaunch` is not packed;
4. `PWR 0%` while `AFTERBURNER ACTIVE`;
5. guards for the PointList class of failure plus a generated-map browser harness.

Native first: every native-behaviour claim below cites `FreeAllegiance/Allegiance` `file:line`. `ResearchBriefing`
and the browser packs are port-only extensions and are labelled as such.

### Environment

- Repo base `6bc0dade` (the KNI PointList hotfix).
- `ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork`, `ALLEGIANCE_NATIVE_SRC_PATH=/srv/corpus/Allegiance`.
- This lane has **no Chromium/Playwright installation**, so `tools/p1073-browser-generated.cjs` is written and
  syntax-checked but not run here. Claude runs it and opens every capture on the workstation (below).

## Item 1 — research briefing

### Finding

`ResearchBriefing` is a port-only wire message (`src/Allegiance.Core/Wire/ResearchBriefing.cs:12`; native reads its
research plan from its own captured objects, so native has no fmid 992 counterpart). It is
Brotli-compressed (`:31`, `:44`). Browser-WASM .NET ships no Brotli codec, so `BrotliStream` throws
`PlatformNotSupportedException`; the P975 containment path drops the message and keeps the session.

The port already solved the identical problem for the mission briefing (`MissionBriefingInfo`, fmid 996): the
browser stages the resident P1051 runtime Brotli decoder and injects a JS-backed decode delegate
(`ConnectionSession.BrowserMissionBriefingDecoder`, `src/Allegiance.ClientCore/Session/ConnectionSession.cs:60,1879`).
P1073 extends that established path to fmid 992 instead of inventing a second mechanism.

### Codec proof in the real browser

`Menu.ProbeCodecs()` (new) reports, in the actual WASM runtime, whether `BrotliDecoder.TryDecompress`,
`BrotliStream`, `DeflateStream` and `GZipStream` accept a real briefing frame. The existing
`ProbeBrotli` hook only reported Brotli. The generated-map harness calls `gameMenu.probeCodecs()` and records
the result. (Result on this lane: not run — no Chromium; see the end of the report.)

### Chosen option

**Option (b)/(c): the already-shipped resident runtime decoder**, driven through the same per-join staging the
mission briefing uses. No server change, no new package, no wire-format change, no transport sniffing.

- The decoder is Google Brotli's MIT-licensed objects from the installed .NET runtime, adapted by
  `tools/p1051-brotli-runtime.py`; licence/size are unchanged from P1051 (no new dependency).
- Desktop clients keep receiving and decoding exactly the same Brotli frame they receive today.
- `ResearchBriefing.Decode` stays the desktop/reference implementation and gains a size-bound test.

## Item 2 — generated-map textures

### Finding

`WarpRenderer.LoadTextureFile` forms `<stem>bmp.mdl` (`src/Allegiance.ClientCore.Game/Rendering/WarpRenderer.cs:394`)
and, when that name is not resident, calls `FindCaseInsensitive` (`:457`), which enumerates `"."`. The browser
`StagedAssetSource.Enumerate` canonicalises its argument (`Assets/StagedAssetSource.cs:29`), and
`StagedManifest.Canonical(".")` fails its allow-list regex → `InvalidDataException: Invalid asset name.` That
exception is what the live log reported; it is swallowed by the optional-asset guard (`WarpRenderer.cs:417-427`)
and the soft fallback is used. The generated map's actual texture names (`plnt19`, `plnt42`, the `nebplnt*`
planet posters, and the `alephbmp` fallback) were also simply not in any browser pack.

### Fix

- `StagedAssetSource.Enumerate` treats `"."`/`""` as the root, so the documented IAssetSource root path works and
  `Canonical` keeps its exact security rules (it is a boundary for the staged pack store).
- `MissionMapGenerator` emits: `PlaneTexture`s `plnt19`/`plnt42` (alephs,
  `src/Allegiance.Core/MapGen/MissionMapGenerator.cs:47-48`), planet posters from `MapGenContext.PlanetNames`
  (`MapGenContext.cs:47-53`), `globe1..8` environment domes (`MissionMapGenerator.cs:385,410`) and asteroid
  models/textures from `NativeAsteroidTypes`. A shared `GeneratedMapArt` list (Core) makes that set enumerable.
- `tools/p1066-scene-packs.py` gains per-family on-demand packs (`mapgen-planet-*`, `mapgen-globe-*`,
  `mapgen-warp-*`, `mapgen-rock-*`), so the join payload is unchanged and a generated sector fetches only the art
  it actually uses.

## Item 2b — stations and asteroids

### Finding

`p1065-battle-packs.core_roots` packed core record kinds 29 (hull) and 23 (missile) only. Station types are native
`OT_stationType` (31), `DataStationTypeIGC` (`src/Igc/igc.h:2649`, port layout
`src/Allegiance.Core/Data/DataStationTypeIGC.cs:251-282`): model name at offset 8, texture at 486, station type id
at 446. The browser station renderer skips any station whose mesh is not resident
(`src/Allegiance.ClientCore.Game.Browser/Game1.Flight.cs:548`), which is why Domingo could dock at an invisible
station.

The white asteroid is the native rock texture path: asteroid rows carry an empty `textureName`
(`src/Allegiance.Core/Data/NativeAsteroidTypes.cs`), so native falls back to the model's own imported texture
(`NativeAsteroidRenderer.TextureStemFor` → `NativeShipResolver.ResolveTextureStem`). If the model is packed but its
`bgrndNNbmp` texture is not, `NativeAsteroidRenderer.Draw` sets `TextureEnabled = false` and the rock renders
lit-solid — the white/blue rock in the capture.

### Fix

- `core_roots` now reads kind 31 and emits one `station-<typeId>` closure per station type, including its model and
  `textureName` (and `ImportImageFromFile` dependencies).
- `mapgen-rock-<model>`/`static-rock-<model>` closures carry both the rock mesh and its native surface; the
  browser requests them for the rocks in the current sector.
- New request path: the browser requests station/rock/warp/sky packs for the current and prefetched sector through
  the same `BattlePackStreamer` hulls use (`Menu.Sectors.RefreshSectorStreaming` → `Game1.RequestSectorArt`).
- `NativeSkyRenderer`/`PlanetPoster` forget logged misses when a verified battle pack is promoted, so a texture that
  arrives after the first frame is loaded instead of remaining permanently "missing".

## Item 3 — `animlaunch`

Desktop resolves `animlaunch.mdl` through `HangarArt.ResolvePath` (`src/Allegiance.ClientCore.Game/Rendering/HangarArt.cs:127-131`).
`tools/p1066-scene-packs.py` now packs `scene-hangar` (`animlaunch.mdl`, `animlaunchbmp.mdl`); the browser requests it
when the hangar is visible and invalidates cached missing art on promotion.

## Item 4 — PWR while the afterburner is active

### Native

The native ship update writes the mounted afterburner's live power ramp into the wire `power` byte:
`pshipupdate->power = p ? BytePercentage(p->GetPower()) : 0`
(`src/Igc/shipIGC.h:1137`), where `p` is `m_mountedOthers[ET_Afterburner]`. The client applies a remote value back
to that part (`src/Igc/shipIGC.cpp:3080`); the afterburner part itself ramps in `afterburnerIGC.cpp`. The native HUD
has no separate "PWR" text; this port's top-centre `PWR` label is a port HUD element whose value source is that same
native quantity.

### Bug and fix

The port hardcoded `ServerShipUpdate` byte 46 to `1.0` (`src/Allegiance.Core/Wire/ServerShipUpdate.cs:176`) and did
the same in `HeavyShipUpdateEntry` (`HeavyShipsUpdate.cs:158`), while the server ship already simulates the native
ramp (`Ship._afterburnerPower`, `src/Allegiance.Core/Sim/Ship.cs:9564-9570`). Fixes:

- `Ship.AfterburnerPower` exposes the ramp; both wire writers now send it.
- The own-ship HUD reads the local simulation's ramp while prediction is active (native's owning client simulates its
  own afterburner), falling back to the server byte otherwise. Shared code, so desktop and browser both change;
  Classic and Modern both consume the same `HudFrame.Power`.

## Item 5 — guards and harness

- `BrowserStarDots.Expand` shape/count unit test.
- A source-scan test rejects any `PrimitiveType.PointList` in a browser-reachable renderer that is not inside a
  `#if !BROWSER` block. `NativeSkyRenderer.cs:241` is converted to the browser line-dot path.
- `tools/p1073-browser-generated.cjs` boots the existing host on a generated `SingleRing` map with showcase research,
  joins as a guest, flies 30 s in Classic and Modern at 1920×1080 and 800×480, and asserts every requirement in the
  brief (page errors, malformed lines, warp texture lines, research briefing reception, station/asteroid residency,
  visible-station docking, hangar launch art, captures).

## Tests and gate results

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **succeeded, 0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 152 tool projects built**, 0 warnings/0 errors |
| Existing pack regressions `tools/test-p1065-battle-packs.py` / `tools/test-p1066-scene-packs.py` (fixtures extended for the new families) | **3 passed ×3 / 3 passed ×3** |
| New `ResearchBriefingWireTests` alone ×3 | **4 passed ×3** (header/round-trip; largest authored plan fits one ushort frame; invalid frames rejected) |
| New `P1073AfterburnerPowerTests` alone ×3 | **1 passed ×3** (real artwork; needs `ALLEGIANCE_ARTWORK_PATH`) |
| New `P1073BrowserRenderGuards` + `P1073GeneratedMapArtTests` alone ×3 | **9 passed ×3** |
| New `tools/p1073-pack-coverage-tests.py` alone ×3 | **3 passed ×3** (real core + artwork; skips cleanly without the corpus) |
| `Allegiance.ClientCore.Browser.Tests` | **52 passed** |
| `Allegiance.ClientCore.Game.Browser.Tests` | **227 passed** |
| Core wire tests (`FullyQualifiedName~.Wire.`) | **389 passed** |
| Full `tools/run-tests.sh --tier fast` (final tree) | 15,966 tests / 561.2 s. No assertion failure in the changed code. `MonoGame.Tests` green (6,007 passed, 4 not executed), `ClientCore.Tests` green (2,490 passed), both browser projects green (52 + 227). `Core.Tests` has exactly three failures, all already-ledgered live-timing families (`AsteroidMinedLiveTests.No_broadcast_fires_once…`, `MissileBroadcastTests.Directly_fired…`, `P855TrapHackTests.Out_of_range_wing_id…`), plus three calibrated 10 s limits from the same slow NPC/loadout set recorded in earlier reports. |

Known/flaky failures seen across the sweeps and re-run isolated: the pre-existing
`AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` mining-receipt family,
`AsteroidCombatLiveTests.Same_side_friendly_missile…`, `MissileBroadcastTests.Directly_fired…`,
`P855TrapHackTests.Out_of_range_wing_id…`, and
`BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights(killPod: True, botCount: 3)`
pod-relaunch family — all already in `docs/TEST_FLAKES.md` and unrelated to this lane (they pass
isolated and touch no changed code path). No new flake is added.

The power-byte change is deliberate native parity, so four pinned transcripts were regenerated and noted
in their tests: `P991FlagOffWirePinTests`/`P993FlagOffWirePinTests` (off-mode digest),
`BroadcastSnapshotTests.Recorded_ticks...` (four native/parallel goldens) and the exact-byte
`GameMessageRoundTripTests.SetCluster...` power byte (240 → 0 for a ship with no afterburner mounted).
The single/heavy captured-snapshot path was completed too (`ShipWireSnapshot.Power`), so every producer
agrees.

## Pack sizes (measured from a fresh local publication)

Generated with `scripts/publish-browser-flight.sh` against `/srv/corpus/Artwork` and the checked-in P1051
traces (`/srv/corpus/Artwork`, P1054 sector traces, P1065 hull/missile/station closures, P1066 scene/
mapgen closures). The generation validates and publishes successfully.

| Content | Files / count | Gzip bytes |
|---|---:|---:|
| Join base (unchanged) | 601 files / 577 unique | 7,564,835 |
| Browser runtime (`_framework`, unchanged) | — | 6,239,044 |
| Join + runtime transfer | — | **13,803,879** of a 14,680,064 budget |
| Station packs (new, one per core `OT_stationType`) | 143 packs | min 19,549 · p25 133,415 · median 1,406,790 · p75 3,184,147 · max 9,784,590 per pack |
| `mapgen-planet-*` (new) | 24 planet posters | 233,721 – 3,909,878 each (raw 8,388,728) |
| `mapgen-globe-*` (new) | 8 domes | 662,474 – 1,202,023 each |
| `mapgen-warp-*` (new) | 3 | `plnt19bmp` 361,699 · `plnt42bmp` 369,205 · `alephbmp` 260 |
| `mapgen-rock-*` (new) | 7 native rock closures | bgrnd03 58,518 · 05 42,898 · 51 2,570,773 · 52 74,574 · 53 3,493,151 · 55 62,960 · 56 2,223,861 |
| `static-rock-*` (new) | 31 authored-map models | min 4,984 · median 74,574 · max 6,135,886 each (bgrnd57 4,306,076; ss307 4,018,868) |
| `scene-hangar` (new) | 2 files | 298,646 |
| Battle index (`battle-manifest.json`) | 872 assets / 727 packs | 493,935 raw (the P1065 index was 237,446 before the station/mapgen/static-rock packs) |

`battle-receipt.json`: **143 station types, 143 covered** (no missing station model); 179/179 hull models
still covered; `joinUnchanged: true`. `scene-receipt.json`: 344 additional verified original scene files,
104,699,294 gzip across every mapgen/scene/static-rock pack (these are alternatives, not a first-flight total). The 31 `static-rock-*` packs cover
every asteroid model authored in a static `.igc` map (31 distinct models: the seven native rocks plus
`bgrnd57`, `ss307`, `cap101`, `fig21`, ...); they are requested only when that model is in the sector.

"First flight" delta depends on the generated sector, but is no longer a fixed base cost: the browser
requests only that sector's families. A representative SingleRing home sector (2 station packs, 1 planet,
1 globe, 1 warp funnel, 2 rock families, `scene-hangar`) is roughly **9–17 MB gzip** on top of the
unchanged 13.8 MB join+runtime transfer; the harness records the exact
`gameMenu.metrics().battlePacks.promotedBytes` and `residentBytes`. The join manifest and its bytes are
byte-identical before/after this lane (`joinUnchanged: true`); no resident file was moved into the join.

## Browser harness (for Claude on the workstation)

This lane has **no Chromium/Playwright installation**, so the harness is written and `node --check`ed but
not run, and no capture exists here. Run it exactly like P1068:

```bash
set -euo pipefail
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1073/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork
export BROWSER_CLIENT_VERSION=2.0.0
NPM_DIR="<existing P1051/P1053 npm directory>"
test -d "$NPM_DIR/node_modules/playwright"
python3 - <<'PY'
import gzip, json
from pathlib import Path
traces = json.loads(gzip.decompress(Path('docs/reports/BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz').read_bytes()))
for name, trace in traces.items():
    target = Path('.scratch/p1073/traces') / name
    target.parent.mkdir(parents=True, exist_ok=True); target.write_text(json.dumps(trace))
PY
TRACE="$PWD/.scratch/p1073/traces"
GENERATION="$PWD/.scratch/p1073/generation"   # fresh directory
scripts/publish-browser-flight.sh "$GENERATION" "$TRACE/opens/Classic.json" "$TRACE/opens/Modern.json"   "$TRACE/flight-opens4/Classic.json" "$TRACE/flight-opens4/Modern.json"   "$TRACE/flight-miners0/Classic.json" "$TRACE/flight-miners1/Modern.json"
for project in tools/p1051-browser-host src/Allegiance.ClientCore.MonoGame src/Allegiance.ClientCore.Browser; do
  "$DOTNET" build "$project" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
done
STATE_DIR=$(mktemp -d "$PWD/.scratch/p1073/generated-host.XXXXXX")
"$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll   "$ALLEGIANCE_ARTWORK_PATH" "$GENERATION" "$STATE_DIR"   --guests 1 --seconds 900 --map-gen SingleRing --map-seed 1073 > "$STATE_DIR/host.log" 2>&1 &
host_pid=$!
trap 'touch "$STATE_DIR/stop"; wait "$host_pid" || true' EXIT INT TERM
for attempt in $(seq 1 300); do [[ -f "$STATE_DIR/state.json" ]] && break; kill -0 "$host_pid"; sleep .1; done
node tools/p1073-browser-generated.cjs "$NPM_DIR" "$STATE_DIR" \
  docs/reports/BROWSER_LIVE_GAPS_P1073_assets "$GENERATION"
```

The harness writes `browser-generated.json` plus the captures `hangar-launch.jpg`,
`flight-classic-1920x1080.jpg`, `flight-modern-1920x1080.jpg`, `flight-classic-800x480.jpg`,
`flight-modern-800x480.jpg` and `docked-hangar.jpg`, and fails closed on any page error, any
`[recv] … malformed` line, any `[warp] texture … unavailable` line, a missing research briefing, a
non-resident home-sector station/asteroid model or texture, a docking that ends at an invisible station,
a missing hangar Launch animation, or an afterburner PWR reading of 0 while active.

`tools/p1051-browser-host` gained `--map-gen SingleRing|PinWheel|DoubleRing` and `--map-seed N`; the
generated-map host also selects `showcase.techMode = "research"`. This is a proof-tool extension only,
not a server configuration or default change.

## Unfinished / deliberately not done

- **The real-browser proof (codec matrix, generated-map captures, visual descriptions) is for Claude.**
  This lane has no Chromium; no claim is made that a capture was opened or visually described.
- The join base still comes from the P1051 traces recorded on `XXV_simple`, so four rock surfaces
  (`bgrnd03bmp`, `05`, `52`, `55`) are resident as the P1054 **256-square first-frame entry copies**.
  The generated-map rock packs reuse those resident names (the stager cannot override an immutable
  resident source), so those four rocks are textured but at entry-copy resolution; `51`, `53`, `56` and
  any non-resident rock now stream the original surface. Removing the entry cap would be a P1054 policy
  change and is not done here.
- The browser's black-sky tier intentionally draws no `globeN`/`environmentNbmp` dome, so the globe
  packs are shipped for completeness and for the shared/native tiers rather than requested by the
  current browser sky.
- The existing `docs/parity/parity-matrix.csv` SHIP_UPDATE row (24) now records the P1073 power source
  (`Igc/shipIGC.h:1137`) and the new test; no new game rule was introduced.

## Candidates noticed (not in scope)

- `NativeSkyRenderer.DrawStars` still allocates a `VertexPositionColor[]` per frame on desktop and
  browser; the P1073 guard only fixes the unsupported primitive.
- `p1054-browser-packs.py` entry caps remain a fixed-sector artifact; a generated-map generation would
  benefit from a "no entry cap when the map is generated" pack rule (separate task).
- `PlanetPoster` logs a missing planet once per name; with the new invalidation it retries only when a
  pack promotion bumps the content revision, which is correct but means a genuinely absent asset is
  logged once per revision in that sector.

## Native intent / Modern approach

**Native intent.** The original client reads ships, stations, rocks, warp funnels and sky art from one local artwork
folder; the ship update carries the mounted afterburner's live power so every peer draws the native ramp; a station or
rock whose surface cannot be loaded is still a solid native model. The browser is a different delivery container, not a
different game rule.

**Modern approach.** The browser keeps the native data sources and renderers, but splits artwork into verified,
content-addressed on-demand packs. P1073 extends that existing pipeline to the generated-map art set, station types and
native rock surfaces, reuses the already-shipped resident Brotli decoder for the port-only research briefing, makes the
afterburner power value flow from the same server simulation native exposes, and adds a generated-map harness plus
static/unit guards for the WebGL point-primitive trap.
