# P488 — Successor upgrades: stations, hulls and parts upgrade to the side's best available version

Date: 2026-10-02
Branch: `master` (not pushed — verified with `git branch --show-current`)
Solution build: 0 warnings / 0 errors (`/home/domingo/.dotnet/dotnet build`)
Full suite: 3 consecutive runs via `scripts/run-tests.sh` — see Results below.

## Summary

The port had been decoding the successor pointers since the data pass but never
consumed them. This task wires the three native `GetSuccessor…` behaviors into
the port, with one shared implementation:

1. **Station successor at build time** — a completed build places the side's
   best *available* station type (`FedSrvSiteBase::BuildStation`, `FedSrv.CPP:11380`
   → `IstationTypeIGC::GetSuccessorStationType`, `stationtypeigc.h:185-195`).
2. **Hull & part successors at purchase** — `ApplyLoadout` grants the requested
   hull/part's furthest buyable successor (`CshipIGC::PurchaseShipLoadout`,
   `shipIGC.h:1004,1033` → `Station::GetSuccessor`, `stationIGC.h:398-433`),
   surfaced in the `BUY_LOADOUT_ACK` and in the P473 harness reporting.
3. **Obsolete filter** — the client shop hides hulls/parts the side has
   out-teched (`!IsObsolete`, `WinTrek/load.cpp:1606,1717,1775,1819`), the term
   P470 left unported; the server buy gate manifests the same rule as the
   successor upgrade (native upgrades, never refuses).
4. **Dock auto-upgrade** — declared the named follow-up **P488b** (see below).

Out of scope honoured: no investment-UI (P487) changes; no new tech data — the
real `PCore001.igc` successor chains are used and listed below.

## Native semantics ported

The shared helper `src/Allegiance.Core/Model/SuccessorUpgrade.cs` is the single
implementation used by the server buy gate, the station-build completion and the
client shop. It transcribes the native code exactly:

- **`Station::GetSuccessor(IbuyableIGC*)`** (`stationIGC.h:398-433`): walk the
  whole `GetSuccessorHullType` / `GetSuccessorPartType` chain — even through
  items the station can't currently buy — and return the *furthest* item that
  *is* buyable (native updates `pSuccessor` only when `CanBuy(pNext)` but
  advances `pNext` unconditionally). So a side that jumped tiers reaches its
  best available hull.
- **`IsObsolete(b)`** (`stationIGC.h:394-396`): `GetSuccessor(b) != b`.
- **`IstationTypeIGC::GetSuccessorStationType(pside)`** (`stationtypeigc.h:185-195`):
  recurse into the *direct* successor only while
  `succ.RequiredTechs ⊆ (thisStation.RequiredTechs | side.DevelopmentTechs)`
  (the side having this station implies it once had its required bits). Unlike
  the hull/part walk, this does not skip a non-researchable intermediate.

Buyability is the existing `TechTreeBitMask.RequiredTechsBuyable(need, local,
sideDev)` containment already used by `Server.StationSellsHull` / the shop
(P283/P284/P468).

## 1. Station successor at build time

`Sector.CompleteBuild` (the port of native `BuildStation`) now resolves
`effect.StationType`'s successor against the building side before placing the
station:

```csharp
var stationType = effect.StationType;
var side = mission.Sides.FirstOrDefault(s => s.Id == effect.SideId);
if (side is not null)
{
    var upgraded = SuccessorUpgrade.StationSuccessor(mission.Core, stationType, side.DevelopmentTechs);
    if (upgraded.StationTypeId != stationType.StationTypeId) { /* log */ stationType = upgraded; }
}
```

A constructor ordered to build a station type the side has researched the
upgrade for builds the successor type (runtime `Station` and the wire
`DataStationIGC` both carry it). A side that has *not* researched it builds the
requested type unchanged (the recursion is skipped, `stationtypeigc.h:185-195`).

## 2. Hull & part successors at purchase

In `ApplyLoadout` (`Server.ShipCommands.cs`):

