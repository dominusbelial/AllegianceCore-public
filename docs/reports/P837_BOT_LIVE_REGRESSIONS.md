# P837 — two master regressions in the bot live tests

Base: public master at dispatch `04b860ae` (`pi/p837` worktree
`~/allegiance-pi-p837`). Bot behaviour lives in `tools/allegiance-mcp`; the only
product file touched is `src/Allegiance.SinglePlayer/SkirmishOptions.cs`. Native
citations verified in `~/Development/Allegiance/src` (paths below). No commit, no
push; everything is left in the working tree. The run logs lived in this lane's
gitignored `.scratch/` (deleted at the end of the task, per the lane rules); every
number quoted below is reproduced in §5 and in
[`P837_assets/verification.json`](P837_assets/verification.json).

## 0. Verdict

| Failure | First bad commit | Mechanism | Fix |
|---|---|---|---|
| `P727SkirmishLiveTests.Local_start_battle_pause_resume_next_match_stop_leaves_no_processes` times out at the `combat.shotsFired > 0 && shipHits > 0` wait (~:217 master) | **`a8896574` (P798, 2026-10-23)** — "scouts first under native fog … skirmish no longer reveals the map" | Under native fog a 9-bot skirmish's only early "resource" is its own miners on its own base, so `ClashScoring` chose **the side's own home** as the clash; every non-scout parked there, the two fleets never met, and the rig-scale 420 s topology fallback is far outside the fixture's 150 s wait (0 shots / 0 hits at sim 210 s). | The skirmish reveals the **warps** (native `bShowMap`) while the fleet is small (Small/Medium), the side's own home is never a clash objective, and the topology fallback commits both fleets to the same balanced **home-to-home midpoint** with a fleet-scaled delay. A 9-bot skirmish is also a **meeting engagement**: it commits early, does not stage, keeps one scout and fights what it meets while travelling. |
| `P682b_second_match_relaunches_mature_and_strike_then_third_match_starts` (`port, pipeline=false` and the other three rows) times out at `match 2 automatic start and fresh finale` (< 240 s) | **`62a5b50f` (P831, 2026-10-26)** — bounded, fog-aware re-arm | After the rotation both bombers were reset and had a fresh enemy-home target, but the new per-side "observed an enemy station" gate needs **both** sides to see one; the one-bot-per-side finale is below `MinFleetForExploration`, so nothing handed the second side an objective and it idled in mid-space. The gate could only be released by P831's **300 s** grace — longer than the fixture's 240 s window. | A rotated finale awaiting its re-arm now counts as "a finale is waiting for this side to find the enemy base" (the P798b small-fleet recon exception), so the bots fly out and see the base; and the grace itself is scaled by the participating fleet (rig 300 s, small finale 120 s). |

Both fixes keep the intent of P830 (no scout-lane churn at 200v200) and P831
(bounded, fog-aware re-arm of fallback-hull bombers): no lane, sweep, IsExplorer,
`RearmRotatedFinales` gate or bot-blocking rule was relaxed, and the rig keeps
every rig-scale number (420 s clash fallback at ≥ 48 combat bots per side, 300 s
re-arm grace at ≥ 48 participating bots).

## 1. Failure 1 — the 9-bot skirmish never fires a shot (P727)

### 1.1 Reproduction (master `04b860ae`, idle host)

```
dotnet test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj --no-build \
  --filter "FullyQualifiedName~P727SkirmishLiveTests.Local_start_battle_pause_resume_next_match_stop_leaves_no_processes"
```
→ `Skirmish condition timed out`, 3 m 32 s, at `P727SkirmishTests.cs:217`, with

```
{"WallSeconds":60.0075,"SimulationSeconds":62.2999,"BotsAlive":true,"Running":true,"Bots":9,
 "Shots":0,"Hits":0,"PilotDistance":6831.47,...}
```

i.e. the 60 s play phase already ends with **zero** shots, and the 150 s wait that
follows (sim ≈ 210 s) never sees one either.

### 1.2 Bisect

`git bisect start 04b860ae 151dc2d0` (`151dc2d0` = batch 6, 2026-10-19, the merge
that carries P727/P728/P730/SP-5 whose ledgers had this test green 3/3–5/5), one
step = full solution build + `tools/allegiance-mcp` build + the one test.
Predicate at first: "the test fails at all".

