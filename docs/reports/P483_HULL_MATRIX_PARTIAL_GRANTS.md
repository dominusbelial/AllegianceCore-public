# P483 — fix the false `partialGrants` in `run_hull_matrix` (requested parts not mounted)

Date: 2026-10-02 · Branch: `master` (committed, not pushed)

## Summary

`run_hull_matrix` reported `partialGrants=2` for every grant-ON row even though
the harness grant bought the hull and tech. P473's `EvaluateGrant`
(`tools/allegiance-mcp/BotManager.cs:823`) is exact — it compares the REQUESTED
part ids against the parts in the server's `BUY_LOADOUT_ACK` — so two requested
parts per bot were genuinely not mounted. Diagnosis (below) pins the cause to
**exactly one real class**:

- **Class 1 (REAL, fixed):** the matrix's loadout assembler
  `Catalog.PartsForWeapon` appended the first (lowest-PartId) Afterburner /
  Cloak / Shield defaults regardless of the requested hull. On hulls lacking
  those slots the server's native `CanMount` gate (which is CORRECT — native
  only mounts a part its hull can host) dropped them, so the matrix was testing
  a loadout that never exists on the hull.
- **Class 2 (ruled out):** the server does NOT drop any part native would mount.
- **Class 3 (ruled out):** the ack DOES carry every mounted part, including
  non-weapon equipment.

The fix makes `PartsForWeapon` request only real defaults that actually mount on
the requested hull (the same `DataHullTypeIGC.CanMountPart` containment the
server gate uses — not re-derived). After the fix, every grant-ON matrix row
reports `partialGrants=0` and `fbought True`.

## 1. Live diagnosis (evidence first)

