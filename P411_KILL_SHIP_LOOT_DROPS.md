# P411 — KILL_SHIP_LOOT_DROPS

A destroyed ship now drops leftover parts / ammo / a carried flag as lootable
treasures, exactly as native `FedSrvSiteBase::KillShipEvent` does
(`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:11686-11762`). The port's
ship-death path previously ran only the *later* half of `KillShipEvent`
(credit/respawn/KILL_SHIP messaging) and never the earlier treasure block.

## Exact insertion point

`src/Allegiance.Server/Server.Combat.cs` — `BroadcastKillShip`, immediately
after the victim is resolved to a `Ship`, guarded on the root-ship check:

```csharp
if (victim.ParentShip is null)
    DropKillShipLoot(victim, now);
```

Why there: `Ship.ReceiveDamage` raises `KillShipEvent`
(`src/Allegiance.Core/Sim/Ship.cs:3434`) while the dying ship's full state
(Parts / Ammo) is still intact — nothing has cleared it yet. Native runs the
treasure block INSIDE the `if (pshipParent == NULL)` guard
(`FedSrv.CPP:11644`) and BEFORE the `bKilled`/eject decision
(`FedSrv.CPP:11770`), so a destroyed root ship drops loot regardless of whether
the pilot subsequently ejects or truly dies. Putting the call at the top of
`BroadcastKillShip`, before the P107 `bKilled`/`EjectSurvivor` gate, reproduces
that ordering exactly. A child/turret ship (`ParentShip != null`) gets no
independent drop.

`DropKillShipLoot` (`Server.Combat.cs`, right after `BroadcastKillShip`) ports
the four native legs:

