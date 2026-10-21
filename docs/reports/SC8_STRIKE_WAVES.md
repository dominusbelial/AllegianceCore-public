# SC-8 — strike waves that arrive together, no identical-order re-sends

Author: Pi lane (DeepSeek V4.1 Flash), branch `pi/sc8` on master `be1d36fd` + P765 `5dca6452`.
Scope: `tools/allegiance-mcp` and its tests only. Nothing under `src/` changed.

This is bot AI (the research-showcase commander). There is no native AI commander to
match, so every claim below cites the port's own file:line; the only native-sourced
constants reused from SC-7 (`RallyGatherRadius` ≈ the native defence RELEASE horizon,
`shipIGC.cpp:2404`) are unchanged.

## Why (rig batch 12, `be1d36fd`)

Verified in the port's own policy before changing it:

1. **Gotos repeated on a timer.** The old strike loop re-sent an identical order after
   15 s by design (`git show be1d36fd:tools/allegiance-mcp/ResearchTactics.cs:125-126`),
   for gotos too. That is the `…CID 5 … Warp` queued command every ~15.5 s in the
   server log.
2. **Every strike pilot attacked on commit.** Old line `120` chose `CidAttack` for every
   pilot whenever `_gathered` was true, so a bomber that undocked later flew at the
   station alone. The old timeout branch committed `atRally > 0` (one pilot, old line
   `102`) and a third branch committed with nobody at all after `2 * RallyTimeoutSeconds`
   (old line `103`).
3. **The rally was dropped when no strike pilot was in the snapshot.** Old line `71`
   set `_rally = null` when `strikePilots.Length == 0`; the research snapshot drops
   docked ships (`BotManager.ResearchTactics.cs:20-21`), so a reload/relaunch lost the
   rally and the next launches attacked directly.
4. **The non-strike loop used the same 15 s rule** (old line `159-160`) for its
   attack/repair orders.

## What changed (all in `tools/allegiance-mcp/`)

### 1. Wave membership — `ResearchTactics.cs`

- `CommitWave` (`ResearchTactics.cs:240-247`) records exactly the ids of the pilots
  inside the rally radius as `_waveMembers` and emits one commit event.
- The order loop now attacks only `_waveMembers.Contains(pilot.Id) || alreadyOn`
  (`ResearchTactics.cs:184-189`); every other strike pilot gets the rally `CidGoto`
  (`:188-190`), so latecomers wait at the rally for the next wave.
- A wave ends when no member is alive and undocked in the target sector after the
  existing `_committedAt + RallyTimeoutSeconds` trip grace
  (`ResearchTactics.cs:157-165`), when the station disappears (`:127-131`), or when
  there are no strike pilots left (`:116-122`). `EndWave`
  (`ResearchTactics.cs:252-259`) clears the membership, resets the rally clock and
  emits one end event.
- The rally is now kept when `strikePilots.Length == 0` (`ResearchTactics.cs:116-122`)
  so a dock/relaunch re-forms at the same aleph instead of losing the rally.

### 2. Minimum wave size — `ResearchTactics.cs:167-176`

- Before the timeout only a full group commits: `strikePilots.Length >= 3 &&
  atRally >= 3` (`StrikeGroupSize = 3`, `:40`).
- After `RallyTimeoutSeconds` (90 s, `:59`) commit with `>= 2` at the rally, or with
  every strike pilot when the side has fewer than 2:
  `timeoutQuorum = Math.Min(2, strikePilots.Length)` (`:172-175`).
- The old `2 * RallyTimeoutSeconds` "commit with nobody" branch is removed. A wave of
  one is therefore only possible when the side has exactly one launched strike pilot.
  No test could produce a real deadlock without it: the changed goto law keeps
  re-sending a stalled bomber every 60 s, and a pilot that dies or docks leaves the
  snapshot (so the side drops to one and commits at the timeout). Residual risk is
  recorded under "Unfinished / risks".
- After a wave end the rally clock restarts (`EndWave:258`), so a fresh pair waits its
  own 90 s before committing; a full group of 3 still commits immediately.

### 3. No identical re-sends while a pilot is obeying — `ResearchTactics.cs:28-30, 269-289`

`_sent` now stores `Sent(Order, Time, MarkTime, MarkDistance, MarkSector)` and is
pruned exactly like before (`ResearchTactics.cs:108`). `ShouldSend`:

- changed order: send after the 2 s floor (`ChangedOrderFloorSeconds`, `:68`);
- identical order: never before the 2 s floor, and then only when the pilot has made
  no progress for 60 s (`ProgressWindowSeconds`, `:75`);
- goto: progress = arrived within `RallyGatherRadius`, or distance to the goto point
  closed by ≥10% (`GotoProgressFraction`, `:80`) — so a pilot making progress gets
  one goto over 120 simulated seconds;
- attack/repair: progress = pilot is in the target's sector;
- changing sector is itself progress and restarts the 60 s window at the tick it is
  observed, so "no sector change for 60 s" is measured from the last change rather
  than from the last send;
- progress only moves the window baseline; it never sends.

