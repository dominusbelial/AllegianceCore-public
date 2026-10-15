# P702 — tech trees, native starting parameters and research plans

Base: `master` at `8ab819837fbb92613613571b175e9185e19195ab` (working branch `codex/p702`).
Implemented in the working tree, without commits or pushes. No rig/221/200v200
runs or showcase configuration changes were made. Plan execution remains P703;
loadout selection remains P704.

## Deliverables and commands

`tools/tech-tree` is a headless CLI for any loaded core/civilization. It supports
`dump`, `validate`, `author`, and `sweep`. The commands and runtime configuration
are documented in [the plan directory](../../data/showcase-plans/README.md).
The tool is included in the solution build.

- Ten [PCore001 civ trees](../parity/tech-tree/) include starting techs and
  purchases; required/effect/local masks as numbered bits; civ-scaled development
  prices/time; attribute multipliers by GA index; station successors,
  constructor IDs and rock masks; drone types; purchases gained at each
  completion; full final purchase catalogs; and direct purchase dependency
  edges for the reachable closure, including redundant alternatives.
- Four JSON plans live under `data/showcase-plans/PCore001`: civ25 and civ3,
  `plan_a` and `plan_b`. A favors cheaper currently available completions; B
  favors more expensive ones. Each plan covers all four paths, advanced bases,
  garrison successors and non-builder drone types. Constructors are covered by
  their corresponding station steps.
- The [matchup table](../parity/tech-tree/PCore001-matchup-sweep.md) covers all
  45 unordered pairs / 90 mission sides. It boots a generated mission for each
  tuple, validates/selects plans using its actual sides, reports funding and
  serial bucket times, station completion timestamps and purchase catalogs.
  The same entry point accepts explicit tuples, including triples. Tests boot
  Dreghklar/Technoflux and Phoenix/Bios/Belters tuples as well as the showcase
  civs. The sweep deliberately does **not** estimate economy-driven tier times:
  income, mining, constructor travel/build effects and combat are not simulated.

Example invocations from the repo root, with `CORE` set to a core file:

```sh
dotnet run --project tools/tech-tree -- "$CORE" dump
dotnet run --project tools/tech-tree -- "$CORE" validate data/showcase-plans
dotnet run --project tools/tech-tree -- "$CORE" sweep data/showcase-plans all
dotnet run --project tools/tech-tree -- "$CORE" sweep data/showcase-plans '25,3;1,2;4,35,101'
```

Authored plan coverage (both orders independently validate):

| Civ | Plan | Developments | Station types | Non-builder drone types | Total steps |
|---|---|---:|---:|---:|---:|
| 25 Iron Coalition | plan_a / plan_b | 141 | 11 | 5 | 157 each |
| 3 Ga'Taraan | plan_a / plan_b | 162 | 13 | 6 | 181 each |

## Native rules and implementation

All native references below are relative to
`~/Development/Allegiance/src/`, read from the local native checkout.

| Rule | Native source | Implementation |
|---|---|---|
| Shipyard/Expansion/Tactical/Supremacy permission bits are 1/2/3/4 | `Igc/igc.h:572-575` | `ResearchSettings.StartingTechs`, MSB-first mask storage |
| All paths default enabled | `Igc/igc.h:1246-1249` | `GameConfig` and `ResearchSettings` defaults |
| Enabled paths OR into civ base techs before `UpdateInitialTechs` and `CreateBuckets`; disabled developments use civ no-dev techs | `Igc/missionigc.cpp:3869-3899` | `Mission.Assemble` seeds side, stable `InitialTechs` snapshot and catalog from the same mask; no-dev mode has no construction buckets |
| Join sync sends current development and stable initial catalog masks separately | `FedSrv/fsmission.cpp:4255-4258`, `Igc/sideigc.h:219-222` | `SendSideTechToPeer` sends `side.InitialTechs`, including mission path permissions, even after research/test grants |
| Initial miner count defaults to 1 | `Igc/igc.h:1214,1330` | `games[].initialMinersPerTeam`, validated 0–127 to fit native char |
| With developments enabled, buy the configured count of the first miner type the home station can buy, docked at home | `FedSrv/fsmission.cpp:2196-2215` | Server startup chooses per side/home station rather than one core-wide miner type |
| Development bucket cost/time scaled once at creation; station/drone costs unscaled | `Igc/bucketigc.cpp:66-74` | `ResearchTree` uses civ GA DevelopmentCost/DevelopmentTime and truncates like native buckets |
| Development tech effects are ORed; attribute effects multiply | `Igc/sideigc.h:194-210`, `Igc/igc.h:935-946` | Pure plan state ORs techs; dump reports original GA factors, existing server performs real completion |
| Side techs combine development and building effects | `Igc/sideigc.h:133-158,274-287` | Planner recomputes effects from retained owned station types |
| Development prerequisites are global; station/drone prerequisites can use one owned station's local mask | `Igc/sideigc.h:237-280` | `ResearchState.CanApply`; local masks are never combined into an imaginary super-station |
| Hull/part purchases use required ⊆ side techs OR station local techs | `Igc/stationIGC.h:383-391` | Shared `RequiredTechsBuyable`, also used by the existing server hull purchase gate; launcher records are resolved through `TryBuildExpendableLauncher` |
| Obsolete development is tech-only with known effects | `Igc/developmentigc.cpp:32-47`, `Igc/developmentigc.h:110-113` | Existing `DevelopmentLogic` used by the planner |
| Obsolete station successor check uses development techs plus predecessor required bits | `Igc/stationtypeigc.h:185-195`, `Igc/sideigc.h:250-254` | Existing `SuccessorUpgrade.StationSuccessor`; successor station types remain separate core identities in the tree |
| A funded station bucket produces a constructor waiting for orders; station build resolves available successor | `FedSrv/FedSrv.CPP:11343-11380` | `station` means fund → constructor → rock order → station completion, not instant building-tech grant in the future executor |
| Builder orders require a compatible asteroid capability | `Igc/stationtypeigc.h:207-210`, `Igc/shipIGC.h:2051-2057,2085`, `Igc/shipIGC.cpp:1955` | Station plan steps select one bit from the loaded type's build mask |
| Drone completion cannot directly create builder pilots | `FedSrv/FedSrv.CPP:11518-11529` | Generated scripts use station steps for builders |