1. **Parts** (`FedSrv.CPP:11686-11696`) — for every still-mounted part, a
   1-in-5 roll (`randomInt(0,4) == 0`) creates a part treasure
   (`dv=100.0f`, `lifespan=60.0f`, the kill-ship call's args at `:11694`);
   the part is terminated either way (in this port the death/respawn path —
   `ChangeHull(Lifepod)` / `FinalizeDeadShip` — clears the loadout, so no
   manual `Terminate` is needed; the parts list is iterated as a snapshot,
   mirroring native's removal-safe `first()` loop).
2. **Ammo** (`FedSrv.CPP:11699-11713`) — if `GetAmmo() > 0`, a 1-in-5 roll
   creates a treasure of the mission's ammo-pack part type
   (`GetAmmoPack()` → the port's faithful stand-in is the core's
   `Pack.PackType == 0` part, found in `mission.Core.PartTypes`), amount = the
   ship's remaining ammo, `dv=100.0f`, `lifespan=30.0f` (`:11709`); ammo is
   zeroed either way (`:11711`).
3. **Fuel** (`FedSrv.CPP:11714-11728`) — structurally identical to ammo with
   `GetFuelPack()`. This port tracks **no runtime fuel pool** (documented
   no-op: `Ship` has no fuel field; `DataHullTypeIGC.MaxFuel` is static-core
   data never tracked live), so the ship's fuel is always 0 and native's
   `if (fuel > 0)` guard is never satisfied — **no fuel treasure is ever
   spawned**. This is a faithful consequence of the port's absent fuel pool,
   not a deviation; it is documented in the code (a comment) rather than
   emitting dead code for a value that cannot be non-zero.
4. **Flag** — see the flag-jettison investigation below.

All three construction sites reuse the existing `Ship.CreateDropTreasure`
path (the port of `CreateTreasure`, `FedSrv.CPP:4324-4396`) — the same one
P144's `DROP_PART` (`Ship.cs`) and P145's `TREASURE_ACK` decline
(`Server.ShipCommands.cs`) already use. Its `dv`/`lifespan` were promoted to
optional parameters (defaults `25.0f`/`120.0f` preserve the DROP_PART /
TREASURE_ACK behavior byte-for-byte); the kill-ship drops pass the native
values (`100.0f`/`60.0f` parts, `100.0f`/`30.0f` ammo) through unchanged. The
construction *math* is untouched. Each created treasure is announced to the
sector with the existing `FM_S_TREASURE(28)` broadcast
(`SendSectorWide(… new TreasureSpawn { Data = treasure.Data } …)`), so it is
visible and pickable live.

`Mission.NextObjectId()` was changed `internal → public` (a widening, no
behavior change) solely so the Server assembly can allocate a treasure id for
the one loot placement it creates directly (the flag respawned at a remote
pedestal station); the part/ammo drops go through `Ship.CreateDropTreasure`,
which already allocates internally.

## Flag-jettison investigation (scope item 2)

Finding: **no jettison/respawn handling of a carried flag existed in the port's
death path.** Prior to this task, `Ship.Flag` was set on flag-treasure pickup
(`Server.Docking.cs:852`) and cleared only on dock-secure
(`Server.Combat.cs:1842`, `pside == psideOld`); a flag-carrying ship that was
destroyed simply kept its `Flag` (a player respawned as a Lifepod would carry
it on), or lost it silently (a bot's ship deleted). Nothing consulted the flag
on death.

So the flag branch is ported too, faithfully to `FedSrv.CPP:11730-11762`:

- Clear the carried flag (`SetFlag(NA)`, `:11735`).
- Universe-radius test (`:11737-11743`): `p.x² + p.y² + p.z²/lm² >
  (1.5·radiusUniverse)²` with `lm`/`radius` read from the core's
  FloatConstants (indices 0 and 1, `igc.h:59-60`; PCore001 decodes
  `lm=0.2`, `radiusUniverse=5000`). If the constants are missing/non-positive
  it defaults to *inside* (the drop leg) — the practically-universal outcome,
  since ships cannot leave the universe radius.
- **Inside universe → drop** (`:11744-11753`): a `c_tcFlag` treasure with
  `TreasureId = sidFlag` (the ORIGINAL side), `Amount = 0`, 10-day lifespan,
  via a new `Ship.CreateFlagTreasure` (same base `CreateTreasure` geometry,
  `dv=100.0f`).
- **Outside universe → respawn** (`:11742` → `RespawnFlag`,
  `FedSrv.CPP:13842-13944`): the real-side leg selects a random eligible
  pedestal station of the flag's own side (capability `c_sabmPedestal`, no
  flag of that side within native's 300-unit "spitting distance") and spawns a
  `c_tcFlag` treasure above it (`position.z += radius + c_fFlagOffset`,
  `c_fFlagOffset = 50.0f`, `igc.h:27`). The SIDE_TEAMLOBBY artifact (-2) leg
  (`FedSrv.CPP:13847-13869`) delegates to `GenerateTreasure` in a random
  neutral cluster — the **explicitly out-of-scope** ambient treasure system
  (P397) — so it is logged and left unspawned, not silently dropped.
- Whichever leg ran, announce whole-mission with `GAIN_FLAG(sideidFlag = NA,
  shipidRecipient = ship, …)` (`:11755-11760`), via the existing
  `BroadcastGainFlag`.

## Out-of-scope confirmations

- The time-based ambient treasure regeneration system
  (`CmissionIGC::GenerateTreasure`, `fsmission.cpp:2555-2582`) is **not**
  built — it remains deferred per P397. The kill-loot drops are combat-driven
  and entirely separate.
- `CreateTreasure`'s construction math is reused unchanged.
- P368–P410 untouched.

## Verification performed

Whole-solution build: `0 warnings, 0 errors`
(`dotnet build -c Debug`).

New live suite `tests/Allegiance.Core.Tests/KillShipLootDropTests.cs`
(`[Collection("LiveNetwork")]`, run sequentially with the other live suites):

1. `Kill_loot_parts_and_ammo_drop_at_one_in_five_odds_statistically` — N=400
   root victims each carrying 1 mounted weapon part + 480 ammo, driven through
   the real `BroadcastKillShip`. Measured: **parts 98/400 = 24.5%, ammo
   81/400 = 20.3%** (expected ~20%; each ratio asserted in [0.12, 0.29], a ~4σ
   band >10σ from 0 and from any guessed probability like 50%). Every surviving
   roll is a real `Treasure` in the correct sector (`ClusterId == FlySector`), with the correct part-type id + amount,
   and `P0` within `0.5·hull.Length + 10` of the death position.
2. `Kill_loot_zero_ammo_never_spawns_ammo_treasure` — 40 zero-ammo kills →
   deterministically **0 ammo treasures** (native `if (ammo > 0)` guard).
   Fuel is structurally always 0 in this port (no fuel pool), so the fuel guard
   holds vacuously and permanently.
3. `Kill_loot_child_ship_does_not_independently_drop_loot` — 40 parented kills
   (each carrying a part + 640 ammo) → deterministically **0 part + 0 ammo
   treasures** (parent-ship-only guard).
4. `Real_kill_loot_is_actually_pickable_by_a_rescue_ship` — LIVE: repeatedly
   kill a real rooted ship carrying a mounted part + ammo near a rescue-capable
   peer until a dropped part treasure appears, fly the peer onto it, and confirm
   the **P145 pickup flow actually consumes it**: the peer receives
   `ACQUIRE_TREASURE(code=Part, id=<weapon>, amount=1)` after 2 kills.

Must-fail-if-removed: with the loot wiring temporarily removed, test 1 failed
(0 drops) and test 4 failed (no loot pickable); tests 2/3 protect their
specific guards. Wiring restored → all 4 green.

Full suite: run 2×; only the pre-existing documented live-wire timing flakes
expected (see commit note for any flake reconciliation).

## Files changed

- `src/Allegiance.Core/Sim/Ship.cs` — `CreateDropTreasure` gained optional
  `dv`/`lifespan` (defaults preserve DROP_PART/TREASURE_ACK); added
  `CreateFlagTreasure`.
- `src/Allegiance.Core/Model/Mission.cs` — `NextObjectId` `internal → public`
  (widening; enables the Server to place the respawned flag).
- `src/Allegiance.Server/Server.Combat.cs` — `BroadcastKillShip` calls
  `DropKillShipLoot` (root-guarded); new `DropKillShipLoot`, `IsOutsideUniverse`,
  `RespawnCarriedFlag`.
- `tests/Allegiance.Core.Tests/KillShipLootDropTests.cs` — the live suite above.

Deliverable status: committed on `master` (not pushed — Domingo pushes).
