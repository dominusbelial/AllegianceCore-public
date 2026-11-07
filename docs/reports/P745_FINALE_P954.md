# P745 finale siege — P954 finale report

Task: `P954_BRIEF.md` — fix the three failure modes of
`P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station`
(`tests/Allegiance.ClientCore.Tests/P745FinaleSiegeLiveTests.cs`) and rule on the
game-over message parity question. Worktree `allegiance-pi-p954`, branch `pi/p954`
(base `f3c2726c`, Claude's master at dispatch). Nothing committed or pushed.
Native line numbers re-grepped 2026-11-07 against `~/Development/Allegiance/src/`.

## 1. Parity ruling: the station-kill tally on the GAME_OVER wire

**Question.** The test expected the bare native deadline reason but the actual
wire message carried two extra lines (`Station kills: …` / `Enemy base shattered:
…`) from `Mission.StationKillSummary()` (`src/Allegiance.Core/Model/Mission.StationKills.cs:31-37`,
appended in `src/Allegiance.Server/Server.Lobby.cs:1676` for showcase presets).

**Ruling: deliberate, approved port addition — adjust the test, keep the server
behaviour.** Evidence:

- Native `CFSMission::QueueGameoverMessage` (FedSrv/fsmission.cpp:3059) puts
  exactly one string on the wire: `m_pszReason` verbatim, or "Your side has been
  eliminated." when null (fsmission.cpp:3064, the `FM_VAR_PARM` blob). The same
  builder serves the whole-mission queue (fsmission.cpp:3437) and the side-only
  queue (fsmission.cpp:5425). A timed match's reason is ranked from
  kills/deaths at fsmission.cpp:2468-2498 — "The clock has stopped. The winner:
  %s by %s" (:2497) or "The clock has stopped. The game is a draw." (:2502).
  Native therefore never appends a station tally to the message. For **ordinary
  missions the port is byte-parity** with this — pinned by
  `P914StationDeadlineTests` (`tests/Allegiance.Core.Tests/P914StationDeadlineTests.cs:135-152`,
  `Assert.Equal(reason, result.GameoverMessage)` for the non-preset rows) and now
  also by the new `P954GameOverWireTests`.
- The tally is a **P914-approved preset-only addition**, documented in
  `docs/reports/P914_REPORT.md` ("Feed and score overlay"): for presets the
  summary lines are also sent as CHATMESSAGE notices, published on the Notable
  spectator feed, logged with `[finale]`, appended to the GAME_OVER reason, and
  carried in the native `SideEndgameInfo.CBaseKills` roster field (native already
  sends that field, fsmission.cpp:3073). The first reason line and the winner ID
  stay authoritative and native-shaped; `GameOverPanelInfo` renders the extra
  lines for both HUD styles with reviewed screenshots (P914 assets). The ledger
  itself is scoped to "authored showcase/skirmish policy, never a player-created
  or LAN mission" (`Mission.StationKills.cs:4`).

Consequently the P745 assertion now requires the native reason to lead verbatim
and pins the exact appended shape (3 lines, the documented prefixes) instead of
bare equality. The tally stays in server logs/telemetry as it already is
(`Server.Lobby.cs:564`); nothing in the message pipeline changed.

## 2. Observer race fix

`manager.FinaleStatus()[0]` threw `IndexOutOfRangeException` when the mission
retired and rebuilt between GAME_OVER and the observer sample, emptying the
finale-run list (the race the test's own comment at `:127-129` describes). The
observer now only indexes a non-empty status array and falls back through the
probe's wire text, which remains the primary evidence; the status read also uses
`TryGetProperty` so a missing key cannot throw. The assertion strength is
unchanged.

## 3. Station-kill rate over the runs — and what actually caused the miss

The brief's item 3 turned out to be **two separate defects**, one in the test
and one in the bot policy, plus a load-sensitive discovery stall:

**(a) Test defect (fixed): the destruction check could only ever see the two
home garrisons die.** The test captured
`mission.Sectors.SelectMany(s => s.StationRuntimes).ToArray()` once at spawn.
A dead station is REMOVED from its sector (`Sector.RemoveStation`,
`src/Allegiance.Core/Model/Sector.cs:405`, the P125 death contract called from
the combat pass at `Sector.cs:2662`), and stations that constructors build
during the match are added to the sector's runtime list after the capture — so
a strike that killed a mid-match-built target was invisible to the check in
both states. Run `p954r3` proves it: the server's game-over log reported
`[finale] Station kills: Alpha=1, Bravo=1` / `Enemy base shattered: Alpha (1),
Bravo (1); uncredited=0` — two real, credited station destructions — while the
test's snapshot showed both garrisons at full health and asserted "No station
destruction". The fix judges destruction from a live per-sample sweep (a
runtime whose armor crossed zero, or a previously seen runtime id vanishing
between samples) ORed with the mission's own sim ledger
(`Mission.StationKills`/`UncreditedStationDestructions`) — the exact evidence
the wire summary reports. This is strictly more complete, not looser;
`Assert.True(destroyed, …)` is untouched.

**(b) Discovery starvation (fixed with a bounded arm grace): the match's only
deadline could be held hostage forever by fog.** In runs `p954r1` and `p954r2`
the finale never armed: `ConfigureFinale` parks a two-side match in
`_pendingFinales` until EVERY side has observed an enemy station, and the
bounded server deadline (`ArmResearchFinale`, `src/Allegiance.Server/Server.Showcase.cs:31`)
is requested only at the maturity trigger — so with a side still blind, no
deadline ever armed, the mission never left `MissionStage.Started`, and the
test timed out at 1200 s with no game over. Run r1's log shows the mechanism:
side 0 discovered the enemy garrison by t≈121 s, but side 1's recon scouts got
pinned in the clash sector (Claytow, sector 1 — the P837 meeting-engagement
handoff yields the tick to the combat drive whenever an enemy is visible) and
never found the aleph mouth 1→0 (`sweep=1:0(407s)` with the waypoint index
stuck at 0; `[opening] t=1206s side 1 … lanes=0, enemyStationSeen=False`).
The multi-side case already refuses to let fog starve the deadline
(`BotManager.Finale.ConfigureFinale` registers targetless immediately when
`HasMultipleEnemies`). P954 extends the same principle to two-side matches
**after a bounded grace**: `ScoutOpening.PendingArmGraceSeconds`
(default 480 s, env `ALLEGIANCE_P954_FINALE_ARM_GRACE`, matching the existing
480 s recon budget) after which `RegisterPendingFinales` registers the run
targetless — a still-blind side arms with no target (the existing P896 legal
state, resolved the moment its own worlds report a station) while the seeing
side strikes and the maturity trigger arms the server deadline. The showcase
must always have a deadline — the same rule `LogRearmGrace` already states for
re-arm. Run `p954f1` exercised exactly this path and passed: registered at
480 s, side 1 blind-with-no-target, deadline armed, `Station kills: Alpha=2`,
first kill at wall 310 s.

### Measured rate

| Run (solo unless noted) | Arm | Station kills on the wire summary | Result |
| --- | --- | --- | --- |
| `p954r1` (pre-fix) | never (side 1 blind 1200 s) | none — no game over | FAIL (old assertions) |
| `p954r2` (pre-fix, concurrent with r3) | never | none — no game over | FAIL |
| `p954r3` (pre-fix, concurrent) | natural, t=121 s | **Alpha=1, Bravo=1** — real kills invisible to the stale snapshot | FAIL ("No station destruction") |
| `p954f1` (post-fix) | P954 grace at 480 s | Alpha=2 | pass 16 m 59 s |
| `p954f2` (post-fix) | natural | Alpha=2, Bravo=3 | pass 12 m 42 s |
| `p954f3` (post-fix) | natural | Alpha=2 | pass 12 m 18 s |

Post-fix: **3/3 passes, 3/3 runs destroy at least one station**; the two
pre-fix never-armed stalls become a guaranteed arm (natural within the opening,
or at the 480 s grace at the latest). The pre-fix starvation rate on this
shared host was 2/3 — consistent with the brief's "fails every nightly since
10-22" once the assertion-only failures (items 1–2) are accounted for; the
nightly mode-1 failures prove arming usually succeeds on the quieter mini
runner. Raw scenario logs: `~/allegiance-evidence/P954/` (linked, outside the
repo) and `.scratch/p954/p74*f*.log`.

## 4. Wire-level pin (new test)

`tests/Allegiance.Core.Tests/P954GameOverWireTests.cs` (2 cases) drives the
production `EncodeGameOverResult` through the real `GameOver` codec:

- ordinary mission: the GAME_OVER text is the native reason alone — no tally
  (fsmission.cpp:3064);
- showcase preset: line 0 is the native reason verbatim (:2502 draw text used),
  the remaining lines equal `Mission.StationKillSummary()` exactly, and the
  roster's `CBaseKills` equals the mission ledger (fsmission.cpp:3073).

## Verification

- `dotnet build AllegianceCore.slnx`: **0 warnings / 0 errors** (three runs
  during the task, final after all edits).
- `tools/build-tools.sh`: **all 120 tool projects built, 0 errors** (includes
  `tools/allegiance-mcp`, whose finale code changed).
- New/affected tests 3× alone:
  - `P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station` (Live):
    **3/3 pass** (`p954f1/f2/f3` above), 0 skipped.
  - `P954GameOverWireTests` (2 cases): **3/3 runs, 6/6 pass**, 0 skipped.
  - `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
    (showcase never-parks, 5× as the brief requires): **5/5 pass**
    (3 m 47 s / 5 m 50 s / 4 m 15 s / 5 m 02 s / 3 m 17 s) — no park in any run,
    matching the base rate; 0 skipped.
  - `P832eConquestBotLiveTests` + `P832fKillBotLiveTests` (assert an empty
    `FinaleStatus()` against the changed BotManager): **2/2 pass**.
- One full fast tier (`tools/run-tests.sh --tier fast`, run
  `tiers-20261107T223455.074578Z`): **13,473 tests — 13,467 passed / 3 failed /
  3 skipped (NotExecuted, the known private Meshy-pack opt-outs in MonoGame;
  all other projects 0 skipped)**. Infrastructure checks 9/9 PASS. The three
  failures are non-task, ledgered in `docs/TEST_FLAKES.md` ("P954 gate" section):
  `NpcWingmanDefendLiveTests.Defender_disengages…` (already-open real
  regression, fails 3/3 alone on master too),
  `MissileTrackWarningLiveTests.Warning_routes_by_the_source_ship…(gunner: True)`
  (25.9 s timing offender, passes 4/4 alone) and
  `MinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell…` (21.7 s timing
  offender, passes 3/3 alone).
- No `src/` change, so the server AOT publish gate does not trigger.

## Out of scope / not done

- The deeper bot-policy question left open by (b): recon scouts pinned by the
  P837 meeting-engagement handoff stop sweeping the clash sector for the aleph
  mouth. The arm grace makes the deadline immune to it, but a blind side still
  cannot strike until one of its own ships sees a station (P896 honesty). A
  recon-vs-combat priority redesign is finale/exploration strategy territory
  and stays out of scope per the brief.
- `NpcWingmanDefendLiveTests` regression: already open, tracked since 10-29
  (`docs/TEST_FLAKES.md`), unrelated to this task's files.

---

**Native intent** — `CFSMission::QueueGameoverMessage` (fsmission.cpp:3059,
called at :3437 and :5425) sends the ranked reason string alone over the wire
(:3064) with the per-side roster (kills/deaths/base-kills at :3066-3081); the
timed-match ranking at :2468-2498 produces only "The clock has stopped. …" text
(:2497, :2502). No station tally was ever part of the message; the port's tally
is a documented preset-only overlay that leaves the native text leading verbatim.

**Modern approach** — keep the approved P914 preset overlay on the wire and pin
it with wire-level tests for both ordinary (byte-parity) and preset missions;
make the siege test's observer race-proof and its destruction verdict complete
(live runtime sweep + the sim ledger the wire summary itself reports); bound the
fog-driven finale arm wait so a showcase match can never lose its deadline; and
report the measured station-kill rate with evidence rather than loosening
`Assert.True(destroyed, …)`.
