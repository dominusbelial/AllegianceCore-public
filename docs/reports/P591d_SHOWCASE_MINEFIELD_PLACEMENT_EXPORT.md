# P591d — Showcase minefields: placed at the enemy's actual arrival aleph + exported to the owning side

**Branch:** `master` (not pushed). **Commit:** `a4b9e83e` (will be amended with suite numbers).

## TL;DR

Two live defects on the 221 showcase (`minefields {side: both}`) are fixed:

1. **Placement** — both fields landed at Alderan's **Cirrus** aleph (2150), far
   from the fight, because the MCP's placement took the *first* outbound aleph in
   the rendezvous (`FindArrivalWarp`, arbitrary dictionary order) and used the
   SAME aleph for both sides. Now each side resolves the ENEMY's home sector and
   computes the aleph the enemy fleet actually transits to ENTER the rendezvous
   (a BFS over the real warp graph, the same routing the bots fly): side 0's
   field → **Juniper (2183)**, side 1's field → **Rigel (2187)** — the two
   contested alephs where the showcase fights happen.
2. **Export audience** — an NPC layer drone (the drone-bucket spawn, not a
   connected peer) laid a minefield that went to **0 same-side peers**, because
   `Server.BroadcastExportMine` gated recipients on `_teammateExportSent[(peer,
   layer)]`, a latch only ever set for a CONNECTED PEER ship. Now the deploy
   export goes to **every same-side seated peer** (native: a mine is a static,
   side-visible object exported to the owning side at creation), and enemies are
   still served by the P453 cross-side sweep once spotted. Live: the `.Caltrop`
   field deployed → **EXPORT(26)/OT_mine → 18 same-side peer(s)**.

---

## 1. Placement — why it picked Cirrus, and the fix

### Root cause (measured live + in code)

`McpTools.ResolveMinefieldPlacement`'s default branch called
`BotManager.FindArrivalWarp(rz)`, which returns the FIRST warp in the rendezvous
sector whose partner is in a DIFFERENT sector. That is a dictionary-iteration
artifact, not a route decision. On the 221 map it returned warp **2150 (Cirrus,
to sector 1056)** — and both sides got the same aleph. The two showcase fleets
actually fight at the two *arrival* alephs: side 0's fleet (home Ganymede 1050)
enters Alderan via **Rigel (2187)**, side 1's fleet (home Organia 1051) via
**Juniper (2183)**. The old field sat at Cirrus, so no fleet ever crossed it
(0 mine hits).

### The fix (per-side, native intent)

- `BotManager.FindSideHomeSector(side)` — the sector of the side's restart-capable
  (c_sabmRestart) home station from the probe's real world.
- `BotManager.EnemyArrivalWarp(warps, rendezvous, enemySide, enemyHomeSector)`
  (pure, internal-static, unit-testable against the real map) — mirrors the bots'
  own routing (`EnsureCombatPath` → `ClusterGraph.FindShortestPath`): BFS shortest
  path from the enemy's home to the rendezvous, the penultimate sector is where
  the enemy enters FROM, and the arrival aleph = the warp IN the rendezvous whose
  partner sits in that penultimate sector.
- `McpTools.ResolveMinefieldPlacement` now takes the laying side + enemy side and
  uses the enemy's arrival aleph; falls back to the old first-outbound aleph only
  when the enemy home/route cannot resolve.

**Result on the 221 map:**
| field | enemy | enemy home | arrival aleph | sector |
|---|---|---|---|---|
| side 0 | side 1 | Organia (1051) | **2183 (Juniper)** | Alderan (1052) |
| side 1 | side 0 | Ganymede (1050) | **2187 (Rigel)** | Alderan (1052) |

## 2. Export audience — why 0 peers, and the fix

### Root cause (measured live + in code)

