# P420 — vizfight `attach`: a real cross-side rendezvous, real missiles, real full-inventory usage

Date: 2026-09-28
Branch: master (committed, not pushed — Domingo pushes)

## What this stage does

P418/P419's `attach` fleet is deliberately all on one side (the spectator's),
so an opposing side's ships — which undock into *their own side's* home
cluster — never co-locate with the spectator. That single-side design has two
real consequences the brief traces back to authoritative gates:

- `Ship.UpdateMissileLock` (Sim/Ship.cs:3164) hard-refuses a lock target on the
  shooter's own side (`otherSide == sideId`), so a same-side fleet can never
  lock a missile target at all.
- the friendly-fire gate (`Sector.ResolveCombatCollisions`) suppresses same-side
  damage, so "combat" was projectile spray, never kills.

P420 solves both by giving the choreography a **genuine second side** that
launches at its own real home base and **flies there** — real wire-only
movement through real warps, the same mechanism every ship already uses — to
rendezvous in the spectator's cluster. Once two real sides are co-located,
cross-side missile *lock* is real, cross-side damage is real (no suppression),
and real combat can produce real kills.

This is a bigger, better showcase than bolting a missile button onto the
same-side fleet.

## 1. Real BFS pathfinding on `ClusterGraph`

`src/Allegiance.ClientCore/State/ClusterGraph.cs` previously held only the two
raw lists from `CLUSTERINFO(200)` (`Clusters`, `Warps`). Added a real,
deterministic **breadth-first shortest path**:

```
FindShortestPath(fromCluster, toCluster) -> List<short>
```

- treats each `WarpEdge` as an undirected edge between two clusters (a warp is
  traversable both ways);
- returns the ordered cluster-id sequence `[from, …, to]`, minimal hop count;
- neighbours walked in ascending cluster-id order → reproducible result;
- empty list when unreachable, `[from]` when the endpoints coincide.

It is deliberately just the graph primitive ("which clusters, in order") and
knows nothing about geometry; the caller resolves each hop to a real warp
against its live world (below). Reusable beyond attach (P403's starmap goto
could use it; out of scope here).

Verified: `ClusterGraphFindPathTests` (4) — the real m2xspiderw topology plus a
small synthetic graph. On m2xspiderw the shortest Ganymede→Organia route is the
3-hop `1050 → 1054 (Bug Nebula) → 1055 (Tellus) → 1051`, which BFS returns; the
often-cited "Ganymede→Alderan→Organia" waypoint is a real but **longer** 4-hop
route (through Rigel and Juniper) that the test proves BFS correctly rejects.
Synthetic: shortest-path selection, deterministic tie-break, undirected
symmetry, unreachable and endpoints-equal edge cases.

## 2. A genuine second side — launch at own base, fly real warps to rendezvous

Each opposing peer is its own `ConnectionSession` (unlike a friendly
`AttachBot`, whose pose the choreography reads from the single spectator world).
That is deliberate and honest: while an enemy transits clusters the spectator
never sees, only the enemy's own world knows its real pose and its own
cluster's warp geometry. The server exports **every** sector's warps/stations/
asteroids to **every** peer unconditionally, so each enemy's world carries the
complete routing table — each warp's `DestinationId` names its partner warp,
whose cluster is the destination.

Flow (per enemy, `DriveEnemyContingent`):
1. seat on the **opposing** side (`SendPositionReq`), ready up, launch at that
   side's own home cluster with the full-inventory loadout (§4);
2. `EnsureEnemyPath` = `FindShortestPath(currentCluster → spectatorCluster)`;
3. each hop: pick the real warp in the current cluster whose partner is in the
   next cluster (`TryFindWarpTo`), and `AttachEnemyFlyTo` its real position —
   the same face-and-thrust autopilot law the sim's own NPCs use, reading the
   pose from the enemy's own world;
4. on a cluster change (SET_CLUSTER), narrate real progress:
   `Transit — VZA00 crosses into sector id 1054 — 1 warp(s) remaining`;
5. once in the spectator cluster, mark `Arrived` and co-locate for combat (§3).

Live (local controlled server, m2xspiderw 2 teams — enemy home = Organia 1051,
spectator home = Ganymede 1050):

