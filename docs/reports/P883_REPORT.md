# P883 — chosen-game joins through native mission cookies

Codex lane. Work started 2026-10-28; final local verification completed
2026-10-29. Changes are left in the working tree; no commits or pushes. All execution
was local; no rig, mini or Mac was contacted. Scratch logs, AOT output, downloaded
Xvfb packages and client configuration are in the git-excluded `.scratch/p883/`.

## Result and native rules

Each assembled server mission has a unique non-NA `Mission.Cookie`. Provisioning
appends the wizard world. LOGONREQ chooses the mission, and each pending connection
retains that mission through POSITIONREQ and the complete join burst. Names are
reserved within the selected mission, including pending names. Both MISSIONDEF
messages and JOINED_MISSION carry that mission’s cookie.

- `FedSrv/FedSrv.CPP:489-493`: NA (`0xffffffff`) chooses the first live mission;
  otherwise resolve the exact cookie. Unknown cookies produce
  DELPOSITIONREQ with `iSide=-1`, `reason=DPR_NoMission` (:495-503).
- `FedSrv/FedSrv.CPP:506-515`: STAGE_OVER refuses with `DPR_ServerPaused`, rather
  than allowing a finished game to be joined.
- `FedSrv/FedSrv.CPP:648-661`, `_Utility/Utility.h:1389-1392`: a non-empty mission
  password is checked after lookup. Provisioned games retain their chosen password;
  refusing at logon cannot be bypassed by sending POSITIONREQ afterward. Existing
  side/invitation/cap/balance gates still run for the selected mission.
- `FedSrv/fsmission.h:229-235`: the cookie belongs to CFSMission. Native game over
  resets that same object’s stage, sides and IGC world
  (`FedSrv/fsmission.cpp:3490-3527`), without resetting `m_misdef.dwCookie`.
  Therefore managed world replacements **keep the cookie**, and pending peers are
  rebound to the fresh world. No first-game substitution occurs on rotation.
- `lobby/client.cpp:390-465` (especially :445-457): creation returns the created
  mission’s cookie. The HTTP adaptation distinguishes queue/claim from readiness:
  a registration-token-authenticated `POST /api/games/{id}/ready` publishes the
  server-assigned cookie (or failure). The creator polls its own request until
  Ready, then joins that cookie. The request is never treated as joinable merely
  because the initial POST was accepted.

The registry carries a `missions[]` array with cookie, name, player count and game
settings for every world; legacy top-level first-game fields remain available.
The client expands the array into independent rows and deduplicates by address,
port **and cookie**. Intro Find Server and the connection list both pass the
selected cookie; direct connections, bots, legacy helpers and skirmish send NA.
Retry/reconnect preserves the selected cookie. Many existing tests and proof
helpers explicitly sent arbitrary former cookie values; their LOGONREQ fixtures
now send NA. Pure wire codec fixtures retain arbitrary round-trip values.

## Wire and reconnect extension

Native layouts are unchanged:

| Message | Mission identity | Layout |
|---|---|---|
| LOGONREQ (1) | `dwCookie` at byte 28 | 1128-byte fixed region plus the four declared variable items (`Inc/Messages.h:36-51`). |
| MISSIONDEF (38) | `dwCookie` at byte 460 | 864 bytes; after the MissionParams and side-name block (`Inc/Messages.h:294-318`). |
| JOINED_MISSION (95) | `dwCookie` at byte 16 | 20 bytes; shipID at 4, dtime at 8, distinct byte cookie at 12 (`Inc/Messages.h:660-665`). |
| POSITIONREQ (39) | `iSide` at byte 4 | Side choice remains native POSITIONREQ (`Inc/Messages.h:320-323`). |
| DELPOSITIONREQ (42) | refusal for the joining ship | 12 bytes; shipID at 4, iSide at 6, 32-bit reason at 8 (`Inc/Messages.h:338-370`). |

