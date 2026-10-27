# P831 — a fallback-hull bomber never finishes its new-mission setup, so the finale never re-arms

Base: public master at dispatch (`f3e5148f`, with P821/P821b, P827, P830). Worktree
`allegiance-pi-p831`; nothing committed. All `src/` untouched → the AOT publish gate
does not apply.

---

## 1. Symptom, re-verified in the copied evidence

Two 200v200 showcase matches ran for hours with **no deadline** and had to be cut. Both
times the next match's finale was never re-armed (`RearmRotatedFinales`,
`tools/allegiance-mcp/BotManager.Finale.cs:331`), and the only bots that went completely
silent were the ones whose **requested role was Bomber while they flew a P821 fallback
hull**.

Measured again in the local copies (no remote host touched):

| Evidence | What the lines actually say |
|---|---|
| b36 `err.worker-1.log` | rotation 1: `[p598] re-readied` batch at **207738**, the bots' next-match purchases (`[p682] … bought and launched next-match hull`) at **208814** — and `[finale] re-armed for next mission` only at **289769**, i.e. **82 011 lines later** |
| b36 `err.worker-1.log` | rotation 2: `[p598] re-readied` at **342730**, last `[p682]` at **344598**; after that the 22 requested-Bomber bots log **exactly 3 lines each** (`[p598] re-readied`, `[loadout] … temporary …`, `[p682] … bought and launched`) and **nothing** for the remaining **139 103** lines, while **174** of that worker's 196 bots stay active |
| b34 `err.worker-1.log` | **no** `[finale] re-armed` line at all: the *first* rotation already failed (finale trigger at 77331) |
| b35 `err.worker-1/2.log` | **7 re-arms (3 + 4)… with the same fallback bombers present** — worker 1 still spawns the same **22** requested-Bomber slots on a fallback hull (worker 2: 26), with 187/230 `… temporary …` loadout lines. The brief's guess that batch 35 had "no fallback bombers" is **wrong**: the failure is a knowledge/timing state, not a property of the matchup |