`Server.BroadcastExportMine` (Server.Combat.cs) selected recipients with
`isVisibleTeammate = … && _teammateExportSent.Contains((kv.Key, layer.Id))`.
`_teammateExportSent` is populated only for CONNECTED PEER ships (their ExportShip
is sent to teammates in `BroadcastShipStates`, Server.Broadcast.cs:692). The
showcase's mine-layer drone is spawned by the drone bucket as an **NPC** — it is
not a connected peer, so the latch was never set for it and the field reached
**0 peers** (the live `-> 0 same-side peer(s)`).

Native: a mine is a static object created with
`c_mtSeenBySide | c_mtPredictable | c_mtHitable | c_mtStatic`
(mineigc.cpp:103-107). `FedSrv LayExpendable` creates it and the **owning side's**
players receive the object export; enemies see it once their scanner net spots it
(the P453 cross-side sweep). There is no "must already hold the layer's ExportShip"
gate in native — a mine is the side's own hazard.

### The fix

`Server.BroadcastExportMine` now sends the `FM_S_EXPORT(26)/OT_mine` to **every
same-side seated peer** (independent of the launcher being a peer). The client
filters mines by its own sector (Game1.Rendering.cs:1127), so a same-side peer in
another sector harmlessly discards it — no leakage. Cross-side/enemy reach stays
on the P453 `SweepCrossSideMineExports` (once spotted). `BroadcastMineDestroyed`
was updated to the same owning-side audience so the field's removal reaches the
same peers.

## 3. Failing-first

| Test | Today's code | After fix |
|---|---|---|
| `P591dShowcaseMinefieldPlacementTests.Side0_field_goes_across_the_enemy_Juniper_aleph_2183_not_Cirrus_2150` | fails (old selector → 2150 Cirrus) | passes → 2183 |
| `P591dShowcaseMinefieldPlacementTests.Side1_field_goes_across_the_enemy_Rigel_aleph_2187_not_Cirrus_2150` | fails (old selector → 2150) | passes → 2187 |
| `P591dShowcaseMinefieldPlacementTests.Neither_field_lands_at_the_far_Cirrus_aleph_2150` | fails (both → 2150) | passes (2183/2187, distinct) |
| `P591dShowcaseMinefieldPlacementTests.Resolver_routes_match_the_bots_shortest_path_machinery` | fails | passes |
| `P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield_reaches_same_side_seated_peer` | **fails** (`Assert.NotEmpty` — audience 0) | passes (peer receives OT_mine) |
| `P591dMinefieldExportAudienceTests.Opposite_side_peer_does_not_receive_the_deploy_mine_export` | passes (negative control) | passes (foreign side gets nothing) |

The placement tests fail-first because the old selector demonstrably returned
Cirrus (2150) — verified with a small loader that reproduced `FindArrivalWarp(1052)
→ 2150` (the exact live bug). The export test fails-first because the NPC launcher
saturates no peer latch → empty `MineExports`; it passes once the owning-side
audience is used.

## 4. Verification

- Whole-solution build: Debug **0 warnings / 0 errors**, Release **0 warnings / 0 errors**.
- Related suites (mine domain) all green: `MineCombatTests` (P452 native-formula
  attrition, P574 per-hit pops at native P2 — the "ships crossing the field"
  damage/pop mechanics), `MineBroadcastTests`, `MineCrossSideVisibilityLiveTests`,
  `ChaffTests`, `P591`/`P591b`/`P591c` layer tests, MonoGame `P563`/`P574` visual
  tests — all pass on the final code.
- Full suite: 2 consecutive runs via `scripts/run-tests.sh` (P471), ledger
  `docs/TEST_FLAKES.md`:

| Run | Lobby | MonoGame | ClientCore | Core | failures (all pre-existing live-wire load-flakes, ledgered) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1689/1689 | **578/579** | **3856/3861** | ClientCore: `FullSessionKill...RealCrossSideKill` (fam.); Core: `LiveWinCondition...Board_refused_once_mission_over`, `LiveRipcord...Cancel_while_active`, `LiveInSpaceEject...Already_a_lifepod`, `CargoBayReload...SwapPart_out_of_range`, `AsteroidMinedLive` (2 members) |
| 2 | 12/12 | 1689/1689 | **578/579** | **3856/3861** | ClientCore: `ThreePeerSeededSession...DeterministicVisibility` (fam.); Core: `LiveRelaunch...Carrier_backward_fallback`, `LiveCountdownStartPing...Multi_mission_countdown`, `GameOverPlayers...Win_by_elimination`, `ConquestTerritory...Conquest_win_leak`, `AddPart...Part_pickup` |

