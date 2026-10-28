# P866 — parity: event audiences, lobby publication and connection bookkeeping

**Base:** public master at dispatch (`b37b4efe`), worktree `allegiance-pi-p866`.
**Scope:** 12 `docs/parity/sim-rules-matrix.csv` rows (SIM-0652, 0655, 0656, 0659,
0660, 0537, 0572, 0579, 0580, 0582, 0583, 0608). No commit/push; changes are in
the working tree.

Verification summary, per-row table, classification and the scorecard are at the
end. Raw TRX/log receipts: `~/allegiance-evidence/P866/` (fast-tier logs from both
runs); small summaries in `docs/reports/P866_assets/`.

---

## 1. Event audiences — one shared fix (SIM-0652, 0655, 0656, 0659, 0660)

Native sends the KILL/MINE notices to `CFSMission::GetGroupRealSides()`
(`FedSrv/fsmission.h:361`), the mission's "All real teams" group. That group's
membership is maintained by `CFSShip::SetSide` (`FedSrv/fsship.cpp:1073-1078`):
a connection joins when seated on a side and is **removed** while the player is on
`SIDE_TEAMLOBBY`. So "whole mission" in native is *every seated player of the
mission* — never a team-lobby connection, never another mission's connection.

The port had three different ad-hoc audiences (owner side, owner side + export
latch, recorded viewers). One shared helper now expresses the native group:

* `src/Allegiance.Server/Server.FireAudience.cs` — `RealSideAudience(Mission?)`:
  every `_peerShips` entry of the mission whose `Ship.SideId` is a real side
  (`null` = never seated and `SideTeamLobby` (-2) = parked are both excluded,
  matching `fsship.cpp:1076`), plus `MissionOfSector(Sector)` so a notice is keyed
  by the **object's** mission even when its launcher has since warped into another
  game and changed side (P697's ghost battle).

Applied:

| native | port before | port now |
| --- | --- | --- |
| `KillProbeEvent` → `GetGroupRealSides()` (`FedSrv.CPP:12479-12482`) | owner side ∪ recorded viewers | whole mission's real sides (`BroadcastProbeDestroyed`, `Server.Combat.cs:820-833`) |
| `KillMissileEvent` → `GetGroupRealSides()` (`FedSrv.CPP:12520-12521`) | recorded viewers (same+cross side) | whole mission's real sides (`BroadcastMissileDestroyed`, `Server.Combat.cs:342-395`); the port-only 205 clear lane and the server-emitted blast VFX stay with the peers that KNEW the missile, because native's `Explode` is a client-side reaction to a notice it can resolve (`clintlib/appmsg.cpp:1795-1804`) |
| `KillMineEvent` → `GetGroupRealSides()` (`FedSrv.CPP:12536-12539`) | owning side ∪ cross-side export latch | whole mission's real sides (`BroadcastMineDestroyed`, `Server.Combat.cs:664-691`) |
| `MineAsteroidEvent` → per-side `GetAsteroidCurrentEye` (`FedSrv.CPP:12454-12471`) | whole mission | peers whose side has an **eye on the rock** (`OnMine`, `Server.Combat.cs:2628-2673`), the same live per-side state `ASTEROIDS_UPDATE(179)` reads; a no-eye side learns from its next per-side batch |
| `KillAsteroidEvent(asteroid, side, false)` from `BuildStation` (`FedSrv.CPP:11429-11457`; overload `:12424-12431`) | every non-owner, non-stale peer **including team-lobby peers** | the informed, seated sides only (`OnStationConstructed`, `Server.Research.cs:74-90`): `Station.StaleRockSides` is exactly native's not-informed complement (`Sector.BuildStation`), a station-visible side gets the pop with its export, a stale side gets nothing until the third overload fires (`Sector.ResolveConsumedRockVisibility` → `BroadcastStaleAsteroidDestroyed`), and unseated peers are skipped |

A peer that never received the creation message ignores the unknown id (native
clients: `clintlib/appmsg.cpp:1808-1819`, `:1822-1829`, `:1795-1804`), so the
whole-mission notice does not invent objects on unaware clients.

## 2. Lobby — native mission info + the dirty flag (SIM-0537, SIM-0572)

**SIM-0537.** `CFSMission::QueueLobbyMissionInfo` (`FedSrv/fsmission.cpp:3983-4096`)
now has a port for every field the port models, carried end-to-end through the
HTTP directory (server → `Allegiance.Lobby` DTO → client mirror):

* `SnapshotLobbyGameParams` (`src/Allegiance.Server/Server.MissionParams.cs`, run
  once per tick inside the existing stats lock) computes the player/noat counts
  (`:4041-4043`), the slot-availability scan with its branches — joiner-policy /
  stage / lock pre-gate (`:4047-4048`), per-team cap (`nMaxPlayersPerTeam −
  playersOnSide`), the not-started / base+active gate (`:4052-4073`), and the
  guaranteed bit on side auto-accept or a leaderless side
  (`fAutoAcceptLeaders`, folded always-on exactly like `RequestPosition`'s fast
  path) — the armed countdown offset (`misparms.timeStart − now`, `:4036-4037`)
  and `fCountdownStarted` (`:4084-4086`), `nTeams`, `nMaxPlayersPerGame =
  min(nTotalMax, nTeams·nMaxPerTeam)` (`:4079-4081`), per-team min/max,
  `bSquadGame`, `bInvulnerableStations`, `bEjectPods`, and `rgSquadIDs`
  (`:3995-4013`). `fLimitedLives` (`:4092`) and `fMSArena` (`:4087`) stay
  derived, as before.
* `LobbyGameParams` (`src/Allegiance.Server/LobbyHeartbeat.cs`) gains the same
  fields (optional trailing members with native `Reset` defaults, so older
  construction sites/tests keep their shape) and `LobbyGameParams.FromMission`
  takes an optional `LobbyMissionLive` snapshot.
* `Allegiance.Lobby.ServerListing.cs` (`GameParamsDto` / `GameParamsRequest`) and
  `ServerRegistry.ToGameParams` pass them through with the same defaults;
  `ClientCore.Game/LobbyGameMetadata.cs` (`GameParams`) mirrors them, and its
  game-type classifier now uses the **real** `bInvulnerableStations` /
  `bEjectPods` instead of assumed Reset defaults (defaults still reproduce the
  pre-P866 classification for a host that publishes none).
* `EjectPods` is published as the port's **effective** value (always on, the
  deliberate fold at `Server.Combat.cs:1715-1717`) rather than the unmodelled
  MISSIONDEF bit, so the advertisement never claims a death model the server does
  not run. The native wire's server-identity strings (address/port/cookie/
  details file/privileged users/version) are the HTTP directory's own identity
  fields — design, no mission-info consumer.

