# P813c (EXPERIMENTAL) — reconnect grace: an unrequested drop rejoins the same ship

Status: **implemented behind a default-off flag**, awaiting the rig A/B (200v200 march, flag OFF vs ON on
rig 221). Nothing in this task changes default behaviour: with `reconnect.enabled=false` (the default) every
disconnect path is byte-for-byte the pre-P813c port, and the whole fast tier (10 213 tests) is green.

Branch/worktree: `pi/p813c` (working tree left uncommitted for the dispatcher).
Native reference: `~/Development/Allegiance/src/` (cites below are from that tree).

---

## 1. Why, and what native does today

A dropped connection ends a native player's session outright:

| native step | cite |
|---|---|
| dropped connection handler destroys the player | `FedSrv/FedSrv.CPP:4549-4566` (`FedSrvSiteBase::OnDestroyConnection` → `delete pfsPlayer`) |
| the player's destructor removes it from the mission as link-dead | `FedSrv/fsship.cpp:995` (`RemovePlayerFromMission(this, QSR_LinkDead)`) |
| the ship destruction announces the exit and terminates the ship | `FedSrv/fsship.cpp:58-68` (`CFSShip::~CFSShip` → `AnnounceExit(NULL, SDR_LOGGEDOFF)` + `m_pShip->Terminate()`) |
| a **voluntary** quit is deleted inside the LOGOFF handler, not on connection teardown | `FedSrv/FedSrv.CPP:5787-5802` (comment: "need to delete here … so we can tell when we get there whether it was a dropped connection") |

**Classification: era constraint.** Native hosted on LAN/Zone with a DirectPlay session per game; a short
drop was indistinguishable from a player leaving, so "delete now" was the simple correct answer (and there is
no client-side reconnect anywhere in `clintlib`). A modern WAN server — and this port, with P809's NAT/relay —
sees short drops routinely, and today that throws a pilot out of a battle mid-fight. **Modern better
approach:** hold the seat for a short window and let the same session resume it; run the native removal
unchanged when the window expires.

The port already ports the native removal faithfully (P127/P128, `Server.Lifecycle.cs`
`ReleasePeerWithReason` → `RetireDepartedPeer` → `RetireShipOnDisconnect`). P813c only *delays* that path for
an unrequested drop and adds the resume; the expiry calls the very same code.

---

## 2. Per-row: rule, port before/after, data path, tests

Flag: `reconnect: { enabled: bool (default false), graceSeconds: double (default 5) }` — `server.json`/CLI
`--reconnect[=true|false]`, `--reconnectGrace=N` (implies enabled). Boot prints a loud banner for both arms
so an A/B run can never be mislabelled.

### R1 — unrequested drop: hold the seat instead of deleting it

* **Native rule:** `FedSrv.CPP:4549-4566` + `fsship.cpp:995,58-68` (above).
* **Before (and with the flag off):** `PeerLeftEvt` → `ReleasePeer` → the ship is retired at once
  (`SHIP_DELETE(20, SDR_LOGGEDOFF)` to its sector, removed from the sector), then `HandleLeaderSuccession`,
  pending-join rejects, `QUIT_MISSION(124, QSR_LinkDead)`, and the win-by-inactive-sides check.
* **After (flag on):** the same `ReleasePeerWithReason` connection-scoped cache cleanup runs (so no per-peer
  state leaks), but instead of the departure block the `LiveShip` is **parked** in
  `Server.ReconnectGrace.cs` keyed by character name, holding the resume token and the expiry time. The ship
  stays in its sector (still flying under `Mission.Update`) and is no longer commanded.
