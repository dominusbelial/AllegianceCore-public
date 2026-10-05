# P540b — Cross-sector autopilot targets must route through warps (the pod "middle of nowhere" fix)

> **Correction note (2026-10-04) → see [P540c](./P540c_WARP_DESTINATION_WIRE_PARITY.md).**
> An early P540b iteration (`0dc429dd`) bent the EXPORT_WARP wire — it overwrote a
> warp's exported `DestinationId` with the resolved destination **sector** id, and a
> `ExportWarpWireTests` case asserted that bend. That iteration was amended out of
> the final P540b (`5ab6ff30`), which keeps `OT_warp` native-correct. **This report
> describes only the final, native-wire P540b.** P540c audits that wire end-to-end
> and fixes the two client-side consumers that still misread `DestinationId` as a
> destination sector (`Game1.AllWarps`'s unknown-partner fallback-guess, and
> `SpectatorFrame.cs:166`), adds failing-first parity tests, and confirms the server
> side is already native. Nothing in this P540b report is retracted.

## Symptom

Domingo (2026-10-04, live on 221 at `c129fb1f`): after ejecting, the pod autopilot
"did work but it took me to the middle of nowhere". Client log:

```
[eject] own ship 8109 became lifepod hull 444 at Vector3 { X = -27.4, Y = 8272.0, Z = -3234.2 }
[monogame] eject: autopilot home autopilot ON → Garrison (Station#4328)
[eject] autopilot target chosen: Garrison (Station#4328) in sector Ganymede (1050)
```

The pod was in **Alderan (1052)**; the rescue garrison is **two warps away** in
**Ganymede (1050)**. P540's target pick was correct; the *flight* was not — it
steered straight at Ganymede's coordinates while still inside Alderan, i.e. into
empty space.

## Root cause

`ResolveTargetPosition` (the `TargetKind.Station` case,
`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:2018-2024`) returns the
station's **raw position with no sector check**. The P524 autopilot
(`TryGetAutopilotControls`, `Game1.FlightInput.cs:1886`) then steers toward those
Ganymede coordinates from inside Alderan. Sectors are independent coordinate
spaces, so the position is meaningless in the current sector — empty space. The
same flaw applied to any cross-sector autopilot target (a ship or probe seen on
the map).

Three **latent defects** had to be fixed for a cross-sector target to be *flyable
at all*; all were uncovered only by trying to fly the pod for real:

1. **The client router navigated a phantom warp graph.** A warp's raw
   `DestinationId` is the id of its **PARTNER warp** (native `warpIGC.h:129-141`;
   `src/Allegiance.Core/Model/Mission.cs:519`), **not** the destination sector.
   The router's warp flattening (`Game1.AllWarps`) fed that raw id straight into
   `StarmapGotoRouter` as the destination sector, so it aimed at a phantom sector
   the server never transits to — measured on the live map: the warp at
   `(320,-5155,1613)` resolves (via its partner) to sector **1057** but the router
   read the raw id as **1052**; the warp at `(3733,3911,606)` resolves to **1052**
   but the raw id read **1054**. The pod transited to a sector the client never
   expected → oscillation. **Fix:** `Game1.AllWarps` now resolves the destination
   sector from the partner warp's `ClusterId` (`world.Warps[w.DestinationId].ClusterId`),
   the exact resolution the server's transit performs (and the MCP bot already used).
2. **An ejected pod could never move.** `Eject.ForSurvivor` built the `EJECT(77)`
   message with `Cookie = 0`, but `ClientWorld.Apply(Eject)` makes the own client
   adopt that cookie and echo it on every `SHIP_UPDATE` — the P507/P518 cookie
   gate (`FedSrv.CPP:4854`, `Server.Dispatch.cs:187`) then dropped *every* pose
   the pod sent (`[ctrlak] accepted=0 dropped[cookie=…]`), freezing the pod at the
   eject point. Native calls `NewCookie()` at eject (`FedSrv.CPP:12019`). **Fix:**
   `EjectSurvivor` rotates the ejecting peer's cookie and carries it on the message.
