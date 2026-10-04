# P528b — Miner never leaves the rock after filling (no sell leg)

**Status:** Fixed on `master` (not pushed). Branch: `master`.
**Fixes a regression introduced by P528 (`942e1dd4`).** Native reference:
`Igc/shipIGC.cpp:2205-2235` (mine→full→`FindTarget(... c_ttFriendly | c_ttStation |
c_ttNearest | c_ttAnyCluster | c_ttCowardly ... c_sabmUnload)`) and the offload /
`PickDefaultOrder` at `:1978-1992`.

---

## Symptom (live, 221 test server — m2xspiderw, IC vs GT, `942e1dd4`)

```
17:20:25.30 [mine] ship 8096 began mining asteroid 3194 'He1051' (ore 7992/7992)
17:21:15.74 [mine] ship 8097 began mining asteroid 3195 'He1052' (ore 11691/11691)
17:21:44.61 [mine] ship 8096 is full (67.5/67.5 ore) — mining paused     ← 79.3 s, correct
17:23:08.05 [mine] ship 8097 is full (90/90 ore) — mining paused         ← 112.3 s, correct
… then NOTHING for 25+ minutes: no [warp], no [npc] decision line, no [mine-sell].
```

After P528 moved He3 mining to native values (rate ≈ 1 ore/s instead of 100×,
capacity = `FloatConstants[7] × GA`, price = `FloatConstants[8] × GA`), a miner
that filled its bag over the now-slow **80–112 s dwell** never began the sell
leg: it sat beside its (huge) asteroid forever and never warped home to sell.
Pre-P528 (`5f0a8156`) the fill took ~1.5 s and the same two ships warped home
and sold promptly.

---

## Reproduction (headless, faithful to 221)

`P528bMinerSellRegressionLiveTests` boots the **real `m2xspiderw.igc`** map with
**civIds `[25, 3]` (IC vs GT)** — the exact 221 server shape — and the
auto-spawned initial miners working at **native rates** (cross-sector sell leg,
2–3 warp hops home). The test asserts **each** initial miner completes **two**
full mine→sell cycles at the P528 amounts.

Instrumenting the stuck state (diagnostic build) captured the moment of full:

```
[DIAG] AT-FULL: side 0 ore 67.5000/67.5000 minAst null isMining True plan 4328 … vel 75.6   ← full, sell decided
[DIAG] AT-FULL: side 1 ore 90.0000/90.0000 minAst 3195 isMining True plan null …            ← full, target NOT cleared
```

and the post-full autopilot trace showed the ship **constantly in the P168 dodge
branch** (`TryComputeDodgeGoal`) with its sell `PlanTarget` set, never reaching
the cross-cluster warp-steering:

```
[P528T DODGE(…) ] ship 8097 ore 90.000/90.000 minAst=null plan=4329 … vel 22.6 Alderan   ← every tick, forever
```

Against the pre-fix code the failing test reports **0 sells** for both ships
(exact bug). With the fix it reports **2 sells each**.

---

## Root cause — a silent collision-avoidance latch (Ship.cs)

The miner decision cycle clears `MiningAsteroidTarget` the instant the bag is
full so the sell leg can decide:

```
Ship.cs:6887   if (Ore >= EffectiveMineCapacity() && MiningAsteroidTarget is not null)
                   MiningAsteroidTarget = null;
```

But the P168 collision-avoidance scan (`Ship.cs:4723`) **exempts the ship's
current `MiningAsteroidTarget` from its obstacle set**:

```
Ship.cs:4972   bool ignoreAsteroid(Asteroid a) => ReferenceEquals(a, MiningAsteroidTarget)
                                             || ReferenceEquals(a, LayerAsteroidTarget);
```

So while mining, the ship parked against its rock does **not** count that rock
as a collision. The moment the bag fills and `:6887` clears the target, the
exemption vanishes: the (often very large) rock the ship is physically beside
becomes an "imminent collision", and `TryComputeDodgeGoal` returns a dodge-away
goal **every tick**. `ApplyNpcAutopilot` returns at the dodge branch
(`Ship.cs:4723-4727`) *before* the sell-leg cross-cluster steering ever runs, so
the full miner with its sell `PlanTarget` set simply orbits its rock and never
warps home. This is a **silent latch** — no log, no exception, the decision
already ran (PlanTarget set) but the navigation is permanently overridden.

### Why P528 exposed it

Pre-P528 the fill took ~1.5 s: the ship transitioned mine→full→sell in a couple
of ticks and departed before settling into the rest-adjacent-to-rock geometry
that trips the dodge. P528's ~80–112 s dwell parks the ship at the rock long
enough that clearing the target leaves it wedged against a dodge obstacle it
cannot steer past (this port has no native strafe thrusters to slide around it).

### Native contrast

