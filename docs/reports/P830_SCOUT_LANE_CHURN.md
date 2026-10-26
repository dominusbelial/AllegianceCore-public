# P830 — scout lane churn: opening scouts re-assigned their frontier lane every pass

Base: master `0dee572d`. Evidence: batch 34
(`~/allegiance-codex-p826/.scratch/rig/b34/bots/err.worker-{1,2}.log`) and batch 35
(`/home/domingo/.claude/jobs/82c1509b/tmp/b35-bots/err.worker-{1,2}.log`), copied to
`~/allegiance-evidence/P830/` for this task; the raw local proof logs are
`~/allegiance-evidence/P830/p830-proof-{before,after}.log`. Worktree:
`/home/domingo/allegiance-pi-p830` (no commit/push).

## 1. Quantification from the rig logs

Streamed with awk (never loaded whole), classifying every `assigned lane` line per
scout: "churned" = the same scout re-assigned **the same lane** ≤ 2 s after the
previous identical assignment (the pass interval is 0.5 s, `ScoutOpening.AssignIntervalSeconds`,
`SideIntel.cs:636`); everything else is a real change (first assignment, a different
lane, or the same lane again after a real gap).

| Log | `assigned lane` lines | churned (same lane ≤ 2 s) | real changes | share churn |
|---|---|---|---|---|
| b35 worker 2 (15 h, 6 matches) | 1,115,059 | 1,100,662 | 14,397 | **98.7 %** |
| b35 worker 1 (15 h) | 679,270 | 641,811 | 37,459 | 94.5 % |
| b34 worker 1 / worker 2 | — | churn begins in the **t = 600 s bucket** in both (5 and 0 lines before it) | — | — |

Per-scout (b35 w2, top of `docs/reports/P830_assets/b35-w2-assignments-per-scout.txt`):
`mcp-w2-b136-9804` alone logged **97,140 assignments (~106/min ≈ one every 0.5 s pass)**,
96,066 of them the same lane within 2 s; the same ~12 scouts each logged 35 k–97 k.

Corroborating counts (b35 w2, 15 h): only **23** `swept sector` lines, **11**
`timed out on lane`, **0** `found the mouth` — after the churn starts, no sweep ever
progresses again. The intel lines show why it matters: at t = 1016 s (one match),
`sectors=12/13 known, swept=6` — and it stays there for hours; matches ran 2–3 h+.

**Churn starts exactly at mission start + 600 s** in every worker (b35 w1/w2 and b34
w1/w2: the first churned assignment falls in the `t = 600 s` bucket; buckets 0–600 hold
none). That boundary is the smoking gun.

## 2. Root cause

`ScoutOpening.OpeningSeconds = 600` (`SideIntel.cs:666`) bounds the opening via
`OpeningActive` (`BotManager.Exploration.cs:501`). When the window expires with the map
**not** yet complete, every 0.5 s assignment pass did:

1. `bot.IsExplorer = … && OpeningActive(now)` (old line 399) → **false for every scout**,
   so `if (!bot.IsExplorer) { bot.ExploreLane = null; … }` (line 421) **cleared every
   lane every pass** (this is one of the silent clearing paths the brief listed —
   `!bot.IsExplorer`; the `!assigned.TryGetValue → ExploreLane = null` path is
   innocent: it only fires when a lane really left the pool).
