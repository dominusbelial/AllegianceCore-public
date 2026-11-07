# P942 — Why Phoenix beats Gigacorp: research and combat

The reproduced research gap is a **bot scheduling problem**, not a broken
Gigacorp prerequisite or a native balance defect. The price-sorted generated
plan puts two utility bases ahead of already-buyable Gigacorp research. The
serial commander waits for their constructors to travel, drill and finish.
Phoenix completes seven developments before reaching its own utility-base wait;
Gigacorp completes one in the original P927 match window. With the patched
commander, Gigacorp completes 22–23 developments in the paired 240-second match
window; Phoenix completes 15. Full-tree closure and qualification receipts are
recorded below.

Dispatch base: `6ef26b783ecee5f790fe2e765efc0eb43cd6c318`.
Changes remain uncommitted in the working tree. No pushes, remote connections,
rig runs or rig configuration edits were made. `local.env` is present; artwork
and native-source fixtures run against the local corpus. Raw evidence is under
`.scratch/p942/`; compact step-by-step evidence is
[research-trace.json](P942_assets/research-trace.json).

## Measurement method

All local server/bot measurements use server GC, immutableSnapshot + encodeOnce,
parallelBuild, pool transport, native ship updates and nativeGotoPlan. The
existing P863 clash budget and default-off experimental simulation/broadcast
options are unchanged. These are small functional experiments, not a local
reproduction of 200v200 performance or a statistical balance estimate.

The original P927 proof runs **10/10/10**, civs 18/4/5, fog on, generated
DiamondRing / SingleRing / DiamondRing with recorded P927 seeds, real research
income ×5000 / research time ×16, ordinary bot purchases, staging and the
existing 120-sim-second finale deadline. It runs three full rotations, including
a fresh fourth world. Raw baseline: `.scratch/p942/before-three-side.log`.

The new paired proof uses **15/15**, m2xspiderw, benchmark seed 942, civs **18/4**
and **4/18**, fog on, showHomeSector=true, `loadout_mode=best`, staging and meeting
engagement. A native countdown ends each match at 240 simulation seconds;
finale is disabled. Test-harness wallets and tech grants are disabled. Initial
miners are disabled in both builds, matching P927; normal paydays, player
purchases and constructor actions remain live. The ticker sleeps 20 ms after
each normal simulation pass. Both configurations use the same fifteen-bot composition, pacing and game
settings before and after the strategy change. The file-map fixture's benchmark seed
is not a resource-geometry seed: `Server.Showcase.cs:67-71` passes a seeded RNG
only when a map-rotation receipt supplies one. This fixture uses the existing
unseeded generic-asteroid variant draws (`NativeAsteroidTypes.cs:109-116`),
while authored homes and resource-placement positions stay fixed. Thus its
combat counters are a functional cohort, not a perfectly seeded geometry A/B.
P927's generated-map comparison supplies explicit map seeds and is the seeded
before/after control. The pure prerequisite audit is independent of geography.

Development counts are **actual completion callbacks**, cross-checked against
`[skills] research`, rather than the number of buckets whose Completed flag is
set. Native marks obsolete alternatives complete without purchasing them
(`Igc/sideigc.h:629-633`). Purchases are successful `BUY_LOADOUT_ACK` records,
joined to the server-observed ship side, including rebuys after death. Guns,
missiles, mines and booster are the existing server `[skills]` counters. Booster
counts **activation edges**, not fuel, duration or individual thrust ticks
(`Ship.ActivateAfterburner`, `src/Allegiance.Core/Sim/Ship.cs:9301`). Guns count
projectiles fired, not player trigger pulls or damage. These are side totals,
including native NPC ships. The local proofs explicitly use `best`; showcase
defaults to `varied` (`McpTools.cs:638-646`). The brief does not provide the
rig’s actual loadout inventory or mode. Therefore local selected-part examples
prove legal equipment constraints and opportunities, not an exact attribution
of every rig activation or shot to a particular hull/part.

### Paired two-side results

