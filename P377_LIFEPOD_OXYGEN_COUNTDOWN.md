# P377 — Display the lifepod's real oxygen countdown to the player

Domingo asked directly: "can we display the count down inside the pod?". The port
already runs the real 5-minute lifepod oxygen mechanic (P279,
`Ship.LifepodTimeToDie`). This task surfaces the REAL remaining-fraction value to
the HUD of the pod's own pilot, mirroring native's self-only `GetEndurance`.

## 1. What native really does (gauge placement investigation)

Native's client exposes the pod's remaining oxygen as a **self-only snapshot
member**:

- `WinTrek/consoledata.cpp:533-546`, `ModelData::GetEndurance()` — the dev
  comment is explicit: *"Andon: Returns the time left in a lifepod. Modified to
  only work with self."* It calls `IshipIGC::GetEndurance()` only when
  `pmodel == trekClient.GetShip()` — i.e. only for the local pilot's own ship,
  never a remote one.
- `WinTrek/consoledata.h:231` registers it as a named reflection member
  (`AddMember("GetEndurance", ..., /*default*/1.0f)`), alongside `GetFuel`,
  `GetEnergy`, `GetAmmo`, etc.
- `IshipIGC::GetEndurance()` (`Igc/shipIGC.h:2473-2491`) is the real formula:
  for a lifepod hull in a cluster it is `(m_timeToDie − GetLastUpdate()) /
  GetMyMission()->GetFloatConstant(c_fcidLifepodEndurance)` clamped to `[0,1]`;
  otherwise `1.0f`.

**Gauge binding.** HUD gauges are built data-driven by
`GaugePaneFactory`/`GaugeValuePane` (`WinTrek/trekmdl.cpp:418-440`), which binds a
`GaugeValuePane` to a `Number` snapshot member supplied by the layout; the actual
HUD layout files that map `"GetEndurance"` onto a named on-screen gauge are **not
present in this source checkout** (they lived in runtime artwork, like the
`*Gaugebmp.mdl` gauge textures). So the *specific* native slot for `GetEndurance`
cannot be recovered from source. What IS recoverable is native's gauge set: the
six gauges Hull/Shield/Energy/**Fuel**/Ammo/Thrust (P241, training-overlay layout
`WinTrek/indicator.cpp:514-566`). A coasting lifepod has no fuel concept, and
`GetEndurance` is a self-only depleting 0..1 fraction — semantically identical to
the fuel fraction — so repurposing the **FUEL gauge slot** for the oxygen
countdown is the sensible placement and the most plausible match to native.

**Placement chosen (stated explicitly, not claimed as confirmed native):** this
port reuses the existing **FUEL slot** — both the upper-right fuel gauge ring
(drawn with the real `FuelGaugebmp` texture) and the status-panel FUEL row — and
relabels it **OXY / OXYGEN** while the local player's own ship is an adrift
lifepod, adding a real **m:ss countdown**. This is a deliberate, clearly-labelled
choice; the report does not claim native definitely used the fuel gauge, only
that it is the plausible native repurposing and the natural fit in this port.

## 2. Wire mechanism (server → owner only, no new protocol)

The 47-byte `ServerShipUpdate` (the per-tick own-ship payload, `SingleShipUpdate`
(78), already riding `FM_S_SET_CLUSTER`/`FM_S_SINGLE_SHIP_UPDATE`) has a FUEL
fraction byte (offset 43) that this port has always hardcoded to `1.0` (no fuel
pool exists). P377 reuses that **existing fraction slot** for the real
remaining-oxygen fraction — exactly the "reuse an existing field before adding
new wire protocol" guidance, and semantically clean because a coasting pod has no
real fuel to report anyway.

- `Ship.LifepodEnduranceFraction(now)` (`Sim/Ship.cs`) — the direct port of
  native `IshipIGC::GetEndurance` (`shipIGC.h:2473-2491`): lifepod hull AND still
  in space (`StationId is null`, the same gate that stops the P279 oxygen burn) →
  `(LifepodTimeToDie − now) / endurance` clamped `[0,1]`; else `1.0`. Endurance is
  the loaded core's `FloatConstants[31]` (default 300 s), read the same way P279
  arms the deadline; the P279 `LifepodEnduranceOverrideSeconds` test seam is
  honoured.
- `ServerShipUpdate.FromModel(shipId, ship, time, extraStateBits, carryEndurance)`
  — the fuel byte is filled with `ship.LifepodEnduranceFraction(mission.LastUpdate)`
  ONLY when `carryEndurance` is set. The OWNER's own-ship call site
  (`Server.Broadcast.cs:402`) passes `carryEndurance:true`; every other call site
  — the same-side teammate relay (`Server.Broadcast.cs:637`), NPC/join/teammate
  encodes — keeps the flat `1.0`. This is the **self-only gate on the wire**:
  the declining oxygen value rides only the owner's stream.
- Time base is the sim clock `Mission.LastUpdate` (which equals `now` when the
  broadcast step runs), NOT the wire `uint` tick slot.

## 3. Client rendering (own lifepod only)

- `ShopCatalog.LifepodEnduranceSeconds` — the client reads the SAME core constant
  (`StaticCore.Constants.FloatConstants[31]`, fallback 300 s) the server arms
  pods with, so it can convert the wire FRACTION into a wall-clock m:ss without
  guessing.
