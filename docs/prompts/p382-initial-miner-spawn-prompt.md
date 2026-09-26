# P382 — spawn each side's initial miner drones, docked at home, at mission start

## Why this task exists

**Depends on P381 landing first** (docked NPC auto-launch) — that task
makes an already-existing docked NPC undock and start working on its
own; this task is what actually puts miners into a live game in the
first place, so P381's behavior is visible without a delegate/test
harness manually spawning one. Together they close Domingo's report:
"I'm expecting them to undock and just start working... I never
spawned as a miner, I had to defend or destroy them."

## Confirmed real native mechanic

- At mission start, for each side's home station, the server auto-buys
  `nInitialMinersPerTeam` copies of the miner drone type, docked:
  `~/Development/Allegiance/src/FedSrv/fsmission.cpp:2196-2217` →
  `CreateStockDrone(pdt, pside)`
  (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:2220-2275`) →
  `CreateDrone` (`~/Development/Allegiance/src/Igc/common.cpp:2407-2484`,
  sets `pilotType = c_ptMiner`, `abmOrders = c_aabmMineHe3`,
  `SetAutopilot(true)` unconditionally).
- `nInitialMinersPerTeam` is a **mission-parameter** field
  (`m_misdef.misparms.nInitialMinersPerTeam`), i.e. real per-mission
  config data, not a fixed engine constant. This port's own
  `Wire/MissionDef.cs` currently treats the whole `misparms` block as an
  **opaque 252-byte blob** (`MissionParamsSize = 252`,
  `MissionDef.cs:63` — no individual field is decoded today). Finding
  the real byte offset of `nInitialMinersPerTeam` inside that struct (a
  native IDL/header search, e.g. near `MissionParms`/`MISPARMS` in
  `~/Development/Allegiance/Inc` or wherever `misparms`'s type is
  declared) is worth a quick check, but **decoding the full struct
  field-by-field is explicitly out of scope** — see below for the
  fallback.

## Scope

1. **Determine the miner drone type to spawn from real core data, not
   a hardcoded id** — this port's own established convention (see
   `Server.SpawnNpcShip`'s `droneTypeId` lookup,
   `Server.cs:675-703`, and P379's `ResolveDebugHull`
   name-substring-lookup precedent in `Server.Lifecycle.cs`). At mission
   boot, resolve the real drone type(s) from `mission.Core.DroneTypes`
   whose `PilotType == Ship.PilotTypeMiner` for the map actually in use
   (`m2xspiderw.igc`, already confirmed loaded this session via
   `tools/missiondump`). If a side has more than one eligible miner
   drone type (race-specific hulls), pick deterministically (e.g. lowest
   id) and note the choice in the report — don't guess silently if it's
   ambiguous.
2. **Spawn count**: try the real offset first if it's quick to find
   (see above); if not trivially findable/decodable within this task's
   scope, use a small, clearly-named, documented default constant
   instead (e.g. `DefaultInitialMinersPerTeam`) rather than blocking on
   full `misparms` decoding — say plainly in the report which path was
   taken.
3. **Spawn docked, not flying** — call `Server.SpawnNpcShip` (reuse
   exactly, passing the resolved drone type id) at each side's real
   home station position, then set `ship.StationId` to that station's
   id and `ship.LastDockTime = now` (the field P381 adds — confirm P381
   landed first; if for any reason it hasn't, this task must add that
   one field/stamp itself rather than skip it, since an initial-spawn
   miner with no `LastDockTime` would either never satisfy P381's
   cooldown check or incorrectly satisfy it instantly — pick whichever
   is wrong and fix it, don't leave it undefined). Do this once per
   side, once per configured miner count, at mission assemble time
   (`Server.Lifecycle.cs`, same boot phase P379's
   `ApplyDebugTestTopology` and the real per-side setup already run in
   — read that method for the established shape/logging convention).
4. **This must run for every real game, not just debug mode** — native
   does this unconditionally for every mission that has a nonzero
   `nInitialMinersPerTeam`. Do NOT gate this behind
   `liveSettings.debugMode` (unlike P379/P380). If the resolved miner
   count is `0` for a side (e.g. a map/race genuinely has none), spawn
   nothing for that side — don't force a nonzero default that native
   itself wouldn't produce.
5. **Debug-mode topology interaction (read, don't guess)**: P379's
   `ApplyDebugTestTopology` (`Server.Lifecycle.cs`) sets each side's
   home to Omega/Plato with synthetic stations
   (`OmegaDebugStationId = 90001`, `PlatoDebugStationId = 90002`) — when
   debug mode is active, this task's per-side home-station lookup must
   resolve to *those* synthetic stations (so debug-mode testing shows
   miners at Omega/Plato, not at the real, now-unused Ganymede/Organia).
   Read how P379 already picks "this side's home station" and reuse the
   exact same resolution, don't duplicate a second copy of that logic.

## Explicitly out of scope

- Full `misparms` struct decoding beyond (at most) locating this one
  field's real offset if that's quick.
- A player-facing "buy a miner drone" purchase flow during a live
  game — that's a separate, larger task (the research this session
  confirmed no such flow exists yet; not needed for "see them working
  at start").
- The Wopr-style `CommanderResearchAndExpand` economic dispatcher
  (which cluster to send miners to, tech/expansion management) — a
  separate, much larger future task if Domingo wants that layer.
- `ResolveMining`'s existing documented simplifications (no per-rock
  crowding arbitration) — unrelated, don't touch.
- P368-P381 — unrelated except reusing P381's `LastDockTime` field and
  P379's debug-mode home-station resolution as described above.

## Verification — real proof, not just build/test

- **Live proof, required, normal (non-debug) mode**: boot a real
  server against `m2xspiderw.igc` with `debugMode` unset/false, connect
  a real client, and confirm each side's real home station
  (Ganymede/Organia) shows the expected number of docked miner ships at
  game start — with P381 also in place, confirm they auto-undock within
  the expected cooldown window and begin mining completely unprompted,
  visible from a connected client (radar/local visibility) exactly as
  Domingo described.
- **Live proof, required, debug mode**: repeat with
  `liveSettings.debugMode=true` against the current 3-sector
  Omega/Plato/Alderan test topology — confirm the miners spawn at the
  synthetic Omega/Plato stations (not Ganymede/Organia), auto-undock,
  and head toward Alderan's real asteroids.
- **Regression, required**: confirm no existing test or live behavior
  that assumes a side's home station starts with zero ships breaks —
  check `MinerDecisionCycleLiveTests.cs` and any other test that spawns
  its own NPC at/near a home station for a stale assumption about
  station occupancy.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes expected.

## Deliverables

1. The initial-miner-spawn boot logic, committed on `master` (not
   pushed — Domingo pushes).
2. Report doc `P382_INITIAL_MINER_SPAWN.md` in the repo root: the real
   drone-type resolution used, the spawn-count source (real offset
   found, or the documented fallback constant used and why), and both
   live verifications (normal mode + debug-mode topology) performed.