**SIM-0572.** Native `SetLobbyIsDirty` (`fsmission.cpp:5624-5628`) sets
`m_fLobbyDirty`; `UpdateLobby` (`:5606-5621`) publishes only when it is set and
`c_flUpdateTimeInterval` (10 s) has elapsed, then clears it. The port's heartbeat
runs off-thread, so the flag is a monotonic **dirty version** on `Server`
(`LobbyDirtyVersion`, `MarkLobbyDirty`): a read/clear race can never lose an
update, and every native call site this port has bumps it —

* host mission-param edit (`FedSrv.CPP:7579`),
* `SetSideSquad` (`:1669`),
* `SetStage` (`:1766`) — starting, started, over, restart-cancel,
* `SetLockLobby` / `SetLockSides` (`:5023-5049`),
* a player entering/leaving `SIDE_TEAMLOBBY` (`fsship.cpp:1134`) — every real side
  switch and every team-lobby park,
* a player leaving the mission (`:523`),
* mission construction / rotation (dirty at construction, `:90`/`:448`; a new
  mission identity bumps the version inside the per-tick stats snapshot).

`LobbyHeartbeat.PumpAsync` now publishes on the first tick **after** the 10 s
coalesce when the version changed (count and lease irrelevant), keeps the HTTP
lease refresh (the port's substitute for native's DirectPlay cookie/session
handling), and never publishes inside the coalesce window — the flag simply stays
set for the next tick.

