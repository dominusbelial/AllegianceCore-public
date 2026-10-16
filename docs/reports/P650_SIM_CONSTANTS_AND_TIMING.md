# P650 — loaded simulation constants and native timing

Worktree implementation, 2026-10-14. No commits or pushes.

## Constants

The complete 40-slot inventory, with each slot's native reader, port reader, and
PCore001 before/after values, is in
[`../parity/p650-core-constants.csv`](../parity/p650-core-constants.csv).
Native paths in this report are relative to `~/Development/Allegiance/src/`.

`Igc/missionigc.cpp:2740-2784` sets constructor defaults. Core import then calls
`SetConstants` (`Igc/missionigc.cpp:3283`), whose `memcpy`
(`Igc/missionigc.h:171-173`) overwrites the table. The previous comments claiming
constructor literals override the core were wrong and have been removed.

Changed runtime consumers:

| Slot | Constant | PCore001 before → after | Native reader | Port reader |
|---|---|---|---|---|
| 0/1/2 | LensMultiplier / RadiusUniverse / OutOfBounds | Bounds damage absent → uses 0.2 / 5000 / 25 | `Igc/shipIGC.cpp:368-408` | `Ship.ApplyOutOfBoundsDamage` |
| 3 | ExitWarpSpeed | 15 → loaded 15 | `FedSrv/fsship.cpp:126` | `ServerHost.HitWarpEvent` |
| 4 | ExitStationSpeed | 50 → loaded 50 | `Igc/stationIGC.cpp:307`; `FedSrv/FedSrv.CPP:12959,13476` | launch, carrier relaunch, and ripcord placement in `Server.Docking.cs` |
| 5 | DownedShield | 0.1 → loaded 0.1 | `Igc/shieldIGC.cpp:158`; `Igc/shipIGC.cpp:851` | shield absorption and both station boarding gates |
| 6 | SecondsBetweenPaydays | 60 → loaded 60 | `FedSrv/fsmission.cpp:2253,2516` | `Mission.SecondsBetweenPaydays` |
| 10 | MountRate | loaded 0.2 → loaded 0.2, now honoring zero too | `Igc/afterburnerIGC.cpp:114`; `Igc/shipIGC.h:608,638` | mount ramp and reload debounce |
| 11 | StartingMoney | 16000 → loaded 16000 | `FedSrv/fsmission.cpp:1992` | `Mission.StartingMoneyPerSide` |
| 12 | WinTheGameMoney | **80000** (`dev.Price`) → **72000** | `Igc/missionigc.h:790` | server and client bucket catalogs, without development GA scaling |
| 14 | He3Regeneration | **0.025 → 0.000166667** | `Igc/asteroidigc.h:52-55` | `Sector.He3Regeneration` → `Asteroid.Regenerate` |
| 30 | Income | 600 → loaded 600 | `FedSrv/fsmission.cpp:2523` | `Mission.IncomePerPayday` |

The public mission payday/income/starting-money constants are now instance properties,
so callers read the current mission. The port's existing starting-money and prosperity
multipliers remain 1. Loaded
slots already used for He3 capacity/value/amount, scoring, rating, lifepod endurance,
and warp-bomb delay remain data driven. Standalone objects without a mission retain
fallbacks; a loaded zero mount rate is no longer replaced with a positive default.
The damage matrix already reads the core and is unchanged.

Slot 13 (`RipcordTime`) has **no native runtime reader**: the countdown uses the
hull's ripcord speed divided by side GA (`Igc/shipIGC.cpp:4665`;
`Igc/shipIGC.h:1536-1540`). Slot 39 is unnamed padding, containing a debug-fill
value in PCore001; it must not drive gameplay.

Decoded all **100** artwork core files. PCore001 has starting money 16000, exit
speeds 15/50, prosperity price 72000, and BaseClusterCost **-72**. Variants include
starting money 14000 and 15000, prosperity price 50000/120000, warp-bomb delays
5/7/10, OOB 30/35, warp exit 10, station exit 30, and BaseClusterCost -50/-36.
One correction to the brief: `Race101.igc` and `racecore.igc` use He3 regeneration
**0.0002**; other cores store float representations near 0.000166667. All are
honored without substituting a common rate.

## Native timing and ordering