The non-strike attack/repair loop uses the same `ShouldSend`/`RecordSend`
(`ResearchTactics.cs:225-231`). `MaxOrdersPerTick` and `ChooseRally` are unchanged
(`:63`, `:197`, `:205`, `:232`, `:328`).

### 4. One summary log line per commit and per wave end

`StrikeEvent`/`TakeStrikeEvents` (`ResearchTactics.cs:22-25, 97-104`) collect edges;
the research loop prints them where `[commander]` is printed
(`BotManager.Research.cs:122-125`) through `StrikeLogLine`
(`BotManager.Research.cs:154-157`):

```
[strike] side=1 wave=2 commit members=3 atRally=3 target=station#100 sector=2 reason=full
[strike] side=1 wave=2 end reason=no-members-in-target
```

Reasons emitted: `full`, `timeout`, `no-members-in-target`, `station-gone`,
`no-strike-pilots`. One line per edge, never per tick.

### 5. Observation/order law left alone

`BotManager.ResearchTactics.cs` (target observation) and the bot goto/attack execution
law are untouched; only `ResearchTactics` and the print site in `BotManager.Research.cs`
changed.

## Tests

New `tests/Allegiance.ClientCore.Tests/SC8StrikeWaveTests.cs` (13 tests):

| Brief item | Test |
|---|---|
| 6 pilots, 3 at rally: only those 3 attack, the 3 latecomers keep a goto | `A_wave_commits_only_the_pilots_at_the_rally_and_latecomers_keep_rallying` (`:27`) |
| Pilot undocking after commit gets the goto, not the attack | `A_pilot_that_undocks_after_the_commit_gets_the_rally_goto_not_the_attack` (`:60`) |
| Timeout with 1 at rally of 3: no commit | `Timeout_with_one_at_the_rally_refuses_to_commit_a_wave_of_one` (`:79`) |
| Timeout with 2 at rally of 3: commits both | `Timeout_with_two_at_the_rally_commits_both` (`:92`) |
| One-pilot side still attacks after the timeout | `A_side_with_one_strike_pilot_still_attacks_after_the_timeout` (`:112`) |
| Dropped "commit with nobody" branch | `Never_commits_with_nobody_at_the_rally_even_after_two_timeouts` (`:129`) |
| Progressing goto sent once / static goto re-sent after 60 s | `A_progressing_goto_is_sent_once_over_120_seconds_a_stalled_one_is_resent` (`:140`) |
| Wave ends and re-forms from the rally | `A_wave_ends_when_its_members_leave_the_target_sector_and_re_forms_at_the_rally` (`:160`) |
| Wave ends when the station is gone | `A_wave_ends_when_the_target_station_is_gone` (`:198`) |
| Re-formed pair waits its own timeout | `A_pair_that_re_forms_after_a_wave_end_waits_its_own_timeout` (`:215`) |
| Log format | `Strike_wave_log_lines_match_the_rig_format` (`:237`) |
| Non-strike loop: no timer re-send while in the target sector | `Non_strike_attack_orders_are_not_resent_on_a_timer_while_in_the_target_sector` (`:246`) |
| Non-strike loop: re-send when no progress | `Non_strike_attack_orders_are_resent_when_the_pilot_makes_no_progress` (`:259`) |

Two existing tests encoded the old 15 s rule and were changed (as the brief allows):

- `SC7CommanderOrdersTests.Strike_target_prefers_damaged_then_non_garrison_then_garrison_and_is_stable`
  (`:49-66`): used to assert the identical attack order came back at t=16. Now it
  asserts **no** order at t=16 and a re-send at t=61 (60 s with no progress), and the
  comments say why.
- `SC6CommanderOrdersTests.Orders_wait_for_start_throttle_repeats_and_share_bandwidth_across_pilots`
  (`:46-65`): used to assert 2 orders at t=15 and 2 at t=16. Now asserts silence at
  t=15 and t=59, a 2-order re-send at t=60.5 (no progress), the `_sent` prune at t=61
  with no pilots, and 2 fresh orders at t=62.

## Verification (all measured on this machine)

- **Whole-solution build** `dotnet build AllegianceCore.slnx -m:8 -v:minimal`:
  **0 warnings, 0 errors** (final tree).
- **New + affected tests 3× alone** (`SC8StrikeWaveTests` + `SC7CommanderOrdersTests` +
  `SC6CommanderOrdersTests`, 24 tests): **3/3 runs pass** (31–37 ms per run).
- **ONE fast tier** `tools/run-tests.sh --tier fast --no-build` (final tree):
  **PASS, 9352 tests** — Lobby 19, MonoGame 2819 (+3 NotExecuted), ClientCore 1569,
  Core 4930, quarantine 12; 269.5 s, no slow flag.
  Earlier attempts (kept for honesty) failed only on known load-sensitive Live tests:
  `CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`
  (3/3 pass alone; historical ledger `docs/TEST_FLAKES.md:635`),
  `SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent`
  (3/3 alone; ledger `:1313`), and
  `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads`
  (3/3 alone; its class is in the ledger, this exact method is not). None touch
  `ResearchTactics` or the tests changed here.
