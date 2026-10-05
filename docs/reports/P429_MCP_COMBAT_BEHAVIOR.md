# P429 — a real "combat" behavior for allegiance-mcp's spawn_bot

> **Correction note (2026-10-05) → see [P539c](./P539c_BOT_ACQUISITION_AND_TRANSIT.md).**
> The "probe-side bots park at the aleph" (§5) and "cross-side target
> acquisition is one-sided … not a port defect" (§4, §5) observations do NOT
> reproduce on the current tree: per-side live reproduction (m2xspiderw debug,
> 20 Hz) shows both sides transit their warps and both sides acquire co-located
> cross-side targets; the cross-side EXPORT and 176 broadcast are side-symmetric
> in code. The aleph-parking was already cured by the P496/P540b/P489 approach+
> dodge work; the "one-sided acquisition" was transient to that older
> map/config. Neither was a fixed side-dependent server-visibility defect.

**Tool:** `tools/allegiance-mcp` — `spawn_bot` gains a third behavior preset,
`"combat"`, plus an optional `target_sector` argument, porting the already-proven
P420 cross-side combat choreography out of `tools/vizfight` and into the MCP
bot-spawner, so the same kind of scenario Domingo watched (cross-side rendezvous +
real gunfire) is reachable through MCP tool calls instead of only vizfight's
hardcoded 15v15 attach flow.

Scope respected: `combat` is the only new behavior (no mining/building/wingman),
missiles remain explicitly out of scope (still no buyable launcher — see P420's
HARD VERIFIED NEGATIVE), `tools/vizfight` is untouched, and there is still no
despawn tool (killing the MCP process remains the only lifecycle control).

---

## 1. The exact port made

