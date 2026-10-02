# P489 — client bots steer with native's dodge law (shared with the server NPC autopilot), so they cross asteroid-field hubs

Date: 2026-10-02 · Branch: `master` (committed, not pushed) · Task: `docs/prompts/p489-client-bots-native-dodge-law-prompt.md`

## Summary

P484 (`769ca118`, §5) root-caused the long-standing "bots park at an aleph"
stall (P429, P474) to **one cause**: the MCP/vizfight client bots fly an
**obstacle-blind straight line** to the warp, so in Alderan's dense mining-hub
field they hit rocks, the server applies native impact damage
`2·|Δv|²/128` + an elastic bounce, and the bot never reaches the aleph's
transit radius — while the **server-side NPC autopilot crosses the same field
fine** because it dodges. P485 therefore could not complete the P450/P455 live
proofs.

P489 fixes this at the root:

1. **Extracted native's dodge law into a pure shared module**
   `src/Allegiance.Core/Sim/ShipDodgeLaw.cs` — a faithful port of native
   `Dodge` (`src/Igc/common.cpp:1692-1790`), following the P448a
   `ShipKinematics` extraction precedent: pure inputs (mover position,
   velocity, radius, acceleration; obstacles position/radius/velocity +
   goal-ignore), pure output (dodge goal or none), testable without a live
   server. `Ship.TryComputeDodgeGoal` now **calls it byte-identically** — the
   existing P168/P170 dodge tests and `MultiHopDroneLiveTests` stay green
   unchanged (verified, see §6).
2. **Used it in the client bots.** `allegiance-mcp`'s `CombatFlyTo` (and thus
   the warp approach, which calls it) now pre-empts the straight-line goal with
   the shared law, fed from the bot's own `ClientWorld`: asteroids, stations
   and known same-side ships in its cluster. vizfight has its **own private
   copies** of the flight helpers (it does not share the code path) — named as
   a follow-up, §5.
3. **Live proof — the P484 differential, inverted** (§4). Running the real
   2-hop flight through **real** Alderan with all 134 asteroids present: with
   the fix the bot **transits both hops** with **zero hull damage**
   (min distance to the hop-warp **108.9 u** — inside the ~110 transit volume;
   P484 measured ~7000 u and destruction without dodge). Disabling the dodge
   makes the test **fail** (red→green, §4).
4. **Player autopilot check** (§5): the MonoGame client's `G` autopilot
   (`AutopilotSteering.SteerToward`) is **also obstacle-blind** — a straight
   line with no dodge term. Named as follow-up **P489b** (a small change once
   the shared module + ClientWorld feed are wired in); player flight is
   deliberately **not** changed in this task.

---

## 1. What P484 proved and what P489 changes

P484's differential:
- Asteroids stripped → the same MCP 2-hop flight transits both hops; min
  distance to the hop-2 warp ~107-113 u (inside the transit volume).
- Asteroids present → the bot is destroyed/bounced in Alderan's field; min
  distance to the hop-2 warp ~7000+ u (never reaches the aleph).

Server-side drones cross the same field (`MultiHopDroneLiveTests`), because
the server NPC autopilot dodges. The fix is therefore **obstacle-aware
navigation in the client bot** using the *same law the server already uses* —
not any change to the server, warp data, or routing (all proven correct by
P484).

P489's production-code change is twofold: (a) the shared pure module, and
(b) `BotManager.CombatFlyTo` consulting it. No server behaviour changed
(byte-identical), and no player flight changed.

---

## 2. The shared module: `ShipDodgeLaw`

`src/Allegiance.Core/Sim/ShipDodgeLaw.cs` — a `static` pure module (like
`ShipKinematics`) owning the whole native dodge law with no dependency on a
live server or on `Ship`/`Sector`:

```
Mover(Position, Velocity, RadiusPlusOne, Acceleration)      // RadiusPlusOne = GetRadius()+1; Acceleration = Thrust/Mass
Obstacle(Position, Velocity, Radius, Ignore)                // Ignore = native Ignore()/pmodelSkip decided by the caller
TryComputeDodgeGoal(in Mover, IEnumerable<Obstacle>, dodgeGoalDistance, fallbackTMax, out Vector3 goal)
IsImminent(t, r, acceleration, fallbackTMax)                // t² ≤ 4·r/accel (common.cpp:1749-1751)
SolveForImpact(deltaP, deltaV, speed, radius, out dir)      // common.cpp:59-119 (moved from Ship)
OrthogonalVector(v)                                         // vector.cpp:6-35 (dead-centre fallback, moved from Ship)
```

The law is byte-identical to the pre-existing inline `Ship.TryComputeDodgeGoal`
scan:
- Combined radius `r = obstacle.Radius + mover.RadiusPlusOne` (common.cpp:1731).
- The winner is the **first** obstacle (in enumeration order) with the
  strictly-smallest time-to-impact passing the imminence gate
  (common.cpp:1740-1751) — so `Ship` enumerates candidates in the same
  ships→asteroids→stations order it always did, preserving byte-identical
  server behaviour.
