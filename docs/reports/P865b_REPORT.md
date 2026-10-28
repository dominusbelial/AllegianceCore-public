# P865b — two native branches P865 classified away: carrier-relaunch re-task and the unseen-station rock disguise

Lane: pi (worktree `allegiance-pi-p865b`, branch `pi/p865b`). Native reference
`~/Development/Allegiance/src/` (read-only; every rule below re-grepped at
implementation time). **No commits/pushes** — all changes left in the working
tree. Base: master with P865 + P864b merged (`a94c13c7`).

## 0. The P865 regression — resolved: the test raced native-correct NPC flight, not a P865 bug

`RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight`
(ClientCore.MonoGame.Tests) failed 3/3 at the P865 merge for the dispatcher and
1/3–2/3 in this lane's runs — **and also failed at `c6453ab0` (2/3 pass)**, the
commit just before the merge. The enemy NPC is spawned identically in both
(`[npc] spawned ownerless NPC ship … side 1 at (0,0,1500)`); what differs
between runs is only *when* the peer's identity export completes relative to the
NPC's flight.

Root cause: `SpawnNpcShip` without a drone id spawns a **miner** (PilotTypeMiner,
the P160 fallback), and a miner does not sit still — native `PickDefaultOrder`'s
`c_ptMiner` case issues a mine order the moment it is idle
(`Igc/shipIGC.h:2237-2250`, the cross-cluster `c_ttAnyCluster` FindTarget at
`:2287-2300`), and this port's P156/P158 mining AI does the same. In rock-less
Ganymede the NPC flies toward a warp from its first tick (~1,800–2,200 u within
~1–2 s, measured at BOTH commits). The test spawned the enemy *before*
connecting, so its behind-the-peer assertion (`dot < -500`, spawn dot −1500)
raced the join burst against that flight. P865's diff touches none of it
(12 files: docking/victory/broadcast/economy — no NPC-flight, join-burst or
export-timing code; `Ship.cs` unchanged since P851).

**Classification: the movement is native-correct; the test relied on the old
P147 "it sits still" behaviour that P156/P158/P160 superseded. Test fixed, code
untouched** (`tests/Allegiance.ClientCore.MonoGame.Tests/RadarTargetHighlightLiveTests.cs`):
the peer now connects and seats FIRST; the enemy is spawned afterwards, so its
identity+position arrive 1–2 ticks after spawn (P349 `SweepCrossSideExports`)
while it is still at (0,0,1500). Comment in the test documents why.

Verification: **5/5 alone** + **4/4 under self-parallel load** (load avg 13.98),
plus 1/1 in the fast tier.

## 1. SIM-0663 — LandOnCarrierEvent Goto→Defend re-task (FedSrv.CPP:13020-13028, re-grepped)

