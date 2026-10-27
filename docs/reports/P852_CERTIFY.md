# P852 — Certify the event-driven mission rules: sides — join requests, locks, ready, alliances

Batch: SIM-0542–0566, SIM-0572, SIM-0573, SIM-0683, SIM-0684 (29 rows, family
"FedSrv mission rules (event-driven)"). Native reference re-grepped this session:
`FedSrv/fsmission.cpp` (4454–5811), `FedSrv/FedSrv.CPP` (6938–7516, 13947–13968),
`FedSrv/fsship.cpp` (1358–1417), `Igc/igc.h` (1123–1467, 4296–4303).

## Family result

| Metric | Before | After |
|---|---|---|
| Weighted implemented (family, parity-scorecard weights) | 0/60 (0% — all 29 rows `partial`) | **53/60 (88.3%)** |
| Rows implemented / partial | 0 / 29 | **26 / 3** |

Remaining `partial` rows, each with the precise surviving branch:

- **SIM-0554 FlushSides** — the team-lobby flush half (move every non-leader to
  `SIDE_TEAMLOBBY`, banned-mask clear, `SetLastSide(SIDE_TEAMLOBBY)`,
  fsmission.cpp:5097–5117) is not built: this port has no team lobby to flush to
  (P195 scope). The auto-accept-off sweep half is now ported (shared with
  `RebalanceSides`, fsmission.cpp:5080–5092).
- **SIM-0556 RandomizeSides** — rank/count balancing + join-request clear +
  auto-accept-off sweep are equal; remaining: the two-phase flush-then-reassign
  shape (no `SIDE_TEAMLOBBY`), the `iMaxImbalance` fewest-players override inside
  the rebalance pick, and the AFK (`GetReady`) filter (n/a without a lobby — every
  non-leader must be re-seated).
- **SIM-0572 SetLobbyIsDirty** — the port's directory heartbeat
  (`LobbyHeartbeat.cs`) reproduces the 10 s dirty coalesce
  (fsmission.cpp:21 `c_flUpdateTimeInterval`) but has no dirty *flag*: changed
  mission details are republished on a fixed cadence from boot-config params;
  runtime mission-param flips (LockLobby etc.) are never advertised and there is
  no dirty-triggered immediate refresh.

## Per-row certification (native branches → port sites → verdict → fix/test)

Verdicts: **equal** (branch mapped to port `file:line`), **fixed** (was
different/missing, fixed this session with a test that fails before the fix),
**n/a** (era/design, reason given), **design** (deliberate, documented).

### SIM-0542 — PickNewSide (fsmission.cpp:4454-4524)

| Native branch | Port | Verdict |
|---|---|---|
| `bAllowTeamLobby && stage > NOTSTARTED → SIDE_TEAMLOBBY` (:4461-4462) | — (no lobby side) | n/a (design) |
| skip inactive sides + banned mask (:4475-4478) | inactivity refused inside `CheckPositionRequest`; no banned-side state | equal / n/a |
| candidate iff `CheckPositionRequest == NA` (:4481) | `AutoPickSide` per-side gate | equal |
| track lowest rank sum (:4488-4492) + fewest players (:4498-4501) | `AutoPickSide` (Server.Lobby.cs), rank floored at 1/player (`GetSideRankSum`, :1352-1369) | **fixed** — port auto-pick previously took the fewest-players side only, ignoring rank |
| imbalance override to fewest-players side (:4506-4516); duplicated `nImbalance != 0x7fff` test (:4509) is a native bug, kept as one check | same condition on `GameConfig.MaxTeamImbalance` | **fixed** (with the rank fix) |

Test: `P852MissionRulesCertifyTests.P1_AutoPick_lowest_rank_sum_then_imbalance_override`.

### SIM-0543 — CheckPositionRequest (fsmission.cpp:4575-4727)

