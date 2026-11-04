# P925 — home geometry, strike entry and pod recovery

The controlled strike fixture supports a **home geometry effect**: three identical
bombers first damage Ganymede's garrison after **56–59 simulation seconds**, versus
**121–130 seconds** for Organia, across the two civilization assignments. It does
not establish a map imbalance for humans or explain the full 105v105 result.

The b63 **pod outcome disparity follows civilization**, but the proposed docking
defect does not reproduce: **24/24 sampled terminal poses dock in real loopback**
with the existing controller and fresh oxygen. Both near and far samples are
included. No control, strategy, standoff, oxygen or map setting is changed. No
runtime fix or new gameplay flag is justified. The additions are offline proof
tools, fixtures and bounded event diagnostics.

One premise needs correcting: b63 match 1 lost **one expansion in each home**,
including Phoenix's Teleport in Organia. Match 2 lost two Phoenix expansions in
Ganymede. Organia won both completed matches; station destruction was not confined
to Ganymede. Match 3 is incomplete and is not counted as another win.

## Evidence and reproducibility

Read-only inputs: the three supplied gzip files in
`~/.claude/jobs/82c1509b/tmp/evidence/p925/`. [audit.json](P925_assets/audit.json)
records their SHA-256 hashes, complete/partial match boundaries, every parsed
pre-finale wave edge, finale commit, rate-limited damage receipt, pod outcome
group, sampled pose and observed station geometry input. Worker streams have no
UTC; the audit joins them to matches through preceding explicit ship identities
learned from server receipts. It does not invent worker timestamps, arrivals,
damage or EOF results. Delayed removal of an old ship does not remap its identity.

Working base: `0ebb5c76` on `codex/p925`. The rig's b63 source base is `ad5c48f8`;
the intervening commit has no changes to the bot controller, shared docking law
or station admission code used here. No commits or pushes were made in this lane.

`tools/p925-home-audit.py` accepts server log, output JSON and worker logs, with
optional `--geometry` for terminal bay-distance measurements. The geometry tool
requires that audit JSON, artwork resolved through `local.env`, and the authored
`m2xspiderw.igc` / `PCore001.igc`. For example, from the repository root:

```sh
python3 tools/p925-home-audit.py SERVER.gz .scratch/p925/audit.json WORKER1.gz WORKER2.gz
~/.dotnet/dotnet run --project tools/p925-home-geometry -- --output .scratch/p925
python3 tools/p925-home-audit.py SERVER.gz .scratch/p925/audit.json WORKER1.gz WORKER2.gz --geometry .scratch/p925/geometry.json
~/.dotnet/dotnet run --no-build --project tools/p925-home-geometry -- --loopback
~/.dotnet/dotnet run --no-build --project tools/p925-home-geometry -- --loopback --swap
~/.dotnet/dotnet run --no-build --project tools/p925-home-geometry -- --loopback --strike --target-home Ganymede
~/.dotnet/dotnet run --no-build --project tools/p925-home-geometry -- --loopback --strike --target-home Organia
```

Repeat the two strike commands with `--swap` for the other civilization assignment.
Each strike uses a separate mission so an earlier last-base destruction cannot
eliminate the opposite attacking side and censor its approach. The fixture checks
that three actual hull-195 bombers were granted; requested role alone is insufficient.

Server/warp and fixture rock seeds are **925**. File-map top-up uses an independent
ambient RNG in production despite `BenchmarkSeed`; the tool explicitly replaces
only generated fixture rocks with a seed-925 population before clients join.
It preserves all authored rocks. Bot loiter parameters are also pinned. Socket
and thread scheduling remain real; loopback durations are measurements, not a
promise of identical network traces. The fixed-step controller replay is exactly
repeatable and is checked byte-for-byte in its fixture test.

## Station, aleph and rock geometry

[geometry.json](P925_assets/geometry.json) contains every launch frame and every
plane of every dock slot in world coordinates, for Gigacorp, Phoenix and Omicron
station types reachable through a fixed-point union of research/building techs.
This is a catalogue of possible branches, not a claim of simultaneous purchase
availability. Candidate types are placed at each authored home; observed b63
expansions are separate rows, preserving distinct station type revisions.

Both home stations face `(0,0,-1)` with up `(0,1,0)` and radius **423.5**.
Ganymede is `(1120,1560,0)`; Organia is `(-1840,-1800,0)`. The following bay
mouths are offsets from the station centre; each model has two launch and two
fighter dock slots, with five planes per dock slot. All other planes and launch
directions are in the JSON.

