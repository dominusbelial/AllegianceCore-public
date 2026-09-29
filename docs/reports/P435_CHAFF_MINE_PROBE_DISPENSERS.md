# P435 — real chaff/mine/probe dispensers: resolution, mounting, shop UI, tools, firing

Date: 2026-09-29
Branch: master (committed, not pushed — Domingo pushes)
Prereqs: P433 (launcher resolution/mounting/firing) and P434 (shop UI) landed.

## Summary

P433 closed the loadout gap for the 56 GENUINE missile launchers in
`StaticCore.Launchers` (those whose `ExpendabletypeId` resolves in
`core.MissileTypes`). The other 67 records in the SAME table are real
chaff/mine/probe dispensers — confirmed real (verified against PCore001: 123
launchers = **56 missile + 8 mine + 6 chaff + 53 probe**, exactly the "67"
Domingo scoped) — but they could never be *equipped*: the server's
`ApplyLoadout` dropped any launcher id that wasn't a missile, the shop only
listed the 56 missile launchers, and neither tool could resolve them. Their
*firing* engine (`HandleFireExpendable` → `DeployMine/DeployChaff/DeployProbe`)
already existed (P182/P224) and was already live-tested — what was missing was
the whole "close the loadout gap" leg P433 built for missiles. P435 generalizes
P433's exact bridge/pattern to the three remaining expendable kinds, per the
brief ("reuse and generalize P433/P434, don't re-derive").

The four expendable kinds are now all supported through the identical pipeline:
real resolution → real server `ApplyLoadout` mounting → real shop row → real
tool loadout → real firing.

## The generalization/parallel-implementation choice (the report's central question)

Following P433's own precedent — **"share the build primitive, parallel the
name-resolution"** — P435 generalizes the SHARED construction primitive in Core
and keeps the per-tool *name-resolution* in parallel twins:

### Core (shared build primitive) — `src/Allegiance.Core`
- `DataPartTypeIGC.FromLauncher` is generalized into a single private
  `FromLauncherDef` body plus four public wrappers:
  `FromMissileLauncher` / `FromMineLauncher` / `FromChaffLauncher` /
  `FromProbeLauncher`. Every expendable kind's embedded `LauncherDef`
  (`LauncherName`, `LauncherPartMask`, `LauncherMass`, `LauncherTtbmRequired`,
  `LauncherTtbmEffects`, `LauncherGroupId`, `LauncherDescription`,
  `LauncherSignature`) spells the mountable part identically, so the four
  wrappers differ only in those scalars + the chosen construction-only subtype:
  missile → `MissileLauncher` (`ET_Magazine`), mine → `MineLauncher`
  (`ET_Dispenser`), chaff → `ChaffLauncher` (`ET_ChaffLauncher`), probe →
  `ProbeLauncher` (`ET_Dispenser`) — each exactly the mount shape the server's
  `FIRE_EXPENDABLE(82)` / `FIRE_MISSILE(70)` handlers resolve at mount 0.
  `FromLauncher` is retained as an alias to `FromMissileLauncher` so the P433
  era call sites compile unchanged.
- `StaticCore.TryBuildExpendableLauncher(partId, out part)` resolves ANY of the
  four kinds by walking `Launchers[partId].ExpendabletypeId` against
  `MissileTypes`/`MineTypes`/`ChaffTypes`/`ProbeTypes` in turn (each launcher's
  expendable resolves in exactly one table). `TryBuildMissileLauncher` now
  DELEGATES to it, filtering to the missile kind — so P433's exact contract is
  preserved (chaff/mine/probe still fail it), while the server and both tools
  get one generalized primitive.

This is the "share the build primitive" half. It is deliberately NOT the place
name-resolution lives, matching P433.

### Server — one mount path for all four — `Server.ShipCommands.cs`
`ApplyLoadout`'s P433 fallback (`!PartTypes.TryGetValue` → build launcher)
switches from `TryBuildMissileLauncher` to the generalized
`TryBuildExpendableLauncher`. A real mine/chaff/probe dispenser now mounts
through the SAME authoritative `ApplyLoadout` path a missile launcher does —
the real P334 `CanMountPart` gate (on hull change) and P314 255-part cap apply
unchanged to the constructed part. The grant log line now reads
`granting real launcher part <id> ('<name>', <EqType>@<mount> subtype <Subtype>, ammo <n>) — P435`.

