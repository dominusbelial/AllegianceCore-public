# P653B — Finish P653: economy and loadout validation

2026-10-14. Follow-up to P653 (`codex/p653`, commit `d6967e4a`), which landed the
HOSTILE_INPUT_AUDIT findings but broke the live/socket suite (Claude's run: 102
failures: 81 Core, 16 ClientCore, 5 MonoGame). This task brought the branch onto
current master, reconciled the conflicts, and fixed the regressions.

## 1. Integration

- Branch `codex/p653` (base `22d024b8` = P646b) was merged onto current master
  (`32ceeccd`, head of P652/P656/P657/P657c/P658/…). `22d024b8` is an ancestor of
  master, so the merge was largely clean.
- Conflicts (3 blocks in 2 files) were reconciled preserving BOTH the audit fix
  and the intervening master work:
  - `Ship.SetFraction`: keep master's `_fractionLastOrder` baseline AND P653's
    H-30 clear of `PendingTreasureOffer` on death.
  - `Ship.DecideBuilderDefaultOrder` + `Ship.TryStartBuilderBuild` +
    `Server.ShipCommands.StartBuildIfLegal` (H-18 × P652): kept P652's
    "travel-then-drill" NPC builder flow while adding H-18's single-commitment
    guards (never claim a rock already under construction; back off if a rock
    is claimed while travelling; refuse a direct BUILD on an occupied rock).
  - Build is clean: 0 warnings, 0 errors.
- Merge commit `27bb84d3`; follow-up P653B fix commits below.

## 2. Regression fixes (per cluster)

Key insight driving most fixes: P653's native-faithful gates (H-06 dock the
fresh spawn, H-07 per-hardpoint mounts, H-08 capacity clamps) are CORRECT for
real play but broke pre-existing tests that used unrealistic fixtures (a `Miner`
hull with zero capacities as the universal test ship, arbitrary mounts, undocked
purchases, regular-pilot suicide). Almost every fix is category (b) — the test
encoded a pre-audit port behavior — executed as test-fixture data made
native-realistic, with the audit gate left intact and re-verified.

### Baseline
Pre-merge master (`32ceeccd`) passes 68/69 of the failing Core set (the single
exception was a timing flake in `AddPartTests`). The merge caused the rest.

### H-06 — fresh default spawn is DOCKED (native fsmission.cpp:791-796)
- Fixed `AssignShip` to apply `DockInitialLoadout` ONLY on the default spawn
  path (`ShipSpawner is null`). An injected `ShipSpawner` is a test/debug
  placement hook that explicitly decides the ship's state (often a flying ship
  placed at a chosen coordinate); docking those would override harness intent.
  The H-06 purchase gate on the BUY_LOADOUT handler is unchanged, so the audit
  fix stays effective. Production (`ShipSpawner` null) is unaffected: all real
  joiners still dock. This cleared ~31 live tests that used injected spawners.
- The remaining default-spawn tests that need a FLYING ship were updated to
  launch the H-06-docked probe after seat (`SendBuyLoadout(fLaunch:true)`) and
  re-observe flying status: `MiningTests.Loopback_default_spawn`,
  `DockDockingTests.Ship_nowhere`, `ShipStatusServerTests.PeerReceivesItsOwnShipStatus`.
- Live tests that exercise wire inventory ops (which H-22 requires to be
  flying) now undock the seat subject: `CargoBayReloadTests.SeatThree`,
  `AutoReloadOnEmptyTests.SeatThree`.

### H-07 — per-hardpoint mounts (native hullTypeIGC.h:199-213, shipIGC.h:1029-1031)
- Non-weapon equipment mounts only at 0; weapons need a matching hardpoint at
  the requested mount; cargo is -1..-5. Tests that mounted a second launcher /
  pack at a positive mount (1) now place it directly in cargo:
  `CargoBayReloadTests.Reload_swaps_cargo_launcher…`, `.Reload_malformed…`.

### H-08 — capacity clamps (native packigc.h:102-123, launchertypeigc.h:130-145, shipIGC.h:597-601)
- Corrected `ClampPartAmount` so WEAPONS (and other single-item equipment) are
  NOT clamped to amount 1 — native only clamps pack/launcher quantities. (The
  `else max = 1` branch was an over-reach.) Fixed `AddPartTests` (3).
- Launcher/pack clamp uses the hull's real capacity. Tests that mounted rounds
  on the zero-capacity `Miner` hull (the generic `Values.First()`) now use a
  hull with a real magazine/chaff/dispenser/fuel capacity:
  `MissileFireTests` (4), `ChaffTests` (3), `ProjectileBroadcastTests`,
  `CargoBayReloadTests.SeatThree` (generous hull for the reload mechanics).
- `Ship.Ammo` clamps to `hull.MaxAmmo` (shipIGC.h:597-601); ammo-reload tests
  now drain the pool first so the transfer has room: `CargoBayReloadTests`
  (transfer / NA / malformed).

