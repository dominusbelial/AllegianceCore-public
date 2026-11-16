# R1B F4 — server repositions skip the cookie / SET_CLUSTER (P1029)

Owner finding: **F4** of `R1B_CARBON_REPORT.md` (R1b review, Gemini 4 Carbon). Brief: `P1029_BRIEF.md`.
Scope: the three server-authorized repositions — carrier relaunch (`PerformRelaunch`), ripcord teleport
(`PerformRipcordTeleport`) and the admin `relocate` cheat (`HandleRelocateCheat` / `RelocateShipToSector`).
Child-ship propagation stays with **P1034** (R1b F3), as the brief says.

## 1. What reproduced, and what did not

The finding has three claims. Two reproduce exactly; the third is partly wrong, and one brief instruction
(refuse docked targets) was deliberately not followed because it breaks the port's legitimate staging path.
Evidence: the pre-fix live run (`4 failed / 2 passed`, `~/allegiance-evidence/P1029/pre-fix.log`) and the probe run for the docked case.

| # | Claim | Verdict | Evidence |
| --- | --- | --- | --- |
| 1 | `PerformRelaunch` sends `RelaunchShip.Cookie = 0` for a player | **Reproduced.** The player's own client adopts `0` (native own-cookie rule), the server peer cookie stays at its join/undock value (e.g. `0x02`), and every later `FM_C_SHIP_UPDATE` is dropped whole. | `Relaunched_player_cookie_rotates_and_the_next_ship_update_is_accepted` failed pre-fix (no accepted update, cookie still 0); log turns show `cookie mismatch (expect 0x02, got 0x00)`. |
| 2 | Ripcord teleport omits `SetCluster` / `NextCookie` / treasure snapshot | **Reproduced** for both a cross-sector and a same-sector ripcord: the model moved, the peer never got `SET_CLUSTER(7)`, the client kept the old sector and the old cookie. | `Cross_sector_ripcord_sends_SET_CLUSTER_with_a_rotated_cookie` and `Same_sector_ripcord_still_sends_SET_CLUSTER_and_rotates_the_cookie` both failed pre-fix. |
| 3 | Relocate "omits `SetCluster` / `NextCookie`" | **Not real for a flying ship.** `RelocateShipToSector` raises `ShipWarped` → `Server.OnShipWarped`, which already sent `SET_CLUSTER` with `NextCookie` + the treasure snapshot. The real gap was target state: docked / boarded / team-lobby / dead ships keep a non-null `Ship.Sector` here, so the old `target.Sector is null` test let them be moved with `StationId`/`ParentShip` left behind. | `Relocate_of_a_flying_ship_sends_SET_CLUSTER_with_a_new_cookie` **passed before any fix** (pinned now); the state-gate test failed pre-fix. |
| 4 | Brief: "refuse docked" | **Deliberately not refused; the dock is cleared instead.** A seated peer can be docked at relocate time (the P554 staging test seats a bot that docks on the first tick — probe `grounded='docked at a station' station=24465`). Refusing docked targets fails three existing legitimate tests (`P554AdminCheatLiveTests`, 3 failures / 7). Clearing the dock is exactly the state the client is told to take (`Apply(SetCluster)` → `SetStation(NULL)`, `clintlib/appmsg.cpp:1044-1049`), so the corruption (a stale dock at a station the ship is no longer at) is removed without breaking the harness. | Refusal variant: `P554AdminCheatLiveTests` 4/7; probe line above; reverted variant: 7/7. The docked leg of `Relocate_refuses_boarded_team_lobby_and_dead_and_clears_a_docked_ships_dock` asserts the move + cleared dock. |

## 2. Root cause (file:line)

1. **Relaunch.** `src/Allegiance.Server/Server.Docking.cs` `PerformRelaunch` built
   `new RelaunchShip { …, Cookie = 0 }` (old line ~1473). The client applies the message as a ship reset for its
   **own** ship and takes the message cookie (`src/Allegiance.ClientCore/State/ClientWorld.FlightInterface.cs:85`
   → `Apply(new ShipReset { … Cookie = message.ShipId == MyShipId ? message.Cookie : ship.Cookie })`), while the
   server's `PeerShipUpdate.Cookie` never rotates. `src/Allegiance.Server/Server.Dispatch.cs:469` then drops every
   `SHIP_UPDATE` whose cookie differs — controls included — matching native `FedSrv.CPP:4851-4864`.
