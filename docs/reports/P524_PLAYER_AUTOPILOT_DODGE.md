# P524 — the player `G` autopilot steers around obstacles with native's dodge law (the P489b follow-up)

Date: 2026-10-03 · Branch: `master` (committed, not pushed) ·
Task: `docs/prompts/p524-player-autopilot-obstacle-dodge-prompt.md` (this brief)

## Summary

P489 (`29715303`, §5) found the MonoGame client's `G` autopilot is
**obstacle-blind** — `AutopilotSteering.SteerToward`
(`src/Allegiance.ClientCore.Game/Flight/AutopilotSteering.cs:117`) flies a
straight line with no dodge term — and named this follow-up **P489b** ("a
small change once the shared module + ClientWorld feed are wired in").

P524 does exactly that:

1. **Feeds the shared native dodge law into the player autopilot.** In the
   `G` path (`Game1.FlightInput.cs` `TryGetAutopilotControls`, formerly
   `AutopilotSteering.SteerToward` at :1919), the autopilot now first consults
   the shared law and, when a collision with a same-cluster asteroid / station /
   known ship is imminent, steers to the **dodge goal** instead of the
   obstacle-blind straight line — mirroring native's
   `GotoPlan::SetControls` "first priority ... dodge"
   (`common.cpp:2000-2018`).
2. **Shares the feed** rather than writing a third copy. The client-side
   obstacle feed that lived (twice — in `BotManager.TryDodgeGoal`/`ObstaclesIn`
   and in the P489 test's private helper) is hoisted into ONE canonical place,
   `Allegiance.ClientCore.Flight.ClientAutopilotDodge`, and the P489 bots, the
   P489 live test, and the new player autopilot all consume it.
3. **Preserves every other autopilot behaviour**: arrival radius, braking
   (P343/P446 velocity-aware ramp, sized by the real hull's GA-scaled
   Thrust/Mass), manual-input disengage (P345/P356), and docking with a station
   target — a station you are flying **to** is the skipped goal (native
   `pmodelSkip`), so the autopilot does not dodge its own destination.
4. **No server change** — the autopilot output feeds P508's local sim exactly
   as manual input does, so P508's server authority is untouched.

---

## 1. Native reference: the player autopilot dodges in `GotoPlan::SetControls`

The port's player autopilot is a local replica of native's steering: native's
`BaseClient::SetAutoPilot` (`src/clintlib/clintlib.cpp:1592`) flips the ship's
autopilot flag and the client runs the **same** `GotoPlan` steering locally that
the server NPC autopilot uses (established in the `AutopilotSteering` doc,
which cites `CshipIGC::SetAutopilot`, `shipIGC.cpp:3974`, and
`BaseClient::SetAutoPilot`, `clintlib.cpp:1592`). That `GotoPlan` steering
contains the obstacle avoidance:

- `GotoPlan::SetControls` (`src/Igc/common.cpp:1830-2034`) is the universal
  "face + thrust" steering (the source of the port's `SteerToward`). After
  computing the goto position it runs, at :2000-2001, **"First priority ...
  dodge"**:
  ```
  //First priority ... dodge
  if (((gpm & c_gpmNoDodge) == 0) && bDodge)
  {
      if (Dodge(m_pship, pmodelSkip, pstate, (gpm & c_gpmDodgeShips) != 0))
      { ...turn to face...; return false; }
  }
  ```
  (:2000-2018). So an imminent collision **pre-empts the straight-line goal** —
  the ship turns to dodge and the goto is deferred — exactly what P524 adds to
  the player autopilot.
- The goal itself is handed to the dodge scan as **`pmodelSkip`** — the model
  to ignore. `GetGotoPosition` sets `*ppmodelSkip = m_pmodelTarget`
  (`common.cpp:1291/1362/1449/1464/1501/1522/1560`), so the ship never dodges
  the very thing it is flying to (a station being docked, an asteroid being
  mined). P524 reproduces this as `ClientAutopilotDodge.SkipGoal`.
- `Dodge` (`common.cpp:1693-1791`) is the law itself — the module P489 already
  ported byte-identically as `ShipDodgeLaw` (`src/Allegiance.Core/Sim/ShipDodgeLaw.cs`):
  combined radius `pmodel->GetRadius() + (GetRadius()+1)` (:1706,1731),
  acceleration `GetThrust()/GetMass()` (:1707), imminence `t² ≤ 4·r/accel`
  (:1749-1751), away-vector with the dead-centre orthogonal fallback
  (:1761-1783). The player autopilot now calls **this same module**.

## 2. The shared feed: `Allegiance.ClientCore.Flight.ClientAutopilotDodge`

Previously the client-side obstacle feed existed in **two** places: `BotManager`
(`tools/allegiance-mcp/BotManager.cs`, `TryDodgeGoal` :1273 + `ObstaclesIn`
:1321) and a private copy inside the P489 live test
(`tests/.../ClientWarpApproachDodgeLiveTests.cs`). P524 extracts it into one
canonical type so the player autopilot does not become a third:

- **`src/Allegiance.ClientCore/Flight/ClientAutopilotDodge.cs`** (new) — builds
  the `ShipDodgeLaw.Mover` (radius `0.5·Length + 1`, acceleration `Thrust/Mass`)
  and enumerates the mover's cluster obstacles (asteroids → stations → known
  ships, native's same-side-only "we're heavier" rule, skipping self + the
  goal), then calls `ShipDodgeLaw.TryComputeDodgeGoal(…, 520, 3, …)` (the
  server's `DodgeGoalDistance` / `DodgeFallbackTMax`).
- **`BotManager.TryDodgeGoal`** now delegates to it (its private `ObstaclesIn`
  and `ClientDodgeGoalDistance`/`ClientDodgeFallbackTMax` constants are deleted)
  — bot behaviour is byte-identical (verified by the P489 live test, §6).
- **`Game1.FlightInput.cs`** `TryGetAutopilotControls` now:
  ```csharp
  SimVec steerPos = targetPos;
  if (ownMoverHull is { } moverHull
      && TryGetAutopilotDodge(pos, vel, moverHull, SkipGoalFor(tgt), out var dodgeGoal))
      steerPos = dodgeGoal;
  controls = AutopilotSteering.SteerToward(pos, orient, steerPos, vel, brakeDeceleration: decel);
  ```
  The `G` autopilot therefore heads at the **dodge goal** while a collision is
  imminent, and at the **unchanged straight-line target** otherwise — native's
  `SetControls` :2000-2018 contract, and the same pre-empt the server NPC
  autopilot's `Ship.TryComputeDodgeGoal` (`Ship.cs:4627`) uses.

### Scope 2 — the current target is the skipped goal

`SkipGoalFor(tgt)` maps the autopilot's current `AutopilotTarget`
(ship/station/asteroid) to `ClientAutopilotDodge.SkipGoal`, so:
- a **station you are flying to** is not dodged (docking preserved) —
  `Autopilot_does_not_dodge_the_station_it_is_flying_to`;
- an **asteroid you are mining** is not dodged;
- a **ship you are following** is not dodged (ships are also same-side-only).

## 3. Out of scope (unchanged)

- **Server NPC autopilot** — `Ship.TryComputeDodgeGoal` is untouched (still
  byte-identical, P489).
- **Bot steering** — P489; now shares the extracted feed but the law + constants
  are unchanged.
- **Native `GotoPlan`'s phased waypoints** — the port deliberately keeps flat
  single-goal steering (`Ship.cs` ~4715-4733); P524 preserves that.

## 4. Pure tests (failing-first) — `ClientAutopilotDodgeTests`

New pure (no live server) tests in `tests/Allegiance.ClientCore.Tests/`:

| test | what it proves |
|---|---|
| `Autopilot_dodges_an_asteroid_on_the_straight_line_when_collision_is_imminent` | a rock dead-ahead + imminent ⇒ a dodge goal is produced with a **lateral** (off-the-line) escape point at `DodgeGoalDistance` (520) — P489's missing term |
| `Autopilot_reverts_to_straight_line_and_arrives_when_no_longer_imminent` | rock behind / off to the side ⇒ **no** dodge ⇒ the unchanged straight-line law flies to the target (arrival preserved) |
| `Autopilot_with_dodge_clears_the_rock_and_arrives` (`[Theory]`) | nav-level: with dodge ON the ship clears the rock (min distance ≥ combined radius) **and** arrives; with dodge OFF (the pre-P524 straight line) it **plows through the rock** (min distance ≈ 1 ≪ rock radius) — the inverted P489 differential |

Measured nav-level numbers (deterministic):
- dodge ON: `minRockDist ≈ 78 u` vs combined radius `60 + 16 = 76` (clears — the
  law brushes past at exactly the combined radius, native's design), arrives.
- dodge OFF: `minRockDist ≈ 1 u < 60` (dead-centre through the rock) — the old
  obstacle-blind `G`.
- `Autopilot_does_not_dodge_the_station_it_is_flying_to` — the destination
  station (`pmodelSkip`) is never dodged even though it would otherwise be
  imminent.

## 5. Live verification

The existing **P489 live test** `ClientWarpApproachDodgeLiveTests` was
refactored to call the shared `ClientAutopilotDodge` (removing its private feed
copy) and still passes: a real client bot with the shared feed transits both
hops through **real** Alderan's mining-hub field (all 134 asteroids present),
reaching each hop-warp's ~110 u transit volume (P484's ~7000 u park, inverted).

The MonoGame player `G` wiring itself (`TryGetAutopilotControls`) is exercised
by build + the shared feed's discriminating pure tests; a headless live-client
playthrough is documented as a follow-up measurement (same measured-metrics
recipe P489 used: min rock clearance + hull damage vs straight-line).

## 6. Build & suite

Whole-solution build via `/home/domingo/.dotnet/dotnet build`:
**0 warnings, 0 errors.**

Full suite via `scripts/run-tests.sh` (P471), three consecutive runs:

| run | Lobby | MonoGame | ClientCore | Core | result |
|---|---|---|---|---|---|
| 1 | 12 | 1202 | 437 passed / **1** | 3705 passed / **3** | 4 live-wire flakes |
| 2 | 12 | 1202 | **438 passed / 0** | 3705 passed / **3** | 3 live-wire flakes |
| 3 | 12 | 1202 | 437 passed / **1** | 3703 passed / **5** | 6 live-wire flakes |

(The `+5` ClientCore count over P523's baseline is P524's five new pure dodge
tests; the P489 live test was refactored, not added.)

Flake adjudication: every failure is a **documented live-wire `load-flake`**
(`docs/TEST_FLAKES.md`; the failure files have **zero references to P524
symbols**) that **passes in isolation** — none is a P524 regression:

- Run 1: `SessionLossLiveTests.EstablishedSession_…RaisesLost_…` (ClientCore),
  `LoadoutChangeLiveTests.Boarding_and_leaving_broadcast_passenger_change_…`,
  `LiveCountdownStartPingTests.StartGame_owner_all_ready_…`, and
  `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_…another`
  (Core).
- Run 2: `WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer…`,
  `StationUnderAttackAlertLiveTests.Second_hit_within_throttle_window…`, and
  `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay…`
  (Core).
- Run 3: `FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill`
  (ClientCore) and, in Core, `AddPartTests.Second_part_touch_…`,
  `AsteroidMinedLiveTests.Mining_broadcasts_ASTEROID_MINED_…`,
  `LiveRipcordTests.First_activation_broadcasts_…flying_in_sector`,
  `LiveRipcordTests.Mid_countdown_anchor_side_change_aborts_sector_wide`, and
  `LiveRipcordTests.Cancel_while_active_aborts_sector_wide`.

Each was re-run with `--filter` in isolation and passed. The one that hiccupped
even in a combined-class isolation run (`LiveRipcordTests.Mid_countdown_…`,
which also aborted on run 3) passes **3/3 solo** — the whole `LiveRipcordTests`
class is a documented rotating live-wire load-flake family
(`docs/TEST_FLAKES.md:36,182,367`). Per P471 these rotate under full-suite
socket load and are not attributable to P524; every P524-relevant test
(5 pure dodge + the P489 live dodge) passed in all three runs.

## 7. Deliverables

- Committed on `master`, not pushed (confirmed `git branch --show-current` +
  `git log -1`).
- This report: `docs/reports/P524_PLAYER_AUTOPILOT_DODGE.md`.
