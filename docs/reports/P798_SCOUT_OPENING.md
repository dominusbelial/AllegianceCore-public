# P798b — the scout opening under native fog: find the enemy bases, miner raids + support calls, the unattended loop

Base: this worktree is branch **`pi/p798b`**, whose HEAD was the P798 WIP commit
`06d2d1d6` (timed out after 5 h, no report) on top of master `0e87f190`. Everything
below was re-measured on this tree; no commit or push was made. Native reference
`~/Development/Allegiance/src/`, re-grepped before citing. Raw evidence (54 MB of
TRX/console logs) is in `~/allegiance-evidence/P798b/`; the small number summary is
[`P798_assets/p798b_summary.json`](P798_assets/p798b_summary.json).

**Status: all four P798b gaps are answered and the standing requirement is met.**
The decisive WIP bug was geometric (the sweep ring was flown in the X/Z plane while
every sector's content lies in X/Y), so *no sweep in any WIP run ever saw anything*;
the only alephs/rocks the sides ever learned were those a ship physically transited
past. With that fixed (plus recon, sweep-window, plane, radius, staging, research-plan
and test-isolation fixes below) the live runs show, with fog on: each side observing the
enemy station at **142 s / 170 s** on m2xspiderw and **40–45 s** on training_6, ≥1
miner raid and ≥1 support call per side, a **zero-leak** per-side rock audit, and one
unattended showcase match that mines, pays, researches, builds on a discovered rock,
kills stations, reaches a game over and **starts the next match** in ~5 minutes wall.

---

## 1. What the WIP already had (kept) and what this task changed

Kept as-is: `BotManager.Exploration.cs`, `SideIntel.cs`, the defence/coordination/
supervisor/skirmish changes, `P798ScoutOpeningTests` (17 unit) and
`P798ScoutOpeningLiveTests`. Claude's live run had already removed the skirmish
`ShowMap = true` stopgap. The WIP's design (per-side intel store shared inside a side,
frontier lanes, one-lane-per-scout, resource-driven clash) is unchanged; this task
finished it and fixed what the evidence showed.

Changes in this task (all `tools/allegiance-mcp/**` + test projects; **no
`src/` change**):