| Native branch | Port (Server.SideIntegrity.cs `CheckPositionRequestCore`) | Verdict |
|---|---|---|
| `!bAllowDefections && STARTED && sideID != GetLastSide() && lastSide != TEAMLOBBY` (:4583-4585) | — | n/a (unportable: the port keeps no `GetLastSide()` state; documented) |
| `!bAllowDefections && STARTED && current side not lobby` (:4587-4589) | NoDefections, now gated on `GameConfig.AllowDefections` (native default **true**, igc.h:1255) with the lobby-park exemption | **fixed** — the port refused EVERY started switch (no knob, wrong default) |
| `bLockSides && sideID != TEAMLOBBY` (:4590-4591) | `mission.LockSides` (no lobby ⇒ every request is a switch) | equal |
| `rgfActive` → DPR_SideGone; STAGE_OVER → DPR_ServerPaused; banned → DPR_Banned; invitation → DPR_PrivateGame (:4596-4611) | first two equal; banned/invitation n/a (no state) | equal / n/a |
| base loss before capacity (:4626-4635); TeamFull (:4638-4640) | equal | equal |
| TeamBalance numeric + newb +2 + bot bypass (:4642-4645) | equal | equal |
| `iMaxImbalance == 0x7ffe` rank-balance (:4646-4671, `#if 0` tail = native dead code) | equal | equal |
| squad game: occupied side → `GetIsMemberOfSquad` else DPR_WrongSquad; empty side → `GetPreferredSquadToLead()==NA` → DPR_CantLeadSquad (:4708-4721; fsship.cpp:1359-1362, 1397-1417) | **added** on `Mission.SquadGame` (P820c gate); side-with-no-squad accepts everyone (`GetIsMemberOfSquad(NA)==true`) | **fixed** — neither gate existed |

Tests: `P852MissionRulesCertifyTests.D1_…` (default allows started switch; explicit
`AllowDefections=false` refuses), `.D2_SquadGame_join_gates_WrongSquad_and_CantLeadSquad`;
`P655GameStateIntegrityTests.H13_Started_defection_fails_lobby_switch_succeeds`
updated to set `AllowDefections=false` (its old premise — refusal by default — was
itself the divergence), `H13_Initial_selection_enforces_capacity_and_started_team_balance`.

### SIM-0544 — RequestPosition (fsmission.cpp:4729-4774) — includes the SIM-0608 pending leg

| Native branch | Port (Server.Dispatch.cs POSITIONREQ + Server.Lobby.cs) | Verdict |
|---|---|---|
| refusal → DELPOSITIONREQ to requester only (:4734-4747) | equal | equal |
| **`RemovePlayerFromSide(QSR_SwitchingSides)` before seating or pending, when not already in the lobby (:4748-4751; SetSide(TEAMLOBBY) :1123)** | **fixed** — the pending branch now parks the switcher on `SideTeamLobby` (-2) via `MoveToTeamLobby` (real succession + payday + whole-mission QUIT_SIDE(126)); `TryJoinSideCore` suppresses the second QUIT_SIDE when the old side IS the lobby park | **fixed** (closes the SIM-0608 pending leg) |
| accept: leaderless && fAutoAcceptLeaders (folded always-on) / bRejoin / rgfAutoAccept → AddPlayerToSide (:4755-4761) | leaderless or `Side.AutoAccept` → `TryJoinSide`; bRejoin = the port's ReconnectGrace rejoin flow (no POSITIONREQ re-join) | equal / design |
| else POSITIONREQ(40) whole-mission + AddJoinRequest (:4763-4772) | equal | equal |

Tests: `P852MissionRulesCertifyTests.R1_Pending_switch_parks_requester_in_team_lobby_then_accept_seats`
(asserts the park, the single QUIT_SIDE, and the accept re-seat without a second
QUIT_SIDE); `P655GameStateIntegrityTests.H13_Pending_approval_parks_requester_and_approval_seats`
(renamed — approval of a lobby-parked switcher after start seats them: both native
no-defections gates carry the TEAMLOBBY exemption and the port's defections default
is native's `true`; the `GetLastSide` refusal for defections-disabled switchers is
the documented unportable branch). Updated for the new faithful flow:
`LivePositionRequestTests` (5 tests), `LiveTeamLobbyToggleTests` (2),
`P678LobbyRelayLiveTests` (1), `P775PlayerInfoMomentsTests.Seat` (see "Ripple" below).