| Build | Side | Civilization | Actual developments | Guns | Missiles | Booster | Mines | Hulls bought |
|---|---:|---|---:|---:|---:|---:|---:|---|
| Before | 0 | Gigacorp | 4 | 1,214 | 20 | 0 | 3 | Scout 210 ×21 |
| Before | 1 | Phoenix | 7 | 3,372 | 25 | 21 | 1 | Scout 187 ×6; Fighter 190 ×10 |
| Before, swapped | 0 | Phoenix | 7 | 3,407 | 29 | 11 | 1 | Scout 187 ×6; Fighter 190 ×9 |
| Before, swapped | 1 | Gigacorp | 4 | 2,054 | 28 | 0 | 2 | Scout 210 ×22 |
| After | 0 | Gigacorp | 22 | 2,104 | 26 | 3 | 1 | Scout 210 ×20; Patroller 215 ×4; Bomber 204 ×1 |
| After | 1 | Phoenix | 15 | 3,794 | 27 | 17 | 0 | Scout 187 ×6; Fighter 190 ×10 |
| After, swapped | 0 | Phoenix | 15 | 3,245 | 28 | 11 | 0 | Scout 187 ×6; Fighter 190 ×11 |
| After, swapped | 1 | Gigacorp | 23 | 1,719 | 23 | 0 | 2 | Scout 210 ×17; Patroller 215 ×1 |

Baseline raw data: `combat-4-18-20261107T050739.{log,json}` and
`combat-18-4-20261107T050936.{log,json}` in `.scratch/p942/`.
The after samples are `combat-4-18-20261107T051501.{log,json}` and
`combat-18-4-20261107T051658.{log,json}`. The short combat changes are mixed:
Gigacorp gun and missile counts rise in slot 0 and fall in slot 1. The proof
shows earlier legal research/purchases, not a guaranteed combat win or a
monotonic increase in every skill counter.
The six required repeated paired matches each recorded **22 Gigacorp / 15
Phoenix** developments, in both slot orders. The full measurement cohort,
including their combat counters and purchases, is consolidated in
[measurements.json](P942_assets/measurements.json). Unlocks do not retrofit ships already flying:
`BotManager.TechLoadouts.cs:10-11` keeps the ordinary purchase cadence on a
new life or dock visit. Early research therefore improves the next legal
purchase; it does not instantly replace every opening Scout or gun.

### Original three-side baseline

| Match / map | Side / civ | Developments | Guns | Missiles | Booster | Mines |
|---|---|---:|---:|---:|---:|---:|
| 1 / DiamondRing | 0 / Gigacorp | 1 | 1,459 | 13 | 0 | 5 |
| 1 / DiamondRing | 1 / Phoenix | 7 | 3,661 | 33 | 8 | 0 |
| 1 / DiamondRing | 2 / Omicron | 7 | 1,873 | 21 | 5 | 1 |
| 2 / SingleRing | 0 / Gigacorp | 1 | 1,045 | 31 | 0 | 0 |
| 2 / SingleRing | 1 / Phoenix | 7 | 4,152 | 31 | 21 | 0 |
| 2 / SingleRing | 2 / Omicron | 7 | 2,044 | 16 | 12 | 1 |
| 3 / DiamondRing | 0 / Gigacorp | 1 | 1,794 | 23 | 0 | 5 |
| 3 / DiamondRing | 1 / Phoenix | 7 | 3,282 | 29 | 22 | 2 |
| 3 / DiamondRing | 2 / Omicron | 7 | 4,759 | 26 | 23 | 2 |

The baseline three-side proof passed all three rotations, with zero skips.
The after diagnostic winners were Omicron / Phoenix / Omicron; Gigacorp’s
research improvement is not presented as a local win-rate reversal.

### Three-side after the scheduling fix

