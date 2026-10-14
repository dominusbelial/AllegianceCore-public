# AllegianceCore bot driver and MCP review — round 2

Reviewed `956e753b` on `review/gpt-sol` (`8f7a3770` plus the round-one review
commit), following `REVIEW_BRIEF_BOTS.md`. No source code was changed. Round-one
findings are excluded. Locations refer to this worktree, not the fixes being
made elsewhere.

The review covered the generic parallel prepare/sequential apply loop,
scenario controllers, target choice, routes and staging, flight/combat feeds,
MCP spawn/report paths, and session ownership. ClientCore and server code were
read to verify the contracts the bots consume; the findings below concern the
bot driver, its primitives, or its misuse of those contracts.

**Evidence labels:** “verified by inspection” means the failure follows from
the code under the stated conditions; “reproduced” means a socket-free probe
executed the relevant code. Neither label means a live 390-bot reproduction.
Proposed causes of the observed snapshot lethality drop are explicitly separated
from verified defects. Severity reflects impact on a load generator and the
validity of its combat measurements.

## 1. High — headless clients retain every projectile after counting it

**Category:** performance / resource lifecycle. **Classification:** bug in
headless consumption of client/test state; not a native gameplay constraint.

**Locations:** `tools/allegiance-mcp/BotManager.cs:2984`, `:202`, `:639`;
`src/Allegiance.ClientCore/State/ClientWorld.cs:1062`, `:2773`, `:2790`;
`src/Allegiance.Bot/Bot.cs:1817`, `:1891`.

```csharp
// BotManager.SampleCombatStats:
foreach (var pj in cs.World.TakeNewProjectiles())
    if (pj.LauncherId == myShipId) bot.ShotsFired++;
// ClientWorld.TakeNewProjectiles:
_projectileDrainCursor = _projectiles.Count;
return taken;
```

The drain advances a cursor; it does not remove entries. The MCP never calls
`PruneExpiredProjectiles`. Its probe and commander worlds do not even drain the
projectiles. Idle/weave clients use `BotClass`, whose projectile and heavy-update
message accumulators are also append-only and are never reset by this manager.
These are rendering/test-observer APIs being used as permanent headless storage.

**Failure scenario:** during a sustained fight, each observing connection keeps
the rounds it has ever received, including rounds from other shooters. Hundreds
of worlds retain overlapping copies of sector combat traffic. RSS and retained
GC work grow with total historical combat, even when live projectile population
and bot count are constant. An idle observer of a busy sector also accumulates
traffic indefinitely.

**Suggested fix:** give headless sessions a count/event consumption mode with
bounded storage and destructive drains, including the probe and commanders.
If using expiry pruning, coordinate it with the drain cursor so pruning cannot
skip or recount events; simply adding pruning to the existing index cursor is
insufficient. Disable or periodically clear unused `BotClass` raw accumulators.

**Evidence:** reproduced **1,000 retained projectile entities after 1,000
apply-and-drain operations**, and **1,000 retained projectile messages** in a
plain `BotClass` after 1,000 receive calls. Expiry cleanup exists but has no MCP
caller. The 390-client memory growth rate was not measured live.

## 2. High — failed joins and purchases leak unregistered connections

**Category:** robustness / cleanup. **Classification:** bot ownership bug.

**Locations:** `tools/allegiance-mcp/BotManager.cs:636`, `:643`, `:655`,
`:456`, `:482`, `:507`, `:1004`, `:1013`, `:196`.

```csharp
var cs = new ConnectionSession();
cs.Connect(_port, name, _host);
// ... timeout throws ...
granted = WaitForAck(() => cs.BuyLoadoutAcks, name, hullId, parts, out warning);
return cs;
```

`SeatCombatClient` has no failure cleanup between creation and return.
`SpawnBot` has additional exceptions after seating but before `_bots.Add`, and
its plain-client branch has the same ownership gap. `EnsureCommander` only
registers the session after all waits succeed. A failed `ConnectProbe` also
escapes the manager constructor without disposing the probe.

**Failure scenario:** a reachable server rejects a purchase, never seats a
requested side, or sends an invalid logon acknowledgment. The tool reports a
failure, but the opened peer is not in the manager's owned collection and is
never closed by its cleanup. Repeated failed spawn slots can leave sockets,
receive threads, worlds, and possibly seated ships behind. Matrix/scenario
rollback only knows the names of successful returns, so it misses these peers.

**Suggested fix:** keep every newly created session under exception-safe local
ownership until it is successfully transferred to the registry. Dispose it on
every failure, including post-seat checks. Apply the same rule to the probe and
commander constructors. Register an active bot only after preflight succeeds,
or remove it transactionally on failure.

**Evidence:** verified by inspection through the timeout/refusal paths and the
only registry insertion sites. No socket-based failure injection was run.

## 3. High — lost and failed-arena bots keep reserving combat capacity

