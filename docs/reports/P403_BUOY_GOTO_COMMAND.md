# P403 — buoy-targeted "goto" command

Click/select a sector and order a ship to fly there. Lands a real player-facing
trigger + server-side receiving execution for the already-ported-but-unused
`FM_CS_CHATBUOY(108)` wire path.

## Premise correction (verified against native source)

The task brief surfaced `Buoy` as a gap from a scoped `Igc/`+`FedSrv/` structural
comparison. The corrected premise, confirmed against the actual native source:
**buoys have no vision/detection/map-reveal component of any kind.** `BuoyType`
(`~/Development/Allegiance/src/Wopr/Wopr/Constants/BuoyType.cs:11-12`) has exactly
two values, both pure navigation: `c_buoyWaypoint = 0` (a point inside the ship's
current sector) and `c_buoyCluster = 1` (a stand-in for "go to this OTHER sector",
deleted once the ship arrives — `FedSrv.CPP:3302`). `AddConsumer`/`ReleaseConsumer`
(`Igc/sideigc.cpp:383-413`) is refcounting for "how many ships are still targeting
this point", not a visibility mechanic. Accordingly **no fog-of-war / sector-reveal
behavior was built** — that would match nothing native actually does.

## Confirmed real native mechanic (what this ports)

- Trigger: clicking a sector on the starmap (`WinTrek/sectormap.cpp:1550-1565`) or
  empty space in the tactical view (`WinTrek/console.cpp:1780-1793`) sets verb
  `c_cidGoto`, builds a client-side `DataBuoyIGC` (`position=(0,0,0)`,
  `clusterID` = clicked sector, `type = c_buoyCluster`), creates it as a purely
  client-side object (`CreateObject(..., OT_buoy, ...)` — **never synced to the
  server**, confirmed by this port's own `DataBuoyIGC.cs` doc), and sends it as the
  target of a command/chat message (`SetChatObject(b)` / `SendChat()`).
- Wire path: `BaseClient::SendChat` (`clintlib.cpp:2460-2469`) sees `otTarget ==
  OT_buoy` and sends `FM_CS_CHATBUOY(108)` with the buoy's exported `DataBuoyIGC`.
- Receiving execution: `CshipIGC::ResetWaypoint` (`shipIGC.cpp:3984-4120`) picks a
  `Waypoint::c_oGoto` vs `c_oEnter` per target type. **There is no `case OT_buoy`**
  — a buoy target falls through to the plain `default: o = Waypoint::c_oGoto`
  (`shipIGC.cpp:4053,4160`): fly toward the point, no dock/enter behavior. This is
  the shape implemented.

Already ported and confirmed present-but-unused beforehand (not re-built):
`DataBuoyIGC.cs` (struct), `ChatBuoy.cs` (codec), `ConnectionSession.SendChatBuoy`
(Session/ConnectionSession.cs:228, never called), the server relay
(`Server.Dispatch.cs` CHATBUOY branch), and the client receipt
(`ClientWorld.Apply(ChatBuoy)` → `Events.ChatBuoys`).

## What was added

### 1. Player-facing trigger (MonoGame client)
The port's starmap is keyboard-driven — the mouse is the M virtual-joystick
**steering axis**, not a picker (documented in P249/P272), and this Wayland host has
no input-injection tool. So the mouse "click" of native is realized with the
established keyboard idiom: `Tab` expands the starmap, `,`/`.` cycle the selected
sector (`_selectedSectorId`, P272/P374), and **G with the map expanded issues the
goto** (G is already native's "goto" key; in normal flight it keeps its existing
target-autopilot meaning). `IssueStarmapGoto(clusterId)` builds the equivalent
`DataBuoyIGC` (`position=[0,0,0]`, `clusterID`=selected sector, `type=c_buoyCluster`)
and calls the pre-existing `SendChatBuoy("goto", ChatTarget.Individual, ownShipId, db)`.

**Judgment call, stated plainly:** this port has **no wingman/squadron-selection
concept** (verified: the only "command another ship" path is the P366 team-leader
bot DOCK/LAUNCH order, which is a different ORDER_CHANGE mechanic, not a goto). So
the commanded ship for v1 is **the player's own ship** — a legitimate real native
mechanic (a player can waypoint-autopilot their own ship). The server applies a
cluster goto only when the order is addressed to the sender's own ship
(Individual/IndividualNoFilter/IndividualEcho + `OidRecipient == sender ship id`);
a CHATBUOY addressed to a teammate/team/everyone is relayed as chat but does not
re-route anybody. Commanding a *different* subordinate via the order's recipient is
the documented follow-up.

### 2. Receiving-side execution (server = the ship's owner/simulation authority)
Added a parallel nav-goal slot on `Ship` — `GotoOrder? GotoWaypoint` with
`GotoKind { Waypoint=0, Cluster=1 }` — fed through the **same** navigation resolver
(`ApplyNpcAutopilot` → `SteerToward` → `CrossClusterWarpGoal`), NOT a second "am I
navigating" mechanism. On a self-addressed `c_buoyCluster` CHATBUOY the dispatch
calls `Ship.SetGoto(...)`:

- **Goal resolution** (in `ApplyNpcAutopilot`, highest-priority branch): in the
  target sector a Cluster goto clears immediately (native "deleted as soon as we are
  in the cluster"); a Waypoint goto clears within `AutopilotArrivalRadius` of the
  point; otherwise cross-cluster travel reuses the existing ripcord/warp-pathing
  (`TryRipcordOrDefer` → `CrossClusterWarpGoal`), and the ship physically transits
  through the pre-existing warp-collision resolver.
- **Works for the player's own ship**: `ApplyNpcAutopilot`'s IsNpc gate is relaxed
  so a real-player ship with an active `GotoWaypoint` is steered server-side while
  the goto is active (control returns to the wire/pilot on clear).
- **Mutual exclusion** (the mining-XOR-selling discipline): `SetGoto` clears the
  station `PlanTarget` and every other goal slot; the NPC default-order / take-cover
  / Accepted-promotion decision gates treat an active `GotoWaypoint` as "not idle",
  so a commanded ship does not wander off to mine/sell/build meanwhile.
- **Refusals**: a docked ship refuses a goto (cannot autopilot while berthed).

### 3. Deferred (documented, not silently dropped)
- **`c_buoyWaypoint` (point inside the current sector)** — the trigger UI only
  selects *sectors* on the starmap, not in-world points; the v1 trigger produces only
  `c_buoyCluster`. The `GotoKind.Waypoint` arrival semantics ARE implemented in the
  resolver (so a future in-view tactical click is a small addition), but no trigger
  builds one yet.
- **Native "last known position" auto-buoy** on lost sensor/radar contact
  (`shipIGC.cpp:2420-2440`) — depends on a sensor/detection-loss model that this
  port does not have (there is no vision/detection subsystem at all, consistent with
  the premise correction). Deferred with the whole detection model; out of scope.

## Verification

### Automated (Core.Tests, new `P403BuoyGotoLiveTests`, 4 tests)
All live loopback (in-process `Server` + real `FltBot`, mission forced Started,
real map `XXV_simple.igc`, cross-cluster warp pair discovered at runtime):

1. `Cluster_chatbuoy_to_own_ship_files_through_warp_and_clears_on_arrival` — the
   headline: bot sends a self-addressed `c_buoyCluster` CHATBUOY; the server sets
   `GotoWaypoint`, the ship path-finds the real connecting warp, physically transits
   into the target sector, and the goto clears on arrival. Observed:
   `bot ... Arrokoth→Haumea: GotoWaypoint set, filed through warp (transit@334t), cleared on arrival (1t)`.
2. `Issuing_a_cluster_goto_clears_an_existing_station_plantarget` — mutual exclusion.
3. `Cluster_chatbuoy_addressed_to_a_teammate_does_not_reroute_the_sender` — addressing
   rule (relayed as chat, re-routes nobody).
4. `Waypoint_type_buoy_to_self_does_not_set_a_goto` — only cluster gotos are applied.

**Must-fail-if-removed**: neutralizing the `Server.Dispatch` `SetGoto(...)` call
makes test 1 red (GotoWaypoint never set), proven by temporary edit and reverted.

### Live MonoGame proof (real client, real server, reverted)
Ran a real `Allegiance.Server` (temp `teams:1` config, `XXV_simple`, port 9673) and
a real MonoGame client in `--capture` mode (auto-seats/auto-readies/flies — the only
unattended path on this host). A **temporary** diagnostic drove the real
`IssueStarmapGoto` ~5s after connect (standing in for a G-keypress, since the host
has no input injection); all temp edits were reverted afterwards (Game1 diff clean,
temp config deleted). Full observed narrative (server log, `consoleVerbose` on):

```
[monogame] TEMP-P403 auto goto: cur 1053 -> target 1052        (client trigger)
[monogame] goto order: own ship 8097 -> sector 1052 (CHATBUOY c_buoyCluster)
[chatbuoy] 1 ship 8097 side 0 'goto' target 6 buoy 0 -> 1 peer(s)
[goto] ship 8097 (side 0) — Cluster goto to sector 1052 issued (position (0,0,0))
[warp] ship 8097 crossed from 'Ceto' (1053) to 'Borasisi' (1052) via warp 1064
[warp] peer 1 ship 8097 -> SET_CLUSTER sector 1052
[goto] ship 8097 (side 0) — arrived in sector 1052; clearing goto
```

The player's ship physically warped Ceto→Borasisi **through the correct real link
(warp 1064)**, then the goto cleared on arrival — the required "goto" behavior, not
just a wire round-trip.

### Build / suite
- Whole solution (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- `Allegiance.Core.Tests`: 3480 total (incl. the 4 new), run repeatedly — every run
  green except the rotating **pre-existing documented live-wire timing flakes**
  (e.g. LiveTurretGunnerBinding persistent machine-state flake; intermittent
  Multi-mission / boarding / refund / mining-depletion live tests, each confirmed
  green in isolation; none reference P403 symbols). Two ClientCore live-network
  tests fail identically on the **pristine tree** (stash-verified) — pre-existing,
  not a P403 regression.
- `Allegiance.ClientCore.MonoGame.Tests`: 770/770.

## Files
- `src/Allegiance.Core/Sim/Ship.cs` — `GotoOrder`/`GotoKind`, `Ship.GotoWaypoint`,
  `Ship.SetGoto`, goto branch in `ApplyNpcAutopilot`, widened idle/take-cover gates.
- `src/Allegiance.Core/Data/DataBuoyIGC.cs` — `CBuoyWaypoint=0` / `CBuoyCluster=1`.
- `src/Allegiance.Server/Server.Dispatch.cs` — apply `SetGoto` on a self-addressed
  `c_buoyCluster` CHATBUOY.
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` — G-key starmap-goto.
- `src/Allegiance.ClientCore.MonoGame/Game1.Panes.cs` — `IssueStarmapGoto`.
- `tests/Allegiance.Core.Tests/P403BuoyGotoLiveTests.cs` — the 4 live tests.