| Match / map | Side / civ | Developments | Guns | Missiles | Booster | Mines | Hulls bought (id × count) |
|---|---|---:|---:|---:|---:|---:|---|
| 1 / DiamondRing | 0 / Gigacorp | 17 | 1,502 | 22 | 0 | 5 | 210 ×17; 215 ×3 |
| 1 / DiamondRing | 1 / Phoenix | 12 | 4,179 | 33 | 8 | 1 | 187 ×7; 190 ×9 |
| 1 / DiamondRing | 2 / Omicron | 8 | 3,802 | 26 | 10 | 1 | 710 ×6; 715 ×5 |
| 2 / SingleRing | 0 / Gigacorp | 16 | 2,451 | 38 | 0 | 0 | 210 ×28 |
| 2 / SingleRing | 1 / Phoenix | 12 | 4,404 | 43 | 16 | 2 | 187 ×9; 190 ×7 |
| 2 / SingleRing | 2 / Omicron | 8 | 1,246 | 15 | 182 | 0 | 710 ×6; 715 ×5 |
| 3 / DiamondRing | 0 / Gigacorp | 19 | 2,163 | 27 | 0 | 4 | 210 ×17 |
| 3 / DiamondRing | 1 / Phoenix | 13 | 2,922 | 25 | 14 | 2 | 187 ×7; 190 ×9 |
| 3 / DiamondRing | 2 / Omicron | 9 | 3,450 | 25 | 8 | 2 | 710 ×6; 715 ×7 |

Before hull purchases for the three rotations, in side order:

- Match 1: Gigacorp 210 ×15; Phoenix 187 ×7, 190 ×7; Omicron 710 ×8, 715 ×8.
- Match 2: Gigacorp 210 ×24; Phoenix 187 ×10, 190 ×6; Omicron 710 ×5, 715 ×6.
- Match 3: Gigacorp 210 ×18; Phoenix 187 ×7, 190 ×10; Omicron 710 ×7, 715 ×7.

Hull ids: 210/187/710 are the three civs’ Scouts; 190/715 are Phoenix/Omicron
Fighters; 215 is Gigacorp Patroller; 204 its Bomber. All purchases join to an
explicit server side assignment, including fresh ship ids on rotation. Raw
after data: `.scratch/p942/after-three-side-diagnostic.log`; compact data:
[measurements.json](P942_assets/measurements.json).

### Required three-side repetitions

All three required runs passed (nine completed matches), with zero skips and
zero unjoined hull purchases. The complete development tuples in side order
0=Gigacorp / 1=Phoenix / 2=Omicron were:

| Repeat | DiamondRing, match 1 | SingleRing, match 2 | DiamondRing, match 3 |
|---|---|---|---|
| 1 | 17 / 12 / 8 | 17 / 12 / 8 | 19 / 14 / 9 |
| 2 | 17 / 12 / 8 | 17 / 12 / 8 | 18 / 13 / 8 |
| 3 | 17 / 12 / 8 | 17 / 12 / 8 | 18 / 13 / 8 |

Every corresponding gun/missile/booster/mine counter and hull purchase is in
[measurements.json](P942_assets/measurements.json), with the authoritative
game-over receipt. The baseline tuple was 1 / 7 / 7 in all three matches.

## Exact stall and prerequisite audit

`ResearchTree.Generate` (`src/Allegiance.Core/Model/ResearchTree.cs:92`) retains
its existing default price order. Gigacorp's canonical fallback is 168 steps:
149 developments, 14 stations and five drones. Phoenix's 186 steps contain
162 developments, 13 stations and 11 drones. Omicron has 131 developments,
11 stations and five drones. Different reachable counts are intentional;
Gigacorp is not supposed to acquire Phoenix's technology.

Gigacorp's original opening, in plan order:

1. Development 222 **Weedcore 03**, 10 credits / 10 bucket seconds.
2. Drone 41 **.TowerAC**, 1,000 / 90.
3. Drone 200 **.Caltrop**, 1,500 / 90.
4. Station 14 **Refinery (Lt)**, 2,250 / 90, generic build asteroid mask 0x8.
5. Station 47 **Teleport (Lt)**, 2,250 / 90, the same mask.
6. Development 75 **EW Disruptor 1**.
7. Development 84 **PW SkyCap 1**.
8. Development 90 **MRM Seeker 2**.
9. Development 109 **Med Shield 2**.
10. Development 114 **Booster 1**; subsequently **Retro Booster**, **Lt Booster
    1**, cloak, counters, Quickfire, Patroller and Bomber.

Both early stations require bits **20,255**, have **no effect or local tech
bits**, and unlock no purchases. Their constructor is type 27. Gigacorp's
initial Garrison 15 supplies local bit 255; its civilization supplies bit 20.
The blocked developments instead require globally supplied bits 6 and 17 (plus
existing part bits for upgrades): bit 6 is already in both civ base masks, and bit 17
is the initial Garrison effect. The separate path switches occupy bits 1–4
(`Igc/igc.h:572-575`). They do **not** require a Refinery or Teleport.

