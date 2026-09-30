# P470 — Remove the player-flyable Builder (P285), keep native behaviour

**Status:** landed on `master` (not pushed).
**Date:** 2026-09-30
**Decision being enacted (Domingo, 2026-09-29):** builders are AI drones
only, as in native. The 2026-09-29 parity pass found P285 (`4f4a3ec9`,
"player chooses a Builder ship's target station type at purchase") to be a
port invention. In native a constructor exists only when a team funds a
station-type bucket; the server then spawns an AI constructor drone, and
players never fly one.

## Prerequisite

P458 ("investing in a station type spawns an AI constructor drone",
`5b77a664`) is present on `master` — confirmed before any code was touched.
Without it, removing P285 would leave no way to build stations. Both commits
exist in the tree.

---

## 1. The group-id signedness finding (the crux)

Native stores each hull's group id as a **signed char**:

    src/Igc/igc.h:639    typedef char BuyableGroupID;          // SIGNED

The hangar lists a hull only when `pht->GetGroupID() >= 0`:

    src/WinTrek/load.cpp:1603-1606   ((pht->GetGroupID() >= 0) && pstation->CanBuy(pht) && !pstation->IsObsolete(pht))
    src/WinTrek/load.cpp:1817-1820   (same predicate, second hangar list)
    src/clintlib/clintlib.h:1858     ((pht->GetGroupID() >= 0) && pstation->CanBuy(pht) && (pht->GetPrice() <= budget))

The port previously decoded `DataHullTypeIGC.GroupId` as an **unsigned**
`byte` (`b[261]`, `DataHullTypeIGC.cs:20,332`), so a native `-1` arrived as
`255` and the `>= 0` rule could never exclude anything — which is why P285's
Builder/Constructor hulls were offered to players at all.

**P470 change:** `DataHullTypeIGC.GroupId` is now `sbyte`, decoded as
`unchecked((sbyte)b[261])` — native's signedness. Nothing else in the port
read a hull's group id (verified by grep), so the type change is confined.

### The real per-hull data (PCore001.igc, decoded)

Raw group byte, signed interpretation, builder (`0x8000`)/miner (`0x4000`)
ability bits, for the hull families that matter:

| Hull class (examples)              | raw grp | signed | builder | miner |
|------------------------------------|---------|--------|:-------:|:-----:|
| Fighter / Scout / Interceptor / Bomber / Troop Transport / Stealth Bomber / Gunship / Guardian … (all price-0 tech tiers) | 0x00 | 0 | – | – |
| Corvette / Cruiser / Destroyer / Frigate / Devastator / Dreadnought / Flagship / PT Bomber … | 0x04 | 4 | – | – |
| **Lg/Sm Adv/Enh/Std Constructor**, Mine Layer Drone, Tower Layer Drone, Nova Bomb Layer | 0xFF | **-1** | **✓** | – |
| **Adv/Enh Miner**, Miner | 0xFF | **-1** | – | **✓** |
| Lifepod, Recon Drone, Rescue Drone, [DR]/*[SR]* drone carrier/variant hulls | 0xFF | **-1** | – | – |

The data is decisive: **every Constructor hull and every Miner hull carries
raw group byte `0xFF` (= signed `-1`), the same negative id as all the other
drone-only hulls (lifepod, recon/rescue drones, [DR]/[SR] variants).** No
drone-only hull has a non-negative group id, and no player-flyable hull has a
negative one. So native's `GroupID >= 0` rule is exactly the drone-hull
separator — no capability-bit filter was invented (the builder/miner ability
bits are *orthogonal* classifications, used by AI-drone roles, not by the
hangar gate).

Consequently the GroupId rule removes **both** constructors **and** miners
from the player-buyable set. That is native-correct and is kept (see
Out-of-scope notes). Every excluded hull also has price 0 in the core, which
is consistent with never being a purchasable hull.

### Where the rule is applied

1. **Server BUY_LOADOUT hull acceptance** (`Server.ShipCommands.cs`): the P441
   hull gate is extended — the same single gate now refuses a
   `GroupId < 0` hull (drone-only) the same way it refuses an unsellable one,
   before any mutation. No parallel gate was added. Both conditions run under
   the gate's single `hullChanged` guard (native's `hullID !=
   pht->GetObjectID()`, `shipIGC.h:996`): a same-hull buy re-mounts
   parts/relaunches with no hull purchase, so it is not gated — only a hull
   SWITCH to a drone hull is refused. The `GroupId < 0` arm is dock-independent
   (a drone hull is unwinnable whether or not the buyer is docked); P441's
   sellable/tech arm still only runs when docked.
2. **Client shop offer** (`ShopCatalog.FilteredFor`): the shop's hull OFFER
   (the per-station hangar list the docked screen builds from) excludes
   `GroupId < 0` hulls in both the normal and the fail-closed branches. This
   is applied to the **offer only**, not to `ShopCatalog.Hulls` wholesale:
   the raw table is still read for the current ship / lifepod-oxygen (P377) /
   hull-model resolution, which legitimately handle non-buyable hulls like the
   pod. So filtering `TryLoad` globally would have broken lifepod detection —
   filtered at the offer layer instead, matching native's per-station list.

---

## 2. What was removed (P285 entirely)

- **Wire message + codec:** `src/Allegiance.Core/Wire/SetBuilderTarget.cs`
  (fmid 201) — deleted. fmid 201 is freed back to the port's free
  extension range.
- **Server dispatch:** `SET_BUILDER_TARGET` branch in
  `Server.Dispatch.cs` — removed.
- **Server handler:** `SetBuilderTargetOnShip` in `Server.ShipCommands.cs` —
  removed.
- **Client send:** `ConnectionSession.SendSetBuilderTarget` — removed.
- **Shop builder-target chooser UI:**
  - `ShopCatalog.IsBuilderHull` / `BuilderTargetsFor` / `IsBuilderConstructible` — removed.
  - `ShopModel` builder-target state (`_builderTargets`,
    `BuilderStationTypeId`, `CurrentBuilderTarget`, `IsBuilderActive`,
    `CycleBuilderTarget`) and the ctor `builderTargets` parameter — removed.
  - `ShopLayout` "BUILDER TARGET" rows and the ",/. BUILD TARGET" hint — removed.
  - `Game1.Panes.cs` `,`/`.` cycling, `SendBuilderTargetIfApplicable`, and the
    `_btPrevWasDown`/`_btNextWasDown` latches in `Game1.cs` — removed.
- **Test-bot support:** `Bot.SendSetBuilderTarget` — removed.
- **Tests:** `SetBuilderTargetCodecTests`, `P285PurchaseBuilderTargetLiveTests`
  (Core) and the P285 blocks in `ShopModelTests` / `ShopLayoutTests`
  (MonoGame) — removed.
- **Docs:** `docs/BUILDER_TARGET_P285.md` — removed (dead documentation of a
  removed feature).

**Kept intentionally (not P285-feature, used by kept ports):**

- `DataHullTypeIGC.HabmBuilder` (0x8000) **constant** — although introduced by
  P285, it is now read by P425's builder-drone command gate
  (`Server.ShipCommands.cs:1482,1554,1590,1594` and
  `CommandOrderResolver.cs:29,45`) which this task keeps. Removing it would
  break P425; it is not dead code.
- `Ship.BuilderStationType` — the settable per-ship field P458's drone spawn
  now sets.
- The P163–P165 BuildingEffect mechanic, the P165 builder AI, and P422's
  player-issuable Build command / P425's commanding of builder drones.

### Adaptation required by the removal (reported, not hidden)

`PlayerBuildCommandLiveTests.cs` is a **P422** test (kept) that used
`bot.SendSetBuilderTarget` purely as a fixture helper to arm the tested ship
with a `BuilderStationType`. With the P285 transport gone, it now sets the
plain-settable field directly:

    peer.BuilderStationType = stationType;

This preserves the test's actual subject (the Build-command legality +
construction arc), which never depended on the SET_BUILDER_TARGET wire — it
only borrowed it to configure the field. The semantic is unchanged from P458's
drone spawn, which likewise sets the field server-side.

---

## 3. End-to-end drone-build proof (kept feature)

The bucket-spawned constructor drone still builds its station end to end:
`PlayerBuildCommandLiveTests` (the full construction arc — Build command →
P163-165 BuildingEffect → 40 s tick → rock→station transformation, builder
consumed, friendly ship docks at the new station) passes after the removal.
Separately, the new P470 tests cover the refusal and the still-buyable cases.

---

## Verification (this task)

- Whole-solution build: `/home/domingo/.dotnet/dotnet build` — **0 warnings,
  0 errors.**
- New tests fail on unfixed code (the discriminating assertions):
  - `P470RemovePlayerFlyableBuilderTests.BuyLoadout_for_a_constructor_hull_is_refused`
    — before P470 the constructor buy succeeded and swapped the ship's hull.
  - `P470ShopFiltersOutDroneHullsTests.Shop_offer_does_not_list_any_drone_hull`
    — before P470 the offer listed every drone hull.
- `A_normal_player_hull_is_still_buyable` (server) and
  `Shop_offer_still_lists_normal_player_hulls` (client) prove the gate is not
  over-broad.
- `grep -rn "SetBuilderTarget\|BuilderTargetsFor\|IsBuilderConstructible" src tests`
  returns **nothing**.
- Full `/home/domingo/.dotnet/dotnet test` — see run table below (3
  consecutive runs). Only the pre-existing documented live-wire timing flakes
  are acceptable; each seen is named.

### Full-suite runs

Four full `/home/domingo/.dotnet/dotnet test` runs were taken. Run 1 was the
development baseline (the initial gate made the drone-hull refusal
unconditional, which over-refused same-hull relaunches — see §2 / the related
finding) and exposed 9 genuine P470-caused failures; refining the gate to
native's `hullChanged` guard eliminated all 9. Runs 2–4 are the post-fix
"3 consecutive clean runs": **zero P470-caused failures in any of them** — the
only failures are the pre-existing environmental live-wire timing flakes
(socket/CPU contention under a full-suite run), whose membership shifts from
run to run and which all pass in isolation on both this tree and the pristine
P459 worktree. MonoGame (972) and Lobby (12) were 100% clean in every run.

| Run | MonoGame | ClientCore | Core | P470 regressions | Nature of the failures |
|-----|----------|-----------|------|:---:|------------------------|
| 1 (pre-fix baseline) | 5 fail | 7 fail | 9 fail | **9** | 9 P470-caused (buy of drone spawn hull, fixed by the `hullChanged` gate) + 12 pre-existing |
| 2 | 0 | 2 | 8 | **0** | pre-existing live-wire flakes only |
| 3 | 0 | 4 | 8 | **0** | pre-existing live-wire flakes only |
| 4 | 0 | 3 | 9 | **0** | pre-existing live-wire flakes only |

**Flakes seen and named** (all fail in isolation AND on the pristine P459
worktree, or are the documented machine-state/socket flakes; none reference any
P470 symbol or exercise the P470 buy/shop surfaces):

- `LiveTurretGunnerBindingTests.Board_binds_gunner...` — documented
  pre-existing machine-state flake (also the P285-run list).
- `ConquestTerritoryLiveTests.*` (×3) and `GameOverPlayersWireTests.*` (×2) —
  conquest/game-over live tests, proven to fail identically on the pristine
  worktree.
- `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
  `ShipDeleteGoneLiveTests.DockOut_...` / `CrossSide_...`,
  `FullSessionKillIntegrationTests.FullSession_ThreePeers_...` — ClientCore
  live-network tests, proven to fail identically on the pristine worktree.
- Run-varying contention flakes seen once or twice: `LiveWinConditionTests`,
  `StationUnderAttackAlertLiveTests`, `LiveStationRescueTests`,
  `AutoReloadOnEmptyTests`, `ThreePeerSeededSessionTests`,
  `LiveRelaunchTests`, `LiveMoneyTests.Board_refund` (the documented
  socket-contention flake).

---

## Out of scope (honoured)

- P458/P459 drone spawning and the builder AI — untouched.
- Miner hulls: the GroupId rule also removes player-buyable miners, which is
  native-correct (every real Miner carries group -1 in PCore001). Kept.
- No other P285-adjacent feature was found or removed beyond the list in §2.
  The one dependency found (P422's use of the P285 wire as a fixture helper)
  was adapted as documented in §2.

## Related finding (reported, not changed — out of P470 scope)

Hull id 1 in PCore001 is **"Adv Miner"** (GroupId = -1), and it is the lowest
`HullId`, so the idiom `HullTypes.Values.OrderBy(HullId).First()` — used as a
generic test/player spawn hull by ~150 test fixtures **and** by the server's
default spawner (`Server.cs:611,727`) — yields a drone hull. This is the reason
the P470 buy-gate initially broke many relaunch/undock tests: their fixtures
put the player on a miner and then issued BUY_LOADOUT for that (now correctly
refused) hull. Resolved by gating the refusal on `hullChanged` (native's own
guard) rather than editing the ~150 fixtures, so a same-hull relaunch is
allowed while a SWITCH to any drone hull is refused. The deeper fact — that
the port's default spawner hands players a Miner — predates P470 and is a
separate native-fidelity concern outside this task's scope; it is reported here
for a follow-up rather than silently changed.

## Files

See the commit on `master` (not pushed). New tests:
`tests/Allegiance.Core.Tests/P470RemovePlayerFlyableBuilderTests.cs`,
`tests/Allegiance.ClientCore.MonoGame.Tests/P470ShopFiltersOutDroneHullsTests.cs`.