**Category:** robustness / target coordination. **Classification:** bot-side
misuse of the session-loss contract. Automatic reconnect is a design choice;
allowing disconnected peers to participate in live coordination is a bug.

**Locations:** `tools/allegiance-mcp/BotManager.cs:1634`, `:1698`, `:4225`,
`:2589`, `:4437`, `:618`; contract:
`src/Allegiance.ClientCore/Session/ConnectionSession.cs:700`, `:673`.

```csharp
if (b.InScenario || b.Behavior != "combat" || b.Cs is not { } pcs) return;
if (pcs.World.MyShipId is null) return;
// ... no Connected/Lost check ...
_tickEngagers[best] = newCount;
// Failed arena spawn:
try { bot.Cs?.Dispose(); } catch { }
```

Loss latches `Lost`; it leaves the world and `MyShipId` intact. Subsequent sends
are no-ops. The manager neither listens for loss nor excludes disconnected
sessions from preparation, driving, staging, or engager accounting. `FailArena`
disposes a bot that has already been added to `_bots`, but never removes it.

**Failure scenario:** earlier bots disconnect during combat while their last
world still contains enemies. They continue selecting targets and incrementing
the per-pass counts before newer live bots are processed. Three lost fighters
can fill a fighter target's cap without sending a shot, making live bots hold
or choose another enemy indefinitely. Lost members also delay staging until a
timeout and appear as active bots with stale positions. A refused arena
relocation enters the same state even though the tool says spawning failed.

**Suggested fix:** queue loss/removal to the loop owner, stop deciding and
counting that bot immediately, and dispose/remove it outside collection locks.
If reconnect is supported, reset behavior state and re-register only after a
fresh seat. Remove failed arena entries as part of the spawn transaction.

**Evidence:** a probe reproduced `Connected=false`, `Lost=true`, with
`MyShipId=1` still present. A staging probe with one staged live member and one
lost member returned **false** before timeout. The uncapped stale world and
engager reservation paths were verified by inspection; the live target-cap
starvation scenario was not run over TCP.

## 4. High — mission rotation readies new ships but retains old arrival state

**Category:** behavior / lifecycle. **Classification:** bot state-transition bug.

**Locations:** `tools/allegiance-mcp/BotManager.cs:4225`, `:4306`, `:4901`,
`:3149`; contract: `src/Allegiance.ClientCore/State/ClientWorld.cs:3418`.

```csharp
bot.ShipId = my;
// ...
if (bot.Arrived) { EngageCombat(bot, now, prep); return; }
// ReReadyBot only updates ReadyShipId and sends PLAYER_READY.
```

Arrival/path/staging state is reset only when an observed pod changes back to a
non-pod. A mission reseat changes the own ship ID and clears the client world,
but does not necessarily make the old bot pass through that pod transition.
The manager adopts the new ID and re-readies it without resetting `Arrived` or
the old route/objective state.

**Failure scenario:** a fighter reached the rendezvous in the previous mission.
After game-over rotation it is seated at home as a fresh fighter. `Arrived`
remains true, so it engages or loiters at home and never executes routing to its
target. The next mission can auto-start successfully while much of its fleet
is still committed to previous-mission latches. Old routes and sector IDs are
also unsafe if rotation changes the map.

**Suggested fix:** treat a changed session/mission identity or own ship ID as a
new-life transition: clear arrival, path, warp, staging, pod, and target
bookkeeping; refresh side/loadout state and re-resolve objectives for the new
map before readying/driving the new ship.

**Evidence:** reproduced a reseat from ship 1 to ship 2 at sector 1 with target
sector 4. `DriveCombat` adopted ship 2 but left **`Arrived=true` and an empty
path**. This is distinct from the existing pod-relaunch reset.

## 5. High — mixed/showcase arena bombers are relocated outside the arena

**Category:** scenario correctness / measurement validity. **Classification:**
bug applying the march role policy to arena mode.

**Locations:** `tools/allegiance-mcp/McpTools.cs:620`, `:701`, `:724`;
`tools/allegiance-mcp/BotManager.cs:968`, `:591`.

```csharp
short botTarget = BotManager.ResolveRoleTarget(slot.Role, targetSector ?? -1,
    mgr.FindSideHomeSector(slot.Side == sideA ? sideB : sideA) ?? -1);
mgr.SpawnBot(..., "combat", botTarget, ... arena: arena,
    arenaPosition: arena ? spot.Pos : null, ...);
// SpawnBot relocates into its targetSector, not the tool's rendezvous.
```

The mirrored positions are computed around the requested arena sector, but the
destination sector passed to each bomber is its enemy's home. The role override
is unconditional even when `arena=true`.

**Failure scenario:** an arena showcase asks for both fleets in rendezvous R.
Fighters are placed in R; side-A bombers are placed in B's home and side-B
bombers in A's home, using coordinates intended for R. They immediately fight
or loiter in those other sectors while the report describes one mirrored arena
fight. Both composition and combat comparisons are invalid.