* **Gate on a token:** only a peer that presented a non-zero `LOGONREQ.dwCookie` is parked (see R2). Clients
  that send a constant/zero (today's proof tools) get the native immediate removal even with the flag on.
* **Data path:** nothing is sent to other clients at park time — a held player has not left the mission, and
  no native message exists for "temporarily away" (we deliberately do **not** invent one).
* **Code:** `Server.Lifecycle.cs` `ReleasePeerWithReason` (park branch), `Server.ReconnectGrace.cs`
  `TryParkLiveShip`.
* **Tests:** `P813cReconnectGraceTests.Unrequested_drop_parks_the_ship_with_neutral_controls`,
  `P813cReconnectGraceTests.Grace_off_by_default_retires_the_ship_immediately`.

### R2 — resume: same ship, same seat, on the native join sequence

* **Native rule:** a client session's identity is proven with a per-session cookie the server mints
  (`LOGONACK.cookie = pfsPlayer->GetCookie()`, `FedSrv.CPP:983`; the in-game cookie is a byte,
  `JOINED_MISSION`'s own comment calls it "NOT a lobby cookie. It's an in-game cookie", `Inc/Messages.h:660-665`).
  The join sync a player receives is `SendMissionInfo`/`AddPlayerToSide`
  (`fsmission.cpp:3943-3974`, `4122-4250`).
* **Resume token:** the **existing native wire field** `LOGONREQ.dwCookie` (`Inc/Messages.h:43`) — the port
  already carries it (and echoes its low byte into `LOGONACK.cookie`). **No new message, no new field.**
  The client mints a 32-bit session nonce per session (`ConnectionSession`, so it is a *session* token, never
  an account — P808a out of scope) and the server parks/accepts only on **name + token** match. A wrong
  token retires the parked seat immediately (native path) and the claimer gets a normal fresh logon, so a
  name cannot be used to hijack a seat and a player can never end up with two ships.
* **Before:** a reconnect was a fresh logon → new ship id, new side-select, score/wallet reset (P193's
  documented limitation).
* **After:** `TryResumeParkedPeer` sends a validated `LOGONACK` with the **same** ShipID and then replays
  **the existing join burst** — `CompleteJoinAndLaunch(..., announceToMission: false, resumed: true)` — i.e.
  `MISSIONDEF` → `JOINED_MISSION` → `PLAYERINFO` for every ship → `SET_MONEY`/tech → `CLUSTERINFO` + cluster
  exports → `SET_CLUSTER` (same ship) → `MISSION_STAGE` → `SHIP_STATUS` → `ENTER_GAME`. No side-select, no
  mission announce (the player never left), no economy re-grant (native re-grants nothing on a resume).
* **Data path:** all existing native server→client messages; the client's world is rebuilt by the same burst
  a fresh joiner of an in-progress mission receives.
* **Code:** `Server.Dispatch.cs` LOGONREQ hook, `Server.ReconnectGrace.cs` `TryResumeParkedPeer`,
  `Server.Lobby.cs` `CompleteJoinAndLaunch` (`resumed`).
* **Tests:** `Reconnect_within_grace_resumes_the_same_ship_and_seat`,
  `Resume_with_the_wrong_token_is_refused_and_the_ship_retires`,
  `P813cReconnectClientTests.Auto_reconnect_resumes_the_same_ship_and_resyncs_the_world`, plus the two
  seat-kind cases the brief names: `Docked_player_resumes_still_docked` (same station, `SHIP_STATUS`
  `stateDocked`) and `Turret_gunner_resumes_into_the_same_turret` (same parent + turret ids, `SHIP_STATUS`
  `stateTurret` with the carrier as ParentID — the port's `MakeOwnShipStatus` writes both, so the client
  learns *where* it is seated from the existing native message).

### R3 — expiry: the native removal, unchanged

* **Native rule:** `FedSrv.CPP:4549-4566` → `fsship.cpp:995,58-68` (R1).
* **After:** the per-tick `AdvanceReconnectGrace` sweep (a new `SimPass` stage `reconnectGrace`) removes the
  parked entry and calls `RetireDepartedPeer(...)` — the exact pre-P813c departure: `SDR_LOGGEDOFF` to the
  sector, ship removed, leader succession, `QUIT_MISSION(QSR_LinkDead)`, win-by-inactive-sides.
* **Data path:** unchanged native messages (`SHIP_DELETE(20)`, `QUIT_MISSION(124)`).
* **Code:** `Server.ReconnectGrace.cs` `AdvanceReconnectGrace`, `Server.Broadcast.cs` (SimPass stage).
* **Tests:** `Grace_expiry_runs_the_native_removal` (observer sees `SDR_LOGGEDOFF` + `QUIT_MISSION(LinkDead)`),
  `P813cReconnectClientTests.Beyond_the_window_the_session_is_lost_and_the_ship_retired`.

### R4 — voluntary quit is immediate

* **Native rule:** `FedSrv.CPP:5787-5802` deletes the player in the LOGOFF handler; the reason is `QSR_Quit`
  (`Inc/Messages.h:797`, rendered by `clintlib/appmsg.cpp:3970` as "has quit").
* **Before:** the LOGOFF handler called the shared `ReleasePeer` (reason hard-coded `QSR_LinkDead`).
* **After:** `ReleasePeerOnRequest` → reason `QSR_Quit`, `requested: true` so the grace never holds it.
* **Data path:** unchanged `LOGOFF(3)` echo + `SHIP_DELETE(20)` + `QUIT_MISSION(124)`; only the reason value
  changes (a small parity fix, see §7).
* **Code:** `Server.Dispatch.cs` LOGOFF branch, `Server.Lifecycle.cs` `ReleasePeerOnRequest`.
* **Tests:** `Voluntary_logoff_is_immediate_and_reports_quit`.

### R5 — controls during the gap: **coast** (decision)

* **Native rule:** native has no "client went quiet" timer at all — the ship keeps whatever `SetControls` last
  gave it. For a ship with no plan target, `CshipIGC::PlotShipMove` zeroes the stick and clears the
  movement/weapon bits (`Igc/shipIGC.cpp:2308-2348`) using `ControlData::Reset()` (`Igc/igc.h:1666-1670`:
  turn axes 0, throttle −1).
* **Decision (Classification: design):** at park time the ship's `Controls` are set to `ControlData.Neutral`
  and `StateM = 0`. The pilot is *not* flying, so leaving the last stick input applied (full yaw + throttle +
  fire bits) would fly the ship into a rock and shoot at nothing for the whole window; the native no-plan
  branch is the closest native behaviour ("idle with neutral input"). The ship keeps its velocity — it
  **coasts** — and can still be shot at. This mirrors what the port's own client sends when input is released
  (`new ControlData(0, 0, 0, -1)` in `Game1.FlightInput.cs`).
* **Code:** `Server.ReconnectGrace.cs` `TryParkLiveShip`.
* **Test:** `Unrequested_drop_parks_the_ship_with_neutral_controls` (asserts the non-neutral pre-drop input is
  replaced by `ControlData.Neutral` and `StateM == 0`).

### R6 — death during the gap: **no resurrection**

* **Native rule:** a dead player's ship is gone (`KillShipEvent` → `SetCluster(NULL)`, lifepod respawn needs a
  friendly station, `FedSrv.CPP:12034-12074`); the port's death contract is `FinalizeDeadShip`
  (`Server.Combat.cs`: fraction 0 + `Sector.Terminate` + `Sector = null`).
* **How the port behaves:** a parked ship is unowned (`_peerShips` has no entry), so the existing kill path
  takes its "no owning peer → no respawn, stays dead" branch (`Server.Combat.cs`), i.e. a death during the gap
  is final. On resume the server sees `Sector is null` and **refuses** the resume, dropping the parked entry;
  the logon continues as a normal fresh logon (a new ship) — the pre-P813c outcome. The death is not counted
  to the parked player's score (the existing P193 rule: score does not survive a reconnect; noted, not fixed
  here).