The P927 baseline stays at step **4/168**, `constructor travelling/building`,
with a funded constructor, available rock and a large unspent wallet. The
240-second two-side proof demonstrates that the station eventually completes,
then waits on Teleport, and only near the end gets through EW Disruptor, SkyCap
and Seeker 2. This rules out an impossible tree node, missing bucket, drone cap,
missing dock, money shortage or commander skipping the development. The
waiting code is `tools/allegiance-mcp/ResearchCommander.cs:208-245`; `_index`
advances only on observed completion. Native constructor travel/drilling is
real work (`Igc/shipIGC.cpp:2795-2822`), not a zero-duration purchase.

Phoenix's first three steps have the same development/tower/mine roles. Six
available developments then cost 2,500 each and precede its 3,000-credit
Refinery: Retro Booster, Seeker 2, Med Shield 2, Lt Booster 1, Counter 2 and
Quickfire 2. Together with Weedcore this is exactly **seven** completions before
its first station wait. The price threshold, rather than a missing Gigacorp
tech prerequisite, creates the 1-versus-7 signature.

The audit replays **every step** of both orders against the actual
`Side.CanBuy` gate, with real station local masks and in-place successor
upgrades. No own-civ prerequisite violation was found. The compact trace records
step, action, id, name, frozen cost/time, required/effect/local bits and the
actual server buy-gate result, for all three civs. The permanent ordering
regression extends the native-gate replay to all ten stock civilizations.

Native rules checked in the read-only checkout, with line numbers re-grepped:

| Native source | Rule and audited port behavior |
|---|---|
| `Igc/civilizationigc.h:63-78`; `Igc/missionigc.cpp:3869-3883` | Seed the civ base/no-development mask and permitted path bits; start at the civ initial station. No shared Phoenix/Gigacorp synthetic tech level. |
| `Igc/sideigc.h:224-280` | Developments use global building + development bits; production needs those bits plus **one individual station's** local bits. Station successors exclude obsolete station types. |
| `Igc/stationIGC.h:383-391` | Hull/part purchases use actual dock-local plus side tech bits. Research unlocks do not bypass the dock. |
| `Igc/developmentigc.h:110-113`; `Igc/sideigc.h:629-633` | Only tech-only developments whose effects are already known become obsolete; obsolete snapshots are not completion callbacks. |
| `Igc/bucketigc.cpp:66-74` | Freeze development cost/time at bucket creation using the civ attributes, excluding team money. Drone/station time is not multiplied by the civ development-time factor. |
| `Igc/hullTypeIGC.h:163-213` | Per-hardpoint/equipment mount masks and magazine/dispenser capacities control equipment; a part being researched does not make it fit every hull. |
| `FedSrv/FedSrv.CPP:11259-11276` | Completion applies tech effects, announces DEV_COMPLETED, applies attributes for non-tech-only development, then may end a prosperity game. |

## Fix and regression evidence

The bot opts **canonical generated fallback plans** into a research-first
schedule (`ResearchCommander.ResearchSchedule`, line 75). The shared generator's
new `prioritizeResearch` option ranks already-buyable developments first, then
stations that add new global technology, then optional production; price/id
remain deterministic ties. Eligibility, successors, per-station locals,
asteroid classes, cost and time formulas are unchanged. Default generator
output, stored/authored plans and their wire content are unchanged. Even a
custom authored plan named `generated` keeps its order when its actions differ
from the canonical fallback.

Gigacorp now completes its 23 initially reachable developments before funding
Outpost (Lt), then Supremacy and the remaining research paths. Phoenix completes
15 before Outpost. Utility bases and drones remain in the exhaustive plan;
they are not deleted or treated as completed, and constructor travel is not
shortened. Extra miner/tower/Caltrop purchases in the generated plan also move
later; configured initial miners and the independent prosperity growth policy
are unchanged. This can reduce early drone-origin mine use. Ordinary
income-multiplier-1 economy performance was not measured by these accelerated
showcase proofs. The existing retention loop (`ResearchCommander.cs:118-136`) can
also rebuild a lost previously completed utility base before resuming research;
the new schedule moves those utility assets past the research critical path.
Rig loss frequency is unmeasured, so this is a code-path implication rather
than a claim about how often it happened in b72. This is generated-plan bot
strategy, not a native simulation rule.
No new hand-authored civilization plans were added.