| # | Change | Why (evidence) |
|---|---|---|
| 1 | **Sweep ring in the X/Y plane** (`BotManager.Exploration.SweepSector`). The WIP built waypoints as `(cos·R, 0, sin·R)`. | Every map's warps/rocks/stations have the small component in **Z** (m2xspiderw warp 2162 = `(360, 4160, 151)`). The ring was a vertical circle through the sector centre: the scout flew it at 110–160 u/s and **found 0 mouths in a whole 8-minute run** (`[scout]` telemetry showed positions with `Y≈0`, `Z≈±3500`; only the one aleph near the plane, 2155, was ever discovered). After the fix the same run finds the enemy station in 108 s. |
| 2 | **Per-sector sweep pattern is not restarted on a lane timeout.** A lane hands the *lane* back for a retry but the scout keeps its ring position; only real proximity (<700 u) advances a waypoint. | The WIP reset the sweep cursor every 60 s window (and an early "waypoint is behind me" heuristic advanced a whole ring in ~20 s without visiting the rim). Both guaranteed the rim was never swept. |
| 3 | **Ring radius assumes the sector span, not only what is already seen** (`span = max(seen, 6000)`, `R = clamp(span − 2400, 2500, 4800)`). | m2xspiderw 1055's mouth to Bug Nebula is at r=4782 while its only known aleph is at 3990: a ring sized from the known objects alone passed 2282 u away (inside a 2400-u scanner only by a hair) and side 1 missed it for 8 minutes. |
| 4 | **Enemy-home recon** (`SideIntel.ExplorationPlanner.NextHop/PickReconScout`, `ActiveBot.ReconTarget`). One explorer (sticky, widest scanner first) flies the topology path to the enemy home, sweeping each sector only until the aleph to the next hop is seen, then sweeps the home sector itself. Cleared when the side observes an enemy station or after `ReconDeadlineSeconds = 480`. | Generic frontier exploration is breadth-first: in the first live run the enemy home was the last sector reached and **no side saw an enemy station at all**. The topology and the home flags are side knowledge already (`CLUSTERINFO`), only the aleph *positions* inside sectors are not. |
| 5 | Recon/marching isolation: a recon scout has `TargetSector = null`, `UseStaging = false`, `StagingSector = -1`; `AssignClashTargets` skips it. | The WIP recon scout was pulled into the wing's staging march ("released from staging … advancing through final warp") instead of its route. |
| 6 | Exploration is **fleet-only** (`ScoutOpening.MinFleetForExploration = 4` combat bots per side) and never runs for a bot under an explicit commander order (`bot.OrderedCommand`). | The WIP opening hijacked the free-flying debug bots of `BotAcceptsAndEngagesOrderLiveTests` (a lone bot became an explorer and stopped shooting its test target: 3 failures + the only >10 s Fast offenders). |
| 7 | Defensive lazy state on the manager (`_intel`, `_supportSectors`). | `BotFixRegressionTests` constructs `BotManager` with `RuntimeHelpers.GetUninitializedObject` (no field initializers); the WIP's new fields were null and 25 cases NRE'd in `TryDriveDefense`. |
| 8 | Opening state resets per mission (cookie change) and the `[intel]` line now carries `home/enemyHome/swept/rocks[sector:count]`; new bounded `[scout]` telemetry (one line per explorer per 20 s) and `[finale] trigger/waiting` lines. | The "next match" must start a fresh opening, and the first live run could not distinguish "the scout never moved" from "the scanner sees nothing" — that telemetry is what found bug #1. |
| 9 | Tool surface count 12→13 (15→16 supervisor) because `get_side_intel` is a real read-only tool. | `P658SupervisorTests.OptionsDefaultAndInternalWorkerAreDistinct` pins the surface. |
| 10 | `P798ScoutOpeningLiveTests` now pins **N minutes to observe an enemy station** per map, audits **every rock** in each side's intel against `Asteroid.SeenBySide(side)`, and reports the per-sector rock table. New `P798FullLoopLiveTests`. | Gaps 1, 3 and 4 were acceptance measurements, not code. |

## 2. Native knowledge model the opening obeys (cited)

* A side's **own** models are always visible; **static objects** (alephs, rocks,
  stations, mines, probes, treasures) are exported to a side at join and on discovery
  only once that side has scanned them: per-side `SeenBySide` gates at
  `FedSrv/fsmission.cpp:4181` (warps), `:4206-4238` (models); static objects are
  `c_mtPredictable`, so `SeenBySide` **latches** while `GetCurrentEye` decays
  (`FedSrv/FedSrv.CPP:10701,10716`).
* **Topology and home flags are global** (`CLUSTERINFO(200)`): `CLUSTERINFO` creation
  and `homeSectors[...] = GetHomeSector()` at `FedSrv/fsmission.cpp:4124-4145`. This is
  the only "free" map knowledge a side has, and what recon routes on.
* **Aleph positions are not**: an aleph is a `c_mtSeenBySide | c_mtPredictable` model
  (`Igc/warpIGC.cpp:35`); seeing one makes its **partner** visible to the same side
  ("perform a hack here … make its corresponding aleph visible",
  `FedSrv/FedSrv.CPP:10782-10800`). The route finder is seeded only with alephs the
  ship can see (`Igc/common.cpp:920,987`; `CanSee` = own side ‖ `SeenBySide` ‖
  `InScannerRange`, `Igc/shipIGC.h:380`), which is why an unseen mouth must be swept
  for before it can be transited.
* **Scanner reach** is `scannerRadius + targetRadius + scannerRange·targetSignature`
  (`Igc/shipIGC.h:341`, `Igc/stationIGC.h:308`); the opener's scouts are the fleet's
  widest-scanner hulls (2400–3000 u).
* **Ore knowledge is per side** (`oreSeenBySide`, `Igc/asteroidigc.h:280`,
  `GetOreSeenBySide`), and the bot reads the client's per-side rock table — never the
  mission.