Same setup as P473: a standalone `Allegiance.Server` (real `PCore001.igc`,
`XXV_simple.igc`, teams 2, debugMode, `testHarness` grant ON with prefix `mcp-`,
money $1,000,000, `ignoreTechGate`) driven by the built `allegiance-mcp` over
newline-delimited MCP JSON-RPC (the `verify_stdio.py` stdio pattern). Matrix:
`hulls=[Adv Fighter, Fighter, Gunship, Scout]`, `weapons=["PW Gat Gun 1"]`
(mask 0x0001 — mounts on all four, so these are the "OK" rows, not the
planner's "weapon does not mount" errors), `sizes=[1]`, `observe_seconds=1`.

The requested loadout per combo was `[PW Gat Gun 1, Booster 1, Sig Cloak 3,
Sm Shield 1]` (weapon + the first Afterburner/Cloak/Shield). The server's
`[loadout]` log shows, for EVERY combo, the same three parts dropped by the
native `CanMount` gate (`shipIGC.h:1028-1031`, mirrored at the P334 hull-swap
hardpoint policy, `Server.ShipCommands.cs:2491`), leaving only the weapon:

| hull (id) | dropped part id | dropped part name | equipment type | requested mount | server log line |
|---|---|---|---|---|---|
| Adv Fighter (15) | 23 | Booster 1 | Afterburner | 0 | `[loadout] 12 ship 8109: part 23 ('Booster 1') has no compatible hardpoint on hull 15 ('Adv Fighter') — dropped (native CanMount gate, shipIGC.h:1030)` |
| Adv Fighter (15) | 34 | Sig Cloak 3 | Cloak | 0 | `… part 34 ('Sig Cloak 3') has no compatible hardpoint on hull 15 ('Adv Fighter') — dropped …` |
| Adv Fighter (15) | 21 | Sm Shield 1 | Shield | 0 | `… part 21 ('Sm Shield 1') has no compatible hardpoint on hull 15 ('Adv Fighter') — dropped …` |
| Fighter (55) | 23 / 34 / 21 | Booster 1 / Sig Cloak 3 / Sm Shield 1 | AB / Cloak / Shield | 0 | `… no compatible hardpoint on hull 55 ('Fighter') — dropped …` (×3) |
| Gunship (22) | 23 / 34 / 21 | Booster 1 / Sig Cloak 3 / Sm Shield 1 | AB / Cloak / Shield | 0 | `… no compatible hardpoint on hull 22 ('Gunship') — dropped …` (×3) |
| Scout (14) | 23 / 34 / 21 | Booster 1 / Sig Cloak 3 / Sm Shield 1 | AB / Cloak / Shield | 0 | `… no compatible hardpoint on hull 14 ('Scout') — dropped …` (×3) |

Ack per combo: `BUY_LOADOUT_ACK hull <h> nParts 1 fbought False fLaunch True
(3 skipped)`. So every bot requested 4 parts, the server mounted 1 (the
weapon), and `EvaluateGrant` correctly flagged a partial grant → both bots per
combo → `partialGrants=2`.

Every one of the 24 dropped-part lines is the SAME three parts on all four
hulls — the signature of a **planner/assembler** defect, not a hull-specific
server behaviour: `Catalog.PartsForWeapon` chose `FirstPart(Afterburner)`,
`FirstPart(Cloak)`, `FirstPart(Shield)` (the global lowest-PartId records)
with no hull-capability check.

### Why only class 1 — classes 2 and 3 ruled out with evidence

- **Class 2 (server drops a part native would mount): NOT real.** The server's
  `CanMount` gate is the faithful port of native `shipIGC.h:1028-1031` and is
  scoped to the hull-change path (`hullChanged && !hull.CanMountPart(partType)`,
  `Server.ShipCommands.cs:2491`). The dropped parts genuinely have no
  compatible slot on these hulls (verified directly:
  `DataHullTypeIGC.CanMountPart`, `PmEquipment` + hardpoint masks — see below).
  When the harness later requests a part that DOES mount, the server mounts it:
  after the fix, Fighter/Scout/Adv-Fighter acks are `nParts 2 fbought True`
  (weapon + the mountable `Lrg Shield 2`), proving the same server path mounts
  a compatible non-weapon part. No drop of a mountable part occurs.
- **Class 3 (ack omits mounted parts): NOT real.** Native builds the
  `BUY_LOADOUT_ACK` loadout from the ship's real mounted parts —
  `ExportShipLoadout`, `FedSrv.CPP:6250-6254` — and the port mirrors it exactly:
  `ackParts = ship.Parts.Select(x => x.ToExpandedPartData())`
  (`Server.ShipCommands.cs:2604`), where `Ship.Parts` is the full equipped list
  including shield/cloak/afterburner/pack bookkeeping (`Ship.cs:542-552`). The
  live ack `nParts 2 fbought True` with the mounted shield proves a mounted
  non-weapon part IS carried in the ack. The reason these parts were absent from
  the ack is that they were never mounted — not that the ack hid them.

So the single real root cause is **class 1**, at its source in
`Catalog.PartsForWeapon` (`tools/allegiance-mcp/Catalog.cs`).

## 2. Fix (class 1, at the source)

`Catalog.PartsForWeapon` now resolves each default equipment type to the first
REAL part of that type that actually mounts on the requested hull via the same
`DataHullTypeIGC.CanMountPart` containment test the server's ApplyLoadout gate
applies (native `CanMount`, `shipIGC.h:1028-1031`; port
`Server.ShipCommands.cs:2491`) — the same "first that fits" policy
`DefaultLauncherPart` already used. A type with no mountable real part is
honestly omitted (never fabricated), matching exactly what the server mounts.
A new private helper `MountableDefaultPart` implements the per-type scan.

- `tools/allegiance-mcp/Catalog.cs`:
  - `PartsForWeapon(…)` — replaced the unconditional `FirstPart(...)` AB/cloak/
    shield appends with `MountableDefaultPart(...)` appends.
  - added `private static short? MountableDefaultPart(StaticCore, EquipmentType, short hullId)`.

Measured against the real corpus, the loadouts now requested per hull:
- Adv Fighter / Fighter / Scout → `[PW Gat Gun 1, Lrg Shield 2]` (a real
  mountable shield; no Afterburner or Cloak fits these hulls).
- Gunship → `[PW Gat Gun 1]` (none of AB/cloak/shield mount — honestly omitted).

The MatrixPlanner's existing "weapon does not mount" planning (which already
emits those combos as specific error rows with a reason, never attempting them)
is unchanged and still covers the 3 default-weapon "does not mount" cases.

## 3. Tests

`tests/Allegiance.ClientCore.Tests/RunHullMatrixPlanningTests.cs` (class 1 —
the only real class; classes 2/3 have no behavioural bug to pin, so no wire/
codec test is warranted — see the ruling-out above):