P813c reconnect grace remains an explicitly **non-native, opt-in port extension**.
Its per-session nonce is now the separate `LogonRequest.ResumeToken`, encoded only
when nonzero as an 8-byte trailing blob after all native variable items:
ASCII `P883` (DWORD `0x33383850`, little endian), then uint32 token. Header cbmsg
includes those bytes; native fixed offsets and declared items remain identical.
Decoding recognizes the tag only immediately after the last declared item with
exactly eight remaining bytes. With no extension, ResumeToken is zero and no seat
is parked. No native field or new message ID is repurposed. There is no backwards
compatibility with old P813c clients that overwrote dwCookie with their nonce;
those clients need this update. Native/legacy clients sending NA continue to work.

Parked seats are keyed by mission cookie plus case-insensitive character name;
the token must match within that mission. A NA auto-reconnecting client pins the
cookie learned from JOINED_MISSION so retries address the same logical game.
LOGONACK’s byte Cookie and SET_CLUSTER’s rotating gameplay cookie are distinct
from the DWORD lobby identity; the former no longer echoes a mission ID’s low byte.

## Additional isolation fixes

The new two-world test exposed existing assumptions beyond the slot-0 grep:

- Serial and parallel teammate groups now use `(mission cookie, side)`.
- Static CLUSTERINFO cache entries are per cookie; rotation/reaping removes them.
- Cross-side ship identity, mine, discovery and status sweeps construct their
  audience per mission. Sweep-cache audiences also include the numeric mission
  cookie, and reaping drops their scoped entries without retaining the old world.
- Stations, asteroids and probes update batches use only the recipient mission.
  Sector-threat history is keyed by cookie and side; collision/side-event viewers
  are restricted to the subject’s mission.
- Pending roster refreshes, player-chat audiences and duplicate-name admission use
  the selected mission; the same pilot name can exist in two games and resume
  independently. Pending creators keep their selected game alive while choosing
  a side (native AddPlayerToMission precedes side choice, fsmission.cpp:406-411);
  failed mission admission releases the pending reservation.

Native audience sources: the mission-local GetShips loop is
`FedSrv/fsmission.cpp:3958-3968`; player chat forwards to GetGroupMission at
`FedSrv/FedSrv.CPP:3382-3388`; per-side world batches are
`FedSrv/fsship.cpp:817-915`. These constrain both the serial and parallel paths.

## Original first-slot lookup inventory

These are **all original grep sites**, including comments, test seams and
single-player hosts. Line numbers identify the dispatch baseline, before edits.
Remaining first-slot uses below are deliberately not per-connection join lookups.

