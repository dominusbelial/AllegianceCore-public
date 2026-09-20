# P282 — Real ready-check screen (replaces the auto-ready POC hack)

## What this stage does

The MonoGame client no longer auto-readies itself the instant it has a ship.
The old hack (`Game1.cs` sent `SendPlayerReady(true, pid)` unconditionally,
guarded only by `_autoReadySent`) lied to the server's P180 `TryAutoStart` gate
— a mission "started" the moment a single ship existed. P282 removes that hack
entirely and replaces it with the real pre-flight READY-CHECK screen native's
Team Lobby performs: the player toggles their own ready flag, watches a live
same-side roster of ready states, and (as mission owner) can force their side
ready.

This is the second of the two pre-game-flow tasks; P281 (side/civ select)
landed first on master (`583a57e`). Confirmed at start via `git log`.

## Native citations (reference: ~/Development/Allegiance, read-only — not touched)

- **PLAYER_READY(45)** — `Messages.h:380-384` ("client is ready or not"). The
  port's server handler (`Server.cs:2861-2908`) already relays it to every peer
  sharing the sender's side, **including the sender itself** (its own client
  waits on that relay to update its own roster entry — native
  `appmsg.cpp:2297-2312`), remembering `_playerReady[from]` then re-running
  `RecomputeSideReady` (native `PlayerReadyChange`, `fsmission.cpp:5485-5489`).
- **TEAM_READY(46)** — `Messages.h:386-390`; server-generated per-side aggregate
  broadcast on a ready flip (`fsmission.cpp:5449-5471` `SetReady`), whole-mission
  (`GetGroupMission()`), ported at `Server.cs:3503-3515` `BroadcastTeamReady`
  (only on an actual flip — no spam on every recompute).