**Suggested fix:** in arena mode, use the requested arena sector for every role
and omit march route/staging objectives. Apply the enemy-home override only to
the travelling mode that actually implements that objective.

**Evidence:** verified by inspection from position planning through the actual
relocator argument. The real corpus resolves the size-scale-13 showcase to
**27 bombers per side**, so this affects 54 of its 390 slots when enemy homes
are resolved. No live arena relocation was run.

## 6. Medium — the snapshot aiming fix predicts position but keeps stale attitude

**Category:** snapshot correctness / combat behavior. **Classification:** bug in
the bot's mixed-time aim inputs; snapshot-based control itself is a design choice.

**Locations:** `tools/allegiance-mcp/BotManager.cs:2143`, `:2166`, `:2175`,
`:2748`, `:2753`, `:2756`, `:2803`, `:2839`, `:2475`.

```csharp
pos = DecisionSnapshot.DeadReckonPosition(fe, ageSec * 1000.0);
vel = fe.Velocity;
orient = fe.MaterializeOrientation();
// EngageShipAt separately reads esnap, own fresh pose, target fresh pose,
// target metadata, current sector, and live dodge obstacles.
```

The “fresh” helper extrapolates translation but not rotation. The lead angle,
steering axes, speed projections, and 0.10-radian fire gate use the frozen own
orientation. Own and target helpers independently reload `World.Decision`;
metadata is read from yet another captured snapshot. Navigation/dodge then
mixes these poses with live obstacle state under the gate. A publish between
those reads can mix generations as well as timestamps.

**Failure scenario:** while a bot turns at 3.75 rad/s, 40 ms of snapshot age
leaves the reported nose 0.15 rad behind its live orientation, exceeding the
0.10-rad fire threshold even if the live nose is on the lead. Position-only
prediction does not fix the fire/steering decision. Near a warp or pod
transition, mixing pose/sector generations can also loiter a bot or apply an
old target decision in a new sector. This is a concrete mechanism for altered
shot rate with the snapshot switch on, not proof of the full live collapse.

**Suggested fix:** capture one decision frame per engagement, pass it through
all lookups, and bring own pose and orientation to one common time using the
existing flight/attitude integration rules and angular rates. Alternatively,
use a short, coherent live own/target pose read for aim while keeping expensive
candidate discovery on snapshots. Revalidate a prepared target against current
side, visibility, sector, and life before applying it.

**Evidence:** reproduced an actual call to `TryCsFreshPose` after publishing an
identity orientation and then applying a live 0.15-rad yaw: returned forward-X
was **0**, live forward-X was **-0.149**. A geometry probe measured the frozen
lead-angle error as **0.150 rad**, with live error zero. Multiple snapshot loads
and the live obstacle feed were verified by inspection. Their contribution to
the measured 200v200 lethality delta remains an **unmeasured hypothesis**.

## 7. Medium — pod docking is terminal, and a later relaunch retains pod latches

**Category:** behavior / reinforcement lifecycle. **Classification:** missing
client action in the reinforcement design, plus a state-reset bug. The server's
requirement that a real player explicitly launches is intentional.

**Locations:** `tools/allegiance-mcp/BotManager.cs:3122`, `:3130`, `:3438`,
`:2677`, `:4439`; server contract:
`src/Allegiance.Server/Server.Docking.cs:2181`.

```csharp
if (!bot.PodDocked && stationId != -1) bot.PodDocked = true;
// DrivePodHome:
if (bot.PodDocked) return;
// UpdatePodState on a non-pod hull:
bot.IsPod = false;
// resets travel fields, but not PodDocked/PodStationId/PodDockStationId.
```

No post-dock bot path buys/relaunches the combat loadout. The server's automatic
undock pass explicitly excludes real player ships, which these bots are. Even
if another actor launches the bot, the pod-docking and selected-station fields
remain latched.

**Failure scenario:** a combat bot survives, docks its pod, and sits there for
the remainder of the run despite reinforcement code/comments promising a
return to combat. If it is externally relaunched and dies again, the second
pod sees old `PodDocked=true`, so `DrivePodHome` returns while it is still in
space. The relaunched fighter is also excluded from living-wing counts and
reported as docked.

**Suggested fix:** if the intended run supports reinforcements, request a normal
`BUY_LOADOUT(fLaunch:true)` after docking with bounded retry and acknowledgment
handling. Clear all per-pod/home/docking latches when entering a new life,
derive actual docking from current world state, and preserve separate historical
telemetry. If terminal docking is intentional, expose that policy instead of
assuming server auto-relaunch.

**Evidence:** verified no post-dock launch call in the manager and the server's
player exclusion. A corpus-backed probe changed an `IsPod=true`, docked bot
back to hull 15; the transition returned **`IsPod=false`, `StagePhase=2`, but
`PodDocked=true`, `PodStationId=23`**. No TCP docking run was performed.

## 8. Medium — staging discards the assigned route and can enter the fight early

**Category:** routing / staging behavior. **Classification:** bot route bug.