### SIM-0545 — VacateStation (fsmission.cpp:4783-4850) — already certified

Port: `Server.Combat.cs BroadcastStationDestroyed` (P125/P799): every docked
player ejects through the real undock path; ghosts are skipped and re-docked at
another restart-capable base or keep the dead station ("side will be eliminated");
`ForceLoadoutChange` folds into the undock path. Whole-mission STATION_DESTROYED(59)
audience = `GetGroupRealSides()` (FedSrv.CPP:12279). Evidence: `StationCombatLiveTests`,
P799 ghost-relocation tests, `SideEliminationLiveTests`.

### SIM-0546 — AddJoinRequest (fsmission.cpp:4860-4869) — equal

`_pendingJoinRequests[from] = (shipId, sideId)` after the whole-mission
POSITIONREQ(40); `assert(pfsPlayer->GetMission()==this)` holds by construction.
Evidence: `LivePositionRequestTests`, `P852MissionRulesCertifyTests.R1`.

### SIM-0547 — NotifyPlayerBoot (fsmission.cpp:4881-4891) — equal

`BroadcastDelPositionReq(mission, shipId, sideId, DPR_Rejected)` — whole-mission
(`GetGroupMission()`), used by the POSITIONACK reject path and the departure
cleanup. Evidence: `LivePositionRequestTests.Leader_reject_broadcasts_and_leaves_unseated`.

### SIM-0548 — RejectSideJoinRequests (fsmission.cpp:4903-4922) — fixed

Native runs it in `RemovePlayerFromSide` when the last player leaves a STARTED
side (:945-951); the pre-game counterpart turns auto-accept ON (:934-941,
`SetAutoAccept(psideOld, true)` at :1158). The port never did either — pending
requests hung on a leaderless side. Fixed in `HandleLeaderSuccession`'s empty-side
tail (after succession + owner migration, native order): mid-game → every pending
request for the side is removed and booted (`DPR_Rejected`); pre-game →
`AUTO_ACCEPT(52) fAutoAccept=true` broadcast + `SweepAutoAcceptPending`.
Tests: `P852MissionRulesCertifyTests.R3_…` and `.R2_…` (both fail on the unfixed code).

### SIM-0549 — RemoveJoinRequest (fsmission.cpp:4938-4960) — equal

Departure cleanup (`ReleasePeer`: `RemoveJoinRequest(pfsPlayer, NULL)` → boot
broadcast, Server.Lifecycle.cs:2075-2085) + self-cancel/leader-reject relay
(FedSrv.CPP:7484-7516). Evidence: `LivePositionRequestTests.Self_cancel…`,
`Leader_reject_via_delpositionreq_reaches_same_end_state`.

### SIM-0550 — SetAutoAccept (fsmission.cpp:4974-5011) — equal

Dispatch AUTO_ACCEPT(52): leader-of-own-side gate (TrapHackBoot analogue), relay,
`Side.AutoAccept`, and on turn-ON the sweep (`SweepAutoAcceptPending`): entry
removed regardless of outcome, requester re-vetted via `CheckPositionRequest` then
seated or refused (Xynth #195; the refused-to-lobby leg lands in the P852 lobby
park). Evidence: `LiveTeamLobbyToggleTests.Auto_accept_on_seats_pending_requester`,
`P852MissionRulesCertifyTests.R2`.

### SIM-0551 — SetLockLobby (fsmission.cpp:5024-5029) — equal (metadata leg noted)

LOCK_LOBBY handler: owner gate, relay, `Mission.LockLobby` (FedSrv.CPP:6938-6951).
The `SetLobbyIsDirty` leg has no port consequence — see SIM-0572. Evidence:
`LiveTeamLobbyToggleTests.Lobby_lock_from_owner_relays_and_sets_flag`,
`Lobby_lock_from_non_owner_refused`.

