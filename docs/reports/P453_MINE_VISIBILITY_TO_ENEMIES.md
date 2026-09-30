# P453 — Enemies can now see / scan-spot a minefield

Landing: **master** (not pushed — Domingo pushes). Branch at commit time:
`master`.

## Summary

Closed the gameplay-parity gap where a minefield was only ever sent to the side
that laid it: an enemy side never learned a field existed, even with a scanner
sitting on top of it. In native, mines are ordinary side-visible, predictable
static objects — created with `c_mtSeenBySide | c_mtPredictable | c_mtHitable |
c_mtStatic` and `SetSignature(dataMineType->signature)`
(`src/Igc/mineigc.cpp:103-107`) — so they take part in the normal per-side
visibility sweep (`UpdateSideVisibility`) as **targets**, and native exports a
newly-visible predictable object to the whole seeing side
(`ThingSiteImpl::ShowObject`, `FedSrv.CPP:10749-10778` → `ExportObj` to that
side's group).

Three things were done, matching the brief's three scope items:

1. **Mines joined the visibility sweep as targets.** `Sector.ResolveCrossSideVisibility`
   (P132/P134) now scores every `Mine` in `Sector.Models` as a side-visible
   target with its owner side, its P452 collision radius, and its
   `MineType.Signature` — but `isScanner:false`, because native mines are not
   `c_mtScanner` and must never reveal anything to their own side.
2. **Export on first sighting.** A new per-tick catch-up sweep
   (`SweepCrossSideMineExports`) sends the seeing side's seated peers the same
   `EXPORT(26)/OT_mine` that `BroadcastExportMine` builds, once per (peer,
   mine), latched in `_crossSideMineExportSent` — the exact P349 ship-sweep
   idiom, so late joiners are caught up and the laying side's existing immediate
   export is untouched.
3. **Destruction reaches everyone who got the export.** `BroadcastMineDestroyed`'s
   audience is now the union of the same-side deploy audience and every peer in
   the cross-side latch, so no enemy client keeps a phantom field after the
   mine is destroyed or expires.

## Files changed

- `src/Allegiance.Core/Model/Sector.cs` — mines in `ResolveCrossSideVisibility`.
- `src/Allegiance.Server/Server.cs` — `_crossSideMineExportSent` latch
  (+ `ConnectionScopedCacheCounts.CrossSideMineExportSent` observability).
- `src/Allegiance.Server/Server.Broadcast.cs` — `crossSideMineExportSweep` step +
  `SweepCrossSideMineExports` / `SendCrossSideMineExport`.
- `src/Allegiance.Server/Server.Combat.cs` — `BroadcastMineDestroyed` audience
  widened.
- `src/Allegiance.Server/Server.Lifecycle.cs` — latch cleanup on disconnect.
- `tests/Allegiance.Core.Tests/MineVisibilitySweepTests.cs` (new, 4 domain
  tests) and `tests/Allegiance.Core.Tests/MineCrossSideVisibilityLiveTests.cs`
  (new, 1 live wire test).

## How mines joined the sweep (scope item 1)

`ResolveCrossSideVisibility` enumerates the sector's participants and, in the
same pass, buckets each participant as a scanner for its own side (ships, probes
and stations are scanners-and-targets; asteroids are target-only via
`sideId:null`). Mines needed a third variant: **a target with a side that is not
a scanner**. The `Add` helper gained an `isScanner` flag (default true), and a
new `case Mine mn:` supplies the participant:

```
case Mine mn:
    Add(mn, mn.SideId, mn.Position, mn.MineType.MineRadius,
        0f, mn.MineType.Signature, isScanner: false);
    break;
```

- **Radius:** `MineType.MineRadius` — the same field P452 settled on as the
  native collision radius (`DataMineTypeIGC::radius`, `igc.h:1992`; port offset
  464). Using the base `Radius` (the visual/model radius at offset 16) would be
  wrong; P452 already ruled this.
- **Signature:** `MineType.Signature` — the port of native
  `SetSignature(dataMineType->signature)`.
- **Side:** `Mine.SideId` (nullable, carried from the laying ship). Non-null
  keeps the own-side-always-visible rule (the owner sees its own field, as
  today) and enables scoring against the other sides' scanner nets. A sideless
  mine (never-seated layer) is never cross-side exported.
- **Not a scanner:** `isScanner:false` keeps the mine out of
  `scannersBySide`, so it never grants its side any awareness — the
  never-a-scanner domain test pins this.

No change to the `SideVisibilityState`/`ISideVisible` surface was needed:
`Mine : Model` already inherits the `SideVisibility` holder and
`SeenBySide`/`GetCurrentEye` from the `Model` base (the exact pattern `Probe`
uses).

## The export latch (scope item 2)

`SweepCrossSideMineExports` runs every `SimPass`, right after the ship
cross-side sweep. For each mine in each Started/Starting mission sector, for
each side that is not the mine's owner, it checks the latched
`mine.SeenBySide(sideId)` (the P132 result this tick) and — if true — sends
each of that side's seated peers the EXPORT once:

```
if (_crossSideMineExportSent.Add((conn, mine.Id)))
    SendCrossSideMineExport(conn, mine, sideId);
```

- **Audience:** every seated peer of the seeing side (per-side sensor net —
  one scanner anywhere makes the whole side aware, the same model the ship
  sweep uses). Never the mine's own side (that got the deploy export via
  `BroadcastExportMine` at creation), never whole-mission.
