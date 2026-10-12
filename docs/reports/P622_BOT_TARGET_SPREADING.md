# P622 — combat bots: spread targets and prioritise threats (anti-swarm)

## Problem

Domingo watched the native 195v195 on the 221 server (2026-10-12): side 0
reached Alderan before side 1's fleet, and **every side-0 bot targeted the one
enemy present — side 1's mining drone (ship 8097, `[initial-miner]`, hull
'Miner')**. `list_active_bots` showed `"target":8097` for every sampled bot:
~195 ships swarmed one miner on one asteroid, colliding with the rock and each
other. Cause: `EngageCombat` (`tools/allegiance-mcp/BotManager.cs`) picked the
**nearest visible enemy in the bot's own sector** with no notion of how many
teammates already engage that target, of target value, or of threat.

## What changed

A new pure, unit-tested selector (`tools/allegiance-mcp/CombatTargetSelector.cs`)
decides which co-located visible enemy a bot engages, replacing the per-bot
nearest-enemy pick:

1. **Engager cap per hull class** — miner/drone 2, fighter 3, bomber 4,
   capital 6. A bot never joins a target that already has its cap of engagers
   assigned this tick.
2. **Threat priority** — ships attacking this bot (recent damage sources from
   the bot's real hull-hit stream) and station-threatening bombers/capitals
   (while a friendly station is co-located) first; then combat ships; then
   miners/drones.
3. **Lone-target detachment-and-hold** — when no enemy combat ship is present
   (only a miner/drone), the target's cap admits a small detachment and every
   other bot **holds / loiters at the staging point** instead of swarming.
4. **Least-engaged spreading** — within a tier a bot picks the target with the
   fewest current engagers (nearest as tie-break), so a fleet distributes
   across distinct ships instead of stacking.
5. **Approach spacing** — several engagers closing on the same target fan out
   laterally (a deterministic per-engager orbit offset, reused from P610's
   `EntryAimPoint` idea); rock avoidance is already the shared P621 convex-hull
   dodge.

The P431 **commanded ship-target path is unchanged and still wins** — a bot under
a real accepted teammate order engages exactly that ship (focus-fire, no cap),
bypassing the selector entirely.

Mechanics: the single `Loop` thread keeps a per-pass engager-accounting map
(reset each pass; bots pick in order and register their choice), so later bots
see a live "already engaged" view — a lock-free coordinated spread.
`list_active_bots` now reports each bot's `target` **and** `engagers` (the
count on that target from the last completed pass) so the anti-swarm cap can be
verified live.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Choose an enemy to attack | `FindTargetShip(…, c_ttEnemy\|c_ttNearest)` **shipIGC.cpp:2030-2057** | design (gameplay) | kept as the WITHIN-TIER tie-break (least-engaged primary, nearest secondary); the P558 wingman law it feeds (`shipIGC.cpp:2356-2604`) is untouched |
| Anti-swarm engager cap / target spreading | **none** — native wingmen concentrate by human wingman/commander orders; there is no automatic "already engaged" bound | design gap (no commander for a test-harness fleet) | **modern design**: per-class cap + least-engaged spreading |
| Target-value / threat tiers | partial: `c_cidDefend` protects a teammate (**shipIGC.cpp:2373-2407**); bombers alert via `c_habmThreatToStation` (**igc.h:5390**) | design | **modern design**: threat→combat→miner tiers, station-threat bombers, per-bot attacker tracking |
| Commanded order wins | wingman `c_cidAttack` command (**shipIGC.cpp:2356**) | design (gameplay) | kept verbatim — P431 ordered path still wins, no cap (focus-fire) |
| Don't grind against rock / stack on target | Dodge owns the tick (**shipIGC.cpp:2410**) | design | kept (shared dodge + P621 convex hulls) + a per-engager lateral fan on approach |
| Miner hull is drone-only / unbuyable | `GroupId < 0` — P470 `load.cpp:1603-1606`, `clintlib.h:1858`; the drone gate runs even under testHarness (**Server.ShipCommands.cs:2550-2553**) | design | documented — live lone-target used a fighter hull (cap 3), see Live proof |

