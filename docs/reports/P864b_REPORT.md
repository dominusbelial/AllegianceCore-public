# P864b — Native team lobby (SIDE_TEAMLOBBY), FlushSides and RandomizeSides in full

Batch: SIM-0554 (FlushSides), SIM-0556 (RandomizeSides), family "FedSrv mission
rules (event-driven)". Native reference re-grepped this session:
`~/Development/Allegiance/src/FedSrv/fsmission.cpp` (5069-5266, 4454-4524),
`FedSrv/FedSrv.CPP` (6964-6997), `FedSrv/fsship.cpp` (1071, 1132),
`Igc/missionigc.cpp` (3612-3644), `Igc/igc.h:514`, `Inc/Messages.h:788-808`,
and the client-side lobby handling (`clintlib/clintlib.cpp`, `WinTrek/teamscreen.cpp`,
`WinTrek/gameoverscreen.cpp`).

**Domingo's ruling (2026-10-28) applied:** every row closes as `implemented`;
"the port chose differently" is NOT a closing reason. The two rows were closed
here as **implemented** (previously `partial`/`design`).

## What changed

### Server — the team-lobby pool and the two flush/rebalance rows (SIM-0554, SIM-0556)
`src/Allegiance.Server/Server.Lobby.cs`:

- **`FlushSides(Mission)`** (new) — the full native flush (fsmission.cpp:5069-5119):
  turns auto-accept OFF on every side that has it on with per-side `AUTO_ACCEPT(52)`
  broadcasts (KGJV "changed to turn on auto accept off", :5080-5092), then moves
  every **non-leader** to `SIDE_TEAMLOBBY` (-2) via `MoveToTeamLobby` with a
  whole-mission `QUIT_SIDE` broadcast (:5097-5117). Leaders stay and anchor their
  side (:5109-5112). The **banned-mask clear** and **`SetLastSide(SIDE_TEAMLOBBY)`**
  (:5105, :5113) collapse to the -2 park itself: this port keeps no separate
  banned-side-mask or `GetLastSide()` state (n/a), and the observable native
  effect — the player is in the lobby, may re-request any side, and is exempt from
  the no-defection gate — is the `Ship.SideId = -2` park.
- **`RebalanceSides(Mission)`** (rewritten) — the full two-phase
  `RandomizeSides` (fsmission.cpp:5139-5260): phase 1 auto-accept-off sweep
  (:5142-5160); **phase 2 flushes every non-leader to the lobby** (:5173-5185)
  — the previously-missing "everyone stripped to a neutral lobby" intermediate;
  phase 3 clears pending join requests (:5183-5196); **phase 4 greedily re-seats
  the highest-rank READY (non-AFK, `GetReady()`) player from the lobby** onto the
  lowest-rank-sum real side (:5199-5261) with the **`iMaxImbalance` fewest-players
  override inside the pick** (`PickRebalanceSide`, PickNewSide :4454-4524,
  :4506-4516). The AFK filter (`GetReady()` at :5235-5241) is implemented on the
  port's ready state (`_playerReady`, the port of native `GetReady()`): an unready
  player is flushed and left in the lobby, exactly native. LockSides stays
  deliberately bypassed (privileged owner-only mass op; P195 decision reaffirmed).
- **`PickRebalanceSide(Mission, Ship)`** (new) — the re-seat pick mirroring
  native `PickNewSide` in the NotStarted context: active + invitation + capacity
  gates (native's no-defect/no-base/rank-balance gates all guard on
  `GetStage()!=NOTSTARTED` and are inert), lowest-rank-sum default with the
  `iMaxImbalance` fewest-players override.
- **`DisableAutoAcceptOnAllSides`**, **`FlushNonLeadersToLobby`** (new) — the
  shared phase-1 sweep and flush half used by both rows.
- `src/Allegiance.Server/Server.Dispatch.cs` — the `FM_C_RANDOMIZE_TEAMS` (21)
  dispatch now **branches on `iMaxImbalance` exactly as native**
  (FedSrv.CPP:6976-6987): `0x7ffe` → `RebalanceSides` (balance), anything else →
  `FlushSides` (flush). Previously the flush branch was documented out of scope.
- `SendSideRoster` — the SIDE_ROSTER(211) snapshot now carries the team-lobby
  pool size (`LobbyPlayerCount`, native clintlib.cpp:994).

### Client — the team screen shows the lobby pool; a lobby player requests a side
- `src/Allegiance.Core/Wire/SideRoster.cs` — added the `LobbyPlayerCount` field
  (header grows 6 → 8 bytes). Kept as a scalar, not a fake -2 `SideRecord`, so the
  many real-side consumers of `Sides` are undisturbed.