| Civ; normal garrison type/model | Dock 0 offset; outward axis | Dock 1 offset; outward axis | Launch offsets |
|---|---|---|---|
| Gigacorp; 15 / `ss24a` | `(141.6,-123.5,-43.0)`; −Y | `(-131.0,109.3,-43.0)`; +Y | `(-128.1,-232.6,-35.9)`; `(140.4,248.4,-45.8)` |
| Phoenix; 60 / `dn_faphbsgar` | `(-286.1,-57.3,26.4)`; −Y | `(277.3,58.3,21.8)`; +Y | `(-284.6,127.5,21.5)`; `(282.2,-129.8,24.9)` |
| Omicron; 715 / `faohbsgar` | `(0,-279.6,-56.3)`; −Y | `(0,279.6,-56.3)`; +Y | `(-290.5,0,-56.3)`; `(290.5,0,-56.3)` |

| Home | Border sector: mouth-to-garrison distance | Main siege entry exemplar / conservative distance envelope | Flight remaining to 3500 standoff |
|---|---|---|---:|
| Ganymede | Terra 5573; **Bug Nebula 3434**; Rigel 4013; Plato 5794 | Bug Nebula: **3419 / 3404–3463** | **0** |
| Organia | **Tellus 7580**; Aurora 3766; Gaia 4264; Juniper 6308 | Tellus: **7558 / 7551–7610** | **4080** |

Entry exemplars call the production `AlephExit` helper for a centre crossing at
speed 100 with hull 195 and seed `925 xor warpId`; they are not recovered b63
poses. The envelope bounds the native radius-plus-five push and lateral offsets.
Thus a Ganymede siege entry is inside **both** the 3500 finale standoff and the
3600 gather radius even allowing the exit offset. Organia's Tellus entry is
outside both. Pre-finale rally remains on the attacker's side of the aleph;
the two radii describe different phases and must not be treated as one rule.
Native exit: `FedSrv/fsship.cpp:90-132` (re-read for this task).

The authored file has **134 rocks, all in Alderan**. Homes are populated by the
server's file-map top-up; seed 925 gives **430 total rocks**, with **18 / 20**
within 6 km of the Ganymede / Organia garrison. The b63 generated home-rock draws
are unavailable, so the fixture inventory is explicitly not their reconstruction.
For a six-kilometre outward bay ray, rock radius + pod radius + 50 padding flags
these geometric lane candidates:

| Civ | Ganymede dock slots 0 / 1 | Organia dock slots 0 / 1 |
|---|---|---|
| Gigacorp | 3333 / 3349 | none / none |
| Phoenix | none / 3335 | none / 3357 |
| Omicron | 3333 / 3335,3349 | none / none |

These are sphere/ray overlaps, not proven collisions or an obstruction verdict.
The live fixture retains the real collision hulls and these rocks. In particular,
the empty authored home-rock lists must not be mistaken for clear runtime lanes.

Observed expansion placement also changes entry distance substantially:

| Match; home/civ | Observed station | Position | Siege mouth distance |
|---|---|---|---:|
| 1; Ganymede/Gigacorp | 9191 Outpost (Lt), type 2 | `(1426,-1927,800)` | 1722 |
| 1; Ganymede/Gigacorp | 9197 Supremacy (Adv), type 7 | `(580,-2605,-800)` | 2818 |
| 1; Organia/Phoenix | 9198 Expansion, type 64 | `(-5075,-465,-800)` | 9924 |
| 2; Ganymede/Phoenix | 9194 Supremacy, type 62 | `(580,-2605,-800)` | 2818 |
| 2; Ganymede/Phoenix | 9204 Transceiver, type 73 | `(4620,1560,-800)` | 3512 |
| 2; Organia/Gigacorp | 9191 Outpost (Lt), type 2 | `(-1534,-5287,800)` | 9837 |

Pod-destination receipts do not locate every non-restart expansion. In particular,
the destroyed Teleport/Refinery/SpecMine positions cannot all be recovered from
these receipts. No garrison-distance substitution is made for those stations.

## b63 pods

Stages are **highest ever observed**, not the terminal position. Denominators
below include only docked/oxygen outcomes, matching the brief; the audit retains
combat deaths, removals, ship rescues and station rescues separately.

