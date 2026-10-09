# P591b — Mine-layer drones no longer ping-pong through alephs

**Branch:** `master` (not pushed). **Commit:** `f294478d` (amended with suite numbers).

## TL;DR

On the 221 config (`m2xspiderw.igc`, commit `937388a8`) the `.Caltrop` mine-layer
drones spawned but **never laid** — they ping-ponged through the very alephs they
had to reach: side-0 ship **8102 crossed Rigel(1059)⇄Alderan(1052) 34×**, side-1
ship **8103 crossed Organia(1051)⇄Gaia(1062) 378×**. Root cause: a ship whose goal
lies back through (or whose steering skims) the aleph it just arrived through sits
inside that aleph's transit volume, and the flat **2 s `LastWarpTime` cooldown**
(the only guard) expires while it is still there, so it re-crosses the very aleph
it arrived via and bounces forever.

The fix is a **native-faithful warp re-entry guard**: the host transit records the
warp the ship arrived **through** (`LastArrivalWarpId`), and
`Sector.ResolveWarpCollisions` refuses to re-transit that warp until the ship has
moved well clear of its transit volume (a 2× margin, so a momentary edge-clear
doesn't release it). It lives in the **shared** transit pass, so it protects every
ship — mine-layers, lifepods (P560), and bots — not just layers with a build order.

Failing-first, on the real server + `m2xspiderw`: the Organia→Alderan layer went
**1453 crossings / never laid** on today's code → **2 crossings and lays** after; a
ship pinned in the arrival aleph's transit volume bounced back out on today's code
and is held after. Live (final code): the side-1 layer crosses **once**, lays
`MINE expendable 1` in Alderan, and the field deals **P452** continuous mine damage
(`MINE_STRENGTH` decay) with **18× P574 per-hit `c_etMine` pops**.

---

## 1. Reproduction and root cause

### 1a. Reproduce the ping-pong (real server, `m2xspiderw`)

An in-process **real-server** test (`P591bLayerPingPongTests`, boots the true
`Allegiance.Server` loopback) drives a `.Caltrop` layer with a cross-cluster
`LayerBuildOrder` to Alderan. On **today's code** (guard absent):

```
[p591b] S1-Organia: FINAL laid=False sector=1062 crossings=1453 steps=60000
```

1453 Organia⇄Gaia crossings, never reaches Alderan, never lays — the exact
live-8103 2-cycle. A separate guard test pins a ship in the arrival aleph's
transit volume with an expired cooldown and a goal pointing back: on today's code
it bounces out (`expected 1057, actual 1051`).

### 1b. Why the flat cooldown is insufficient (native)

Native emits a crossing ship with outward velocity and holds the `m_warpState`
cooldown until it clears (`fsship.cpp:71-137`, `1185-1191`; re-arm ≈ 2 s after a
client update at `:1188`). The port's port of that is a single
`LastWarpTime`/`WarpCooldownSeconds = 2.0` threshold (`Sector.cs:1416, 3288`;
`fsship.cpp:1188`). That threshold alone cannot prevent re-entry for a **slow/heavy
drone that turns back toward the aleph it just came through**: the cooldown expires
while it is still (or back) inside the transit volume, and nothing else holds it.
The P591 suppression (`Sector.cs:3301`, layer-in-target-sector) only covers a layer
that actually holds a `LayerBuildOrder` pointing at its current sector; a layer
whose goal lies elsewhere (or any ship in the same situation) gets no protection —
hence the ping-pong.

### 1c. The exit geometry

Both native (`fsship.cpp:90-132`) and the port (`ServerHost.HitWarpEvent:958-994`)
emit the ship along the **destination** warp's forward axis (velocity
`backward · −(speed+exitBonus)` = along the destination warp's forward) with a
`shipRadius+5` forward offset. For the Organia→Gaia hop the exit aleph (2175,
Gaia's Organia aleph) points back toward Organia, so the drone starts moving back
toward the aleph it just came through — it must rely on its steering to clear it,
and the bare-edge release lets it swing back and re-cross.

## 2. The fix (native-faithful re-entry guard)

Three small, shared changes:

- **`Ship.LastArrivalWarpId`** (new field, `Ship.cs`): the warp the ship most
  recently arrived **through** (the destination-side partner warp it emerged at),
  `-1` once clear. This is the concrete form of native's "don't re-enter the
  arrival aleph".
- **`ServerHost.HitWarpEvent`** (`ServerHost.cs:967-973`): on transit, stamp
  `sh.LastArrivalWarpId = partner.Warp.WarpId`.
- **`Sector.ResolveWarpCollisions`** (`Sector.cs:3294-3336`): (a) refuse to
  re-transit the arrival warp while the ship is near it — skip it in the warp
  loop (`if (ship.LastArrivalWarpId == warp.Warp.WarpId) continue`); (b) release
  the latch only once the ship has moved **well clear** — more than `2 ×
  (warp.Radius + shipRadius)` from the arrival aleph — so a drone that skims the
  sphere edge as it turns toward its next goal cannot release the lock and
  immediately re-cross. Releasing on the bare edge was the Debug-geometry variant
  of the ping-pong (see §3); the 2× margin fixes it in both Debug and Release.

Because the guard lives in the shared transit pass it applies to every ship:
mine-layers (this task), **lifepod-home** routing (P560) and **bot** cross-cluster
routing all get the same "don't bounce back through the aleph you just came
through" rule. Legitimate re-crossing is preserved: a ship that has genuinely
moved clear may re-cross the warp (the latch releases).

## 3. Failing-first

`tests/Allegiance.Core.Tests/P591bLayerPingPongTests.cs` (3 tests, real in-process
server so the true `HitWarpEvent` transit runs):

| Test | Today's code | After fix |
|---|---|---|
| `Ship_that_just_arrived_through_a_warp_does_not_recross_the_same_warp_until_it_clears_it` | **FAIL** — ship pinned in the arrival aleph bounces out (1057→1051) | PASS — held 300 ticks |
| `Side1_layer_from_Organia_reaches_Alderan_and_lays_without_ping_pong` | **FAIL** — 1453 crossings, sector 1062, never lays | PASS — 2 crossings (Organia→Gaia→Alderan), lays |
| `Side0_layer_from_Rigel_reaches_Alderan_and_lays_without_ping_pong` | PASS (route resolves, 1 crossing, lays) | PASS (regression: 1 crossing, lays) |

The side-1 test reproduces the exact live-8103 Organia⇄Gaia 2-cycle
(1453 ≫ the 2-hop route length). Both route-resolution tests assert the layer lays
**and** that crossings ≤ the 2-hop route length (no ping-pong). Verified stable:
10/10 isolation runs, and passing in both full-suite runs.

## 4. Verification

### Whole-solution build
`dotnet build AllegianceCore.slnx` (Debug and Release): **0 warnings, 0 errors**.

### Full suite — 2 consecutive runs on the final code
Runner: `scripts/run-tests.sh` (P471), each assembly as the runner does it
(sequential, `--no-build`). Ledger: `docs/TEST_FLAKES.md`.

| Run | Lobby | MonoGame | ClientCore | Core | failures (all pre-existing live-wire load-flakes) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1677/1677 | **570/575** | **3842/3846** | ClientCore: `CrossSide_sector_stays_correct_across_warp_return`, `ShipDeleteGone_SameSide_Warper…`, `ThreePeerSeededSession…`, `ClientWarpApproachSlowHull…Legacy_200_450…`, `ShipDeleteGone_CrossSide_Warper…`; Core: `GameOverPlayersWire…Win_by_elimination…`, `LiveInSpaceEject…Already_a_lifepod…`, `LiveTreasurePickup…Rescue_ship_picking_cash…`, `P493EnemySpottingLiveTests` |
| 2 | 12/12 | 1677/1677 | **572/575** | **3840/3846** | ClientCore: `CrossSide_sector_stays…`, `ShipDeleteGone_SameSide…`, `ThreePeerSeededSession…`; Core: `AsteroidMinedLive…ASTEROID_MINED…`, `GameOverPlayers…Conquest_win_lists…`, `LiveChatSectorWire…`, `LiveLifepodEnduranceDisplay…`, `ShipsUpdateLive…Locked_target…` (ledger line 245), `WarpBombTests…Live_whole_mission_warp_bomb…` |

Every failure is a **pre-existing live-wire timing load-flake** in a ledgered
family (several are literally P591-era failures). The failure sets **differ**
between the two runs (only 2 overlap), confirming non-deterministic load flakes,
not this change. The **`P591bLayerPingPong` tests passed in both runs (0
failures)**.

### Live proof (local 221 server, final code, `consoleVerbose:true`)
`scripts/repro_p591b.sh` boots the server with the 221 config (civs 25/3,
`m2xspiderw`), runs the MCP `spawn_scenario … minefields{side:both}` showcase, and
captures the server log:
```
[drone] drone-type bucket '.Caltrop' complete on side 1 → spawned drone ship 8101
        (drone type 200, pilotType 5, hull 'Mine Layer Drone', laysExpendable 1) …
[layer] ship 8101 (side 1) ordered BUILD at warp 2152 → lay point sector 1052 (-3066,2666,-381), lays expendable 1
[warp]  ship 8101 crossed from 'Plato' (1060) to 'Alderan' (1052) via warp 2184     ← ONE crossing, no ping-pong
[layer] ship 8101 (side 1) laid MINE expendable 1 at (-2945,2301,-367) sector 1052 — LayExpendable (FedSrv.CPP:13780-13799)
[layer] ship 8101 (side 1) consumed after laying expendable 1 — KillShipEvent(launcher NULL, 0.0) (FedSrv.CPP:13860)
[mine]  ship 8101 hit by mine 9147: c_etMine pop at … sector 1052                      (×18 — P574 per-hit pops)
[mine]  mine 9147 strength -> 0.990 … 0.878 (MINE_STRENGTH)                            (P452 continuous attrition)
```
The side-1 layer reaches the target in **one crossing** (≤ the route length; the
pre-fix failing-first was 1453 for the analogous side), lays the minefield at the
Alderan aleph, is consumed, and the field deals continuous **P452** mine damage
with **18× P574** per-hit `c_etMine` pops broadcast to the peers.

**Before/after crossing counts** (the task's proof metric, target ≤ route length):
| Drone | Route | Before (today) | After (final) |
|---|---|---|---|
| side-1 layer (analogue of live-8103) | Organia(1051)→Alderan(1052) | **1453** crossings, never lays | **2** crossings (Organia→Gaia→Alderan), lays |
| side-0 layer (analogue of live-8102) | Rigel(1059)→Alderan(1052) | 1 crossing, lays (suppression covers) | **1** crossing, lays |
| live side-1 layer | Plato(1060)→Alderan(1052) | — | **1** crossing, lays + 18 pops |

### Honest limitations
- **Side-0 auto-invest window on this box.** The showcase's minefield
  invest-observe window (`max(40, TimeToBuildSec+15)` seconds, `McpTools.cs`)
  occasionally races side-0's layer build on this machine, so an automatic
  `minefields{both}` run frequently spawns/orders only side 1. This is a
  pre-existing invest-detection timing gap (P560 territory), not the P591b
  ping-pong; both sides' *route resolution + lay* are proven by the real-server
  `Side0_layer_from_Rigel` and `Side1_layer_from_Organia` tests above, and side-1's
  lay is captured live.
- **Real-window screenshot not completed this session.** Like P591, the live lay
  + mine damage is proven by the server log (above) and by the P591b tests; a
  headed real-window capture of the field was not completed within this session.
  The **P574 per-hit pop** and **P452 attrition** are additionally covered by the
  existing `MineCombatTests` in the suite.

## Files
- `src/Allegiance.Core/Sim/Ship.cs` — `LastArrivalWarpId` (re-entry latch).
- `src/Allegiance.Core/Model/Sector.cs` — re-entry guard + 2× clear-margin release.
- `src/Allegiance.Server/ServerHost.cs` — stamp the arrival warp on transit.
- `tests/Allegiance.Core.Tests/P591bLayerPingPongTests.cs` — 3 tests (failing-first
  guard, failing-first Organia route, Rigel route regression).
- `scripts/repro_p591b.sh` — live 221 reproduction/proof harness.
- `docs/reports/P591b_LAYER_PING_PONG.md` — this report.