### Shop — all four kinds as real rows — `src/Allegiance.ClientCore.MonoGame`
- `LauncherOffer` gains a `Kind` (`LauncherKind { Missile, Mine, Chaff, Probe }`),
  and `ShopCatalog.TryLoad` now resolves the whole `Launchers` table via
  `TryBuildExpendableLauncher`, labelling each offer by its construction-only
  subtype (`LauncherKindUtil.KindOf` — the single subtype→kind mapping). So the
  shop goes from "56 missile launchers" to "all 123 real launchers" (the 67 new
  dispensers included), each with its real name + real ammo.
- `ShopModel` is unchanged in logic (it already operated on the generalized
  `_launchers` list); its cursor/activate/`LookupPart` machinery now naturally
  covers all four kinds — a bought dispenser toggles as a real part id with its
  real ammo and is fit-flagged via the SAME `CanMountPart` gate as any part.
- `ShopLayout` renders each row with a kind marker (`[MISSILE]`/`[MINE]`/
  `[CHAFF]`/`[PROBE]`) so a player can tell a missile launcher from a dispenser,
  and the `FilteredFor` P284 tech gate applies identically to all four kinds.

### Tools — one generalized `launcher` argument covers all four kinds
Decision: **a single generalized `launcher`/6th-arg** (not separate per-kind
arguments), mirroring "share the build primitive" — the name-resolution twins
were already standalone per tool, so extending each twin to the four-kind
candidate set is the natural, lower-surface change. Documented per tool:
- `tools/allegiance-mcp/Catalog.cs`: `ResolveLauncher` now resolves across ALL
  four kinds (exact-over-substring, same ambiguity policy; the message became
  "no launcher matches"). `Launcher` gains a `Kind`. `AppendLauncher` uses
  `TryBuildExpendableLauncher` and reports the kind ("mounted real mine
  dispenser '…'"). `DefaultLauncherPart` stays a missile launcher (the standing
  combat default; a caller names a mine/chaff/probe explicitly). `ResolveLauncherName`
  resolves any kind. The `launcher` argument on `spawn_bot`/`spawn_scenario`/
  `run_hull_matrix` therefore accepts a real mine/chaff/probe dispenser by name
  too.
- `tools/vizfight/Program.cs`: `ResolveMissileLauncher` → generalized
  `ResolveLauncher` (four kinds, private `LauncherKind` twin); `AppendCompatibleLauncher`
  uses `TryBuildExpendableLauncher`; `DefaultMissileLauncher` stays the missile
  default. `FullInventoryParts`/`attach`'s 6th positional arg accepts any kind.

### Firing — confirmed complete, nothing new needed
The brief asked to "confirm (and complete if genuinely incomplete)" real firing
per kind. Confirmed genuinely COMPLETE and already live-tested (P182/P224): the
human client already binds C → `SendFireExpendable(ChaffLauncher)` and N →
`SendFireExpendable(Dispenser)`, and the server's authoritative
`HandleFireExpendable` resolves the mounted dispenser by construction-only
subtype (ChaffLauncher/MineLauncher/ProbeLauncher), deploys the real object and
decrements ammo — proven by the pre-existing ChaffTests live suite. What P435
ADDED is the missing equippable half: a real dispenser can now be resolved,
bought and MOUNTED through the real loadout path, after which the existing fire
engine acts on it. No new dispenser auto-fire for bots (chaff/mine/probe have no
natural targeting moment the way missiles lock; the human keys + the new live
tests are the proof standard).

## Real data confirmed (PCore001.igc)

123 `core.Launchers` = 56 missile + 8 mine + 6 chaff + 53 probe (67 new, as
scoped). All 123 have disjoint PartIds from the 117 `core.Parts`. Dispensers
carry the identical embedded `LauncherDef` shape as missiles (decoded per kind
in `DataMineTypeIGC`/`DataChaffTypeIGC`/`DataProbeTypeIGC`).

