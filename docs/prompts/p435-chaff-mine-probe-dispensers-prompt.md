# P435 — real chaff/mine/probe dispensers, same treatment as P433/P434's missile launchers

## Why this task exists

Domingo, after confirming real missiles now work end-to-end (P433/P434):
*"can you scope the other 67 [of core.Launchers' 123 records] are
chaff/mine/probe dispensers, we want to port them all."* P433/P434
closed the gap for the 56 GENUINE missile launchers (whose
`ExpendabletypeId` resolves in `core.MissileTypes`); the remaining 67
records in the SAME `StaticCore.Launchers` table are real chaff/mine/
probe dispensers whose `ExpendabletypeId` resolves in `core.ChaffTypes`/
`core.MineTypes`/`core.ProbeTypes` instead — confirmed real, distinct
data tables, each carrying the SAME embedded `LauncherDef` shape
(`LauncherName`, `LauncherPartMask`, `LauncherTtbmRequired`, etc.) that
`DataMissileTypeIGC` already has (`DataMineTypeIGC.cs`,
`DataChaffTypeIGC.cs`, `DataProbeTypeIGC.cs` — verified against the real
corpus, same alignment-corrected embedded-LauncherDef pattern P433
already decoded for missiles). This task generalizes P433/P434's exact
approach to these three remaining expendable kinds.

This task assumes **P433** (launcher resolution/mounting/firing) and
**P434** (shop UI) have already landed — reuse and generalize their
exact bridge/pattern rather than re-deriving it; if either hasn't landed
when this is picked up, stop and say so.

## Confirmed real building blocks (mirror, don't re-derive)

- `StaticCore.Launchers` (123 total `DataLauncherTypeIGC` records) —
  ALREADY fully decoded; P433 only ever filtered to the 56 whose
  `ExpendabletypeId` resolves as a missile. The other 67 are equally
  real and already sitting in the same table, just never resolved
  against `MineTypes`/`ChaffTypes`/`ProbeTypes`.
- P433's shared Core bridge (`DataPartTypeIGC.FromLauncher`,
  `StaticCore.TryBuildMissileLauncher`) — generalize this (or add
  parallel `FromMineLauncher`/`FromChaffLauncher`/`FromProbeLauncher`
  + `TryBuildExpendableLauncher`-style resolution, your call which
  shape is cleaner) so a dispenser launcher becomes a real mountable
  `DataPartTypeIGC` the exact same way a missile launcher does.
- The server's `ApplyLoadout` P433 fix (`Server.ShipCommands.cs`) —
  extend its fallback so a dispenser launcher partId also resolves and
  mounts, not just a missile one.
- P434's shop `LauncherOffer` pattern (`ShopCatalog.cs`/`ShopModel.cs`/
  `ShopLayout.cs`) — extend to also list dispenser launchers, same
  tech-gate (`RequiredTechsBuyable`) and hull-compatibility
  (`CanMountPart`) filtering already applied to missile launchers.
- **Firing may already partly work**: `Game1.FlightInput.cs` already
  sends `SendFireExpendable(EquipmentType.Dispenser)` (bound to N,
  labeled "drop mine/probe" in an existing comment) — investigate
  exactly what this currently does and does NOT cover (does it
  correctly fire whichever dispenser is actually mounted? Does chaff
  have its own real key/path, e.g. `SendFireExpendable(EquipmentType.
  ChaffLauncher)`, already wired from a much earlier task — check
  `EquipmentType`'s real enum values, `ChaffLauncher=0` per the native
  `AGCEquipmentType` P433 already cited). Don't assume broken — confirm
  exactly what's real and what (if anything) is missing before adding
  new firing code.

## Scope

1. Real resolution for all three remaining dispenser kinds (mine,
   chaff, probe) — mirroring `ResolveLauncher`'s exact-over-substring +
   specific-error policy, generalized or triplicated as makes sense for
   the codebase (your call, document the choice, matching P433's own
   "share the build primitive, parallel the name-resolution" precedent
   if that's still the right shape here).
2. Real loadout mounting for all three, through the SAME `ApplyLoadout`
   path missile launchers now use (server-authoritative, real
   `CanMountPart` gate, real 255-part cap — no new parallel mounting
   mechanism).
3. Real shop UI rows for all three — buyable, tech-gated,
   compatibility-checked, exactly like a missile launcher row.
4. `allegiance-mcp` and `tools/vizfight`: extend whichever loadout-
   building entry points P433 added (`spawn_bot`'s `launcher` arg,
   `FullInventoryParts`) to also accept these three dispenser kinds —
   your call whether one generalized `expendable`/`launcher` argument
   covers all four kinds (missile + mine + chaff + probe) or whether
   separate arguments read better; document the choice.
5. Confirm (and complete, if genuinely incomplete) real firing for each
   kind — a real wire request that the server's own real gate
   (ammo/mount/cooldown) authoritatively accepts or refuses, the same
   proof standard P433 used for missiles.

## Explicitly out of scope

- Any change to the 56 real missile launchers' own resolution/mounting/
  firing (P433/P434) — already correct, don't touch beyond whatever
  natural generalization is needed to add the other 67 alongside them.
- Any new gameplay mechanic beyond making these three real, already-
  data-complete expendable kinds actually purchasable/mountable/
  fireable — no new mine/chaff/probe BEHAVIOR (detonation radius,
  countermeasure effectiveness, etc.) if that's already implemented
  elsewhere; this task is the same "close the loadout gap" pattern as
  P433, not a mine/chaff/probe feature redesign.

## Verification — real proof, not just build/test

- **Live proof, required, for EACH of the three kinds separately**:
  against a real running dev server, resolve, buy/mount, and fire a
  real mine, a real chaff dispenser, and a real probe — confirm via
  real server-side log lines (mirroring P433's `[loadout] ... granting
  real ... launcher` and `[firemissile]`-style proof) that each
  genuinely mounts and genuinely fires/deploys.
- A test proving resolution + hull-compatibility-gated mounting for all
  three kinds in isolation (mirroring `LauncherResolutionTests`).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. Real mine/chaff/probe dispenser support — resolution, mounting,
   shop UI, `allegiance-mcp`/`vizfight` loadouts, and confirmed firing —
   committed on `master` (not pushed — Domingo pushes).
2. Report doc `docs/reports/P435_CHAFF_MINE_PROBE_DISPENSERS.md`: the
   exact generalization/parallel-implementation choice made for each of
   the four expendable kinds now supported, and the live verification
   performed for each of the three new ones.