`P942ResearchCommanderTests.Generated_commander_funds_buyable_research_before_optional_production`
failed **3/3 civ cases** before the fix: expected `develop`, saw `drone` after
Weedcore. The unchanged regression passes against the patched controller.
The prerequisite/order regression also failed before on all three original
civs at `.TowerAC`. Raw failure receipts are
`.scratch/p942/commander-fail-before.{log,trx}` and
`.scratch/p942/ordering-fail-before.{log,trx}`.

## Full research closure

The first no-combat closure proof passed: **Gigacorp 149/149 and Phoenix
162/162 actual developments**. It uses only real research commander clients
and native constructors, with showMap=true to remove fog/exploration and
combat destruction as confounders. There are no test-wallet or technology
grants; the same income ×5000 and research-time ×16 preset supplies normal
paydays and bucket progression. The original diagnostic slept 20 ms between
ordinary fixed simulation steps. Final repeated closure fixtures sleep 5 ms
to bound wall time; this changes test pacing, not simulation steps or
research/travel rules, and is not a throughput measurement.

The fixture stops only when both sides reach their generated own-tree counts.
The final fixture also compares every actual completed development id with
the expected set, excluding obsolete snapshots and duplicate completions.
Short fogged combat matches remain free to end earlier and are reported
separately; full closure does not imply all 149 developments should finish
inside a 120-second finale.

| Initial closure | Allowed / actually completed | First sim-time | Last sim-time |
|---|---:|---:|---:|
| Gigacorp | 149 / 149 | 16.35 | 1,638.25 |
| Phoenix | 162 / 162 | 16.65 | 2,533.8 |

Initial full closure: [closure-initial.json](P942_assets/closure-initial.json);
raw `.scratch/p942/closure-20261107T051515.{log,json}` and
`closure-diagnostic.{log,trx}`. All **three required repeated closures passed**, each with **149/149
Gigacorp and 162/162 Phoenix**, including equality of every actual development
id to its expected set. All closure executions skipped zero tests. The
complete receipts are included in the validation evidence.

## Native intent

