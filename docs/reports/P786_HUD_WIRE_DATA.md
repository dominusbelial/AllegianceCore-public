# P786 (PAR-5) — HUD wire data: the native values the client HUD needs but does not receive

Branch `pi/p786`, worktree of the public `AllegianceCore` master at dispatch
(`6ef6d9bf`). No commits — everything is in the working tree.

## 0. Summary

Six HUD rows left `partial` by P782's re-audit were closed end to end, and the
seventh (cloak drained state) was reclassified with evidence. The unifying
finding, checked per value against the native client, is that **native gets
every one of these values client-side from state the wire already carries** —
no new wire field was needed anywhere:

| # | Row | Native source & rule | How the port gets it now |
|---|-----|----------------------|--------------------------|
| 1 | Signature percentage | `pship->GetSignature()` = accumulated part signature × live `m_cloaking` ramp (`WinTrek/console.cpp:865-866`; `Igc/shipIGC.h:268-270`; part gating `weaponIGC.h:103-122`, `afterburnerIGC.h:78/89`, `cloakigc.h:76/89`) | The existing owner-only `OwnShipCloakUpdate(220)` stream already carries the continuous `GetCloaking()` fraction; `OwnStatusData.SignaturePercent` now multiplies by it instead of the engaged 0 end state (fallback: engaged bit → 0 when no sample has arrived) |
| 2 | Launcher arming progress | `PartWrapper::GetReadyState` case 2 + `GetArmedFraction` (`WinTrek/consoledata.cpp:1157-1177,1189-1202`; `Igc/magazineigc.h:156-165` — `1 − max(0,(m_timeLoaded−now)/loadTime)`; fire resets `m_timeLoaded = now + GetLoadTime()`, `magazineigc.cpp:116`; mount stamps `lastUpdate + 1/mountRate + loadTime`, `magazineigc.h:73`) | Native computes it on the **client** from its own magazine sim. The renderer-only client replays the same clocks from wire state it already receives: the own `FM_CS_FIRE_MISSILE(70)` salvo count (`ClientWorld.OwnMissilesFired`), own dispenser creation EXPORTs (new `ClientWorld.OwnDispenserDeployed` counter, from `DataProbeExport.ShipId`/`DataMineExport.LauncherId`), and the loadout's `FractionMounted` transitions (`OwnLauncherArming.cs`). `HudInventoryPaneBuilder` now returns `ReadyState 2` + the row's `ArmedFraction`; both renderers fill the `invsmissile.mdl:69` arming bar with it |
| 3 | Radar probe/mine lifetime fill | `GetTimeFraction` (`WinTrek/radarimage.cpp:1073-1082`; `Igc/probeigc.h:271-279`, `mineigc.h:183-191`) — `(m_timeExpire − now)/(m_timeExpire − m_time0)`, `m_timeExpire = m_time0 + lifespan` | **The wire already carried `m_time0`** (`probeigc.cpp:542`/`mineigc.cpp:167` → `DataProbeExport.Time0`/`DataMineExport.Time0`); the client entities dropped it. `ProbeEntity`/`MineEntity` now keep `Time0ServerSeconds`, and the radar contact fill evaluates the fraction against the server clock sample (`ClientWorld.SimulationTimeSeconds`, PING-seeded) with the type lifespan from the loaded core |
| 4 | Radar missile radius | Range/LOD clip runs on the **surface separation**: `separation -= radiusSource + pmodel->GetRadius()` (`radarimage.cpp:626-636`); a missile's radius is its type's (`missileigc.cpp` Load) | The missile type id already rides `FIRE_MISSILE(70)`/`MISSILE_UPDATE(205)`; the contact's `ClipSeparation` now subtracts `DataMissileTypeIGC.Radius` from the loaded core instead of 0 |
| 5 | Ore seen per side | Radar asteroid + target ore fill = `GetOreSeenBySide/(2·capacity)` (`radarimage.cpp:1051-1069`; `capacity` = miner shipload `FloatConstants[c_fcidCapacityHe3]·GA[c_gaMiningCapacity]`, `radarimage.cpp:576-577`), drawn only when the side-known ore is non-zero | **P797's per-side wire IS `GetOreSeenBySide`**: the client's live asteroid fraction is only ever delivered for ore the side has seen. `HudWireValues.OreSeenFill`/`MinerCapacityHe3` implement the native units; the radar asteroid contact and the target pane (`TargetPaneModel`) consume them |
| 6 | Scanner "can see" + last seen | `IshipIGC::CanSee` = same side ∥ `SeenBySide(side)` ∥ `InScannerRange` (`Igc/shipIGC.h:380-389`; scanner math `:341-376` incl. the local-frame backward z-dilation `z<0 → z*=4` and `LineOfSightExist`, `Igc/common.cpp:2324-2398`); last-seen hull = `PlayerInfo::LastSeenShipType()` = the SHIP_STATUS HullID (`clintlib/clintlib.h:432`; `consoledata.cpp:34-53,202-212,252-276`; arrows `indicator.cpp:411-457`) | New pure `TargetVisibility.CanSeeShip` evaluates the three legs client-side: same side → true; EXPORT-identified presence = the SeenBySide leg (the P797 wire exports a model exactly once the side has seen it); `InScannerRange` reuses `VisibilityMath` (range + GA division + LOS over the sector's asteroids). Consumed by `TargetPaneModel` (hull/shield/speed of a ship target; type description = the SHIP_STATUS HullID the client already stores) and by the order turn arrows (`Game1.Camera` ship-target gate, `indicator.cpp:417-419`) |
| 7 | Cloak strength and drained state | **Reclassified `done-visual`** — the strength row N%/T sec is the live `100·(1−GetCloaking())` (`consoledata.cpp:480-489`) with the energy-starved ramp (`cloakigc.cpp:85-122`, `maxCloaking *= r²`) in the port's sim; the drained presentation is **GlassHUD-only art** (`gh_cloak1/gh_cloak2/gh_cloakdrained_bmp`, `dialog.mdl:570-621,651-656`, selected only under `GlassHUDSlot`) that native's classic HUD never draws, so no classic behaviour is missing |

**No non-native field was needed.** The one port-specific convention this wave
relies on already existed: `OwnShipCloakUpdate(220)` (documented there) — the
port's client is a pure renderer and cannot run the cloak-part sim native's
client runs, so the server streams the ramp; the HUD consumers read it.

## 1. What changed (files)

- `src/Allegiance.ClientCore.Game/Rendering/HudWireValues.cs` — **new** pure
  rules: `ProbeMineTimeFraction`, `OreSeenFill`, `MinerCapacityHe3`,
  `LauncherArmedFraction`.
- `src/Allegiance.ClientCore.Game/Rendering/TargetVisibility.cs` — **new**
  client-side `CanSeeShip` (shipIGC.h:380-389 leg-for-leg).
- `src/Allegiance.ClientCore.Game/Rendering/OwnLauncherArming.cs` — **new**
  own-launcher arming clocks (magazine/dispenser), replayed per frame.
- `src/Allegiance.ClientCore.Game/Rendering/OwnStatusData.cs` — signature ×
  live cloaking ramp.
- `src/Allegiance.ClientCore.Game/Rendering/HudInventoryPane.cs` — ReadyState 2
  + per-row `ArmedFraction` (magazine/dispenser only; chaff never arms —
  `IsLauncher = OT_magazine | OT_dispenser`, `igc.h:3993-3999`).
- `src/Allegiance.ClientCore.Game/Rendering/HudRenderer.cs` /
  `ModernHudRenderer.cs` — ReadyState 2 fill = `ArmedFraction`.
- `src/Allegiance.ClientCore.Game/Rendering/TargetPaneModel.cs` — CanSee gate
  (hull/shield/speed), target ore in native fill units, last-seen hull type
  documented as the SHIP_STATUS HullID.
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — probe/mine/asteroid
  radar fills, missile radius clip, signature caller passes `world.MyCloaking`,
  per-frame launcher-arming replay.
- `src/Allegiance.ClientCore.Game/Game1.Camera.cs` — order-arrow ship target
  gated on `CanSee`; target pane gets the miner-shipload capacity.
- `src/Allegiance.ClientCore.Game/Game1.FlightParts.cs` — inventory pane gets
  the armed-fraction query.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `ProbeEntity`/`MineEntity`
  keep the export `Time0` (server seconds); `OwnDispenserDeployed` counter;
  `ServerClockSeeded` accessor.
- `src/Allegiance.ClientCore.Game/LiveProof/*` — proof-harness only:
  `--auto-radar <mode>` pins the radar LOD for a real-window proof frame
  (the FLIGHTKEYS proof cycles modes only right after launch, before a
  `--auto-goto` flight arrives), re-pinned before each scheduled shot.
- `docs/parity/parity-matrix.csv` — 13 rows updated (below).

## 2. Tests (new)

`tests/Allegiance.ClientCore.MonoGame.Tests/P786HudWireDataTests.cs` (20 after
the review round — was 14) —
signature ramp (0.4 → 40%, engaged fallback 0), probe/mine time fraction
(clamps + no-lifespan guard), ore fill in shipload units (zero-ore → no bar),
miner capacity (`fc[7] × GA`, native 90 default), launcher armed-fraction
ramp, `OwnLauncherArming` fire/mount replay (magazine vs dispenser clocks),
inventory ReadyState 2 + `ArmedFraction` + chaff-never-arms, `CanSeeShip` legs
(same side / unknown / gone / other sector / range / backward z-dilation /
asteroid occlusion) **plus the P786B side leg (below): teammate scanner spots
what the own ship cannot, the friendly-probe plain-radial rule (no backward
dilation), LOS blocked for the only spotter, outside every friendly scanner →
hidden, and the pane-level teammate/probe sightings through
`TargetPaneModel.Build`**, target-pane hull/shield/speed gated by the scanner
test, target ore in native fill units.

`tests/Allegiance.ClientCore.Tests/P786ClientWorldWireTests.cs` (3) —
`ExportProbe`/`ExportMine` keep the wire `Time0` in server seconds; own
dispenser deploys count, foreign ones do not. **These fail on master** (the
entity dropped `Time0`; no counter existed). The MonoGame suite's new tests
fail on master likewise (no `HudWireValues`, no ramp parameter, ReadyState 2
unreachable, no CanSee gate).

Pre-existing suites affected and re-run green: `TargetPaneModelTests` (the pane
keeps its conservative stand-in when the hull catalogue is absent — the gate
returns null instead of guessing), `HudInventoryPaneTests`, `Hud6bOwnStatusTests`,
`Hud18TargetPaneTests`.

## 3. Verification (light gate)

- Whole-solution build **0 warnings / 0 errors** (Debug, twice — before and
  after the test-driven fix).
- `tools/build-tools.sh` — all 72 tool projects, 0/0.
- New/affected tests 3× alone: `P786HudWireDataTests` + affected HUD suites
  3× (79/79 each), `P786ClientWorldWireTests` 3× (3/3 each). All pass 3/3.
- One Fast-tier run: **PASS — 10,144 tests, 286 s**
  (`TestResults/tiers-20261024T121000.046651Z/summary.json`). The first Fast
  run failed 3 pre-existing `TargetPaneModelTests` cases (the new gate needed
  hull data the tests never passed) — fixed by the null-return fallback, then
  re-run green. No flakes ledgered.
- `scripts/parity-scorecard.py` validates; TOTAL 94.39% weighted
  (hud rows this wave moved `partial → done`/`done-visual`).
- AOT publish gate **does not apply** (no `src/Allegiance.Server` or
  `src/Allegiance.Core` change — the wire codecs are untouched).
- Raw runs (server/client logs, full PNGs) in `~/allegiance-evidence/P786/raw/`.

## 4. Screenshots — real window, before (master) vs after

Captured with `scripts/live-proof.sh` (real 2560×1440 backbuffer, real local
`Allegiance.Server` on `m2xspiderw.igc`, testUnlock), the new `--auto-radar all`
harness flag pinning the radar LOD. Every image below was opened and read.

| Asset | Shows |
|---|---|
| [`radar-asteroid-before-master-modern.webp`](P786_assets/radar-asteroid-before-master-modern.webp) / [`radar-asteroid-after-p786-modern.webp`](P786_assets/radar-asteroid-after-p786-modern.webp) | RADAR **All** in Alderan (side 1, `--auto-goto Alderan`): He3 asteroid contacts with name + range + hull bar. Both skins draw the fill bar — the P786 change is the **units** (native `oreSeen/(2·shipload)` + the non-zero gate), not bar presence, so on the unmined rocks these frames show the pre-change state of record. |
| [`radar-asteroid-fill-after-p786-modern.webp`](P786_assets/radar-asteroid-fill-after-p786-modern.webp) | After, later into the session (NPC miners active server-side): the white fill bar (native shipload units) alongside the red hull bar per contact. |
| [`inventory-arming-before-master-modern.webp`](P786_assets/inventory-arming-before-master-modern.webp) / [`inventory-arming-after-p786-modern.webp`](P786_assets/inventory-arming-after-p786-modern.webp) | F4 flight inventory, Mustang's `5.MRM Zeus` row mid-mount (t = 6.5 s after launch): the mounting/arming track. On master `ReadyState 2` was unreachable — a fully mounted launcher read straight to ready; the after client implements the native state machine (`OwnLauncherArming` replays the fire/mount clocks). |
| [`inventory-arming-after-timeline-modern.webp`](P786_assets/inventory-arming-after-timeline-modern.webp) | After, t = 6.0→9.5 s: the launcher bar filling, then the row resolving to ready stats (`6 / 1230m / 100`) exactly when the armed fraction completes. |

**Not photographable this wave** (documented, not skipped silently):

- *Signature ramp* — needs a cloak-capable hull with the cloak engaged mid-ramp;
  no auto-proof action engages the cloak and no offered hull's preferred loadout
  carries one. The ramp itself is the already-proven `OwnShipCloakUpdate(220)`
  stream (CloakToggleLiveTests/CloakVisualTests cover the stream); the gauge
  math is pinned by `Signature_percent_multiplies_by_the_live_cloak_ramp`.
- *Missile radius clip* — needs a live missile at the 1000 m clip boundary; the
  change is the contact's `ClipSeparation` input (type radius from the loaded
  core), covered by the code cite and the existing clip tests.