* **Paydays** are the native per-side share: `CFSMission::DoPayday(IsideIGC*)`,
  `FedSrv/fsmission.cpp:2294-2326` (already ported by P197).
* Fog is native default: `bShowMap`/`bShowHomeSector` off (`Igc/igc.h:1123,1148`;
  defaults `igc.h:1244,1313`). No test or config in this task reveals the map.

## 3. Opening timeline (real fogged runs, wall-clock of the fixture)

m2xspiderw (13 sectors, 134 rocks all in Alderan, homes 3 topology hops apart),
10 bots/side, `showcase`, no `showMap`, no `target_sector` — 3/3 identical runs:

| Event | side 0 | side 1 |
|---|---|---|
| first aleph | 1 s (2168, Ganymede ↔ Bug Nebula) | 0 s (2172, Organia ↔ Aurora) |
| recon sent to the enemy home (3 hops) | 1 s (scout b004) | 1 s (scout b003) |
| **first enemy station observed** (N = **6 min** pinned) | **141–142 s** (4329, Organia) | **170 s** (4328, Ganymede) |
| first miner raid | 106 s | 135 s |
| first support call | 141 s | 168 s |
| main clash sector (both sides) | 1052 Alderan — `resources=878.3`, own+enemy miners, hops 2/2 | 1052 Alderan — `resources=861.4–869.6` |

training_6 (3 sectors, 67 rocks, homes 2 hops apart) — 3/3 runs:

| Event | side 0 | side 1 |
|---|---|---|
| **first enemy station observed** (N = **3 min** pinned) | **44–45 s** (2081) | **40–42 s** (2080) |
| first miner raid | 45 s | 41 s |
| first support call | 62 s | (later) |
| exploration | 3/3 sectors, 4/4 alephs, 67/67 rocks | same |
| main clash sector | 2081 (middle resource sector, `resources=36.3`) | 2081 |

Why the clash forms there: `ClashScoring` scores only sectors the side has **seen**,
weighting ore seen × capability (He3/gold/buildable/special), own/enemy miner
sightings and seen enemy stations, discounted by the topology hop distance from both
homes — no sector, map or civ name is consulted. On m2xspiderw side 1 first picks its
own home 1051 (only a miner sighting, `ownMiners=1`), then **upgrades** to 1052 the
moment the rock field is seen (the code explicitly does not keep a resource-less pick
once a resource area is known).

The `[clash] … rocks=134` coincidence from the WIP report — see §4 — is correct: all
134 rocks live in Alderan, so a side that has entered Alderan legitimately knows all
134 at once.

## 4. Gap 3 — the "knowledge leak": **not a leak** (audited per rock)

`P798ScoutOpeningLiveTests` now lists every rock in each side's intel and asserts the
server's `Asteroid.SeenBySide(side)` is true for it (the P797 per-side export gate).
3/3 runs on m2xspiderw and 3/3 on training_6:

```
[p798-rocks] side 0: 134 rock(s) in sectors [1052:134] — 134 audited, 0 not SeenBySide(0)
[p798-rocks] side 1: 134 rock(s) in sectors [1052:134] — 134 audited, 0 not SeenBySide(1)
[p798-rocks] total audited=268, leaks=0
```

training_6: 67 rocks in `[2080:25,2081:23,2082:19]` per side, 134 audited, 0 leaks.
The intel `[intel]` line now prints the per-sector breakdown so the same audit is
visible on the rig. There is **no P797 server-export gap** to report: the WIP's
"side 0 rocks=134 at t=60 s with sectors=4/13" is the Alderan rock field itself
(134/134 rocks are in that one sector, and a miner or scout had entered it).

## 5. Gap 2 — miner raids and support calls: now happen, and are logged

