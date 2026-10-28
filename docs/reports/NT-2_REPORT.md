# NT-2 — triage of the 2026-10-28 03:01 nightly (master `a2f73c13`)

Base: master `a2f73c13` at dispatch. Worktree `~/allegiance-pi-nt-2` (branch `pi/nt-2`),
no commit/push — all changes left in the working tree. Raw nightly logs/TRX copied once
from the mini runner into `.scratch/nt2/` (deleted after the task; this report and the
repo diffs are the durable record). The mini (192.168.0.164) was only **read**, never
written to and never asked to run anything.

## Summary table

| Failure (tier) | Verdict | Root cause (first bad commit) | Fix in this worktree |
|---|---|---|---|
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders…` (Fast) | runner/env | the nightly builds only the solution before the fast tier; `tools/p800-hull-proof` is not built (`dotnet <missing.dll>` → exit != 0, 219 ms) | test builds the tool in-process once if the DLL is missing, else **skips with a clear reason**; runner diff proposed below |
| `SC2NaniteSupportTests.Support_selection…(civ: 25)` 16.3 s (Slow-Fast) | load flake | fast tier runs 4 projects concurrently on the mini's slower cores | 3/3 alone locally (6–7 s for both class cases); ledgered in `docs/TEST_FLAKES.md` |
| `P814GunnerTests.P814b_TurretCarrier_pref…` 17.3 s (Slow-Fast) | load flake | same | 3/3 alone locally (~3 s); ledgered |
| `P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate…` (Live) | regression (test fixture vs P797) | P797 `7734a197` map discovery: a station reaches a bot only once SeenBySide; BotManager registers a finale only after every side has **observed** an enemy station → both finales stayed pending (`[finale-sample] []`), `FinaleStatus().Length == 2` never true | fixture seeds the sides' station visibility before the bots spawn and again on the live mission after each rotation (what the showcase's scouts discover); post-rotation waits widened to 240 s (rotation drain + intermission + 15 s countdown) |
| `P699PodHomeLiveTests.Mcp_pod_flies_two_jumps…(side 0/1)` (Live) | regression (test fixture vs P797) | P797: the pod's client knows only the alephs its side has seen → the two-jump route is unroutable → the pod dies of oxygen (`KillShip BDeathCredit=True`) | `ShowMap = true` (native `bShowMap`, `igc.h:1123`) in the game config — the same fix P859 applied to the sibling `P699HumanPodHomeLiveTests` |
| `P733ResearchTests.No_bomber_tech_finale…` (Live) | regression (test fixture vs P797) | P797: the enemy home station never reaches the client → `RegisterFinale` throws "finale needs an observed enemy home station" (3 s, deterministic) | fixture seeds station visibility per match and pumps until the peers observe an enemy station |
| `P765GameOverStallTests.P765_GameOver_delivers_once…` (Live) | test race | the migration pump accepted a peer whose `World.MyShipId` was **cleared** (old ship removed before the fresh assignment) — `!= oldIds[i]` is true for null → `Single()` found no ship | pump now requires a live new id (`MyShipId is { } newId && newId != old && server.Ships.Any(id)`); 5/5 after (was ~50–70 % fail) |
| `P778ABomberPushLiveTests.Staged_finale_pushers_contact…` (Live) | regression (two parts) | ① P797: enemy station never observed → spawn_scenario armed the finale pending, `_finales` stayed null → `Runs()` threw ArgumentNullException (890 ms); ② P798/P830: the staged escort was assigned as a P798 opening explorer (`IsExplorer` re-armed every pass), and `TryDriveExploration` outranks `TryDriveFinaleEscort` in the decision order → the pusher chased lanes instead of pushing (0 `[push] contact` lines) | ① fixture seeds station visibility after Boot; ② production: the opening planner no longer marks a committed `FinaleEscort` bot as an explorer (`tools/allegiance-mcp/BotManager.Exploration.cs:430`) — a finale escort is owned by the strike (P778a) |
| `SC2NaniteSupportLiveTests.One_legally_purchased_support_pilot…` (Live) | regression — **P791 `2bc9bf83`** | P791 added `HullFraction = 1f` to `RepairTarget` and gated the nanite fire law on `target.HullFraction < 1f` (native full-target stop, `shipIGC.cpp:2668-2671`) — but the commander-order repair path built its `RepairTarget` **without** the fraction → default 1 → **commander-ordered repairs never fired** (repair credit 0.0000 forever, 53 s timeout) | `tools/allegiance-mcp/BotManager.Orders.cs`: pass `HullFraction: shipPose.HullFraction` / `live.HullFraction` in both commander repair-target constructions |
| `SC2NaniteSupportLiveTests.Research_x16_showcase_support…` (Live) | regression (test fixture vs P797) | P797: the finale never registered (`[finale-sample] []` for the whole 20-min window on every nightly since it was added) → no strike → no damage → both repair counters stayed 0/0 | fixture seeds station visibility before the 90-bot fleet spawns (same rule as P660/P733/P778) |
| `SC2SupportPhaseLiveTests.Support_pilots_follow_the_side_phase…` (Live) | regression — **P791 interaction** | a commander-driven repair (research commander) never touched the autonomous hold bookkeeping, so `SupportHoldTarget`/`SupportHoldSince` kept reading as one endless hold — the gate measured "held one repair target for 198.2 s" (the 45 s leash only runs on the autonomous path) | production: the commander-repair path now maintains the hold state honestly (`SupportHoldTarget` = commander target, `SupportHoldSince` reset on change — `BotManager.Support.cs`); test: the hold-duration metric scores autonomous holds only, since a commander repair has no leash in production (`shipIGC.cpp:1936-1939` — the commander owns the target) |
| `SC5CloakLiveTests.Rig_civs_at_research_eight…` (Live) | load flake (watch) | failed only on the 10-28 nightly (side 0 = 0 cloak activations in 900 s); passes at pure `a2f73c13` in 2 min locally (clone) and 4 min in the worktree — the P832g per-match goal rotation may make side-0 cloak usage slower under the runner's load | 3× alone locally (see verification); ledgered |
| `BroadcastSnapshotTests.Recorded_ticks…` ×4 (Perf) | stale golden — explained, intended change | **P797 `7734a197`** (real `git bisect --first-parent`, `fd510d2e` green … `7734a197` red): the cross-side DISCOVERY sweep (`SweepCrossSideDiscoveryExports`, port of `FedSrv.CPP:10766-10775` ShowObject + `:11055-11062` OBJECT_SPOTTED, `fsmission.cpp:4169-4250`) exports each newly-seen static object once per peer, adding those one-shot discovery packets to the recorded transcript | golden regenerated with the cited commit; the per-peer off/on/encode-once byte-equality assertions are unchanged and all four variants still agree |
| `P706DispatchProfilingTests.Dispatch_budget(matchStart, 165)` (Perf) | budget calibration (env), not a regression | nothing grew: the mini's median is flat 302–331 ms across the 10-19…28 nightlies (before and after P797…P860). Normalised (median ÷ P744 factor ≈ 1.5) the workload costs **202–222 ms — above the 200 ms Debug base**: the hull-separation loop is cache/branch sensitive (P706's deliberate random-attitude worst case defeats the pair-keyed separation cache; 23,227 of 25,828 events pay the full convex `Separate` and are only then debounced) and scales superlinearly with the scalar-FP reference factor | Debug base 200 → 240 ms with the full evidence in a comment (Release gate untouched at 50 ms); a real dispatch regression still trips it (`P744_SIMULATE_REGRESSION`) |
| `Transport.ConsoleVerbosityLiveTests.Stalled_stdout…` (Perf) | regression — found by instrumentation, old code newly *exercised* | `BroadcastStationsUpdate`/`BroadcastAsteroidsUpdate`/`BroadcastProbesUpdate` (P133/P134/P135) wrote `[stations]`/`[asteroids]`/`[probes]` per cadence tick as **plain `Console.WriteLine` on the sim thread**, bypassing the P619c non-blocking sink. When stdout is stalled, the drain thread holds the `Console.Out` sync lock ~30 ms per queued line, so each cadence write costs the tick 30–60 ms → ~17 s for 300 ticks. Captured live: a local run of the test failed with the writer's stack showing `Server.BroadcastStationsUpdate ← SimPass`. Since P797 latches stations visible more reliably, the lines now fire almost every night on the mini (passing 2 s on 10-22/23, failing ~17 s since 10-24) | the three per-cadence lines are now `if (ConsoleLog.Verbose) ConsoleLog.Write(…)` (gated, non-blocking) — same class as P318/P619b; P797's per-object `[discover]` rig-probe lines (same vector, bursty) gated the same way |

## The two bisects (real `git bisect`, transcripts in `.scratch/nt2/`)

1. **Broadcast golden** — `git bisect start --first-parent HEAD fd510d2e`, script builds
   `Allegiance.Core.Tests` and runs the four `Recorded_ticks` cases:
   `fd510d2e` good … `7734a197` (P797) bad, 6 steps, every step a real 4-case run.
2. **P765** — the test is genuinely flaky (a docs-only commit tested "bad" once), so the
   bisect was abandoned in favour of state capture: the failing peer's `World.MyShipId`
   was **null** (cleared mid-migration) while the pump had already accepted it. The pump
   predicate was the defect (test race), not the production migration.

## Verification

Build: `dotnet build AllegianceCore.slnx` — 0 warnings / 0 errors.
Tools: `tools/build-tools.sh` — clean (see below).

Every touched/fixed test, 3× alone (durations are single-run wall times on the
workstation):

| Test | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| `BroadcastSnapshotTests.Recorded_ticks…` (4 cases) | 47 s ✓ | 46 s ✓ | 44 s ✓ |
| `ConsoleVerbosityLiveTests.Stalled_stdout…` | ✓ | ✓ | ✓ (pre-fix it failed 1 of 8 with a captured `[stations]` stall stack) |
| `P706DispatchProfilingTests` (both scenarios) | 34 s ✓ | 35 s ✓ | 34 s ✓ |
| `P800HullProofGpuTests` (tool built in-process on run 1) | 4 s ✓ | 3 s ✓ | 3 s ✓ |
| `P733ResearchTests.No_bomber_tech_finale…` | 4 s ✓ | 4 s ✓ | 4 s ✓ |
| `P778ABomberPushLiveTests.Staged_finale…` | 54 s ✓ | 54 s ✓ | 54 s ✓ |
| `P765GameOverStallTests.P765_GameOver_delivers…` | 17 s ✓ | 18 s ✓ | 18 s ✓ (5/5 total) |
| `P699PodHomeLiveTests` (both sides) | 1 m 37 s ✓ | 1 m 37 s ✓ | 1 m 36 s ✓ |
| `P660ShowcaseLiveTests.Showcase_bots_and_humans…` | 22 s ✓ | 22 s ✓ | 22 s ✓ |
| `SC2NaniteSupportLiveTests.One_legally_purchased…` | 11 s ✓ | 12 s ✓ | 14 s ✓ (`hull=0.500→0.891 nativeRepairCredit=0.391 effectiveHits=3`) |
| `SC2NaniteSupportLiveTests.Research_x16…` | see below | | |
| `SC2SupportPhaseLiveTests.Support_pilots_follow…` | see below | | |
| `SC5CloakLiveTests.Rig_civs…` | 3 m 58 s ✓ | see below | |
| `SC2NaniteSupportTests.Support_selection…` (fast, load-flake evidence) | ✓ | ✓ | ✓ |
| `P814GunnerTests.P814b…` (fast, load-flake evidence) | ✓ | ✓ | ✓ |

Fast tier: one `tools/run-tests.sh --tier fast` (result below).
Showcase never-parks ×5: (result below).

## Runner-script diff for Claude to apply

The nightly's fast tier fails `P800HullProofGpuTests` because `tools/` is not built.
The test now covers the fresh-checkout case itself (in-process build, clear skip), but
the runner should build the tools once before the fast tier so the proof tool is
consistent and the first-run build cost stays out of the calibrated tier:

```diff
--- a/tools/test-tiers/runner.py
+++ b/tools/test-tiers/runner.py
@@ -247,6 +247,11 @@ class Runner:
     def run(self):
         if not self.args.no_build:
             print("Building solution", flush=True)
             if self.command(["build", "AllegianceCore.slnx", "-m:8", "-v:minimal"], self.output / "build.log"):
                 raise RuntimeError(f"Build failed: {self.output / 'build.log'}")