* **Test:** `Death_during_the_gap_is_not_resurrected`.

### R7 — game over during the gap: the grace ends with the world

* **Native rule:** a finished mission vacates and resets its players (`Vacate()`/`Reset(true)`,
  `fsmission.cpp:2604-2622`); the port's rotation rebuilds the mission and re-seats connected peers (P439/P480).
* **After:** `RotateMissionAfterGameOver` calls `DropParkedShipsForMission(oldMission)`: parked entries for the
  discarded world are dropped without a departure protocol (there is no audience and no side left to
  re-evaluate), and their ship-scoped latches are freed. A reconnect afterwards finds no parked entry and gets
  a normal fresh logon into the rotated mission.
* **Test:** `Game_over_during_the_gap_ends_the_grace`.

### R8 — side leadership during the gap: **kept while held, native succession at expiry**

* **Native rule:** `RemovePlayerFromSide` promotes the heir at departure
  (`pship->Promote()` + `FM_S_PROMOTE`, `fsmission.cpp:895-913`).
* **Decision (Classification: design):** the player has not left the mission, so leadership is kept for the
  window (the port's `HandleLeaderSuccession` runs only in the departure block, which expiry still executes
  unchanged). The alternative — running succession at park and re-electing on resume — could hand leadership
  to someone else for 5 s and then take it back, which is worse than a 5 s hold.