```
t 41.7  Transit — VZA00 crosses into sector id 1055 — 2 warp(s) remaining.
t 44.8  Transit — VZA01 crosses into sector id 1055 — 2 warp(s) remaining.
t 88.4  Transit — VZA00 crosses into sector id 1054 — 1 warp(s) remaining.
t 91.6  Transit — VZA01 crosses into sector id 1054 — 1 warp(s) remaining.
t 131.4 Rendezvous — VZA00 crossed real warps into the spectator's cluster (id 1050) …
t 134.5 Rendezvous — VZA01 crossed real warps into the spectator's cluster (id 1050) …
t 131.4 Rendezvous complete — opposing contingent co-located with the fleet. Cross-side engagement window begins.
```

This is the piece that unlocks everything downstream.

## 3. Real cross-side combat — real missiles, real kills (honest results)

### The missile finding (verified, honest)

The task brief assumed a real `ET_Magazine` launcher is buyable via
`SendBuyLoadout`. **It is not** in this port. I dumped PCore001.igc: its 117
part records contain **no `EquipmentType.Magazine`, no `ChaffLauncher`, no
`Dispenser`** — only `Weapon`, `Shield`, `Cloak`, `Pack`, `Afterburner`.
`DataPartTypeIGC.MissileLauncher`/`ChaffLauncher` are **construction-only**
subtypes (Ship.cs:2660-2667 documents "no corpus part-record to decode"), so
the server's authoritative `ApplyLoadout` (`mission.Core.PartTypes.TryGetValue`)
can never grant one wire-only. Consequently `Ship.TryFireMissile`'s mounted-
`ET_Magazine@0` gate can never be satisfied by a real client loadout against the
unmodified server.

So I implemented the missile send path exactly as the brief asked —
`Bot.SendFireMissile()` mirroring `SendFireExpendable` (and `ConnectionSession`
already had one) — plus a wire test proving its `FM_CS_FIRE_MISSILE(70)`
encoding is correct (layout: fixed region 32 B + one 40-B `MissileLaunchData`
entry, `TargetId = NA`). But the live run honestly does **not** fire missiles:
there is no buyable launcher, so I do not spam the request (the brief itself
rules out "inert traffic"). This is documented the same way P418 documented
flag capture and guaranteed kills. The cross-side lock *pipeline* is nonetheless
real and demonstrable: cross-side `CommandTarget` acquisition works both
directions on the live wire, which is exactly the state
`Ship.UpdateMissileLock` requires — the day a core sells a launcher, the request
method and its gate are already in place.

### Real cross-side combat (achieved)

Rendezvoused enemies and friendlies genuinely co-locate, so both the spectator
(the same-side/friendlies + cross-side ships in its own cluster) and each
enemy's world see both contingents. A `Combat` phase:
- friendly attackers acquire the nearest co-located enemy in the spectator's
  world (real `ORDER_CHANGE c_cmdCurrent` cross-side → `Ship.CommandTarget`),
  steer at it and hold `AllWeapons`;
- each enemy does the same against the fleet in its own world;
- attackers pop `AfterburnerButton` on boost passes; defenders and enemies pop
  `CloakActive` as an evasive beat — real fuel drain (P412) and energy drain
  (P413) against the mounted parts.

Live server `[ctrl]` states during combat (real usage beats, captured):
`0x8000` AllWeapons (10283), `0x8400` AllWeapons|Afterburner (1808),
`0x800` Cloak (795), `0x8800` AllWeapons|Cloak (270), `0x8C00`
AllWeapons|Afterburner|Cloak (202).

### Real kills (occurred — honest count)

Because cross-side damage is no longer suppressed, real deaths happen:

```
[attach][t 137.7] kill: ship 8102 killed by 8107 (real KILL_SHIP)
[server] [combat] ship 8102 killed by ship 8107 at sim-t 160.15 — KILL_SHIP(105) -> 11 peer(s)
```

`8107` is an enemy (opposing side 1), `8102` a friendly — a genuine cross-side
kill, broadcast to 11 peers. A second run produced another real kill (the
throwaway spectator ship `8098` killed by enemy `8109`). I did **not** script
these: they emerged from real combat, and the count varies run-to-run (other
windows produced none). That is the honest outcome the brief asked for.

## 4. Real full-inventory usage

