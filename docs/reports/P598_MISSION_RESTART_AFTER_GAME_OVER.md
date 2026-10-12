# P598 — Mission restart after game-over (rebuilt mission never auto-starts)

## Summary

After a game ends with players still connected, the P439/P480 rotation rebuilt a
fresh `NotStarted` mission, but **the new mission never auto-started** — everyone
stuck in `NotStarted` until a server restart. Hit four times on the 221 server on
2026-10-10/11.

**Root cause:** P480's migration (`MigrateConnectedPeersIntoRotatedMission`)
cleared each migrated peer's ready flag (`_playerReady.Remove(conn)`), and after
the rotation **nothing re-readied them**. `TryAutoStart` / `IsAllOccupiedSidesReady`
require every occupied side aggregate-ready; with the migrated peers unready and
no re-ready source (the MonoGame client readied only at first join; the
allegiance-mcp probe readies once at startup, P485), occupied sides never became
ready and `TryAutoStart` never fired. Only a server restart fixed it.

**Fix (native-faithful):**
1. **Server preserves each player's ready flag across the rotation** — native
   `ProcessGameOver` resets only the *side aggregate* readiness and **preserves
   each player's individual ready flag** (`fsmission.cpp:3501` resets
   `rgfReady[side]`, while the "set everyone unready" block at
   `fsmission.cpp:3468-3481` is commented out by Imago — ready is an AFK flag).
   The port's migration now **preserves** `_playerReady` instead of clearing it.
2. **Server re-derives the side aggregates after re-seating** (Phase 4): a side
   whose every connected peer was ready comes back aggregate-ready and
   `RecomputeSideReady`'s flip fires `TryAutoStart` — the rebuilt mission
   auto-starts, no manual re-ready and no server restart (native
   `CheckForSideAllReady`, `fsmission.cpp:5498-5529`).
3. **Clients re-ready via the native lobby flow**: the MonoGame client
   re-sends `PLAYER_READY(true)` for its fresh ship id when it lands back in
   ReadyCheck on the fresh `NotStarted` mission (re-armed for a changed ship id,
   incl. `--auto-ready` capture runs).
4. **allegiance-mcp re-readies its probe and every live bot** after a rotation
   (re-sends `PLAYER_READY(true)` for the new ship id), so bot-driven showcases
   keep auto-starting the next wave.
5. **Port of native's unattended restart** (`bAutoRestart`) behind per-game
   config `GameConfig.AutoRestart` + `GameConfig.RestartCountdownSeconds`
   (`fsmission.cpp:3539-3546`, `igc.h:1154/1171/1268/1274`): a game-over rotation
   arms a timer that starts the rebuilt mission even when **no peer is ready**.

New joiners after a rotation still start normally (the first wave after a
game-over auto-starts once sides are ready — via the migrated peers' preserved
ready and/or the new joiner readying up), and a mission can rotate repeatedly
(three rotations exercised in tests) without leaking peer slots / ship ids
(`server.Ships` stays at the connected count).

---

## Mechanism confirmed

`TryAutoStart` (`Server.Lobby.cs` ~1092) requires `IsAllOccupiedSidesReady`
(~1116): every side that currently has ≥1 connected player must be aggregate
ready (`side.Ready == true`). `side.Ready` is derived from each connected peer's
`_playerReady[conn]` by `RecomputeSideReady` (~987), which is only (re-)run when a
peer sends `PLAYER_READY` (dispatch `Server.Dispatch.cs` ~1431) or a side forces
ready. Native's analogous gate is `FAllReady` (`fsmission.cpp:4375-4445`) plus
`CheckForSideAllReady` (`fsmission.cpp:5498-5529`).

After P480's rotation + migration:
- The old mission is swapped for a fresh `NotStarted` world at the same
  `_missions` index (`RotateMissionAfterGameOver`, `Server.Lobby.cs` ~471).
- Still-connected peers are re-seated with fresh ship ids and cleared ready
  (`_playerReady.Remove(conn)`, the P598 bug).
- No `PLAYER_READY` ever arrives again (clients/probe only readied once), so
  `RecomputeSideReady` never runs, `side.Ready` stays false, and `TryAutoStart`
  never fires → permanent `NotStarted`.