| Match; home/civ | Docked / oxygen | Oxygen stages: away / home / radius / bay | Radius→oxygen median / p95 seconds (n) |
|---|---:|---:|---:|
| 1; Ganymede/Gigacorp | 161 / 51 | 23 / 17 / 11 / 0 | 141.90 / 278.80 (11) |
| 1; Organia/Phoenix | 37 / 120 | 12 / 84 / 24 / 0 | 65.65 / 74.75 (24) |
| 2; Organia/Gigacorp | 120 / 56 | 4 / 23 / 29 / 0 | 73.20 / 185.35 (29) |
| 2; Ganymede/Phoenix | 25 / 134 | 3 / 36 / 95 / 0 | 141.00 / 186.20 (95) |
| 3 partial; Ganymede/Gigacorp | 78 / 20 | 0 / 5 / 15 / 0 | 78.80 / 95.10 (15) |
| 3 partial; Organia/Phoenix | 31 / 103 | 3 / 75 / 25 / 0 | 58.25 / 73.55 (25) |

Across completed matches, Gigacorp docks **281/388 (72.4%)**, Phoenix **62/316
(19.6%)** on that restricted denominator. This is an association, not a fleet
exposure-adjusted recovery rate. Both pod types have length 4, mass 10, base speed
60, thrust 150, turn rate about 0.698 rad/s and torque about 17.453. Civilization
base attributes make Gigacorp speed 66 and thrust 135, Phoenix speed 60 and thrust
150; research can subsequently alter attributes. All logged oxygen allowances
are 300 seconds. There is no shorter Phoenix oxygen budget in these receipts.

For oxygen trips ending in the diagnostic home sector, terminal position medians
relative to the recorded station/bays are:

| Match; civ | Station-centre distance (n) | Nearest bay mouth distance (n) | Nearest bay-line lateral offset (n) |
|---|---:|---:|---:|
| 1; Gigacorp | 2481 (28) | 2353 (28) | 848 (28) |
| 1; Phoenix | 4379 (108) | 4284 (104) | 2210 (104) |
| 2; Gigacorp | 3483 (52) | 3394 (52) | 1093 (52) |
| 2; Phoenix | 2103 (131) | 2188 (130) | 781 (130) |

Bay counts differ where the recorded station has no landing slots. Distances
are to its actual type's transformed bay planes, not to a civ-inferred model.
Among radius-reaching oxygen trips, **2/11, 10/24, 14/29 and 42/95**, respectively,
end outside that station's radius again. Thus “reached radius” does not mean the
pod spent all remaining oxygen parked at that radius. The server's diagnostic
nearest-home target can differ from the bot's selected station/probe, which was
not recorded alongside every trip. The new actual-target receipts address this.

## b63 strikes

Pre-finale waves are from worker 1; worker 2 has finale commits/staging but no
numbered pre-finale commits in this capture. `arrived` is the wave's logged
arrival count; `peak` is the maximum simultaneously on target, not their sum.

| Match; target home/civ | Commits / aborts | Sum arrived / max peak | End duration median | End reasons |
|---|---:|---:|---:|---|
| 1; Organia/Phoenix | 8 / 0 | 1 / 1 | 77.5 s | members-gone 7; no-strike-pilots 1 |
| 1; Ganymede/Gigacorp | 6 / 1 | 2 / 1 | 100.0 s | members-gone 5; no-strike-pilots 1 |
| 2; Ganymede/Phoenix | 4 / 1 | 6 / 3 | 58.15 s | members-gone 2; station-gone 1; no-members-in-target 1 |
| 2; Organia/Gigacorp | 3 / 1 | 1 / 1 | 88.7 s | members-gone 2; no-strike-pilots 1 |
| 3 partial; Organia/Phoenix | 3 / 2 | 0 / 0 | 45.1 s | members-gone 3 |
| 3 partial; Ganymede/Gigacorp | 1 / 0 | 2 / 2 | 75.9 s | station-gone 1 |

All five aborts are `insufficient-volley`. Adaptive required mass in these logs
is not always three. Exact commit/end health observations are retained per wave
in the audit. The clear garrison change is match 2, side-0 wave 4 into Ganymede:
four arrivals, peak three, **hull 1.000→0.850; shield 1.000→0.000**, duration
90.1 s. Other known end hulls remain 1.000; Organia shield observations include
0.892→1.000 and Ganymede 0.600→1.000 regeneration. `-1` means unknown and is
never credited as destruction/damage. Match-2 wave 3 targets an already damaged
station (hull 0.158) and ends `station-gone` after 1.1 s with no arrivals: that
wave is not credited with killing it.

Finale commit counts across workers: match 1 attacks Organia six times and
Ganymede once; match 2 attacks Organia seven times and Ganymede zero times.
These are commits, not successful strikes; missing timestamps and full membership
prevent attributing rate-limited server hits to individual finale waves.

