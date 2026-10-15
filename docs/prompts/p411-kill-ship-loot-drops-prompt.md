# P411 — a destroyed ship should drop leftover parts/ammo/fuel as lootable treasures (native does; the port never wires it)

## Why this task exists

Domingo remembered this from watching old gameplay footage: destroying an
enemy ship sometimes leaves behind a pickup — fuel or missiles. Traced to
a real, confirmed native mechanic that this port's kill-ship path never
calls.

## Confirmed real native mechanic

`FedSrvSiteBase::KillShipEvent` (`~/Development/Allegiance/src/FedSrv/
FedSrv.CPP:11634-11763`) — native's actual ship-destruction handler — runs
this block **before** credit assignment/respawn/messaging, gated on
`pshipParent == NULL` (only for a root ship, not a child/turret):

1. **Parts** (`:11686-11696`): for every part still mounted on the dying
   ship, a 1-in-5 chance (`randomInt(0,4) == 0`) it survives as a lootable
   treasure at the death position; the part is terminated either way.
2. **Ammo** (`:11699-11713`): if the ship had ammo remaining
   (`pship->GetAmmo() > 0`), a 1-in-5 chance it drops as a treasure of the
   mission's real ammo-pack part type (`pmission->GetAmmoPack()`); ammo is
   zeroed either way.
3. **Fuel** (`:11714-11728`): same shape for fuel
   (`pmission->GetFuelPack()`), same 1-in-5 chance, fuel zeroed either way.
4. **Flag** (`:11730-11762`): if the ship was carrying a flag, it's either
   respawned (if the death position is outside the universe radius) or
   dropped as a `c_tcFlag` treasure with a 10-day lifespan.

Each drop reuses native's own `CreateTreasure` builder chain
(`FedSrv.CPP:4324-4396`) — the exact same one this port has already
ported for other purposes (see below).

## Confirmed port gap and what's already there to build on

The port already has the real building blocks, just never wired to ship
death:
- `Treasure : Model` (`src/Allegiance.Core/Sim/Treasure.cs`) — the real
  runtime object.
- `CreateTreasure`-equivalent construction already ported and used for
  **other** flows: `DROP_PART` (`Ship.cs:2698-2710`, "P144 — create the
  part-type treasure a DROP_PART leaves in space") and declining a
  pickup offer (`Server.ShipCommands.cs:1300-1320`, the `TREASURE_ACK`
  decline branch). Reuse this existing construction path — don't build a
  parallel one.
- The port's own ship-death handling (`Server.Combat.cs`'s
  `BroadcastKillShip`, citing `FedSrv.CPP:11918-12022`) only ports the
  **later** part of native's `KillShipEvent` — credit assignment,
  respawn/no-respawn decision, `KILL_SHIP` messaging. The **earlier**
  parts/ammo/fuel/flag-drop block (`:11686-11762`, cited above) is never
  called anywhere in this codebase (confirmed: no `CreateTreasure` call
  site in `Server.Combat.cs`).

## Scope

1. In the ship-death path (`Server.Combat.cs`, wherever `BroadcastKillShip`
   or its caller has the dying ship and its full state before parts are
   cleared), add the parts/ammo/fuel drop rolls exactly as native does:
   1-in-5 chance per part, 1-in-5 chance for ammo (if any), 1-in-5 chance
   for fuel (if any) — each constructing a real `Treasure` via the
   existing `CreateTreasure`-equivalent path, placed in the ship's sector
   at (or very near) its death position. Gate this on the dying ship being
   a root ship (no parent), matching native's `pshipParent == NULL` guard
   — a turret/child ship dying should not separately drop loot.
2. **Investigate and decide on the flag-jettison sub-case**: this port
   already has real flag pickup/secure logic (`Server.Combat.cs:1792+`,
   `Side.Flags`/`Side.Artifacts`). Check whether a ship carrying a flag
   that gets destroyed currently has ANY jettison/respawn handling at all;
   if not, port native's flag drop-or-respawn branch too; if some
   equivalent already exists, just confirm it and don't duplicate it —
   document your finding plainly either way.
3. Confirm the exact odds (1-in-5, i.e. `randomInt(0,4)==0` — a uniform
   pick in [0,4] equals 0) and port them faithfully, not a guessed
   "similar" probability.

## Explicitly out of scope

- The general time-based ambient treasure regeneration system
  (`CmissionIGC::GenerateTreasure`, `fsmission.cpp:2555-2582`) — a
  separate, unrelated mechanic (sectors slowly accumulate treasures over
  time regardless of combat); still deferred per P397, don't build it
  here.
- Any change to `CreateTreasure`'s own construction math — reuse exactly
  as-is.
- P368-P410 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving a destroyed root ship with mounted parts/ammo/fuel rolls
  the drops with the correct 1-in-5 odds each (e.g. seed/mock the RNG, or
  run enough trials to statistically confirm ~20% per category) and that
  each surviving roll produces a real `Treasure` object in the correct
  sector at the correct position.
- A test proving a ship with zero ammo/fuel never spawns a treasure for
  that category (matching native's `if (ammo > 0)`/`if (fuel > 0)`
  guards).
- A test proving a child/turret ship's death does NOT independently roll
  loot drops (parent-ship-only guard).
- **Live proof, required**: destroy a real ship in a live session with
  mounted parts and confirm at least one loot drop appears and is
  actually pickable (dock-adjacent or fly-through pickup, matching
  whatever the existing `Treasure` pickup flow already does).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P411_KILL_SHIP_LOOT_DROPS.md` in the repo root: the exact
   insertion point, the flag-jettison investigation result, and the
   verification performed.
