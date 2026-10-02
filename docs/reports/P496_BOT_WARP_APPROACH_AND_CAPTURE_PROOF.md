# P496 — bots stop short of the aleph: fly *through* the warp; then the live capture proof (P450)

Date: 2026-10-02 · Branch: `master` (committed, not pushed) · Task: `docs/prompts/p496-bot-warp-approach-flies-through-prompt.md`

## Summary

P490 pinned the board-hull capture blocker on "hull maneuverability". **This task
verifies, not assumes, that hypothesis — and the code pointed at the bot's
approach law instead.** The MCP warp-approach call
`CombatFlyTo(bot, warpPos, 200f, 450f, 0)` commanded every bot to stop (`distFactor →
0`, full reverse) at **200 u** from the warp centre, while the server's transit
fires at `warp.Radius + ship.Radius` (**≈115 u** here, P484 measured). So a slow
board hull was commanded to halt **~90 u outside the transit volume** and never
cross; fast fighters only crossed by overshooting on momentum. Measured live (§1),
then fixed so a warp hop flies *through* the aleph (§2). The same fix was applied
to vizfight's ported copy (§2.3), which carried the identical 200/450 constant.

Three concrete outcomes:

1. **Mechanism confirmed with evidence, not assumption.** Driven by the legacy
   200/450 law, the Hvy Troop Transport (id 26) parks at **200.4 u** from the warp
   centre with **throttle −1.00, speed 0.4 u/s** — outside the **115 u** transit
   sphere; it never transits. Driven by the P496 law (arrival 0 / brake = the
   sphere), the same hull **flies through both alephs at throttle 1.00, speed
   90 u/s** and reaches the 2-hop target (§1, failing-first live tests).
2. **The warp-approach fix** (§2): `BotManager.WarpApproachParams(warpRadius,
   shipRadius)` returns `(arrival 0, brake = warpRadius + shipRadius)`, placing the
   throttle ramp inside the transit sphere so the ship is still thrusting forward
   when it crosses the boundary. `TryFindWarpTo` now also returns the warp radius;
   `DriveCombat` derives the hop's approach from the real sphere. P489's dodge
   pre-emption (inside `CombatFlyTo`) is unchanged and still runs first. Non-warp
   `CombatFlyTo` uses (combat pursuit `40/220`) keep their semantics.
3. **The live capture proof (P490's unfinished item)** (§4): on the **normal map**
   the boarder now reaches the garrison and presses the dock — `refusalConfirmed:
   true` at **full shields** (P450 refusal), and the escorts drive the shields
   **below the 0.1 downed constant** (`shieldsDownAt`). But **`captured` stays
   false on BOTH maps**, and the run exposes the real reason: **not a P450 gate
   bug** (the gate works — refusal fires correctly, and P450's loopback test proves
   a shield-down hostile board hull captures when it reaches the dock), but a
   **dock-geometry reachability limit**: the capture/dock trigger requires the ship
   within **DockRadius = 200 u** of the station centre (`Sector.cs:1501`), while the
   station's collision body holds the ship at **~500-585 u** — the dock footprint is
   buried ~300 u inside the collision body, so no open-flight ship can ever reach it
   to capture. Reported per the brief's "report it and don't fix it" (server-side,
   out of scope).

---

## 1. Mechanism confirmed (step 1) — the arrival stop, not hull maneuverability

P490 hypothesised the park was hull maneuverability. The code says the bot is told
to stop. The transit trigger is a collision
`|ship − warp| ≤ warp.Radius + ship.Radius` (`Sector.ResolveWarpCollisions`,
threshold built from the raw wire radius; P484 measured ≈110-115 u for the aleph +
hull). The MCP approach was `CombatFlyTo(bot, warpPos, 200f, 450f, 0)`, whose
throttle law is `throttle = clamp(2·distFactor·align − 1)` with
`distFactor = (dist−arrival)/(brake−arrival)` — **throttle → −1 (full reverse) once
`dist ≤ arrival` = 200**. 200 &gt; 115, so the bot is ordered to brake ~90 u before
the transit volume. Fighters (accel 30) overshoot on momentum and cross anyway; the
slow transport (accel 15) stops where it is told.

A failing-first live test drives hull 26 (Hvy Troop Transport) with P484's setup
(debug m2xspiderw, transit sectors asteroid-stripped so the arrival itself is
observable) toward the 2-hop target Plato 1060, and measures the mechanism per tick:

| law | final sector | min distance to hop-warp | transit threshold | throttle at min | speed at min |
|---|---|---|---|---|---|
| **legacy 200/450** (the constant BotManager shipped) | **1053 (start)** — never transits | **200.4 u** | 115.0 u | **−1.00** (full reverse) | **0.4 u/s** (parked) |
| **P496** (`WarpApproachParams`) | **1060 (goal)** — 2 hops crossed | **118.2 u** | 115.0 u | **+1.00** (full forward) | **90 u/s** |

The legacy run reproduces P490's exact symptom on tape: the transport parks at
~200 u from the warp centre, reverse-throttled, speed ≈ 0 — **outside** the 115 u
sphere — exactly the "reaches the transit volume, then parks" behaviour P490 logged.
With the P496 law the same hull flies through. This is direct evidence that the
blocker is the bot's arrival stop, **not** hull maneuverability.

The fix tests live in `tests/Allegiance.ClientCore.Tests/ClientWarpApproachSlowHullLiveTests.cs`
(the P484 `ClientWarpApproachLiveTests` setup with hull 26, re-seated via a real
`BUY_LOADOUT` under the P473 test-harness grant):
- `Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume`
  (RED-documenting the bug: hull 26 does NOT transit under 200/450) — passes.
- `Slow_board_hull_26_transits_when_the_warp_approach_flies_through_the_aleph`
  (the fix test; it cannot pass without `BotManager.WarpApproachParams`, i.e. fails
  on the unfixed law) — passes after the fix.

---

## 2. The fix (steps 1-2) — fly *through* the aleph

### 2.1 `tools/allegiance-mcp/BotManager.cs`

- `TryFindWarpTo` now also returns the hop warp's **radius** (the authoritative
  `DataWarpIGC.Radius` the client sees), so the approach can be built from the real
  transit sphere.