**Locations:** `tools/allegiance-mcp/BotManager.cs:2367`, `:2355`, `:4320`,
`:4329`, `:4254`.

```csharp
if (rp[^1] != goal) return null; // RoutePathSuffix
bot.StagingSector = rp[^2];
EnsureCombatPath(bot, cur, bot.StagingSector, now);
// fallback: g.FindShortestPath(cur, goal)
```

The assigned route ends at the rendezvous, but staging asks for its penultimate
sector. Consequently `RoutePathSuffix` always rejects it during that leg.
Unrestricted BFS to the staging sector can choose a path through the rendezvous
that the distinct-entry planner deliberately excluded.

**Failure scenario:** assigned route is `1->2->3->5->6->4`, with rendezvous 4
and staging sector 6. A shorter `1->4->6` path exists. Staging chooses that
shortcut, enters 4 before release, latches `Arrived`, and immediately engages
there. The bot neither reaches its designated staging point nor uses its
assigned entry aleph, invalidating wing-arrival/fair-contact claims.

**Suggested fix:** take the assigned route prefix/suffix ending at the staging
sector rather than rejecting it because it ends at the full objective. Any
fallback staging BFS must avoid the rendezvous. Test the controller path, not
just the route enumerator.

**Evidence:** reproduced `EnsureCombatPath` returning **`1->4->6`** for the
assigned route above using a synthetic `ClusterInfo`. The subsequent arrival
branch was verified by inspection. This is a conditional topology failure,
not a claim that every wing on `m2xspiderw.igc` takes such a shortcut.

## 9. Medium — the optional cross-wing barrier never examines another wing

**Category:** staging / environment-switch correctness. **Classification:** bug;
independent wings by default are an intentional design.

**Locations:** `tools/allegiance-mcp/BotManager.cs:977`, `:4456`, `:4458`,
`:4463`, `:4469`.

```csharp
if (!SameStagingGroup(rs, rg, ri, side, goal, routeIndex)) continue;
sideMaxHops = Math.Max(sideMaxHops, r.Hops);
if (ri == routeIndex) continue;
if (r.Alive > 0 && r.Staged < r.Alive) allOthersAssembled = false;
```

`SameStagingGroup` requires equal route indices. Every other wing is skipped,
and the current wing is then skipped by the next condition. Therefore
`allOthersAssembled` is always true; `sideMaxHops` also only sees this wing, so
the intended cross-wing timeout offset is zero.

**Failure scenario:** with `ALLEGIANCE_BOT_SYNC_WINGS=on`, route 0 is assembled
while route 1 is still travelling. Route 0 releases immediately, exactly as
with the switch off. A purported synchronized-wing A/B run cannot exercise its
advertised behavior.

**Suggested fix:** select the barrier population by side and objective only;
compare route indices separately when excluding the current wing. Compute the
maximum hop count across that population. Preserve the default independent
release policy when the switch is off.

**Evidence:** reproduced in a fresh process with the switch **on**: one staged
route-0 bot and one unstaged route-1 bot, before timeout, produced
**`release-before-other-wing-staged=true`**.

## 10. Medium — showcase bombers route to a base but never attack that base

**Category:** behavior / role objective. **Classification:** incomplete bot
objective implementation; not a limit on what a real client can send.

**Locations:** `tools/allegiance-mcp/BotManager.cs:968`, `:4306`, `:2523`,
`:2624`; `tools/allegiance-mcp/McpTools.cs:675`, `:701`; compare
`tools/allegiance-mcp/BotManager.Scenarios.cs:225`.

```csharp
// ResolveRoleTarget: Bomber -> enemyHome
if (bot.Arrived) { EngageCombat(bot, now, prep); return; }
// ChooseCombatTarget enumerates ships only.
```

The role affects where a bomber travels and disables its staging, but does not
give it a station-attack state. The generic arrived branch exclusively chooses
enemy ships. The station attack controller exists only in `run_scenario` and
is never selected for a background showcase bomber.

**Failure scenario:** a Stealth Bomber reaches an undefended enemy home station.
There are no eligible enemy combat ships there; miners are excluded by default.
It loiters with no weapons instead of attacking the garrison. Against a defended
base it dogfights ships rather than carrying out its advertised base objective.
At scale 13 this can remove 27 ships per side from the rendezvous without gaining
the stated anti-base pressure.

**Suggested fix:** implement a station target/attack state for the bomber's
travel objective, reusing the existing scenario flight/fire primitives and
ordinary wire targeting/controls. Resolve and revalidate a station from the
bot's own observable world, with an explicit fallback when none is observable.

**Evidence:** verified by inspection through role assignment, arrival, and all
generic target branches; real showcase composition was verified by the probe.
Live station damage was not measured.

## 11. Medium — omitting target_sector creates an unroutable -1 objective

**Category:** MCP argument translation / behavior. **Classification:** nullable
sentinel conversion bug.