Stock-core verification: both civ25 and civ3 can reach Supremacy, Tactical,
Expansion and Shipyard with native-default permission bits. With all four
permissions disabled, neither civ's generated plan can construct these four
branches. The bits are therefore necessary in this core, not cosmetic flags.
Tests exercise all 16 combinations of the path settings at mission assembly.
No new implementation branches on a civ ID, core station/development ID or hull
name; those identities occur only in core data, plan data and known-fact tests.

## Reachability and bucket compatibility

The dumped/generated tree means reachable through the civ's own research and
construction, retaining prior stations and assuming enough unoccupied suitable
rocks and funding. It does not assume captures. The closure also reports
redundant alternatives; a particular valid ordered plan omits tech-only
alternatives that become obsolete. Purchase dependency edges identify direct
consumers of granted/local bits, while completion rows identify actual newly
buyable purchases in the selected order. Raw hull gate queries include NPC hulls;
capability/group metadata lets P704 distinguish human loadouts from drone roles.
The [role legend](../parity/tech-tree/README.md) maps the native hull flags to
boarding, rescue, lifepod, fighter, station-threat, carrier, miner and builder
capabilities; parts carry their native equipment role.

The existing runtime bucket list intentionally remains a compatibility
extension, rather than being silently renumbered by P702. Native
`Igc/sideigc.cpp:123-175` computes ultimate techs assuming capture of every part
and station, and `:187-189` excludes TeamMoney and developments outside that
ultimate set. The port's `BucketCatalog.ForSide`/`Describe` instead retain the
pre-existing synthetic ship bucket, all non-obsolete developments and the
TeamMoney bucket used by the port's prosperity implementation (P302). Removing
those here would change the live/client bucket identity contract and remove
that existing prosperity route; this task does not migrate that wire/catalog
contract. This is a documented remaining native parity difference, not a claim
that the list matches native. Both producers still share identical ordering and
skip behavior for identical initial tech inputs. P702's tree and scripts never
use the synthetic bucket or TeamMoney and enforce actual research prerequisites
rather than treating catalog membership as reachability. Native capture-based
bucket eligibility must not be used to generate own-economy plans.

## Downstream contract

P703 consumes `ResearchPlan` schema version 1, the JSON files and
`mission.ResearchPlans[sideId]`. Opt in with `games[].researchPlanDirectory` and
optional `researchPlans` keyed by side ID; missing selections mean `auto`.
The actual mission side supplies its civ. `auto` chooses the first authored plan by name or emits a clear no-authored-plan message and generates a fallback.
`generated` forces fallback; an unknown explicit name fails. All files for the
loaded core are checked at boot against that core; the selected plan is checked
again against the mission's path settings. A stale identity or impossible step
reports the file, plan/civ and offending one-based step. Matching core header
version and exact core item names are required. JSON uses source-generated
serialization compatible with the server's AOT path.

Steps are `develop`, `station`, `drone`, or `wait`, with core ID and exact name.
Station steps carry a single asteroid capability bit. Expected unlocks carry
kind/id/name/role identities and must actually be purchasable after the step.
Waits support a nonnegative duration and a tech-mask condition that must already
be satisfiable in script order. Plans are keyed by one civ, and selections and
telemetry are keyed by side ID; no schema or selection loop assumes two sides.