- **Hull** — after the P441/P470 gate, and only when the hull is *changing*
  (native only runs the successor block when `hullID != current`, `shipIGC.h:995-1004`),
  a docked purchase resolves `grantedHull = SuccessorUpgrade.HullSuccessor(...)`
  and swaps/mounts that. The `BUY_LOADOUT_ACK.HullId` is now the **granted**
  hull (the successor), exactly as native's `ExportShipLoadout` reports the real
  ship.
- **Part** — when a requested part passes the tech gate, a docked purchase
  resolves `partType = SuccessorUpgrade.PartSuccessor(...)` before the hardpoint
  gate, so the mounted part is the successor.
- Non-docked ships keep the requested items (no station to consult — the same
  scope as the existing tech gate, documented in P441).
- Pricing/debit run on the *granted* values (`grantedHull.Price`, the mounted
  successor parts), so the wallet is charged for what is actually bought.

The ack therefore shows the successor — and the P473 harness surfaces it:

- `Allegiance.Bot` already decodes `BuyLoadoutAck` (P473).
- `BotManager.WaitForAck` / `EvaluateGrant` (P473) previously treated any
  `granted.HullId != requested` as a **refusal** (throw). P488 teaches the
  harness to recognise a legitimate successor grant (walking the requested
  hull's real successor chain via `IsSuccessorOf`) and report it as an
  informational warning ("granted the SUCCESSOR hull … — the side has out-teched
  it, native GetSuccessor"), never a refusal throw. `run_hull_matrix` /
  `spawn_bot` therefore show the real granted (successor) hull per row.

## 3. Obsolete filter

Native's hangar hides a hull/part the side has out-teched
(`WinTrek/load.cpp:1606,1717,1775,1819`: `GroupID >= 0 && CanBuy && !IsObsolete`).
P470 ported the first two terms; P488 adds the `!IsObsolete` third term to the
client shop (`ShopCatalog.FilteredFor`) for hulls, parts and launchers. The
filtered catalogue now also retains the parsed `StaticCore` (needed to walk
successor chains); a manually-constructed catalogue with no core degrades
gracefully (no obsolete filter). The **server** buy gate enforces the same rule
the native way — an obsolete purchase is upgraded to its successor (see §2),
not refused.

## 4. Dock auto-upgrade → P488b

Native's carrier-land restock walks `GetSuccessorPartType()` for each *desired*
part (`FedSrv.CPP:12831-12854`), gated on the successor's required techs being a
subset of the side's (development | building) techs. The port has no per-part
desired-loadout / inventory infrastructure (documented P117; see
`Server.Docking.cs` LandOnCarrierEvent remarks, updated by this task), so this
restock remains out of reach this session and is the **named follow-up P488b**.
P488 supplies the shared successor *resolution* (`SuccessorUpgrade`) that P488b
will consume; the scalar-pool reset remains the resupply here.

## Real PCore001 chains exercised (from the live dump)

Stations (Garrison/Supremacy/… families — base → successor):
- `Garrison (15) → Garrison (Str) (10)`; `Garrison (44) → Garrison (Str) (45)`;
  `Supremacy (11) → Supremacy (Adv) (7)`; `Expansion (12) → Expansion (Adv) (8)`;
  `Tactical (13) → Tactical (Adv) (9)`; `Shipyard (5) → Shipyard (Dry) (1)`.
  (The station tests pick a pair dynamically from the real core.)

Hulls (player, `group = 0`):
- `Scout (14) → Adv Scout (13) → Hvy Scout (90)` (three-step — exercises the
  "furthest buyable" walk)
- `Lt Interceptor (21) → Interceptor (20) → Hvy Interceptor (19)`
- `Fighter (121) → Enh Fighter (122) → Adv Fighter (123)`
- `Bomber (25) → Hvy Bomber (24)`; `Troop Transport (27) → Hvy Troop Transport (26)`

Parts (weapons, `SuccessorPartId`):
- `PW Lancer 1 (1003) → PW Lancer 2 (1004) → PW Lancer 3 (1005)` (three-step)
- `PW A-B Hammer 1 (212) → PW A-B Hammer 2 (213) → PW A-B Hammer 3 (214)`

Launchers share the part space (e.g. `launch 1 → 600`, `launch 654 → 65 …`) and
resolve their successors through the same launcher table.

## Tests (all fail on unfixed code first; verified red by neutralizing the
shared helper before wiring, then re-green after)

Allegiance.Core.Tests:
- `BuildingConstructionLiveTests.Completed_build_upgrades_to_the_successor_station_type_when_the_side_has_outteched_it`
- `BuildingConstructionLiveTests.Completed_build_keeps_the_base_station_type_when_the_successor_is_not_researched`
- `SuccessorUpgradeTests.HullSuccessor_returns_the_furthest_buyable_hull_in_the_chain`
- `SuccessorUpgradeTests.HullSuccessor_returns_the_requested_hull_when_no_successor_is_buyable`
- `SuccessorUpgradeTests.PartSuccessor_walks_the_part_chain_to_its_furthest_buyable_version`
- `SuccessorUpgradeTests.HullIsObsolete_is_true_exactly_when_a_buyable_successor_exists`
- `SuccessorUpgradeTests.StationSuccessor_advances_only_while_the_direct_successors_tech_is_met`

Allegiance.ClientCore.Tests (live loopback, `Bot` + in-process `Server`):
- `SuccessorUpgradeLiveTests.Buy_loadout_grants_the_successor_hull_when_the_side_has_outteched_it_Live`
- `SuccessorUpgradeLiveTests.Buy_loadout_mounts_the_successor_part_when_the_side_has_outteched_it_Live`

Allegiance.ClientCore.MonoGame.Tests:
- `ShopModelTests.Shop_filter_hides_an_obsolete_hull_and_offers_its_successor_when_outteched`
- `ShopModelTests.Shop_filter_keeps_a_hull_that_is_not_outteched`

Red-first evidence: with the shared helper's resolvers forced to return their
input (and `IsObsolete` therefore always false), the station build test, both
live ApplyLoadout tests and the shop obsolete test all went red (expected-value
mismatches / missing successor), then re-green once the real helper was restored.

## Results

- Whole-solution build: **0 warnings, 0 errors**.
- Full suite via `scripts/run-tests.sh`: 3 consecutive runs. The Lobby (12) and
  MonoGame (1079) assemblies passed **3/3**, and ClientCore passed 2/3 — the
  only failures in every run are the documented live-wire *timing* flakes in
  `docs/TEST_FLAKES.md` (game-over broadcasts, boarding, ready-relay, transport
  flood, seeded visibility, cargo reload); each one passes in isolation and is
  unrelated to P488 (none touches successor/purchase/station-build paths). The
  P488-specific and P488-affected tests are 100% green in every run.

| run | Lobby | MonoGame | ClientCore | Core | observed flake(s) (all pass in isolation; see TEST_FLAKES.md) |
|-----|-------|----------|------------|------|------|
| A   | 12/12 | 1079/1079 | 388/388 | 3673/3675 | `SideEliminationLiveTests.Eliminating_side2…` (ledger ✓); `GameOverPlayersWireTests.Win_by_elimination…` (game-over broadcast timing) |
| B   | 12/12 | 1079/1079 | 387/388 | 3673/3675 | `FullSessionKillIntegrationTests.FullSession_ThreePeers…` (ledger ✓); `InboundFloodSafetyLiveTests.Unsupported_fmid_flood…` (ledger ✓); `LiveWinConditionTests.Board_refused_once_mission_over` (ledger ✓) |
| C   | 12/12 | 1079/1079 | 387/388 | 3674/3675 | `ThreePeerSeededSessionTests.SeedThreePeers…DeterministicVisibility` (ledger ✓); `WarpBombTests.Live_whole_mission_warp_bomb…` (warp live-wire timing) |

Flake methodology: every failing test above was re-run in isolation and passes;
the ledger (`docs/TEST_FLAKES.md`) documents the same live-wire timing class.
No P488 regression appears in any of the 3 runs.