There are only **29 station-damage receipts** across the whole capture. They
include match-2 Phoenix garrison hits in Ganymede, so damage is not solely to
expansions, although all seven logged destructions are expansions. Completed
match station losses are:

| Match | Ganymede losses | Organia losses | Winner |
|---|---|---|---|
| 1 | 9196 SpecMine | 9190 Teleport | Organia/Phoenix; kills 507–373 |
| 2 | 9189, 9198 Refineries | none | Organia/Gigacorp; station kills 2–0 |

The two completed matches suggest better strike penetration into Ganymede, but
do not prove a home-dependent bot defect or support “every station loss was in
Ganymede.” The partial third match contributes three more Ganymede expansion
losses without a completed result.

## Local controlled results and verdicts

The [pure replay](P925_assets/replay.json) starts from the first three near
(≤1500) and first three farther garrison-target oxygen endings per home/pod hull,
with exact logged position, velocity, forward/up and angular rates. All **24/24**
current-controller cases and **24/24** angular-braking comparison cases enter a
real model bay within 300 seconds. The maximum across both is 145.85 s.
This comparison changes only the proof controller; no runtime switch is shipped.

Real loopback uses authoritative server ejection, the same sampled poses and
civilizations, seed-925 home rocks, native collision hulls, ordinary bot control
messages and real docking. It gives fresh oxygen, uses baseline civilization
attributes and has no combat, expansions, rescue probes or relaunch crowd fixture.
Original ejection/radius poses and research attribute snapshots were not logged;
this is a terminal-pose recovery experiment, not an exact replay of the original
300-second trip. It cannot establish that the pod had enough oxygen left to
recover in b63. Results are reconciled to authoritative `[pod-trip]` outcomes so
oxygen restarts never count as successful docks.

| Home / civ | Docked / trials | Time min / median / max, simulation seconds | Terminal stage |
|---|---:|---:|---|
| Ganymede / Gigacorp | 6 / 6 | 7.90 / 33.40 / 146.05 | bay, all six |
| Organia / Phoenix | 6 / 6 | 7.95 / 35.87 / 84.45 | bay, all six |
| Ganymede / Phoenix | 6 / 6 | 7.90 / 48.52 / 103.05 | bay, all six |
| Organia / Gigacorp | 6 / 6 | 7.90 / 30.65 / 95.65 | bay, all six |

Authoritative trip outcomes: [normal](P925_assets/pods-normal-off.json) and
[swapped](P925_assets/pods-swapped-off.json). All 24 are confirmed real docks,
with zero simulation-stage failures; there is no local failure stage to diagnose.

The three-bomber fixture holds hull 195 and preferred loadout constant on both
sides, bypassing purchase tech to isolate entry geometry. It validates actual
granted hulls; incompatible preferred parts may be omitted by ordinary mounting.
There are **zero defending ships**; station durability, regeneration and collision
retain their normal behavior. It
measures pre-finale direct station attack, not finale quorum/standoff assembly.
No 105v105 or wall-clock performance claim is made.

| Target home / defending civ | First bomber arrival | First station damage | Peak bombers on target |
|---|---:|---:|---:|
| Ganymede / Gigacorp | 11.35 s | 58.70 s | 3 |
| Ganymede / Phoenix | 11.35 s | 56.00 s | 3 |
| Organia / Phoenix | 11.35 s | 120.60 s | 3 |
| Organia / Gigacorp | 11.40 s | 129.85 s | 3 |

Times are simulation seconds from the fixture's release barrier before the
source-side aleph, including its real warp handshake. All four measurement
windows have zero sim-stage failures. JSON receipts are linked in the asset
directory, including requested parts, remaining mounted parts at first damage and final bot state.

* **Strike entry: map geometry interacting with bot strategy.** Extra approach
  time reproduces across civ assignments. Full-match causation, expansion-specific
  exposure and defended strike success remain unknown. No standoff retune is
  proposed from this experiment; a rig A/B would need to measure those separately.
* **Pod failure: civ-associated in b63; causal mechanism unknown.** Every sampled
  pose recovers locally with unchanged controls. No clear model-bay or angular
  steering defect is demonstrated. Route/target changes, remaining oxygen,
  research attributes and traffic remain candidates, not findings.

## Changes and next trial receipts

There are **no new environment variables, services, ports or gameplay settings**.
All existing decisions and control calculations remain in place. Telemetry is
default-on and event-only:

* `[pod-nav] edge=target`: actual selected station, type, sector, centre, ship,
  mission cookie/generation, UTC, snapshot time, position/velocity/forward/up and
  angular rates; emitted when the station target changes.