+            # NT-2 — the fast tier's P800 GPU proof runs tools/p800-hull-proof as a
+            # child process; build every tool project once (the same gate ci.yml and
+            # tools/build-tools.sh enforce) so a fresh checkout does not fail the
+            # proof on a missing DLL. The test still builds the tool itself and skips
+            # with a clear reason if this is missing (defence in depth).
+            if self.command(["bash", "tools/build-tools.sh"], self.output / "build-tools.log"):
+                raise RuntimeError(f"tools build failed: {self.output / 'build-tools.log'}")
         with ThreadPoolExecutor(4) as pool:
```

## Notes for follow-ups (outside this task's scope)

* `Server.Timescale.cs:256` (`[tidi]`), `Server.Broadcast.cs:2549` (`[overrun]`, real
  cadence only), `:549/:567/:519` (window summaries) and the P853 `[missionparams]` /
  `[wire]` handler lines are still plain `Console.WriteLine` on the sim thread. In
  production `ConsoleLog.Install()` makes every plain write an enqueue, so there is no
  freeze vector for a real server — only in-process test servers (which capture
  `Console.Out` directly) are affected. If more in-process tests hit stalled-stdout
  budgets, those lines should move to the gated sink too.
* `Research_x16` (90 bots, 20 min) and the SC suite dominate the live tier's 10,046 s;
  after the finale-registration fix the strike damage path may let the 1200 s internal
  budget shrink — worth a follow-up measure.