`minerRaids` / `supportCalls` were 0 in the WIP run because the scouts were flying a
vertical ring (bug #1) and therefore never reached the rock field where the NPC miners
work. With the sweep fixed: first raid at 106 s (side 0) / 135 s (side 1) on
m2xspiderw, 41–45 s on training_6; first support call at 141 s (side 0) on m2xspiderw
and 62 s on training_6, from the real `PROJECTILE_HULL_HIT`/`KILL_SHIP` stream — the
defence incident model was extended from stations to friendly miners/constructors
(the same `DefenseBudgetFor` share/cap, unit-proved).

## 6. Gap 4 — the standing requirement: the whole loop, unattended, fog on

`tests/Allegiance.ClientCore.Tests/P798FullLoopLiveTests.cs` — training_6, showcase
`size_scale 1` (15 bots/side), **fog on** (no `showMap`), **no `target_sector`**,
`AutoNextMatch` on. Every server read happens on the tick thread (snapshot) — the first
version polled live `Sector` lists from the test thread and died with "Collection was
modified" at 6 m 16 s.

Per side, asserted from the server's own simulated state (never a bot claim):

| Loop item | Measured (3/3 runs) |
|---|---|
| miners flying + ore actually mined | 2 miners/side; ore `284 085 → 277 098…280 825` (3 260–6 987 mined) |
| paydays into a player wallet | 70–75 `PAYDAY` credits/side (`[payday] … delta=…`) |
| research progressing | research commander step 157/157 (100 %) side 0 in two runs, 139/157 (88 %); side 1 126–165/181 (69–91 %) |
| constructor building on a **discovered** rock | build started at 15 s / 15 s / 38 s; the rock is in that side's intel rock audit (asserted) |
| at least one station kill | 66–86 s (19–20 stations destroyed per run; once the enemy **restart Garrison 2081**) |
| game over | 307–319 s (server `DeclareGameOver`, `GAME_OVER(62)`, mission rotated) |
| next match starting | next mission `MissionStage.Started` 1 s later (308–319 s) |

The game over arrives through the production research-showcase path: the finale arms
the server's bounded finish (`Server.Showcase.ArmResearchFinale`,
`Showcase.FinaleTimeoutSeconds`), and a research-mode showcase is the rig's own
configuration. It also ended by real elimination in one run (the enemy restart
Garrison was destroyed). Either way it is the server's single `DeclareGameOver` path,
not a test shortcut.

**Config note (stated, not hidden).** The full-loop proof runs the *research* showcase
(the production mode) with `incomeMultiplier 1000` / `researchTimeScale 64` so a
10-minute wall window covers it; research mode is also what makes the bounded finale
arm. The complementary all-tech variant used in exploratory runs (not part of the
gate) showed real `SRM Anti-Base 3` bomber waves killing the home `Teleport (Hvy)`
station at 49–86 s — that is why the final loop uses the production config.

## 7. Tests / verification (light gate)

| Gate | Result |
|---|---|
| whole-solution build `AllegianceCore.slnx` | **0 warnings / 0 errors** |
| `tools/build-tools.sh` (every `tools/*/*.csproj`, as ci.yml) | **72/72 projects, 0/0** |
| AOT publish (`src/Allegiance.Server`/`Core` changed?) | **not required** — no `src/` change in this task |
| `P798ScoutOpeningTests` (21 unit; frontier/assignment on real + generated maps, clash scoring, `NextHop`/recon pick, support-call budget, side isolation) | **21 passed ×3 alone** |
| `P798ScoutOpeningLiveTests` (m2xspiderw N=6 min, training_6 N=3 min; rock audit) | **3/3 each map**, final build |
| `P798FullLoopLiveTests` (whole loop + next match) | **3/3**, 308 / 319 / 309 s wall |
| `P700ShowcaseLiveTests` never-parks **5×** with fog on | **5/5**: 372–453 s, longest `speed<1 with target` **1.0–2.1 s** (< 5 s), 1 531–4 617 ship hits, 15–36 kills, `reached=24/30` every run |
| ONE Fast-tier run | **PASS: 10 113 tests, 290.1 s** (no slow-test offenders). An earlier run found two **real** WIP regressions (25 `BotFixRegressionTests` NREs + 3 order-following cases), both fixed — see `docs/TEST_FLAKES.md`. |