The tests reproduce exactly this: two real sides, all peers ready (as they would
be to have started the first game), a real `#draw → OfferDraw → AcceptDraw →
game-over` rotation, then **no re-ready** — the rebuilt mission must climb
`NotStarted → Starting → Started` on its own.

## Native fidelity (file:line)

| Rule ported | Native | Port |
|---|---|---|
| Player ready survives game-over (AFK flag) | `fsmission.cpp:3468-3481` (reset block **commented out**) | `_playerReady` preserved in migration |
| Side aggregate readiness reset at game-over | `fsmission.cpp:3501` `rgfReady[side]=false` | fresh side starts `Ready=false`; re-derived by Phase 4 |
| Side readiness re-derived from its players | `CheckForSideAllReady`, `fsmission.cpp:5498-5529` | `RecomputeSideReady` Phase 4 |
| Ready-up auto-start gate (all occupied sides ready) | `FAllReady`, `fsmission.cpp:4375-4445` | `IsAllOccupiedSidesReady` (port's stated substitute) |
| Unattended restart timer | `bAutoRestart` + `fRestartCountdown`, `fsmission.cpp:3539-3546`, `igc.h:1154/1171/1268/1274` | `GameConfig.AutoRestart` + `RestartCountdownSeconds` → `AdvanceAutoRestarts` |

Note: the original port comment claimed native clears player ready at
`fsmission.cpp:3493-3521`; the spot-check corrected this — that region resets the
*side* aggregate (`rgfReady[sideID]=false`, `:3501`), and the *player* ready reset
block (`:3468-3481`) is commented out. Proved above from the native source.

## Changes

- `src/Allegiance.Server/ServerConfig.cs` — `GameConfig.AutoRestart` +
  `GameConfig.RestartCountdownSeconds` (native `bAutoRestart`/`fRestartCountdown`).
- `src/Allegiance.Server/Server.cs` — `_autoRestartDeadline` field.
- `src/Allegiance.Server/Server.Lobby.cs` — preserve `_playerReady` across
  migration; Phase 4 recompute after re-seat; arm auto-restart in
  `RotateMissionAfterGameOver`; `AdvanceAutoRestarts` driver.
- `src/Allegiance.Server/Server.Broadcast.cs` — hook `AdvanceAutoRestarts` into
  `SimPass`.
- `src/Allegiance.ClientCore.Game/Game1.cs` + `Game1.Panes.cs` — client re-ready
  for a fresh ship id after rotation.
- `tools/allegiance-mcp/BotManager.cs` — re-ready probe + live bots after
  rotation (`ReReadyBot`, `ReReadyProbe`).
- `tests/Allegiance.Core.Tests/P598MissionRestartLiveTests.cs` — new tests
  (below).

## Tests (failing-first)

New file `tests/Allegiance.Core.Tests/P598MissionRestartLiveTests.cs`:

1. `Rotated_mission_auto_starts_from_preserved_ready_after_game_over` — two
   sides, all peers ready, real game-over rotation → rebuilt mission auto-starts
   (`Starting → Started`) with **no re-ready**. **Fails on today's code**
   (ready cleared, side never ready, mission stuck NotStarted); passes after.
2. `New_wave_after_rotation_enters_and_three_rotations_run_without_leak` — three
   rotations in a row, a fresh wave joins after rotation 1 and starts normally,
   and `server.Ships` stays at the connected peer count (no peer/ship leak).
   **Fails on today's code** (rotation 1 never re-starts).
3. `AutoRestart_config_restarts_rotated_mission_without_ready_up` — with
   `GameConfig.AutoRestart=true, RestartCountdownSeconds=2` and **no peer
   ready**, the game-over rotation's rebuilt mission auto-starts on the timer.
   Passes independently of the ready path.

Failing-first was demonstrated by temporarily restoring the old behavior
(`_playerReady.Remove(conn)` + no Phase 4 recompute): tests 1 and 2 fail
(mission never re-starts), test 3 passes (independent timer path); all pass with
the fix restored.

Existing related suites still pass: `P480RotationKeepsPlayersLiveTests`,
`MissionRotationLiveTests`, `PlayerReadyTests`, `GameOverPlayersWireTests`.

Full-suite runs (`Allegiance.Core.Tests`, 3955 tests) show only documented load
flakes varying run-to-run (chat wire, `GameOverPlayersWireTests.Conquest_win…`,
`CargoBayReloadTests` members — see `docs/TEST_FLAKES.md`); each reproduced
failure passes in isolation and none touches rotation/ready/start. Reported
below per the task's "report each run as it happened" rule.

## Verification numbers

Suite via `scripts/run-tests.sh` on the final code (each project's own
Passed!/Failed! line; `Lobby` = Allegiance.Lobby.Tests, `Mono` =
Allegiance.ClientCore.MonoGame.Tests, `CC` = Allegiance.ClientCore.Tests,
`Core` = Allegiance.Core.Tests):

Run 1: Lobby **12/12**, Mono **1751** (+3 skipped), CC **602/603**, Core **3952/3955**.
  - CC: `ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility`
    (logon drain-order nondeterminism) — passes in isolation.
  - Core: `LiveChatSectorWireTests.…relay_to_real_peers…`,
    `GameOverPlayersWireTests.Win_by_elimination…`,
    `CargoBayReloadTests.SwapPart_out_of_range_negative…` — all pass in
    isolation (ledger: `docs/TEST_FLAKES.md` chat/GameOverPlayers/CargoBay
    load-flake families).
Run 2: Lobby **12/12**, Mono **1751** (+3 skipped), CC **603/603** (clean), Core **3952/3955**.
  - Core: `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129…`,
    `LoadoutChangeLiveTests.Boarding_and_leaving…`,
    `CargoBayReloadTests.SwapPart_negative_mount…` — all pass in isolation.

Both runs carry ONLY documented load-flakes that pass in isolation; the set of
failing members varies run-to-run (chat / GameOverPlayers / CargoBay /
ThreePeer / AutoReload / LoadoutChange — all in `docs/TEST_FLAKES.md`). No
rotation/ready/start test failed, and **P598MissionRestartLiveTests (3/3) passed
in both runs**. Failing-first was demonstrated by temporarily restoring the old
behavior (`_playerReady.Remove` + no Phase 4 recompute): the two ready-path
tests fail (mission never re-starts), the AutoRestart-config test passes
(independent timer), and all pass with the fix.

## Live proof (real local server, real wire clients)

Server logs captured in `docs/reports/P598_assets/`.

**Part 1 — mandatory bot-reach check** (`server_botreach.out`): a local
NON-debug server (`m2xspiderw.igc`), `spawn_scenario preset=showcase
size_scale=1 target_sector=Alderan` (15 fighters/interceptors/scouts/bombers per
side). The mission auto-started via ready-up, and the bots left their home
sectors and crossed warps toward the target — **58 `[warp]` lines**, e.g.
`[warp] ship 8110 crossed from 'Ganymede' (1050) to 'Rigel' (1059) via warp
2176`, `…from 'Organia' to 'Juniper'`, etc. (`MCP RESULT`: `mode showcase,
targetSector 1052, spawned 15+15`).

**Part 2 — win → rotation → rebuilt mission AUTO-STARTS** (`server_elim_rotation.out`):
`run_scenario preset=elimination_run` drives a real win-by-elimination. The log
shows the complete P598 sequence with the still-connected peer:
```
[gameover] mission #0 -> Stage Over, iSideWinner=0, 1 peer(s)
[gameover] P439: mission #0 finished -> rebuilt fresh STAGE_NOTSTARTED …
[reseat]  peer 1 -> fresh mission: new ship 8104 (was 8098) targeting side 0 (was leader)
[ready]   side 0 aggregate readiness True -> TEAM_READY(46) …
[ready]   ALL occupied sides ready — mission auto-start begins countdown
[start]   mission #0 countdown begun (15s) -> STAGE_STARTING …
[start]   mission #0 countdown elapsed -> STAGE_STARTED, broadcasting ENTER_GAME
```
i.e. after the win, the rebuilt `NotStarted` mission migrated the connected
peer (preserving its ready flag, Phase 4 recompute re-derived side 0 ready) and
**auto-started to `STAGE_STARTED` — no server restart, no manual re-ready**. On
today's (pre-fix) code this sequence stalls at `NotStarted` forever (the 221
bug). The full real-window client screenshots of a second wave were not driven
in this session (driving a second 15v15 elimination is out of proportion to the
fix); the rotation auto-start is additionally proven live at the wire level by
`P598MissionRestartLiveTests` (real TCP `FltBot` clients against a real
`Server`).