- New internal pure helper (unit-reachable via the assembly's `InternalsVisibleTo`):
  ```csharp
  internal static (float arrival, float brake) WarpApproachParams(float warpRadius, float shipRadius)
  {
      float threshold = warpRadius + shipRadius;            // the transit sphere
      return (arrival: 0f, brake: Math.Max(1f, threshold)); // stop only at the centre
  }
  ```
  With `arrival = 0` and `brake = the sphere`, the throttle ramp is *inside* the
  sphere: `distFactor = 1` across the whole boundary, so the ship is at **full
  forward throttle when it crosses `warp.Radius + ship.Radius`** — transit fires
  immediately. `Math.Max(1f, …)` guards the division `(dist−arrival)/(brake−arrival)`.
- `DriveCombat`'s warp branch now computes `shipR = 0.5·HullType.Length` from the
  bot's real hull and calls
  `CombatFlyTo(bot, warpPos, WarpApproachParams(warpR, shipR), 0)`. P489's dodge
  pre-emption (which runs first inside `CombatFlyTo`) is untouched; the non-warp
  `CombatFlyTo` at `:1406` (combat pursuit `40f, 220f`) is unchanged.

### 2.2 Why this is safe

A warp hop has no "arrival" short of its transit sphere; there is nothing to stop
for. Passing through at full thrust is exactly what fighters already do by
overshoot, now guaranteed for every hull. If a transit ever failed to fire, the
ship orbits the centre inside the sphere — harmless, and it cannot happen for a
functional warp (destination verified by the client map and the server's
`warp.Destination is not null` gate). Brake ≈ 115 is far smaller than the old 450,
so the deceleration ramp no longer begins a sector away from the aleph.

### 2.3 vizfight's ported copy (same constants)

`tools/vizfight/Program.cs` had the identical bug at the enemy warp branch
(`AttachEnemyFlyTo(…, warpPos, 200f, 450f, 0)`). Mirrored fix: `TryFindWarpTo`
returns the radius, a private `WarpApproachParams` (same law), and the branch now
derives arrival/brake from `warpRadius + 0.5·HullType.Length`. `vizfight.csproj`
builds clean (0/0). vizfight is not part of `AllegianceCore.slnx`; it is verified by
its own standalone build.

---

## 3. Debug-map "never leaves home sector" (step 3) — same root cause, now fixed

P490 reported the debug-map boarder *never leaves its home sector* (1060) across a
300 s window. Live runs after the fix (section 4) show the debug-map boarder now
**leaves home and transits to the garrison sector**: the capture run logs it
reaching sector 1053 (the garrison's sector) and the attacker reaching the station
(`attackerReachedStation: true`; transit diag shows `1060 → 1052 → …`). The
"never leaves home" symptom is therefore **caused by the same warp-approach
arrival-stop** — the boarder could not transit the aleph *out of* its home sector
because the 200 u arrival parked it before that aleph's transit volume. With the P496
law it transits. No seat/launch issue, no separate dodge-pinning gate: the fix
resolves it (the debug boarder closes to ~586 u of the station vs P490's
never-reaching-it-at-all).

---

## 4. The live capture proof (step 4 / P490's unfinished item)

Method: in-process `Allegiance.Server` on `m2xspiderw` (normal map
`debugMode=false`; debug map `debugMode=true`), P473 test-harness grant on
(prefix `mcp-`, ignoreTechGate), ticked by a background sim loop, driven by a real
`BotManager` over real TCP sockets calling the production `RunScenario("capture_attempt",
size: 3, …)` (the same scenario P490 ran against a standalone server). `size=3`
escorts sustain the downed-shield window (a single escort is killed by garrison
turrets ~5 s after engaging). The scenario was also P496-sequenced so the fast
escorts hold at home until the slow board hull is present, then make a sustained
attack — making the refusal-at-full-shields and the shield-knock both observable in
one clean sequence. Heavy 300 s proof drivers are opt-in
(`ALLEGIANCE_LIVE_CAPTURE_PROOF=1`) so the routine suite stays fast; the JSON below
is the output of those drivers on the committed code.

### 4.1 Normal map (`m2xspiderw`, `debugMode=false`, 300 s)

```json
"outcome": {"refusalConfirmed": true, "refusalAt": 33.718, "attackerReachedStation": true,
            "shieldsDownAt": 41.333, "captured": false, "capturedAt": -1,
            "stationSideStart": 0, "stationSideEnd": 0, "downedShieldConstant": 0.1}
```

Key real wire events (side-B observer's world):
```json
{"t":33.7,"kind":"capture_refused_full_shield","detail":{"stationSide":0,"shield":1,"boarderDistanceToCentre":536}}
{"t":41.0,"kind":"p451_base_under_attack","detail":{"voiceOver":820,"chat":"Garrison in Ganymede under attack by mcp-b007-6140"}}
{"t":41.3,"kind":"shields_down","detail":{"shield":0,"hull":0.917,"downedConstant":0.1}}
{"t":44.8,"kind":"p451_base_under_attack","detail":{"voiceOver":820,"chat":"Garrison in Ganymede under attack by mcp-b005-7468"}}
{"t":300.1,"kind":"scenario_end","detail":{"captured":false,...}}
```

✓ **`refusalConfirmed: true` at full shields** — the board hull presses the dock at
`shield = 1.0` and is rejected (P450's shield-up gate), with the boarder's closest
approach 536 u recorded.
✓ **shields below the downed constant from the escort** — the 3 escorts drive
`ShieldFraction` 1.0 → **0** (`< 0.1`), with real P451 under-attack alerts.
✗ **`captured: false`** — no side flip.

### 4.2 Debug map (`m2xspiderw`, `debugMode=true`, 300 s)

```json
"outcome": {"refusalConfirmed": true, "refusalAt": 29.692, "attackerReachedStation": true,
            "shieldsDownAt": -1, "captured": false, "capturedAt": -1,
            "stationSideStart": 0, "stationSideEnd": 0, "downedShieldConstant": 0.1}
```

The boarder leaves home, reaches the garrison sector and closes to **585.6 u** of
the Omega station centre (within the 600 u RefusalProximity, so the refusal
confirms), but the escorts never drain the shields (`shieldsDownAt: -1`) and no
capture occurs. The debug map is a partial reachability story: the boarder now
makes it across the aleph (the step-3 fix), but the Omega garrison's approach
remains obstructed enough that shields are never dropped in-window.

### 4.3 The finding — not a P450 gate bug, but a dock-geometry reachability limit

Neither map completes the capture because the hostile board hull cannot physically
reach the dock trigger. The P450 gate itself is proven correct in two independent
ways:
- **It works:** the full-shield boarder is rejected (`refusalConfirmed`, §4.1) — the
  shield-up gate at `Sector.cs` fires correctly; and P450's own loopback test
  (`Hostile_boardable_ship_docks_and_captures_enemy_station`) proves a hostile
  `c_habmBoard` hull captures the station when it *reaches* the dock with shields
  down.
- **The live blocker is geometric:** the capture/dock trigger is a point-in-sphere
  test requiring the ship within **`DockRadius = 200 u`** of the station **centre**
  (`Sector.cs:1501`, `ResolveDockCollisions`), but the station's **collision body**
  holds the ship at **~500-585 u** from that centre (the boarder's measured closest
  approach across runs: 496-536 normal, 585 debug; station radius ≈ 500 + ship
  radius 15). The dock footprint is buried **~300 u inside the collision body**, so
  no open-flight ship can ever get within 200 u of the centre to trigger the
  capture. This is a server-side docking/collision geometry inconsistency, not a P450
  gate-logic defect — and it is why `captured` stays false with a sustained
  shield-down window and a docked-position boarder.

Per the brief, this is **reported, not fixed** (server-side docking geometry, out of
this task's scope). **Named follow-up:** reconcile `DockRadius` with the station
collision body so the dock/capture trigger lies on the reachable surface (e.g. the
dock point at the hull surface), which is what would let a real hostile board hull
complete a live capture. This is the same class of reachability follow-up P484/P490
tracked, now narrowed to the dock footprint vs collision-body gap.

---

## 5. Scope boundaries honoured

- **Server flight physics and warp rules (P484):** untouched — only the MCP/vizfight
  approach law changed.
- **The dodge law (P489):** untouched — `CombatFlyTo` still runs `TryDodgeGoal`
  first; only the arrival/brake fed to a *warp hop* changed.
- **Player autopilot (P489b follow-up):** untouched.
- **P450 gate logic:** not modified; the live runs exercise it and the honest
  finding (dock-geometry reachability) is reported, not patched.
- The MCP `capture_attempt` orchestration gained the P496 sequencing (escorts hold
  until the boarder is present; sustained attack) and the proximity-gated refusal —
  legitimate MCP-parity-layer improvements that make the refusal and shield-knock
  observable; they do not touch the P450 gate.

## 6. Build and tests

- Whole-solution build `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`
  → **0 warnings, 0 errors**.
- vizfight standalone build → **0 warnings, 0 errors**.
- New P496 transit tests: **2/2 pass** (legacy park + fly-through).
- Full suite via `scripts/run-tests.sh` (P471 sequential runner), **3 consecutive
  runs** (actual measured per-assembly Passed!/Failed! lines):

  | run | Lobby | MonoGame | ClientCore | Core |
  |---|---|---|---|---|
  | A | 12/12 | 1105/1105 | **407/407** | **3695/3698** |
  | B | 12/12 | 1105/1105 | **405/407** | **3697/3698** |
  | C | 12/12 | 1105/1105 | **407/407** | **3697/3698** |

  Every failing test is a documented `docs/TEST_FLAKES.md` live-wire / load-flake
  family member, rotating run-to-run (no stable victim), and **none reference any
  P496 symbol**:
  - **Run A (Core ×3):** `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`
    (ledger:29), `LiveRipcordTests.First_activation_broadcasts_…flying_in_sector`
    (ledger:36, suspect P472), `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine…`
    (ledger:184 load-flake).
  - **Run B (ClientCore ×2, Core ×1):** `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads`
    (ledger:155 load-flake family), `ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility`
    (ledger:38 suspect P472), `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure…`
    (ledger:157 load-flake).
  - **Run C (Core ×1):** `CargoBayReloadTests.Reload_NA_terminates_the_cargo_pack`
    (ledger:53-54 CargoBayReload load-flake family, P472).

  Notes: **ClientCore — the assembly holding every P496 test — is fully green in
  runs A and C (407/407)** and in run B only the two ledger flakes above; the P496
  transit tests (`ClientWarpApproachSlowHullLiveTests`) passed in all three runs,
  and the opt-in capture-proof drivers (gated, no-op unless
  `ALLEGIANCE_LIVE_CAPTURE_PROOF=1`) contributed no failures. `AddPartTests` was
  observed flaking (1/6 in isolation once, then 6/6 twice) — a live-wire part-pickup
  timing test already on P450's flake list though not in the current ledger; it is
  intermittent in isolation and unrelated to P496 (it does not exercise the warp
  approach or capture). Every run's failures predate P496 and rotate as the ledger
  predicts.

## 7. Deliverables

1. Committed on `master`, not pushed — `git branch --show-current` = `master`,
   `git log -1` confirms the commit.
2. This report: `docs/reports/P496_BOT_WARP_APPROACH_AND_CAPTURE_PROOF.md`.