| Original site | Resolution |
|---|---|
| `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:208` | Retained: the embedded training host creates exactly one mission; no remote per-connection selection. |
| `src/Allegiance.SinglePlayer/SkirmishObservation.cs:42` | Retained: embedded single-game observation lifecycle; not a shared-server connection lookup. |
| `src/Allegiance.SinglePlayer/SkirmishObservation.cs:74` | Retained: embedded single-game observation snapshot; not a shared-server connection lookup. |
| `src/Allegiance.Server/Server.MissionParams.cs:713` | Retained: TestLobbyParams is a first-mission test seam, not a connection lookup. |
| `src/Allegiance.Server/Server.Callbacks.cs:14` | Retained: sender.Mission owns routing; senderless callbacks fall back only when exactly one mission exists. |
| `src/Allegiance.Server/Server.Provisioning.cs:41` | Removed: availability permits another mission while configured games are occupied (research-only showcase still declines provisioning). |
| `src/Allegiance.Server/Server.Broadcast.cs:223` | Retained: legacy first-game advertisement change detection; the new missions[] snapshot includes every game. |
| `src/Allegiance.Server/Server.Broadcast.cs:225` | Retained: legacy advertisement identity; never selects a connection mission. |
| `src/Allegiance.Server/Server.Broadcast.cs:228` | Retained: legacy top-level Game settings; missions[] carries per-game cookie, name, count and settings. |
| `src/Allegiance.Server/Server.Lobby.cs:631` | Documentation corrected: joins address the logical mission by stable cookie. |
| `src/Allegiance.Server/Server.Lobby.cs:2364` | Documentation corrected: pending peers retain their selected mission. |
| `src/Allegiance.Server/Server.Lobby.cs:2365` | Removed first-slot gate: refresh pending peers filtered by PendingSideSelect.Mission. |
| `src/Allegiance.Server/Server.Lobby.cs:4071` | AssignShip now takes the explicitly selected Mission. |
| `src/Allegiance.Server/Server.Lobby.cs:4325` | CompleteSideSelection uses pend.Mission and rechecks admission before spawning. |
| `src/Allegiance.Server/Server.Lobby.cs:4496` | CompleteJoinAndLaunch requires live.Ship.Mission; no first-game fallback. |
| `src/Allegiance.Server/Server.Lobby.cs:4623` | First MISSIONDEF core version uses live.Ship.Mission (goalMission). |
| `src/Allegiance.Server/Server.Lobby.cs:4641` | First MISSIONDEF lives uses goalMission. |
| `src/Allegiance.Server/Server.Lobby.cs:4660` | Join diagnostic uses goalMission.Core.Version. |
| `src/Allegiance.Server/Server.Lobby.cs:4711` | Second MISSIONDEF side table uses goalMission.Sides. |
| `src/Allegiance.Server/Server.Lobby.cs:4717` | Second MISSIONDEF core version uses goalMission. |
| `src/Allegiance.Server/Server.Lobby.cs:4732` | Second MISSIONDEF lives uses goalMission. |
| `src/Allegiance.Server/Server.Lobby.cs:4939` | SET_MONEY and research/briefing side lookup uses goalMission.Sides. |
| `src/Allegiance.Server/Server.Lobby.cs:5039` | TREASURE_SETS uses goalMission.Core. |
| `src/Allegiance.Server/Server.Lobby.cs:5077` | CLUSTERINFO derives from goalMission.Sectors and caches per cookie. |
| `src/Allegiance.Server/Server.Lobby.cs:5117` | Cluster exports enumerate goalMission.Sectors. |
| `src/Allegiance.Server/Server.Lobby.cs:5122` | Cluster export diagnostic enumerates goalMission.Sectors. |
| `src/Allegiance.Server/Server.Lobby.cs:5159` | Discovery join burst uses goalMission, the seated ship’s mission. |
| `src/Allegiance.Server/Server.Lobby.cs:5489` | SendSideRoster resolves ConnectionMission(to): live ship or pending selection. |
| `src/Allegiance.Server/Server.Dispatch.cs:227` | USEAUTH duplicate-name lookup compares the selected joinMission. |
| `src/Allegiance.Server/Server.Dispatch.cs:231` | USEAUTH removal targets the selected joinMission. |
| `src/Allegiance.Server/Server.Dispatch.cs:235` | Live and pending duplicate-name reservations are scoped to joinMission. |
| `src/Allegiance.Server/Server.Dispatch.cs:1701` | Documentation corrected: pending peers belong to the selected mission. |
| `src/Allegiance.Server/Server.Dispatch.cs:1702` | Removed first-slot gate: refresh the changed mission’s pending rosters. |
| `src/Allegiance.Server/Server.BroadcastTestSeams.cs:21` | Retained: TestMission is deliberately the first-mission test fixture. |
| `src/Allegiance.Server/Server.BroadcastTestSeams.cs:36` | Retained: TestSectors is deliberately the first-mission test fixture. |

## Verification and evidence

| Check | Result |
|---|---|
| Final solution build | PASS, 0 warnings / 0 errors (`build-final.log`). |
| All tool projects | PASS, all 94, 0 warnings / 0 errors (`build-tools-final.log`). |
| Server Native AOT, Release linux-x64 | PASS; executable and symbols in `.scratch/p883/aot/` (`aot-final.log`). NuGet HTTP cache was redirected to scratch because the default cache is read-only. |
| New server tests, three isolated runs | PASS, 8/8 ×3 (`new-server-1.log` through `new-server-3.log`). |
| New client tests, three isolated runs | PASS, 4/4 ×3 (`new-client-1.log` through `new-client-3.log`). |
| Affected core tests after final edits | PASS, 87/87: economy/admission fixtures, reconnect, HTTP provisioning, parallel broadcast identity and sweep coverage (`affected-core-final.log`). |
| Affected ClientCore tests | PASS, 7/7: cache retirement and auto-reconnect (`affected-client-final.log`). |
| Client ready-cookie flow and shared UI notices | PASS, 5/5 (`affected-ui-final.log`). |
| ONE Fast tier invocation | Completed: 11,688 executions, 11,678 passed, 7 failed, 3 skipped. See the failure accounting below; no full-tier repeat. |
| Showcase never-parks | PASS, 5/5 explicit NA runs (`showcase-1.log` through `showcase-5.log`); each completed both matches. |
| Parity scorecard / glossary links / whitespace | PASS: all matrices validate (856 sim rows); 322 glossary paths exist; `git diff --check` is clean. |