3. **A warp is a pass-through point, not a destination.** Steering a routed leg
   with the braking `SteerToward` (arrival radius 60 u) makes a small hull creep
   into the aleph, exit slowly, and be unable to escape the *same* warp's collision
   radius (`warp.Radius + shipRadius`, `Sector.cs:3203`) before the
   `WarpCooldownSeconds` (2 s, `Sector.cs:1398`) debounce expires — so it
   re-crossed and oscillated between the two sectors forever. **Fix:** routed legs
   use a new full-throttle pass-through `Pursue` law.

## Native citation

Native flies a goto to a model in another cluster **through warps**: the goto
path is the ordered cluster list to the target's cluster, and the client autopilot
steers to the connecting aleph of the next hop. The eject autopilot itself is
`SetAutoPilot(true)` + `FindTarget(pShip, c_ttStation|Friendly|AnyCluster|Probe,…)`
(`src/WinTrek/…/appmsg.cpp:230-237`), and the cross-cluster warp walk that
resolves the connecting aleph is `FindTarget`'s adjacent-cluster search
(`src/Igc/common.cpp:677-707`). P540 already proved the eject *pick*; P540b ports
the *warp traversal* the native autopilot performs, and the server already performs
the actual transit when the ship reaches the aleph's raw collision volume
(`Sector.ResolveWarpCollisions`, `Sector.cs:3182-3210`; `ServerHost.HitWarpEvent`,
`ServerHost.cs:837`). The fix reuses P541's client router (`StarmapGotoRouter`)
rather than writing a second one.

## The fix

### Client — sector-aware autopilot (new behaviour)

`src/Allegiance.ClientCore.Game/Game1.Targeting.cs:565` — pure, headless
`TryResolveAutopilotGoal(ownSector, targetSector, targetLocalPos, allWarps, out goal, out routed)`:

- **Same sector** (or unknown target sector) → `goal = target's local position`, `routed = false`
  (the pre-P540b law, unchanged).
- **Cross sector** → `goal = next warp mouth toward the target sector` via
  `StarmapGotoRouter.TryNextWaypointFromWarps`, `routed = true`.

`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:1886` `TryGetAutopilotControls`:
resolves the target's sector (`ResolveTargetSector`, `:2177`) and the own sector
(`OwnSectorIdOr`), feeds the goal decision, and:

- keeps the same-sector arrival disengage law unchanged;
- for a **routed** goal, steers with the new pass-through **`Pursue`**
  (`src/Allegiance.ClientCore.Game/Flight/AutopilotSteering.cs:60`) — full forward
  throttle through the aleph, no braking — so the pod transits and escapes;
- for a same-sector destination, keeps the existing `SteerToward` braking law.

Once the pod reaches the target's sector, `TryResolveAutopilotGoal` returns
`routed=false` and the goal flips to the station itself; the normal arrival law
stops it and the server's P301/P540 rescue/land path docks it.

Everything preserved: manual-input disengage, the engage sound, the HUD/autopilot
state, and the P524 dodge pre-empt.

### Server — one enabling wire fix + a client router fix

`src/Allegiance.Server/Server.Combat.cs:2026` — `EjectSurvivor` now rotates the
ejecting peer's cookie (`NextCookie(EnsurePeerUpdate(owner))`) and stamps it on the
`EJECT` message, so the ejected player's own client can fly the pod (native
`NewCookie()`, `FedSrv.CPP:12019`). Without this, every pod `SHIP_UPDATE` was
cookie-dropped and the pod was frozen.

`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:2159` — `AllWarps` now resolves
each warp's **destination sector** from its partner
(`world.Warps[w.DestinationId].ClusterId`) instead of feeding the raw partner-warp
id into the router. Shared by the cross-sector autopilot and the cluster goto, so
both route over real connectivity. The `OT_warp` wire format is left native-correct
(`DestinationId` = partner warp id), so the MCP combat bot's existing
`TryBuildWarpDestMap` (P484) is unaffected.

## Tests

### Failing-first unit tests (headless)

`tests/Allegiance.ClientCore.MonoGame.Tests/AutopilotCrossSectorGoalTests.cs` (6):

- own in sector A, station in sector B (two warps away) → goal is the **next warp
  mouth toward B**, `routed=true`, **not** the station's coordinates;
- once in B → goal is the station, `routed=false`;
- same-sector target unchanged (`routed=false`, local position);
- unknown target sector falls back to local position;
- unreachable cross-sector target → no goal;
- multi-leg advance: A→B→C the goal moves warp-to-warp then to the station.

