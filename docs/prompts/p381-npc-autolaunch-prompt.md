# P381 — docked NPC drones auto-launch on their own (port native's `OkToLaunch` pass)

## Why this task exists

Domingo's memory of real native play: "I'm expecting them to undock and
just start working... I never spawned as a miner, I had to defend or
destroy them" — i.e. in native, drone ships that start (or end up)
docked undock and go back to work with **zero player/bot involvement**.
Confirmed this session (research agent) that this port has ported the
entire *rest* of that loop already — an already-undocked NPC will find
an asteroid, fly to it, mine, return, dock, and sell, fully
autonomously (see `MinerDecisionCycleLiveTests.cs`, P156/P149/P160/
P151/P161) — but nothing in this port ever undocks a docked NPC on its
own. That one missing step is why miners "just sit there" instead of
"just working."

## Confirmed real native mechanic

- `CclusterIGC::Update` (`~/Development/Allegiance/src/Igc/clusterIGC.cpp:106-140`)
  — every cluster tick, for each docked ship at each station:
  ```cpp
  if (pship->GetAutopilot() && (pship->GetPilotType() < c_ptPlayer)) {
      if (pship->OkToLaunch(now) || <has an accepted Goto/Build command>)
          pship->SetStation(NULL);   // the actual undock
  }
  ```
- `IshipIGC::OkToLaunch` (`~/Development/Allegiance/src/Igc/shipIGC.h:1912-1976`)
  requires, together: `!m_stayDocked`; docked at least ~10 real seconds;
  either an existing legal accepted command or `PickDefaultOrder(...,
  bDocked=true)` finding something to do; and a friend/enemy-count
  safety check so it won't launch into a lopsided local fight. Checked
  every tick, not event-driven.
- Every drone gets `SetAutopilot(true)` unconditionally at creation
  (`common.cpp:2484`, `CreateDrone`) — only real players ever have
  autopilot off.

## This port's current state (confirmed, reuse exactly — don't reinvent)

- **`Ship.StayDocked`** (`src/Allegiance.Core/Sim/Ship.cs:698`, P148) —
  already the direct port of native's `m_stayDocked`. Already correctly
  set/cleared by the leader/commander `ORDER_CHANGE` handler
  (`src/Allegiance.Server/Server.ShipCommands.cs:1102` sets `false` on a
  LAUNCH command; the stay-docked branch sets it `true`). This task
  reads it, never writes it outside that existing handler.
- **The exact undock precedent to reuse, not reimplement**: the same
  `ORDER_CHANGE` LAUNCH handler
  (`Server.ShipCommands.cs:1100-1122`) already does — for a
  **player-commanded** launch of a docked NPC — exactly what this task
  needs to do on a **timer**: `target.StayDocked = false;` then
  `UndockShipState(target, undockSector)`
  (`Server.Docking.cs:1585`, called from `Server.ShipCommands.cs:1113`
  and from the real-peer path at `Server.Docking.cs:1655`).
  `UndockShipState` is pure state mutation (clears `StationId`, pushes
  the ship clear of `DockRadius`, starts the re-dock debounce) and
  sends **no wire messages itself** — its doc comment confirms
  observing peers learn the NPC's new flying state for free via the
  existing `HeavyShipsUpdate`/`BroadcastShipsUpdate` machinery, the same
  reasoning P147 established. So the new auto-launch pass needs no new
  wire message of its own either — just call the same helper.
- **No `PickDefaultOrder`-feasibility check exists or is needed**: the
  `ORDER_CHANGE` handler's own comment block
  (`Server.ShipCommands.cs:1090-1100`) already established — after
  reading the only ported `PickDefaultOrder` sub-block
  (`shipIGC.h:2230`, the P151 take-cover check, gated on `!bDocked`) —
  that a `PickDefaultOrder(bDocked=true)` call is dead code for this
  port's scope. The real behavior is already covered for free: every
  existing per-pilot-type default-order entry point (`Ship.cs:6010`
  mining, `:6023` layer, `:6047` wingman/attack-defend, `:6061`
  builder) is gated on `StationId is null` — the instant this pass
  clears `StationId`, the ship's existing per-tick update loop
  (`Ship.cs` ~6000-6110, already running every tick regardless of dock
  state) picks it up on its own the very next tick. **Do not port a
  feasibility pre-check** — matches this port's own stated precedent.
