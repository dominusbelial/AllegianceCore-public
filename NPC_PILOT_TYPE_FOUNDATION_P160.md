# NPC_PILOT_TYPE_FOUNDATION_P160

Status: landed
Date: 2026-09-11
Branch: master

## Summary

This task builds the correct **gate**: an NPC's actual pilot type decides
whether the existing miner decision cycle runs at all. Every previous stage of
the NPC-autopilot/miner arc (P155-P159) forced **every** `IsNpc` ship into the
miner cycle because this port had no pilot-type concept — `Ship.IsNpc`
(`Ship.cs:352`) was the only marker, and `Ship.OnUpdate` ran
`DecideMiningOrSelling` / `ArbitrateAsteroidContention` / the asteroid branch
of `ApplyNpcAutopilot` unconditionally for every NPC.

After P160, a non-miner NPC spawned today does **nothing** autonomous — a
faithful, honest stand-by that matches native exactly: native's
`PickDefaultOrder` (`Igc/shipIGC.h:2216-2463`) dispatches per-type via
`switch (m_pilotType)` with cases only for `c_ptMiner`(2227), `c_ptBuilder`
(2388), `c_ptLayer`(2417), `c_ptWingman`(2436); a type with **no case**
(`c_ptCarrier` notably, or anything unhandled) falls through to
`if (!fGaveOrder) SetCommand(c_cmdAccepted, NULL, c_cidNone)` at
`shipIGC.h:2458-2459` — no order, the ship stands by. This task reproduces
exactly that stand-by for every not-yet-implemented type.

No new pilot-type *behavior* is implemented (no Wingman attack AI, no
Layer/Builder/Carrier logic) — each of those is real, separate, future work.

## The pilot-type values — confirmed against native

Native's authoritative enum is `src/Wopr/Wopr/Constants/PilotType.cs` (a
pre-existing enum in the native tree's own C# interop layer, 19 lines):

    c_ptMiner       = 0
    c_ptWingman     = 2
    c_ptLayer       = 5
    c_ptBuilder     = 6
    c_ptCarrier     = 9
    c_ptPlayer      = 10
    c_ptCheatPlayer = 11

This port mirrors them byte-for-byte as named `byte` constants on `Ship`
(`Ship.cs`):

    public const byte PilotTypeMiner       = 0;
    public const byte PilotTypeWingman     = 2;
    public const byte PilotTypeLayer       = 5;
    public const byte PilotTypeBuilder     = 6;
    public const byte PilotTypeCarrier     = 9;
    public const byte PilotTypePlayer      = 10;
    public const byte PilotTypeCheatPlayer = 11;

### Why `byte` + named constants rather than a C# enum

The task framed the choice: "an enum ... or a short with named constants,
whichever this port's existing conventions favor (check how
`PlayerInfo.PilotType` is typed, `Wire/PlayerInfo.cs:139`, for precedent)."
Precedent settles it decisively:

- `PlayerInfo.PilotType` is a plain `byte` with `public const byte
  PilotTypePlayer = 10;` (`Wire/PlayerInfo.cs:126,139`) — the port's existing
  pilot-type convention is a primitive + named constants, not an enum.
- The direct source of truth this field is derived from,
  `DataDroneTypeIGC.PilotType`, is a raw `byte` (`Data/DataDroneTypeIGC.cs:33`,
  decoded from the real core record at byte offset 376, `:68`). Using `byte`
  on `Ship` makes the derivation assignment-free (`pilotType =
  droneType.PilotType`) with no cast.
- Native's raw enum also carries values outside the seven canonical ones
  (e.g. pilot type **1** = rescue/recon, observed live in PCore001:
  `.Rescue`/`.Recon`/`.SmallRip`). A `byte` field legally holds any value,
  matching native's freedom; an enum restricted to the 7 named cases would
  reject (or mislabel) a legitimately-decodable type. Only the 7 canonical
  values are *named*, matching the task's exact mirroring requirement.

### The property

    public byte PilotType { get; init; } = PilotTypePlayer;