Every failure is a documented pre-existing live-wire load-flake in a ledgered
family (`docs/TEST_FLAKES.md`); the failure sets differ between the two runs
(only `AsteroidMinedLive`/`GameOverPlayers` families recur), confirming
non-deterministic load flakes, not this change. The **`P591d` tests passed in
both runs** (placement 4/4 in ClientCore, export 2/2 in Core).

## 5. Live proof (local 221 server, final code, consoleVerbose)

`scripts/repro_p591d_full.sh` / `repro_p591d_final.sh` boot the 221 config
(`m2xspiderw.igc`, civs 25/3) and run the MCP showcase + minefields. Selected
server lines (also in `docs/reports/P591d_SHOWCASE_MINEFIELD_ASSETS/minefield_evidence.txt`):

```
[layer] ship 8101 (pilotType 5) ordered BUILD at warp 2183 → lay point sector 1052 (-5706,2494,96)  ← side 0 field → JUNIPER
[layer] ship 8102 (pilotType 5) ordered BUILD at warp 2187 → lay point sector 1052 (-2363,5866,24)  ← side 1 field → RIGEL
[mine]  ship 8101 deployed -> EXPORT(26)/OT_mine 1 pos Vector3 { X = -5405.239, Y = 2735.0425, Z = 89.58042 } life 600 -> 18 same-side peer(s)
[layer] ship 8101 (side 0) laid MINE expendable 1 at (-5405,2735,90) sector 1052 — LayExpendable (FedSrv.CPP:13780-13799)
[layer] ship 8101 (side 0) consumed after laying expendable 1 — KillShipEvent(launcher NULL, 0.0) (FedSrv.CPP:13860)
```

- **Placement:** both drones ordered to the enemy's actual arrival alephs
  (2183 Juniper, 2187 Rigel) — the two contested alephs where the showcase fleets
  route (side 0 via Rigel 2186, side 1 via Juniper 2182, both observed crossing
  into Alderan).
- **Export audience:** the `.Caltrop` field deployed → **18 same-side peer(s)**
  (the pre-P591d code emitted `0 same-side peer(s)`). This is the concrete live
  inversion of the defect.
- **P452/P574 damage + pops with ships crossing:** the live 15v15 field-lay is
  timing-dependent (the P560 invest race and the long, fragile drone lay), so a
  clean real-window "violet trail of pops" was not reproducibly captured this
  session. The damage/per-hit-pop mechanics are proven by the passing
  `MineCombatTests` (native P452 attrition formula, P574 pop at native P2) and by
  the `P591dMinefieldExportAudienceTests` real-server lay. No screenshot or pop
  count is fabricated.

## Files

- `src/Allegiance.Server/Server.Combat.cs` — `BroadcastExportMine` + `BroadcastMineDestroyed` owning-side audience.
- `tools/allegiance-mcp/BotManager.cs` — `FindSideHomeSector`, `FindEnemyArrivalWarp`, pure `EnemyArrivalWarp` (BFS) + helpers.
- `tools/allegiance-mcp/McpTools.cs` — per-side `ResolveMinefieldPlacement`.
- `tests/Allegiance.Core.Tests/P591dMinefieldExportAudienceTests.cs` (2 real-server tests).
- `tests/Allegiance.ClientCore.Tests/P591dShowcaseMinefieldPlacementTests.cs` (4 pure tests).
- `scripts/repro_p591d_{full,final}.sh` — live 221 repro/proof harnesses.
- `docs/reports/P591d_SHOWCASE_MINEFIELD_ASSETS/` — server-log evidence + real-window PNGs.
- `docs/reports/P591d_SHOWCASE_MINEFIELD_PLACEMENT_EXPORT.md` — this report.
