# P763 — 30-second side auto-seating behind a server flag

**Status: complete.** The server now defaults to native behaviour (wait for a
`POSITIONREQ`); the pre-P763 30-second silent-peer auto-seat is an explicitly
opt-in port adaptation, and tests pin both modes.

## Native intent

Native has **no timeout that seats a silent client**. The team screen sends
`C POSITIONREQ` when the player picks a side:

- `~/Development/Allegiance/src/WinTrek/teamscreen.cpp:2964-2981` — opens the
  request (`BEGIN_PFM_CREATE(... C, POSITIONREQ)`), sets `iSide = m_sideCurrent`,
  records `m_sideToJoin`/`m_lastToJoinSend`, and updates the button/prompt state.
- `~/Development/Allegiance/src/Inc/Messages.h:320` — the `POSITIONREQ` message
  id used by that send.

The server then only acts on that recorded request:

- `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6658-6691` — receives
  `FM_C_POSITIONREQ`, resolves `NA` through `PickNewSide`
  (`FedSrv.CPP:6668-6669`), then calls `CFSMission::RequestPosition`.
- `~/Development/Allegiance/src/FedSrv/fsmission.cpp:4729-4774` — validates the
  request; auto-accepts a leaderless side when `fAutoAcceptLeaders`/rejoin/
  `rgfAutoAccept` allows it (`:4755-4762`), otherwise broadcasts
  `S POSITIONREQ` and records a join request waiting for the leader
  (`:4763-4772`).
- `~/Development/Allegiance/src/WinTrek/teamscreen.cpp:2970-2983` — the client
  remains in “waiting to join” state; there is no client- or server-side timer
  that assigns the side.

So a native peer that never sends `POSITIONREQ` simply remains unseated. P281’s
30-second auto-assign was a port adaptation, not a native rule (P763 brief).

## Modern approach / port adaptation

`ServerConfig.SideSelectAutoSeatSeconds` (`src/Allegiance.Server/ServerConfig.cs`)
is a top-level `double?`:

- `null`/absent, `<= 0`, `NaN` or infinite → **OFF**, the default and the native
  behaviour: only peers with a recorded `POSITIONREQ` choice are drained.
- A finite positive value → opt-in timed auto-seat. The pre-P763 value is `30`.

`AdvanceSideSelection` (`src/Allegiance.Server/Server.Lobby.cs`) now reads the
config instead of the deleted `SideSelectTimeoutSeconds` constant. It still
drains in logon (`JoinSeq`) order, still treats `POSITIONREQ(NA)` as an immediate
auto-pick, and still never lets a pending peer block the P180 ready-up
auto-start (pending peers are side-less and absent from `_peerShips`). The
existing log line is unchanged when the timer is on:

`[sideselect] peer N 'name' never chose a side within 30s — auto-assigning`

The property is part of the source-generated `ServerJsonContext` graph
(`[JsonSerializable(typeof(ServerConfig))]`), so the new field is AOT-safe and
reflection-free.

### Showcase / rig behaviour

- `src/Allegiance.SinglePlayer/SkirmishOptions.cs` now sets
  `SideSelectAutoSeatSeconds = 30` for `SkirmishSpeed.Showcase` only
  (`ShowcaseSideSelectAutoSeatSeconds`). Normal/Fast leave it `null`, so normal
  single-player matches wait for the client’s side choice.
- The rig’s live config (`192.168.0.221:~/allegiance-test/docker/compose.server.json`,
  the file mounted by the compose stack used by
  `~/.claude/jobs/82c1509b/tmp/deploy-107.sh`) was updated with top-level
  `"sideSelectAutoSeatSeconds": 30`. A timestamped backup was left beside it.
  No deploy, restart or 221/200v200 run was performed. The exact delta is
  [rig-side-select-delta.json](P763_assets/rig-side-select-delta.json).
- `ShowcaseConfig`/`ResolveShowcaseMode` are about showcase economy/research and
  are unchanged; the seating policy belongs on the top-level server config.

### MCP bots

`tools/allegiance-mcp` bots **always send `POSITIONREQ`** before they are used:
the startup probe (`BotManager.cs:335`), idle/weave bots and combat seats
(`SeatWithRetry` at `BotManager.cs:371, 593-595, 786-787`) and the research
commander (`BotManager.cs:1179`). They therefore seat without the timer. The
rig flag is still needed for AFK/human clients that never pick a side, and
preserves the pre-P763 rig behaviour.

## Changes

### Production

- `src/Allegiance.Server/ServerConfig.cs` — `SideSelectAutoSeatSeconds` with the
  native/opt-in contract in its XML doc.
- `src/Allegiance.Server/Server.cs` — removed the `SideSelectTimeoutSeconds`
  constant; updated pending-side-select docs.
- `src/Allegiance.Server/Server.Lobby.cs` — `AdvanceSideSelection` reads the
  config; native-off path drains only recorded choices.
- `src/Allegiance.Server/Server.Dispatch.cs`,
  `src/Allegiance.Server/Server.Broadcast.cs` — comments now describe the
  opt-in timer instead of an unconditional 30 s timeout.
- `src/Allegiance.SinglePlayer/SkirmishOptions.cs` — showcase-only 30 s.
- `src/Allegiance.ClientCore.Game/Game1.AutoProof.cs`,
  `Game1.Panes.cs`, `LiveProof/AutoProofPlan.cs` — comments no longer claim a
  server auto-assign fallback; unattended proofs must pass `--auto-side`.
  `docs/LIVE_PROOF.md` updated to match.

