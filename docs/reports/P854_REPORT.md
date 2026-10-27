# P854 — Private games by invitation: per-side invitation lists, join gate, lobby wiring (SIM-0491, SIM-0492, SIM-0766)

**Branch:** `hermes/p854` (public `AllegianceCore` master worktree). Changes left in the working tree — not committed, not pushed.

**Rows moved to `implemented`:** SIM-0491 (`CFSMission::AddInvitation`), SIM-0492 (`CFSMission::IsInvited`), SIM-0766 (`GetInvitations`).

---

## 1. Why

Native games can require an invitation: a mission whose `misparms.nInvitationListID != 0` loads per-side invitation lists
(asynchronously, from SQL) and refuses players who are not invited — both at mission entry and at the per-side join gate. The
port had no such feature, so the three rows were `missing` and a host could not run a private game.

Native contracts transcribed (re-grepped on `~/Development/Allegiance/src/`, read-only):

| Behavior | Native source |
|---|---|
| Mission records the invitation-list id at construction; `RequiresInvitation() == (m_nInvitationListID != 0)` | `FedSrv/fsmission.cpp:217-222`, `fsmission.h:407-411` |
| Async list load kickoff | `FedSrv/FedSrv.CPP:1307-1318` `GetInvitations` |
| Async completion feeds `CFSMission::AddInvitation` per row `(iTeam, szSubject)` | `FedSrv/FedSrv.CPP:785-801` `GotInvitationList` |
| Mission-level `AddInvitation` (bounds guard, delegates per side) | `FedSrv/fsmission.cpp:1168-1174` |
| Per-side `CFSSide::AddInvitation` (squad game sets side name; push-back) | `FedSrv/fsside.cpp:69-86` |
| Per-side `CFSSide::IsInvited` (squad game → match squad memberships; else callsign; `_stricmp` case-insensitive) | `FedSrv/fsside.cpp:34-64` |
| Mission-level `IsInvited` (any side invites the player) | `FedSrv/fsmission.cpp:1182-1192` |
| **Mission-join gate** — refused with `DELPOSITIONREQ(DPR_PrivateGame)`, `iSide=NA` | `FedSrv/FedSrv.CPP:581-594` |
| **Per-side join gate** — refused with `DPR_PrivateGame` | `FedSrv/fsmission.cpp:4605` (inside `CheckPositionRequest`, fsmission.cpp:4575-4649) |
| Client refusal text ("You are not on the invitation list for this team.") | `WinTrek/teamscreen.cpp:3604-3605` |

The port's client already renders `DelPositionReqReason.PrivateGame` with that exact native text
(`src/Allegiance.ClientCore.Game/Rendering/LobbyStatusLayout.cs`), so the refusal reason needs no client change.

## 2. What changed

### 2.1 Model — `src/Allegiance.Core/Model/Mission.cs`
- `Mission.RequiresInvitation` (bool) — port of native `RequiresInvitation()`/`nInvitationListID != 0`.
- `Mission._sideInvitations` (per-side subject lists) + `Mission.AddInvitation(short sideId, string subject)` —
  port of `CFSMission::AddInvitation` → `CFSSide::AddInvitation` (fsmission.cpp:1168-1174, fsside.cpp:69-86), including
  native's out-of-range-side drop.
- `Mission.LoadInvitations(...)` — the port's synchronous-config substitute for native's async SQL load
  (`GetInvitations`/`GotInvitationList`, FedSrv.CPP:1307-1318/785-801). Additive/idempotent so a later async source could
  re-seed without breaking the gates.
- `Mission.SideInvitations` (read-only view) for gates/tests.
- `Mission.IsSideInvited(sideId, playerName, squadNames)` — pure port of `CFSSide::IsInvited` (fsside.cpp:34-64): a **squad
  game** matches each invitation against the player's active squad memberships, a **non-squad** game against the player's
  callsign; both case-insensitive (`_stricmp`). Empty/absent side list (incl. the not-yet-loaded window) invites nobody.
- `Mission.IsInvited(playerName, squadNames)` — port of `CFSMission::IsInvited`, "any side" (fsmission.cpp:1182-1192).

### 2.2 Config — `src/Allegiance.Server/ServerConfig.cs` (`GameConfig.SideInvitations`)
Per-game `Dictionary<short, List<string>> SideInvitations` (JSON key `sideInvitations`). Key = side/team index; value = the
invited subjects for that side. A non-empty side list makes the game require an invitation.