P865 classified this n/a on a "the port has no c_cid autopilot command state"
reading that no longer holds: the port has `Ship.SetCommand` for
`c_cmdCurrent`/`c_cmdAccepted` since P760b (Model-typed — a carrier fits),
`DefendTarget` since P166, and the plan-slot family (`PlanTarget`/P403
`GotoWaypoint`/P652 retreat/P767 ammo legs). Ported as
`Server.Docking.RetaskAutopilotAfterRelaunch`, called from `PerformRelaunch`
right after the RELAUNCH_SHIP send (native's order):

- **GetAutopilot() gate** (`:13020`): the port's server-visible autopilot state —
  an NPC lander (its `ApplyNpcAutopilot` IS the autopilot) or a player lander
  with an active P403 goto. Stated delta: a real player's wire-only controls
  (P155) give the port no client autopilot toggle to read.
- **Goto branch** (`:13021-13024`): accepted slot = (carrier, `CidGoto`) →
  `SetCommand(c_cmdAccepted, carrier, CidDefend)` (`:13022`); the plan leg
  (`:13023-13024`, native pilots ≥ `c_ptPlayer`) is the port's `DefendTarget`
  (P166) for NPC and player alike — the port's promote-from-accepted leg
  (shipIGC.cpp:1936-1939) reads only the station-typed accepted target, so the
  plan write is made here; the net flight is native's. Without this branch a
  carrier-bound lander would fly straight back into the bay and re-land forever
  — native's stated purpose.
- **Else branch** (`:13026-13027`): `Ship.ClearRelaunchPlan()` (new, internal) —
  the plan slot's content (station plan, P403 goto) cleared and the retreat/ammo
  legs ended, exactly the native plan-write resets (shipIGC.h:755-762, this
  port's `DronePlanChanged`); the accepted order survives ("drones will do
  c_cmdAccepted") for the P386 re-promotion next tick.

Tests (new `tests/Allegiance.Core.Tests/P865bCarrierRetaskTests.cs`, real
server + real cap502 bay geometry, no bots): Goto branch (accepted becomes
(carrier, Defend), `DefendTarget` set, goto consumed), the player plan leg, the
else branch (plan + retreat cleared, accepted (carrier, CidAttack) survives,
no defend issued), and the non-autopilot gate (accepted Goto untouched).

## 2. SIM-0643 — the unseen-station rock disguise (FedSrv.CPP:11441-11454, re-grepped)

P865 classified the disguise "design — no hidden-station wire record exists to
disguise". Under the 2026-10-28 ruling that is not a closing reason. The
disguise **closes as implemented**: the port's stale-rock design IS the
disguise, and this task pinned it wire-level per side and closed the one real
gap it exposed.

- Native (`FedSrv.CPP:11443-11454`): sides that saw the build get
  `KillAsteroidEvent` (`:11429-11436`); a side that sees the station but not the
  build gets the kill + station (`:11445-11446`); a side that sees neither gets
  the station record tagged with the rock's sig/pos/radius/caps
  (`SetRoidSig/Pos/Radius/Caps/Side`, `:11449-11453`) and is sent nothing — its
  client keeps drawing the rock with the data it already holds.
- Port: the uninformed side is latched into `Station.StaleRockSides`
  (Sector.cs `BuildStation`, the informed test = eye on rock / builder-seen /
  station visibility — native's three flags) and receives nothing; the retained
  `Station.ConsumedAsteroid` record IS the copied sig/pos/radius/caps. The two
  native consumption paths were already ported (P652/P747/P846): the per-tick
  zone-eye (`clusterIGC.cpp:141-155` → `Sector.ResolveConsumedRockVisibility`),
  the station-death retirement (`FedSrv.CPP:12384-12389` →
  `ServerHost.KillStationEvent`), and the first-sight show packet
  (`FedSrv.CPP:11035-11062` via `ExportDiscoveredStations`: the silent rock pop
  rides the station EXPORT + OBJECT_SPOTTED).
- New wire-level, per-side pins
  (`tests/Allegiance.Core.Tests/P865bRockDisguiseWireTests.cs`): the disguised
  side receives NOTHING while blind (rock kept, original data — nothing
  re-sent); on first sight it gets [ASTEROID_DESTROYED + EXPORT_STATION +
  OBJECT_SPOTTED] while the other blind side stays quiet; the zone-eye kills
  only that side's rock; killing the station retires every still-disguised
  side's rock. Detection radii (`VisibilityMath.DetectionRadius` =
  scanner + target + range·sig) are tuned per test so exactly the intended side
  is in range.

**The gap the wire tests exposed — a real cross-side visibility leak, fixed.**
Native's `sv.pLastSpotter()` is per side (each side's own ThingSite state,
`FedSrv.CPP:10896-10903`); the port kept a single model-level slot, so once side
A's scanner spotted the station, every other side's last-spotter short-circuit
inherited A's spotter geometry and "saw" the station too (observed: the 30 km
side flagged visible with the 3 km side's ship as recorded spotter). That breaks
the disguise for every side the moment one side discovers. Fixed:
`SideVisibilityState` now keeps the last spotter **per side**
(`LastSpotterFor/SetLastSpotter/ClearLastSpotter(sideId)`), and all consumers
read the seeing side's own slot — the sweep's short-circuit + write phase
(`Sector.cs`), OBJECT_SPOTTED in the discovery packets and the station export
(`Server.Broadcast.cs`, `Server.Research.cs`). Ledgered on SIM-0840 in the
matrix.

## Verification (light gate)

- `dotnet build AllegianceCore.slnx` — **0 warnings, 0 errors**.
- `tools/build-tools.sh` — all 91 tool projects built.
- AOT publish (`-p:PublishAot=true`, linux-x64) — clean, no IL2026/IL3050.
- New tests 3× alone: `P865bCarrierRetaskTests` + `P865bRockDisguiseWireTests`
  8/8 ×3.
- The fixed `RadarTargetHighlightLiveTests`: 5/5 alone, 4/4 under self-parallel
  load (load avg 13.98), 1/1 in the fast tier.
- Visibility-adjacent regressions 3× (per-side spotter change):
  P835VisibilityCostTests + CrossSideVisibility{Sweep,Foundation}Tests +
  P476 + MineVisibilitySweepTests + P797 + P851 + P832c + P866 + P747 + P652 +
  P865b — 200/200 and 33/33 ×3.
- ONE fast tier: `tools/run-tests.sh --tier fast` — 11,423 tests, 3 failures
  (`NpcWingmanLeadLiveTests.Wingman_fire_gate_leads_a_laterally_moving_enemy`,
  `NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy…` at 35.9 s — also
  over the 10 s fast limit — and
  `KillBonusLiveTests.Real_end_to_end_projectile_kill_increases_kill_bonus`).
  All three classes **8/8 ×3 alone in ~2 s** under the same build → load flakes
  under tier parallelism; ledgered in `docs/TEST_FLAKES.md` (P865b touches none
  of their code paths).
- Showcase never-parks ×5:
  `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`
  **5/5** (2 m57 s–4 m19 s).
- `python3 scripts/parity-scorecard.py` — validates; sim-rules-matrix **98.63%
  weighted (1877/1903)**, up from P865's 96.11%; no CRLF in the CSV.

## Scorecard (P865 → P865b)

| scope | before | after |
|---|---|---|
| sim-rules TOTAL weighted | 96.11% (1829/1903) | **98.63% (1877/1903)** |
| sim-rules TOTAL unweighted | 94.76% (651/687) | **97.67% (671/687)** |
| FedSrv mission rules weighted | 89.72% (541/603) | **97.68% (589/603)** |

## Native-intent classification (branches not ported verbatim)

- **implemented — SIM-0663 Goto→Defend re-task** (previously "n/a — autopilot
  command model"): the port's order/command model (P760b SetCommand, P166
  DefendTarget, the plan-slot family) expresses every native branch; the one
  stated delta is the GetAutopilot gate's player leg (no client autopilot toggle
  is wire-visible, P155 — an active P403 goto stands in).