2. **Ripcord.** `PerformRipcordTeleport` ended in `UpdatePeerSectorSnapshot(rip, targetSector)`: server-side peer
   sector only, **no** `SET_CLUSTER`, no cookie rotation, no destination exports. Native `UseRipcord` ends in
   `pship->SetCluster(pcluster)` (`FedSrv.CPP:13482`), and for a player that is `CFSPlayer::SetCluster`
   (`fsship.cpp:742-767`: guaranteed `SET_CLUSTER` with `cookie = NewCookie()`), reached through
   `Igc/shipIGC.h:233-240` — which runs even when the cluster does not change ("the ship mostly likely teleported
   to a new position") → `FedSrv.CPP:13525-13527` `pfsShip->SetCluster(pclusterNew)`.
3. **Admin relocate.** `HandleRelocateCheat` only tested `target.Sector is null`. Docked, boarded and team-lobby
   ships keep `Ship.Sector` non-null in this port, so they were moved with `StationId`/`ParentShip` intact
   (a stale dock in the destination sector, from which `BuyLoadout` could still mount a hull). The flying-move wire
   notification was already correct (see claim 3).

## 3. Fix

- `Server.Docking.cs` `PerformRelaunch` (1490-1500): stamp `NextCookie(EnsurePeerUpdate(ConnOf(lander)))` for a
  peer-owned lander; a drone/NPC lander keeps `0`. `NextCookie` also clears `HasMovementAnchor`, the same rebase
  `SET_CLUSTER` performs.
- `Server.Combat.cs` `SendOwnerSetCluster(ship, sector)` (2948): the per-peer half of native `CFSPlayer::SetCluster`,
  extracted from the existing `OnShipWarped` so an aleph warp and the ripcord share **one** implementation — refresh
  `LiveShip.Sector`, send `SET_CLUSTER(7)` with `NextCookie(EnsurePeerUpdate(conn))`, send the destination treasure
  snapshot; returns the peer addressed or `null` for an NPC.
- `Server.Docking.cs` `PerformRipcordTeleport` (685): replace `UpdatePeerSectorSnapshot` with
  `SendOwnerSetCluster(rip, targetSector)` — the peer sector refresh plus the native cookie/exports, for cross- and
  same-sector moves. TODO in code points at P1034 for the ripped **child** ships (out of scope).
- `Server.AdminCheats.cs` `HandleRelocateCheat`: refuse dead, boarded and team-lobby targets (358); for a docked
  target clear `StationId`, `StateM` and stamp `LastLaunchTime` (the P75 re-dock debounce) before the move (370), so
  the server matches the `SetStation(NULL)` the client applies. `RelocateShipToSector` is unchanged — it already
  delivers `SET_CLUSTER` + fresh cookie through `ShipWarped`.
- `src/Allegiance.Bot/Bot.cs:2167`: the harness adopts the `RELAUNCH_SHIP` cookie for its own ship, exactly as the
  real client does; otherwise its next pose echo carries a cookie the server has rotated away.
- **Client:** no change needed. `ClientWorld.Apply(SetCluster)` (`ClientWorld.cs:2896-2999`) already clears the
  sector view/treasures, calls `SetStation(NULL)`, sets `SectorId`/`PoseSectorId` and adopts the own cookie;
  `Apply(RelaunchShip)` already takes the own cookie through `Apply(ShipReset)`. Both are native shapes.

## 4. Native parity

| Behaviour | Native cite (re-grepped) | Verdict |
| --- | --- | --- |
| Player relaunch cookie | `FedSrv/FedSrv.CPP:13015-13016` (`if (pfsShip->IsPlayer()) pfmLaunch->cookie = pfsShip->GetPlayer()->NewCookie();`) | **Native parity** |
| Relaunch for a drone/NPC stays `0` | same lines (the `IsPlayer()` guard) | **Native parity** |
| Ripcord cluster move → `SET_CLUSTER` + `NewCookie()` + exports, also same-cluster | `FedSrv/FedSrv.CPP:13442,13482`; `FedSrv/fsship.cpp:742-767`; `Igc/shipIGC.h:233-240`; `FedSrv/FedSrv.CPP:13525-13527` | **Native parity** (own ship only; children = P1034) |
| Cookie gate that drops stale updates | `FedSrv/FedSrv.CPP:4851-4864` | Native rule the fix satisfies |
| Relocate target state (docked/boarded/lobby/dead; dock cleared) | No native relocate cheat; native's teleport arm requires a free-flying ship (`FedSrv.CPP:7040-7073`); client dock clear `clintlib/appmsg.cpp:1044-1049` | **Deliberate port hardening** (documented deviation on docked: allowed + cleared, evidence in §1) |
| Relocate flying ship `SET_CLUSTER` + cookie | `FedSrv/fsship.cpp:742-767` | **Native parity** (was already correct; pinned) |

Parity rows: `docs/parity/p1029-reposition-sync.csv` (4 rows, validated by `scripts/parity-scorecard.py`,
100% weighted). GLOSSARY: "Server-authorized reposition cookie (P1029)".

## 5. Tests

New, live loopback (real corpus hulls, in-process server, real `FltBot` peers over TCP, artwork required):
`tests/Allegiance.Core.Tests/P1029RepositionSyncTests.cs`

| Test | Proves |
| --- | --- |
| `Relaunched_player_cookie_rotates_and_the_next_ship_update_is_accepted` (179) | player relaunch rotates the cookie and the next `SHIP_UPDATE` is accepted (not dropped) |
| `Drone_lander_relaunch_keeps_cookie_zero` (240) | NPC/drone lander keeps `0` (native `IsPlayer()` guard) |
| `Cross_sector_ripcord_sends_SET_CLUSTER_with_a_rotated_cookie` (342) | cross-sector ripcord: `SET_CLUSTER` + rotated cookie + treasure snapshot |
| `Same_sector_ripcord_still_sends_SET_CLUSTER_and_rotates_the_cookie` (402) | same-sector ripcord still notifies (`Igc/shipIGC.h:233-240`) |
| `Relocate_refuses_boarded_team_lobby_and_dead_and_clears_a_docked_ships_dock` (479) | state gate; docked move clears `StationId` |
| `Relocate_of_a_flying_ship_sends_SET_CLUSTER_with_a_new_cookie` (545) | the finding's claim 3 was already satisfied; pinned |

Reproduction: pre-fix run **2 passed / 4 failed** (the four above failed: player relaunch, both ripcords, state
gate). Post-fix: **6/6, three consecutive runs alone** (696/783/824 ms).

Unskipped: none (no test was unskipped by this task). Changed existing test:
`tests/Allegiance.Core.Tests/LiveRelaunchTests.cs:199` — the old `Assert.Equal(0, p.Cookie)` ("no per-player cookie
machinery") now asserts a rotated non-zero cookie equal to the bot's pose cookie.

Existing classes exercised for the touched paths (all pass):

- `P1029RepositionSyncTests` — 6/6, 3× alone.
- `LiveRelaunchTests` — 4/4, 3× alone.
- `LiveRipcordTests` — 13/13.
- `P554AdminCheatLiveTests` — 7/7 (admin relocate auth/ownership/SET_CLUSTER; the docked-move decision in §1).
- `P655GameStateIntegrityTests` — 35/35 (relocate cheat refusals and `RelocateShipToSector` contract).

## 6. Config / limits

No new setting or named constant: cookies are the existing per-peer byte rotation (`NextCookie`), the messages are
native shapes, and the relocate gate extends the existing hardened cheat surface (P554). Nothing to add to
`server.json` or `docker-compose.yml`.

## 7. Gate

- **Build:** `dotnet build AllegianceCore.slnx` with `--disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false` — **0 warnings / 0 errors** (`~/allegiance-evidence/P1029/build3.log`).
- **Tools:** `tools/build-tools.sh` — **all 140 tool projects built**, exit 0 (`~/allegiance-evidence/P1029/tools2.log`).
- **AOT:** `dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`
  (local, `~/allegiance-evidence/P1029/aot.log`) — result in §7.1.
- **Task tests:** 6/6 Passed, 0 Failed, 0 Skipped — three consecutive runs alone.
- **One Fast suite** (tier `20261116T011254Z`): **14,597 passed / 3 failed / 17 NotExecuted** out of 14,617,
  528.1 s; all nine infrastructure checks passed. Four unrelated executions exceeded the calibrated 11.525 s limit.
  The three failures are existing families, recorded in `docs/TEST_FLAKES.md` (P1029 section):
  `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` (4/4 ×3 alone),
  `NpcDodgeLiveTests.Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes` (6/6 ×3 alone),
  `P739TimerLiveTests.Warp_bomb_notice_…(delay: 5)` (fails 3/3 alone with the already-led one-ULP upper-bound
  signature). The sweep result is **FAIL** on those non-task failures; no second sweep. All raw gate logs and the
exact observed command counts are in `~/allegiance-evidence/P1029/` (`gate-results.txt`, `fast.log`,
`fast-summary.json`, `fast-Core.Tests-main.log`, `build3.log`, `tools2.log`, `aot.log`, `pre-fix.log`, `p554c.log`).

### 7.1 AOT publish

`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained
-p:PublishAot=true`: **succeeded, exit 0**, 0 `IL2026` / 0 `IL3050`, native binary
`Allegiance.Server` (21,874,040 bytes). The publish path restores/builds `Allegiance.Core`,
`Allegiance.Update` and `Allegiance.Server` only.

---

**Native intent.** Every authoritative move of a player's own ship rotates that player's cookie and tells the
client the new cluster: `LandOnCarrierEvent` stamps `NewCookie()` on `RELAUNCH_SHIP` for a player
(`FedSrv.CPP:13015-13016`), and `UseRipcord` ends in `SetCluster` → `CFSPlayer::SetCluster`, which sends a
guaranteed `SET_CLUSTER` with `NewCookie()` and the destination state even for a same-cluster move
(`FedSrv.CPP:13482,13525-13527`; `fsship.cpp:742-767`; `Igc/shipIGC.h:233-240`). The client's pose stream is
validated against that cookie (`FedSrv.CPP:4851-4864`); anything that moves a ship without rotating it freezes
the honest player's controls. Native has no relocated cheat, but its teleport arm only ever moves a free-flying
ship (`FedSrv.CPP:7040-7073`).

**Modern approach.** The port keeps the existing per-peer cookie (`PeerShipUpdate`, P507) and expresses native's
`CFSPlayer::SetCluster` as one shared server helper, `SendOwnerSetCluster`, so the aleph-warp and ripcord paths
cannot drift apart; the relocate cheat is hardened as a single state gate (dead/boarded/team-lobby refused, a
docked target's dock cleared to match the `SetStation(NULL)` its `SET_CLUSTER` tells the client). The harness bot
mirrors the real client's own-cookie adoption. Child-ship cluster propagation remains P1034.
