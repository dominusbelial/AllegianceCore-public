# COUNTDOWN_START_PING — P188

Stage: P188
Branch: `master` (prior top commit `b8e78ce P187: ballot/voting system`)
Date: 2026-09-12

Ports three small federated messages into `Allegiance.Core`:

  1. `SET_START_TIME`(14) + a real `STAGE_STARTING` start-countdown stage,
  2. `START_GAME`(157) — the mission owner's manual start trigger,
  3. `PING`(35) — a client keepalive/latency-probe echo.

## Summary of changes

New wire codecs (`src/Allegiance.Core/Wire/`):

  - `SetStartTime.cs` — fmid 14, `{ uint32 timeStart }`, FixedSize 8
    (`Messages.h:153-155`).
  - `StartGame.cs` — fmid 157, zero-field header-only marker, FixedSize 4
    (`Messages.h:868-869`). Same shape as the existing `EndSpoofing`/`Suicide`
    marker precedent.
  - `Ping.cs` — fmid 35, `{ uint32 timeClient; uint32 timeServer; int32 fmg }`,
    FixedSize 16 (`Messages.h:280-284`).

Server (`src/Allegiance.Server/Server.cs`):

  - `StartCountdown(Mission, float)` — the port of native `StartCountdown`'s
    wire + stage effect (`fsmission.cpp:1926-1938`): from `NotStarted`, set
    `Stage = Starting`, record the double-seconds target `now + countdownSeconds`
    in a per-mission deadline map (`_startCountdownDeadline`), and broadcast
    `SET_START_TIME` whole-mission. Native's `GenerateMission`/briefing-text
    side-effects are OUT OF SCOPE (bucket/mission generation happens once at
    `Mission.Assemble`; briefing text is a separate feature).
  - `AdvanceStartCountdowns(double)` — the per-tick countdown driver, called in
    `SimPass` right after the mission tick. Any mission in `Starting` whose
    deadline has passed crosses to `Started` via `CompleteStartCountdown`, which
    reuses the existing P180 `BroadcastEnterGameToMission` (the exact same
    ENTER_GAME path — not a second one).
  - `IsAllOccupiedSidesReady(Mission)` — the port's own already-simplified
    readiness check, factored out of P180's `TryAutoStart` and now shared by
    both start paths (a deliberate stand-in for native's `FAllReady`, whose
    team-balance/rank logic needs `nMaxPlayersPerTeam`/a rank system this port
    lacks).
  - `START_GAME` dispatch (port of `FedSrv.CPP:7597-7621`): refused unless the
    sender is the mission owner (`IsMissionOwnerPlayer`, P183), the mission is
    `NotStarted`, and every occupied side is ready; on success calls
    `StartCountdown(BriefingCountdownSeconds)`. Native's bot-authentication
    special-case (bots may start regardless of ownership / with zero countdown)
    is OUT OF SCOPE — no such concept in this port.
  - `PING` dispatch at the VERY TOP of `ProcessDatagram`, before any
    ship/mission lookup: decode, stamp `timeServer = _simNow*1000`, re-encode,
    `SendPeer(from, ...)` — echo to the SAME connection only, no broadcast, no
    gating (faithful to `FedSrv.CPP:4897-4911`).

Bot (`src/Allegiance.Bot/Bot.cs`): `SendStartGame()`, `SendPing(timeClient,
fmg)`, and `SetStartTimes`/`Pings` receive accumulators (with
`ResetStartSignals`).

## Did auto-start get upgraded to the real countdown? YES — both paths.

Decision: the ready-up auto-start (P180) was UPGRADED to route through the real
`Starting` countdown rather than remaining instant. `TryAutoStart` now calls
`StartCountdown(mission, BriefingCountdownSeconds)` instead of jumping straight
to `Started`.

Why this rather than keeping auto-start instant:
  - Item 1's stated purpose is "a visible countdown between everyone ready and
    the mission actually starting" — i.e. for the auto-start path. Keeping
    auto-start instant would make the countdown nearly meaningless, since the
    ready-up auto-start races ahead of the manual trigger.
  - Faithfulness to native: native's own start ALWAYS passes through
    `STAGE_STARTING` — `StartGame` does `StartCountdown(0.0f)`
    (`fsmission.cpp:2231-2239`), and the briefed start uses a non-zero
    countdown. Routing both paths through `Starting` matches that.
  - The five P180 tests continued to pass unchanged (verified): they simply
    pump through the ~300-tick (15 s @ 20 upd) countdown within their 800-tick
    caps. `Starting` is now a genuinely-used stage.

