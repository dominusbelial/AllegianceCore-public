# P554 — Arena mode on a real (networked) server

**Config-gated, privileged `relocate` command through native's cheat-code mechanism, so `spawn_scenario {arena:true}` works on a networked server (221), not just the in-process test.**

## What was wrong (why this task exists)

P539d (`78831d20`) added `spawn_scenario {…, arena:true}` (both sides placed mirrored
in the target sector) but its arena placement depended on `BotManager.Relocator`
(`tools/allegiance-mcp/BotManager.cs:39`, used at `:342`). The **only** place that set
`Relocator` was the in-process test's `WireArenaRelocator`
(`tests/Allegiance.ClientCore.Tests/P539dFairContactLiveTests.cs`), which called server
internals directly (sector remove/add + the `ShipWarped` hook → per-peer `SET_CLUSTER`,
`Server.Lifecycle.cs:679` → `Server.OnShipWarped`). The standalone MCP talking to a
**networked** server had no `Relocator`, so `arena:true` silently fell back to staging
(bots stayed home and travelled). Claude confirmed this on 221 (2026-10-05).

Native has **no** teleport/relocate cheat at all, so the native frame to follow was the
chat-line cheat mechanism:

- `DoCheatCode(CFSShip*, const char* msg)` — `FedSrv/FedSrv.CPP:2814`. Its contract
  ("Returns: whether message was indeed a cheat, and hence doesn't need to be sent as
  chat") is the *consumed, not broadcast as chat* semantic.
- Called from the chat handler `FedSrv.CPP:5023` (debug builds only): a `true` return sets
  `bForward = false` (`:5027`) so the cheat line is never relayed as chat.
- Privilege: `CanCheat()` / `UTL::PrivilegedUser(name, -1)` are checked against the join
  gate at `FedSrv.CPP:664`; privileged pilots get `c_ptCheatPlayer` (`FedSrv.CPP:923`).
- `_TEST` builds set `fCanCheat = true` for every connection (`FedSrv.CPP:914-916`); the
  allow-list/suffix logic of `UTL::PrivilegedUser` is `_Utility/Utility.cpp:46-82`.
- Native cheat tokens in `DoCheatCode`: `bootme, spew, stations, exile, OhSayCanYouSee,
  IfYouWishUponAStar, fourteen, fifteen, ragnarok, gimme, winwinwin, checkbank, capture,
  createdrones, jpd, fireaway, testagcevents` (`FedSrv.CPP:2822-3022`). **No relocate /
  move / teleport token exists anywhere in DoCheatCode — `relocate` is a documented port
  extension.**

The port had **no** cheat/admin concept (e.g. the suicide gate collapses `CanCheat()`
because "this port has… no admin/cheat concept at all", `Server.Lobby.cs:830-837`), but it
has a precedent for **loudly-bannered, config-gated port extensions**:
`liveSettings.testUnlock` (`ServerConfig.cs:160-170`; `Server.Lifecycle.cs:85-92`).

## Implementation

### 1. Privileged cheat-code hook (port of native `DoCheatCode`)
- **Config**: `liveSettings.adminCheats` = `{ enabled: bool, allowNames: [string] }`
  (`ServerConfig.AdminCheatsConfig`). **Disabled unless configured** (absent section =
  byte-identical to pre-P554; no chat line is ever a cheat). Loudly banned at boot like
  `testUnlock` (`Server.Lifecycle.cs:94-102`).
- **Hook**: in the `ChatMessage` dispatch (`Server.Dispatch.cs:307`), `TryHandleAdminCheat`
  runs BEFORE ballot commands / relay, exactly where native calls `DoCheatCode`
  (`FedSrv.CPP:5023`). A `true` return consumes the line — never forwarded as chat
  (native `bForward=false`); a `false` return falls through to ordinary relay.
- **Privilege** mirrors native `CanCheat`/`PrivilegedUser` (`FedSrv.CPP:664`): the sender's
  character name must be in `AllowNames` (case-insensitive, the native list comparison,
  `Utility.cpp:60-67`). Native's `_TEST`/CDKey (`FedSrv.CPP:914-921`) and name-suffix
  (`@HQ`/`@Dev`/…, `Utility.cpp:74-80`) paths are **not** ported — the config-driven
  allow-list replaces them (documented). Disabled ⇒ refused even for an allow-listed name.

### 2. `relocate <shipId> <sectorId> <x> <y> <z> [fx fy fz]` (port extension)
`Server.AdminCheats.cs`. The ONLY ported token; the other native tokens are listed as
follow-ups. Parses the line (token = first word, native `ZString::GetToken`,
`FedSrv.CPP:2819`), resolves the target live peer ship, applies the ownership rule, and
calls the shared relocation.

**Ownership rule** ("a ship the privileged player controls or launched — the MCP's own
bots", decided + justified): a privileged sender may relocate **their own ship** or **any
ship whose character name starts with the MCP harness prefix** (default `mcp-`, the same
prefix `testHarness` uses). This precisely encodes "the MCP's own bots": every MCP bot and
the probe use `mcp-`, so the allow-listed probe (`mcp-probe`) can place every bot it
launched — while an **arbitrary human ship is never touchable**. Consumed either way (never
relayed as chat); refusals answered with an individual no-filter reply (native
`SendChatf(CHAT_INDIVIDUAL_NOFILTER)`, e.g. `FedSrv.CPP:2834`).

**Shared relocation** — `Server.RelocateShipToSector(Ship, Sector, Vector3, Vector3)` uses
the SAME server path a warp transit uses: source-sector remove / dest add / `Sector` /
`Position` / `Orientation` / **velocity zeroed**, then raises the site's `ShipWarped` hook
→ `Server.OnShipWarped` → per-peer `SET_CLUSTER` (`Server.Lifecycle.cs:679`). This is the
**extraction of `WireArenaRelocator`'s logic into the server**, so the in-process test and
the network cheat share ONE implementation (`WireArenaRelocator` now calls
`server.RelocateShipToSector`).

### 3. MCP — networked relocator
`BotManager` installs a networked `Relocator` by default: it sends
`relocate <shipId> <sectorId> <x> <y> <z> [fx fy fz]` through the **probe** connection (the
allow-listed `mcp-probe`) and waits for the **bot's own ClientWorld** to show the new sector
(`SET_CLUSTER` received) — the authoritative proof the server processed it. If the server
refuses (not privileged / disabled), the arena path **throws a clear, explicit error**
instead of silently staging (`BotManager.SpawnBot` + `FailArena`); `spawn_scenario` reports
it as a per-bot failure in the tool output. `arenaNote` updated.

### 4. 221 config (documented only — NOT edited)
Domingo adds the following to `~/allegiance-deploy-compose.server.json.local` under
`liveSettings` (the option must never be on in a default config; it is added here ONLY as
the documented step for the 221 driver):

```json
"adminCheats": {
  "enabled": true,
  "allowNames": [ "mcp-probe" ]
}
```

(The `mcp-probe` name is the standalone MCP's probe/leader connection; the ownership rule
lets it place every `mcp-`-prefixed bot it launches. Do not enable this on a server with
human players.)

## Proof — failing-first tests (`P554AdminCheatLiveTests`, +6, ClientCore.Tests)

| test | what it proves |
|---|---|
| `Non_privileged_relocate_is_refused_and_broadcast_as_normal_chat` | not allow-listed ⇒ refused + forwarded as normal chat (native `fIsCheat=false`) |
| `Relocate_refused_when_option_disabled_even_for_allowlisted_name` | option disabled ⇒ refused even for an allow-listed name (never-on-by-default) |
| `Privileged_relocate_moves_ship_sends_SET_CLUSTER_and_client_world_shows_new_sector` | privileged own-ship relocate moves the ship, zeros velocity, sends SET_CLUSTER, bot ClientWorld shows the new sector |
| `Privileged_admin_relocates_an_mcp_harness_bot_it_launched` | the "launched a bot" ownership leg: allow-listed admin relocates an `mcp-` bot |
| `Privileged_admin_cannot_relocate_a_non_harness_human_ship` | safety leg: a non-`mcp-` human ship is never touchable |
| `Mcp_arena_against_a_refusing_server_returns_an_explicit_error` | MCP arena vs a refusing server (adminCheats off) throws a clear error |

**Must-fail-if-removed** (demonstrated red on today's code, then green after):
- Removing the cheat hook (dispatch → only ballot) makes `Privileged_relocate_moves_ship…`
  FAIL (ship never shows the new sector).
- Removing the networked `Relocator` (constructor) makes `Mcp_arena_against_a_refusing_server…`
  FAIL (no clear "REFUSED" error).

## Proof — networked live (separate server process over TCP + standalone MCP over stdio)

Ran `scripts`-built `Allegiance.Server` as its **own process** with a temp config
(`liveSettings.adminCheats.enabled=true, allowNames=["mcp-probe"]`, plus `testHarness` for
the MCP bots' economy) and drove the **standalone `allegiance-mcp`** over stdio JSON-RPC
(the exact 221-driver shape). `spawn_scenario {size:5, target_sector:"Rigel", arena:true}`.

**Mirrored placement over the network** — the MCP stderr log shows, per bot,
`sent relocate cheat 'relocate <shipId> 1059 <pos> <facing>' via the probe connection` then
`confirmed in sector 1059 (SET_CLUSTER applied to its ClientWorld)`; the server log shows
10 `[warp] peer N ship X -> SET_CLUSTER sector 1059` lines. First snapshot (`t=12s`):
side-0 ships at `Z≈+361..+363`, side-1 ships at `Z≈−629..−634` — **mirrored about Rigel's
centre**, each `target=`-engaged with a specific enemy.

**Initial size-5, tech 4 vs 0 (100s):** 10 bots relocated into Rigel, engaged within ~12s,
tech-4 side 0 scored **2 gunfire kills** (ships 8104, 8100) on tech-0 side 1.

**Matrix — 3 × tech 4 vs 0 and 3 × tech 0 vs 4 (240 s each), gunfire/collision deaths per
side** (from the server log `[eject]`/`[combat]` lines, the P539d methodology):

| run | tech A:0 / B:1 | side0 gunfire | side0 collision | side1 gunfire | side1 collision |
|---|---|---|---|---|---|
| 4v0_a | 4 : 0 | 1 | 0 | 5 | 0 |
| 4v0_b | 4 : 0 | 0 | 1 | 5 | 0 |
| 4v0_c | 4 : 0 | 0 | 0 | 5 | 0 |
| 0v4_a | 0 : 4 | 5 | 0 | 1 | 0 |
| 0v4_b | 0 : 4 | 3 | 2 | 1 | 0 |
| 0v4_c | 0 : 4 | 0 | 2 | 5 | 0 |

In all 6 runs every one of the 10 bots was placed in Rigel via the networked relocate cheat;
engagement began within seconds; the **higher-tech side won decisively** every time (and the
reversal holds when the tech levels swap sides — the same 5/6-dominance P539d measured
in-process, now reproduced over the real networked path).

## Whole-solution build

`dotnet build -m:8` → **0 warnings, 0 errors**.

## Full suite — 3 consecutive runs on the final code (`scripts/run-tests.sh`, assemblies
sequential per P471)

Assemblies were run exactly as `scripts/run-tests.sh` does (Lobby → MonoGame → ClientCore →
Core, sequential); the tool's per-call timeout required issuing each assembly's run as its
own foreground call. Grand total per run: **12 + 1295 + 507 + 3739 = 5553**.

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| A | 12/12 | 1295/1295 | **507/507** | 3737/3739 |
| B | 12/12 | 1295/1295 | **507/507** | 3734/3739 |
| C | 12/12 | 1295/1295 | 505/507 | 3733/3739 |

Every failure is a **pre-existing, documented live-wire load-flake** (cite
`docs/TEST_FLAKES.md`), all **verified green in isolation**:
- Run A Core: `WarpBombTests.Live_whole_mission_warp_bomb…` (TF line 183),
  `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure…` (TF line 157).
- Run B Core: `StationsUpdateLiveTests.Visible_station_received…`,
  `LoadoutChangeLiveTests.Boarding_and_leaving_broadcast_passenger_change…` (TF line 185),
  `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` (TF line 36),
  `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers…` (TF line 216),
  `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` (TF line 272).
- Run C ClientCore: `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone` (TF line 51),
  `FullSessionKillIntegrationTests.FullSession_ThreePeers_…RealCrossSideKill` (TF line 33/148).
- Run C Core: `Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood…` (re-run 3/3
  green isolated; TF line 54 family), `ShipsUpdateLiveTests.Cross_side_ships_see_each_other…` (TF line 245),
  `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine…` (TF line 184),
  `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` (TF line 40),
  `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players…` (TF line 31/267),
  `AsteroidMinedLiveTests.Mining_broadcasts_ASTEROID_MINED…` (TF line 272).

**None of the P554 tests failed in any run** (6/6 in all three ClientCore runs).

## Out of scope (stated)
Other native cheats (follow-ups, tokens listed in `Server.AdminCheats.cs`); any change to
warp/transit physics; making cheats available to normal players.