- `PartsForWeapon_RequestsOnlyPartsThatMountOnTheTargetHull` — for each of the
  four matrix hulls, every requested part must satisfy `CanMountPart` on that
  hull (and the weapon under test is present). **Fails before the fix**
  (`Adv Fighter requested part 23 ('Booster 1') which does not mount …`),
  passes after.
- `PartsForWeapon_UsesAMountingDefaultPerEquipmentType_WhenOneExists` — guards
  the "pick a mounting default, don't just omit the type" intent: the Scout
  (whose `PmEquipment[Shield]=0x011C` hosts `Lrg Shield 2`, id 45) must request
  that real mountable shield. **Fails before the fix** (the global-lowest shield
  `Sm Shield 1` doesn't fit), passes after.

Both proved **failing-first** (failed on the pre-fix tree, then green after the
change).

## 4. Verification

### Live matrix — before → after (same standalone server + MCP stdio)

**Before (pre-fix):** all four OK rows `partialGrants=2`, ack `fbought False …
(3 skipped)`:

```
OK  hull=Adv Fighter   grantedHulls=[15,15] partialGrants=2
OK  hull=Fighter       grantedHulls=[55,55] partialGrants=2
OK  hull=Gunship       grantedHulls=[22,22] partialGrants=2
OK  hull=Scout         grantedHulls=[14,14] partialGrants=2
```

**After (post-fix):** all four OK rows `partialGrants=0`, ack `fbought True`,
`nParts` == requested:

```
OK  hull=Adv Fighter   grantedHulls=[15,15] partialGrants=0
OK  hull=Fighter       grantedHulls=[55,55] partialGrants=0
OK  hull=Gunship       grantedHulls=[22,22] partialGrants=0
OK  hull=Scout         grantedHulls=[14,14] partialGrants=0
```

Post-fix server log: no `has no compatible hardpoint … dropped` lines; e.g.
`BUY_LOADOUT_ACK hull 55 nParts 2 fbought True fLaunch True` (Fighter),
`… hull 22 nParts 1 fbought True …` (Gunship), `… hull 14 nParts 2 fbought True …`
(Scout).

### Build

`/home/domingo/.dotnet/dotnet build` (whole solution): **0 warnings, 0 errors**.

### Full suite — `scripts/run-tests.sh` (P471), 3 consecutive runs

All four assemblies pass clean except a rotating set of pre-existing
live-socket timing flakes — the documented `docs/TEST_FLAKES.md` P471/P472
family — none of which exercise the changed symbols
(`PartsForWeapon` / `MountableDefaultPart` / `RunHullMatrix`), and the two new
P483 tests (`PartsForWeapon_*`) pass in every run.

- Run 1: `ClientCore…FullSessionKillIntegrationTests.FullSession_ThreePeers_…RealCrossSideKill` (documented flake).
- Run 2: `Core…AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` (documented),
  `Core…ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self`,
  `Core…LiveRipcordTests.Re_target_activates_to_requester_only`.
- Run 3: `ClientCore.MonoGame…ChatTargetComposeLiveTests.ComposeDefault_Team_…NotEnemy`,
  `ClientCore…ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone` (documented),
  `ClientCore…FullSessionKillIntegrationTests.FullSession_ThreePeers_…RealCrossSideKill` (documented),
  `Core…CargoBayReloadTests.Reload_NA_terminates_the_cargo_pack` (documented family).

Per-assembly totals: Lobby 12/12 every run; MonoGame 1077/1077 in runs 1–2 and 1076 in run 3 (one `ChatTargetCompose` flake); ClientCore 385–386; Core 3665–3668 (flakes above). No failure is
caused by P483.

## Files changed

- `tools/allegiance-mcp/Catalog.cs`
- `tests/Allegiance.ClientCore.Tests/RunHullMatrixPlanningTests.cs`
- `docs/reports/P483_HULL_MATRIX_PARTIAL_GRANTS.md` (this report)

## Out of scope (honoured)

- The harness grant's design (P473) and real-player purchase rules (P441) —
  untouched.
- No new behaviours/presets in allegiance-mcp (P474) — the P474 scenario paths
  and the 4-part `DefaultParts()` default (pinned by
  `DefaultPartsIsTheRealFourCategoryPlayableLoadout`, used on no-hull-change /
  P474 spawns) are unchanged. This task fixed the run_hull_matrix loadout only.
- Shop UI — no server bug shared with real players was found (class 2 ruled
  out), so no player-visible server change.