**Locations:** `tools/allegiance-mcp/McpTools.cs:596`, `:701`, `:724`;
`tools/allegiance-mcp/BotManager.cs:968`, `:4239`, `:4381`.

```csharp
short botTarget = BotManager.ResolveRoleTarget(slot.Role, targetSector ?? -1, ...);
mgr.SpawnBot(..., "combat", botTarget, ...);
// DriveCombat distinguishes null from a real goal:
if (bot.TargetSector is not { } goal) { EngageCombat(...); return; }
```

The modern mixed/showcase path converts an absent nullable target to a concrete
`short -1` and passes it as a non-null target. Non-bombers therefore take the
travel branch rather than the documented engage-in-place branch.

**Failure scenario:** spawn a mixed fleet without the optional `target_sector`.
Even when enemies are co-located, fighters repeatedly search for a path to
sector -1, find none, and loiter. The homogeneous path preserves `null` and
behaves differently for the same omitted argument.

**Suggested fix:** preserve `short?` throughout role-target resolution; normalize
an unresolved negative ID to `null` before spawning. Decide explicitly whether
a bomber may acquire a home objective when no travelling scenario was requested.

**Evidence:** verified by inspection through the conversion and nullable branch.
No live spawn was required to establish the sentinel mismatch.

## 12. Medium — steering and docking use the requested hull after authoritative changes

**Category:** behavior / authoritative loadout handling. **Classification:** bot
misuse of the granted/current ship state.

**Locations:** `tools/allegiance-mcp/BotManager.cs:469`, `:475`, `:520`,
`:2475`, `:3182`, `:3200`, `:3244`, `:2816`, `:3097`.

```csharp
new ActiveBot(name, hullId, hullName, ...) {
    Granted = granted,
    WeaponPartId = Catalog.FirstWeaponPartId(finalParts),
};
// Flight/docking:
core.HullTypes.TryGetValue(bot.HullId, out var hull);
```

The authoritative grant is stored for reporting but does not replace behavior's
requested `HullId` or requested weapon/launcher selection. The current hull
read by `UpdatePodState` is only used to recognize pods. Dock approach, dodge
mass/thrust/radius, bay class, cruise speed, and travel spacing keep using the
original hull. Part changes similarly leave aim ballistics stale.

**Failure scenario:** a requested hull is legitimately granted as a successor,
or a fighter becomes a pod. Pod docking still chooses fighter bay rules and
fighter stopping-distance/radius parameters despite the actual pod being a
different class. A partial or upgraded loadout can leave the bot leading with
the requested weapon instead of the one actually mounted, degrading aim or
making a usable replacement gun appear unarmed.

**Suggested fix:** separate the requested configuration from current runtime
hull/loadout. Update behavior's effective hull and mounted-part IDs from the
grant and subsequent own-ship loadout/hull transitions. Feed pod docking the
actual pod's capabilities and motion parameters.

**Evidence:** verified by inspection of field assignments and every listed
consumer; the server-successor path is explicitly accepted by `WaitForAck`.
Actual pod docking with different bay geometry was not run live.

## 13. Medium — a commanded dead enemy can permanently disable autonomous combat

**Category:** order lifecycle / stuck behavior. **Classification:** bug in the
modern persistent-order policy, not an era or wire limitation.

**Locations:** `tools/allegiance-mcp/BotManager.cs:1999`, `:2005`, `:2026`,
`:2535`, `:2556`, `:3117`.

```csharp
if (pending is not { } pq)
    return new OrderDecision(false, currentOrderedTarget);
// A non-ship order also returns currentOrderedTarget.
if (bot.OrderedTargetId is { } ordered) {
    // not engageable -> LoiterCombat; return;
}
```

Keeping an order through a transient visibility gap is intentional, but there
is no terminal release on an observed target death, permanent departure, or a
fresh stop/non-ship order. Only a fresh ship-target order or the bot's own pod
transition reliably changes the commitment.

**Failure scenario:** a teammate orders the bot to attack X. X dies, and the
bot receives its death notification. Even while Y is visible and shooting at
the bot, it continues to loiter for X forever. Sending a targetless/non-ship
replacement does not cancel that commitment. This is especially visible in
long-lived spawn-and-forget clients.

**Suggested fix:** release the commitment on authoritative terminal target
events, and give fresh cancel/stop/non-attack orders explicit semantics. Keep
temporary loss of visibility distinct from confirmed death/removal; a bounded
reacquisition policy can handle ambiguous gaps without permanent inactivity.

**Evidence:** verified by inspection of the complete order/engagement branches.
The pure decision's retention is intentional and tested, but no terminal-target
transition exists in the bot controller. No live order exchange was run.

## 14. Medium — teammate counting reads receive-owned dictionaries without their gate

**Category:** concurrency / performance. **Classification:** synchronization bug.

**Locations:** `tools/allegiance-mcp/BotManager.cs:2665`, `:2675`, `:2678`;
`tools/allegiance-mcp/CombatTargetSelector.cs:139` and `:255`.

