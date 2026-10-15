# P403 — buoy-targeted "goto" command: click a sector to order a ship there

## Why this task exists, and a premise correction first

This surfaced from a re-run of the native-vs-port structural comparison
(scoped to `Igc/`+`FedSrv/` only, to cut out native's rendering/sound/
Steam-SDK/WinTrek-UI noise that made an earlier whole-repo pass
degenerate). `Buoy` was the one real gap out of 9 candidates; the other
8 were confirmed false positives (deliberate renames like
`IclusterIGC`→`Sector`, already-ported functionality under a different
name like `CdevelopmentIGC`→`DataDevelopmentIGC`/`Bucket.cs`, or
genuinely out-of-scope native subsystems like FedSrv's Windows-service
clustering infra).

**Corrected premise, confirmed against the actual native source — buoys
have no vision/detection/map-reveal component of any kind.** `BuoyType`
(`~/Development/Allegiance/src/Wopr/Wopr/Constants/BuoyType.cs:11-12`)
has exactly two values, both pure navigation: `c_buoyWaypoint = 0` (a
point inside the ship's current sector) and `c_buoyCluster = 1` (a
stand-in for "go to this OTHER sector," deleted once the ship arrives —
`FedSrv.CPP:3302`, `"c_buoyCluster would be deleted as soon as we are in
the cluster"`). `AddConsumer`/`ReleaseConsumer`
(`Igc/sideigc.cpp:383-413`) is refcounting for "how many ships are still
targeting this point," used only to know when to delete it — not a
visibility/detection mechanic. Do not build any sector-reveal/fog-of-war
behavior into this task; it would not match anything native actually
does.

## Confirmed real native mechanic

- **Trigger**: clicking a sector on the starmap (`WinTrek/sectormap.cpp
  :1550-1565`) or empty space in the tactical view
  (`WinTrek/console.cpp:1780-1793`) sets verb `c_cidGoto`, constructs a
  `DataBuoyIGC` locally (`position = (0,0,0)`, `clusterID` = the
  clicked sector, `type = c_buoyCluster`), creates it as a purely
  client-side object (`CreateObject(..., OT_buoy, &db, ...)` — **never
  synced to the server as a real object**, confirmed by this port's own
  `DataBuoyIGC.cs` doc comment), then sends it as the target of an
  ordinary command/chat message (`SetChatObject(b)` / `SendChat()`).
- **Wire path**: `BaseClient::SendChat` (`clintlib.cpp:2460-2469`) sees
  `otTarget == OT_buoy` and sends `FM_CS_CHATBUOY(108)` instead of the
  plain `FM_CS_CHATMESSAGE(23)`, carrying the buoy's exported
  `DataBuoyIGC` block. The commander→miner dispatch
  (`CommanderResearchAndExpand.cs:1610-1618` in the native Wopr AI) is
  the canonical real caller — this is a command-console order (tell a
  ship, often an AI wingman/subordinate, to autopilot to a point/sector),
  not a manual-flight-replacement for the commanding player's own ship.
- **Already ported in this repo, confirmed unused**:
  `DataBuoyIGC.cs` (wire struct), `ChatBuoy.cs` (the `FM_CS_CHATBUOY`
  codec), `ConnectionSession.SendChatBuoy` (`Session/
  ConnectionSession.cs:228`, never called), the server-side relay
  (`Server.Dispatch.cs:311-345`, decodes and forwards via the shared
  `SelectChatTargets`/`IChatRoutingTarget` path — already works, just
  never triggered by anything a real player does), and
  `ConnectionSession.cs:577-578`'s `_world.Apply(ChatBuoy.Decode(m))`
  on receipt (applies it into client world state, but nothing turns
  that into an actual ship command yet).

## What's actually missing (the real scope)

1. **No player-facing trigger.** Nothing in
   `Allegiance.ClientCore.MonoGame/Rendering/StarmapRenderer.cs` (or
   wherever the starmap/tactical click handling lives — check both) ever
   calls `SendChatBuoy`. `Game1.Session.cs:427-429`'s own comment
   ("SendChatBuoy and command parsing are out of scope") was scoped
   narrowly to the *typed chat box* specifically (correctly — typed text
   never targets a buoy) and says nothing about a starmap click, which
   is a different, still-open trigger.