* **Test:** `Side_leader_keeps_leadership_during_the_gap`.

### R9 — bot clients reconnect the same way

* Combat bots (and the MCP probe) are `ConnectionSession`s, so the client half is shared:
  `tools/allegiance-mcp/StallWatchdog.cs` arms `ConnectionSession.EnableAutoReconnect` when
  `$ALLEGIANCE_RECONNECT_GRACE_SECONDS` is set, and `BotManager.Lifecycle.cs` `IsParticipating` no longer
  reaps a bot that is inside its reconnect window (it would otherwise be removed as "connection lost").
* **Limit, stated plainly:** the legacy `Allegiance.Bot.Bot` client (MCP `idle`/`weave` bots) is *not*
  auto-reconnecting; it still benefits from the server-side hold if it dials again, but it does not dial by
  itself. The 200v200 combat fleet is `ConnectionSession`-based.

---

## 3. Verification (light gate)

| check | result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 74 tool projects built** (includes the new `tools/reconnect-fault`) |
| AOT publish (`-c Release -r linux-x64 --self-contained -p:PublishAot=true`) | **succeeded, no IL2026/IL3050**; published binary boots a config with the new `reconnect` block (source-generated JSON) and prints `[reconnect-summary]` |
| new server tests, 3× alone | 12/12, 12/12, 12/12 (`P813cReconnectGraceTests`) |
| new client tests, 3× alone | 3/3 × 6 consecutive runs plus 3/3 in the final gate (`P813cReconnectClientTests` + `ReconnectingBot_IsStillParticipating`) |
| new MCP-lifecycle test | `BotFixRegressionTests.ReconnectingBot_IsStillParticipating` (runs with the 140-test class, all pass) |
| affected existing suites (flag off) | 47 tests (LiveLogoff, LiveSuicideShipDelete, LiveTeamLeadership, MissionRotation, GameOverPlayersWire, LiveP388DebugNoGameOver, ConfigDefaultsJson) + 173 tests (RetryCleanup, BotFixRegression, P657bStallTelemetry, P671KeepAliveLive, P718InterfaceLoopback) — all pass |
| **one** fast-tier run (`tools/run-tests.sh --tier fast`) | **10 213 tests, 305.9 s, PASS** (Lobby 24, MonoGame 3310+3 not-executed, ClientCore 1755+2 quarantine, Core 5109+10 quarantine). Interim runs during the task failed only on already-ledgered load flakes under a host load average of ~14 (each verified 3/3 alone; one `SLOW FAST` 11.2 s); rows appended to `docs/TEST_FLAKES.md` |
| fault injection, local showcase (`tools/reconnect-fault`) | 10 bots × 2 kill rounds → **20/20 resumed, same seat**; 30 bots × 2 rounds → **60/60 resumed, same seat** (`docs/reports/P813C_assets/fault-injection.json`, `…-30bots.json`) |
| parity scorecard | `scripts/parity-scorecard.py` validates both matrices (parity 670 rows, sim-rules 474 rows) |

Note on the one intermediate failure: the first full fast-tier run red-flagged
`P813cReconnectClientTests.Beyond_the_window_…` with `lostRaised == 0`. That was a test-side
visibility race (it polled the `Lost` latch, which is set immediately *before* the synchronous
`SessionLost` raise on the reconnect thread); the test now waits for both, and passed 6/6 alone plus
the final gate. No product change was needed and no ledger entry is warranted.