- **implemented — SIM-0643 rock disguise** (previously "design"): expressed as
  the port's stale-rock latch + retained rock record + the three consumption
  paths; no hidden-station wire record is invented because none is needed — the
  observable per-side wire behaviour is native's (pinned).
- **n/a — AGC/Windows telemetry** (SIM-0643 `EventID_StationCreated`): no AGC
  module exists in the port (unchanged from P865).
- **bug (fixed, out-of-row) — the single-slot last spotter** (SIM-0840): not a
  native branch but a port defect the disguise tests exposed; fixed to native's
  per-side `sv.pLastSpotter()` semantics.

## Modern/better-approach note

The re-task lives in one named method (`RetaskAutopilotAfterRelaunch`) beside
the relaunch body it tails, with each native line cited and the port's order
model named per branch — instead of spreading `c_cid` writes through the flight
code. `ClearRelaunchPlan` centralises "what the port's plan slot contains" in
one Ship method, so the next plan-slot feature only extends that list. On the
disguise: the port's design (latch + retained record + nothing sent) is
strictly simpler than native's roid-tag copy because the port's clients are
authoritative about what they hold — the copy native makes onto the station
record exists here as the retained `ConsumedAsteroid`, and the three
consumption paths read it directly.

## Unfinished

Nothing known. The fast tier is green modulo the three ledgered load flakes;
the scorecard validates; `.scratch/` was removed (evidence: the fast-tier run
`TestResults/tiers-20261028T224800.552715Z/`, kept in the worktree).