- *Ore fill visibly differing* — needs a partially mined rock in view: the map's
  autonomous NPC miners only reached ore fraction 0.989 in a 6-minute soak
  (`~/allegiance-evidence/P786/raw/after-mine4/server.out`, `ASTEROID_MINED`
  lines), and at 300-ore rocks both units clamp to a full bar. The units rule is
  pinned by `Ore_seen_fill_uses_two_shiploads_as_full`.
- *Mine/probe fill live* — MCP minefield lays failed this session
  (`spawn_scenario` fleet hull name mismatch, then `TeamBalance` refusal with
  the map's initial miners occupying slots); the fill is pinned by
  `P786ClientWorldWireTests` (wire `Time0` reaching the entity) +
  `Probe_mine_time_fraction` and consumed by the same tested radar fill path.

## 5. Parity rows touched

`done`: Signature percentage · Launcher arming progress · Radar probe and mine
contacts · Radar missile contacts · Radar asteroid contacts · Radar ore and
miner fill bars · Target ore · Target name · Target type description ·
Target hull · Target shield · Accepted and current order turn arrows.
`done-visual` (VISUAL: reason): Cloak strength and drained state.

## 6. Review round (P786B)

Three review findings, fixed in the same worktree on top of `80d4f10e`.

### 6.1 CanSee was missing the side leg (behaviour bug)

`TargetVisibility.CanSeeShip` only implemented same-side ∥ own-scanner; native
`IshipIGC::CanSee` (`Igc/shipIGC.h:380-389`) is
`GetSide()==side || pModel->SeenBySide(side) || InScannerRange(pModel)`, and on
the **client** `SeenBySide` is the ThingSite side visibility computed in
`UpdateSideVisibility` (`WinTrek/trekigc.cpp:2057-2120`): same/allied side, else
the last spotter, else **any scanner of the client's side in the target's
cluster** (`pcluster->GetClusterSite()->GetScanners(0)`, each
`s->InScannerRange(pmodel)`, trekigc.cpp:2098-2113). The client's scanner list
holds exactly the friendly ships/probes/stations of the client's side — the
client site only registers own-side (or allied-viz) scanners
(`WinTrek/trekigc.cpp:283-293`); every ship/probe/station registers as a scanner
for its side on entering a cluster (`Igc/clusterIGC.h:126,148,219`,
`Igc/shipIGC.cpp:3700`, `Igc/stationIGC.h:275`). A target a teammate (or a
friendly probe/station) sees must therefore show hull/shield/speed — the WIP hid
it whenever the own-ship scanner leg failed.

Fix (`TargetVisibility.cs`):

- New `TargetVisibility.SideScanner` (position, radius, scanner range, ship
  flag, nose) and a `SideScanners` parameter on both `CanSeeShip` overloads.
  After the own-ship leg fails, any side scanner whose own `InScannerRange`
  passes marks the target seen. Each scanner type keeps its native rule:
  - **ship** — `Igc/shipIGC.h:341-376`: hull `ScannerRange · signature /
    target-side GA[c_gaSignature]`, the delta in the scanner's LOCAL frame with
    the backward z-dilation (`z<0 → z*=4`, `:358-360`) and `LineOfSightExist`
    (`common.cpp:2324-2398`). Turrets never scan (`m_pshipParent == NULL`,
    `shipIGC.h:346`) — the gather enforces it via `ShipEntity.CanDrawInSector`
    (`ParentId < 0`).
  - **probe** — `Igc/probeigc.h:163-193`: probe-type `ScannerRange · signature`
    (the `× GA[c_gaScanRange]` factor of `:165` stays 1.0 — the P117/P119/P121/
    P124 no-GA-infrastructure convention) over a **plain radial test**
    (`probeigc.h:176`, no local frame) + `LineOfSightExist` (`:177`).
  - **station** — `Igc/stationIGC.h:308-333`: station-type `ScannerRange ·
    signature / target-side GA` over a plain radial test (`:321`) +
    `LineOfSightExist` (`:322`).
- `TargetVisibility.FriendlyScanners(...)` gathers the client's scanner list for
  one cluster (friendly ships with fresh poses, probes, stations — radius 0.5 ×
  hull length for ships per the port convention, `DataProbeTypeIGC.Radius` /
  `DataStationTypeIGC.Radius` + each type's `ScannerRange` for the statics; a
  type missing from the catalogue cannot contribute). **Caching:** the order-arrow
  path gathers once per frame/cluster into a reusable list keyed by a new
  `Game1._hudFrame` stamp (bumped at the top of `Draw`); `TargetPaneModel.Build`
  runs once per frame with a single ship target, so its gather is once per
  frame/cluster by construction. Both paths are allocation-free in steady state.
- Applied everywhere `CanSeeShip` is consumed: `TargetPaneModel.CanSeeTargetShip`
  (target-pane hull/shield/speed — name/type already resolve from the last-seen
  state, which is native's own CanSee-false fallback branch
  `consoledata.cpp:41-58`) and `Game1.Camera.CanSeeOrderTarget` (order turn
  arrows, `indicator.cpp:417-419`). The pane's `Build` grew a
  `probeTypes` parameter (the probe catalogue for the side leg).

Tests (fail on the WIP, pass now): enemy outside the own scanner but inside a
teammate's scanner → visible; inside a friendly probe's scanner → visible (and
the same geometry held by a ship scanner → hidden, pinning the no-dilation probe
rule); outside every friendly scanner → hidden; LOS blocked by an asteroid for
the only spotter → hidden; plus the same two sightings end-to-end through
`TargetPaneModel.Build`.

### 6.2 CSV line endings

`docs/parity/parity-matrix.csv` was rewritten with CRLF in `80d4f10e` (a 669-line
diff for 13 real row changes). The working tree now carries the same content
with LF endings only (verified: 669 LF, 0 CRLF; `diff` against HEAD's content is
empty line-for-line) — kept uncommitted, as the review directed.

### 6.3 Both HUD styles

The WIP screenshots were all Modern (this machine's
`~/.config/AllegianceCore/hud.json`). The existing assets are renamed
`*-modern.webp` and their **Classic counterparts** were captured with the same
commands (`scripts/live-proof.sh`, real 2560×1440 window, local server on
`m2xspiderw.igc`, testUnlock) with a copy of `hud.json` carrying
`"Style": "Classic"` passed via `ALLEGIANCE_HUD_SETTINGS`:

- radar: `--auto-launch --auto-goto Alderan --auto-target Asteroid --auto-radar
  all --autoshots 40,50 --exit-after 58` (t = 40/50 s frames in Alderan);
- arming: `--auto-launch Mustang --auto-inventory --autoshots 6,6.7,7.4,8.1,8.8,9.5
  --exit-after 12` (the same Mustang launch the Modern arming shots used).

Every new image was opened and read before being listed here. **New media is
reviewed by Domingo before the push.**

| Asset | Shows |
|---|---|
| [`radar-asteroid-after-p786-classic.webp`](P786_assets/radar-asteroid-after-p786-classic.webp) | Classic skin, RADAR **All** in Alderan: the He1055 contact label (name, range, the teal underline + red ore bar under the label) over the asteroid field — the Classic counterpart of the Modern contact crop. |
| [`radar-asteroid-fill-after-p786-classic.webp`](P786_assets/radar-asteroid-fill-after-p786-classic.webp) | Classic skin: a closer He3 rock (He1051) with the two-tone contact bar (white fill + red hull bar) drawn by the shared label-block code — the Classic counterpart of the Modern fill crop. |
| [`inventory-arming-after-p786-classic.webp`](P786_assets/inventory-arming-after-p786-classic.webp) | Classic skin, Mustang flight inventory mid-mount (t = 6.7 s): the metallic Classic pane, `5.MRM Zeus` in ReadyState 2 with the olive `Ar` badge and the dark fill bar being fed by the live `ArmedFraction`. |
| [`inventory-arming-after-timeline-classic.webp`](P786_assets/inventory-arming-after-timeline-classic.webp) | Classic skin, t = 9.5 s: the same row resolved to ready stats (`6 / 1230m / 100`) exactly when the arming fraction completes — the Classic twin of the Modern timeline shot. |

Per-row renderer ownership (Classic = `HudRenderer`, Modern = `ModernHudRenderer`;
both styles verified by the pairs above):

| Row | Classic draws in | Modern draws in |
|---|---|---|
| Signature percentage (own status) | `HudRenderer.DrawOwnStatus` (`HudRenderer.cs:1407`) | `ModernHudRenderer.DrawOwnStatus` (`ModernHudRenderer.cs:146`) |
| Launcher arming progress (`ReadyState 2` + `ArmedFraction`) | `HudRenderer.DrawInventoryPane`/`DrawInventoryRow` (`:2907`/`:2965`, fill `:3046-3048`) | `ModernHudRenderer.DrawInventoryPane`/`DrawModernInventoryRow` (`:412`/`:454`, fill `:496-498`) |
| Radar probe/mine lifetime fill | shared contact model (`Game1.Rendering.cs:4123`/`:4166`) → label bar in `HudRenderer.DrawRadarLabelBlock` (`:744`, bar `:850-852`) — **not overridden by Modern** | same shared path |
| Radar missile radius clip | shared `ClipSeparation` model (`Game1.Rendering.cs:4076`) → the same `DrawRadarLabels` path | same shared path |
| Radar asteroid contacts (ore fill) | shared model (`Game1.Rendering.cs:4298-4304`) → `DrawRadarLabelBlock` bar | same shared path |
| Target pane (name/type/hull/shield/speed/ore) | `HudRenderer.DrawTargetPane` + `DrawTargetPaneGauges` (`:2317`/`:2477`) | `ModernHudRenderer.DrawTargetPane` + `DrawModernTargetGauge` (`:274`/`:371`) |
| Order turn arrows | `HudRenderer.DrawOrderArrows` + `DrawTurnArrow` (`:2777`/`:2791`) | `ModernHudRenderer.DrawOrderArrows` + `DrawOrderArrow` (`:241`/`:254`) |

### 6.4 Review-round verification (light gate)

- Whole-solution build **0 warnings / 0 errors** (Debug); Release build 0/0 via
  every `live-proof.sh` run.
- New tests 3× alone: `P786HudWireDataTests` **20/20, three consecutive runs**;
  affected `TargetPaneModelTests` + `Hud18TargetPaneTests` **28/28 ×3**.
- `tools/build-tools.sh` — all 72 tool projects, 0/0.
- One Fast-tier run: **PASS — 10,150 tests, 287 s**
  (`TestResults/tiers-20261024T132234.099853Z/summary.json`).
- `scripts/parity-scorecard.py` validates; TOTAL 94.39% weighted (unchanged —
  the review round fixes behaviour inside already-`done` rows).
- AOT publish gate **does not apply** (no `src/Allegiance.Server` or
  `src/Allegiance.Core` change).
- Capture flake (documented, not silent): during the radar captures the windowed
  client silently quit ~50-60 s after launch in several runs (no managed
  exception, no coredump; server logs `conn-drop … ReadErrorOrEof`). Reproduced
  identically with the review-round changes **stashed** (WIP `80d4f10e`
  baseline), so it is not a P786B regression; this morning's Modern run
  survived the full 95 s. Runs with shots scheduled before the window
  (`--autoshots 40,50 --exit-after 58`) completed cleanly; worth a look if it
  reproduces on the rig.
- Raw capture runs (server/client logs, full PNGs) in
  `~/allegiance-evidence/P786B/raw/` (linked, not committed).