Native's `Dodge` (`common.cpp:1692-1790`) emits omnidirectional strafe buttons
via `getDirection` and excludes the model the ship is deliberately heading
toward (`pmodelSkip`, `common.cpp:2003`) — so a departing miner can maneuver
clear of the rock it was mining. This port's flat nose-point-and-thrust
steering (the long-established P155 simplification) cannot strafe; combined with
the vanishing `MiningAsteroidTarget` exemption, the rock becomes an
insurmountable obstacle.

---

## The fix (minimal, faithful to the native "head to sell" flow)

Remember the rock a miner is **leaving** and keep it out of the dodge-obstacle
set until the ship has visibly departed — restoring the mining→full→sell→warp
flow without re-tuning any P528 constant.

1. **`Ship.cs:4579`** — new field `private Asteroid? _departingMineAsteroid;`
   (the rock a full miner is pulling away from).

2. **`Ship.cs:6887-6891`** — when the bag is full and the target is cleared,
   first remember the rock being left:
   ```csharp
   if (Ore >= EffectiveMineCapacity() && MiningAsteroidTarget is not null)
   {
       _departingMineAsteroid = MiningAsteroidTarget;
       MiningAsteroidTarget = null;
   }
   ```

3. **`Ship.cs:4976`** — the dodge scan keeps ignoring that rock until departed:
   ```csharp
   bool ignoreAsteroid(Asteroid a) => ReferenceEquals(a, MiningAsteroidTarget)
       || ReferenceEquals(a, LayerAsteroidTarget)
       || ReferenceEquals(a, _departingMineAsteroid);   // P528b
   ```

4. **`Ship.cs:4581-4595`** — `UpdateMineDeparture()` releases the rock once the
   ship has clearly left it (moved beyond `rock.Radius + hullRadius + 300`, or
   docked — a docked ship has definitely departed), so normal collision
   avoidance resumes after extraction.

5. **`Ship.cs:6728`** — `UpdateMineDeparture()` is called at the top of
   `OnUpdate`, before the autopilot/dodge scan reads it each tick.

No P528 constant is touched; only the departure-collision exemption and its
lifecycle. `MiningAsteroidTarget` clearing (the decision enabler) is unchanged.

---

## Live headless proof — m2xspiderw, IC (side 0) vs GT (side 1), native rates

From `P528bMinerSellRegressionLiveTests` (deterministic sim, 20 ticks/s). Each
initial miner completes **two full cycles**: began → full → warp(s) →
`[mine-sell]` with the P528 amounts → relaunch → began again.

**IC miner 8096 (cap 67.5, station 4328 Ganymede, route Alderan→Plato→Ganymede):**

```
[npc-autolaunch] ship 8096 side 0 auto-undocked from station 4328 after 10.0s docked
[warp] ship 8096 crossed from 'Ganymede' (1050) to 'Rigel' (1059) via warp 2176
[warp] ship 8096 crossed from 'Rigel' (1059) to 'Alderan' (1052) via warp 2186
[mine] ship 8096 began mining asteroid 3194 'He1051' (ore 7992/7992) …
[mine] ship 8096 is full (67.5/67.5 ore) — mining paused
[warp] ship 8096 crossed from 'Alderan' (1052) to 'Plato' (1060) via warp 2185
[warp] ship 8096 crossed from 'Plato' (1060) to 'Ganymede' (1050) via warp 2179
[npc] ship 8096 arrived at station 4328 — PlanTarget/AcceptedTarget cleared, ready to re-decide next tick
[mine-sell] ship 8096 sold 67.5 ore at 80/u = 5400.0 credits to side 0 — ore zeroed      ← cycle 1
[npc-autolaunch] ship 8096 side 0 auto-undocked from station 4328 after 10.0s docked
[warp] ship 8096 crossed from 'Ganymede' (1050) to 'Rigel' (1059) via warp 2176
[warp] ship 8096 crossed from 'Rigel' (1059) to 'Alderan' (1052) via warp 2186
[mine] ship 8096 is full (67.5/67.5 ore) — mining paused
[warp] ship 8096 crossed from 'Alderan' (1052) to 'Plato' (1060) via warp 2185
[warp] ship 8096 crossed from 'Plato' (1060) to 'Ganymede' (1050) via warp 2179
[npc] ship 8096 arrived at station 4328 — PlanTarget/AcceptedTarget cleared, ready to re-decide next tick
[mine-sell] ship 8096 sold 67.5 ore at 80/u = 5400.0 credits to side 0 — ore zeroed      ← cycle 2
```

**GT miner 8097 (cap 90, station 4329 Organia, route Alderan→Juniper→Organia):**