**Why this shape (the "say which persistence pattern" ask):** native's store is a SQL table of `(listid, iTeam, szSubject)`
rows fetched once at mission construction. The port has no SQL persistence layer for mission state; its config is
`server.json` (`ServerJsonContext` source-generated, AOT-safe). A per-game dictionary of side→subject-lists is the simplest
config that maps 1:1 to native's per-side `m_Invitations` vectors and to the `(iTeam, szSubject)` rows, and it is
asynchronous-safe: the mission is marked `RequiresInvitation` from config and the lists are loaded at assemble. Because the
config is already in memory, the load is effectively instantaneous, but the gates are written to tolerate the empty
pre-load state exactly as native does (refuse `DPR_PrivateGame`) — so a future async source cannot regress the join
semantics. `LoadInvitations` is additive, mirroring native's later-arriving rows.

### 2.3 Gates — `src/Allegiance.Server/Server.SideIntegrity.cs` and `Server.Lobby.cs`
- **Per-side gate** (`CheckPositionRequestCore`): inserted before the capacity/balance checks —
  `if (mission.RequiresInvitation && !mission.IsSideInvited(sideId, playerName, squadNames)) return PrivateGame;` — the exact
  native position (fsmission.cpp:4605 precedes the player-count block; after the banned-side check, which the port lacks).
  Reached for every seat: initial side-select, auto-assign, and side-switch / position-ack. The player's callsign + active
  squads are threaded in from the peer (`_peerNames`/`PeerSquads`), anonymous/bot → empty (no match, matching native).
- **Mission-join gate** (`CompleteSideSelection`, before side selection): a player invited to no side is refused with
  `DELPOSITIONREQ(DPR_PrivateGame)` (`iSide = NA`), staying pending for retries — native FedSrv.CPP:581-594. Only reached
  when `RequiresInvitation`; mirror of the mission-level `IsInvited` (fsmission.cpp:1182-1192).
- Rotation migration / reconnect-resume re-seats are intentionally **ungated**: those players already passed the gate when
  they first joined, and the fresh mission carries the same config lists (native likewise does not re-kick players on a
  resume).

### 2.4 Assembly wiring — `src/Allegiance.Server/Server.Lifecycle.cs`
At mission assemble (alongside the P820c `mission.SquadGame = g.SquadGame` line):
`mission.RequiresInvitation = <any non-empty side list>` and `mission.LoadInvitations(g.SideInvitations...)`.

### 2.5 Test seams — `src/Allegiance.Server/Server.BroadcastTestSeams.cs`
Two inert-in-production internal seams (`TestCheckPositionRequest`, `TestAdmittedAtMissionJoin`) so loopback tests can
drive the two gates without staging a live position request (same pattern as the P820c `Test*` seams).

### 2.6 Tests — `tests/Allegiance.Core.Tests/P854PrivateGameInvitationTests.cs` (10 tests)
- **Model** (`P854PrivateGameInvitationModelTests`): public game invites nobody; configured invitations flag the mission and
  load per side; uninvited callsign not invited anywhere; per-side gate only allows the side that invited you;
  case-insensitive matching (`_stricmp`); squad game matches squad names (not callsigns); **list-not-yet-loaded** refuses
  everyone until the lists arrive; mission-level `IsInvited` any-side.
- **Server gates** (`P854PrivateGameInvitationServerGateTests`, artwork-backed, `[Collection("LiveNetwork")]`):
  per-side gate allows the invited side and refuses an uninvited side with `DPR_PrivateGame`; squad-mode per-side gate
  matches identity squads; mission-join gate admits anyone invited to at least one side and refuses the rest.

All 10 **fail before the change** (the members/gates did not exist; the server tests' `PrivateGame` assertions returned no
refusal and the seams did not exist).

### 2.7 Docs
- `docs/parity/sim-rules-matrix.csv`: SIM-0491, SIM-0492, SIM-0766 → `implemented` (only those three lines; LF preserved,
  `grep -c $'\r'` = 0).
- `README.md`: new "Private games (by invitation, P854)" section documenting `games[].sideInvitations` + the squad/callsign
  matching rule.
- `src/Allegiance.Server/server.example.json`: added `"sideInvitations": {}` (empty = public) to the example game.
- This report.