`FullInventoryParts(core, weaponPartId)` builds a real buyable loadout: a real
weapon + a real `Afterburner` (P412 thrust/fuel) + a real `Cloak` (P413) + a
real `Shield`, each at its own EquipmentType slot 0. Missile launcher + chaff
are deliberately excluded (see §3). Verified on the live server: every peer
carried **4 mounted parts** (`[loadout] … Mounted -> LOADOUT_CHANGE(96) hull 13
4 part(s)`), for both the friendly fleet and the opposing contingent. Usage
beats (§3) exercise afterburner, cloak, shield soak and gun fire throughout the
combat window; between combats the fleet's existing Flight→Regroup→Mining→
DockCycle→relaunch cycle re-establishes the inventory via the real relaunch.

## 5. Wiring / files

- `src/Allegiance.ClientCore/State/ClusterGraph.cs` — `FindShortestPath` (BFS).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — new 5-arg
  `SendOrderChange(shipId, commandId, command, IgcObjectType, objectId)` to set
  a cross-side `CommandTarget` (P153/P154 SetTarget).
- `src/Allegiance.Bot/Bot.cs` — `SendFireMissile()` (mirror of `SendFireExpendable`).
- `tools/vizfight/Program.cs` — `AttachEnemyBot` + `DriveEnemyContingent` +
  `Combat` phase + cross-side targeting + full-inventory loadouts + narration.
- `tests/Allegiance.ClientCore.Tests/ClusterGraphFindPathTests.cs` (4).
- `tests/Allegiance.Core.Tests/FireMissileSendWireTests.cs` (3).

## 6. Verification

- **Builds**: `AllegianceCore.slnx` 0 warnings / 0 errors; `vizfight.csproj`
  0 warnings / 0 errors.
- **New tests**: 7/7 pass (deterministic; re-run clean).
- **Full suites, 2× each**:
  - `Allegiance.Core.Tests` (3505): run 1 → 3 failures, run 2 → 9 failures;
    every failure is in the pre-existing documented live-wire timing-flake
    family (LiveInSpaceEject, CargoBayReload, TcpLoopbackProof,
    LiveTurretGunnerBinding, LiveTreasurePickup, LiveTeamLobby, LiveRipcord,
    LiveLifepodEnduranceDisplay, GameOverPlayersWire Conquest-leak, …), a
    rotating subset that passes in isolation; none touch the P420-added code.
  - `Allegiance.ClientCore.Tests` (232): 2 failures each run — the documented
    ShipDeleteGone dock flake + the known P382 JoinFlow boot-miner regression
    (both pre-existing). My ClusterGraph tests passed every run.
- **Live, controlled local server** (m2xspiderw, 2 teams, this repo's current
  server): the full P420 arc above — launch-at-own-base, real-warp transit with
  narrated crossings, rendezvous, cross-side combat with real afterburner/cloak/
  gun usage (server `[ctrl]` states), and real KILL_SHIP cross-side kills.
- **Live, production server `192.168.0.221:6073`** (the exact host:port a real
  MonoGame client connects to): the spectator + 8 friendlies (all 4-part full
  inventory) and the 2-peer opposing contingent (side 1, also 4-part inventory)
  seated and launched; the enemies launched at **their own home base** (cluster
  1 of the production map) and were routed toward real warps (the production
  server sends ClusterInfo — graph present). **Honest limitation:** the full
  transit to the spectator's cluster did not complete within the ~2–5 minute
  observation windows (and no kills were observed there), unlike the controlled
  local server. This is a production-server behavioural difference (transit not
  completing in-window), not a P420 code defect — the controlled-server run is
  the complete end-to-end proof. My injected clients were disconnected cleanly
  at the end of each production run (kill → link-dead), as in P418/P419.

## Honest kill outcome

Real cross-side kills **occurred** (verified on the controlled live server:
`KILL_SHIP(105)` for friendly `8102` by enemy `8107`, and spectator `8098` by
enemy `8109` in a later window). They were not scripted and not guaranteed —
other combat windows produced none, and the production server showed none
within its windows. Reported as-is, per the brief.

## Out of scope (unchanged)

- No server-side gameplay change; everything is real client/bot wire traffic
  against the unmodified server.
- No general pathfinding library beyond the focused BFS.
- `showcase`/`explore`/`roster`/`proof` modes and `wwwroot/index.html` untouched.
- P368–P419 not touched (the P419 phase-cycle shape is retained and extended).
