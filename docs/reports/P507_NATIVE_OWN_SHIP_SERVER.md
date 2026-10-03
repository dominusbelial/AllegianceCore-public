# P507 — native own-ship model, part 1 (server): accept the client's reported pose (ProcessShipUpdate) + SHIP_RESET + cookie

**Stage:** P507 · **Branch:** `master` (unpushed) · **Date:** 2026-10-03

## Goal & outcome

Domingo's live playtest (2026-10-03): *prediction stutters, and without it flight
feels sluggish even on a 0.9 ms-RTT LAN.* Decision: **follow native's pattern** —
the client flies its own ship locally and tells the server where it is; the
server only sanity-checks. This task is the **server side** of that model.

Before P507 the port threw the client's pose away: the `SHIP_UPDATE` dispatch
set only `Controls`/`StateM` ("the payload's position/velocity/orientation
echoes are ignored as authority") and relied on a bolted-on server-authoritative
predictor (P447/P502/P505) that still sat ~15 m off with constant snaps. P507
ports native's accept/reject pipeline so the server **trusts a sane client pose**
and resets it (via `SHIP_RESET`) when it isn't plausible.

## Native model (what we follow)

- **Client sends** `FM_C_SHIP_UPDATE(24)` every `c_dsecUpdateClient = 0.15 s`,
  non-guaranteed, with `timeUpdate` (converted to server time), the client's
  `cookie`, and the full `ClientShipUpdate` pose (`clintlib.cpp:1861-1863`;
  `Messages.h:225-231`; `shipIGC.h:1173+`).
- **Server receive** (`FedSrv.CPP:4851-4864`): the update is ignored unless
  `pfsPlayer->GetCookie() == pfmShipUpdate->cookie`, and only when the ship is
  in a cluster with no parent ship. Then `CFSPlayer::SetShipUpdate`
  (`fsship.cpp:1173-1200`) → `CshipIGC::ProcessShipUpdate`
  (`shipIGC.cpp:3139-3210`):
  - out-of-date if `|lastUpdate − su.time| ≥ 1.5 s` (`c_susOutOfDate`);
  - `LegalPosition` (0 < |pos|² < 90000²) check, else `c_susInvalid`;
  - `CalculateShip(su.time, deltaT, …)` advances the client pose to server time
    (`shipIGC.cpp:3244-3297`);
  - **orientation always accepted** (`shipIGC.cpp:3178`);
  - **position & velocity accepted if within 20 m** (d < 400) → `c_susAccepted`,
    else `c_susRejected`.
- **On rejection** the server sends `FM_S_SHIP_RESET(183)` with its own
  exported pose and a `NewCookie()` (`fsship.cpp:1192-1200`); updates are then
  ignored until the client applies the reset and echoes the new cookie
  (`FedSrv.CPP:4851-4864`). Native's per-player cookie is
  `CFSPlayer::GetCookie/NewCookie` (`fsship.h:313-321`): `m_cookie` starts 0 and
  `NewCookie()` = `++m_cookie`, delivered in `SET_CLUSTER` at join/warp
  (`fsship.cpp:767`).

## Implementation

### 1. Per-player cookie (native `NewCookie`)
`PeerShipUpdate` (private nested class on `Server`, `Server.cs`) holds the
current cookie + the acceptance counters; a `Dictionary<ConnectionId,
PeerShipUpdate> _peerShipUpdate` is keyed by the same id as `_peerShips` (so it
survives a mission-rotation migration that rebuilds `LiveShip`), created at the
first join, removed on disconnect (`Server.Lifecycle.cs`). `NextCookie(p)` =
`p.Cookie = (byte)(p.Cookie + 1)` — the native unsigned-counter-mod-256 on the
one-byte wire cookie. Both `SET_CLUSTER` deliveries now stamp it: the join burst
(`Server.Lobby.cs` CompleteJoinAndLaunch, replacing the old logon-echo `cookie`)
and the warp path (`Server.Combat.cs` OnShipWarped, replacing `Cookie = 0`) —
both are native `NewCookie()` sites (`fsship.cpp:767`).

### 2. `ProcessShipUpdate` port (`Server.Dispatch.cs`)
The `SHIP_UPDATE` dispatch distinguishes **legacy** vs **native** updates and
routes accordingly:
- **Legacy** (`u.Time == 0` **or** zero position — what the current client and
  all test bots send): applied **controls-only exactly as before P507**
  (`ship.Controls`/`StateM`), bypassing the cookie gate. This is the P508
  backward-compatibility bridge.
- **Native** (real time + real pose): the cookie gate first — a mismatch drops
  the **whole** update including controls (exactly `FedSrv.CPP:4854`). Then
  `ProcessPeerShipUpdate`:
  1. `GetSC` — always apply `Controls`/`StateM` (task-directive "as today",
     a deliberate divergence from native, which reaches it only inside the
     in-window+legal gate, kept for control-input continuity);
  2. `deltaT = SimNow − u.Time/1000` (u.Time is uint ms; native
     `GetMyLastUpdate() − su.time`), ±1.5 s window → `outOfDate`;
  3. `LegalPosition` → `invalid`;
  4. `ship.CalculateShipPose(deltaT, ref pos, ref vel, ori, turnRates)` advances
     the **client-reported** pose to server "now" **with the real flight model**
     — reusing `ShipKinematics` (P448a), not a second integrator
     (`ship.CalculateShipPose`, `Ship.cs`, the port of
     `shipIGC.cpp:3244-3297`: chunked `n = 1 + ⌊ΔT/0.25⌋`, each chunk
     `ShipKinematics.Step` then `position += velocity·ΔT`; the `ΔT ≤ 0` branch
     backs out linearly with the turn rates);
  5. `SetOrientation` always accepted (`shipIGC.cpp:3178`);
  6. `dSq = (ship.Position − pose).LengthSquared() < 400` → **accepted** (set
     position/velocity), else **rejected**.

`Ship.ExecuteMove` was refactored to extract `BuildKinematicsInputs()` (shared
by `ExecuteMove` and the new `CalculateShipPose`) — behaviour-identical,
pinned by the existing golden `ShipKinematics` tests.

### 3. `SHIP_RESET` (183) codec + send-on-reject
New codec `Wire/ShipReset.cs`: `4-byte header + ServerShipUpdate(47 B) + 1-byte
cookie = 52 B`, packed with no alignment pad (the same no-padding invariant as
SET_CLUSTER P36 / SHIP_UPDATE P33; native `Messages.h:944-949`). On reject the
server exports its own pose + `NewCookie()` and sends it
(`fsship.cpp:1192-1200`). Removed `ShipReset` from `NotSupportedFedMsgs`
(enum member + registry entry).

### 4. Backward compatibility (P508 bridge)
Legacy detection (`Time==0 || zero position`) keeps the pre-P507 controls-only
path, so the current client, `Allegiance.Bot.CommandControls`, MCP and vizfight
keep working unchanged. Test bots send `Time=0` → legacy → unaffected by the
new cookie gate.

### 5. Logging
`SimPass` gained a `shipUpdateSummaries` step (every ~10 s): per-peer
`accepted / rejected / out-of-date / invalid` counts + mean reject distance +
current cookie, tagged `[ctrlak]`, so the live test can watch it working.

## Out-of-scope (confirmed not touched)
- Client sending its pose / handling `SHIP_RESET` → **P508** (next).
- Broadcasts to other peers (peers still receive their own-pose via the existing
  `SINGLE_SHIP_UPDATE` path; teammates/others unchanged).
- Cheat detection beyond native's rules.
- Persistent per-update turn-rate / afterburner-power adoption from the wire is
  deliberately **not** done (native's `GetOVTP` does overwrite `m_turnRates` and
  afterburner power; the task only mandates controls+stateM "as today", and the
  port's continuing between-update simulation is meant to stay as-it-is — the
  client-reported turn rates are used only for the `CalculateShipPose` replay).

## Verification

### Failing-first
Temporarily bypassing the native path in the dispatch (treat native updates as
legacy) made the two centrepiece tests go **red**: `Native_pose_within_20m…`
("accepted pose was not applied") and `Native_pose_over_20m…` ("no SHIP_RESET
was sent on rejection"). Reverted → green. (The cookie/out-of-date/invalid/
legacy tests are secondary discriminators meaningful in combination.)

### New tests — `P507OwnShipModelLiveTests` (+7), failing-first, all green
- `Native_pose_within_20m_is_accepted_and_applied` — pose 10 m from the
  stationary server ship (zero-thrust hull at Spawn) with the matching cookie is
  accepted and applied.
- `Native_pose_over_20m_is_rejected_and_sends_SHIP_RESET_with_new_cookie` —
  1000 m pose is rejected (ship doesn't jump), a SHIP_RESET with a **fresh**
  cookie arrives, and an update echoing that new cookie is then accepted.
- `Wrong_cookie_drops_the_update_whole` — mismatched cookie drops the update
  (no pose, no SHIP_RESET), per `FedSrv.CPP:4854`.
- `Stale_update_is_out_of_date_pose_ignored` — 5 s-old time → c_susOutOfDate.
- `Illegal_position_is_invalid_pose_ignored` — |pos|² ≥ 90000² → c_susInvalid.
- `Legacy_Time0_zero_pose_update_still_applies_controls` — CommandControls
  (Time=0, zero pose, Ack cookie) still applies controls (backward compat).
- `ShipReset_round_trips_on_the_wire` — 52-byte codec round-trip (fmid 183,
  cookie, exported pose preserved).

All 7 pass in isolation and in every full-suite run.

### Whole-solution build
`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug` → **0 warnings,
0 errors** (whole solution; verified at the end).

### Full suite — `scripts/run-tests.sh` (P471), four consecutive runs
Grand total per run: **5336** = Lobby 12 + MonoGame 1207 + ClientCore 411 +
Core 3706 (the 3706 Core includes the 7 new P507 tests; all green in every run).

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 2 | 12/12 | 1207/1207 | 410/411 | 3704/3706 | ClientCore `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`; Core `WarpBombTests.Live_whole_mission_warp_bomb_…`, `LiveRipcordTests.Ship_anchor_insufficient_energy_…` |
| 3 | 12/12 | 1207/1207 | 411/411 | 3703/3706 | Core `SideEliminationLiveTests.Eliminating_side2_sends_side_only_…`, `LiveRipcordTests.Re_target_activates_to_requester_only`, `AddPartTests.Second_part_touch_while_offer_pending_…` |
| 4 | 12/12 | 1207/1207 | 411/411 | 3705/3706 | Core `LiveWinConditionTests.Board_refused_once_mission_over` |

(A 1st run after the registry fix measured Core 3702/3706 with 4 live-wire
flakes including `StationUnderAttackAlertLiveTests.Defending_side…`,
`LiveWinConditionTests.Board_refused…` and `AddPartTests.Second_part_touch…`.)

Every failure is a **pre-existing live-wire timing load-flake** in the
**untouched** assemblies — the rotating class `docs/TEST_FLAKES.md` documents
(`WarpBombTests`, `LiveRipcordTests`, `SideEliminationLiveTests`,
`AddPartTests`, `LiveWinConditionTests.Board_refused…`, `ShipDeleteGoneLiveTests`
are all cited in the ledger; `StationUnderAttackAlertLiveTests` and
`AddPartTests.Second_part_touch…` are the same documented rotating class).
None of their files reference any P507 symbol, and each confirmed **green in
isolation** (`--filter`, measured: StationUnderAttackAlertLiveTests +
AddPartTests.Second_part_touch together; WarpBombTests + LiveRipcordTests +
SideEliminationLiveTests together 15/15; ShipDeleteGoneLiveTests; and
LiveWinConditionTests.Board_refused 1/1). **No failure reproduced on the P507
area.**

## Files changed
- `src/Allegiance.Core/Wire/ShipReset.cs` — **new** SHIP_RESET(183) codec (52 B).
- `src/Allegiance.Core/Wire/ClientShipUpdate.cs` — `FromPose` factory (native
  payload) + `FromControls` delegates to it.
- `src/Allegiance.Core/Sim/Ship.cs` — extract `BuildKinematicsInputs()`; add
  `CalculateShipPose` (port of `shipIGC.cpp:3244-3297`, reuses ShipKinematics).
- `src/Allegiance.Core/Wire/NotSupportedFedMsgs.cs` — remove ShipReset (183).
- `src/Allegiance.Server/Server.cs` — `PeerShipUpdate` + `_peerShipUpdate` +
  `NextCookie`/`EnsurePeerUpdate`.
- `src/Allegiance.Server/Server.Dispatch.cs` — legacy-vs-native detection,
  cookie gate, `ProcessPeerShipUpdate`, `IsLegalPosition`.
- `src/Allegiance.Server/Server.Broadcast.cs` — ~10 s per-peer summary step.
- `src/Allegiance.Server/Server.Combat.cs` / `Server.Lobby.cs` — SET_CLUSTER
  cookie via `NextCookie` (join + warp).
- `src/Allegiance.Server/Server.Lifecycle.cs` — disconnect cleanup.
- `src/Allegiance.Bot/Bot.cs` — `SendOwnShipPose` (test/P508 bridge) + a
  `ShipResets` accumulator (fmid 183).
- `tests/Allegiance.Core.Tests/NotSupportedFedMsgsTests.cs` — registry count
  12→11.
- `tests/Allegiance.Core.Tests/P507OwnShipModelLiveTests.cs` — **new** (+7).
- `docs/reports/P507_NATIVE_OWN_SHIP_SERVER.md` — this report.

## Commit
`master` only, **not pushed** (branch verified with `git branch --show-current`
→ `master`). `git log -1` confirms the `P507:`-prefixed commit is HEAD.
Working tree has only pre-existing untracked files (prompts, verification dirs)
plus the two new P507 files, which are committed.