- `Rendering/LifepodHud.cs` — pure helpers, unit-testable: `IsAdriftLifepod`
  (hull carries `HabmLifepod` from the own `SHIP_STATUS` `HullId` resolved against
  the loaded core, AND `StationId` is NA) and `OxygenSeconds` (`ceil(fraction ×
  endurance)`).
- `Game1.DrawHud` computes these **only for the local player's own ShipEntity**
  (`world.Ships[MyShipId]`) — so a remote/other player's lifepod never triggers a
  countdown on this client (mirrors native's `pmodel == GetShip()` gate). Fields
  `IsAdriftLifepod` + `OxygenSecondsRemaining` are added to `HudFrame`.
- `HudRenderer` — while adrift-lifepod: the status-panel FUEL row is relabelled
  **OXYGEN** and gets a real **m:ss** countdown beside the depleting bar (amber
  under 30 s); the upper-right fuel gauge is relabelled **OXY** and draws the
  same declining fraction with the authentic fuel-gauge texture.

## 4. Dock-cancel / inactive behaviour

Matches P279's "docking cancels the timer": the `StationId is null` conjunct is
the server-side gate. Once the pod is berthed (rescued via `PerformRescue`, or
death-respawned docked), `LifepodEnduranceFraction` returns `1.0` (inactive) and
the client's `IsAdriftLifepod` is false (`StationId >= 0`), so the countdown stops
and reads full/inactive rather than continuing to tick.

## 5. Tests added

- `LifepodEnduranceDisplayTests` (Core, pure unit, no network):
  - `LifepodEnduranceFraction_tracks_formula_and_clamps` — 1.0 → 0.5 → 0.0 along
    the real formula, clamped.
  - `LifepodEnduranceFraction_is_full_while_docked` — the `StationId is null`
    gate (docked → 1.0).
  - `LifepodEnduranceFraction_is_full_for_non_lifepod` — 1.0 for a normal hull.
  - `FromModel_endurance_rides_only_the_owner_payload` — the same adrift pod
    encoded `carryEndurance:true` carries the mid-depletion fraction while
    `carryEndurance:false` (a remote/teammate relay) stays flat 1.0. **This is
    the airtight, side-independent self-only proof.**
- `LifepodHudTests` (MonoGame, pure): `IsAdriftLifepod` gates on hull capability +
  in-space; `OxygenSeconds` converts fraction→seconds against the real core
  endurance (300 s) — the client-side guarantee that only the local pilot's own
  pod shows a countdown.
- `LiveLifepodEnduranceDisplayTests` (Core, real in-process server + loopback):
  the DRIVER's own `SINGLE_SHIP_UPDATE` fuel fraction counts down
  `1.000 → 0.100` (91 samples) while adrift; a SAME-SIDE teammate watching the pod
  receives its relay (121 updates) but only ever at flat `1.0` (self-only,
  non-vacuous); no watcher's stream ever carries the depleted value; and docking
  the pod makes the driver's countdown read inactive `1.0`.

## 6. Verification performed

- Whole-solution build: **0 warnings, 0 errors** (`AllegianceCore.slnx`, Debug).
- New tests green in isolation (5 Core + 2 MonoGame) and in both full runs.
- Full Core suite `tests/Allegiance.Core.Tests`, run **twice**:
  - run 1: 3352 passed / 5 failed / 3357 — all 5 are pre-existing live-wire
    timing flakes in unrelated tests (ShipsUpdateLive, NpcShipFoundationLive,
    LiveTurretGunnerBinding, LiveCountdownStartPing, CloakVisibilityGateLive);
    each confirmed to pass in isolation (environmental), the TurretGunner one is
    the documented persistent machine-state flake.
  - run 2: 3349 passed / 8 failed / 3357 — a *different* set of pre-existing
    live-wire flakes (AsteroidExport, P366LeaderBotCommand, LoadoutChangeLive,
    LiveWinCondition, LiveTurretGunnerBinding, LiveRipcord, ConquestTerritory,
    CargoBayReload); all confirmed environmental (pass in isolation). The
    varying set across runs is the documented ephemeral-port/socket flakiness of
    the LiveNetwork suite. **None of the failures are the P377 tests in either
    run** — no regressions.
- Full MonoGame suite `Allegiance.ClientCore.MonoGame.Tests`: **729/729 pass**.
- Must-fail proof: with the `carryEndurance` wire wiring neutralised, the
  discriminator test
  `FromModel_endurance_rides_only_the_owner_payload` goes RED
  ("owner should see a mid-depletion oxygen fraction, got 1") — proving it
  genuinely fails if the feature is removed.
- Scope note on "real client" live proof: this is a headless CLI environment, so
  the fully interactive MonoGame session (launch client, eject, eyeball the HUD)
  is not automatable here. The strongest automated live proof delivered is: the
  real-server wire live test (`LiveLifepodEnduranceDisplayTests`) proving the
  driver's own-ship stream carries the real declining fraction (1.000 → 0.100,
  91 samples) while a same-side watcher's copy stays flat 1.0 and docking makes
  it inactive, PLUS the client unit tests proving the render decision
  (`LifepodHud.IsAdriftLifepod` is self-only and `OxygenSeconds` conversion).
  The actual HUD pixels (OXYGEN row, m:ss text, OXY gauge label) are rendered by
  `HudRenderer` from those proven values.

## Out of scope (untouched)

- P279's oxygen mechanic/timer logic (unchanged; this task only surfaces it).
- P376 (target bracket colour), P368–P375 (unrelated).