### Tests and tools

- New tests in `tests/Allegiance.Core.Tests/LiveSideSelectTests.cs`:
  - `Flag_off_keeps_a_silent_peer_pending_past_31_seconds_and_an_explicit_positionreq_still_seats_it`
    (theory: `null`, `0`, `-1`) — native mode.
  - `Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` — the timer
    seats at ~30 s, not immediately.
- `tests/Allegiance.Core.Tests/ConfigDefaultsJsonTests.cs` — default is `null`,
  `0` and `30` bind, and the source-generated JSON round-trips.
- `tests/Allegiance.ClientCore.Tests/SP6GameSpeedTests.cs` — Showcase maps to 30;
  Normal/Fast stay native-off.
- The brief’s listed suites now set `SideSelectAutoSeatSeconds = 30`:
  `LiveSideSelectTests`, `LiveP548GameOverStageGateTests`,
  `LiveTeamLeadershipTests`, `BallotVotingLiveTests`,
  `LiveP390DebugSpawnerSideReconcileTests`, `tools/targetinfo-proof`.
- The “grep for any others” pass found many live tests and proof tools whose
  headless peers use `ConnectionSession`, `WireClient`, `ShieldClient`,
  `CombatClient` or proof observers and **never send `POSITIONREQ`**. They
  previously inherited the 30 s default; they now set the flag explicitly (30)
  so their behaviour is unchanged. This is the bulk of the test/tool diff. All
  proof observers in `hitsound/hullhit/shieldflare/warp/hullswap/lighting/UI`
  proofs and `targetinfo-proof` were included.

### Parity

`docs/parity/parity-matrix.csv` row
`lobby,AFK / auto-seat handling / Timed automatic side assignment` is now
`done`: native has no timeout, the default is the native
wait-for-`POSITIONREQ` behaviour, and the 30 s auto-seat is an opt-in port
adaptation. The row cites `teamscreen.cpp:2962-2982`, `FedSrv.CPP:6658-6691`,
`fsmission.cpp:4729-4774`, the new config/read sites and both new test modes.

Scorecard before → after (the row was previously `partial`):

```
            lobby weighted / unweighted
before      69.96% (32.880/47.000) | 66.67% (22/33)
after       73.15% (34.380/47.000) | 69.70% (23/33)

after (all areas)
TOTAL       78.78% (1431.405/1817.000) | 76.07% (480/631)
```

## Verification

All commands were run from this worktree; no commit/push.

- Whole-solution Release build: **0 warnings / 0 errors**
  ([build log](/home/domingo/allegiance-evidence/P763/logs/build-final2.log)).
- Native AOT publish of `src/Allegiance.Server` (linux-x64): **PASS**, no
  IL2026/IL3050 ([AOT log](/home/domingo/allegiance-evidence/P763/logs/aot.log)).
- **ONE fast tier**: `tools/run-tests.sh --tier fast` → **PASS**, 9,251 tests
  (Lobby 19, MonoGame 2,767 passed + 3 not-executed, ClientCore 1,546, Core
  4,904), 271.3 s, no timing offenders
  ([summary](/home/domingo/allegiance-evidence/P763/fast-final/summary.json)).
- New/touched suites **3× alone**: `LiveSideSelectTests` 8/8,
  `LiveP548GameOverStageGateTests` 5/5, `LiveTeamLeadershipTests` 12/12,
  `BallotVotingLiveTests` 11/11, `LiveP390DebugSpawnerSideReconcileTests` 3/3,
  `ConfigDefaultsJsonTests` 8/8, `SP6GameSpeedTests` 8/8 on all three runs.
  The previously failing live suites (`LiveShieldRegenBroadcastTests`,
  `LiveCombatWireTests`, `SideSwitchDevMaskLiveTests`, `P682MissionStateTests`,
  `LeaderSameSideBotClientLiveTests`, `GameOverBackLiveTests`,
  `DeadLatchRespawnLiveTests`, `P641FlightPartsLiveTests`) were also re-run 3×
  alone and passed (raw logs in the evidence directory).
- `scripts/parity-scorecard.py docs/parity/parity-matrix.csv` → 665 rows
  validated.
- `git diff --check` → clean.

**Environment note:** the final fast-tier run used
`TMPDIR=/home/domingo/allegiance-evidence/P763/tmp` (outside the worktree). With `TMPDIR`
inside the worktree, `PlayerBundlePathsTests` finds the worktree’s repo-root
`local.env` while walking up from its temp install directory and 5 path tests
fail as an environment artifact. With an external temp directory they pass 9/9
3×; this is unrelated to the side-select change and is recorded so the result
is not misread.

## Evidence

- Small committed assets: [verification.json](P763_assets/verification.json),
  [rig-side-select-delta.json](P763_assets/rig-side-select-delta.json).
- Raw logs, TRX and the full fast-tier tree:
  `/home/domingo/allegiance-evidence/P763/` (outside the repository; ~75 MB, not committed).

## Unfinished / notes

- No side-select UI changes and no other lobby rows were touched (out of scope).
- The rig config was edited but the server was **not** redeployed or restarted;
  Claude’s next deploy picks it up. The rig delta is also recorded in
  `P763_assets/rig-side-select-delta.json`.
- The opt-in 30 s auto-seat remains a documented non-native adaptation. It is
  now off unless an operator (or the showcase/test/rig config) explicitly asks
  for it.