- **Default `Player`(10)** — the correct implicit type of a real player ship.
  Real player ships are never spawned through `SpawnNpcShip` (confirmed:
  `IsNpc` is always explicitly `true` there), so no new sentinel is needed;
  `Player` is correct, not invented. It matches the same value
  `PlayerInfo.PilotType` carries (`Wire/PlayerInfo.cs:126`).
- **`init`-only** — set only at the spawn site, cannot be flipped afterward,
  the same rationale and shape as `IsNpc` (`Ship.cs:352`).

## `SpawnNpcShip` — resolving pilot type from the drone TYPE

Pilot type is a property of the drone **type**, not the spawn site: native
ships get `m_pilotType` from their drone-type definition and `PickDefaultOrder`
dispatches per `m_pilotType`. This port's `DataDroneTypeIGC.PilotType` is the
direct analogue, loaded into `StaticCore.DroneTypes`
(`Model/StaticCore.cs:48,83`), a real, already-parsed source of truth that was
simply never consulted at spawn time. So `SpawnNpcShip` now looks the pilot
type up from `Mission.Core.DroneTypes` — it does **not** accept a raw pilot
type argument ad hoc.

Signature (`src/Allegiance.Server/Server.cs:2671`):

    internal Ship SpawnNpcShip(Mission mission, Sector sector, short sideId,
                               Vector3 position, short? droneTypeId = null)

Resolution:

    byte pilotType = Ship.PilotTypeMiner;
    if (droneTypeId is { } dtId &&
        mission.Core.DroneTypes.TryGetValue(dtId, out var droneType))
        pilotType = droneType.PilotType;
    ... new Ship(...) { IsNpc = true, PilotType = pilotType };

### Which existing call sites were updated and how

**Zero existing call sites required edits.** The `droneTypeId` parameter is
optional (defaults to `null`), and the `null`/absent-from-core path falls back
to `Ship.PilotTypeMiner` directly — behavior-preserving for every existing
caller, which all spawn miner-behaving NPCs. This is the task's explicitly
permitted option: "default to `PilotType.Miner` directly if no drone-type
lookup is convenient at a given call site."

Reasoning for choosing the fallback over threading a concrete drone-type id
through ~18 existing call sites: every existing call site is in a test that
exercises **miner** behavior (or a neutral spawn-and-observe test) and none of
them has a drone-type id in hand; coupling each to a specific core drone id
would be brittle (core-version-specific magic numbers) and pure churn. The
single P160 gate test supplies a real drone-type id explicitly.

The existing call sites, each confirmed to keep spawning a miner-behaving NPC
(via the `PilotTypeMiner` fallback):
- `tests/.../NpcShipFoundationLiveTests.cs:147` (P147)
- `tests/.../NpcAutopilotLiveTests.cs:166,193,233` (P155)
- `tests/.../MinerDecisionCycleLiveTests.cs:162` (P156)
- `tests/.../NpcCrossClusterLiveTests.cs:199,264` (P157)
- `tests/.../NpcCrossClusterMinerDecisionLiveTests.cs:217,300` (P158)
- `tests/.../NpcAsteroidContentionLiveTests.cs:187,188,264,265` (P159)
- `tests/.../OrderChangeCurrentLiveTests.cs:206`,
  `OrderChangeLaunchLiveTests.cs:164,227`,
  `OrderChangeStayDockedLiveTests.cs:145,186`,
  `OrderChangeAcceptedLiveTests.cs:227`,
  `OrderChangeReturnToBaseLiveTests.cs:185,224,253` (P148-P154)

## The gate in `Ship.OnUpdate`

The miner-specific machinery is now wrapped in `if (PilotType == PilotTypeMiner)`
(`Ship.cs:~2690`): the stale-target-clearing-on-full check and
`DecideMiningOrSelling()`. The contention arbitration call is likewise gated
(`if (IsNpc && PilotType == PilotTypeMiner && MiningAsteroidTarget is not
null)`, `Ship.cs:~2741`) — arguably not load-bearing (a non-miner's
`MiningAsteroidTarget` is never populated) but explicit and self-documenting.