The validator models permanent retention of earlier stations. It validates
prerequisites and obsolescence, not economy, asteroid inventory, maximum drones,
construction travel or enemy action. P703 must use real wallets and bucket
funding/completion, reserve a suitable currently available rock, respect live
unit caps and react to lost prerequisites. `station` only grants effects in the
pure model after its whole build step, not at constructor funding. This task
adds no commander or loadout behavior.

P704 consumes `ResearchTree.Unlocks(side.Techs, ownedStationTypeIds)` or the
same query using one current dock station. Results include real hull/part IDs,
names and roles; launchers/dispenser parts are included. It must additionally
apply loadout/mount compatibility and human-hull selection rules. It must
re-query current side techs and stations at relaunch, rather than cache a civ's
ultimate purchase set. `Find`, `Nodes`, `ReachableNodes` and `PurchaseDependents`
provide static planning/explanation queries for both follow-ups. All queries
use the caller's loaded core and actual civ.

## Verification

Final build/suite results and isolated repetitions are recorded in
[P702_assets](P702_assets/). The build command uses
`NUGET_HTTP_CACHE_PATH=/tmp/p702-nuget-cache` because the sandbox permits writes
to `/tmp` and the workspace but not the user's normal NuGet HTTP cache.

The deterministic issues found during verification were corrected rather than
classified as flakes:

- The shared JSON-default guard assumed every dictionary had string keys.
  It now serializes a real key using the production key converter; numeric
  side-ID dictionaries work, and guards in all three projects passed 3/3.
- The constructor fixture funded the first catalog station, which can now be a
  future tier because native path permission bits make it theoretically
  reachable. It now picks the first station `side.CanBuy`; the complete
  donation/constructor/build-order proof passed 3/3.
- The join-sync burst still used raw civ base bits for `TtbmInitial`, omitting
  the newly seeded permissions. Sides now retain the actual creation-time
  `InitialTechs`; current tech grants cannot mutate this snapshot. The protocol
  and client catalog agreement checks are included in `wire-final.json`.

- The MonoGame departed-pod loopback fixture placed the pod at the station
  center instead of a valid native landing bay (`Igc/stationIGC.cpp:385-449`).
  It failed 3/3 isolated runs. It now uses the existing `DockTestPlacement.Inside`
  helper, without changing its gather assertion or timeout; all three scenarios
  passed in each of three isolated runs (9 cases, `mono-final-focused.json`).

The final full build succeeded with **0 warnings / 0 errors**. Fixture-only
rebuilds also succeeded. New and affected methods were run three times in
isolation; `focused-final.json`, `wire-final.json`, `guards-final.json`,
`station-final.json`, `mono-final-focused.json` and `extensibility-final.json`
record the final repetitions. The extensibility test checks required plan names
and validates every file, so adding another race's plan does not require changing
an authored-plan-count assertion.

| Run | Passed | Failed | Skipped | Disposition |
|---|---:|---:|---:|---|
| Core, first full run | 4,570 | 5 | 0 | Two deterministic issues fixed; three network flakes passed 3/3 alone |
| Core, rebuilt full run | 4,574 | 1 | 0 | Rotation receipt flake passed 3/3 alone |
| ClientCore, full run | 1,059 | 1 | 0 | Slow-hull warp fixture passed 3/3 alone |
| MonoGame, final standalone full run | 2,208 | 0 | 3 | Existing optional Meshy-pack skips; includes the real 605s human-flight soak |
| Additional local opt-in proofs | 5 | 0 | 0 | Normal/debug capture, two combat baselines, acquisition/transit reproduction; standard windows, 20m 2s total |

The first MonoGame invocation produced no TRX summary while its fixture was being
rebuilt and is treated as incomplete. The final standalone run completed with
**2,208 passed / 0 failed / 3 existing optional Meshy-pack skips**. Its duration
was 15m 32s, including the existing real-wall 605s human-flight soak.
`mono-final.json` records the complete counters.

The full suites enabled local P542 projectile and P660 15v15 proofs. The extra
proof run explicitly enabled the otherwise gated non-221 P496/P539-baseline/
P539c drivers. Rig/221/200v200 drivers remained gated. No assertions or timeouts
were loosened. Native landing-bay placement corrected the pod fixture; it is not
classified as a flake. Known transient failures and isolated evidence are in
[TEST_FLAKES.md](../TEST_FLAKES.md).

The CLI validation passed all **4 authored** and **10 generated** PCore001 plans;
the sweep completed **45 tuples / 90 sides**. Raw test TRX/log files are under
`/tmp/p702-results` and `/tmp/p702-*.log`; portable counters and failure details
are preserved in this report's asset directory.