| Step | Commit | Result |
|---|---|---|
| 1 | `ac3cbb7e` | **PASS** (later re-used as the verified good end) |
| 2 | `d4464dc0` | FAIL — *the combat symptom* (3 m 32 s, `Shots:0`) |
| 3 | `fdd70154` (P795) | **PASS** |
| 4 | `769a30c6` (P801) | FAIL — *the combat symptom* (3 m 32 s, `Shots:0`) |
| 5 | `e3e71a0e` | FAIL — **startup**, not combat (`Bot driver exited`) |
| 6 | `b183d01b` (P800) | FAIL — startup |
| 7 | `0eca1fa2` (P796) | FAIL — startup |
| 8 | `7734a197` (P797) | FAIL — startup → bisect reported `7734a197` as "first bad" |

Step 8 exposed that the bisect was answering a *different* question: between
P797 and P798 the fixture cannot even spawn (the skirmish's `spawn_scenario`
finale needs an observed enemy home station, which P797's per-side static
visibility withholds at spawn):

```
allegiance-mcp: startup/transport failed: {... "failures":[{"error":"spawn_scenario: finale needs an observed enemy home station"}]}
```

So the "first bad commit for the *combat* symptom" was then resolved by hand at
the P798 boundary, with a forced rebuild on each side:

| Commit | Result |
|---|---|
| `ba6f7a66` (parent of P798) | **PASS** — 3 m 42 s |
| `a8896574` (P798) | FAIL — 3 m 32 s, `Skirmish condition timed out` at the 150 s combat wait (P798's `P727SkirmishTests.cs:204`, the then-current line of the same assertion), `Shots:0` |

**First bad commit: `a8896574` (P798).** The one thing P798 changed for the
skirmish is the map reveal:

```diff
-                // P797 — the bots cannot scout yet, so a skirmish reveals the map …
-                ShowMap = true,
+                // P798 — native fog stays on …
```
(`src/Allegiance.SinglePlayer/SkirmishOptions.cs`, P798 hunk at `:124-131`.)

### 1.3 Mechanism (what the bots do instead of fighting)

All of this is fog-on behaviour; every line below is from the same P798-era code
path that still exists on master.

1. **Both fleets are parked by the clash choice.**
   `AssignClashTargets` (`tools/allegiance-mcp/BotManager.Exploration.cs:527-615`)
   gives every non-scout bot the side's *clash* sector and `UseStaging = true`
   (`:614`, now `= !meetingEngagement`). The clash comes from `ClashScoring.Choose`
   (`tools/allegiance-mcp/SideIntel.cs:354-431`), whose candidates include
   **own** miner sightings (`:405`) and whose `OwnMinerWeight = .4` /
   `HopPenalty = .12` (`SideIntel.cs:762-766`) make a miner sitting on the home
   station (`hopsOwn = 0`) beat everything a fogged side knows. The rig run
   reproduces it verbatim:

   ```
   [clash] side 1 chose sector 1051 (score=1.03, ownMiners=1, hops own=0 enemy=3; known=2 rocks=0 enemyStations=0)
   [clash] side 0 chose sector 1050 (score=1.03, ownMiners=1, hops own=0 enemy=3; known=2 rocks=0 enemyStations=0)
   ```

   1051/1050 *are* the two homes, so each fleet's objective is its own base: the
   support pilot parks (`[support-summary] side=1 phase=opening anchor=1051`), the
   single fighter per side sits at `targetSector=1050/1051, arrived=True`, and the
   bomber is the only ship that leaves.
2. **The fallback that would have produced contact is rig-scale.**
   `ClashFallbackSeconds = 420` (`SideIntel.cs:713`) is the only way out when no
   *contested* sector is known — 7 minutes, i.e. three times the fixture's wait —
   and it is not even reached here because the own-home miner *is* a "resource"
   candidate, so `Choose` never returns null.
3. **Under fog the fleet cannot route anyway.** `TransitWarp` failing on an aleph
   the side has not seen falls into `SweepSector` (`BotManager.cs:4955-4956`), a
   full 3600-radius, 10-waypoint ring (`ScoutOpening.SweepRadiusMin/Max`,
   `SideIntel.cs:701-702`) that costs 100-160 s in a sector. Measured on master
   with a temporary per-bot trace: side 0's interceptor needed ~100 s to cross its
   own home sector, and side 1's needed ~60 s plus a 40 s staging hold, before
   either could enter the second sector — so even a perfect clash far from home
   cannot be reached inside 210 s with two scouts per side on a 13-sector map.

Net: the two **fleets** never co-locate and the few ships that do cross paths
(scouts, the loners on their own objectives) are outside weapon range or are
explorers that hand the tick to their sweep, so `shotsFired` stays 0 in the window.
Pre-P798 the same fixture passed because `ShowMap = true` made every aleph known,
so the clash was a *contested rock* sector and both fleets could fly straight to it.

### 1.4 Fix (behaviour, scaled — not the test)

Nothing here is a new rule for the rig: every piece is gated on the fleet being
small (`MeetingEngagementCombatBots = 8` combat bots per side, i.e. the 4–5 of a
Small skirmish) or on the skirmish size (the map reveal), so the 200v200 P798/P830/P831
behaviour is unchanged.

1. **`SkirmishOptions`** (`src/Allegiance.SinglePlayer/SkirmishOptions.cs:132-146`):
   `ShowMap = TeamSize < 30`. Native `bShowMap` reveals **warps only** — "Show all
   warps at the start of the game" (`Igc/igc.h:1123`, default false at
   `Igc/igc.h:1244`), `CmapMakerIGC::RevealMap` "Show every side every warp"
   (`Igc/mapmakerigc.cpp:999-1016`, run with `RevealHomeClusters` at `:471`/`:477`
   before `ActivateSides` at `:482`) — never stations or rocks. A Small/Medium
   skirmish (5/15 per side, the single-player quick game on the authored 13-sector
   map) gets routable space; *what* the fleets fight over stays per-side discovery.
   Large/Epic/Grand keep native fog exactly as shipped.