- The away vector is native's `ca` (common.cpp:1761-1783): straight out of the
  centre when already inside (`t==0`), else the closest-approach offset
  `dp − dv·(dp·dv)/|dv|²`, orthogonalised when aimed dead-centre; the goal is
  `Position + ca·(DodgeGoalDistance/|ca|)`.

`Ship.TryComputeDodgeGoal` became a thin adapter: it resolves the mover's own
inputs (`0.5·HullType.Length+1`, `EffectiveThrust()/Mass`) and enumerates the
obstacle categories with the same skip predicates (self / navigation-goal /
enemy same-side-only rule / dead guard) folded into `Obstacle.Ignore`, then
delegates. `Ship.SolveForImpact` now delegates to the module (it was also used
by `SolveForLead` and the tests); `Ship.IsImminent` and `Ship.OrthogonalVector`
were absorbed into the module (their only consumers).

---

## 3. Wiring the module into the client bots (allegiance-mcp)

`tools/allegiance-mcp/BotManager.cs`:

- `CombatFlyTo` now **pre-empts** the straight-line goal with the shared law,
  exactly like the server NPC autopilot's universal Stage-1 `Dodge` check
  (common.cpp:1920/1932 — Dodge fires before every goal branch): when an
  imminent collision is found it flies the dodge goal instead of the bee-line;
  otherwise the law is unchanged.
- `TryDodgeGoal` feeds the module from the bot's own `ClientWorld`:
  - mover pose (position + live velocity) and hull — radius
    `0.5·HullType.Length+1` and acceleration `Thrust/Mass` from the real hull
    via the shared static core (client lacks GA scaling; raw `Thrust/Mass` is
    native's own identity-GA starting value);
  - **asteroids** in the bot's cluster (static rocks, real radius);
  - **stations** in the bot's cluster (radius resolved from the station TYPE
    via the core, like the MonoGame client; sane fallback when unresolvable);
  - **known ships** in the bot's cluster — enemies marked `Ignore` (native's
    "trucker rules of road: we're heavier", common.cpp:1679) so only same-side
    ships are dodged, self excluded.
- The warp approach (`DriveCombat` → `CombatFlyTo(bot, warpPos, 200f, 450f, 0)`)
  inherits the dodge because it calls `CombatFlyTo` — so the exact P484 park
  path (fly at the aleph through the field) is what is fixed.

---

## 4. Live proof — the P484 differential, inverted

`tests/Allegiance.ClientCore.Tests/ClientWarpApproachDodgeLiveTests.cs` boots
the **real** debug arena (`m2xspiderw`, `debugMode=true`: Omega 1053 ↔ Alderan
1052 ↔ Plato 1060), drives a real `ConnectionSession` peer with the MCP routing
law **plus** the shared-dodge pre-empt, and — crucially — **does NOT strip
Alderan's asteroids**. It asserts the bot transits BOTH alephs to the 2-hop
target (Plato 1060), recording the minimum distance to the hop-warp and the
hull damage taken.

**GREEN (fix in place):**

```
[boot] … 13 sectors, 2 sides [Alpha, Bravo], 44 warps, 4 stations, 134 asteroids
P489-ARENA: Alderan client-known rocks = 134            ← nothing stripped
P489-DODGE:
  reached sector 1053 at tick 0     (Omega, spawn)
  reached sector 1052 at tick 918   (crossed hop 1 → Alderan)
  reached sector 1060 at tick 1818  (crossed hop 2 → Plato)
  ARRIVED goal sector 1060 at tick 1818
P489-DODGE: final sector 1060 vs goal 1060; minDist-to-hop-warp = 108.9 u; min hull fraction = 1.000 (damage 0.0%)
```

The bot crosses **both** alephs through the 134-rock Alderan field; its minimum
distance to the hop-warp is **108.9 u — inside the ~110 transit volume** (the
exact opposite of P484's ~7000 u park), and it took **0.0% hull damage** (the
native law clears every rock, no impact damage at all). This is P484's
differential inverted, proven live.

**RED (dodge disabled — the "unfixed" baseline):** with only the dodge pre-empt
commented out, the same test **fails**:

```
P489-DODGE:
  reached sector 1053 at tick 0
  reached sector 1052 at tick 844
P489-DODGE: final sector 1052 vs goal 1060; …
Assert.Equal() Failure: Values differ   ← does not reach 1060
Failed! 1, Passed 0
```

Without the dodge the bot stalls in Alderan at the hop-2 aleph (final sector
1052), never completing the second hop — the P484 park reproduced in-test. The
exact stall geometry varies run-to-run (P484 observed outright destruction on
its particular spawn line; here it bounces at the aleph mouth), but the
invariant is clear and asserted: **no dodge ⇒ does not reach 1060; dodge ⇒
reaches 1060 cleanly.** Red→green confirmed empirically.

---

## 5. vizfight scope note + the player-autopilot finding (P489b)

**vizfight does NOT share the code path.** `tools/vizfight/Program.cs` has its
own private `FlyTo` (in-process `ScBot`/`Ship`) and `AttachEnemyFlyTo`
(`ConnectionSession`), neither of which is the MCP `CombatFlyTo`. As the task
permits, this is named a follow-up: wire the shared `ShipDodgeLaw` + the peer's
`ClientWorld` into vizfight's two flight helpers the same way. (vizfight is
also outside the solution build, so it is not part of this stage's build/test
verification.)

