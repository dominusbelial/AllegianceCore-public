# P844b — finish the P844 victory/results rows: allies override in the conquest
# recompute, allied win with no populated side, accumulated time played

Base: **public master at dispatch** (a85dad5b). This is the work left in the P844
certificate's "Unfinished / handoffs" (docs/reports/P844_CERTIFY.md): the SIM-0531
allies override for the conquest recompute (blocked then by the P861 freeze,
now merged), the SIM-0533 allied-win-with-no-populated-side draw edge, and the
SIM-0526/0527 time-played accumulation across rejoins. Native bodies re-grepped
at dispatch. Rows touched in `docs/parity/sim-rules-matrix.csv` (only my rows'
lines): SIM-0526, SIM-0527, SIM-0531, SIM-0533.

## Family score

| | before | after |
| --- | --- | --- |
| SIM-0531 | `partial` (w5) | **implemented** (w5) |
| SIM-0533 | implemented (w3) | implemented + zero-populated edge closed (w3) |
| SIM-0526 | implemented (w3) | implemented + time-accumulation fix (w3) |
| SIM-0527 | implemented (w2) | implemented + departure accumulation (w2) |

`python3 scripts/parity-scorecard.py` validates (851 rows, no row errors).

## 1. SIM-0531 — allies override in `CheckForVictoryByStationKill` (partial → implemented)

| native branch | port site | verdict |
| --- | --- | --- |
| allies-override tail (fsmission.cpp:3719-3743, Imago ALLY 7/17/09): when every ACTIVE side is one alliance group → `psideWon` = the alliance | **fixed (missing)**: in `Mission.RecomputeConquestAndTerritory`, after the whole conquest/territory recompute (native end-of-body placement :3742), apply `Mission.AllianceOverrideWinner` when gated | implemented + test |
| `bAllies` gate (:3573/3615) — set only when the conquest branch finds a side with `GetAllies() != NA`; skipped for territory-only and lone-un-allied sides | gate = `ConquestEnabled && Sides.Any(s => s.AllyGroup is not null)` — reproduces native: territory-only games never trigger; a lone un-allied active side keeps the AET one-active-team suppression intact (fsmission.cpp:3628-3630) | equal |
| the pure double loop (`IsideIGC::AlliedSides`, Igc/igc.h:4297-4305: same side or equal non-NA alliance ids) | **moved into Core**: `Mission.AllianceOverrideWinner` / `Mission.AlliedSides` (faithful double loop, includes a lone active side, skips inactive). The Server's win-by-inactive-sides family now delegates to it (`Server.AllAlliedWinner` → `Mission.AllianceOverrideWinner`) — Core no longer depends on Server | equal |
| reason: native ally/team name list ("… won by dominating their opponents", FedSrv.CPP:12356-12374) | flows through the existing `Mission.StationVictoryReason` (already names every ally, Mission.StationVictory.cs:31-33) | equal |

Test: `P844MissionRulesCertifyTests.Conquest_recompute_allied_sides_win_by_override`
— allied sides + conquest recompute (threshold 100, so the tally is provably
NULL via the un-allied control) → the alliance wins by the override; **proven to
fail before the fix** (Assert.NotNull(winner): null without the override).

## 2. SIM-0533 — allied win when NO side has players (draw edge)

| native branch | port site | verdict |
| --- | --- | --- |
| the override replaces the tally result even when the tally is NULL (fsmission.cpp:3820-3856): all active sides one alliance → the alliance wins instead of a draw; a single active side → that side | **fixed (missing)**: `CheckForVictoryByInactiveSides` and `CheckForVictoryByInactiveSidesAfterDeactivation` now apply `AllAlliedWinner` in the zero-populated branch instead of always declaring a draw | fixed + test |
| KGJV #62 AET Starting gate + STANDALONE/CanCheat exemptions | unchanged (certified P844) | equal |

**P388 interaction (explained).** The peer-departure path
(`CheckForVictoryByInactiveSides`) has a debug-mode early return *before* the
tally/override (Server.Lobby.cs:88-99): when `LiveSettings.DebugMode` is on, the
0-player unattended arena is the designed steady state, so a client leaving must
never end the mission — the new zero-populated override is therefore unreachable
in debug and P388's empty-lobby rule is untouched. The after-deactivation variant
(`CheckForVictoryByInactiveSidesAfterDeactivation`) intentionally has no P388
skip (station elimination is a genuine combat outcome, so it ends even a debug
mission); there `alive` is `Active`-alone in debug, so a zero-alive state means no
active sides at all and the override stays null — a real all-eliminated draw
survives. Production matches native exactly.

Tests: `Allied_zero_populated_sides_win_instead_of_draw` (proven to fail before:
the port drew, winner -1), `Unallied_zero_populated_sides_draw` (control: no
alliance → draw, "All sides were destroyed"), and the pure-helper truth table
`AllianceOverrideWinner_single_active_side_or_one_group_wins` (0 active → null,
lone active side → that side, two un-allied → null, one group → a member).

## 3. SIM-0526/0527 — time played accumulates across rejoins