```csharp
lock (_gate)
    foreach (var o in _bots)
        if (... o.Cs.World.Ships.TryGetValue(o.ShipId, out var me)
            && me.SectorId == myCluster) n++;
```

The manager lock protects the bot registry, not another bot's `ClientWorld`.
Each receive thread adds/updates/clears that world's ship dictionary under its
own `Cs.Gate`. This read bypasses that gate in both snapshot modes. Moreover,
the resulting `TeammatesInCluster` value is never consumed by the selector.

**Failure scenario:** while other clients receive join bursts, transits, or a
mission reset, selection performs concurrent dictionary reads against their
writes/clears and unsynchronized sector fields. Results can be inconsistent;
concurrent dictionary mutation is unsupported and can fail. An exception is
silently swallowed by the per-bot loop catch. At 390 bots the entirely unused
scan also adds roughly 390-by-390 registry visits per fully engaging pass.

**Suggested fix:** remove the unused count and context input. If needed later,
build a once-per-pass count from coherent snapshots, or read each world under
its own gate after releasing `_gate`. Do not extend the manager lock across a
fleet of session locks.

**Evidence:** verified the writer lock contract, the unguarded reader, and that
the selector does not read this context member. A timing-dependent dictionary
exception was not induced; the race is verified by inspection.

## 15. Medium — the spatial index is rebuilt per bot for a single query

**Category:** performance / allocation / lock contention. **Classification:**
avoidable cost in the modern bot design; not an era constraint.

**Locations:** `tools/allegiance-mcp/BotManager.cs:3915`, `:3926`, `:3946`;
`tools/allegiance-mcp/BotSpatialIndex.cs:78`, `:94`, `:97`, `:99`.

```csharp
var gridOff = BotSpatialIndex.Build(myCluster, pts, 400f);
foreach (var (sid, pos) in gridOff.QueryRadius(myPos, reach)) ...;
```

Every firing decision scans its own world, materializes points, builds a dense
grid, performs one radius query, and discards the grid. Default snapshot-off
mode performs the entire build/query while holding that session's receive gate.
The builder allocates four cell arrays plus two occupant arrays, including an
unused `cellStart`, and copies `occ` to `sorted` immediately before overwriting
every sorted entry. No runtime caller shares or caches an index despite its
“built once per sector snapshot” documentation.

**Failure scenario:** a crowded fight generates hundreds of nearly identical
index builds every 50 ms. The build retains the O(n) scan cost and adds
O(bounding-box cells) work, allocations, and receive-lock hold time. Sparse,
widely spread ships make the dense-cell term especially costly. Parallel target
preparation does not parallelize this sequential engagement work.

**Suggested fix:** for one cone test, use a direct filtered scan with no grid
materialization, or cache one immutable index for the actual decision frame and
reuse it across compatible same-side/sector consumers. Preserve per-client
visibility/freshness when sharing. Remove unused arrays/copies and use bounded
or pooled scratch storage.

**Evidence:** a build-only allocation probe with 195 points spanning a 15-by-13
cell rectangle measured **9,680 bytes per build**, **3,775,200 bytes per 390
builds**, equivalent to **72.0 MiB/s at 20 such passes/s**. This excludes point
lists, query/output lists, target lists, and other decision allocations. It is
a synthetic allocation measurement, not an M4 Max benchmark or a claim every
bot reaches this branch every pass.

## 16. Medium — rolling event logs are consumed as append-only lists

**Category:** metrics correctness / scenario robustness. **Classification:**
bot-side misuse of bounded `EventLog`.

**Locations:** `tools/allegiance-mcp/BotManager.cs:3019`, `:3036`, `:1395`,
`:1407`; `tools/allegiance-mcp/BotManager.Scenarios.cs:254`, `:255`;
contract: `src/Allegiance.ClientCore/State/EventLog.cs:33`, `:76`.

```csharp
for (int i = Math.Min(bot.KillCursor, kills.Count); i < kills.Count; i++) ...;
bot.KillCursor = kills.Count;
// Chats:
cs.World.Events.Chats.Skip(cursor).ToArray();
```

Event lists evict their oldest entry above 500. Their count stops increasing
while new entries replace older ones, but the bots' index cursors assume stable
append-only indices. `OrderDrone` uses `chatsBefore` in the same way.

**Failure scenario:** after a connection has observed 500 kills/ejections, its
kill cursor stays 500 forever. New credited kills are never counted and new
TTK completions are never consumed. At a saturated commander chat log,
`order_drone` can send a successful command and report no affirmative reaction.
Scenario alert/elimination observation likewise stops when its chat window fills.
A burst that causes eviction before polling can lose events earlier as well.

**Suggested fix:** consume events using monotonic sequence numbers or a
destructive observer-specific drain; carry an eviction/base-sequence counter
if retaining rolling lists. Detect/report overruns rather than equating
unchanged count with no new events. Reset observer state on mission changes.