### SIM-0552 — SetLockSides (fsmission.cpp:5041-5045) — equal

LOCK_SIDES handler + the real consumer (POSITIONREQ `DPR_SidesLocked` gate,
:4590). Evidence: `Sides_lock_from_owner_relays_and_non_owner_refused`,
`Sides_lock_refuses_side_switch_and_unlock_restores_p184`,
`Multi_mission_lock_sides_scopes_per_mission`.

### SIM-0553 — SetMaxTeamImbalance (fsmission.cpp:5057-5061) — implemented (design)

The native setter is **dead code**: nothing in FedSrv/AdminGame calls it (the
`#autobalance` chat command was never wired; only fslobby.cpp:33 seeds
`mp.iMaxImbalance = 0x7ffe`). The port supplies the parameter from boot config
(`GameConfig.MaxTeamImbalance`), consumed at `CheckPositionRequestCore` and
`AutoPickSide` — behaviour-equal because the in-game mutation path is unreachable
natively. Evidence: `P852MissionRulesCertifyTests.P1`,
`P655…H13_Initial_selection_enforces_capacity_and_started_team_balance`.

### SIM-0554 — FlushSides (fsmission.cpp:5069-5119) — partial

Auto-accept-off sweep: ported (`RebalanceSides` shares it, with per-side
AUTO_ACCEPT broadcasts). Remaining: the flush body — non-leaders to
`SIDE_TEAMLOBBY`, banned-mask clear, `SetLastSide(SIDE_TEAMLOBBY)` — P195 scope
(the RANDOMIZE_TEAMS flush branch is owner-only/NotStarted-only and this port has
no lobby to flush to). Evidence: `P852MissionRulesCertifyTests.F1…`,
`RandomizeTeamsLiveTests`.

### SIM-0555 — GetLockSides (fsmission.cpp:5127-5130) — equal

`Mission.LockSides` consumed at the POSITIONREQ gate. Evidence:
`Sides_lock_refuses_side_switch_and_unlock_restores_p184`.

### SIM-0556 — RandomizeSides (fsmission.cpp:5139-5266) — partial

Equal now: auto-accept-off sweep + broadcasts (fixed this session),
pending-join-request clear (:5183-5196), highest-rank-first reassignment to the
lowest rank-sum side with leader contributions anchored. Remaining: the two-phase
flush-then-reassign shape, the `iMaxImbalance` fewest-players override inside the
rebalance pick, and the AFK filter (n/a without a lobby). Evidence:
`RandomizeTeamsLiveTests`, `P852MissionRulesCertifyTests.F1…`.

### SIM-0557 — SetSideCiv (fsmission.cpp:5268-5284) — equal

`ApplySideCivilization` (Server.TeamCiv.cs): SetCivilization + dev-techs := civ
base techs (sideigc.h:88), station retype (sideigc.h:92-100), per-ship
persist-score reset (:5274-5283, collapsed to neutral — no career persistence,
P193/P583); the `rgCivID` write is commented out natively (:5272) and untouched.
Evidence: `P802TeamCivLiveTests`.

### SIM-0558 / SIM-0559 — Set/GetSideAllies (fsmission.cpp:5288-5295) — equal

`Side.AllyGroup`, written/read by the `UpdateAlliances` port and the allied-winner
naming. Evidence: `LiveAllianceTests`, `P852MissionRulesCertifyTests.Y2`.

### SIM-0560 / SIM-0561 — Set/GetSideActive (fsmission.cpp:5300-5307) — equal

`Side.Active` — set by the SIDE_INACTIVE dispatch and the DeactivateSide paths,
read by CheckPositionRequest / UpdateAlliances / victory checks. Evidence:
`LiveSideInactiveTests.Owner_deactivates_empty_side_flips_active_and_aet_whole_mission`,
`SideEliminationLiveTests`.

### SIM-0562 — DeactivateSide (fsmission.cpp:5318-5430) — fixed