## 3. Connection bookkeeping (SIM-0579, 0580, 0582, 0583)

New `src/Allegiance.Core/Transport/ServerCounters.cs` — the named native
`SERVER_COUNTERS` fields (`FedSrv/counters.h:10-21`) as thread-safe counters:
`CTimeouts`, `CPacketsOut`, `CBytesOut` and their per-second twins from a window
drain.

* **SIM-0583 / `OnMessageSent`** (`FedSrv.CPP:4613-4652`): only `FM_GUARANTEED`
  messages count — `cPacketsOut += cConnections`, `cBytesOut += cb·cConnections`.
  Under TCP there is no DirectPlay send callback, so `PeerSender.EnqueueCore`
  (`TcpTransport.cs`) walks each guaranteed frame's already-built message headers
  at hand-off (one frame == one peer == `cConnections = 1`, each FEDMESSAGE == one
  packet, `cbmsg` bytes). No allocation, no codec/CRC work, no send-policy
  change; independent of the profiler-gated egress lane. The window is reported
  as a `[server-counters]` line next to `[egress-by-type]` (`Server.Egress.cs`,
  `LastServerCounters`). Native's `FM_CS_ORDER_CHANGE` debug block inside the
  callback is a DEBUG-only log — era/debug plumbing, no behavioural branch.
* **SIM-0580 / `OnDestroyConnection`** (`FedSrv.CPP:4549-4566`): an unrequested
  connection loss that still had a player attached bumps `cTimeouts` (+ per-second
  twin); a graceful LOGOFF is not a timeout (native deletes the player in the
  LOGOFF handler first, so the callback finds none — the port's `requested` flag
  on `ReleasePeerWithReason`). The retirement/announcement half was already
  ported (P813c/P128). `AllsrvEventID_PlayerDropped` is an AGC event with no
  backend anywhere in this port — era constraint, the console departure line is
  the substitute.
* **SIM-0582 / `OnBadCRC`** (`FedSrv.CPP:4591-4603`): the dispatcher used to
  **log and continue** on a CRC/framing failure — a corrupt client survived
  forever. It now logs the player/peer and the decoder's CRC detail and closes
  the connection (`DisconnectReason.BadCrc`), then the server keeps ticking. The
  `crashplayers.txt` copy to a shared Windows path and the desktop message box are
  DirectPlay/Windows-desktop plumbing — era constraint, the server console is the
  substitute.
* **SIM-0579 / `OnNewConnection`** (`FedSrv.CPP:4536-4546`): the whole native body
  is `debugf("New connection ID: %x IP: %s")`; under TCP the id is minted when
  the greeting is accepted, and that is where the port logs id + source IP +
  claimed name and records the address (`TcpTransport.PeerAddress`). DirectPlay
  session/connection semantics differ by design.

## 4. SIM-0608 — the remaining RequestPosition leg

Native `RequestPosition` removes the switcher from the current side with
`RemovePlayerFromSide(QSR_SwitchingSides)` **before** seating or pending
(`fsmission.cpp:4747-4751`); `AddPlayerToSide` does the same as its opening move
(`:562-563`). P852 closed the pending leg. The auto-accept leg called
`TryJoinSide` directly, which ran only leader succession + `QUIT_SIDE` — no flag
drop and no payday.

Both legs now go through one `LeaveSideForSwitch` block
(`src/Allegiance.Server/Server.Lobby.cs`):

1. flag drop — respawn/jettison + `GAIN_FLAG(NA, ship)` (`:875-893`),
2. leader succession / mission-owner migration (`:917-1099`),
3. `QUIT_SIDE(SwitchingSides)` whole mission (`:1130-1137`),
4. the leaver's wallet captured, zeroed and credited to the old side's treasury
   with an immediate payday (`:1042-1068`, `:1141-1145`),
5. the `SIDE_TEAMLOBBY` park (`:1140`) so the payday excludes the leaver.

