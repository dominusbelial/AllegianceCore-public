# P539 — MCP combat bots: lead-aimed fire in a cone, stand-off, a real dogfight loadout, no cloak pulsing, lifepods fly home

## ⚠️ Correction (2026-10-05, superseded by P539b)

**Two headline claims of this report are FALSE and are struck:**

1. **The §6 "shield-regen equilibrium" explanation for 0 kills is false.** The real
   reason P539's bots never died is that P539's dogfight loadout armed them with
   `Lrg Shield 2` (part 45, **3750** strength, **35/s** regen) — P539's
   "lowest PartId that mounts" shield pick — which is effectively indestructible.
   The Adv Scout's NATIVE preferred shield `Sm Shield 1` (part 21) does not even
   mount on the hull (shield mount mask `0x011C` excludes `0x0001`), so a
   native-faithful bot flies unshielded and IS killable. P539b makes the default
   combat loadout the hull's native preferred loadout (scope 0).
2. **The §5 hit rates (29.4% / 38.1% and the ~4,400 "ship hits") are inflated and
   meaningless.** `SampleCombatStats` counted EVERY `PROJECTILE_HULL_HIT` in the
   bot's sector as every bot's own, and "shots" were counted at the fire
   DECISION, not from real fired rounds. P539b attributes shots and hits to the
   real shooter (wire `SourceId` + real PROJ_INST echoes) — the corrected
   numbers are in `docs/reports/P539b_MCP_BOT_REAL_HITS.md`.

See **P539b** (`docs/reports/P539b_MCP_BOT_REAL_HITS.md`) for the corrected
metric, the loadout fix that makes bots really kill, the `CanMountPart` ×
`Sm Shield 1` × Adv Scout finding, per-side tech levels, and the new live numbers.

---

Scope: **`tools/allegiance-mcp` harness only.** No game/server/combat/vizfight/client code
changed. Branch: `master` (see §Follow-ups for the probe-leadership note). Build:
`/home/domingo/.dotnet/dotnet build` — **0 warnings, 0 errors.**

---

## 1. Root causes (from Domingo's live 5v5 on 221, 2026-10-04)

Traced in `tools/allegiance-mcp` before this change:

1. **No lead / intercept aiming.** `EngageShipAt` (`BotManager.cs` pre-P539) called
   `CombatFlyTo(bot, tp, 40f, 220f, stateM)` where `tp` was the target's **current**
   `Latest.Position`, and held `ShipControlState.AllWeapons` unconditionally every tick.
   Fixed forward guns pointed at where a scout *is*, not where it *will be* when the round
   arrives → mostly missed (10 bots, 528 shots, **4** ship hits on 221).
2. **Ramming.** The same call used an **arrival radius of 40 units at the enemy's position**:
   the bot flew *into* its target, no stand-off band, no overshoot/breakaway → dogfights
   collapsed into collisions.
3. **Test-convenience weapon.** Without `weapons`, the loadout used
   `Catalog.DefaultWeaponPart()`/`OuterWeaponPartId()` (`Catalog.cs:209-217,505-516`) — the
   cheapest dispersion-0 gun (**PW A-B Omega 2**, part 216) — a determinism choice for tests,
   not a dogfight gun.
4. **Cloak pulsing.** `EngageShipAt` set `CloakActive` for 4 s of every 18 s (old
   `BotManager.cs:1395-1399`), dropping ships out of visibility/targeting mid-fight.
5. **Lifepods never flew home.** When a bot's ship was beaten, the server ejected it to a
   drifting lifepod hull (same ship id; `Server.Combat.cs:1913-2020`, native
   `FedSrv.CPP:11944-12006`). The MCP bot did not notice and kept running its combat routine
   in an unarmed pod — chasing and ramming until shot down or oxygen-out (P279). Domingo saw
   this as "bots won't die".

---

## 2. Changes

### Lead-aimed fire in a cone + stand-off (scope 1,2,3)

`EngageShipAt` (`BotManager.cs:1389`) now:
- reads the target's **position and velocity** and the bot's own pose/velocity from the
  bot's own `cs.World`;