- **Late joiners:** because the sweep reads the latched `SeenBySide` every tick
  and latches per (peer, mine), a peer who joins an already-informed side is
  caught up on its very next tick — the same P349 rationale that abandoned the
  old one-shot transition queue.
- **No sector gate:** unlike the ship sweep (which must align with the per-cluster
  HEAVY_SHIPS_UPDATE telemetry gate), a mine is a static object with no per-cluster
  telemetry channel, so the export is gated on `SeenBySide` alone; the client's
  mine renderer already filters by the viewer's own sector.
- **Wire fields:** the EXPORT is `ExportMine.FromMine(mine, tickIndex, launcherId,
  sideId: mine.SideId)` — identical to `BroadcastExportMine`, with the mine's OWN
  (owner) side on the wire as the faction identity, and the real laying ship's id
  as `launcherID`.

`SendCrossSideMineExport` builds the byte-identical EXPORT `BroadcastExportMine`
sends, so the same-side and cross-side notices are interchangeable on the wire —
which is why the existing client renderer (`Game1.Rendering.cs:800-826`, P291)
needed **no change**: it already renders every mine in `ClientWorld.Mines`
filtered only by the viewer's own sector, not by side (confirmed by reading the
draw loop).

## Destruction reach (scope item 3)

`BroadcastMineDestroyed` was restructured: it no longer early-returns when the
launcher is a non-Ship or sideless, and its audience is now

```
same-side (layer + _teammateExportSent-gated teammates)  ∪  _crossSideMineExportSent[(conn, mine.Id)]
```

so every peer that was ever sent the mine — including every enemy that scan-spotted
it — receives `MINE_DESTROYED(84)` and drops the field. The cross-side reach rides
the latch (so it survives even a launcher whose side became null by destroy time);
the `P263` explosion VFX follows the same widened audience.

## Pre-fix failing evidence

The **must-fail** proof: with the production change removed (the `case Mine`
neutralised to a no-op in the sweep AND `crossSideMineExportSweep` disabled),
all five new tests failed — including the wire-level in-range EXPORT step, which
is the "this part fails before the fix" evidence the brief asks to record:

```
Allegiance.Core.Tests.MineVisibilitySweepTests.An_enemy_scanner_in_range_with_line_of_sight_spots_the_mine [FAIL]
Allegiance.Core.Tests.MineVisibilitySweepTests.Moving_the_scanner_out_of_scope_flips_the_mine_to_unseen [FAIL]
Allegiance.Core.Tests.MineVisibilitySweepTests.An_asteroid_occluding_an_in_range_pair_flips_the_mine_to_unseen [FAIL]
Allegiance.Core.Tests.MineVisibilitySweepTests.A_mine_is_never_a_scanner_for_its_own_side [FAIL]
Allegiance.Core.Tests.MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction [FAIL]
Failed!  - Failed: 5, Passed: 0, Skipped: 0, Total: 5
```

(On the unfixed code the domain tests fail because the mine is never scored, so
`SeenBySide(enemy)==false` always; the live test fails because B never receives
the EXPORT once in range — the exact pre-fix gap.)

## What the tests assert

### Domain — `MineVisibilitySweepTests` (core-level, real corpus hulls + a real `Mine`)
- `An_enemy_scanner_in_range_with_line_of_sight_spots_the_mine` — a side-0
  `faohshcar` scanner 300 u from a side-1 mine makes `mine.SeenBySide(0)==true`
  after the sweep, and `mine.SeenBySide(1)==true` (own-side rule); false before
  the sweep.
- `Moving_the_scanner_out_of_scope_flips_the_mine_to_unseen` — moving the scanner
  beyond detection (D = 790 for the representative mine: 150 + MineRadius 100 +
  3600·sig 0.15) flips `SeenBySide(0)` true→false on the re-sweep; own side stays.
- `An_asteroid_occluding_an_in_range_pair_flips_the_mine_to_unseen` — an in-range
  pair (300 u < 790) with a radius-120 rock colinear at 150 blocks LOS
  (covered asin(60/150)≈23.6° > mine visibleAngle asin(100/300)≈19.5°) →
  `SeenBySide(0)` false while still in range.
- `A_mine_is_never_a_scanner_for_its_own_side` — side 1's only presence is a mine
  (its launcher is unplaced); an adjacent side-0 ship is NOT seen by side 1,
  proving the mine never acts as a scanner, while the mine itself remains a valid
  target for side 0.