```
[npc-autolaunch] ship 8097 side 1 auto-undocked from station 4329 after 10.0s docked
[warp] ship 8097 crossed from 'Organia' (1051) to 'Gaia' (1062) via warp 2174
[warp] ship 8097 crossed from 'Gaia' (1062) to 'Alderan' (1052) via warp 2188
[mine] ship 8097 began mining asteroid 3195 'He1052' (ore 11691/11691) …
[mine] ship 8097 is full (90/90 ore) — mining paused
[warp] ship 8097 crossed from 'Alderan' (1052) to 'Juniper' (1061) via warp 2183
[warp] ship 8097 crossed from 'Juniper' (1061) to 'Organia' (1051) via warp 2181
[npc] ship 8097 arrived at station 4329 — PlanTarget/AcceptedTarget cleared, ready to re-decide next tick
[mine-sell] ship 8097 sold 90.0 ore at 80/u = 7200.0 credits to side 1 — ore zeroed      ← cycle 1
[npc-autolaunch] ship 8097 side 1 auto-undocked from station 4329 after 10.0s docked
[warp] ship 8097 crossed from 'Organia' (1051) to 'Gaia' (1062) via warp 2174
[warp] ship 8097 crossed from 'Gaia' (1062) to 'Alderan' (1052) via warp 2188
[mine] ship 8097 is full (90/90 ore) — mining paused
[warp] ship 8097 crossed from 'Alderan' (1052) to 'Juniper' (1061) via warp 2183
[warp] ship 8097 crossed from 'Juniper' (1061) to 'Organia' (1051) via warp 2181
[npc] ship 8097 arrived at station 4329 — PlanTarget/AcceptedTarget cleared, ready to re-decide next tick
[mine-sell] ship 8097 sold 90.0 ore at 80/u = 7200.0 credits to side 1 — ore zeroed      ← cycle 2
```

Test summary:

```
[P528B-LIVE] side 0 miner 8096: 2 full cycles, each 67.5 ore @ 80/u = 5400 cr
[P528B-LIVE] side 1 miner 8097: 2 full cycles, each 90.0 ore @ 80/u = 7200 cr
     Passed: 1
```

(The headless proof advances deterministically at the server's 20 ticks/s, so
each line corresponds to a sim instant; the wall-clock `HH:MM:SS` prefix shown in
the live docker-server log is added by that deployment's log sink.)

---

## Tests

- **Failing-first regression test:** `P528bMinerSellRegressionLiveTests.cs` —
  asserts each initial miner completes two full sell cycles at native rates on
  the real `m2xspiderw` map (IC vs GT, cross-sector sell leg). Verified to
  **FAIL against `942e1dd4`** (both ships stay full, `sells=0`) and **PASS with
  the fix** (`side0 sells=2`, `side1 sells=2`).
- **P528 + mining + NPC + autopilot + dodge families** (`FullyQualifiedName~`
  P528 | MinerDecision | NpcCrossCluster | MiningTests | MiningSell |
  InitialMinerSpawn | NpcAutoLaunch | NpcAutopilot | MinerSellRegression):
  **45 passed, 0 failed.**
- **Whole-solution build** (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`):
  **0 warnings, 0 errors.**
- **Full suite** (`scripts/run-tests.sh`, P471) on the final committed code — 4
  consecutive runs (assembly totals, `passed/total`):

  | run | Lobby | MonoGame | ClientCore | Core |
  |-----|-------|----------|------------|------|
  | 1 | 12/12 | 1227/1228 | 438/438 | 1 documented flake (`LiveRipcordTests.Cancel_while_active_aborts_sector_wide`) |
  | 2 | 12/12 | 1228/1228 | 437/438 | 3715/3719 (4 flakes) |
  | 3 | 12/12 | 1228/1228 | 437/438 | 3716/3719 (3 flakes) |
  | 4 | 12/12 | 1228/1228 | 438/438 | 3716/3719 (3 flakes) |

  Every failing test is a pre-existing live-wire / socket-timing load-flake
  already recorded in `docs/TEST_FLAKES.md` (LiveRipcord — lines 36/182/367;
  FullSessionKillIntegration — line 33/148; MineCrossSideVisibility;
  LiveCountdownStartPing — line 383; LiveChatSectorWire; AddPartTests;
  NpcShipFoundation; LiveWinCondition — line 374; ConquestTerritory /
  GameOverPlayers — line 30/267; SideElimination — line 384; AsteroidMined —
  line 272), they **rotate run-to-run** and **each passes in isolation**. None
  touch the P528b mining-departure change (confirmed: every isolated re-run of
  every flake passes; the P528b regression test and the 45-test mining/NPC/
  autopilot family are green in every run). No remaining flake is introduced by
  this change.

  Measured with `scripts/run-tests.sh` (the P471 sequential-assembly runner) on
  `c056ff25`; numbers above are the tool's own per-assembly `Passed/Failed`
  totals.

---

## Out of scope (unchanged)

- No P528 constant re-tuned (rate / capacity / price are correct and live-verified).
- Asteroid lighting (P529) and all client-side work: untouched.
- Miner routing quality beyond "goes to the nearest unload-capable friendly
  station and sells": unchanged.