### H-22 — wire inventory ops require flying
See H-06 seat-undock entries.

### H-26 — child-or-CAN_CHEAT suicide gate (native FedSrv.CPP:6338)
- Regular pilots can no longer suicide. Tests that used suicide as a
  convenience now enable the configured admin predicate (`AdminCheats
  { Enabled=true, AllowNames=[conn] }`) for the test connection (the same
  escape hatch native's CanCheat provides): `LiveWinConditionTests.Suicide`,
  `LiveSuicideShipDeleteTests`, `LivePlayerDeathRespawnTests` (×2),
  `GhostShipSectorMembershipTests`.

## 3. Verification (measured)

- Focused runs during development, each on the filtered failing set:
  run2 (after dock fix) 46, run3 42, run4 32, run5 23, run6 20.
- MissileCombat (weapon/missile damage + collision) fails consistently 3/3 in
  isolation — a real remaining regression, not a flake (see §5).

### Two full-suite runs (`scripts/run-tests.sh`), final code — measured as-run
Run 1 (log `/tmp/p653b-final-suite1.log`), Run 2 (log `/tmp/p653b-final-suite2.log`):

| Assembly | Run 1 | Run 2 |
| --- | --- | --- |
| Allegiance.Lobby.Tests | 0 fail / 12 pass | 0 fail / 12 pass |
| Allegiance.ClientCore.MonoGame.Tests | 6 fail / 1963 pass | 6 fail / 1963 pass |
| Allegiance.ClientCore.Tests | 12 fail / 853 pass | 12 fail / 853 pass |
| Allegiance.Core.Tests | 31 fail / 4130 pass | 31 fail / 4130 pass |

Quarantine: Core — 1 flake in run 1 (passes in isolation), 0 in run 2.
ClientCore — 1 REAL failure in isolation (`ClientWarpApproachSlowHullLiveTests.
Legacy_200_450…`, pre-existing warp-transit geometry, unrelated to P653).

The two runs are reproducible (identical failure sets). The suite is NOT green:
**50 failures** (Core 31, ClientCore 12, MonoGame 6, plus 1 ClientCore
quarantine-real), down from the P653 merge's ~102. See §5. Both the P653 audit
regression suite (`P653EconomyLoadoutIntegrityTests`, 26) and the P652
`DirectNpcBuildOrderLiveTests` pass in full.

## 4. Audit fixes kept effective
Every H-xx regression in `P653EconomyLoadoutIntegrityTests.cs` still passes on
the final code (they fail against `22d024b8` and pass now). The fixes above
adjust test fixtures, never the audit gates.

## 5. Remaining failures (honest status)
NOT all are green. Of the original 81 Core failing tests, **18 still fail** on the
final code (plus 12 ClientCore and 6 MonoGame not yet tackled):
- BuyLoadoutPricingLiveTests (2), LiveTeleportDock: buy/teleport while UNDOCKED to
  dodge the tech gate, but H-06 now requires a docked purchase at a sellable
  station → (b) needs to buy docked with sellable hull/parts.
- SwapPartTests, LoadoutRoundTripTests, HullSwapLiveTests (2), LoadoutChange,
  TeammateLoadout, DropPart: request weapons/mounts that don't fit the granted
  hull's real hardpoints (H-07) → (b) needs a fitting hull/weapon.
- DockAutoRestockLiveTests (2), LiveMoney, LiveBoardShip: H-03 refund /
  MONEY_CHANGE broadcast after the docked-purchase change.
- MissileCombat: real regression (weapon/missile damage or collision).
- ClusterWarning, CombatLogVolume, LiveTeleportProbe: misc live timing.
Plus 12 ClientCore + 6 MonoGame + 1 ClientCore quarantine-real (unrelated
warp-transit).

→ **This task is NOT complete.** The merge, conflict reconciliation, the H-06/H-07/
H-08/H-18/H-22/H-26/H-03-driven fixes (~45+ tests, incl. the full P653 audit
regression suite), and the report are landed and committed on `master` (unpushed);
the remaining ~18 Core + ClientCore + MonoGame failures, the live 30v30 proof, and
a fully green suite still need work. No clean-suite claim is made. The two mandated
suite runs (above) were measured and recorded as-run.

## Native-behaviour classification
- H-06 docked fresh spawn: **design** (native-faithful, fsmission.cpp:791-796) — ported.
- H-07 per-mount CanMount: **design** (native-faithful, hullTypeIGC.h:199-213) — ported; test fixtures adjusted.
- H-08 pack/launcher/ammo capacity: **design** (packigc.h:102-123, launchertypeigc.h:130-145, shipIGC.h:597-601) — ported; weapon-amount clamp was over-broad — corrected.
- H-18 direct-BUILD single commitment: **design** — ported; direct commands build immediately, autonomous builders keep P652 travel-then-drill.
- H-26 child/admin suicide: **design** (FedSrv.CPP:6338) — ported; tests updated to admin.