The silent set is exactly the requested-Bomber block (`mcp-w1-b171…b198` in batch 36;
`22 + 26 = 48` bots over the two workers — the brief's 48 of 48). Their `[loadout]` line
says `requested role=Bomber unavailable/over budget; temporary Interceptor` — the
P821/P821b fallback (`BotManager.TechLoadouts.cs`, `PickTechLoadout`).

## 2. Root cause (proved, not assumed)

`RefreshLife` (`tools/allegiance-mcp/BotManager.Lifecycle.cs:240`) marks
`ObjectivesPending = true` on every fresh life of a new mission, then tried to derive the
new mission's objective and — **before this task** — did this:

```csharp
if (bot.TargetSector is not null && target is null) return false;   // old line :290
```

and the bot's drive path starts with `if (… !RefreshLife(bot, cs, now)) return;`
(`BotManager.cs:4758`, and the pass loop at `:1685`). **A pending bot is never driven.** So
the derivation could deadlock itself: it needs knowledge of the fresh, fogged world
(an observed enemy station, or exactly one home-flagged cluster after subtracting the
side's *own* stations) that a bot which never flies cannot obtain.

Three concrete ways it stayed unresolvable, all reproduced or measured:

1. **Two "enemy" homes.** `bot.TargetSector` survives the reset; the bomber branch
   (`:276-288`) prefers an observed enemy station, else subtracts `world.Stations`
   own-side clusters from the `CLUSTERINFO(200)` home flags. On a fresh mission
   `ClientWorld.Stations` is empty (`ClientWorld.ResetCore` clears `_stations`,
   `src/Allegiance.ClientCore/State/ClientWorld.cs:2069`) until the side's exports
   arrive, so **both** homes count as "enemy" (`enemyHomes.Length == 2`) and the branch
   fails.
2. **No `ObjectiveName`.** It is only set when the spawn pinned a `target_sector`
   (`BotManager.cs:620-624`; `ResolveScenarioTarget` returns null without one,
   `BotManager.Lifecycle.cs:365-367`), so a fog showcase has no name fallback
   (`:289-291` is gated on `ObjectiveName is { }`).
3. **The whole fleet can park.** Every bot that still carries a clash target from the
   previous map (`AssignClashTargets`, `BotManager.Exploration.cs:559-576`) fails the
   same gate; the non-bomber branch has no other way to name a sector, so nobody flies,
   so nobody discovers anything. Locally this reproduced with **61 of 61 bots pending and
   0 m of movement for the whole window** (see §4).

Why some rig rotations still worked (batch 35, and b36's rotation 1 after 82k lines):
the gate only closes while the bot's world has neither an observed enemy station nor a
deducible single enemy home. A rotation that lands after the side has re-observed a
station — or before the clash assignment has given a non-trivial target — resolves on the
first pass. That is exactly why the failure looks sporadic on the rig but is
deterministic once the knowledge state occurs.

**The finale re-arm made it fatal.** `RearmRotatedFinales` waited for `EVERY` participating
bot (`!b.FinaleResetObserved || b.ObjectivesPending || …`, old `:326-327`), and it is the
only thing that arms the next match's finale — the showcase's only deadline. It also read
the station list from the **first** ready bot's world (old `:342`) and gated on the union of
that one window, so a single blind bot (or a blind *side*) could hold it forever too. The
P827 diagnostic (master, `:359`) named the condition but nothing bounded it.

**`RestoreFinaleForNextMission` was checked** (`BotManager.Finale.cs:711-745`): a fallback
bomber can only be finale-reserved in research mode (`ResearchFinale` true for every bot —
`BotManager.Finale.cs:482-485` ranks `Role == "bomber"` first), and on rotation it restores
`FinaleOriginal.Target`, i.e. a sector id **from the map that just ended**. That is another
source of an unresolvable stale objective at reset; after the fix it is dropped in the same
pass as any other stale target (the resolution always overwrites `TargetSector`).

## 3. Fix (bot-side, race-agnostic, no native/server change)

Both halves are in `tools/allegiance-mcp/`; no civ, sector or station id is hard-coded.

1. **A fresh life always resolves its objectives** — `BotManager.Lifecycle.cs:292-306`.
   The `return false` is gone. When the fresh world cannot derive an objective the bot
   resolves to **"no fixed objective yet"** (`TargetSector = null`, `ObjectivesPending =
   false`) and is driven normally — it fights where it is, and
   `AssignClashTargets` re-issues a bomber's own enemy-home duty from the side's intel as
   soon as the side has one (unchanged P797/P830 path). One bounded line per life names
   exactly what the world was missing (`LogObjectiveFallback`, `:328`):
   `[p831] <bot> (side S, requested role=bomber, flying Lt Interceptor [Interceptor], P821
   fallback hull): no new-mission objective derivable yet (stale sector 1052,
   enemyStations=0, ownStations=1, candidate enemy homes=2, objectiveName=unset);
   engaging in place until this side resolves a target`.
   The requested duty is untouched (P821), the bomber objective rules themselves are
   unchanged when the world *can* answer (enemy station cluster → else the single
   non-own home cluster → else the named objective), and a stale sector id never outlives
   the reset.
2. **The re-arm is bounded in time** — `BotManager.Finale.cs:318-460`.
   * `FinaleRun.RotatedAt` is stamped by `RestoreFinaleForNextMission` (`:717`); the
     bounded grace is `RearmGraceSeconds` (`:327`, default **300 s**, override
     `ALLEGIANCE_P831_REARM_GRACE`).
   * Inside the grace nothing changes: a blocked bot still blocks (P827 line, once a
     minute, unchanged).
   * Past the grace, bots still blocked on a **per-bot** condition are **excluded from the
     new run** and named once (`LogRearmGrace`, `:424`):
     `[finale] re-arm grace (300s past the rotation): excluding 1 still-stuck bot(s) from
     the next finale run (objectives-pending=1 [bot/side0/Fighter/target1052/pending]);
     re-arming with 3 bots`. The finished match keeps its own report (`run.Bots` untouched).
   * The station gate is now per side and from each participant's **own** world
     (`SideObservedEnemyStation`, `:411`; union of observed stations instead of "the first
     bot's window" — same honesty level as the first registration's
     `StationsSeenByAnEnemy`): `no enemy station observed by side(s) [0]`. Every side must
     have seen an enemy station before a normal re-arm; past the grace the deadline wins
     and the relaxation is logged with the same `re-arm grace` line. A non-bomber classic
     P656 rotation still re-arms on the **first** pass (the grace never delays a healthy
     rotation — pinned by a test).

## 4. Local repro — fails on master, passes on the fix

Harness (temporary, deleted; source kept at
`~/allegiance-evidence/P831/P831RotationReproHarness.cs.txt`): an in-process
`Allegiance.Server` + real `BotManager` over loopback, map `m2xspiderw.igc`, civs
**1 Dreghklar / 201 Rixian** (the batch-36 matchup; neither civ has a legal bomber hull at
starting tech, so every requested-Bomber slot takes the P821 fallback), **native fog**
(`ShowMap` off, no `target_sector`), `spawn_scenario` showcase `size_scale 3`
(= 45 slots/side; 61 bots seated), 200 s of match 1, a forced game over
(`DeclareGameOver` — the P439 rotation), then the auto next match for 525 s. Per-bot state
(`ObjectivesPending`, `TargetSector`, sector, position, generation) is sampled twice 20 s
apart, and the finale log is scraped.

| | master (`f3e5148f`) | fix |
|---|---|---|
| bots after the rotation | 61 | 61 |
| `ObjectivesPending` | **61 / 61** | **0 / 61** |
| movement in the 20 s sample | **0 / 61 moved at all** (all still in the hangar, 0 m) | 55 / 61 moved > 200 m; the rest are 2 lifepods (0 m) and 3 in-sector dogfight loiters (86–115 m) |
| P827 line | `[finale] re-arm blocked: 61 participating bots; objectives-pending=61 …` every minute, for the whole 525 s | `objectives-pending` never appears |
| re-arm | **never** (`re-armed=False`) | **yes** at t=556 s: the fresh fog gate held (“no enemy station observed by side(s) [0]”), then `[finale] re-arm grace (300s past the rotation) … arming the next match's finale anyway with 61 bots` → `[finale] re-armed for next mission with 61 bots and fresh station objectives` |

Trimmed logs: `docs/reports/P831_assets/repro-master.txt`, `repro-fix.txt`. Raw dotnet
output: `~/allegiance-evidence/P831/repro-{master,fix}-dotnet-output.txt`. The master run
is the same defect the rig shows, one step earlier and fleet-wide: with no `target_sector`
pinned, *every* bot with a carried-over clash target parks; with a pinned rendezvous only
the duty-Bomber bots are left unable to name their objective (the rig's 48).

## 5. Tests / verification (light gate)

New tests: `tests/Allegiance.ClientCore.Tests/P831FallbackBomberRearmTests.cs`

| Test | What it pins |
|---|---|
| `P831_Fallback_bomber_resolves_its_new_mission_objectives_under_fog` | role Bomber + fallback fighter hull + carried-over sector + fogged world (two home flags, no stations) → `RefreshLife` **returns true**, `ObjectivesPending=false`, `TargetSector` cleared, one `[p831]` line naming the P821 fallback hull and `candidate enemy homes=2`, no repeat on later passes |
| `P831_Bomber_objective_rules_are_unchanged_when_the_world_can_answer` | observed enemy station still wins (cluster 7); own station known → the single remaining home cluster; a named objective still resolves for any role |
| `P831_Rearm_excludes_a_bot_stuck_past_the_grace_and_names_it` | inside the grace the stuck bot still blocks (P827 names `objectives-pending=1`); past it a second run exists with 3 of 4 bots, the exclusion is logged once, and the finished run keeps all 4 |
| `P831_Rearm_grace_releases_a_side_that_never_saw_an_enemy_station` | per-side fog gate blocks with `no enemy station observed by side(s) [1]`; past the grace it re-arms with 4 bots and logs the relaxation |
| `P831_Rearm_still_waits_inside_the_grace` | a healthy rotation re-arms on the first pass (states `next mission` + `armed`), no grace line — the bound never delays the normal path |
| `P831_Rearm_grace_never_excludes_a_bot_blocked_only_just_now` | the grace is **per bot**: a bot whose block is first seen as the run-level grace expires keeps its own clock, is not excluded, and is only excluded (and the fog gate only released) one grace after it was first seen stuck |

| Gate | Result |
|---|---|
| whole-solution build `AllegianceCore.slnx` | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **87/87 tool projects, 0 warnings / 0 errors** |
| AOT publish | not required — no `src/Allegiance.Server` / `src/Allegiance.Core` change |
| new tests **3× alone** | 6/6, 6/6, 6/6 |
| affected tests **3× alone** (`P656ShowcaseFinaleTests`, `BotFixRegressionTests`, `P819FinaleOrderTests`, `P801StrikeMassTests`, `P745SiegeDutyTests`) | 187/187, 187/187, 187/187 |
| ONE `tools/run-tests.sh --tier fast` (final tree) | **PASS** — 10 838 tests, 306.2 s, 0 failed (`TestResults/tiers-20261026T233312.200194Z/summary.json`). An earlier run on the same tree: 10 838 tests, 1 failure — `AsteroidsUpdateLiveTests.Mining_depletion_reflected_in_next_update_while_health_fraction_stays_full`, the ledgered load-flake class (P830 lists it), **3/3 alone** afterwards; `src/` is untouched by this task |
| showcase never-parks (bots changed → 5×, final tree) | **5 / 5 PASS**, longest “speed < 1 with a live target” 0.0–2.01 s (limit 5 s); base rate 5/5 (P830, idle host). Host load at the starts: 1.8, 3.2, 1.9, 1.1, 1.3 |

Machine-readable: `docs/reports/P831_assets/verification.json`. Fast-tier logs:
`~/allegiance-evidence/P831/fast-tier*.txt`.

`GLOSSARY.md` gained a **Finale re-arm / new-mission objectives (P656, P831)** entry
(`tools/check-glossary-links.py` clean).

## 6. Native-intent classification and approach note

**Classification: pure bot-side policy (a harness/AI defect), not a native rule — and it
is a *violation* of native intent, not a port.** Native has no bot fleet and no finale:
nothing in `~/Development/Allegiance/src` re-arms a scenario or derives an AI objective
after a map reseat. The rules this fix must not break are the native ones the code around
it already cites, and they are preserved:

* **Per-side fog** — a station is exported only when the side has seen it
  (`FedSrv/fsmission.cpp:4220`, inside the per-side join export loop from `:4169`), and
  own-side objects are always seen (`FedSrv/FedSrv.CPP:10907-10912`); the start-of-game
  reveal is `bShowHomeSector` → `RevealHomeClusters` (`Igc/mapmakerigc.cpp:470-472`,
  `:969`), the live sweep is `Igc/clusterIGC.cpp:350`. The re-arm still refuses to hand a
  side a target it has not observed (per-side gate) and only relaxes that past an explicit,
  logged grace.
* **Reseat + relaunch** — native reseats a player into a hangar
  (`FedSrv/fsmission.cpp:795`) and a ship only exists in space after a real
  `BUY_LOADOUT(fLaunch)` purchase (`FedSrv/FedSrv.CPP:6189-6190`). Unchanged: the fix only
  lets the bot reach the drive path that already buys and launches.
* **No fabricated knowledge or orders** — the bot flies to a sector id it derived from its
  own client world (`CLUSTERINFO(200)` home flags + its own station table) or from its own
  side's intel, never from an unseen station's coordinates.

**Modern / better approach, if one wants to go further.** The fallback-hull special case
disappears entirely once objective selection is *state-free*: instead of a bot carrying a
"target sector" across lives and missions, keep a per-side objective in the side's intel
store (`SideIntel`) and have each bot read the current objective every pass, deriving its
own route from its own world. That removes the whole class of "stale id after a reset"
bugs, makes the fallback hull irrelevant to duty resolution, and would let the finale
re-arm ask the same store instead of asking 196 bots whether *they* are ready — i.e.
readiness becomes fleet state (`intel.objectiveKnown && every side has flown since the
reset`) rather than a per-bot accumulator that a single stuck bot can hold hostage. The
bounded grace added here is the safety net that keeps today's accumulator honest; it is
deliberately generous (300 s, ~1/6 of the default finale delay) so it can only ever fire
for a genuinely stuck bot.

---

### Remaining / not verifiable from here

* The 200v200 rig runs (batches 34/36 replays, and batch 37+ `[finale] re-arm blocked` /
  `[finale] re-arm grace` lines) are Claude's — no remote host was touched. The expected
  signature on the fix is: no `objectives-pending` in any post-rotation block line, an
  `[p831]` line for any bot whose objective is not yet derivable, and a re-arm (normal or
  grace) inside ~5 minutes of every rotation.
* b36's rotation 1 blocked the re-arm for 82 011 log lines under the **old** code; with the
  fix the remaining candidate causes of any such delay are the (now per-side) fog gate and
  the grace. Whether that particular 1.5 h was the objective deadlock, the single-window
  station gate, or both cannot be separated from a log that predates P827 — the local
  reproduction shows both, and both are now bounded.