| native branch | port site | verdict |
| --- | --- | --- |
| `PlayerScoreObject` accumulates time over Connect/Disconnect pairs (`m_dtPlayed += (t - m_timeConnect)`, Igc/igc.h:5602-5613); a leaver+rejoiner+leaver has the SUM | **fixed (different)**: the three stamping sites no longer overwrite with `_simNow - ConnectSimTime` | fixed + test |
| — | **site 1** `Server.Lifecycle.cs ApplyDeparturePaydayAndOldPlayer` (~2185): the departed player's cached `TimePlayed` = accumulated baseline + the closing stint | fixed |
| — | **site 2** `Server.Lobby.cs FinalizeMissionScores` (~1262): folds the final open stint onto the restored baseline, then marks the mission `_scoresFinalized` — folded exactly once, never double-counted | fixed |
| — | **site 3** `Server.Lobby.cs BuildGameOverPlayerEntries` (~1436): the roster uses the folded `TimePlayed` when finalized (whole-mission game-over); for a side-only game-over (not finalized) it folds the still-open current stint onto the restored baseline — guarded by `_scoresFinalized` | fixed |

The lobby career report reads the same `score.TimePlayed` verbatim into
`TimePlayedSeconds`/`Minutes` (Server.Lobby.cs:1324/1335), so the accumulated
value carries into the career report; that contract round-trip is already pinned
by `Results_report_carries_game_team_and_player_facts_over_the_wire`.

Test: `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster`
— Ace joins, leaves (~3 s), rejoins (old score restored), plays ~2 s, then Bee
leaves → Ace's side wins by elimination while Ace is online; the GAME_OVER roster
carries stint1 + stint2 (~5 s), **proven to fail before the fix** (2.05 s — the
last stint alone, outside the [4,8] assertion).

## Verification (light gate)

- `~/.dotnet/dotnet build AllegianceCore.slnx` — **0 warnings / 0 errors**.
- `tools/build-tools.sh` — all 90 tool projects, 0/0.
- Rig AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`) — clean, **0 IL2026 / 0 IL3050**.
- New tests 3× alone, each **proven to fail before its fix** (temporarily reverted
  the fix, confirmed the test fails, re-applied):
  - `Conquest_recompute_allied_sides_win_by_override` (fails: no override → null).
  - `Allied_zero_populated_sides_win_instead_of_draw` (fails: draws, winner -1).
  - `Rejoined_player_accumulates_time_across_stints_in_roster` (fails: 2.05 s
    last-stint only).
  - `AllianceOverrideWinner_single_active_side_or_one_group_wins`,
    `Unallied_zero_populated_sides_draw` — helper/control pins.
- Re-ran 3× each: `P844MissionRulesCertifyTests` (12/12 ×3),
  `GameOverPlayersWireTests` (6/6 ×3), `P841MissionRulesCertifyTests` (6/6 ×3),
  `P852MissionRulesCertifyTests` (9/9 ×3), `P855SquadGameLoopbackTests` (1/1 ×3).
- ONE `tools/run-tests.sh --tier fast` — 11,213 tests, 319.6 s; **one failure**:
  `AsteroidsUpdateLiveTests.Mining_depletion_reflected_in_next_update_while_health_fraction_stays_full`
  ("ore must decline with real mining; first 534 -> last 535"). It is a live
  mining ore-depletion timing test with **no overlap with this batch** (which
  touches only victory/game-over/score-time paths), passes **3/3 alone**, and
  has been appended to `docs/TEST_FLAKES.md` as a load flake (P844b fast-tier
  gate, 2026-10-27).
- Showcase never-parks (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`)
  **5/5** (3m24s–4m31s each) — no parking; mining/research/kills/relaunch all
  observed. No regression in the shared sim path the batch touches.
- `python3 scripts/parity-scorecard.py` — validates; CSV kept LF (0 CR).

Raw receipts (fast-tier summary + TRX tree, showcase 5×, gate battery, AOT
publish log) are archived at `~/allegiance-evidence/P844B/` (outside the repo).

## Native-intent classification

- **design** (kept): the conquest allies-override reason reuses the existing
  `StationVictoryReason` (native ally "dominating their opponents" text) instead
  of duplicating a reason; the `bAllies` gate is reproduced as
  `ConquestEnabled && any side has an alliance` so territory-only games (where
  native never sets `bAllies`) never trigger; the single shared
  `Mission.AllianceOverrideWinner` replaces native's two copy-pasted loops.
- **era constraint** (n/a): none newly introduced; the SQL/AGC/Steam branches of
  this batch remain as certified in P844.
- **native bugs**: the fsmission.cpp:5397 allied-reason loop uses the eliminated
  side's `pside` instead of `psideWin` — pre-existing P852 port note, kept
  unchanged. Also noted: native computes `bAllies` only inside the conquest
  branch, so a territory-only game with allied sides does not get the station-kill
  override — the port reproduces that via the `ConquestEnabled` half of the gate.

## Modern/better-approach note

The pure alliance-override logic now lives once in Core
(`Mission.AllianceOverrideWinner`, a faithful port of the double loop) and is
shared by both the station-kill recompute and the win-by-inactive-sides family —
one helper instead of native's two copy-pasted loops (one wrapped in `bAllies`).
The time-played accumulation collapses native's scattered Connect/Disconnect
bookkeeping into three consistent fold sites with a single `_scoresFinalized`
guard, so a rejoined player's roster and career rows show the true sum across
stints — matching native's accumulator without a per-connection timer. A side-only
roster shows the accumulated baseline plus the current open stint (native shows
only the prior baseline for a still-connected player) — a small, deliberate
improvement the brief requested ("accumulate the restored score's time plus the
current stint"); the whole-mission path is identical to native.

## Unfinished / handoffs

- SIM-0528 exp-mult damping wiring: still blocked on a persisted career score on
  the server side (or an explicit design decision to weight by session score) —
  P841's call, unchanged by this batch.
- The lobby career store still does not persist the `Game`/`Teams`/extended player
  columns (the contract carries them; the ingest ignores extras until its schema
  grows) — unchanged.