- **No "how long has this NPC been docked" tracking exists at all** —
  confirmed by grepping every `StationId = <value>` assignment site
  (`Server.Docking.cs:171, 701-702, 767-768, 1820-1821`, plus P382's new
  spawn-docked site if P382 lands first) — none stamp a timestamp. This
  is the one real gap to fill, mirroring the exact shape/rationale
  `Ship.LastLaunchTime` already uses for the opposite transition
  (`Ship.cs:394`, paired with `Sector.LaunchCooldownSeconds = 8.0`,
  `Sector.cs:1420`).

## Scope

1. Add `Ship.LastDockTime` (a `double`, same shape as
   `Ship.LastLaunchTime`) defaulting sensibly for a ship that starts
   undocked (e.g. `double.NegativeInfinity`, matching `LastLaunchTime`'s
   own default), documenting it as the port of native's per-ship
   "docked since" clock that `OkToLaunch`'s ~10s minimum reads.
2. Stamp `LastDockTime = now` at every existing site that sets
   `ship.StationId` to a non-null value (`Server.Docking.cs:171,
   701-702, 767-768, 1820-1821` — board, rescue x2, teleport, normal
   dock). Use whatever `now` value each site already has in scope (they
   are all inside the server's per-tick/per-message handling, so a real
   timestamp is already available at each — do not add a new clock).
3. Add a new per-tick pass, following this project's existing
   `Server.*.cs` file-split convention (either a new small file or an
   existing one you judge fits, e.g. `Server.Docking.cs`), called once
   per server tick alongside the existing `Step("update", ...)` entry
   in `Server.Broadcast.cs:41` (add a new `Step(...)` there, don't bury
   it inside the existing one). For every `Sector` in the current
   `Mission`, for every `Ship` where `IsNpc && StationId is not null &&
   !StayDocked && (now - LastDockTime) >= <cooldown>`: set
   `StayDocked = false` (already false, but matching the existing
   handler's shape) and call the same `UndockShipState(ship, sector)`
   used by the `ORDER_CHANGE` LAUNCH path. Name the cooldown constant
   clearly (e.g. `NpcAutoLaunchCooldownSeconds`), default it to match
   native's real ~10s.
4. Log plainly per auto-launch (matching this port's existing
   `[orderchange]`/`[npc]`-style tags), e.g.
   `[npc-autolaunch] ship {id} side {sideId} auto-undocked from station {stationId} after {elapsed}s docked`.
5. Port native's friend/enemy local-safety check (`OkToLaunch`'s last
   condition) **only if it's a small, clearly-scoped addition** using
   data this port already tracks per-sector (ship-by-side counts are
   already computable from `Sector.Ships`); if it would require new
   tracking infrastructure, skip it and say so plainly in the report —
   it's a real behavior but secondary to "miners undock at all."

## Explicitly out of scope

- Any `PickDefaultOrder` feasibility pre-check — see above, deliberately
  not ported, matches existing precedent.
- Spawning any new NPCs, or spawning them already-docked at mission
  start — that's P382, a separate task. This task only makes an
  *already-existing* docked NPC (however it got there — spawned,
  returned-to-sell, boarded, rescued, teleported) eventually leave on
  its own.
- Any change to real player docking/undocking behavior — `StayDocked`
  and `UndockShipState` are read/reused exactly as-is; player-driven
  code paths must be byte-for-byte unaffected (an NPC-only gate,
  `IsNpc &&`, is non-negotiable on the new pass).
- Full `misparms`/mission-config decoding — not needed for this task.
- P368-P380 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: using the existing `SpawnNpcShip` test
  harness pattern (see `MinerDecisionCycleLiveTests.cs` for the shape),
  spawn a docked NPC miner (`IsNpc=true`, `PilotType=PilotTypeMiner`,
  `StationId` set to a real station) against a real running server.
  Confirm: it does **nothing** for the first ~10s (still docked), then
  auto-undocks on its own with zero commands sent to it, then — because
  the rest of the loop already exists — proceeds to find and mine a
  real asteroid exactly like the existing P156 test already proves for
  an NPC that starts undocked. Also confirm a *player's own* docked
  ship is completely unaffected (never auto-launches) — the `IsNpc`
  gate is the whole reason this is safe.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes expected.

## Deliverables

1. The auto-launch pass, committed on `master` (not pushed — Domingo
   pushes). Do not create a side branch (this is a normal bug-fix-shaped
   task, not the P379 PR-only exception).
2. Report doc `P381_NPC_AUTOLAUNCH.md` in the repo root: what was
   ported vs. deliberately skipped (feasibility check, friend/enemy
   safety check if skipped), the new `LastDockTime` field and every
   site it's now stamped at, and the live verification performed.