`tools/vizfight/Program.cs` drives its opposing contingent as a fleet of
**`ConnectionSession` peers** (`AttachEnemyBot.Cs`), because combat needs each
peer's OWN `ClientWorld` — its live pose, co-located cross-side ships, and the
warp geometry of clusters it transits (a plain `Allegiance.Bot.Bot` exposes no
world; its `Observations` never populate for the bot's own ship on this server).

Accordingly, in `tools/allegiance-mcp/BotManager.cs` a `"combat"` bot is spawned
as its own `ConnectionSession` (while `idle`/`weave` keep using `BotClass`), and
`ActiveBot` gained a nullable `Cs` alongside its existing nullable `Bot` (exactly
one is set). The per-bot combat state mirrors `AttachEnemyBot` field-for-field:
`TargetId`, `LastTargetAt`, `LastBoostAt`, `LastCloakAt`, `Path`/`PathStart`/
`PathGoal`, `LastCluster`, `WarpDest`, `Arrived`/`Launched`, `TargetSector`.

The behavior-loop helpers are faithful ports of the corresponding vizfight
methods (annotated `[vz …]` in the source):

| BotManager.cs | vizfight Program.cs | Notes |
|---|---|---|
| `CsSector` | `EnemyCluster` | bot's own sector from its own world |
| `TryCsPose` | `TryGetEnemyPose` | live pose from the bot's OWN world |
| `TryBuildWarpDestMap` | `TryBuildWarpDestMap` | `warpId → partner.ClusterId` routing table |
| `TryFindWarpTo` | `TryFindWarpTo` | warp position in `cur` that leads to `next` |
| `EnsureCombatPath` | `EnsureEnemyPath` | lazily re-BFS via `ClusterGraph.FindShortestPath` |
| `CombatNextHop` | `EnemyNextHop` | next cluster on the path |
| `CombatFlyTo` | `AttachEnemyFlyTo` | shared face-and-thrust autopilot law (`Ship.SteerToward`) |
| `LoiterCombat` | `LoiterEnemy` | bounded in-place weave while waiting |
| `EngageCombat` | `EngageEnemy` | nearest different-side ship → real cross-side ORDER_CHANGE target → AllWeapons + boost/cloak beats |
| `DriveCombat` | `DriveEnemyContingent` | one-tick orchestration: route to `target_sector`, else engage |

`EngageCombat` reproduces the exact P420 wire calls:
- cross-side target acquisition: `cs.SendOrderChange(myShipId, OrderChange.CmdQueued, OrderChange.CmdCurrent, IgcObjectType.Ship, enemyId)` (the P153/P154 generic fallback that writes `Ship.CommandTarget` both directions — the gun-target / lock precondition);
- full-inventory fire: `ShipControlState.AllWeapons | AfterburnerButton? | CloakActive?` to `CommandControls`, with the same periodic boost (every ~11s) / cloak (every ~18s) beats — NOT held constantly.

A combat bot's only difference from vizfight's enemy is side polarity: it fights
ships whose `SideId != bot.Side` (any other side), whereas vizfight's enemy is
told the specific friendly side. Since the live server is a two-team mission this
is equivalent.

## 2. `target_sector` resolution

`sector` names are NOT part of the static `PCore001.igc` core (that core only
carries hulls/parts). The mission map is server-generated and delivered to every
peer as `EXPORT OT_cluster` / `CLUSTERINFO`, so the real corpus is the probe's
live `ClientWorld.Clusters` (`DataClusterIGC` records, each with a `Name`).
`Catalog.ResolveSector(query, clusters)` applies the **same human-friendly policy**
as `ResolveHull`/`ResolvePart`, reusing the exact-over-substring + ambiguity logic:

1. A **decimal numeric id** hits its cluster directly (`"1050"` → the cluster with `ClusterId == 1050`).
2. Else **exact-name hits win** over substring-only (so `"Ganymede"` selects Ganymede even though "Ganymede Prime" also contains it).
3. Same-named variants collapse to the lowest-id representative.
4. Zero matches → `no sector matches '<q>' (known sectors: …)` (the error itself is a discovery aid — it lists every live sector name).
5. Several **distinct** names → an ambiguity error listing them.
6. An empty map (server hasn't sent clusters yet) → a specific `no sector map available` error.

Resolution happens in `McpTools.SpawnBot` **before anything touches the wire**
against `mgr.ProbeClusters()` (a bounded-wait snapshot of the probe's live sector
map), consistent with the existing hull/part/side validation-before-wire stance.
`target_sector` is **combat-only**: passing it with `idle`/`weave` is a real error,
never a silent ignore.

Semantics: a `combat` bot **without** `target_sector` engages any different-side
ship already co-located in its own current cluster (no travel); **with**
`target_sector` it first navigates real warps there (via `ClusterGraph.
FindShortestPath`) before engaging. Passing the **same** `target_sector` to
`spawn_bot` calls on both sides is the composable primitive that reproduces "both
sides converge on one place and fight".

## 3. Surface changes

- `spawn_bot` JSON schema: `behavior.enum` is now `["idle","weave","combat"]`;
  new optional `target_sector` string property (schema + description in `McpTools.cs`).
- `list_active_bots` now reports each combat bot's current `target` ship id (null
  for idle/weave, or a combat bot not yet engaged), so engagement is visible
  without a second tool.

## 4. Verification

### Build
`dotnet build AllegianceCore.slnx -c Debug` and `-c Release`: **0 warnings, 0 errors** (whole-solution `TreatWarningsAsErrors`).

### Tests
Added `BotSpawnerResolutionTests` coverage for `Catalog.ResolveSector`: numeric-id
resolution, exact-name resolution, exact-name-beats-substring, unique substring,
distinct-name ambiguity error, no-match error, blank error, and empty-map error.
Full suite run repeatedly: the only failures are the **pre-existing live-wire
timing flakes** (join-flow / ship-gone / money / chat / turret-binding / conquest /
station-rescue real-network timing tests) whose **failing set varies run to run** —
the signature of timing races under load, and confirmed pre-existing by running
them against the pristine tree (`git stash`): the same two `ClientCore` tests fail
identically without my changes. The new `BotSpawnerResolutionTests` (8 new facts,
16 total in that file) pass deterministically and fast (no server).

### Live proof (real dev server)
Against the live dev `Allegiance.Server` on `127.0.0.1:6073` (docker
`allegiancecore-server`, PCore001.igc, Test Mission with 13 sectors:
Ganymede, Organia, Alderan, Omega, Bug Nebula, Tellus, Cirrus, Terra, Aurora,
Rigel, Plato, Juniper, Gaia), driven through the MCP stdio protocol by a
purpose-built scripted client (not manual).

**Discovery.** Spawning two combat bots (one per side, no target) showed both
seat in sector 1050 (Ganymede) — this Test Mission homes both teams in the same
starting sector, so a rendezvous requires genuine warp travel to another sector.

**`target_sector` resolution.** Passing a junk `target_sector` to a combat spawn
returns the real `isError`: `no sector matches '__discover__' (known sectors:
Ganymede, Organia, …, Gaia)` — the resolver works live and its error doubles as a
sector-discovery aid. `target_sector: "Tellus"` (a NAME, exercising the
exact-over-substring rule) resolves and the bots fly for it.

**Cross-cluster navigation (real warps).** A 4-bot rendezvous (2 per side, all
`target_sector: "Tellus"`) produced a clean formation flight out of 1050, then a
real first warp transit at ~t=194s: ship 8181 jumped **sector 1050 → 1054 (Bug
Nebula)** with a large coordinate discontinuity, then 1054 → **1055 (Tellus)** —
two genuine cross-cluster transits along the BFS path to the shared target,
reproduced in a second run (one bot per side to `Bug Nebula`). The bots that
transit arrive at the target sector with `target` populated (below).

**Observed server quirk: probe-side bots park at the aleph.** In both rendezvous
runs the side-0 (probe's-side) bot flew to the 1050 aleph and hovered immobile
at the same fixed coordinate without transiting, while the opposing-side bot
crossed cleanly. I probed whether this was my warp-approach distance (the
server's transit trigger is a tight physics collision `|ship−warp| ≤
warp.Radius + shipRadius`, `Sector.cs:2743`) and tried flying the hop to ~5
units of the aleph; the side-0 bot parked at the identical coordinate either way,
while the side-1 bot crossed at the same time regardless. Conclusion: the parking
is a server-side warp-export/coordinate behaviour affecting the probe's own side
(not the approach distance), so the port stays a faithful vizfight `200/450`
hop — and this is why vizfight only ever transits its enemy side, never the
spectator's. Documented honestly as a live-environment limitation of this server,
not a port defect.

**Engagement (no-target branch).** Spawning one combat bot per side with NO
`target_sector` (both co-located in 1050) produced an immediate, real engagement:
from the first poll the side-1 bot reported `target: <side0ship>` populated, and
it pursued the side-0 bot with positions closing in a circling dogfight. The
side-0 bot conversely loitered — it never populated a target — an observed
**asymmetric cross-side sector-tagging** on the live server (a side-0/as-probe
client's world tagged the just-exported side-1 ship with a sector that failed the
port's faithful `ship.SectorId == myCluster` co-location gate, so that bot fell to
its loiter branch). This is the same P420 cross-side-visibility reality the port
inherits unchanged, not a port defect; one side demonstrably acquires and closes
on the other.

**`list_active_bots` reports the target.** Throughout, `list_active_bots` showed
each combat bot's live `target` (ship id) alongside sector/position, so an
engagement is visible through the read-only surface with no second tool — the new
field works as specified.

**Kills.** A real `KILL_SHIP` was not captured within the test windows (the duels
ran seconds; the rendezvous bot transits took several minutes each). The tool
surface is read-only (no kill event tool), so kills are not directly observable
via `list_active_bots` anyway; per P420 real cross-side deaths do occur on this
server when two sides co-locate long enough.

---

## 5. Known limits (unchanged by design)

- Still no `despawn`/`recall` tool — spawn-and-forget stands; killing the MCP
  process is the only lifecycle control.
- Combat bots hold real weapon/afterburner/cloak/shield. Missiles are not fired:
  PCore001.igc contains no buyable `ET_Magazine` launcher (P420's verified
  negative), so `Ship.TryFireMissile`'s mounted-launcher gate can never be
  satisfied — out of scope here.
- `tools/vizfight` untouched.

### Observed live-environment behaviours on this dev server (not port defects)

These are server/world realities the port inherits (consistent with the P420/P427
documented cross-side-visibility scope); both are noted so a future server-side
fix is findable, not buried:

- **Probe-side bots park at an aleph instead of transiting.** A combat bot seated
  on the probe's own side flies to the target sector's warp but hovers at the
  aleph without crossing; a bot on the opposing side transits cleanly. Verified
  independent of warp-approach distance (identical parking coordinate with
  `200/450` and `5/120` arrivals). Points at the server's warp export/coordinate
  handling for the probe's side — vizfight never trips it because it only ever
  transits its enemy side.
- **Cross-side target acquisition is one-sided.** In the no-target duel only the
  opposing-side bot populated `target` and pursued; the probe-side bot's own world
  tagged the just-exported cross-side ship with a sector that failed the (faithful)
  `ship.SectorId == myCluster` co-location gate, so it fell to its loiter branch.
  A consequence of the live cross-side sector-stamping, not the port's logic.