* `[pod-nav] edge=radius`: the same pose at first entry into that selected station's
  radius, plus selected bay, bay count and planner mask. One per selected station
  episode, reset at ejection or station-target change.
* `[pod-nav] edge=no-bay-fallback`: at most once per pod life if geometry is absent.
  `[pod-nav] edge=docked|restart` distinguishes recovery from true-death restart.
* Numbered `[strike]` commit/abort/end edges append UTC, mission cookie/generation,
  target type and position. Existing static `StrikeLogLine` formatting remains
  unchanged for its callers/tests. There is no per-tick output or new log timer.

The next dispatcher-run trial should retain both complete worker streams and the
server pod/damage receipts. Join by UTC/ship/generation and the actual bot target,
then compare radius poses and remaining oxygen across home swaps. Strike target
positions allow the next run to measure threatened expansions instead of using
the garrison as a proxy. Finale commits still have no common numbered wave ID;
their individual damage attribution remains a limitation. This task did not
contact or change the running rig, mini, Mac or any remote host.

## Validation

The [verification receipt](P925_assets/verification.json) and
[isolation results](P925_assets/isolations.json) retain the final counts/timings.

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 errors, 0 warnings** |
| `tools/build-tools.sh` | **115/115 projects built**; final affected tool rebuilt with 0 warnings |
| Three new C# fixture tests | **3/3 isolated runs**, 9 passes, **0 skipped** |
| Four Python analysis fixture tests | **3/3 isolated runs**, 12 passes |
| Authored-map local fixtures | **24/24 confirmed pod docks; 4/4 three-bomber measurements**, seed 925 |
| Baseline showcase never-parks | **5/5 passed**, 0 skipped; 265.31 / 235.85 / 247.77 / 225.08 / 271.66 wall seconds |
| One Fast tier | **13,005 passed, 11 failed, 3 skipped**; three timing offenders; **gate remains red** |
| Glossary link check | Passed, 538 distinct paths |
| Parity scorecard before/after | Passed; unchanged rows/credit, sim weighted score 98.51% |

`local.env` supplied the artwork/native paths. The three Fast skips are optional
Meshy-pack tests, not native artwork tests. All P925 tests and all five live
never-parks runs have **Skipped=0**. There is no fix-on run because no gameplay
fix or setting was introduced.

The exact [Fast summary](P925_assets/fast-summary.json) remains a failure. All
non-task failures and timing offenders have three-run isolation results in
[`docs/TEST_FLAKES.md`](../TEST_FLAKES.md): default-profile write obstruction;
five in-checkout temporary-path assumptions; intermittent partial missile-lock
tip; join-status, pod-display and station-clearance assertions; and the existing
persistent NPC defend-target assertion. The two passed-but-slow methods were
also isolated three times; P777 remains marginal at 10.064 s in its last run.
No unrelated simulation, profile, wire or test assertion was changed to clear
these failures, and the Fast suite was not repeated.

Raw TRX/logs are retained in the ignored task-worktree
[`.scratch/p925/`](../../.scratch/p925/); small public JSON evidence is under
`docs/reports/P925_assets/` (about 1.6 MB, below the 2 MB cap). No new media was
created. Core/Server source, training, HUD, services and environment variables
are unchanged, so the conditional AOT, training, visual-pair and Compose gates
do not apply. All changes are left in the working tree.

## Native-intent footer

**Design:** native docking uses named CVH launch/garage frames scaled by station
radius (`Igc/stationtypeigc.cpp:40-144`), world-rotated bay approach vectors
(`Igc/common.cpp:1513-1542`), angular braking (`common.cpp:216-245`) and the
non-experimental 0.01 rad/s entry gate (`common.cpp:1295-1305`). Admission tests
every oriented hull extreme against the bay planes (`Igc/stationIGC.cpp:385-432`).
Oxygen is armed on lifepod hull change (`Igc/shipIGC.cpp:1408`), with the native
300-second constant (`Igc/missionigc.cpp:2724,2775`). These sources were re-read;
the task reuses their existing ports and adds no native simulation rule.

**Era constraint:** authored station/aleph coordinates and named collision-model
frames are the original content contract. They legitimately differ by model and
home. Their asymmetry alone is not a port bug.

**Bug verdict:** no clear bot control defect survived the controlled reproduction;
therefore no gameplay fix, strategy rewrite or guessed constant adjustment ships.
No parity CSV row is added or promoted.

**Modern approach:** preserve native geometry/controls, use small deterministic
fixtures plus bounded actual-target/stage receipts, and make any future behavior
experiment default-off until the dispatcher runs a paired rig trial.