| Difference | Classification | Evidence and disposition |
|---|---|---|
| 1 versus 7 developments in short matches | **Bot strategy gap, fixed.** | A price-only exhaustive plan was executed as a strict research schedule. The live wait and actual buy-gate replay show research was already legal. The bot now prioritizes it, with fail-before regressions. |
| Gigacorp's reachable 149 versus Phoenix's 162 developments | **Native civilization design, keep.** | Different base masks, station effects and available definitions in the loaded PCore001; all audited steps satisfy native per-side rules. Equalize progression opportunity, not the civs' tech sets. |
| Phoenix's initial Fighter; Gigacorp's initial Scout-only player hull | **Native civilization design, keep.** | Initial legal catalogs show hulls 187/190 versus 210. Gigacorp's Patroller 215 is unlocked by development 22. The baseline bought only Scout 210; the patch enables a real Patroller purchase. |
| Gun advantage | **Native hull/equipment design, amplified by the fixed schedule.** | Phoenix Fighter 190 has two fixed guns, Scout 210 one; both initial best choices use Gat Gun 33. Research and survival determine later lives and firing time. Counts do not prove a civilization damage multiplier bug. |
| Booster gap | **Native mount/tech design, amplified by the fixed schedule.** | Both Scouts' afterburner mask is 0x10: only the negative-thrust Retro Boosters fit, and the normal forward-boost selector excludes those. Initial Afterburner 125 (0x20) cannot mount. Phoenix Fighter mask 0x3 accepts Booster 23 (0x2; thrust 900), whose required bit 37 is initially present. Gigacorp must unlock Patroller 215 (development 22) and Lt Booster 26 (development 116): Patroller mask 0x11 accepts the light booster (0x1; thrust 450), not Booster 23. Unlocking Booster 114 or Lt Booster alone never gives Scout 210 a forward booster. No native masks or thrusts were edited. |
| Retro booster selection | **Native negative-thrust design under an existing forward-boost policy, keep.** | Retro Booster 188/208 fits Scouts but thrust is −1750/−2005. `TechLoadoutSelector.cs:542-553` scores afterburners by signed thrust and both purchase modes require a positive support score. This avoids mounting a reverse thruster under ordinary chase/escape forward-boost commands; adding a dedicated retro maneuver policy would be a separate strategy extension. |
| Missile gap | **Native capacity/equipment design and ordinary combat opportunity.** | Scout 210 magazine capacity 10, Phoenix Scout 187 capacity 15, Fighter 190 capacity 25. Phoenix also starts with Quickfire part 103: required bit 126 is in its base mask; Gigacorp needs development 235 before buying it. The old plan delays that behind utility construction. Actual lock/refire and aim still gate firing. Unlocks, mount masks, target survival and exposure explain opportunity; no invented fixed ratio is claimed. |
| Gigacorp mines | **Native mount design under existing valid bot duties.** | Gigacorp Scout dispenser mask 0x1c01 accepts Prox Mine 59 (0x800); Phoenix Fighter mask 0x400 does not. Scout/defensive mine duties and locked-role fallback can therefore place mines on more Gigacorp ships. Native layer drones also contribute to the side counter; the fixed schedule delays optional .Caltrop production, so before/after mines also reflect that deliberate strategy change. Mines are not evidence that Gigacorp lacks weapons. |
| Research speed/attributes | **Native civilization design, keep.** | Development-time GA is 1.0 Gigacorp, 1.5 Phoenix, 2.5 Omicron; cost GA is 1.0 for these civs. Other differences include Gigacorp speed 1.1/thrust 0.9/mining capacity 1.25 and Phoenix shield 1.25/mining yield 1.1. Base gun damage is 1.0 for both; Phoenix missile damage is 0.95 versus Gigacorp 1.0, so more launches do not imply greater damage per missile. None were edited. |
| Missing research timing in server logs | **Modern log-only observability, implemented.** | The existing completion and game-over hooks now emit receipts; no wire, client or gameplay change. |

No ported native prerequisite, availability, cost, mount or civilization-data
defect was found in this audit. PCore001 version **990051329**, SHA-256
`f73aa7bbce7414140f6691366f8829ea58829727660a8a86c114b29a83a55de8`.
The persisted [core-comparison.json](P942_assets/core-comparison.json) includes
the hull masks/capacities, every afterburner mask/signed thrust, initial selected
loadouts and named civ base attributes. Native afterburners expose signed thrust
unchanged (`Igc/afterburnerIGC.h:113-119`); fuel uses that signed thrust times
fuel consumption (`Igc/afterburnerIGC.cpp:148-157`), explaining the negative
retro fuel-consumption values too.

Native fields are `Igc/igc.h:1767-1810` (hulls), `:2071-2080` (civilizations)
and `:2649-2707` (stations/developments); their consumers are cited above.
The exact 2.8× booster ratio and 20–8 rig result are observations from the brief,
not consequences calculable from a hull capacity ratio or proven local effects.

## Telemetry

Every actual development callback logs, invariant-culture:

```text
[research-done] side 0 civ 18 'Gigacorp' tech 222 'Weedcore 03' at sim-t 20.125
```

Immediately beside `[kills]` at game over:

```text
[research] match 1 side 0 civ 18: 2 developments, first at 20.125, last at 200.75
[research] match 1 side 1 civ 4: 0 developments, first at none, last at none
```

Times are the supplied absolute simulation clock, not elapsed research wall
time. Counts include real attribute-only and team-money completions; synthetic
ship buckets, station/drone production and obsolete bucket snapshots do not
increment them. Recording precedes the existing effect handler so a winning
team-money completion appears in that same game-over summary. Weak mission
keys prevent an ever-growing retained-match history; the state is sim-thread
owned and naturally separated across parallel worlds and fresh rotations.

`P942ResearchTelemetryTests` uses real stock development buckets, checks two
completions at fractional times under `fr-FR`, repeated updates and game-over
calls, no completion for an obsolete snapshot, an unresearching side, a second
mission, and a freshly rebuilt mission.

## Validation