`MoveToTeamLobby` (pending leg / squad boots) and `TryJoinSideCore` (auto-accept
and any live switch) share it; the silent rotation re-seat (`announce:false`) is
unchanged. Proof: a started game, a 250-credit flag-carrying side leader
auto-accepted onto a leaderless side — the observer decodes `GAIN_FLAG(NA)`,
`PAYDAY(250)` and `QUIT_SIDE(SwitchingSides)`, leadership passes to it, the
switcher's wallet is zero and it is on the new side. The bots-team-0 gate
(`FedSrv.CPP:6673-6685`) remains n/a (no `bDrones` mission param in the port;
`DPR_BotsOnTeam0Only` wire constant only), and `PickNewSide(NA)` maps to the
pending side-select, as before.

---

## Verification (light gate)

| check | result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 91 tool projects built**, 0 warnings |
| AOT publish (`Allegiance.Server`, linux-x64, self-contained, `PublishAot=true`) | **success**, no IL2026/IL3050 |
| new/affected tests 3× alone (51 cases: `P866*`, Probe/Mine/MissileBroadcast, AsteroidMinedLive, P739, P806, P591d, MineCrossSideVisibilityLive, ProbeCombatLive, P747) | **3/3 runs, 51/51 pass each** |
| `P697GhostBattleLiveTests` 3× alone (mission-scope regression found by the first gate run) | **3/3, 14/14 pass** |
| fast tier (run 1) | 11 286 tests; **1 failure**: `P697GhostBattleLiveTests.Static_emissions_and_cleanup_stay_in_the_owning_mission_when_side_ids_repeat` — a real P866 bug (the mine/probe/missile cleanup audience used the launcher's CURRENT mission); fixed by keying the audience on the object's owning sector mission (`MissionOfSector`) |
| fast tier (run 2, final tree) | 11 286 tests; **1 failure**: `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate` (expected `"goto Garrison"`, got `"goto"`) — already ledgered live-network greet-timeout flake; whole class **5/5 × 3 alone** |
| showcase never-parks 5× (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`) | **5/5 pass** (3 m 12 s – 4 m 38 s) |
| `python3 scripts/parity-scorecard.py` | validates; see scorecard below |
| `tools/check-glossary-links.py` | OK (250 paths) |
| CSV line endings | LF (`grep -c $'\r'` = 0) |

Flakes observed outside my tests, each isolated 3× (ledgered in
`docs/TEST_FLAKES.md`): `P699bPodLoadTests…` (3/3), `ConsoleVerbosityLiveTests…`
(3/3), `P746KillCreditOnEjectionTests.Winning_kill…` (8/8 theory + 12/12 class),
and `BroadcastSnapshotTests` golden cases — the latter is a **pre-existing stale
golden baseline**: `offHash == onHash == sharedHash` in every case and the actual
hash is `D0ED7EEB…`, exactly the dispatch/master value in
`docs/reports/P788C_assets/verification.json`, so the failure is not caused by
this task (reconfirmed after reverting the only MISSIONDEF-affecting experiment).

### Scorecard (before → after)

```
sim-rules-matrix.csv   before 94.80% weighted (1804/1903), 640/687 rows
                       after  96.11% weighted (1829/1903), 652/687 rows   (+25 weight = the 12 rows below)
parity-matrix.csv      unchanged 99.91% (1856.332/1858.000), 648/649
```

Full before/after detail: `docs/reports/P866_assets/scorecard-after.txt` and
`docs/reports/P866_assets/verification.json`.

---

## Per-row results

| row | native cite | before → after | tests |
| --- | --- | --- | --- |
| SIM-0652 `KillAsteroidEvent@12423` | `FedSrv/FedSrv.CPP:11429-11457,12424-12431` | partial → **implemented** (retirement audience = informed seated sides; team-lobby peers skipped; stale sides learn later) | `P866EventAudienceTests.Build_retires_the_rock_only_to_informed_seated_sides_never_team_lobby_peers`; `P747ConstructionTests.Stale_rock_discovery_is_side_scoped_ship_only_and_emitted_once`; `P747ConstructionTests.Destroying_an_unseen_constructed_station_retires_its_rock_for_uninformed_sides` |
| SIM-0655 `MineAsteroidEvent@12454` | `FedSrv/FedSrv.CPP:12454-12471` | partial → **implemented** (per-side `GetAsteroidCurrentEye`) | `AsteroidMinedLiveTests.Mining_broadcasts_ASTEROID_MINED_only_to_sides_with_an_eye_on_the_rock`; `…No_broadcast_fires_once_miner_hits_capacity`; `…MineOre_threshold_gates_the_pending_report`; `MiningTests.Near_rock_accrues_Ore_linearly…` |
| SIM-0656 `KillProbeEvent@12474` | `FedSrv/FedSrv.CPP:12479-12482` | partial → **implemented** (whole mission's real sides) | `ProbeBroadcastTests.Directly_deployed_probe_broadcasts_create_to_teammate_and_destroy_to_whole_mission`; `ProbeCombatLiveTests.Lethal_hit_kills_probe_removes_from_sector_and_broadcasts_destroy_to_same_side_peer` |
| SIM-0659 `KillMissileEvent@12512` | `FedSrv/FedSrv.CPP:12520-12529` | partial → **implemented** (whole mission's real sides; Explode branch already equal) | `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission`; `WarpBombTests.Domain_missile_in_warp_radius…` |
| SIM-0660 `KillMineEvent@12531` | `FedSrv/FedSrv.CPP:12536-12540` | partial → **implemented** (whole mission's real sides) | `MineBroadcastTests.Directly_deployed_mine_broadcasts_create_to_teammate_and_destroy_to_whole_mission`; `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction`; `P591dMinefieldExportAudienceTests` |
| SIM-0537 `QueueLobbyMissionInfo@3983` | `FedSrv/fsmission.cpp:3983-4096` | partial → **implemented** (counts/caps/slot scan/countdown/squads end-to-end) | `P866LobbyPublicationTests.Published_mission_info_carries_counts_caps_and_the_slot_scan`; `…carries_the_countdown_offset_and_playing_squads`; `P806LobbyGameParamsTests.Heartbeat_publishes_the_remaining_native_mission_info_fields`; `…Heartbeat_publishes_the_game_settings_to_a_real_directory` |
| SIM-0572 `SetLobbyIsDirty@5624` | `FedSrv/fsmission.cpp:5606-5628` (+ call sites `:90,:448,:523,:1669,:1766,:5028`, `fsship.cpp:1134`, `FedSrv.CPP:7579`) | partial → **implemented** (dirty version + 10 s coalesce + lease) | `P866LobbyPublicationTests.Runtime_changes_mark_the_lobby_dirty`; `P739LobbyPeriodicTests.Dirty_version_triggers_a_publication_after_the_ten_second_coalesce`; `…Dirty_lobby_details_coalesce_at_ten_seconds…` |
| SIM-0579 `OnNewConnection@4536` | `FedSrv/FedSrv.CPP:4536-4546` | partial → **implemented** (accept-time id + IP log; DirectPlay semantics = design) | `P866ConnectionBookkeepingTests.Live_server_new_connection_ip_egress_counters_and_timeout_counter` |
| SIM-0580 `OnDestroyConnection@4549` | `FedSrv/FedSrv.CPP:4549-4566` | partial → **implemented** (`cTimeouts` for unrequested drops, not LOGOFF; AGC event = era) | `P866ConnectionBookkeepingTests.Live_server_new_connection_ip_egress_counters_and_timeout_counter`; `…Graceful_logoff_does_not_bump_the_timeout_counter`; `P813cReconnectGraceTests` |
| SIM-0582 `OnBadCRC@4591` | `FedSrv/FedSrv.CPP:4591-4603` | partial → **implemented** (logs native-style detail and DROPS the peer; crashplayers/MessageBox = era) | `P866ConnectionBookkeepingTests.Corrupt_crc_packet_is_logged_and_the_peer_is_dropped` |
| SIM-0583 `OnMessageSent@4613` | `FedSrv/FedSrv.CPP:4613-4652`, `FedSrv/counters.h:10-21` | partial → **implemented** (named `cPacketsOut`/`cBytesOut` + per-second twins, guaranteed only) | `P866ConnectionBookkeepingTests.ServerCounters_count_guaranteed_messages_and_timeouts_like_native`; `…Live_server_new_connection_ip_egress_counters_and_timeout_counter`; `P661EgressAccountingTests` |
| SIM-0608 `FM_C_POSITIONREQ@6658` | `FedSrv/FedSrv.CPP:6658-6709`, `fsmission.cpp:4747-4751,562-563,875-893,917-1099,1130-1145` | partial → **implemented** (auto-accept leg runs the full `RemovePlayerFromSide(QSR_SwitchingSides)`) | `P866RequestPositionSwitchTests.Auto_accept_position_request_runs_the_full_remove_from_side_effects`; `LivePositionRequestTests`; `LiveTeamLobbyTests`; `P655DuplicateIdentityTests` |

## Native-intent classification

* **Event audiences (0652/0655/0656/0659/0660)** — *design*. Native's audience is
  a DirectPlay group; TCP has per-peer sends, so the port expresses the same
  membership rule explicitly (`RealSideAudience` / per-side eye). Nothing is
  skipped: the group's construction and every exclusion (team lobby, other
  mission) is ported, and each branch has a test.
* **Lobby publication (0537/0572)** — *design*. The native lobby wire
  (`LOBBYMISSIONINFO`, `lobby/messagesall.h:25-63`) is replaced by the HTTP
  directory; the native fields are carried on the register payload, and the
  *server-identity* wire fields (address/port/cookie/details file/privileged
  users/version strings) have no mission-info consumer on the port — they are the
  directory's own identity fields. The dirty flag becomes a monotonic version so
  the off-thread heartbeat cannot lose an update; `UpdateLobby`'s coalesce is
  ported literally.
* **Connection callbacks (0579/0580/0582/0583)** — *era constraint* for the
  DirectPlay/Windows-only parts: `FedMessaging` connection objects and session
  enumeration, the AGC `AllsrvEventID_PlayerDropped` bus, `crashplayers.txt` on a
  shared Windows path, the desktop message box, and native's DEBUG-only
  `ORDER_CHANGE` log. The behavioural parts are ported on the TCP/server seam:
  accept-time id+IP log, drop-on-bad-CRC, unrequested-drop timeout counter, and
  guaranteed-message egress counters.
* **SIM-0608** — *design*: same side-effect sequence on the port's park/seat
  primitives; the bots-team-0 gate is n/a (no `bDrones` param), and the port keeps
  its established no-ship-reset side switch (native's `Reset(false)` hull reset
  belongs to `AddPlayerToSide`, a separate row).

## Modern / better-approach note

* The audience rule is one named helper with the native group's semantics in its
  doc, so the next KILL/MINE port cannot silently invent a fourth audience. The
  object's mission is resolved from its owning sector (`MissionOfSector`) rather
  than from the launcher — the class of bug the first gate run caught.
* The lobby dirty flag is a monotonic version instead of a bool: it is
  thread-safe across the off-thread HTTP heartbeat without a lock on the sim
  thread and cannot lose a read/clear race; the coalesce and lease rules stay
  explicit.
* The native counters are always-on (native allocates them unconditionally) but
  cost a flat header scan only on guaranteed frames, and the same object feeds
  both the per-second window and the admin snapshot — no perfmon, no Windows
  telemetry.
* The client's game-type classifier now reads the real invulnerable-stations /
  eject-pods bits from the directory instead of assuming Reset defaults — a
  small downstream improvement the publication made possible.

## Unfinished / follow-ups

* `BroadcastSnapshotTests` golden hashes are a **pre-existing** stale baseline
  (recorded actual `D0ED7EEB…` matches `docs/reports/P788C_assets/verification.json`,
  not this task's bytes); needs a dedicated regeneration task after the recent
  broadcast changes, not a P866 edit.
* The port still does not model `MissionParams.bInvulnerableStations` / `bEjectPods`
  as gameplay switches (published as Reset default / effective behaviour); a
  future row would have to wire them into the death and station-damage paths
  first.
* `AddPlayerToSide`'s ship `Reset(false)` (fresh hull/position on a real side
  switch) is outside SIM-0608's removal leg and remains as previously certified.