`AutopilotSteeringTests` — 2 new `Pursue` cases (full throttle even inside the
braking zone; turns onto an off-axis target while keeping full throttle).

The destination-sector resolution (partner-warp `ClusterId`) is exercised
end-to-end by the live proof below and by the existing P484
`ClientWarpApproach*LiveTests` (which resolve the destination the same way and
still pass — no regression).

### Live proof (loopback, real server + real client session)

`tests/Allegiance.ClientCore.MonoGame.Tests/P540bPodAutopilotCrossSectorLiveTests.cs`
— a real in-process server seats an ejecting player and a cross-side shooter in
sector **1051**; lethal damage ejects the player into a lifepod. The nearest
friendly rescue-capable station is the side-0 garrison in sector **1053**, exactly
**two real warp hops** away. A faithful client pilot then flies the pod with the
**production** law (`TryResolveAutopilotGoal` + `Pursue`/`SteerToward`) driving the
real `OwnShipSimulator`, emitting native `SHIP_UPDATE(24)` poses. The pod transits
**both** warps and docks via the server's rescue/land path, with **zero
SHIP_RESETs and zero rejects**.

Measured live:

```
[eject] autopilot target chosen: Garrison (Station#1051) in sector 1053 (garrison, 2 warps away)
[p540b] plan: eject sector 1051 -> 1052 -> garrison sector 1053 (2 warps), first warp mouth (3733.3,3911.1,606.7), garrison @ (0,0,0)
[p540b] transits 1051 -> 1052 -> 1053; final sector 1053; docked=True; rejects 0; SHIP_RESETs 0; ticks 4757
[rescue] ejected pod 8096 docked (landed) at station 1051 'Garrison' via the server's station dock/rescue path after 2 warp transit(s)
```

The pod is confirmed server-side to cross sector 1051→1052→1053 (two real warp
transits) and dock at the garrison, with the server's per-peer `[ctrlak]` summary
showing `accepted>4500, rejected=0` and zero received SHIP_RESETs. Red-when-removed
checks: reverting the cross-sector routing reintroduces "flies to the target's
coordinates in the wrong sector"; reverting the `Pursue` law reintroduces the
sector oscillation; reverting the eject cookie freezes the pod.

### Regression sweep

- `AutopilotCrossSectorGoalTests`, `AutopilotSteeringTests`, `StarmapClickGotoTests`,
  `EjectRescueTargetTests`, `P540EjectLiveProofTests`, `P541GotoFlyLiveTests` — all pass.
- `ClientWarpApproachLiveTests`, `ClientWarpApproachSlowHullLiveTests`,
  `ClientWarpApproachDodgeLiveTests` (the MCP bot warp-approach, P484) — all pass
  (no regression from the destination-sector resolution).
- `ExportWarpWireTests`, `P541GotoGateLiveTests`, `WarpTransitServerTests`,
  `PayloadDecodeTests` — all pass.

Whole-solution build: **0 warnings, 0 errors.**

Full suite (`scripts/run-tests.sh`, 3 consecutive runs on the final code), measured
counts per run:

| run | Lobby | MonoGame (all P540b tests) | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1268/1268 | 457/457 | 3720/3721 |
| 2 | 12/12 | 1268/1268 | 455/457 | 3720/3721 |
| 3 | 12/12 | 1268/1268 | 454/457 | 3720/3721 |

The **only** failures are pre-existing live-wire load-flakes logged in
`docs/TEST_FLAKES.md` (P471 class): run 1 `ShipsUpdateLiveTests.Locked_target…` ;
run 2 `ThreePeerSeededSessionTests…`, `ReadyCheckLiveTests…`,
`LiveRelaunchTests.Carrier_without_launch_slots…`; run 3 `ShipDeleteGoneLiveTests.
SameSide…`, `RetriedConnectLiveTests…`, `FullSessionKillIntegrationTests…`,
`LiveStationRescueTests…`. The two not explicitly in the ledger
(`ShipDeleteGone…SameSide`, `RetriedConnect…`) pass 2/2 in isolation — timing
flakes, not regressions. Every P540b test and the entire unit suite pass in all
three runs; MonoGame (which hosts the P540b live proof) is 1268/1268 in all three.
<!-- SUITE -->