The report was written before qualification. Focused diagnostic results:
solution build 0 warnings/errors; telemetry and original three ordering cases
4/4 pass; three generated-commander cases 3/3 pass; baseline paired combat and
P927 rotation proof 3/3 pass. All these focused executions have zero skips.
The expanded diagnostic cohort passed **12/12 Core + 5/5 commander cases**,
with zero skips. **All 117 tool projects build with 0 warnings/errors**; the
server Native AOT publish succeeds without IL2026/IL3050 diagnostics.

The dispatch-base original P700 never-parks test passed **5/5**, with arrivals
24, 24, 24, 24, 24 /30 respectively. Every sample held the unmodified five-second parking bound.
The required patched parking cohort is **QUALIFIED FAIL: 4/5**, versus
**5/5** dispatch controls. Attempt 4 reached only 18/30 ships at 480.1 s;
its parking bound held (longest idle with target 1.813 s). All five attempts
held the original five-second parking bound. Supplementary filtered current
and dispatch attempts are retained separately and never replace that required
five-attempt outcome. The three subsequent filtered patched attempts all
passed, reaching 24/30 each, with longest idle 2.409/1.905/1.905 seconds; their
observation windows were 189.1/425.8/391.3 seconds. Supplementary dispatch
controls passed 2/3: the third reproduced the arrival failure (20/30 at 480.1 s),
with its parking bound also holding at 1.311 s. The same arrival signature
therefore exists on dispatch; the required patched cohort remains failed. The live research/measurement
cohort passed **138/138
executions**, zero skips: 42 focused cases ×3 plus six paired matches, three
P927 three-rotation proofs and three exact-id closures.

Exactly **one Fast sweep** completed: **13,287 passed, 10 failed, 3 skipped**
(13,300 executions), 531.4 seconds. All nine infrastructure checks passed.
The three skips are the existing optional private Meshy-pack fixtures; no
artwork-backed proof was skipped. The sweep remains **QUALIFIED FAIL**.
Every new P942 case passed. Six executions exceeded the 10-second Fast limit:
the existing hull GPU proof, missile-lock case, Bios varied-loadout coverage,
nanite support selection, turret-carrier preference, and warp-exit touch-rate
measurement. No threshold or fixture assertion was relaxed.