Raw logs (outside the repo): `~/allegiance-evidence/P813C/fast-tier-final.log`, `fault-10bots.log`,
`fault-30bots.log`, `server-tests-run.log`, `client-tests-run.log`.

### Which tests fail on master

The new tests exercise APIs that do not exist on master (`ReconnectConfig`, `Server.ReconnectGraceEnabled`,
`ConnectionSession.EnableAutoReconnect`), so on master they do not compile — the honest statement is that they
**define** the difference. The behavioural deltas are each the exact inverse of a master assertion:

* master = `LiveLogoffTests.Abrupt_drop_retires_the_ship_and_broadcasts_loggedoff` ("the dropped ship is gone
  from the sector at once"); `Unrequested_drop_parks_the_ship_with_neutral_controls` asserts that with the flag
  on the ship is **still in the sector** and its controls are neutral. Master's behaviour is preserved by
  `Grace_off_by_default_retires_the_ship_immediately`, which is the flag-off control for exactly that test.
* master has no resume: `Reconnect_within_grace_resumes_the_same_ship_and_seat` and
  `Auto_reconnect_resumes_the_same_ship_and_resyncs_the_world` would get a *different* ship id (master's
  `RetryCleanupTests` documents that a retry "re-seats with new assignment").
* master broadcasts `QSR_LinkDead` on a voluntary logoff; `Voluntary_logoff_is_immediate_and_reports_quit`
  requires `QSR_Quit` (`FedSrv.CPP:5787-5802`).

---

## 4. Rig recipe (the 200v200 march that decides whether this is kept)

Everything below is Claude's to run on rig 221; this task only provides the flag, the telemetry and the
expected direction.

**Setup.** Standard baseline: `spawn_scenario` preset `showcase`, `target_sector` Alderan, `size_scale` 13,
`mode: march`, native ship-update mode, bots on the M4 Max, fog on; two full unattended matches
(game over → next match → game over) per arm.

**Arm A (baseline):** server config `reconnect.enabled = false` (or absent — the default), nothing on the bot
host. **Arm B:** server config

```json
"reconnect": { "enabled": true, "graceSeconds": 5 }
```

and, on the bot host, `ALLEGIANCE_RECONNECT_GRACE_SECONDS=5` (this is the one switch the bots and the MonoGame
client read; the window should match the server's). Note: the *baseline* has no drops by design — unless a
bot socket actually dies, arm B is identical work, so also run a **fault arm**: arm B plus a periodic
socket-kill of a slice of the fleet (the fault-injection tool does exactly this locally; on the rig, kill
N bot sockets at a fixed cadence from the bot host) — that is the arm where the feature can pay off.

**What to measure (per match, both arms):**

| metric | source | expected direction with the flag ON |
|---|---|---|
| ticks/5 s (100 = real time) | the standard `[egress]`/profiler window line + runner tick counts | **≥ 100 (no regression)**; ideally unchanged (the parked path is O(1) per drop) |
| p95 / max tick | profiler window report (`[egress]`/CSV), `[gameover-timing]` for the handoff tick | **unchanged or lower** (fewer mid-battle re-seats: a resumed seat skips ship spawn + side-select + the full join cost) |
| slow ticks | profiler `slowTick` count | **↓** (each avoided fresh join removes one join-burst spike) |
| REFUSED / drops | `[disconnect-summary]` window line (transport reasons) | drops **unchanged** (the fault injection is what creates them); REFUSED unchanged |
| kills/min, bots in the fight | scenario/`get_perf_stats` | **↑ or unchanged** — bots that would have been removed keep fighting |
| game-over handoff | `[gameover-timing] elapsedMs`, `GameOverMigrationTicks` | **unchanged** (the grace adds no work to rotation; parked seats are dropped with the old world) |
| match outcome spread | match results across the two matches | **unchanged** (no side/leader change is fabricated by the grace) |

**Log lines to watch (all bounded):**

* `[boot] *** RECONNECT GRACE ACTIVE …` / `[boot] reconnect.enabled=false …` — which arm this log is.
* `[reconnect] peer … parked …` (one line per unrequested drop), `[reconnect] … RESUME — ship …` (one per
  resume), `[reconnect] '…' did not return within …` (one per expiry), `[reconnect] … REFUSED …` (wrong
  token / dead ship / name taken), `[reconnect] '…' grace ended by game over …`.
* `[reconnect-summary] window: parkedTotal=… resumedTotal=… expiredTotal=… refusedTotal=… currentlyParked=…`
  — one line per 100 ticks (~5 s) **only when the flag is on**; this is the single line to diff between arms.
  Expected on a healthy arm B fault run: `resumedTotal` tracks the injected kills, `expiredTotal` ≈ 0
  (it also counts seats discarded by a game-over rotation, so a rotation between matches bumps it once
  per parked seat), `refusedTotal` ≈ 0, `currentlyParked` back to 0 between kills. On arm A the same kills show up in
  `[disconnect-summary]` and `[session-loss]` on the bot host, and `RemovedBots` grows instead.
* Bot host: `[reconnect] session lost — auto-reconnect armed …` / `[reconnect] session restored after N
  attempt(s): ship …` (from `ConnectionSession`).

**Decision rule (from the brief):** keep only if the target metric improves without hurting any of the
metrics above; otherwise the flag stays in the tree but off (it is inert, and the default-off behaviour is
native parity).

---

## 5. Design notes, limitations, and what is deliberately not done

* **Token is session-scoped, not an account** (P808a out of scope): a fresh session mints a new nonce; the
  token dies with the parked entry. No persistence.
* **No new wire data.** The token rides `LOGONREQ.dwCookie` (native field, `Inc/Messages.h:43`) and the
  resume reuses `LOGONACK`/`JOINED_MISSION`/`SET_CLUSTER`/`ENTER_GAME` — native messages only.
* **Connection-scoped state is not carried over.** The park path runs the full `ReleasePeerWithReason` cache
  cleanup, so admin/harness auth, spectate-sector and per-peer latches are dropped; on resume,
  `AuthenticatePrivileges` re-runs from the same LOGONREQ (same CD key/secret), and the join burst refills
  everything else. The test-harness/test-unlock wallet grants are **skipped** on resume so a reconnect cannot
  re-grant money.
* **A parked carrier keeps its gunners.** Pilots who drop with gunners aboard: the gunners stay connected and
  can keep firing while the carrier coasts; native never has this state (it detaches at once,
  `shipIGC.cpp:243-250`). The reverse case (the GUNNER dropping) IS covered — it resumes into the same turret;
  the pilot-drops-with-crew case is not exercised by a test, so it is flagged for the rig run rather than
  claimed.
* **A stale parked entry costs one ship slot for ≤ graceSeconds** — bounded by the window and by the
  duplicate-name/wrong-token retire.
* **MCP telemetry after a resume:** the bot host's stall watchdog decorates the *original* transport
  (`ObservedSession`); the resumed socket is a plain one, so that bot's read-age samples stop after a resume
  (it is already removed from the watchdog's table when the loss is recorded, so this never shows up as a
  false stall). Accepted for the experiment; a kept feature would re-wrap the resumed transport.
* **Not covered:** the legacy `Bot` class does not auto-reconnect (§R9); no account-level identity; no
  mid-gap command queueing (controls during the gap are simply absent).
* **Idea source:** `wivuu/stellarallegiance` (GLOSSARY terms "Reconnect Grace", "Reliable / Lossy Outbound
  Tiers") — ideas only, **no code copied** (that project has no license).

---

## 6. Files

New:

* `src/Allegiance.Server/Server.ReconnectGrace.cs` — the park table, resume, expiry sweep, rotation drop,
  bounded telemetry, boot resolution.
* `tests/Allegiance.Core.Tests/P813cReconnectGraceTests.cs` — 12 server/wire tests (park/resume/expiry,
  wrong token, death and game-over in the gap, leadership, docked and turret seats, config/CLI).
* `tests/Allegiance.ClientCore.Tests/P813cReconnectClientTests.cs` — 3 end-to-end client tests with a
  drop/kill-switch proxy.
* `tests/Allegiance.ClientCore.Tests/BotFixRegressionTests.cs` — `ReconnectingBot_IsStillParticipating`
  (the MCP roster must not reap a bot inside its window).
* `tools/reconnect-fault/` — the local fault-injection showcase (kill N bot sockets mid-flight).
* `docs/reports/P813C_assets/fault-injection.json`, `fault-injection-30bots.json`.

Changed:

* `src/Allegiance.Server/ServerConfig.cs` — `ReconnectConfig` + `ServerConfig.Reconnect` + CLI keys/validation.
* `src/Allegiance.Server/ServerJsonContext.cs` — source-generated JSON for the new section (AOT-safe).
* `src/Allegiance.Server/Server.Lifecycle.cs` — `LiveShip.ResumeToken`; `ReleasePeerWithReason(requested)`;
  departure block factored into `RetireDepartedPeer` (shared by expiry); `ReleasePeerOnRequest`; boot
  `ResolveReconnectGrace`.
* `src/Allegiance.Server/Server.Dispatch.cs` — LOGONREQ resume hook; LOGOFF → `ReleasePeerOnRequest` (`QSR_Quit`).
* `src/Allegiance.Server/Server.Lobby.cs` — `CompleteJoinAndLaunch(resumed)`, token capture, no re-grant on
  resume, rotation drops parked seats.
* `src/Allegiance.Server/Server.Broadcast.cs` — `reconnectGrace` SimPass stage + `[reconnect-summary]` line.
* `src/Allegiance.Server/Properties/AssemblyInfo.cs` — `InternalsVisibleTo("reconnect-fault")`.
* `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — resume token, `EnableAutoReconnect`,
  `Reconnecting`, `ResumeAttempts/Successes/ShipMismatches`, in-place resume loop (world reset + native join
  burst re-sync), `Dispose` stops the loop.
* `src/Allegiance.ClientCore.Game/Game1.Session.cs` — opt-in via `$ALLEGIANCE_RECONNECT_GRACE_SECONDS`.
* `tools/allegiance-mcp/StallWatchdog.cs`, `ObservedSession.cs`, `BotManager.Lifecycle.cs` — arm bots and stop
  reaping reconnecting ones.
* `GLOSSARY.md` (Reconnect Grace entry + concept row), `docs/parity/sim-rules-matrix.csv` (**SIM-0474**),
  `docs/parity/parity-matrix.csv` (fedmsg 1/2/3 notes).

No new service, port, volume or server env var → no `docker-compose.yml` change (`docker compose config` is
unaffected; the flag is a JSON section that is absent — i.e. off — by default).

---

## 7. Parity rows updated

* **`docs/parity/sim-rules-matrix.csv` — new row `SIM-0474`** (`session`, `disconnect_removes_player_and_ship`,
  weight 4, `implemented`, native_class **era constraint**): cites `FedSrv.CPP:4549-4566`,
  `fsship.cpp:995`, `fsship.cpp:58-68`, `FedSrv.CPP:5787-5802`; the port citation names both the always-on
  native path and the experimental grace; evidence lists the six decisive tests plus the unchanged
  `LiveLogoffTests.Abrupt_drop_retires_the_ship_and_broadcasts_loggedoff`.
* **`docs/parity/parity-matrix.csv`** — `fedmsg 3 LOGOFF`: a voluntary quit is never held by the grace and its
  broadcast reason is now `QSR_Quit` (a real parity fix: native `FedSrv.CPP:5787-5802` treats LOGOFF as an
  immediate delete, and `clintlib/appmsg.cpp:3970` renders `QSR_Quit`); `fedmsg 1 LOGONREQ` / `2 LOGONACK`:
  with the experimental flag on, `dwCookie` doubles as the session resume token and the ack carries the
  resumed ship id.
* No row was marked done on symbol presence: every claim cites the executable path and a test above.