**`TryTakeCover` (P151) stays UNGATED.** Native's take-cover applies to any
damaged NPC regardless of pilot type (the `case c_ptMiner` take-cover block is
inside the miner case only in native, but P151's own port deliberately applies
it to any damaged `IsNpc` ship — and this port's `TryTakeCover` call site was
already outside the miner-only scope, `Ship.cs:2670` pre-P160). Gating it
behind Miner would be an incorrect narrowing, so it is left applying to any
damaged `IsNpc` ship. A fresh, undamaged non-miner never triggers it (its
`PlanTarget` stays null, `TryTakeCover` requires `Fraction < 1.0f`).

## Explicitly out of scope

- Any actual Wingman/Layer/Builder/Carrier behavior (attack AI, asteroid
  laying, station building, carrier logic) — infrastructure only.
- `BUILDINGEFFECT_DESTROYED`(181) / any station-construction mechanics
  (separate, later, larger arc — next-phase-plan Tier 3).
- Changing `TryTakeCover`/P151's damaged-ship behavior — stays ungated.
- No change to P103-P159 code beyond the `PilotType` field/constants, its
  resolution in `SpawnNpcShip`, and the miner-block gating in `OnUpdate`.

## New tests

`tests/.../NpcPilotTypeGateLiveTests.cs` (live loopback, no VM):

1. **Negative** `Non_miner_idle_npc_stands_by_despite_minable_asteroid` —
   spawns a real **Wingman** NPC (drone-type id looked up dynamically from
   `Core.DroneTypes` as the first entry with `PilotType ==
   Ship.PilotTypeWingman`) beside a real minable He3 rock; ticks 1000 sim
   steps (past the idle-miner's ≤600-tick target-pick latency) and asserts
   `MiningAsteroidTarget` null, `PlanTarget` null, `Ore == 0`, and no movement
   (< 1u travelled).
2. **Positive control** `Miner_idle_npc_still_picks_the_rock` — identical
   setup, spawned with no drone-type id (the `PilotTypeMiner` fallback, same as
   every pre-P160 caller); asserts it picks the rock and travels — proving the
   gate is behavior-preserving for the miner case.

## Verification

- Baseline re-confirmed: P159 = **2781** tests going in.
- Full suite after P160: **2783** (= 2781 + 2 new), across **5 consecutive full
  runs** (runs 1-5) every run passed **2782** with exactly 1-2 failures, and
  every failure was one of the five **documented timing flakes**
  (`LiveChatSectorWireTests`, `LiveCombatWireTests`,
  `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`,
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
  `ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions`).
- **Flakes proven pre-existing, not my regression**: on the pristine P159
  baseline (changes stashed) `LiveCombatWireTests.Real_Bot_class_observes_its_
  own_kill_over_the_wire` fails in isolation, and
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
  passes solo on baseline but fails solo on occasion on both trees — classic
  timing flakes, reproduced on the untouched baseline.
- Focused mining + gate set (19 tests: `NpcPilotTypeGateLiveTests` ×2,
  `MiningSellTests`, `AnyClusterAsteroidSearchTests`,
  `NpcCrossClusterMinerDecisionLiveTests`, `NpcAsteroidContentionLiveTests`,
  `MinerDecisionCycleLiveTests`): **19/19 green** in one clean run.
- New tests prove the gate works, not just compiles:
  - `Non_miner_idle_npc_stands_by_despite_minable_asteroid` — a real Wingman
    NPC beside a minable rock: `MiningAsteroidTarget` null, `PlanTarget` null,
    `Ore == 0`, travelled < 1u after 1000 ticks.
  - `Miner_idle_npc_still_picks_the_rock` — the positive control: an
    identically-idle Miner picks the rock and travels.

## Files changed

- `src/Allegiance.Core/Sim/Ship.cs` — `PilotType` constants + `init` property;
  miner-block + contention gating in `OnUpdate`.
- `src/Allegiance.Server/Server.cs` — `SpawnNpcShip` optional `droneTypeId`
  + drone-type derivation; doc comment updated.
- `tests/Allegiance.Core.Tests/NpcPilotTypeGateLiveTests.cs` — new.
- `NPC_PILOT_TYPE_FOUNDATION_P160.md` — this file.