- **Showcase never-parks 5×** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  final tree): **5/5 PASS**, 1 m 43 s – 1 m 50 s each. SC-7's base rate was
  1 m 50 s – 1 m 53 s, so the base rate is intact (no parks, no slowdown).
- **AOT publish not run:** `src/Allegiance.Server` and `src/Allegiance.Core` did not
  change, so the light gate does not trigger it.

## Before/after order counts

There is no headless/loopback harness that reaches a research strike in this repo
(the SC-7 report reached the same conclusion: the local showcase boots but a strike
needs many minutes of research + asteroid stations). The `never-parks` run above
exercises the showcase but never starts a research commander (`[commander]`/`[strike]`
count = 0 in all five logs). So, as SC-7 did, the showcase is not the measurer here.

Instead, I added a temporary deterministic policy replay (evidence, not committed to
the test suite): the **same** scripted pilot telemetry at 0.5 s steps for 600 simulated
seconds through `ResearchTactics.Tick`, run once against master `be1d36fd`'s policy and
once against this tree. Only the policy differs.

| Scenario (600 s) | Before | After |
|---|---|---|
| 6-bomber strike sortie: attack orders | 180 | **6** |
| 6-bomber strike sortie: goto orders | 2 | **3** |
| 30 fighters + 30 ships, non-strike loop: attack orders | 1202 | **30** |

The strike script: three bombers at the rally, ordered into the target sector at
t=50 and docked at t=300; three latecomers reach the rally by t=30 and enter the
target sector at t=330. The after run commits wave 1 (3 members) and wave 2 (the 3
latecomers) and sends the latecomers exactly one rally goto each; the before run sends
everyone the attack at commit and re-sends it every 15 s. Full method, harness source
and raw lines: `docs/reports/SC8_assets/` (`order-counts.txt`,
`policy-replay-harness.cs`). Raw gate logs/TRX are outside the repo at
`~/allegiance-evidence/SC8/` (build logs, the final green `fast-final2` tier plus
earlier runs, all failed-attempt logs, and the five final never-parks logs).

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| AI commander / strike wave composition | none — native has no AI commander | design | No parity target; policy is a human-play model (rally, commit together, focus one station). All rules are cited to the port above. |
| Rally gather radius ≈ defence release horizon | `shipIGC.cpp:2404` (from SC-7) | design | Unchanged `RallyGatherRadius = 3600` (`ResearchTactics.cs:47`). |
| Goto/attack/repair order ids | — (port wire constants) | design | Unchanged: `CidAttack=1`, `CidGoto=5`, `CidRepair=6` (`src/Allegiance.Core/Wire/OrderChange.cs:70,84,119`). |

## Unfinished / risks

- **Rig measurement still needed:** anti-base hits ≥3 on one station within 30 s, side-1
  station kills ≥1/match, goto orders ≥5/side with no 15 s repeats. Claude deploys and
  measures; this task cannot run 200v200.
- **No `2 * RallyTimeoutSeconds` fallback:** if a second strike pilot stays alive and
  undocked but never reaches the rally, one pilot at the rally will wait forever
  (the old fallback would have committed it, violating "never a wave of 1 while 2
  exist"). No deterministic test produced that state (the goto is re-sent every 60 s,
  and death/dock drops the side to 1 and commits at the timeout). If the rig shows a
  side with zero commits and one pilot parked at the rally, re-introduce a longer
  fallback that still respects the one-of-two rule.
- **Docked bombers count as leaving the wave** by design (they are absent from the
  snapshot, `BotManager.ResearchTactics.cs:20-21`); they re-rally after relaunch.
- **Non-strike progress is sector-only** (per the brief): a fighter in the target's
  sector is never re-sent even if it is not actually engaging. This is what removes
  the 15 s spam and is pinned by the two non-strike tests.
- The before/after harness is deliberately not part of the suite; the permanent SC8
  tests pin the rules directly.

## Merge review (Claude, 2026-10-20)

- **Fixed: a wave member that docks to reload rejoined the attack alone.** Bombers relaunch under the same ship id,
  and `_waveMembers` only cleared on `EndWave`. Members that leave the snapshot (docked or dead) now drop out of the
  wave; if none are left, the wave ends with reason `members-gone`. Tests:
  `A_member_that_docks_and_relaunches_rejoins_the_rally_not_the_station`,
  `A_wave_whose_members_all_leave_the_snapshot_ends_once`.
- **Fixed: the goto progress distance ignored sectors.** In a sector other than the rally's, the distance is now
  "infinite", so only a sector change counts as progress there.
- **Added: a wave ends after `3 × RallyTimeoutSeconds`** even if one member lingers in the target sector, so a
  straggler can't hold the next group at the rally forever. Test for the `no-members-in-target` path added.
- **To watch on the rig:** the "fewer than 2" fallback counts only bombers out of dock, so one launched bomber can
  still commit alone after the timeout while the rest reload. Count `[strike] … commit members=1` lines.