- Afterburner thrust multiplies by side `c_gaThrust`
  (`Igc/afterburnerIGC.h:113-116`). `ExecuteMove` advances power before building
  this step's kinematic inputs (`Igc/shipIGC.cpp:2945-2957`), and pose replay does
  the same per chunk. The mounted-parts pass advances power again and consumes
  fuel (`Igc/shipIGC.cpp:358-359`; `Igc/afterburnerIGC.cpp:103-164`). Fuel consumption
  still uses the raw part thrust, as native does at line 150.
- The afterburner keeps a float mount accumulator across sub-byte time steps,
  writes wire fraction **255** when it reaches 1, and deactivates on becoming
  unready (`Igc/afterburnerIGC.cpp:111-125`; `Igc/afterburnerIGC.h:160-165`).
- Resupply deactivates the afterburner and clears its power tail
  (`Igc/stationIGC.cpp:474-478`).
- Construction changes the rock into a station at **start + 30 s**, the shrink
  boundary (`Igc/buildingeffectigc.h:19-22,51-54,314-316`). The cosmetic effect's
  remaining 10 seconds do not delay simulation. Selective rock reveal is untouched.
- Fixed weapons update selected mount first, then descending cyclic mounts.
  Turret mounts ascend. Cloak remains first; shield follows the guns
  (`Igc/shipIGC.cpp:305-349`). Tests give only two shots' energy to three guns,
  with deliberately scrambled insertion order, and check the projectile sequence.
- Ripcord voice uses native `1 + int(timeLeft)` change detection
  (`WinTrek/trekigc.cpp:1016-1075`), the existing countdown10…1 assets, and **-7 dB**
  linear gain (native artwork `sounddef.mdl:353-362`, also present in MonoGame content).
  The voice and HUD expiry integrate the server simulation scale across frames,
  matching the simulation countdown (`Igc/shipIGC.cpp:283-289`) under time dilation.
  Inactivity, abort, and completion reset the cue tracker. The flight update calls
  the shared sound manager; pure tests cover boundaries, repeated frames,
  skipped frames, restart, changing simulation scale, assets, and gain. These
  checks run headlessly; audio-device playback was not manually auditioned.
- A tapped NPC miner picks a new order (`Igc/shipIGC.cpp:2240-2244`), ignores
  rocks with less than a quarter-load after other same-side reservations
  (`Igc/common.cpp:592-613`), and can unload a partial bag
  (`Igc/shipIGC.h:2280-2339`). This transition was missing; fixing the rate alone
  would leave miners waiting for regrowth. The departure-rock collision exemption
  is preserved so the miner can leave.

## P629 coordination

P629 has **not** landed in this checkout. No crowded-sector cost implementation or
hardcoded -36 consumer exists here. Its six native slots are inventoried in the
CSV: 33–38, especially **37 = -72 in PCore001**. P629 must read all six from each
mission's core (`Igc/clusterIGC.cpp:163-197`), rather than constructor defaults.
This task does not implement P629's separate crowded-sector rule.

## Local mining verification

`P650ServerRulesTests.Local_mining_battle_reports_income_and_leaves_drained_rocks`
boots a local two-side server on `m2xspiderw.igc` (PCore001, IC versus GT), runs
both initial autonomous miners through real navigation, mining, warp, sale, and
relaunch loops for **1200 sim seconds**, and compares the retired rate against
the loaded rate:

| Regen slot 14 | Mining proceeds sold | Combined miner income |
|---|---:|---:|
| Previous 0.025 | 18000 credits | 15.0000 credits/s |
| Loaded 0.000166667 | 18000 credits | 15.0000 credits/s |

Travel and sale cadence limit this stock-map run; neither miner exhausts its
large stock deposit during the measurement. Thus correcting regrowth does not
change the measured early-game sale rate. This is a measured result, not a claim
that depleted fields have equal income. A fully drained **100-unit** rock supplies
at most **0.0166667 ore/s**, versus **2.5 ore/s** under the retired rate, before
accounting for the decreasing deficit.

The same live-server test substitutes a drained 100-unit rock under an actively
drilling miner to expose that supply limit. Miner **8096** leaves asteroid
**3194**, selects unload station **4328**, and carries its partial load home.
The assertion accepts a replacement rock or an unload order, and rejects idle
waiting on the depleted rock. The ordinary stock-map multi-cycle miner regression
is also included in the repeated affected-test runs.