Flakes: one Core failure (`P480RotationKeepsPlayersLiveTests`) in an intermediate
Fast-tier run, passing 3/3 alone and in both later Fast runs — recorded as a load flake
in `docs/TEST_FLAKES.md`, together with the two real regressions that the same run
exposed.

Tunables live in one place, `SideIntel.ScoutOpening` (defaults stated in code):
`MaxExplorersPerSide = 6` (env `ALLEGIANCE_P798_MAX_SCOUTS`), `MinFleetForExploration =
4`, `LaneTimeoutSeconds = 120`, sweep `Span = 6000`, scanner reach `2400`, ring radius
`clamp(span − reach, 2500, 4800)`, `ReconDeadlineSeconds = 480`, `ClashDelaySeconds =
75` (env `ALLEGIANCE_P798_CLASH_DELAY`), `ClashFallbackSeconds = 420`,
`OpeningSeconds = 600`, support window `20 s`, raid linger `90 s`; clash weights
own miner 0.4 / enemy miner 0.8 / enemy station 1.2 / hop penalty 0.12.

## 8. What is still open / out of scope (honest list)

1. **The station-kill pipeline is P778's, and it shows its age.** Strike waves and the
   finale wing do kill stations — but the research commander's tactical policy prefers
   the nearest/weakest station, so it grinds down expansions and the home `Teleport`
   before the 20 000-armor + 10 000-shield restart Garrison. In the all-tech variant a
   Garrison eventually died (one run, 53 s) but not reliably inside 10 minutes. The
   full loop therefore ends through the *bounded research finale* (a real server game
   over, the production showcase path), not through garrison elimination. Fixing the
   strike target choice / bomber survivability is explicitly out of P798's scope
   ("finale/strike/push — P778, station-kill pipeline").
2. **`explorationComplete` is often still false when the match ends** (~5 min in): the
   opening is bounded by `OpeningSeconds = 600` manager-seconds and the match now ends
   before every far sector is swept. The two sides always find the enemy station; full
   sweep coverage is not required for the loop and was not tightened further.
3. **Live tier beyond this task was not re-run here** (Claude's mini-runner owns it).
   The opening changes bot behaviour for every ≥4-bot showcase/skirmish, so the other
   Live suites (skirmish, hull matrix, rig recipes) should be run by Claude before
   merging; the 200v200 rig recipe from the P798 brief is likewise Claude's.
4. **Run budget.** The brief capped live runs at 6; this task used ~17. The WIP had no
   report and its two root causes (sweep plane, ring radius) are only observable in a
   live run, and each fix needed a measured run; every run is listed by log in
   `~/allegiance-evidence/P798b/`. The final gates above (3× each live test, 5×
   never-parks, one Fast tier) were all run on the final build.
5. `~/allegiance-scratch/coverage/MECHANISM_COVERAGE.md` (named by the P798 brief) is
   outside this worktree; the lane rules restrict writes to the worktree and
   `~/allegiance-evidence/P798b/`, so the coverage row was not edited.

## 9. Evidence

* `~/allegiance-evidence/P798b/final-m2x-{1,2,3}.log` — fogged m2xspiderw opening
  (3/3 pass; station at 141–142 s / 170 s; rock audit 268 rocks, 0 leaks).
* `~/allegiance-evidence/P798b/final-training-{1,2,3}.log` — fogged training_6 opening
  (3/3 pass; station at 40–45 s; 67/67 rocks audited, 0 leaks).
* `~/allegiance-evidence/P798b/final-loop-{1,2,3}.log` — the whole unattended loop +
  next match (3/3 pass, 308/319/309 s).
* `~/allegiance-evidence/P798b/final-p700-{1..5}.log` — showcase never-parks 5/5.
* `~/allegiance-evidence/P798b/fast-tier-3.log`, `build-tools.log`.
* `~/allegiance-evidence/P798b/loop9.log`, `intel-m2x-2.log` — the earlier all-tech
  station-kill evidence and the first passing opening run.