**Player autopilot — obstacle-blind (finding).** The MonoGame client's `G`
autopilot (`Game1.TryGetAutopilotControls`, `_autoPilotEngaged`) steers with
`AutopilotSteering.SteerToward(pos, orient, targetPos, vel, brakeDeceleration)`
— a pure straight-line "face and thrust" law with **no dodge/obstacle term**
(read, `Flight/AutopilotSteering.cs`). So yes, a player flying `G` through a
dense field is as obstacle-blind as the bots were. Native players' autopilot
uses the same goto/avoid law, so this is a genuine parity gap.

It is a **small change** to close: `SteerToward` is already a pure function on
plain inputs, and `ShipDodgeLaw` is now the shared pure module — feeding it the
player's `ClientWorld` (asteroids/stations/same-side ships in the cluster,
exactly as the bot does) and pre-empting the target with the dodge goal would
make the player autopilot avoid obstacles. Because the task explicitly says
"don't change player flight in this task," this is **not implemented here** —
tracked as follow-up **P489b**.

---

## 6. Build and tests

- Whole-solution build `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`
  → **0 warnings, 0 errors**.
- Byte-identical server check: the pre-existing P168/P170 `NpcDodgeLiveTests`
  and `MultiHopDroneLiveTests` all pass **unchanged** with the refactored
  `Ship.TryComputeDodgeGoal` (7/7), confirming the extraction preserves server
  behaviour.
- New pure module tests `ShipDodgeLawTests` (8 tests, `Allegiance.Core.Tests`):
  perpendicular dodge on a collision course, no-dodge when off-path,
  goal-ignored, nearest-impact-wins, moving-obstacle relative velocity,
  already-inside straight-out, non-positive-acceleration fallback, and the
  moved `SolveForImpact` intercept time — all green in milliseconds, no live
  server.
- New live proof `ClientWarpApproachDodgeLiveTests` — **red** on unfixed (dodge
  off: stalls in Alderan), **green** on fixed (transits both hops, 0% damage).
- Full suite via `scripts/run-tests.sh` (the P471 sequential runner), 3
  consecutive runs:

  | run | Lobby | MonoGame | ClientCore | Core |
  |---|---|---|---|---|
  | 1 | 12/12 | 1079/1079 | 389/389 | 3680/3683 |
  | 2 | 12/12 | 1079/1079 | 388/389 | 3682/3683 |
  | 3 | 12/12 | 1079/1079 | 388/389 | 3682/3683 |

  The failures, by run (all the documented P472 live-wire timing / load flake
  family from `docs/TEST_FLAKES.md` — rotating run-to-run, no stable victim,
  and green in isolation where checked):
  - **Run 1 (Core ×3):** `ShipAsteroidCollisionTests.Loopback_bounced_ship_velocity_and_position_reach_the_wire` (NRE in the loopback helper — **green in isolation**, 355 ms), `ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions` and `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` (heavy-slot counting under load — **green in isolation**, same batch).
  - **Run 2 (ClientCore ×1, Core ×1):** `FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill` (the ledger's `FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill`, 1/3 "suspect (P472)" live-wire timing — reproduced intermittently even in isolation, 1/2, matching its documented 1/3; pre-existing), and `LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` (the ledger's `LiveRipcordTests…` live-wire family — **green in isolation**, 157 ms).
  - **Run 3 (ClientCore ×1, Core ×1):** the SAME `FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill` (a live-wire ready-relay race, ~1/2 intermittent even in isolation — confirmed P472 "suspect", and the test references **no** P489 symbol: it drives raw `ConnectionSession` peers, not `BotManager`/`ShipDodgeLaw`), and `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` (same LiveRipcord live-wire family — **green in isolation**, 152 ms).

  **None reference P489 symbols.** P489's delta adds two new test classes
  (`ShipDodgeLawTests` ×8 and `ClientWarpApproachDodgeLiveTests` ×1) and the
  bot/comms changes; neither appears in any failing run, and both are green in
  every run. **Actual measured numbers above — no claimed pass rate not
  observed.**

---

## 7. Scope boundaries honoured

- Server NPC behaviour **not changed** — `Ship.TryComputeDodgeGoal` is
  byte-identical through the shared module (existing tests green unchanged).
- Parity presets (**P490**) not rerun.
- Native `GotoPlan`'s phased waypoints not ported (the flat single-goal
  steering this port deliberately uses is unchanged; the ships-only narrowing
  `c_gpmDodgeShips` remains N/A to the flat form).
- Player flight (`AutopilotSteering`) **not changed** — finding reported,
  P489b named.
- vizfight not modified (separate code path) — follow-up named.
- No parity scenario re-run as proof.

## 8. Deliverables

1. Committed on `master` (not pushed) — `git branch --show-current` = `master`,
   `git log -1` confirms the commit.
2. This report: `docs/reports/P489_CLIENT_BOT_DODGE.md`.