The one Fast run was taken before the final fixture/admission follow-ups. Six of
its seven failures were stale private-fixture assumptions introduced by this
change: two `P653EconomyLoadoutIntegrityTests` reflection calls needed the explicit
mission argument; two `P655GameStateIntegrityTests` cases needed the pending
constructor’s mission argument; two `P682MissionStateTests` cases needed to check
invalidation of per-game graph bytes rather than replacement of a single byte-array
field. All six were corrected and are green in the focused final reruns above.
The seventh is the already-ledgered, pre-existing
`NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
phase-1 failure at line 179 (see `docs/TEST_FLAKES.md`, P872). The full Fast result
is retained as **red**, not relabelled green; the requested single invocation was
not repeated. Its infrastructure checks passed, and it reported no timing offenders.

The final new-test runs additionally cover the native password refusal, isolated
player chat and a creator spending more than ten simulated seconds on side choice.
The explicit NA showcase runs exercise the existing bot path independently.

- Local raw evidence: `.scratch/p883/` (excluded from git).
- Real-window proof: `tools/p883-join-proof/`. It populates the production game
  list from two live mission snapshots, invokes the production selected-row join,
  and checks the selected second game’s cookie and three-team roster. Captures:
  [Create-game capacity](P883_assets/games.png),
  [Selected-game teams](P883_assets/selected-teams.png).
  These screens are style-independent: Classic and Modern produced byte-identical
  pairs. P883b replaces the old game-list capture with the changed create-game
  screen to keep exactly two PNGs. They were generated under `xvfb-run -a`;
  configuration stayed in scratch. Both retained captures were visually reviewed.
- Parity: SIM-0539 corrected; SIM-0853/0854/0855/0856 added; SIM-0474’s reconnect note
  now names the extension; the stale JOINED_MISSION codec-only wiring row is fixed.

**Claude’s 200v200 rig run is the acceptance test for the per-connection lookups.**
This Codex lane does not claim that acceptance run and did not contact the rig.

## Native-intent footer

Mission cookies, per-connection world selection, NA fallback, unknown/finished
refusals, mission password admission, reset identity and joining a created mission
by its cookie are **B: executable native behaviour**. The native sources above were
read and their line numbers rechecked locally. No behaviour is excluded as n/a,
and no row is closed on a “same outcome” or “port chose differently” argument.

P813c reconnect grace is the pre-existing opt-in port extension; moving its nonce
to its own tagged field restores native dwCookie semantics. HTTP registration,
queue/claim/ready acknowledgements and source-generated JSON adapt the lobby
transport without changing which mission the player selected. No new service,
store, port or environment variable was introduced; compose configuration is
unchanged. Both HUD styles share the selection and refusal model.


## P883b — per-server game capacity and media tidy (2026-10-29)

P883 is included in this working tree, followed by the capacity fix. The required
`git cherry-pick --no-commit 521e7fde` could not create its index lock because this
worktree's Git metadata is mounted read-only. The same commit's binary patch was
applied to the working tree; its TEST_FLAKES addition was appended separately
because the base already contains newer ledger entries. No commits, pushes,
stashes or remote-machine access were performed. The verification section above
records the original P883 run; the results below are this follow-up's local gates.

### Capacity, listing and admission

- `ServerConfig.MaxGames` / JSON `maxGames` defaults to **99**, matching
  `g.cMaxGames` and registry `MaxGames` (`FedSrv/FedSrv.CPP:9560-9562`,
  `FedSrv/FedSrv.H:346`). Values below 1 are rejected for both direct configuration
  and JSON loading. The example config and server-key documentation describe it.
- All live missions count, including configured worlds. `CanHostPlayerGame`
  declines at `count >= MaxGames`; `ProvisionPlayerGame` rechecks on the sim thread
  before decoding or assembling anything. The same full-server comparison is
  native `WinTrek/CreateGameDialog.h:20`. A claim that raced the predicate reports
  **Failed**, with no cookie and no appended mission/config entry, through P883's
  existing ready acknowledgement. Research showcases still refuse provisioning.
  Configured worlds are retained even if an operator sets the cap below their
  initial count; additional creation remains blocked until capacity returns.
- The heartbeat advertises `currentGames` and `maxGames`, mirroring native's
  `MaxGames` logon (`FedSrv/FedSrv.CPP:8107,8850`), lobby storage
  (`lobby/server.cpp:114`) and server-list fields (`lobby/client.cpp:361-362`).
  The mission snapshot is initialized before heartbeat startup and refreshed after
  each tick, so the first listing, creation and empty-game reaping carry the right
  count. The registry preserves both fields on registration and heartbeat refresh,
  and exposes them on `/api/servers` and `/api/games/catalog` server choices.
  Omitted legacy-directory maximums use native lobby's constructor default **20**
  (`lobby/server.cpp:463`); this server explicitly advertises its configured cap.
- The shared wizard prints **current/max**, matching
  `WinTrek/CreateGameDialog.h:87`, and refuses a full server with
  “That server is full. Choose another server.” (`:20`). The HTTP create endpoint
  also rejects a server already advertised as full. A stale catalog or simultaneous
  claim is covered by the final server-side recheck. The creator stays on the
  wizard and sees “Server did not create the game.” on a failed ready poll.

### Media and native tracking

Exactly two PNGs remain: [games.png](P883_assets/games.png) (the changed Create Game
screen, showing **2/2** and the refusal) and
[selected-teams.png](P883_assets/selected-teams.png) (the original selected-game
team screen). Find Server, Create Game and side selection are style-independent.
The four style-named duplicate files are removed. Only the changed Create Game
screen was re-captured, using `tools/p883-join-proof --capacity` under
`xvfb-run -a`; Classic and Modern captures were asserted byte-identical before
retaining one. Xvfb was downloaded and extracted into scratch because the host did
not have it installed. Configuration and temporary captures stayed in scratch.
Both retained images were visually reviewed; they contain game UI only.

SIM-0857 records default/configuration, current/max advertisement and cap admission
with native citations and executable tests. LOB-0029 now acknowledges the count
fields but remains **partial** because its broader computed-percent-load rule is
outside this change. No native behaviour is excluded as n/a. No new service,
store, port or environment variable was introduced.

### Follow-up verification

Codex stopped at "model at capacity" before its gate finished; Claude ran it on master after the
merge (92f050d3, base 72a82ee9):

- `dotnet build AllegianceCore.slnx`: 0 warnings; `scripts/parity-scorecard.py` passes.
- Filtered P883/P883b/P871b/lobby/create-game/reconnect/logon tests 3×: one deterministic failure,
  `LobbyHeartbeatTests.LobbyRegisterRequest_serializes_to_byte_identical_wire_shape` — its expected
  wire shape predated the new `currentGames`/`maxGames` fields. Fixed at merge (expected shape
  includes both; asserts `maxGames` 99); the class passes 8/8 ×3.
- One Fast sweep: `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` failed 3/3 alone
  because P877 (merged after this lane's base) logged on with `DwCookie = 877`, which a P883 server
  correctly refuses with DPR_NoMission. Fixed at merge (NA cookie); P877 passes 11/11 ×3. The
  remaining failures are ledgered (`AddPartTests…`, `ShopLauncherMountNumberingLiveTests…`, both
  3/3 alone; `NpcWingmanDefendLiveTests…` line 179, pre-existing).
- Showcase never-parks (P730 two-match) ×5: 4/5; the failing run's text was not captured, and three
  further runs passed 3/3 (4m24s–5m17s). Ledgered.
- Training Fast + Live (both client projects): pass.

### Deployment

Deploy P883 clients and bots together with the server. P883 changes the meaning of
`LOGONREQ.dwCookie` to native's mission cookie: a pre-P883 client or bot sending
an arbitrary value (for example an older MCP bot's `0xC0FFEE`) is refused with
**DPR_NoMission** by a P883 server. NA (`uint.MaxValue`) selects the first mission.
There is no compatibility shim. The independent ResumeToken extension remains the
opt-in reconnect mechanism described above.