The P188 writeup records this as the upgrade of P180's documented "Stage 1
simplification" (instant auto-start) to the real countdown mechanic.

## The countdown length — real native constant found

`c_fMissionBriefingCountdown = 15.0f // seconds` at `src/Igc/igc.h:1005`.
Used verbatim (`BriefingCountdownSeconds = 15.0f`) for BOTH the manual
`START_GAME` trigger (the faithful `AdminGame.cpp:286` usage) and the
auto-start. NO default had to be guessed — the constant is in the live native
tree.

## Test coverage (added to `tests/Allegiance.Core.Tests/LiveCountdownStartPingTests.cs`)

  1. Codec round-trips pinning exact wire bytes for all three messages.
  2. `StartGame_owner_all_ready_goes_through_real_countdown` — the
     MUST-FAIL-IF-REMOVED proof that the countdown actually delays the
     transition: START_GAME moves NotStarted → Starting with a whole-mission
     SET_START_TIME, but is NOT yet Started and sends NO ENTER_GAME; only once
     the countdown elapses on a later tick does it reach Started with the
     ENTER_GAME broadcast to both peers. Verified: temporarily bypassing the
     countdown (jump straight to Started) turns this test red.
  3. Refusal matrix: non-owner; not-all-ready; already Starting; already Started
     — each refused with no SET_START_TIME broadcast.
  4. `Ping_echoes_to_sender_only_with_server_timestamp` — echo to the sender
     only (the second peer sees nothing), with a real server TimeServer stamped
     and the client's TimeClient/fmg preserved, in a NotStarted mission.
  5. Multi-mission scoping of the SET_START_TIME / countdown-completion
     ENTER_GAME broadcasts.

## Verification

  - `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx` — clean, 0 warnings.
  - New suite (7 tests): green in isolation.
  - P180 suite (`LiveTeamLobbyTests`) + new suite: 15/15 green (confirms the
    countdown-routed auto-start didn't regress P180).
  - Full suite: repeated runs reached 2923/2923 fully clean (baseline grew from
    P187's 2916 by the 7 new tests). The only intermittent failures across all
    runs were the two DOCUMENTED live-network flakes, each confirmed
    environmental:
      - `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
        — green in isolation.
      - `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`
        — the P186 machine-state flake (fails in isolation even on the pristine
        tree; the test bypasses readiness entirely by setting `Stage = Started`
        directly and its failing assertion is a kill-observation at line 399,
        with zero references to P188's symbols).

## Divergences from / confirmations of the prompt (verified against live native)

  - Confirmed `c_fMissionBriefingCountdown = 15.0f` (`igc.h:1005`) — the prompt
    left it "not confirmed"; it is.
  - PING actually carries THREE fields (`timeClient`, `timeServer`, `fmg`;
    `Messages.h:280-284`, cross-checked against the Wopr interop
    `AllMessages.h:2015-2071`), not just `timeServer`. All three are ported
    faithfully; the server stamps only `timeServer` and echoes the rest
    unchanged. The 16-byte natural-alignment layout was derived from those
    declarations.
  - Confirmed `START_GAME`(157) is a zero-field pure trigger, and is gated on
    mission owner + `STAGE_NOTSTARTED` + `FAllReady` in native
    (`FedSrv.CPP:7597-7621`). The port substitutes its own simplified
    `IsAllOccupiedSidesReady` for `FAllReady` (deliberate, as directed).
  - Confirmed `BANDWIDTH`(193) is DEAD in native — its entire `FM_C_BANDWIDTH`
    handler sits inside a `/* ... */` comment block (`FedSrv.CPP:7731-7739`).
    Nothing built for it.
  - Confirmed `START_GAME`'s happy-path test needs a slight test-technique
    divergence: because the ready-up auto-start consumes the all-ready +
    `NotStarted` state through the normal wire ready-up, the happy-path/refusal
    tests arrange "every occupied side ready" by setting `Side.Ready` directly
    (bypassing `RecomputeSideReady`, which is what triggers auto-start) so the
    manual `START_GAME`'s own `NotStarted` gate is exercisable. This isolates
    the START_GAME feature from the auto-start feature.
  - `GAME_STATE`(155)/`GAIN_FLAG`(156) and `PINGDATA`(194)/`REQPINGDATA`(195)
    remain OUT OF SCOPE as directed (conquest/territory game mode and
    per-connection latency tracking respectively).