**Evidence:** reproduced **500 kills counted**, followed by another credited
kill with **count=500, cursor=500, total still=500**. Chat consumers and the
shared eviction contract were verified by inspection.

## 17. Medium — cadence sampling hides freeze time between actual updates

**Category:** observability / performance measurement. **Classification:** metric
bug, especially relevant to the reported freeze-then-catch-up symptom.

**Locations:** `tools/allegiance-mcp/BotManager.cs:4873`, `:4877`, `:4888`,
`:4850`; `tools/allegiance-mcp/McpTools.cs:1038`.

```csharp
if (changed) {
    double interval = now - bot.LastPosAt;
    // record
}
bot.LastPosAt = now; // also updated when no new position arrived
```

The timestamp advances on every poll, including polls of an unchanged/stale
pose. The next changed position is timed from the last poll, not the previous
observed update. `TryCurrentPose` also prefers the shared probe over the bot's
own connection, so same-side cadence can describe probe delivery instead of
that bot's stream. Multiple receives between polls are inherently collapsed.

**Failure scenario:** the receive stream freezes for seconds while the behavior
loop keeps running. When one new position arrives, the recorded interval is
around one loop period and the recorded jump is large. The tool reports healthy
update intervals despite the freeze it is supposed to diagnose. Stationary
updates are not measured at all because position equality is used as the signal.

**Suggested fix:** timestamp actual own-session receive updates or track a
monotonic update/arrival sequence; update the cadence bookmark only for a new
observation. If keeping poll-based motion sampling, label its resolution and
measure elapsed time from the last changed observation, using the bot's own
session as the primary source.

**Evidence:** reproduced unchanged position observations at seconds 0 through
4, followed by a 100-unit move at second 5: recorded interval was **1 second**
instead of the **5-second observation gap**. In the production 50 ms poll the
same logic reduces an ongoing receive stall to approximately the last poll gap.

## 18. Medium — missed loop deadlines cause bursts of redundant fleet decisions

**Category:** performance / scheduling. **Classification:** scheduling bug in
the modern fixed-cadence driver.

**Locations:** `tools/allegiance-mcp/DecisionMetrics.cs:103`, `:106`;
`tools/allegiance-mcp/BotManager.cs:1771`.

```csharp
double sleep = _nextTickMs - nowMs;
_nextTickMs += _periodMs;
return sleep > 0 ? sleep : _minYieldMs;
```

A long pass or process pause advances the absolute deadline by only one period.
Every subsequent pass remains overdue until enough passes have been executed.
The minimum yield prevents literal busy-spinning but does not prevent an
effective catch-up burst of full-fleet decisions on nearly unchanged worlds.

**Failure scenario:** after a 341 ms process hitch, several 50 ms deadlines are
overdue. If pass work becomes cheap, the loop performs multiple fleet scans,
control sends, and allocations separated only by 1 ms. This can amplify GC/
receive starvation just when the process is recovering. The burst is especially
relevant when evaluating the faster snapshot-on path.

**Suggested fix:** skip missed periods and move the next deadline beyond current
time, or resynchronize to `now + period` after an overrun. There is no simulation
integration step to recover here: these decisions act on the latest world, so
replaying obsolete decision periods provides no gameplay benefit.

**Evidence:** reproduced a scheduler anchored at 0 with calls at 341 through
347 ms; sleeps were **`1,1,1,1,1,1,3` ms**. The amplification of a live hitch
was not measured. This issue exists in both snapshot modes.

## 19. Low — the spatial-index cell cap truncates storage without fixing indices

**Category:** robustness / silent bot-tick failure. **Classification:** bounds
bug in the bot index, conditional on unusually large finite coordinates.

**Locations:** `tools/allegiance-mcp/BotSpatialIndex.cs:73`, `:75`, `:76`,
`:88`, `:105`.

```csharp
int ncells = cw * ch;
if (ncells > 1 << 20) ncells = 1 << 20;
// cw/ch unchanged:
int cell = cz * cw + cx;
cellCount[cell]++;
```

The cap changes allocation length but not the grid dimensions or flattened
indices. `cw * ch` can also overflow before the comparison.

**Failure scenario:** two visible teammates have finite positions spanning more
than the capped cell rectangle. Index construction throws every decision for
that geometry. The loop swallows the exception, so the bot retains previous
controls and looks like an unexplained combat failure. The index's advertised
defensive cap does not provide its intended fallback.

**Suggested fix:** compute dimensions/products with checked wide arithmetic and
fall back to a sparse index or linear query when the grid cannot fit. Validate
finite cell size/query inputs as well. Never shorten arrays while preserving
larger valid cell indices.

**Evidence:** reproduced **`IndexOutOfRangeException`** with just two finite
points, `(0,0,0)` and `(410000,0,410000)`, cell size 400. This geometry was not
observed in the normal-map test, hence low severity.

## Snapshot question: what is established, and what still needs measurement