- solves the native **lead** direction via the SHARED solver
  `ShipDodgeLaw.SolveForImpact` (`ShipDodgeLaw.cs:154`, faithful port of native
  `solveForImpact`, `common.cpp:59-119`, that the wingman's `solveForLead` layers on,
  `shipIGC.cpp:2463-2468`; cf. `Ship.SolveForLead`, `Ship.cs:5085-5103`), using the bot's
  real weapon projectile speed and `AbsoluteF` from the real part/projectile data
  (`TryWeaponBallistics`, `BotManager.cs:1455`);
- holds `AllWeapons` **only** when the target is inside the lead-aim **cone** (bot forward
  within cosine 0.90 of the lead direction — the wingman's fire gate, `Ship.cs:4485`,
  `shipIGC.cpp:2470-2476`) **and** within the weapon's real reach
  (`Speed × Lifespan`, the wingman's `EstimateWeaponRange`, `Ship.cs:5232-5241`) — the pure
  `DecideFire` gate (`BotManager.cs:1710`);
- steers to a **stand-off** goal that keeps the nose on the lead direction but never the
  target's position: `StandOffLeadGoal` (`BotManager.cs:1731`) places the goal
  `standOff` short of the target along the aim, with `standOff = max(floor + both radii,
  0.72 × weaponRange)` so it never enters the collision ring and always stays in range.
  `CombatFlyTo` (with its existing P489 imminent-collision dodge branch) is reused unchanged
  to fly that goal.

### Dogfight default weapon (scope 4)

`Catalog.DogfightWeaponPart(hullId)` (`Catalog.cs:236`) — the omitted-`weapons` combat
default: among real weapons that **actually mount the hull** (`CanMountPart`, the server's
ApplyLoadout gate, `shipIGC.h:1030`), are energy-affordable (`EnergyPerShot ≤ MaxEnergy`),
and fire a real damaging projectile (`Power > 0`), pick the **highest projectile Power**,
tie-broken by reach (`Speed × Lifespan`) then lowest `PartId`. `Catalog.DogfightParts`
(`Catalog.cs:273`) wraps it with the standing mountable AB/cloak/shield defaults. Wired as the
default for `spawn_bot` (combat) and `spawn_scenario` (`McpTools.cs:249-253,335,343`). The
dispersion-0 path stays as the explicit **test-only** default (`Catalog.DefaultWeaponPart`,
used by `run_hull_matrix` for determinism) — not the live default.

### No cloak pulsing (scope 5)

Removed the periodic `CloakActive` beat from `EngageShipAt` entirely. The mounted cloak part
stays armed; the bot simply never pulses it in combat.

### Lifepods fly home (scope 6)

`UpdatePodState` (`BotManager.cs:1517`) reads the bot's OWN ship's current hull from its
world and detects a lifepod via the real `HabmLifepod` capability (`DataHullTypeIGC.cs:72`),
which fires regardless of the specific hull id (444/113 etc.). On detection it drops any
committed order/target (no more chasing as a pod) and `DrivePodHome` (`BotManager.cs:1562`)
routes the pod through real warps to the nearest friendly station and flies it at the
station centre to dock (reusing the P496 warp-approach law and `CombatFlyTo`). `PodDocked`
latches on arrival, and once the bot relaunches into a fresh fighter `UpdatePodState` clears
the pod state and combat resumes. Decision logic is the pure `DecidePodBehavior`
(`BotManager.cs:1742`) + `Catalog.IsLifepodHull` (`Catalog.cs:305`).

### Stats (scope 7)

Per-bot **shots fired** (fire-gate passes), **ship hits** (PROJECTILE_HULL_HIT on a
different-side ship in the bot's world) and **kills** (KILL_SHIP credited to the bot) are
tallied from the bot's own wire/world events (`SampleCombatStats`, `BotManager.cs:1487`);
`get_perf_stats` now also returns `combat.{shotsFired,shipHits,kills,hitRate,podsCreated,
podsDocked,podsLingering,perBot}` via `GetCombatSummary` (`BotManager.cs:2002`,
`McpTools.cs:372-396`).

---

## 3. Native citations for aim and fire gating

- Lead solve: `solveForImpact` / `solveForLead` — `src/Igc/common.cpp:59-119,121-149`
  (ported `ShipDodgeLaw.SolveForImpact`, `Ship.SolveForLead` `Ship.cs:5051-5103`).
- Fire gating: native wingman turns to face the lead point (`turnToFace(direction)`,
  `shipIGC.cpp:2470-2476`) and fires when the angle is small and in weapon range
  (`Ship.cs:6033-6098` `UpdateWingmanCombat`; facing gate cosine 0.90, `Ship.cs:4485`;
  range `Speed × Lifespan`, `Ship.cs:5232-5241`).
- Range/velocity data come from real `DataProjectileTypeIGC.Speed/Lifespan/AbsoluteF` and
  `DataPartTypeIGC.Weapon.ProjectileTypeId` — no literals.

---

## 4. Unit tests (failing-first, pure helpers)

`tests/Allegiance.ClientCore.Tests/P539CombatDecisionTests.cs` (9 tests, all pass):

| test | asserts |
|---|---|
| `CrossingTarget_LeadDirectionDiffersFromRawBearing_AndHitsIntercept` | a crossing target yields a lead direction with a lateral (+X) component ≠ the raw current-position bearing, and the fired round meets the target's future position |
| `Fire_On_InsideConeAndRange` / `Fire_Off_BeyondWeaponRange` / `Fire_Off_OutsideAimCone` / `Fire_Off_WhenNoLeadExists` | the cone+range fire gate cuts off correctly |
| `StandOffGoal_KeepsStandOffRing_NeverOnTarget` | the steering goal never equals the target's position and holds the stand-off ring |
| `DogfightDefaultWeapon_IsNotTheDispersionZeroTestGun_AndMountsHull` | the dogfight default is not the dispersion-0 gun, mounts the hull (real `CanMountPart`), deals real damage, is energy-affordable |
| `LifepodHull_PicksGoDockOverCombat` / `LifepodHullIds_DetectedViaRealCapability` | a lifepod-hull bot chooses go-dock over combat; detection via the real `HabmLifepod` capability |

---

## 5. Live headless proof (in-process server, m2xspiderw debug map, both teams co-located; bots driven by a real `BotManager` over real TCP)

Drivers (gated, keep the routine suite fast):
- AFTER: `tests/.../P539CombatProofLiveTests.cs` (spawns `size` combat bots per side with the
  dogfight loadout, rendezvous in the shared sector).
- BEFORE: `tests/.../P539LogBaselineLiveTests.cs` + `scripts/p539-before-baseline.sh`
  (stashes the pre-fix source, runs the same driver against the OLD combat code, restores).
  The BEFORE driver uses the OLD default loadout (dispersion-0 gun + cloak).

Per-side/collision/lifepod figures measured from the captured **server log** (same source for
both builds) and, for AFTER, the new MCP per-bot stats.

| | **BEFORE (old code, in-process)** | **AFTER (P539, in-process)** |
|---|---|---|
| 20 Hz | shots=0¹, collisions **235**, ejects **7**, podsDocked 0 | shots **15,009**, shipHits 4,420, **hit rate 29.4%**, kills 0, collisions **115**, ejects 0, podsDocked 0 |
| 10 Hz | shots=0¹, collisions **72**, ejects **7**, podsDocked 0 | shots **11,641**, shipHits 4,430, **hit rate 38.1%**, kills 0, collisions **101**, ejects 0, podsDocked 0 |
| real 221 log (before) | shots 528, **4** ship hits ≈ **0.76%**, 0 bot kills, bots ramming, "bots won't die" | — |

¹ On this map the OLD code never settled into firing — its bots collapsed straight into
ramming: **235 / 72 bot-bot collisions**, and **7 bots ejected to lifepods from the collision
damage**, which then **lingered** (podsDocked 0) because the old code kept running its combat
routine in the pod. That is exactly the "ramming each other" + "bots won't die" symptoms. The
AFTER code stands off (115 / 101 collisions, **0 ejects**) and lands 4,400+ ship hits at
~29–38% (vs 0.76% on the real 221 before). The hit rate and the "far fewer collisions / no
ramming-deaths" bars are met; the old code's pods never docked while the after code's pod
path is armed (see §6).

Fixed-window run, same map, same spawn count, same 240 s window for the 20 Hz / 10 Hz pairs;
numbers are exactly as measured (see §Live-proof rows quoted in the tool logs).

---

## 6. Lifepod outcome

- **Symmetric fights reach a shield-regen equilibrium: 0 kills / 0 ejects.** Even an
  outnumbered 8v2 AFTER run (32071 shots, 2965 ship hits) produced **0 kills / 0 ejects**
  over 600 s — the dogfight gun's sustained dps does not out-strip Adv Scout shield regen, so
  a correctly-standing-off bot never dies. Killing requires a materially stronger foe (e.g.
  Domingo's player ship on 221) or an out-tech'd opponent. Consequently the pod fly-home path
  is **unit-proven and armed but not live-triggerable in an equal bot-vs-bot fight** in these
  windows.
- The **only** deaths in the whole exercise were the OLD code's **7 collision-damage ejects**
  (bots rammed each other to death), and those pods **lingered** (podsDocked 0) because the
  old code kept running combat in the pod. The P539 stand-off removes the ramming entirely —
  **ejects fell 7 → 0** — which is the removal of exactly the "bots won't die / ramming"
  pathology, at the honest cost that unarmoured-bot-vs-bot duels become non-lethal.
- Pod detection/decision is unit-tested (`LifepodHull_PicksGoDockOverCombat`,
  `LifepodHullIds_DetectedViaRealCapability`); `DrivePodHome` will fly an ejected bot home the
  moment one is actually beaten.

## 7. Suite (3 consecutive runs on the final code, `scripts/run-tests.sh` P471)

Whole-solution build: **0 warnings / 0 errors** (`dotnet build -m:8`).

| run | result |
|---|---|
| 1 | Lobby 12 ✓ · MonoGame 1228 ✓ · **ClientCore 2 flake-fails** · Core 3719 ✓ |
| 2 | Lobby 12 ✓ · MonoGame 1228 ✓ · **ClientCore 1 flake-fail** · **Core 1 flake-fail** |
| 3 | Lobby 12 ✓ · MonoGame 1228 ✓ · **ClientCore 451/451 ✓** · **Core 2 flake-fails** |

Every failure is a **live-wire timing load-flake** in the ClientCore/Core live-network
collections, documented in `docs/TEST_FLAKES.md` or verified to pass in isolation (none
implicate the harness-only P539 changes — the P539 unit tests pass in all three runs):

- `ShipDeleteGoneLiveTests…WatcherLatchesGone_OwnPeerNotHidden` (runs 1,2) — load-flake (P472), `TEST_FLAKES.md`.
- `FullSessionKillIntegrationTests…RealCrossSideKill` (run 1) — suspect (P472), `TEST_FLAKES.md`.
- `AutoReloadOnEmptyTests…RELOAD129_sector_wide` (run 3) — `TEST_FLAKES.md`.
- `ShipIdAllocationLiveTests.Valid_logon_advances_nextShipId` (run 2) — passes 3/3 in isolation (load-flake).
- `AddPartTests.Part_pickup_destroys_then_accept_merges_amount…` (run 3) — passes 5/5 in isolation (load-flake).

## Follow-ups

- **Out of scope (as briefed): probe leadership.** The MCP probe becomes side 0's team leader
  and mission owner (`[lead] side 0 new team leader ship 8113 'mcp-probe'`, bare hull, never
  launches). Involves server leadership rules; noted for a future P-number, not changed here.
- **Bomber/missile tactics** beyond keeping `MaybeFireMissile` working: unchanged.
- Post-live-proof numbers for the full 3× suite are recorded in a follow-up commit to this
  one (this report's §7 table is finalised there).