**Honest data fact (chaff + CanMountPart):** chaff dispensers carry `LauncherPartMask`
0x0001/0x0002 and, under the port's `CanMountPart` gate (non-weapon equipment
tests `PmEquipment[eqType]`, i.e. `PmEquipment[ChaffLauncher]=PmEquipment[0]`),
fit NO hull in PCore001 (no hull's `PmEquipment[0]` covers that mask). This is a
real corpus/approximation fact, not a defect I introduce. Consequences, handled
honestly:
- On the SAME-hull loadout path (the shop's default, no hull swap), P334's
  `CanMountPart` gate is skipped and the chaff dispenser mounts freely (the
  free-mount path) — proven live.
- On a hull-SWAP, the real gate drops a chaff dispenser; the shop's
  `ChosenPartsThatDontFitEffectiveHull` flags it for a different chosen hull
  exactly like any non-fitting part.
- `AppendLauncher` (tools) reports a chaff dispenser as gun-only on any hull —
  never a fabricated chaff. Mine (mask 0x0400/0x0800) and probe (mask 0x0001)
  dispensers DO fit some hulls.

## Tests

### Unit — `LauncherResolutionTests` (Allegiance.ClientCore.Tests), real PCore001
- Updated: `UnknownLauncherThrowsASpecificError` now expects "no launcher matches".
- New per-kind resolution facts: `ResolvesReal{Mine,Chaff,Probe}DispenserByExactName`
  (each resolves to a real `core.Launchers` id whose expendable is a real
  mine/chaff/probe, with the right `Kind`).
- New `DispenserResolutionAndHullGatedMounting` [Theory] over mine/chaff/probe:
  resolution + hull-compatibility-gated mounting in isolation — `AppendLauncher`
  mounts a fitting dispenser into a loadout with its real ammo; for a
  non-fitting hull (and for chaff, which fits no hull) it falls back to gun-only
  with no fabricated part.

### Unit — `ShopLauncherCatalogueTests` (Allegiance.ClientCore.MonoGame.Tests), real PCore001
- Updated: `CatalogueCarriesRealLaunchersWithRealNamesAndAmmoAcrossAllKinds`
  (was missile-only) now asserts each offered launcher's expendable resolves in
  the table its `Kind` claims; `BoughtLauncherRendersAsASensibleLauncherRow`
  uses the kind marker.
- New: `CatalogueOffersAllFourKindsWithTheRightSubtypeMapping`;
  `MovingOntoADispenserRowBuysItWithItsRealAmmo` [Theory] over mine/chaff/probe;
  `DispenserRowRendersWithItsKindMarker` [Theory] (`[MINE]`/`[CHAFF]`/`[PROBE]`).

### Live — `DispenserEquipFireLiveTests` (Allegiance.ClientCore.MonoGame.Tests), [Collection("LiveNetwork")]
`BuyMountAndFireARealDispenser_Live` [Theory] over mine/chaff/probe — the full
end-to-end proof per kind over a REAL in-process server + real
`ConnectionSession`: opens the real shop on a dock, SPACE-buys the dispenser
with its real ammo, L launches (BUY_LOADOUT fLaunch), the authoritative server
GRANTS + mounts it (read back from the server-side ship), then a real wire
`SendFireExpendable` makes `HandleFireExpendable` deploy the real object (a
`Mine`/`Chaff`/`Probe` in the ship's sector) and decrement the ammo by 1.

## Build + full test suites (each run twice)

- Whole solution `AllegianceCore.slnx -c Release`: **0 warnings, 0 errors**.
- `Allegiance.Lobby.Tests`: 12/12 both runs.
- `Allegiance.ClientCore.MonoGame.Tests`: **858/858 both runs** (incl. the 13 P435
  shop unit tests + the 3 P435 live equip+fire tests; the pre-existing
  `ShopLauncherPurchase_GrantsTheRealLauncher_Live` now incidentally buys a real
  probe dispenser — the first launcher that fits some hull — reinforcing P435).
- `Allegiance.ClientCore.Tests`: run 1 → 2 failures, run 2 → 2 failures — the
  exact pre-existing documented live-wire flakes (ShipDeleteGone dock,
  JoinFlow boot-miner regression); zero touch P435 symbols; my 15 launcher
  resolution tests pass both runs.
- `Allegiance.Core.Tests`: run 1 → 7 failures, run 2 → 5 failures — both in the
  pre-existing documented live-wire timing-flake family, ROTATING between runs
  as expected (run 1: LiveMoney, LiveTurretGunner, LiveWinCondition, CloakVisibility,
  Conquest, LiveCountdown, AutoReload; run 2: LiveTurretGunner, LiveRipcord ×2,
  LiveLifepodEndurance, WarpBomb). Each verified to have ZERO references to P435
  symbols; the set is disjoint from and unrelated to launcher/dispenser/shop
  resolution.

## Live verification — real proof per kind (server-side log lines)

A real dev server was driven (fresh `Allegiance.Server` Debug, real Artwork,
`XXV_simple` 2-team mission, docked spawner) and for each kind a real BUY_LOADOUT
named the dispenser and a real `SendFireExpendable` was issued. The SERVER's
authoritative log lines (real stdout) proved resolve + mount + fire for each:

Mine (part 13, 'Mine Pack 1', MineLauncher):
```
[loadout] 1 ship 8098: granting real launcher part 13 ('Mine Pack 1', Dispenser@0 subtype MineLauncher, ammo 2) — P435
[loadout] 1 ship 8098 <- BUY_LOADOUT_ACK hull 1 nParts 1 fbought True fLaunch True
[fireexpendable] 1 ship 8098 fired mine 12 (id 5464) -> FE(87)+EXPORT(26)/OT_mine to 1 same-side peer(s); dispenser 2 -> 1
```
Chaff (part 53, 'Hvy Counter 2', ChaffLauncher) — mounted via the same-hull
free-mount path (the real-data fact that chaff fits no hull under CanMountPart):
```
[loadout] 1 ship 8098: granting real launcher part 53 ('Hvy Counter 2', ChaffLauncher@0 subtype ChaffLauncher, ammo 10) — P435
[loadout] 1 ship 8098 <- BUY_LOADOUT_ACK hull 1 nParts 1 fbought True fLaunch True
[fireexpendable] 1 ship 8098 fired chaff 53 (pos Vector3 { X = 320.0684, Y = 0, Z = 0 } vel ...) -> FE(87)+CREATE_CHAFF(109)+[0×MISSILE_SPOOFED]+END_SPOOFING(111) to 1 cluster peer(s); dispenser 10 -> 9
```
Probe (part 1, 'Teleport Probe 1', ProbeLauncher):
```
[loadout] 1 ship 8098: granting real launcher part 1 ('Teleport Probe 1', Dispenser@0 subtype ProbeLauncher, ammo 2) — P435
[loadout] 1 ship 8098 <- BUY_LOADOUT_ACK hull 1 nParts 1 fbought True fLaunch True
[fireexpendable] 1 ship 8098 fired probe 6 (id 5464) -> FE(87)+EXPORT(26)/OT_probe to 1 same-side peer(s); dispenser 2 -> 1
```

Each is the P433-standard proof: a real wire `BUY_LOADOUT` is granted by the
authoritative `ApplyLoadout` (resolved via `TryBuildExpendableLauncher`), and a
real wire `FIRE_EXPENDABLE(82)` is accepted by the server's real gate — it
deploys the real object and consumes real ammo. The same flows are pinned as
repeatable automated live tests (`DispenserEquipFireLiveTests`).

## Deliverables

1. Real mine/chaff/probe dispenser support — resolution, mounting, shop UI,
   `allegiance-mcp`/`vizfight` loadouts, and confirmed firing — committed on
   `master` (not pushed; Domingo pushes).
2. This report: `docs/reports/P435_CHAFF_MINE_PROBE_DISPENSERS.md`.

## Out of scope (unchanged, per brief)

- The 56 real missile launchers' own resolution/mounting/firing (P433/P434) —
  untouched beyond the natural generalization (`TryBuildMissileLauncher` still
  rejects non-missiles).
- No new mine/chaff/probe BEHAVIOR (detonation radius, countermeasure
  effectiveness, etc.) — the firing engine (P182/P224) is reused as-is; this
  task is the same "close the loadout gap" pattern as P433.
- The `CanMountPart` gate itself — reused exactly; chaff's fit-no-hull-in-core
  outcome is a real data fact reported honestly, not patched.