The switch changes more than lock acquisition. Finding 6 establishes that the
position-only “fresh” path still uses frozen own attitude, can mix snapshot
generations, and consults live obstacles with differently aged poses. The
0.15-rad probe gives a concrete fire-gate failure without invoking any server
cheat or changing the attack law. Existing snapshot lethality tests exercise
uniform-motion position extrapolation; they do not verify the actual turning
controller or a coherent own/target/obstacle decision.

The expected cost reduction is also incomplete. Snapshot preparation replaces
the candidate scan's world lock, but `SampleCombatStats`, `UpdatePodState`,
`TryDodgeGoal`, missile lock, mine proximity, transit spacing, and several other
paths still read live state under `Cs.Gate`. While bots are active,
`RequestDecisionRefresh` leaves a sticky level asserted: receive threads can
publish every 40 ms when dirty, rather than exactly once per 50 ms decision
pass. Those publishes copy/sort all known ships and refill visible ships,
stations, and warps. Much live-table work therefore remains while the publish
work is added to receiving. Finding 18 supplies another verified way cheap
decisions can burst after a hitch.

There is an additional snapshot-on-only instrumentation cost at
`BotManager.cs:2192`: once 16,384 age samples are retained, each sample's
`RemoveAt(0)` shifts about 128 KiB. At a hypothetical 390 aims x 20 passes/s,
that is about 1.0 GB/s of array movement, before summarization. Use a ring
buffer if retaining this diagnostic. This arithmetic is a workload estimate,
not a measured explanation of the shot-rate collapse.

Consequently, **there is a verified aim-coherence defect and several plausible
cost mechanisms, but this review does not establish one complete causal
explanation for the live snapshot-on/off kill delta**. A useful next experiment
would measure own angular pose age and actual fire-gate decisions while keeping
candidate discovery on snapshots and reading coherent aim poses live; separately
measure receive publish time, decode lag, decision allocations, and missed-
deadline bursts. Existing MCP cadence numbers need finding 17 fixed before they
can serve as evidence that a receive freeze disappeared. Snapshot publish age
alone is also not the age of each copied ship's latest wire update.

## Concurrency and design boundaries checked

The normal generic prepare phase reads `RecentAttackers` while the sequential
apply phase writes/prunes it, but `Parallel.For` completes before apply starts.
This is not, by itself, a concurrent dictionary access bug. The generic
`_tickEngagers` settle is sequential, and `_engagersSnapshot` publication/read
uses `_gate`. Sample and dodge lists have their own gates. I did not establish a
normal-path lock-order deadlock from these paths.

The broader statement that `_gate` protects active bot state is inaccurate:
most driving fields are written outside it while MCP reporting reads under it.
For example, combat counters may describe different instants in one response;
`SampleRuntimeOnce` also writes resource/report scalars outside the
`_metricsGate` used by readers. These are additional observation-consistency
risks, rather than evidence that the parallel prepare/apply phases themselves
simultaneously drive every bot. `OwnForScenario` is set only after registration;
it is not a handshake that waits for a previously captured generic pass to
finish. Scenario ownership should be established before publication if strict
single-controller ownership is required.

The staging population also grows during synchronous slot-by-slot spawning
(`McpTools.cs:692`, `BotManager.cs:568`). A first member may find its current wing
fully assembled before later members have even joined, and there is no planned
roster/sealed-wave barrier. This can undermine initial group contact on short
routes; its impact on the standard map was not measured. It is a **design risk
to verify**, separate from the reproduced route and switch bugs above.

Ordinary-client visibility, real purchases, control packets, command targeting,
and NPC investment/order limitations are intentional design/era boundaries and
are not reported as bugs. In particular, this review does not propose access to
server-private targets, arbitrary movement, or unlimited visibility to improve
bot performance. Arena relocation already has an explicit admin-cheat mechanism;
finding 5 concerns its wrong destination, not the existence of that mechanism.

## Validation and limits

- `dotnet build tools/allegiance-mcp --no-restore -m:1 -v:minimal` succeeded with
  **0 warnings / 0 errors**. The exact brief command with default parallel
  MSBuild exited 1 with “Build FAILED” and no diagnostics; the serialized retry
  succeeded, as in round one.
- A temporary console/reflection probe was built and run under
  `/tmp/gpt-sol-bot-review`, referencing the built assemblies. It used synthetic
  worlds and the available real core for hull/showcase checks. It opened no
  sockets. Probe sources/results are outside the repository.
- Final probe environment:
  `ALLEGIANCE_BOT_SYNC_WINGS=on ALLEGIANCE_BOT_DECISION_SNAPSHOT=on`.
  Findings explicitly marked inspection-only were not artificially presented
  as live reproductions.
- No live load session, M4 Max profile, reconnect soak, or network failure test
  was run. Existing tests were read for the contracts they cover; this was the
  requested reading review with focused probes, not a full test-suite rerun.
- Only this findings file was added. No source edits, commits, pushes, or branch
  changes were made.