The functional failures were five known bundle-path/local.env ancestry cases,
the known wingman-defend null-target assertion, a read-only real-profile
restore, a HUD formatting allocation sample, one P660 countdown boundary
(14.90 against [14.94,15.06]), and a missile-lock receipt case that reached
lock 1.000 without observing its required partial-lock tip. Isolated controls
and the final classification are recorded in the [P942 flake-ledger entry](../TEST_FLAKES.md#p942-validation--one-fast-sweep-and-dispatch-controls-2026-11-07-utc) and
[verification.json](P942_assets/verification.json). A passing isolation does
not rewrite the sweep result. The five bundle-path cases, wingman null-target
case and missile partial-tip case persist 3/3 and also reproduce 3/3 on the
dispatch libraries. HUD allocation, P660 and the timing-only offenders pass
3/3 alone; the profile case passes 3/3 with a writable scratch configuration.
The missile case remains both a functional and timing failure.


### Gate account

| Check | Final implementation result |
|---|---|
| Solution build | PASS, 0 warnings / 0 errors. |
| Tools build | PASS, all 117 projects, 0 warnings / 0 errors. |
| Server Native AOT, linux-x64 | PASS; no IL2026 / IL3050 diagnostics. |
| New/affected research cohort | PASS, 42 cases ×3 plus 12 live proof executions = 138 passed, zero skips. |
| Final isolated certification | PASS, all 17 new unit cases ×3 and P702's eight cases ×3, zero skips, with no overlapping test process from this lane. |
| Required paired combat / three-side / full-tree repeats | PASS: six paired matches, nine three-side matches, three 149/162 exact-id closures. |
| Required original P700, five attempts per build | QUALIFIED FAIL: dispatch 5/5; patched 4/5. Parking bound held in all ten; patched attempt 4 missed arrivals. |
| One Fast sweep | QUALIFIED FAIL: 13,287 passed / 10 failed / 3 optional private-pack skips; six timing offenders; infrastructure 9/9 passed. |
| Filtered Fast exceptions and dispatch controls | Persistent path/wingman/missile signatures reproduce on dispatch; other cases pass their filtered controls as detailed in the ledger. |
| Supplementary P700 controls | Patched 3/3; dispatch 2/3, including the same 480-second arrival failure. Parking bound held in all six. |
| Glossary / parity / Compose / hygiene | PASS; 601 glossary paths, four supplemental n/a rows, no added parity credit, Compose config valid, no new media/private code paths. |

No training, HUD or client presentation code changed. No new service, store,
port or application environment setting was introduced. Native civilization
assets, costs, stats and mount masks are unchanged. Raw receipts are under
`.scratch/p942/`; published report evidence stays below the 2 MB task limit.

### Required parking comparison

Both cohorts use the original P700 method and the prescribed GC/broadcast/
transport/ship-update/GotoPlan baseline. Its existing civs 25/3 and unlocked-tech
test-harness configuration are unchanged (`P660NextMatchLiveTests.Config`;
boot receipts confirm `showcase.techMode = unlocked`). The research-mode
measurements above exercise the new generated schedule. The dispatch observations overlapped
initial local diagnostics; the patched required cohort ran serially after
research qualification. Shared-host load is retained per attempt in the raw
metadata. These are functional arrival/idle controls, not a rig throughput A/B.

| Attempt | Base observation s | Base arrived / 30 | Base longest idle s | Patched observation s | Patched arrived / 30 | Patched longest idle s | Patched outcome |
|---:|---:|---:|---:|---:|---:|---:|---|
| 1 | 205.2 | 24 | 1.906 | 253.5 | 24 | 1.914 | PASS |
| 2 | 241.0 | 24 | 1.613 | 180.4 | 24 | 1.913 | PASS |
| 3 | 255.5 | 24 | 0.902 | 246.0 | 24 | 0.000 | PASS |
| 4 | 170.9 | 24 | 1.711 | 480.1 | 18 | 1.813 | FAIL, arrival threshold |
| 5 | 369.6 | 24 | 2.005 | 190.9 | 24 | 2.007 | PASS |

The fourth patched sample held the parking bound and recorded real gunfire/
hits, but failed the 24-arrival requirement (18 arrived). Its final traces
include moving pilots in other sectors/staging states; the exact route cause
was not established. Movement, scouting, arrival thresholds and timers were
not edited. Supplementary filtered runs are reported separately in the final
receipts and do not replace the required **4/5** result.

| Supplementary repeat | Patched observation s | Patched arrived / 30 | Patched longest idle s | Dispatch observation s | Dispatch arrived / 30 | Dispatch longest idle s | Dispatch outcome |
|---:|---:|---:|---:|---:|---:|---:|---|
| 1 | 189.1 | 24 | 2.409 | 244.6 | 24 | 0.100 | PASS |
| 2 | 425.8 | 24 | 1.905 | 276.5 | 24 | 0.105 | PASS |
| 3 | 391.3 | 24 | 1.905 | 480.1 | 20 | 1.311 | FAIL, arrival threshold |

These filtered runs followed all other lane test processes, current first and
then dispatch. The dispatch third attempt reproduced the same arrival-threshold
signature, with 4,428 shots, 1,483 hits and 14 kills. Across the required and
supplementary observations each build passed 7/8; all sixteen held the parking
bound. This supports a baseline arrival/trajectory flake, without identifying
its exact cause or establishing a performance difference. Raw receipts:
`.scratch/p942/{after,base}-isolation-parking-*.{log,trx}`. Binary control hashes
are preserved in `.scratch/p942/control-library-hashes.json`; scratch binaries
and build caches were removed after collecting the receipts.

## What the rig should show

Claude owns the rig check. Keep the same maps, alternate civ slots, counts,
seeds, goal/deadline pacing and performance baseline. Group the new `[research]`
summaries by match and civ, and join `[research-done]` to the match boundaries.
Gigacorp should no longer spend the opening at Refinery/Teleport while its
combat developments are already buyable. Patroller, Lt Booster, better weapons
and Bomber should become legal earlier, with upgrades appearing on normal next
purchases. Full allowed trees remain different; fog, money, combat losses and
match duration still constrain live progress. A 50/50 win rate or equal weapon
counts is neither required nor predicted by this fix.