- `src/Allegiance.ClientCore.Game/Rendering/SideSelectLayout.cs` — the team
  screen now renders the lobby pool as a muted **"Not on a team (players: N)"**
  row (native teamscreen.cpp:378,445 / clintlib.cpp:994). The layout is the shared
  model both HUD styles (Classic + Modern) draw — the side-select overlay is drawn
  unconditionally from these rows (`Game1.Lifecycle.cs:453`,
  `Game1.Rendering.cs:369`), so the lobby row appears in **both** styles. The lobby
  is never a selectable side (a player picks a real side).
- `src/Allegiance.ClientCore/State/GameOverBackPlanner.cs` + `Game1.Panes.cs` —
  the game-over Back / "ready check" flow now routes a player **on no side**
  (team lobby, `MySideId == -2`) to the side-select picker (`TeamLobbySideSelect`)
  so it can request a real side (native gameoverscreen.cpp:641,669;
  teamscreen.cpp:2931-2970). The server already accepts a lobby→side request
  (`CheckPositionRequestCore` exempts the `SideTeamLobby` park from the
  no-defection gate).

### Bots (`tools/allegiance-mcp`)
- `BotManager.cs` — added a ~1 Hz self-throttled, background-task
  `ReseatFlushedBots()` (wired to the fixed-cadence `RecordPass`): a bot whose own
  ClientWorld `MySideId` is -2 (flushed to the team lobby) re-requests its side via
  the same `SeatWithRetry` path a fresh spawn uses, best-effort (failure only logs),
  non-overlapping, off the decision thread. This keeps the showcase running
  unattended (never-parks) across a flush/randomize.

## Verification

- Whole-solution build **0 warnings / 0 errors** (`dotnet build AllegianceCore.slnx`).
- `tools/build-tools.sh`: **all 91 tool projects built** (0 warnings / 0 errors).
- New/affected tests **3× alone**:
  - `P852MissionRulesCertifyTests.P1..P5` (the new team-lobby cert tests) — 3/3 pass.
  - `SideSelectLayoutTests` (new lobby-row tests) — 3/3 pass (8 total).
- Affected rebalance live tests pass as a set:
  `RandomizeTeamsLiveTests` + `LiveSideSelectTests` (16 tests, all pass).
- `SideRoster` codec round-trip tests (`P820bSquadGameWireTests.SideRoster_old_format`,
  `ShipTrailSideColorTests.SideRoster_codec...`) pass with the widened header.
- ONE fast-tier run: `tools/run-tests.sh --tier fast` — see below.
- `scripts/parity-scorecard.py` **validated** (851 rows; LF-only CSV).