**Check-native note:** the task said to check whether native drones share
targets. They do not — `FindTargetShip` is nearest-only; native dispersal comes
from human wingmen following orders and defending via `c_cidDefend`. Nothing to
port for the cap/tiers, so we designed them (as the task permits: "Where native
has nothing, design something modern and say so").

## Modern alternative considered

| Aspect | Native | Modern option | Chosen | Why |
|---|---|---|---|---|
| Assign bots to targets | nearest-only (humans coordinate) | optimal multi-agent assignment (Hungarian / auction) | greedy **least-engaged** per tick | O(n²) worst-case vs O(n³), deterministic, lock-free in the single-threaded Loop, and reaches the same steady-state (even coverage, no over-pile); the optimum was not worth the tuning sensitivity at 200v200 |

## Verification

### Unit tests — `P622CombatTargetSpreadTests` (19, all failing-first)

On the pre-P622 code the `CombatTargetSelector` symbols do not exist, so these
fail at compile time (the repo's accepted failing-first form, cf.
`P558NativeWingmanAttackTests`). All pass on the final code (and pass in both
full-suite runs):

- **Cap respected / lone-miner**: a lone miner admits a 2-bot detachment, the
  3rd bot HOLDS; caps are miner 2 / fighter 3 / bomber 4 / capital 6.
- **Threat priority**: a threat is engaged before any non-threat combat ship
  and before a nearer miner, even when farther; a station-threatening bomber
  beats an idle fighter.
- **Combat before miners**: with an enemy fighter present a miner is never
  engaged; when every combat ship is over-cap a bot holds rather than farm a
  miner mid-fight.
- **Spreading**: the 2nd bot takes the OTHER fighter (fewer engagers); nearest
  wins the equal-engagement tie-break.
- **Reassignment on death**: a dead target (removed from candidates) is never
  re-chosen; a living target is picked instead.
- **Classification** (native cues): miner/builder/pod/drone → MinerDrone,
  bomber (`c_habmThreatToStation`) → Bomber, carrier/heavy → Capital, else
  Fighter.
- **Approach spacing**: solo engager gets no offset; higher ranks get distinct
  lateral fans of the requested magnitude.

### Whole-solution build

`dotnet build -m:8`: **0 warnings, 0 errors**.

### Suite — `scripts/run-tests.sh`, 2 consecutive runs on the final code

(Run 1 was promoted to a tracked background process after the 600 s foreground
cap; run 2 likewise. Both executed to completion and were polled to exit.)

| Assembly | Run 1 | Run 2 |
|---|---|---|
| Lobby.Tests | 12/12 | 12/12 |
| ClientCore.MonoGame.Tests | 1776/1779 (3 skipped) | 1776/1779 (3 skipped) |
| ClientCore.Tests | 622/623 (1 flake) | **623/623 clean** |
| Core.Tests | 3996/3997 (1 flake) | 3996/3997 (1 flake) |

**Zero P622 failures in either run** (the 19 P622 unit tests + gated live
harness all pass; ClientCore.Tests total went 622→623 with my additions).

Run-1 failures (both `load-flake`, ledgered in `docs/TEST_FLAKES.md`, unrelated
to P622):
- `ClientCore…SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent` — network-loss live timing (same family P610's run 2 reported).
- `Core…AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` — ledgered TEST_FLAKES line 52 (load-flake P472).

Run-2 failure (ledgered, unrelated to P622):
- `Core…P590PodRescueTests.Pod_pickup_order_streams_queued_order_to_real_player_rescuer` — ledgered TEST_FLAKES line 434 (live-wire pod-order timing; P610 cited the same).

Each run is reported as it happened; the flakes are ledgered, not chased.

### Live proof — local in-process 221-map server, 60v60

Method (`P622TargetSpreadLiveTests`, env `ALLEGIANCE_LIVE_P622`): boot the real
221 config (`m2xspiderw` NON-debug; side0 home Ganymede 1050, side1 home Organia
1051, rendezvous Alderan 1052). Place one enemy "lone target" in Alderan
(spawned on side 1, flown straight and parked). Spawn side 0's 60 fighters
(staged toward Alderan), then side 1's 60 fighters 12 s later — so side 0
arrives first and meets the lone enemy alone, then the full enemy fleet arrives
(the "one side arrives first and the enemy miner is present" condition). Sample
`list_active_bots` engagers every 2 s; parse `[collision] ship A x ship B` from
the stamped server log.

**Lone-target note (honest limitation):** every one of the 33 `c_habmMiner`
hulls in `PCore001.igc` is a **drone hull (`GroupId < 0`)** and is refused by the
server even under testHarness — the P470/P441 drone gate always runs
(`Server.ShipCommands.cs:2550`). A real miner hull therefore cannot be bought by
a bot. The harness used a fighter hull (cap 3) as the lone low-capacity target;
the cap/detachment/hold mechanism is identical, only the cap number differs
(3 here vs 2 for a real miner).

**Mandatory live check — bots reach the target:** `botsReached = 121/121`
in both runs.

| Metric | BEFORE (legacy nearest) | AFTER (P622) | Δ |
|---|---|---|---|
| Max engagers on a single target | **60** (the swarm) | **3** (fighter cap) | swarm eliminated |
| Engagers on the lone target during the lone window | 5→…→60 (monotonic pile-up) | 3 (detachment), 57 HOLD | detachment + hold |
| Bots holding at the staging point (lone window peak) | 0 | 57 | the rest hold |
| Collisions / minute | 24.60 | 18.00 | −27% |
| Same-side collisions | 78 | 60 | −23% |
| Bots reached Alderan | 121/121 | 121/121 | no regression |
| Kills | 0 | 0 | n/a (see note) |

Engager distribution, AFTER (lone window): `targets={ 8129:3 }` held steady while
`sideAInAlderan` grew 3→60 and `holding` climbed to 57. When side 1's fleet
arrived the fleet spread across it (`8190:1, 8191:1, …`), max ≤3 per target
throughout.

Kills = 0 in both runs: 60v60 of evenly-matched shielded fighters over a 200 s
window, with the lone window spent largely holding, produced no destruction —
a measured (honest) outcome; the anti-swarm property is the engager cap, which
is decisive.

### Measurement switch

`ALLEGIANCE_P622_LEGACY_NEAREST=1` (default OFF) makes `EngageCombat` revert to
the pre-P622 nearest-enemy pick so the harness can quantify the swarm
before/after with the same binary — the same idea as the env-gated live tests.

## Deliverables

- `tools/allegiance-mcp/CombatTargetSelector.cs` (new) — pure selector, caps,
  classification, spacing offset.
- `tools/allegiance-mcp/BotManager.cs` — coordinated `EngageCombat`, per-pass
  engager accounting, threat tracker, `Engagers` in `list_active_bots`, the
  gated legacy-nearest measurement switch.
- `tools/allegiance-mcp/McpTools.cs` — expose `engagers` in `list_active_bots`.
- `tests/…/P622CombatTargetSpreadTests.cs` (new) — 19 failing-first unit tests.
- `tests/…/P622TargetSpreadLiveTests.cs` (new) — the gated live-proof harness.
- This report.

Committed on `master` (not pushed). See `git log -1`.