No rig/221/200v200 work was run.

## Regression fixture updates

- Construction now asserts the rock still exists at start + 29.99 s and becomes
  a station at exactly start + 30 s.
- Existing income assertions read the mission's instance property. The GA cost
  tests expect slot 12 for team money, preserving the exemption from research GA.
- The P649 synthetic capture case explicitly sets slot 5 to 0.1; its otherwise
  zero-filled table no longer inherits a hidden hardcoded threshold.
- The standby logging test passes `false` to the miner decision method's new
  optional `tapped` argument when invoking it by reflection.

- Mine attrition and mine visibility fixtures use remote coordinates (including
  a 900,000-unit peer separation). Their test cores explicitly enlarge slot 1,
  keeping unrelated bounds damage out of those assertions while retaining the
  original collision/scanner geometry.

## Verification

- `dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings / 0 errors**.
  Log: `/tmp/p650-final/build.log`. The fresh checkout first needed `dotnet restore`;
  all subsequent mandated builds used `--no-restore -m:1`.
- **159 selected test methods × 3 isolated runs = 477 final passing method runs.**
  Theory rows ran together under their individual method filter. This includes
  every new P650 case, affected economy/mining/equipment/construction/travel
  regressions, fixture corrections, and the full-suite timing failures retried
  alone. No selected method remains failing. Manifests and individual real
  `dotnet test --no-build --filter FullyQualifiedName=...` logs:
  `/tmp/p650-verification/`, `/tmp/p650-final/`, `/tmp/p650-post/`.
- The three final mining runs produced identical income and depletion-order
  results. Raw output: `/tmp/p650-final/income-1.trx` through `income-3.trx`.

The required full assemblies ran using real `dotnet test tests/<assembly>
--no-build`, including loopback/socket cases:

| Assembly | Passed | Failed in full run | Skipped | Result file |
|---|---:|---:|---:|---|
| Core | 4170 | 1 timing flake | 0 | `/tmp/p650-final/Core-full.trx` |
| ClientCore | 867 | 1 timing flake | 0 | `/tmp/p650-final/ClientCore-confirmed.trx` |
| MonoGame | 1972 | 0 | 3 | `/tmp/p650-final/ClientCore.MonoGame-full.trx` |

Full-suite failure classification, following the brief and `docs/TEST_FLAKES.md`:

- `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`:
  already marked `Lane=LiveTiming`; positional assertion drifted during the wire
  wait. **3/3 passes alone** after the full run, together with 3/3 for each of the
  other carrier-relaunch methods.
- `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`:
  the fixture observed a different asynchronous logon order, producing sides
  `(0,1,0)` instead of its expected order. **3/3 passes alone** after the full run.
  This existing connection-order flake is also recorded in the flake ledger.

These are classified load/timing flakes, not simulation regressions; neither was
weakened or excluded. Their final retry logs are in `/tmp/p650-post/`. Other
preliminary wire timing failures (`ShipsUpdateLiveTests.Cross_side...`,
`P590PodRescueTests.Pod_pickup...`, `StationUnderAttackAlertLiveTests.Second_hit...`,
`ShipDeleteGoneLiveTests.SameSide_Warper...`) also passed **3/3 alone** and did not
remain in the final full-run results.

The three MonoGame skips are the existing `MeshyPackTests` checks for an absent
generated optional content pack. All new P650 cases executed.

For an additional local combat check, a temporary copy of `tools/livecombat`
explicitly starts the mission, offsets the four lanes away from station docking,
and enables friendly fire so seating order cannot suppress the synthetic fight.
It references the final built Core/Server/Bot assemblies. **Eight TCP bots,
4/4 lanes with real `KILL_SHIP(105)` receipts, clean shutdown, exit 0.** Harness:
`/tmp/p650-battle/`; log: `/tmp/p650-post/battle.log`. The unmodified legacy demo
stayed in `NotStarted`, so its initial zero-kill run was a harness setup issue.
No production behavior was changed for this check.

`git diff --check` is clean. All changes are left in the working tree; no commits
or pushes were made.