- **FORCE_TEAM_READY(47)** — `Messages.h:392-397` ("force team to be ready…
  causes TEAM_READY to also be sent"), the side-leader override, handled at
  `Server.cs:2295-2341` (forwarded whole-mission, sets `Side.ForceReady`, then
  `RecomputeSideReady` which bypasses the per-player check —
  `fsmission.cpp:5508-5509`).
- **P180's `TryAutoStart`** (`Server.cs:3539-3554`) — the real "once everyone
  agrees, the game launches" gate, **unchanged** (out of scope). It routes the
  ready-up through the real `STAGE_STARTING` countdown (`StartCountdown` →
  `SET_START_TIME(14)`).

## Client work (all additive / corrective; NO server or Allegiance.Core change)

The server already did everything P282 needed — confirmed this session, no new
server logic. Only a real client was missing.

1. **ClientWorld** (`Allegiance.ClientCore/State/ClientWorld.cs`) — added the two
   aggregate-READY tables TEAM_READY/FORCE_TEAM_READY feed, which the client was
   NOT decoding before:
   - `TeamReadyBySideId` (per-side aggregate ready, last-write-wins)
   - `ForceReadyBySideId` (per-side force flag, from the whole-mission forward)
   - `Apply(TeamReady)` / `Apply(ForceTeamReady)` + switch cases.
   Per-ship ready was already decoded into `TeamRoster.ReadyByShipId`
   (from PLAYER_READY relays) — confirmed, not re-added.
2. **ConnectionSession** — added `case TeamReady.Fmid` / `case ForceTeamReady.Fmid`
   receive cases + `SendForceTeamReady(bool, short sideId)` (the mirror of the
   existing `SendPlayerReady`).
3. **Game1** — new `ClientState.ReadyCheck` between `SideSelect` and `Flying`;
   `UpdateSideSelect` now hands off to ReadyCheck (not Flying) on seating;
   `UpdateReadyCheck` toggles own ready with **Y** (sends real `PLAYER_READY`)
   and, when `world.AmMissionOwner`, forces the side ready with **O** (sends
   `FORCE_TEAM_READY`); hands off to Flying the moment the countdown begins
   (`world.CountdownStartLocalMs` — the client's Mission.Stage is pinned to
   Started by the join burst, so the SET_START_TIME receipt is the real
   "the game is launching" signal, P216). **The auto-ready block is fully
   removed, no fallback.**
4. **New rendering** — `Rendering/ReadyCheckLayout.cs` (pure ASCII rows, same
   discipline as `SideSelectLayout`), `Rendering/ReadyCheckOverlayRenderer.cs`
   (translucent panel, identical conventions), `Rendering/ReadyCheckRoster.cs`
   (pure gather of the screen's inputs from the live world: same-side players'
   ready flags from the real PLAYER_READY relays + teammate EXPORT names + the
   TEAM_READY aggregate).
5. **KeyBindingTable** — added a "READY CHECK (PRE-FLIGHT)" group: **Y** toggle
   ready, **O** force team ready (mission owner); Y/O are otherwise unused.
   Updated the table's group-count test 6→7.

### Mission-owner force gate (brief vs. server)

The brief says reuse P281's randomize-teams `IsMissionOwnerPlayer` gate. The
actual server `FORCE_TEAM_READY` handler gates on "sender is their own side's
current leader" (`frSide.LeaderShipId == frLive.Ship.Id`, `Server.cs:2324`).
These align for the owner-force case: the mission owner is by definition the
owning side's leader (P183), so `AmMissionOwner` implies "leader of one's side"
when the owning side is one's own, and you can only force your OWN side anyway
(the server requires `ft.SideId == own side`). The client therefore surfaces O
under the same `AmMissionOwner` check P281 uses — honest (non-owners never see
it) and server-accepted for the owner. No server change.

## Out of scope (confirmed not touched)

- P180 `TryAutoStart` gating — unchanged; the client now just stops lying to it.
- The side-select screen itself (P281) — unchanged, landed before this task.
- Cross-side per-player ready visibility — not attempted; the roster is
  same-side (server relays PLAYER_READY same-side only) plus the whole-mission
  TEAM_READY aggregate line. Named follow-up if full cross-side rosters are ever
  wanted.
- Native reference source — untouched.

## Live proof (real wire, real clients, real screen)

`/tmp/rdyverify` harness (real in-process `Allegiance.Server` + two real
`ConnectionSession`s on the same side + the real `ReadyCheckOverlayRenderer`
rendered offscreen to PNGs):

```
=== ready-check: same-side two-peer, real start gating ===
seated: A ship 8096, B ship 8097, side 0
BOTH CONNECTED, NEITHER READY -> server stage = NotStarted   (the behavior change)
B ready, relayed to A; A roster = {A:False, B:True}
B ONLY READY -> server stage = NotStarted
A readies too -> mission stage = Starting (n=2 ticks)
TEAM_READY side 0 (A sees) = True ; A roster final = {A:True, B:True}
=== mission-owner FORCE_TEAM_READY ===
non-owner FORCE -> stage = NotStarted (refused)
owner FORCE_TEAM_READY -> stage = Starting ; ForceReadyBySideId[0] = True
```

Rendered screen (vision-confirmed):
- `readycheck_B_ready_only.png`: "TEAM Alpha READY: --", "YOU: NOT READY",
  "[READY] ry_B", "[WAIT ] you (you)" — roster reflecting the real per-player
  toggle.
- `readycheck_both_ready_started.png`: "TEAM Alpha READY: YES", "YOU: READY",
  "[READY] ry_B", "[READY] you" — aggregate flipped real, both ready.
- `readycheck_owner_forced.png`: "TEAM Alpha READY: YES (FORCED)" while BOTH
  individuals show "[WAIT ]" — the force override made the side aggregate-ready
  with no individual readiness, exactly native's force branch.

Equivalent coverage is locked in as tests:
- `tests/Allegiance.ClientCore.Tests/ReadyCheckLiveTests.cs` (2 live tests) —
  mission-not-started-while-unready + starts-when-last-readies; non-owner force
  refused + owner force starts. Both genuinely fail if the `TeamReady` receive
  wiring is removed (verified: red on probe, green after revert).
- `tests/Allegiance.ClientCore.MonoGame.Tests/ReadyCheckLayoutTests.cs` (5) +
  `ReadyCheckRosterTests.cs` (4) — pure layout/roster logic. The roster's
  same-side filter test fails if that filter is disabled (verified).

## Build / test

- Whole-solution build (`dotnet build AllegianceCore.slnx -c Debug`): **0
  warnings, 0 errors**.
- Allegiance.ClientCore.MonoGame.Tests: **441 passed** (was 432 baseline; +9
  ReadyCheck tests).
- Allegiance.ClientCore.Tests: **126 passed** (baseline 124; +2 live).
- Allegiance.Core.Tests: 3 full runs; each hit only the documented
  pre-existing/environmental live flakes — LiveTurretGunnerBindingTests (the
  known persistent machine-state flake) plus a rotating set of the P281-era
  latent timing flakes (StationsUpdateLive, LiveMoney, CargoBayReload,
  GameOverPlayersWire — all named in the P281 reference as "pass in isolation").
  **None can be regressions by P282:** Core.Tests references only
  Allegiance.Core / Server / Bot / Lobby and never ClientCore, so this P282
  change (all in ClientCore/ClientCore.MonoGame) is not even in that assembly's
  dependency graph; the production code under test is byte-identical to
  baseline.