2. The assignment block still ran (`if (complete || lanes.Count == 0) continue;` —
   `complete` is false because nobody sweeps any more), re-assigned the **same** lane to
   every scout (the planner's `current`-keeper was fine), and the log line
   (`BotManager.Exploration.cs:468`) fired every pass because `had` was always false.
3. `TryDriveExploration` refused to drive (`… || !OpeningActive(now)`, old line 689), so
   no sweep ever completed, `complete` never became true, and the loop ran for the rest
   of the match — 1.08 M lines on b35 worker 2.
4. The `_laneStarted` hold-timeout could never fire either (`timed out on lane`, line
   330): the lane age resets every pass because the lane is re-assigned (and
   `_laneStarted[bot.Name] = now`) 0.5 s later. Hence 11 timeouts in 15 h.

So the churn and the fog stall are one bug: **the opening window cut exploration off at
a fixed 600 s, while the assigner kept re-issuing lanes nobody was allowed to fly.**
The local "before" proof reproduces the boundary exactly (§4).

## 3. Fix (bot-side, race-agnostic)

All in `tools/allegiance-mcp/` (no `src/` change → AOT gate not triggered):

1. **The window is a floor, not a cutoff** — `BotManager.Exploration.cs:410`:
   `bot.IsExplorer = … && (OpeningActive(now) || !complete)`. Scouts keep exploring
   while the side's reachable map is not fully known **and** swept; the stand-down when
   `complete` (raid linger, recon) is unchanged. `TryDriveExploration` (line 721) drops
   its `OpeningActive` gate for the same reason — the `complete` stand-down inside it
   governs.
2. **Lanes are only handed to scouts that ARE explorers this pass** (line 440). The
   clear-then-reassign cycle cannot happen: a non-explorer keeps a cleared lane and is
   not re-issued one.
3. **Hysteresis** — `ExplorationPlanner.Assign` (`SideIntel.cs:173`) keeps a held lane
   while it stays valid (in the pool) and releases it only when a **free** lane beats it
   by `ScoutOpening.LaneSwitchScoreMargin` (250 score points, env
   `ALLEGIANCE_P798_LANE_SWITCH_MARGIN`, `SideIntel.cs:673`). Never a lane another scout
   holds; the released lane returns to the pool for the next pass. Deterministic
   (kept lanes by scout id), O(scouts × lanes). Unchanged inputs assign exactly the
   same lanes every pass — that is the churn-freedom proof the unit tests pin.
4. **The log line now fires only on a real change** (kept, line 468), plus a one-shot
   per invalidation line (`… lane X->Y no longer valid — taking the next frontier`,
   line 461) that names the silent drop when a held lane leaves the pool (swept / timed
   out / destination known). No per-pass summary needed — after the fix there is
   nothing per-pass to summarise.

Lane validity is exactly the brief's rule: a scout keeps its lane until the sweep
completes, the lane becomes invalid (sector known/swept, scout dead/pod/rearming →
leaves the roster, recon duty, hold-timeout backoff), or a strictly better assignment
beats it by the margin. No sector or civ IDs anywhere.

## 4. Local proof — 30v30, m2xspiderw, native fog (temporary Live harness, deleted after)

In-process server + `spawn_scenario` showcase `count_a/b:30`, fog on (no `ShowMap`, no
`target_sector`), bot log streamed to file, side intel sampled every 5 s. "Full
knowledge" = `seenReachableShare == 1`; "full sweep" = `sweptSectors` covering all
reachable non-home sectors (JSON in `docs/reports/P830_assets/proof-{before,after}.json`).

| Metric | before (`0dee572d`) | after (fix) |
|---|---|---|
| assignments / minute (whole run) | 35/min, **all 396 churned re-assignments occur after t = 600 s** (99/min in that window; zero before it — the rig boundary, reproduced) | **11.5/min, 0 churn** — 72 assignments, 72 real changes, 4 lane invalidations |
| time to full map knowledge | 265 s / 260 s (side 0 / 1) | 245 s / 270 s |
| full sweep | side 1 ~360 s (12/13 + home); **side 0 never — stuck 11/13 at 14 min**, no sweep after 600 s | **both sides ≤ 375 s, `exploration complete: 13/13` logged** |
| time to first contact (first enemy station seen) | 142 s / 194 s | 167 s / 200 s (same, run noise) |

Honest statement of the behavioural effect: on this map/size the knowledge phase
finishes before the window either way, so time-to-knowledge and first contact are
unchanged within noise. The effect the rig shows locally too is the **tail**: before
the fix the last 1–2 sectors are never swept once the window expires (side 0 stuck
11/13 for the remaining 8 min of its run, churning 396 log lines), after the fix both
sides finish the sweep and stand down. On the rig (200v200, deaths, raids) the window
expired mid-opening far more often — that is where ~1.08 M lines/match and hours of
unswept fog came from.

## 5. Tests / verification (light gate)

| Gate | Result |
|---|---|
| whole-solution build `AllegianceCore.slnx` | **0 warnings / 0 errors** |
| `tools/build-tools.sh` (every `tools/*/*.csproj`) | **0/0** (all projects) |
| AOT publish | **not required** — no `src/Allegiance.Server`/`src/Allegiance.Core` change |
| New unit tests (`P798ScoutOpeningTests.P830_*`: lane kept across passes while valid; re-assigned when invalid; no churn under score jitter / unchanged explorers; hysteresis margin, never stealing a held lane) | **3 tests ×3 alone: 3/3, 3/3, 3/3** |
| ONE `tools/run-tests.sh --tier fast` | 10,832 tests; 3 failures, **each 3/3 alone** afterwards (`SessionLossLiveTests.EstablishedSession_ServerKilled…` transport-thread timing, `AsteroidsUpdateLiveTests.Mining_depletion…` timing, `P828RadarLabelDeclutterTests.The_selection_path_allocates…` allocation) — the ledgered load-flake classes, none in P830 code; P798/P830 unit tests all green in the sweep |
| Showcase never-parks (bots changed → 5×) | before-fix base: **5/5**; fixed tree: **7/10**, 3 × `Parked >5s` with a live combat target and zero velocity — byte-for-byte the ledgered P706/P708 signature (host load average 3–6 with no local test running; see the P830 note appended to `docs/TEST_FLAKES.md`). The P830 diff does not touch the combat engage path that sets a Fighter's target; recorded as the same host-load artifact, honestly noted here rather than waived |

Scratch removed: baseline worktree, temporary proof harness and `.scratch/` deleted;
evidence at `~/allegiance-evidence/P830/` (raw proof logs), summaries in
`docs/reports/P830_assets/`.

## 6. Native-intent classification and approach note

**Classification: design (bot-side policy), not native.** Native Allegiance has no
scout/exploration AI at all — its miners flee attackers on their own
(`Igc/shipIGC.cpp:1780-1930`) and nothing in the native source assigns or times out
exploration lanes; the frontier/lane planner, the opening window and the intel store
are P798 policy (`BotManager.Exploration.cs:19-31`). The only native rules touched by
this fix are the fog constraints the opening already cites (per-side visibility
`FedSrv/fsmission.cpp:4169-4250`, route-over-unseen-aleph refusal
`Igc/common.cpp:894-960`), and this change keeps them: scouts sweep only what their own
scanners see, and knowledge grows only from sightings. The fix therefore changes
P830-owned policy to match its own stated intent (lanes are held until invalid), not
any native behaviour.

**Modern/better-approach note.** The deeper smell is that two responsibilities shared
one flag: `IsExplorer` meant both "this scout is on exploration duty" and "the opening
phase is active", so a *phase* boundary (600 s) mutated per-scout *state* every pass.
The cleaner shape — partially reached here — is a small explicit explorer state machine
per scout (`Idle → Lane(lane) → Sweeping(sector) → Done`), where the assigner only
diffs desired-vs-current state each tick and the log is a change feed, not a side
effect of re-computation. With that, phase expiry would be just another transition and
a bug like this one would be impossible to express (there is no "clear everyone, then
re-assign everyone" path). If the opening grows more special cases (finale re-arm,
rotation), factoring that state machine out of `BotManager` into its own testable type
is the recommended next step.
