# P388 — Debug mode: don't end the mission just because no player is connected

Status: done, committed on `master` (not pushed — Domingo pushes).

## Mechanism (why it happened)

The connected client disconnecting was not a bug — it was a real, correct,
native-faithful feature doing its intended job, with no debug-mode exception.

- **`CheckForVictoryByInactiveSides`** (`Server.Lobby.cs:54-82`) is the port of
  native's win-by-elimination (`FedSrv/fsmission.cpp:3767-3788`, trigger site
  `:5371-5427`). It counts how many sides still have at least one **seated
  player** (`_peerShips`; NPCs never appear there — matching native `HasPlayers`'
  `ISPLAYER && !IsGhost`, `fsmission.cpp:1422-1431`). More than one side with
  players → game continues; exactly one → that side wins; zero → draw
  ("All sides were destroyed"). Either way it calls `DeclareGameOver`
  (`Server.Lobby.cs:98-`), which flips `mission.Stage` to
  `MissionStage.Over` and broadcasts `GAME_OVER(62)`.
- It is called from exactly one place: `Server.Lifecycle.cs:901`, inside the
  real peer-disconnect handler right after the departing peer's ship is retired.
  That is the correct production behavior — if every real player leaves, the
  match should end.
- But debug mode is an **unattended autonomous test arena** (P379/P381/P382/
  P387): zero players is the designed steady state, and the autonomous NPC
  miners must keep running (undock/warp/mine/sell/redock) with nobody connected.
  So the empty-lobby elimination check was exactly the wrong thing to fire
  there. Live reproduction this session, right after P387:

```
[conn] peer 1 left
[gameover] mission #0 -> Stage Over, iSideWinner=NA(draw), 0 peer(s)
```

## The fix

A debug-mode early return alongside the existing idempotency guard in
`CheckForVictoryByInactiveSides` (`Server.Lobby.cs`):

```csharp
if (mission.Stage == MissionStage.Over)
    return;                                  // idempotent: already decided

if (_config.LiveSettings?.DebugMode == true)
{
    // ... [debugmode] skip log when this actually prevents an outcome ...
    return;                                  // debug mode: unattended arena, never ends on empty lobby
}
```

It uses the exact `_config.LiveSettings?.DebugMode == true` access convention
already used at `Server.Lifecycle.cs:475` and `:664` (no second convention
invented), and logs plainly with the port's `[boot]`/`[npc]`-style tagged
logging when the skip actually prevents an outcome that would otherwise have
fired:

```
[debugmode] skipping win-by-elimination check (0 players is expected in the unattended test arena)
```

**Out of scope, intentionally untouched:** `DeclareGameOver` itself and the P191
conquest/territory win path that shares it (`OnVictoryByStationsComputed`). A
genuine combat/conquest outcome can still end a debug-mode mission; P388 only
suppresses the *empty-lobby population* elimination check, nothing else.

## Verification 1 — debug mode survives the disconnect and keeps cycling (LIVE)

`LiveP388DebugNoGameOverTests.Debug_mode_disconnect_does_not_end_mission_and_miners_keep_cycling`,
against the real `m2xspiderw.igc` map in debug mode. Sequence: boot debug server
(auto-starts, P387; 2 initial docked miners) → connect a real loopback TCP
client → disconnect it → confirm the mission does NOT end and both autonomous
miners keep cycling with zero clients connected. Real observed output:

```
[boot] *** DEBUG MODE ACTIVE (liveSettings.debugMode=true) ...
[start] mission #0 countdown elapsed -> STAGE_STARTED, broadcasting ENTER_GAME
[stage] peer 1 'P388PEER' -> MISSION_STAGE(37) Started
[debugmode] skipping win-by-elimination check (0 players is expected in the unattended test arena)
[P388-LIVE] debug mode: client 8098 disconnected → mission stage 'Started' (NOT Over) — arena survives, 0 peers connected.
[P388-LIVE] miner 8097 (side 1) completed mine-sell cycle #1 — ore sold, back to 0
[P388-LIVE] miner 8096 (side 0) completed mine-sell cycle #1 — ore sold, back to 0
[P388-LIVE] miner 8096 (side 0) completed mine-sell cycle #2 — ore sold, back to 0
[P388-LIVE] miner 8097 (side 1) completed mine-sell cycle #2 — ore sold, back to 0
```

Both miners (side 0 ship 8096 and side 1 ship 8097) each completed **2 full
mine-sell cycles** (ore rose then sold to zero at the dock) **with zero clients
connected**, and the mission stage remained `Started` (never `Over`).

**Must-fail discriminator proved:** with the P388 gate disabled, the same test
reproduces the exact session symptom and fails:

```
[gameover] mission #0 -> Stage Over, iSideWinner=NA(draw), 0 peer(s)
[...] Debug_mode_disconnect_does_not_end_mission_and_miners_keep_cycling [FAIL]
      Assert.NotEqual() Failure: Values are equal  (line 220)
```

## Verification 2 — production (debugMode off) still ends correctly (LIVE)

`LiveP388DebugNoGameOverTests.Production_last_player_disconnect_still_ends_mission`
— a real regression check, not just "the flag exists." With debugMode off
(XXV_simple, 2 teams), a real client is seated and then disconnects; the last
player leaving still declares the game-over/draw exactly as before P388:

```
[P388-LIVE] production (debugMode off): last player ... disconnected → stage Over after N ticks — game-over/draw intact.
```

This mirrors the pre-existing `LiveWinConditionTests.Last_player_disconnect_is_a_draw`
(outcome preserved, stage → `MissionStage.Over`).

## Whole-solution build

`dotnet build` (whole solution): **0 warnings, 0 errors**.

## Full test suite

Run twice. Total 3372 tests. Both P388 tests pass in both runs. The only
failures in each run were the pre-existing, documented live-wire timing flakes
(a rotating subset of `LiveTurretGunnerBindingTests`,
`Multi_mission_disconnect`, `LiveStationRescueTests`), verified independent of
P388 by the pristine A/B technique from the landing-bay skill: with the P388
gate disabled (source effectively pristine), those same tests still fail in
isolation.

## Files changed

- `src/Allegiance.Server/Server.Lobby.cs` — the debug-mode early return in
  `CheckForVictoryByInactiveSides`.
- `tests/Allegiance.Core.Tests/LiveP388DebugNoGameOverTests.cs` — new live
  proof suite (both verifications above).
- `P388_DEBUG_MODE_NO_GAMEOVER_ON_EMPTY.md` — this report.