Port: `DeactivateSide` (P455) + `DeactivateSideForPeerDeparture` (P548). Fixed
this session: **`side.Ready = false`** (native `rgfActive = rgfReady = false`,
:5325-5327 — the port left a stale Ready=true) and the **allied-winner naming**
(:5396-5414): a surviving side with allies now produces
"Alpha & Bravo won by outlasting all other sides." (`AlliedSides` = alliance-group
equality, igc.h:4296-4303; native's loop walks all sides with no active filter, so
a dead-but-present ally is named). n/a: `SetTimeEndured` (:5328 — no time-endured
win state in the port). Evidence: `P852MissionRulesCertifyTests.Y2…`,
`SideEliminationLiveTests`, `LiveSideInactiveTests`.

### SIM-0563 — SetForceReady (fsmission.cpp:5441-5448) — equal

FORCE_TEAM_READY handler: leader-of-own-side gate, relay, `Side.ForceReady`,
`RecomputeSideReady`. Evidence: `LivePositionRequestTests.Force_team_ready_true_auto_starts_even_with_unready_teammate`,
`Force_team_ready_false_reverts_to_normal_check`, `Force_team_ready_non_leader_refused`.

### SIM-0564 — SetReady (fsmission.cpp:5461-5472) — equal

`BroadcastTeamReady`: TEAM_READY(46) whole-mission, flip-only
(`fReady != GetReady(iSide)`). Evidence: `LiveTeamLobbyTests.Both_sides_ready_broadcasts_team_ready_and_autostarts`,
`Unready_broadcasts_team_ready_false`.

### SIM-0565 — PlayerReadyChange (fsmission.cpp:5485-5489) — equal

PLAYER_READY handler: own-ship latch + `RecomputeSideReady` (the TEAMLOBBY gate is
moot — no lobby). Evidence: `P852MissionRulesCertifyTests.Y1…`, `LiveTeamLobbyTests`.

### SIM-0566 — CheckForSideAllReady (fsmission.cpp:5498-5529) — fixed

`RecomputeSideReady`: the `nMinPlayersPerTeam` floor was hardcoded to 1;
`GameConfig.MinPlayersPerTeam` (native default 1, igc.h:1333) now carries it —
behaviour-preserving by default. Force-ready bypass and all-players aggregation
were already equal. Also fixed en passant: `TryJoinSideCore` now re-runs the old
side's readiness check after a switcher leaves (`else CheckForSideAllReady(psideOld)`,
:1150-1152 — the deactivation half is P548/P455 scope). Evidence:
`P852MissionRulesCertifyTests.Y1_MinPlayersPerTeam_gates_side_readiness`.

### SIM-0572 — SetLobbyIsDirty (fsmission.cpp:5624-5628) — partial

See family table above. Evidence: `Allegiance.Lobby.Tests` heartbeat cadence tests.

### SIM-0573 — UpdateAlliances (fsmission.cpp:5639-5811) — equal

P190's port reproduces every branch (range filter :5642-5647, group→mask,
inactive-side clear :5675, propagation/merge, `activesides >= 3` gate, solo reset,
stabilise loop, group build, invalid-clear validation :5786-5792, unconditional
CHANGE_ALLIANCES(197) + write-back). Deliberate divergence (P190 report): the
dispatch adds an `IsMissionOwnerPlayer` gate native lacks. Evidence:
`LiveAllianceTests` (five scenarios), `P852MissionRulesCertifyTests.Y2` (group-equality consumer).

### SIM-0683 / SIM-0684 — Create/DestroySideEvent (FedSrv.CPP:13953-13968) — implemented (design)

Native only creates/deletes the `CFSSide` wrapper (message group, invitation
state). The port builds its `Side` model objects wholesale at `Mission.Assemble`
and routes per-side traffic via the Side model + `SendToMissionPeers`; the IGC
seam exists (`IIgcSite.CreateSideEvent`/`DestroySideEvent` default no-op,
`RecordingIgcSite` wraps both for capture). Sides are never destroyed mid-mission
(elimination deactivates). Evidence: `P841MissionRulesCertifyTests.Side_init_seeds_native_InitSide_defaults`,
`SideEliminationLiveTests`, `P598MissionRestartLiveTests`.