2. **No receiving-side execution.** A ship that receives a routed
   CHATBUOY order has no code path turning it into an actual move
   command. This port's only existing "autopilot to a target" concept is
   `Ship.PlanTarget` (`Sim/Ship.cs:1032`), which is **typed as
   `DataStationIGC?`** — stations only, NPC-only (mining/selling
   decisions), and only ever set internally, never from an externally-
   received command. Extending it (or adding a parallel point-target
   field) to also accept a plain position/sector target is the real
   work here.

## Scope

1. Add a player-facing trigger: clicking a sector on the starmap (reuse
   whatever click/hit-testing `StarmapRenderer.cs` already has for
   sector selection) constructs the equivalent of native's
   `DataBuoyIGC` (`position = zero`, `clusterID` = clicked sector,
   `type = c_buoyCluster`) and calls `SendChatBuoy` with the currently-
   selected/commanded ship as target — investigate what "selected ship
   to command" means in this port today (is there any existing
   selected-target/wingman-selection concept, or is the only sensible
   v1 target the player's own ship?) and document your judgment call
   plainly rather than guessing silently.
2. Add receiving-side execution: when a ship's owner (server-side)
   receives a routed CHATBUOY order for one of its own ships, translate
   it into an actual move order the ship's update loop acts on —
   whether that's extending `PlanTarget` to a discriminated point-or-
   station target, or a parallel field, is your call; follow whatever
   shape keeps `Ship.cs`'s existing PlanTarget-consuming code
   (`Ship.cs:1662-1697, 3990-4074, 4232-4258, 5118-5166`) coherent rather
   than forking a second parallel "am I navigating" concept. A ship
   with a point/sector target should fly toward it and clear the target
   on arrival, mirroring how a station-typed `PlanTarget` already
   resolves and clears.
   **Confirmed native reference for exactly this step**:
   `CshipIGC::ResetWaypoint` (`shipIGC.cpp:3984-4120`) is what turns a
   command target into an actual navigation objective, picking
   `Waypoint::c_oGoto` vs `c_oEnter` per target type (`OT_ship`,
   `OT_station`, `OT_asteroid`, `OT_warp` each get their own case,
   `shipIGC.cpp:3995,4021,4029,4053`). **There is no `case OT_buoy`** —
   a buoy target falls through to the plain default, i.e. the simplest
   possible objective (fly toward the point, no special dock/enter
   behavior) — confirms the "fly toward it and clear on arrival" shape
   above is the right one, not a simplification you're inventing.
3. Point-in-current-sector target (`c_buoyWaypoint`) — port this too if
   the trigger UI naturally supports "click empty space in the tactical
   view" as well as "click a sector on the starmap"; if that's a bigger
   UI lift than the starmap case, it's fine to land the cross-sector
   (`c_buoyCluster`) case first and note the waypoint case as a
   documented, scoped-out follow-up rather than silently dropping it.

## Explicitly out of scope

- Any vision/detection/fog-of-war behavior — confirmed not real, see
  premise correction above.
- The typed-chat-box path — already correctly scoped out
  (`Game1.Session.cs:427-429`), don't touch it.
- Native's "last known position" auto-buoy on lost sensor/radar contact
  (`shipIGC.cpp:2420-2440`) — this depends on a sensor/detection-loss
  model; investigate whether this port has an equivalent concept at all
  before deciding whether it's in scope, and document your decision
  plainly rather than silently building or silently skipping it.
- Native training-mission-specific waypoint usage
  (`training/mission6.cpp`, `mission10.cpp`, `CreateWaypointAction`) —
  training missions are unported, unrelated.
- P368-P402 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: a real MonoGame client clicks a sector on
  the starmap and confirms the targeted ship (the player's own ship, or
  whatever selection model you land on) actually turns and flies toward
  that sector, warping through the correct real link when it's not
  directly connected, then stops/clears the order on arrival — mirroring
  native's real "goto" behavior, not just a wire round-trip.
- A test proving a ship with a point/sector target and a ship with a
  station `PlanTarget` don't interfere with each other (mutual
  exclusion, same discipline as the existing mining-XOR-selling
  station-target logic).
- A test proving the server-side relay + receiving-ship execution work
  end-to-end without a live client (a real in-process `Server` +
  `FltBot`, same style as prior tasks' live-proof tests).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The feature, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P403_BUOY_GOTO_COMMAND.md` in the repo root: the trigger
   UI decision, the PlanTarget-extension shape chosen, what was deferred
   (waypoint-in-current-sector and/or last-known-position, if either),
   and the verification performed.