### 2.8 Lobby / client (scope item 3)
Native's `LOBBYMISSIONINFO` carries **no** private/invitation flag (checked `SendLobbyMissionInfo`, fsmission.cpp:3936-4100:
`fSquadGame` etc. are the only flags; there is no `fInviteOnly`/`fPrivate`). So native does not mark a private game in the
lobby — the game is discoverable and join is gated at entry. The port mirrors that: nothing to advertise, and no mission-info
flag is added. A host sets the list from `server.json` (`sideInvitations`). In-game **UI editing** of the invitation list
(native has no such UI either) is out of scope and noted for **P853**.

## 3. Verification (light gate)

| Step | Result |
|---|---|
| Whole-solution build (`dotnet build AllegianceCore.slnx -c Release`) | 0 warnings / 0 errors |
| `tools/build-tools.sh` | all 90 tool projects built, 0/0 |
| Rig AOT publish (`dotnet publish src/Allegiance.Server/... -c Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aotpub`) | clean; native binary produced; no IL2026/IL3050 (new config type is source-generated JSON) |
| New tests alone ×3 | 10/10 ×3 (200/196/204 ms) |
| `tools/run-tests.sh --tier fast` (one run) | 11146 tests; **3 pre-existing load flakes** unrelated to P854 — see below |

**Fast-tier failures analysis.** The three failures were `P857NpcWarpParityTests.Player_is_held_by_arrival_latch_when_guard_enabled`,
`P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect`, and
`ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over`. None touch invitations:
P854's gates are inert on every existing suite because `RequiresInvitation` defaults to `false` (no `sideInvitations`
configured) — the gates short-circuit immediately. Re-run alone, P857 and P480 pass; `ConquestTerritoryLiveTests` is a
documented intermittent live-wire flake (see `docs/TEST_FLAKES.md`, multiple entries incl. "existing intermittent quarantine
behavior remains, including isolated failures") and passed 2/2 alone here. No P854 test failed in the fast tier.

## 4. Native-intent classification

**Faithful port.** Every native rule above is mirrored: the mission is marked `RequiresInvitation` at construction and the
per-side lists are independent of that flag (empty ⇒ nobody invited), the two gates fire exactly where native's do with
native's reason (`DPR_PrivateGame`) and, for the mission-join gate, `iSide=NA`; matching is case-insensitive and
squad-aware exactly as `CFSSide::IsInvited`. The only deliberate divergence is the data source: native loads the lists
from SQL asynchronously; the port loads them from `server.json` at assemble (synchronously, because the config is already
in memory). This is a substitution of the persistence mechanism, not of the observable contract — the "not-yet-loaded"
semantics are preserved and pinned by a test.

## 5. Modern / better-approach note

- **Config over SQL:** embedding per-side invitation lists in the game config removes a SQL round trip and a latent race
  (a player joining in native's pre-load window is refused even though they are invited — a known native quirk). The port
  eliminates the race because the source is synchronous; the refusal semantics remain tested for correctness and portability.
- **Identity-based matching:** unlike native's name-string matching, the port already carries verified squad memberships in
  the signed join token (`PeerSquads`). The invitation gate reuses that identity rather than re-querying, and could later
  match on the stable account `Sub` instead of the mutable callsign — a stricter, spoof-resistant improvement over native.
- **Per-side granularity:** native keys on a single `nInvitationListID` whose rows split by team; expressing lists directly
  per side in config is clearer for operators and needs no list-id indirection, at no loss of fidelity.
- **Async-ready loader:** `LoadInvitations` is additive, so an admin console (future P853) could push updates at runtime
  without disturbing an in-flight join; the gates already cope with partial lists.
- **Future UI (P853):** in-game/host UI to edit an invitation list mid-game is not implemented; the gates and model already
  support runtime mutation, so the follow-up is a UI + admission-path concern only.

## 6. Unfinished / follow-ups

- In-game invitation-list **UI** editing — deferred to **P853** (model/gates are already runtime-mutable).
- No dedicated end-to-end bot flow that visibly seats an invited pilot and boots/refuses an uninvited one through the live
  `POSITIONREQ` wire path (covered here at the gate/model level; a Live-tier showcase could be added if desired).
- Screenshot/visual parity: none applicable (no client rendering change).

Evidence root (raw logs, outside the repo, per the evidence-size rule): `~/allegiance-evidence/P854/` — `test1..3.log`
(P854 tests ×3), `slnbuild.log` (solution build), `buildtools.log`, `aot.log`, `fasttier.log`, `flakecheck.log`
(pre-existing flake re-runs).