Existing tests updated to match native's newly-restored AFK filter:
`RandomizeTeamsLiveTests` and `LiveSideSelectTests` now mark their bots READY and
pump until the side-ready aggregate confirms the readiness was applied before
triggering `RANDOMIZE_TEAMS`, and set `AutoStart=false` (so the ready-up does not
auto-start the mission out of NotStarted before the owner's rebalance). This
matches native exactly: RandomizeSides re-seats only non-AFK players.

## Parity scorecard before → after (family "FedSrv mission rules (event-driven)")

| Metric | Before | After |
|---|---|---|
| Weighted implemented | 525/603 (87.06%) | **530/603 (87.89%)** |
| Rows implemented / eligible | 222/256 | **224/256** |

(The +5 weighted and +2 rows are exactly SIM-0554 weight 2 + SIM-0556 weight 3.)

## Per-row table

| Row | Native cite | Before → After | Tests |
|---|---|---|---|
| **SIM-0554** FlushSides | FedSrv/fsmission.cpp:5069-5119 (RANDOMIZE_TEAMS flush branch FedSrv.CPP:6981-6987) | `partial` (only the auto-accept-off sweep existed; flush half documented "not built") → **`implemented`**: full `FlushSides` (sweep + non-leaders→`SIDE_TEAMLOBBY` with `QUIT_SIDE`; banned-mask clear + `SetLastSide(lobby)` ≡ the -2 park), wired to the dispatch's non-rank-mode branch | `P1_FlushSides_moves_non_leaders_to_lobby_leaders_stay`, `P4_Randomize_teams_dispatch_flushes_when_not_rank_mode`, `F1_RebalanceSides_turns_autoaccept_off_whole_mission` |
| **SIM-0556** RandomizeSides | FedSrv/fsmission.cpp:5139-5260 (PickNewSide :4454-4524) | `partial` (one-pass direct rebalance; no lobby flush, no `iMaxImbalance` override in the pick, no AFK filter) → **`implemented`**: two-phase flush-then-reassign, `iMaxImbalance` fewest-players override inside `PickRebalanceSide`, AFK (`GetReady`) filter; `SetLastSide`/banned-mask ≡ the -2 park | `P2_Randomize_leaves_unready_afk_player_in_lobby`, `P3_Randomize_reseats_ready_non_leader`, `P5_Rebalance_pick_applies_iMaxImbalance_override`, `RandomizeTeamsLiveTests` (updated) |

## Native-intent classification

- **SIM-0554** — intent: a privileged owner-only, NotStarted-only "dump everyone
  to a neutral lobby" (no reassignment). Achieved exactly; the two player-state
  effects that have no separate port state (banned mask, last side) are realised
  by the `SIDE_TEAMLOBBY` park, whose observable behaviour matches native.
- **SIM-0556** — intent: a privileged owner-only two-phase balance that strips
  every non-leader to the lobby, ignores AFK players, then greedily re-seats the
  ready pool to minimise per-side rank imbalance with the `iMaxImbalance` override.
  Achieved exactly.

Both rows are genuinely **implemented** (not n/a / not design). Nothing is marked
n/a that physically can exist in this port: the only n/a-ish note is that the
banned-side-mask and `GetLastSide()` are state the port has never modelled, and
their single native observable effect (lobby park + defection exemption) is fully
reproduced by the `-2` park.

## Modern / better-approach note

- The port keeps `SIDE_TEAMLOBBY` as a nullable `Ship.SideId` value (-2) rather
  than native's lazy first-class `IsideIGC` lobby object (missionigc.cpp:3612) —
  the port already had a robust lobby-park model, so building a separate lobby
  `Side` object would have duplicated state for no behavioural gain. The pool is
  a genuine, multi-player, mission-scoped state (any ship can sit on -2), which is
  what matters.
- `RandomizeSides` re-seats via the existing `TryJoinSide` (the port of
  `AddPlayerToSide`), so the reassignment broadcasts ride the real
  `JOIN_SIDE`/`SET_TEAM_LEADER`/`SET_MISSION_OWNER` announcements — no new
  broadcast shape is invented.
- The AFK filter reuses the port's existing `_playerReady` (P180), so no new AFK
  flag was needed; it is exactly native's `GetReady()`.
- Client: instead of widening the huge `MISSIONDEF`, the lobby pool rides the
  port-local `SIDE_ROSTER(211)` as a scalar, and the shared `SideSelectLayout`
  model means one change covers both HUD styles.

## Unfinished / deferred

- **Pixel screenshots (both HUD styles)** — required by the brief in
  `docs/reports/P864b_assets/`. This session is a headless CLI: I could not
  exercise the graphical MonoGame client to capture real frames. The layout logic
  is unit-tested (both styles share the same rows) and the exact on-screen text is
  verified (`SideSelectLayoutTests`), but genuine pixel captures of the side-select
  lobby row and a lobby→side request are left to Claude's review/test runner
  (which also runs the Live-tier showcase). Evidence path for raw artefacts:
  `~/allegiance-evidence/P864b/`.
- **Showcase never-parks 5×** (`P730ShowcaseLiveTests.Two_local_showcase_matches_...`)
  — a Live-tier, load-sensitive test that Claude runs per merged batch (per the
  shared-host and per-batch Live rules). The bot re-seat mechanism is implemented
  and compiled; its 5× run is left to that runner. `docs/TEST_FLAKES.md`: no flakes
  encountered in the runs executed here.
- Fast-tier full suite: **PASS** — `tools/run-tests.sh --tier fast`: 11323 tests, 0 failures
  (Allegiance.Core.Tests 5606 ✓, ClientCore.Tests 1931 ✓, ClientCore.MonoGame.Tests 3656 ✓
  + 3 NotExecuted, Lobby 87 ✓, Update 28 ✓; quarantines pass). Two failures in earlier
  runs were both regressions I had introduced and fixed: (1) the SIDE_ROSTER header
  growth (6→8) initially broke decode of pre-P864b battle-replay captures — fixed with a
  backward-compatible length-sniffing decoder (P729/P730 replay tests pass 3×, plus a new
  `SideRoster_decodes_old_6byte_header_with_zero_lobby` test); (2) a buggy header in that
  new test itself (wrong `cbmsg`) — fixed. Both now pass 3× alone and the final fast tier
  is clean.