2. **`ClashScoring.Choose`** (`SideIntel.cs:396-404`): the side's own home is never a
   clash candidate (the fleet is already there; own miners in a *forward* sector
   still mark a contested area).
3. **`ClashScoring.Midpoint`** (`SideIntel.cs:441-460`) + the fallback path
   (`:415-431`): with nothing contested known, both sides commit to the balanced hop
   midpoint between the two homes, computed from the topology and home flags only —
   global knowledge every side has (`CLUSTERINFO(200)` carries every cluster id, its
   home flag and every warp's from/to cluster without positions,
   `FedSrv/fsmission.cpp:4122-4163`) — and **symmetric**, so both sides target the
   same sector instead of their own bases.
4. **A small skirmish is a meeting engagement** (`spawn_scenario`
   `meeting_engagement=true`, sent by `SkirmishDriver.cs:29-36` when the battle has
   ≤ 12 bots; `BotManager.ConfigureMeetingEngagement`, `BotManager.Exploration.cs:55-64`):
   * `ScoutOpening.ClashDelaySecondsFor` / `ClashFallbackSecondsFor`
     (`SideIntel.cs:712-748`) — the opening commit delay and the topology fallback
     scale with the side's combat bots (20 s / floor for a small fleet, 75 s / 420 s
     at the rig's 48), so a four-ship side does not spend its battle waiting.
   * `AssignClashTargets` (`BotManager.Exploration.cs:540-570`) clears
     `UseStaging` for the small fleet — the P539d wing barrier exists to stop a
     30-ship wing trickling into a settled position; four ships cannot "assemble" and
     the hold only cost ~40 s measured.
   * `DriveCombat` (`BotManager.cs:4846-4859`) with `VisibleEnemyInSector`
     (`BotManager.Lifecycle.cs:405-432`): a transiting small-fleet ship engages a
     **visible** enemy in its own sector instead of flying past it.
   * `TryDriveExploration` (`BotManager.Exploration.cs:757-770`): a small fleet's
     single scout hands the tick to the combat drive when it can see an enemy ship
     (two nine-bot fleets' scouts met at 1.2 km and nobody fired).
   * the explorer detachment floor drops from 2 to 1 when a side has fewer than six
     eligible hulls (`BotManager.Exploration.cs:318-326`), so the 9-bot skirmish
     puts two fighters into the clash instead of one.

Everything else about the opening (lanes, sweeps, `IsExplorer` including P830's
floor-not-cutoff rule, the intel store, miner raids, the finale pilot) is untouched.

### 1.5 Measured result (same fixture, same host, final code)

```
[clash] side 0 chose sector 1052 (score=0.50, topology fallback: midpoint between the homes (hops own=2 enemy=2; no contested resource seen yet); …)
[clash] side 1 chose sector 1052 (score=0.50, topology fallback: midpoint between the homes (hops own=2 enemy=2; no contested resource seen yet); …)
[gallery] coverage: t=60s … fired=0 triggerEvents=0 hits=0 damage=0 …
[gallery] coverage: t=163s … fired=3 triggerEvents=4 hits=13 damage=308 …
```

Both sides pick the *same* sector for the clash, the first shots and the first real
hits land inside the fixture's window (`hits=13, damage=308` by sim 163 s), and
`P727SkirmishLiveTests.Local_start_battle…` passes **3/3 alone** (§5). Intermediate
iterations (clash-only fix, then clash + reveal) fired 0–1 shots: the engagement was
*possible* but not reliable, which is why the meeting-engagement half exists.

## 2. Failure 2 — the rotated 2-bomber finale never re-arms inside the window (P682b)

### 2.1 Reproduction (master `04b860ae`)

All four theory rows failed; the named one:

```
dotnet test … --filter "FullyQualifiedName~P682b_second_match_relaunches_mature_and_strike_then_third_match_starts&DisplayName~port"
```
→ `match 2 automatic start and fresh finale timed out` at `P682NextMatchCombatLiveTests.cs:62`
(240 s), 5 m 09 s, `Shots: 0`.

### 2.2 Mechanism (why the re-arm does not happen in time)

First bad commit confirmed by a boundary pair run in this worktree on an idle host
(`git worktree add` of the two revisions, full build each, the two `pipeline=false`
cases each):

| Commit | Result |
|---|---|
| `8f7dda9d` (P831's parent) | **PASS** — 2/2 cases, 6 m 59 s |
| `62a5b50f` (P831) | FAIL — 2/2 cases, `match 2 automatic start and fresh finale timed out` (10 m 18 s) |

**First bad commit: `62a5b50f` (P831)** (P832a had already recorded 3/3 of the
`port, pipeline=false` case failing on exactly that commit).

A temporary trace inside `RearmRotatedFinales` (removed again) shows the state
once per second after the rotation. Rotation happens at manager t ≈ 69 s; the
fixture's wait allows until ≈ 309 s.

```
rearm t=194.3 gate=no enemy station observed by side(s) [0] grace=False
  [mcp-b001 s1 reset=True pend=False tgt=1050 recon= sec=1059 stations=4329:s1@1051,4328:s0@1050]
  [mcp-b002 s0 reset=True pend=False tgt=1051 recon= sec=1052 stations=4328:s0@1050]
```

So:

* The bot-blocking half of P831 is **not** the problem any more: both bombers are
  `reset=True`, `pend=False`, `sameRun=True`, on the new life generation.
* P831's **per-side fog gate** is: `blind = [0]` — side 0's bomber has not yet
  observed an enemy station (`SideObservedEnemyStation`,
  `tools/allegiance-mcp/BotManager.Finale.cs:477-486`), and P831 releases that gate
  only when `now - run.RotatedAt >= RearmGraceSeconds` — **300 s**
  (`BotManager.Finale.cs:354-357`), i.e. t ≈ 369 s, 60 s past the fixture's
  deadline. (Before P831 the condition was `stations` from the **first** bot's
  world, and side 1's bomber had seen side 0's station at t = 144 s, so the re-arm
  fired inside the window — that is the regression P831 introduced.)
* Why side 0's bomber takes so long: after `RestoreFinaleForNextMission` +
  `RefreshLife` the fresh life's objective became `TargetSector = null` (P831's
  "resolvable" fallback). `AssignClashTargets` skips any side below
  `ScoutOpening.MinFleetForExploration = 4` combat bots unless a finale is
  *pending* (`BotManager.Exploration.cs:537-549`) — and a *rotated* finale is not
  pending, so the one-bomber side was left with no objective at all ("no fixed
  objective yet" is only resolvable if something later hands out a duty). It
  eventually got the enemy-home target (the trace shows `tgt=1051`), but only from
  the clash path, and it was still one sector short at the deadline.

### 2.3 Fix

1. **`FinaleAwaitingRearm`** (`BotManager.Finale.cs:138-159`): a finale that is
   `Rotated && !NextRunCreated` is the same situation as a *pending* finale from
   the small fleet's point of view — a side must go and observe an enemy station
   before the next match can be armed. Both small-fleet exceptions now accept it
   (`BotManager.Exploration.cs:286` recon roster, `:310` bomber-recon fallback,
   `:547` clash assignment). This is the missing duty hand-out, not a relaxed
   rule: the fresh objectives still come only from what the side itself observes.
2. **`RearmGraceSecondsFor(participating)`** (`BotManager.Finale.cs:354-371`),
   used by `RearmRotatedFinales` at `:387-395`: the rig figure 300 s scaled by the
   participating bots, floored at 120 s (a real pod relaunch plus one sector of
   flight) and never above 300 s. The 200v200 rig (≥ 48 bots) is unchanged; a
   two-bomber finale gets a deadline inside the fixture window. Both relaxations
   keep P831's boundedness and its once-per-run log lines (`LogRearmGrace`), and
   the per-bot exclusion timer uses the same scaled value (a bot blocked only just
   now still keeps its own fresh clock — P831's rule is untouched).

Measured result: the second and third matches arm and strike, all four rows pass
(§5).

## 3. Native-intent classification

* **Revealing the warps for a small skirmish** — *native rule, native option*:
  `bShowMap` "Show all warps at the start of the game" (`Igc/igc.h:1123`,
  default off `Igc/igc.h:1244`), implemented as `CmapMakerIGC::RevealMap`
  "Show every side every warp" (`Igc/mapmakerigc.cpp:999-1016`) which runs with
  `RevealHomeClusters` before `ActivateSides` (`:471`,`:477`,`:482`). P797 already
  ported it as the per-mission `ShowMap`; P837 only chooses *when* a skirmish
  enables it (a mission option, exactly like native's lobby checkbox). Warps
  only: stations/rocks keep native per-side discovery
  (`FedSrv/fsmission.cpp:4169-4250`).
* **Midpoint clash from the topology + home flags** — *port adaptation of native
  knowledge*: native hands every bot the full cluster/warp/home-sector overview
  without positions (`CLUSTERINFO(200)`, "Bots can't see the map preview, so give
  them a cluster overview", `FedSrv/fsmission.cpp:4122-4163`). Deciding *where*
  the fleet commits is P798 bot policy (native has no strategic AI); P837 only
  changes the policy's input set from "sectors whose contents we have seen" to
  "the topology we were given", and makes the pick side-symmetric.
* **Rotated-finale recon duty / scaled grace** — *port adaptation*: the re-arm and
  the finale are Showcase/P798 features (no native counterpart); the grace is a
  bot-safety bound, now scaled with the fleet instead of fixed at the rig's size.
* **Nothing was fixed by weakening a test**: no assertion, deadline, tier or
  quarantine in `P727SkirmishTests.cs` / `P682NextMatchCombatLiveTests.cs` was
  touched.

### Modern / better-approach note

The two failures are the same design smell: **one rig-scale constant used at every
scale**. Both numbers now derive from the fleet (`ClashFallbackSecondsFor`,
`RearmGraceSecondsFor`) instead of being re-tuned by hand, which is also what a
live-service balance pass would do — derive time budgets from fleet size and map
size, not from one golden configuration. The next step that would remove the
remaining hand-tuned boundary (`TeamSize < 30` for the reveal) is to derive it from
*scouting capacity*: `explorers × sweep-area / reachable-sectors` against the
opening budget, so the map reveal switches off exactly when the fleet can afford
to scout. Today's `TeamSize < 30` cut is the measured version of that: 2 scouts
cannot cover 13 sectors inside two minutes, ~6 (a Large side) can.

## 4. Files changed

| File | Change |
|---|---|
| `src/Allegiance.SinglePlayer/SkirmishOptions.cs` | skirmish reveals the warps (native `bShowMap`) for Small/Medium, keeps native fog for Large/Epic/Grand |
| `tools/allegiance-mcp/SideIntel.cs` | `ClashScoring.Choose` never picks the side's own home; new `ClashScoring.Midpoint` topology fallback; `ScoutOpening.ClashDelaySecondsFor` / `ClashFallbackSecondsFor` (fleet-scaled) |
| `tools/allegiance-mcp/BotManager.Exploration.cs` | meeting-engagement flag/`ConfigureMeetingEngagement`; small-fleet recon/clash exception also fires while a rotated finale waits for its re-arm; no staging + scaled clash delay for a small fleet; one-explorer floor; small-fleet scout engages a visible enemy |
| `tools/allegiance-mcp/BotManager.cs` | `ActiveBot.MeetingEngagement`; transit engagement of a visible enemy for a small fleet |
| `tools/allegiance-mcp/BotManager.Lifecycle.cs` | `VisibleEnemyInSector` (nearest visible different-side ship in the bot's sector) |
| `tools/allegiance-mcp/BotManager.Finale.cs` | `FinaleAwaitingRearm`; `RearmGraceSecondsFor` (fleet-scaled, floored at 120 s) used by the re-arm gate and the per-bot exclusion clock |
| `tools/allegiance-mcp/McpTools.cs` | `spawn_scenario` accepts `meeting_engagement` (default false) |
| `tools/allegiance-mcp/SkirmishDriver.cs` | asks for the meeting engagement when the skirmish has ≤ 12 bots |
| `tests/Allegiance.ClientCore.Tests/P837BotEngagementScaleTests.cs` | new: own-home exclusion, symmetric midpoint, knowledge-free fallback, both scaling curves, skirmish reveal-per-size |
| `docs/TEST_FLAKES.md` | P837 observation section (supersedes the P832a "fixture mismatch" reading of P682b) |
| `docs/reports/P837_BOT_LIVE_REGRESSIONS.md` | this report |

No new service, store, port or environment variable (the two knobs added are
compile-time constants; `ALLEGIANCE_P831_REARM_GRACE` keeps working as the rig
override), so `docker-compose.yml` and `docker/` are untouched. No new media.

## 5. Verification

Host: 24 cores, idle between runs (load ≤ 0.25/CPU before each run; the only
concurrent load was the single `tools/run-tests.sh` sweep, whose projects run in
parallel by design). Every command below ran in this worktree; the JSON summary is
[`P837_assets/verification.json`](P837_assets/verification.json).

| Gate item | Command | Result |
|---|---|---|
| whole-solution build | `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| every tool project | `tools/build-tools.sh` | **89/89 built** |
| failing test 1, alone ×3 (final code) | `--filter FullyQualifiedName~P727SkirmishLiveTests.Local_start_battle…` | **PASS 3/3** (3 m 07 s each; base 0/2) |
| failing test 2, alone ×3 (final code) | `--filter FullyQualifiedName~P682b_second_match…&DisplayName~pipeline: False` (port + native rows) | **PASS 3/3** (10 m 20 s each; base 0/4) |
| failing test 2, all four rows ×1 (final code) | same filter without `pipeline` | **PASS 4/4** (20 m 42 s) |
| P830's opening tests (`P798ScoutOpeningTests`, lane churn included) + P831's re-arm tests + the new P837 tests, alone ×3 (final code) | `--filter FullyQualifiedName~P837BotEngagementScaleTests\|FullyQualifiedName~P798ScoutOpeningTests\|FullyQualifiedName~P831FallbackBomberRearmTests` | **PASS 3/3** (34 tests each) |
| never-parks showcase ×5 (final code) | `--filter FullyQualifiedName~P700ShowcaseLiveTests.Local_showcase_never_parks` | **PASS 5/5** (5 m 41, 4 m 47, 4 m 54, 5 m 29, 5 m 46) |
| ONE fast tier | `tools/run-tests.sh --tier fast` | 10,972 tests, 304.8 s, **1 failure** — `CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`, the ledgered load flake (`docs/TEST_FLAKES.md:803`, `:2546`, `:2845`); **PASS 3/3 alone** afterwards, and it touches none of the files changed here |
| AOT publish | not run | no `src/Allegiance.Core` or `src/Allegiance.Server` file changed; `Allegiance.Server` references Core + Update only (never SinglePlayer), so the rig publish is unaffected |

Bisect steps (P727) and the P682b boundary pair are in §1.2 / §2.2. The earlier
P727/P682b reproductions on the dispatch base (0/2 and 0/4) are the "before" side of
the same fixtures.
