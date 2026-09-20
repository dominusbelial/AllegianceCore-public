# P283 — Wire `Side.DevelopmentTechs` into the station buy-gate

Closes the **P110/P189 disconnect**: the port's station buy-gate
(`Server.StationSellsHull`) — the real decision behind whether a station will
sell a player's current hull (`bNewHull`, `FM_S_TELEPORT_ACK(102)`) — consulted
only the connecting civilization's **static** `TtbmBaseTechs`, and explicitly
NOT the side's live `Side.DevelopmentTechs`. Since P189 landed, researching a
development correctly mutated `Side.DevelopmentTechs` and broadcast it to
clients, but had **zero effect** on what a station would actually sell. P283
makes the two halves talk to each other.

## The fix — exact fields composed into the buy mask

Native `IsideIGC::CanBuy(b)` (`sideigc.h:224-260`) is a pure bitmask-containment
test:

```
ttbmSide  = side.BuildingTechs | side.DevelopmentTechs          // sideigc.h:133-136 GetTechs()
CanBuy(b) = b.RequiredTechs <= (station.GetLocalTechs() | ttbmSide)   // stationIGC.h:383-391
```

**BuildingTechs survey (scope item 1):** this port has **no** `BuildingTechs`
field — `Side.DevelopmentTechs`' doc (confirmed by reading `Side.cs`) states the
other half, `BuildingTechs`, is explicitly out of scope. `Side.DevelopmentTechs`
is seeded from the civ's base techs at side construction
(`Mission.Assemble` → `new Side(..., TechTreeBitMask.FromBytes(civ.TtbmBaseTechs))`,
and `sideigc.cpp:102` in native) and only ever ORs in genuinely-new bits on
completion (`CompleteDevelopment`, P189) — so it is always a super-set of the
civ base mask. Consequently the port's faithful stand-in for native
`side.GetTechs()` (the whole `BuildingTechs | DevelopmentTechs` term) is simply
`side.DevelopmentTechs`, and it subsumes the P110-era static `civ.TtbmBaseTechs`
approximation.

New composition (`Server.cs`, `StationSellsHull`):

```
buyMask = stationType.TtbmLocal | side.DevelopmentTechs
sellable(hull) = hull.TtbmRequired ⊆ buyMask
```

`StationSellsHull` signature changed from `(DataStationTypeIGC, DataCivilizationIGC,
DataHullTypeIGC)` to `(DataStationTypeIGC, Side, DataHullTypeIGC)`, and the caller
(the `FM_C_DOCKED(48)` teleport handler) now resolves the ship's `Side` object
rather than just its civilization.

**Stale docstring corrected (scope item 2):** P110's docstring justified the
static-civ approximation with *"this port has no tech-tree/development simulation —
sides never accrue developments"* — false since P189. Replaced with the accurate
P283 composition narrative above.

**Duplicate-gate sweep (scope item 3):** grep for every `TtbmBaseTechs` and
`CanBuy`/`SellsHull`/`RequiredTechs` usage across `src/`. `StationSellsHull` is the
**only** buy-gating consumer in the port (the other `TtbmBaseTechs` sites are the
civ decode `DataCivilizationIGC.cs`, the `Side` construction seed `Mission.cs`, and
the `CompleteDevelopment` effect handling in `DevelopmentLogic`/`Bucket.cs` — none a
buy gate). No other duplicate gate exists to fix.

## Out of scope (honoured)

- Non-development bucket completion effects (station/hull/part/drone type) —
  P119's separate named follow-up; not built here.
- Client "team tech" UI for bucket donation — doesn't exist yet.
- No role/permission gate on research spending — native has none (confirmed:
  `BUCKET_DONATE(64)` and `CanBuy` carry no role check), so none was added.

## Verification — real live proof (wire, both sides)

New discriminating live test `LiveBuyGateDevelopmentTests.Researched_development_flips_the_station_buy_gate_on_the_wire`
over a real in-process `Srv` + real TCP `FltBot`s, real PCore001.igc data:
dev 37 **'Bomber'** (ttb=120s, price=10000) unlocks hull 25 **'Bomber'**
(price 500) at the garrison for civ 1 Dreghklar.

```
BUYGATE-P283: hull 25 'Bomber' at garrison 1051: dev 37 'Bomber' unresearched
  -> bNewHull=true (wire); BUCKET_DONATE completed it (+1 bit, 28->29,
  SIDE_TECH_CHANGE(75)); same garrison -> bNewHull=false (wire)
```

1. **Before (wire):** a docked ship holding hull 25 teleports to the garrison while
   dev 37 is unresearched → `TELEPORT_ACK bNewHull=true` (garrison does not sell it).
2. **Real completion:** dev 37's bucket is fully funded for real via `BUCKET_DONATE`
   (P198 per-player wallet), build time elapses → `OnBucketCompleted` →
   `CompleteDevelopment` ORs the new bit into `Side.DevelopmentTechs`
   (28 → 29 bits, asserted) and broadcasts `SIDE_TECH_CHANGE(75)` (asserted on the wire).
3. **After (wire):** a second docked ship holding hull 25 teleports to the **same**
   garrison → `TELEPORT_ACK bNewHull=false` (garrison sells it now).

**Must-fail-if-reverted (RED/GREEN):** with `StationSellsHull` temporarily reverted
to the old civ-base-only gate, both teleports report `bNewHull=true` and the test
fails exactly on the AFTER assertion ("hull 25 MUST be buyable after dev 37 completes")
— the test is genuinely discriminating, not an always-pass.

## Build / test status

- Whole-solution build: **0 warnings, 0 errors** (Debug).
- Full `Allegiance.Core.Tests` suite, 3 consecutive runs (3153 total):
  - Run 1: 2 failed, 3151 passed
  - Run 2: 5 failed, 3148 passed (3 extra are intermittent live-network flakes)
  - Run 3: 2 failed, 3151 passed
  - Every failure is **pre-existing and unrelated to P283**, reproduced identically on
    clean master (HEAD `8c21858`): the known `LiveTurretGunnerBindingTests`
    timing flake, and a `StationsUpdateLiveTests` station-visibility family failure
    (hardcoded `SideId 0/1` in `SeatTwoBases`). Intermittent run-2 additions
    (`ConquestTerritoryLiveTests`/`GameOverPlayersWireTests` cross-mission, plus the
    sibling `StationsUpdateLiveTests.Visible_station...`) all pass in isolation —
    confirmed flakes. `LiveBuyGateDevelopmentTests` passes consistently across all runs.