## Fixes that ripple (tests updated for the now-native behaviour)

- **Pending switchers park in the team lobby** (SIM-0544/SIM-0608): expectations
  "requester still seated on their old side while pending" were the divergence and
  were updated in `LivePositionRequestTests` (5), `LiveTeamLobbyToggleTests` (2),
  `P678LobbyRelayLiveTests` (1: rejected applicant now at -2),
  `P655GameStateIntegrityTests` (H13 tests reworked).
- **P775 `Seat`**: the bots' post-seat explicit POSITIONREQ — previously a no-op
  pending entry — now legitimately parks the bot; the helper no longer sends it
  (seating always came from the lazy POSITIONREQ(NA) auto-pick drain, so assigned
  sides are unchanged).
- **Defections default**: `P655…H13_Started_defection_fails_lobby_switch_succeeds`
  now opts into `AllowDefections=false` explicitly (native's default is true).

## Native-intent classification + modern/better-approach note

- **Native bugs kept**: `PickNewSide`'s duplicated `nImbalance != 0x7fff` test
  (:4509); `RandomizeSides`' O(n²) rank scan (fine at n≤16); `UpdateAlliances`'
  unconditional CHANGE_ALLIANCES broadcast (idempotent for clients). The
  rank-balance `#if 0` tail in `CheckPositionRequest` (:4676-4711) is native dead
  code and stays unported.
- **Native gaps corrected (deliberate, consistent with P184/P186/P190 policy)**:
  the CHANGE_ALLIANCE owner gate; the port's pending-request map is keyed by
  connection (native trusts ship ids).
- **Modern approach note**: native's `m_listJoinReq`/`rgfAutoAccept`/`rgfReady`
  arrays are mutable mission-wide state mutated from message handlers; the port
  keeps per-side flags on the `Side` model, computes readiness/counts on demand
  from `_peerShips` (one maintenance mechanism), and funnels every side change
  through `TryJoinSideCore` — the same single state machine native expresses as
  `RemovePlayerFromSide` + `AddPlayerToSide`. The lobby park (-2) gives the port a
  real `SIDE_TEAMLOBBY` analogue without inventing a fake side object; a future
  pass could finish SIM-0554/0556 by making that park state the rebalance pool
  (native's literal two-phase flush), which the current architecture now supports.

## Verification

- `dotnet build AllegianceCore.slnx`: **0 errors / 0 warnings**.
- `tools/build-tools.sh`: all 90 tool projects built.
- Rig AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`): clean, no IL2026/IL3050.
- New/affected tests 3× alone: `P852MissionRulesCertifyTests` (9) 3/3;
  `LivePositionRequestTests` + `LiveTeamLobbyToggleTests` + `P655GameStateIntegrityTests`
  (65) 3/3; `P775PlayerInfoMomentsTests` (7) 3/3; `P678LobbyRelayLiveTests` 3/3;
  `QueuedStationGoto_KeepsOwnTarget` 3/3; `Steady_in_range_and_tti_paths_allocate_nothing` 3/3.
- ONE `tools/run-tests.sh --tier fast` (11,097 tests): green except two ledgered
  load flakes under concurrent lanes (`P480Rotation…` — recurrence ledgered in
  `docs/TEST_FLAKES.md`; `Hud18…allocate_nothing` — the known allocation-sampling
  full-run effect), each passing 3/3 alone afterwards.
- Showcase never-parks 5×: **5/5 pass** (4:32–5:39 each).
- `python3 scripts/parity-scorecard.py`: validates; family 88.3% weighted
  implemented (before 0%), no CRLF introduced.

No evidence assets beyond this report (raw TRX logs are disposable test-runner
output; nothing exceeded the 2 MB asset rule or needs `~/allegiance-evidence/`).