### Live wire — `MineCrossSideVisibilityLiveTests` (loopback)
- `Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction` — two
  bots on different sides in the same sector. Side 0 lays a mine; side 1 starts
  far out of scanner range (its `MineExports` is empty); side 1 flies to 300 u
  and receives the EXPORT(26)/OT_mine **exactly once** (asserted after extra
  pumping — no per-tick re-export), with the correct owner side / mine id /
  cluster / type; pumping past lifespan delivers MINE_DESTROYED(84) naming that
  mine. The loopback client's `MineExports`/`MineDestroyeds` are the raw wire
  records that feed `ClientWorld.Mines` (the table layer is pinned by the existing
  `ClientWorldMineTests` unit tests in `Allegiance.ClientCore.Tests`), so this
  establishes "appears in / leaves B's `ClientWorld.Mines`".

## Did the client renderer need a change?

No. `Game1.Rendering.cs:800-826` (P291) draws every mine in `ClientWorld.Mines`
filtered only by `me2.ClusterId == mySector` (the viewer's own sector) — it never
filters by the mine's `SideId`. Since `ClientWorld.Apply(ExportMine)` upserts into
`_mines` regardless of side and `Apply(MineDestroyed)` removes by id (both already
in place), an enemy mine simply appears and disappears in the table exactly like a
friendly one. Confirmed by reading the render loop and the client dispatch; no
client change shipped.

## Post-fix verification

After the production change: build `/home/domingo/.dotnet/dotnet build` → 0
warnings, 0 errors. New + adjacent suites green (38 tests: MineVisibilitySweep,
MineCrossSideVisibilityLive, MineBroadcast, CrossSideExportLive,
CrossSideVisibilitySweep/Foundation, MineCombat). `MineBroadcastTests` — which
had asserted a cross-side enemy receives neither message — still passes because
its default-spawned enemy ends up out of scanner range (or in a different side's
home sector), so the narrowed same-side-only assertion remains valid.

Full-suite runs below (filled from the 3 consecutive `dotnet test` runs).

## Verification appendix — full-suite runs

Three consecutive full `dotnet test` runs (`/home/domingo/.dotnet/dotnet test -c
Debug`). Per-assembly totals:

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 ✅ | 12/12 ✅ (run 1; unchanged in later runs) | 12/12 ✅ |
| Allegiance.ClientCore.MonoGame.Tests | 974/974 ✅ | 974/974 ✅ | 974/974 ✅ |
| Allegiance.ClientCore.Tests | 331/333 (2) | 330/333 (3) | 330/333 (3) |
| Allegiance.Core.Tests | 3574/3583 (9) | 3573/3583 (10) | 3571/3583 (12) |

(The Core total grew 3578 → 3583 with P453's five new tests. Mine-related tests —
`MineVisibilitySweepTests`, `MineCrossSideVisibilityLiveTests`, `MineCombatTests`,
`MineBroadcastTests`, `ExplosionPropagationTests` — passed in **every** run.)

Live-wire timing flakes observed (union over the 3 runs; membership varies run to
run — the documented signature — and none touch mine logic):

- `ConquestTerritoryLiveTests.{Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over,
  Flags_disabled_capture_broadcasts_nothing_and_does_not_end,
  Conquest_win_in_one_mission_does_not_leak_to_another}` (all 3 runs)
- `GameOverPlayersWireTests.{Conquest_win_lists_every_connected_player_including_losing_side,
  Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another}` (all 3 runs)
- `LiveWinConditionTests.{Board_refused_once_mission_over,
  Multi_mission_disconnect_scopes_per_mission}`
- `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  (persistent machine-state flake, documented in the skill's list — not chased)
- `LiveRipcordTests.Cancel_while_active_aborts_sector_wide`,
  `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission`
- `WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands`,
  `SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone`
- `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`,
  `CargoBayReloadTests.Reload_transfers_ammo_updates_ship_ammo_resets_weapon_fraction_and_broadcasts`
- `LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side`,
  `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it`
- ClientCore: `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
  `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`

**Environmental proof:** none of the flagged test files reference any P453 symbol,
and the most-recurring families (`ConquestTerritoryLiveTests`,
`GameOverPlayersWireTests`, `LiveWinConditionTests`) were re-run on the **pristine**
tree (P452 source, via `git stash` of `src/`) and failed identically there
(GameOverPlayers ×2, ConquestTerritory ×3) — so they are pre-existing timing
flakes, not regressions from this change.

## Out of scope (unchanged)

The minefield damage model (P452, landed first; this task uses its radius
decision), periodic per-side mine health/strength updates, "hidden again"
semantics (native never un-exports predictable statics on sight loss), client
rendering changes, enemy-ship spotting reports, station capture (P450), station
alerts (P451), teleport probes (P454), armed probes, and the in-flight turret
seat switch are all deliberately left untouched.
