# P293 — Treasure Visual (live treasures are now tracked and visible client-side; buoy researched and deferred)

## Relationship to the arc

- `P123` (cash-treasure pickup), `P144` (DROP_PART → part-treasure), `P145`
  (FM_C_TREASURE_ACK part-treasure pickup round-trip), `P201` (flag capture via
  a `c_tcFlag` treasure) — the treasure mechanic is **fully simulated and
  reachable server-side**. `Allegiance.Core.Sim.Treasure` is a real sector
  `Model` picked up by `Sector.ResolveTreasurePickups`.
- `MINE_VISUAL_P291` / `CHAFF_VISUAL_P292` — the sibling tasks that established
  the pattern this one repeats: real client-side wire-state plumbing into
  `ClientWorld` + a missing visual driven by a real wire message, plus a pure
  `MeshFactory`-style placeholder.
- **Gap closed here (treasure):** treasures were **not tracked client-side at
  all** (`ClientWorld` had no `TreasureEntity`/`Treasures`; `ConnectionSession`
  did not consume `TREASURE(28)`/`DESTROY_TREASURE(30)` — confirmed by grep
  before building), so nothing could render them even though they exist live
  (e.g. a peer's dropped part is a real `TREASURE(28)` object in the sector).
- **Buoy — investigated and DEFERRED (see below).** It is not server-simulated
  in this port; a renderer would be premature.

## Investigation findings (the brief's "investigate scope first")

### Treasure — what it is and how native draws it

Native `CtreasureIGC` (`src/Igc/treasureIGC.h/.cpp`) is a lootable pickup
(object type `OT_treasure`): a ship whose base hull has the `c_habmRescue`
capability touching it triggers `HitTreasureEvent` → pickup
(`treasureIGC.h:78-99`); hitting a static object or expiring destroys it.

Its visual is **decided at runtime, not a fixed `ModelName`**
(`treasureIGC.cpp:118-191`):
- a *buyable* treasure (part / development) uses that buyable's own model:
  `name = m_buyable->GetModelName()` (`treasureIGC.cpp:125-126`);
- a *flag* treasure uses the `"biosflag"` decal (`texture = "biosflag"`,
  `icon = "flag"`) or the `"artifact"` model for the team-lobby artifact case
  (`treasureIGC.cpp:128-149`);
- a *cash / powerup* treasure uses the fixed `"acs14"` model + `"cashicon"`/
  `"iconpu"` icon (`treasureIGC.cpp:153-155`).

Crucially the wire `DataTreasureIGC` carries **no `ModelName`** (igc.h:2517-2529
— the brief's claim is accurate), so the visual is always derived from
`treasureCode` + `treasureID`. Radius = `SetRadius(5.0f)` — hardcoded, `//NYI --
needs to be gotten from the DB` (`treasureIGC.cpp:193`); there is **no size
field** on the record (the brief's "scaled by whatever real size field"
cannot apply — the only real size is the fixed 5.0). Non-flag treasures get a
random orientation **and** a random rotation `r.angle(random(-6,6))`
(`treasureIGC.cpp:180-190`), i.e. they tumble. `DataTreasureSetIGC` is the
static-core treasure-**set catalog** (the spawn table), not a per-instance
container — irrelevant to rendering an individual treasure.

Because none of those runtime-decided native models/assets is present in this
checkout (the same two-tree situation ships/warps/asteroids were already in),
P293 builds a **procedural placeholder** — a single shared gold faceted gem —
framed exactly like P289's station placeholder (deliberately not over-designed).

### Treasure — real depth in this port (citation-verified)

`Allegiance.Core.Sim/Treasure.cs` is the port of `CtreasureIGC`, kept to what
cash pickup needs (static model, `DataTreasureIGC`, `PickupRadius = 5.0f`;
movement-integration and expiry are deferred spawn-system tasks per its
honest-scope remarks). Server reachable live paths:
- `Server.PerformTreasurePickup` (cash credit + `DESTROY_TREASURE(30)`)
  called from `Sector.ResolveTreasurePickups` (`Server.cs:666-676,8824`).
- `HandleDropPart` creates a real part-treasure and broadcasts
  `FM_S_TREASURE(28)` sector-wide (`Server.cs:3124,6961,7013-7014`).
- `HandleTreasureAck` (P145) re-spawns a declined part-treasure with another
  `TREASURE(28)` (`Server.cs:7817`).

So treasure is genuinely simulated **and** reachable in live gameplay — the
live path P293 proves is `DROP_PART(33)` → `TREASURE(28)`.

### Buoy — what it is, why it is deferred

Native `CbuoyIGC` (`src/Igc/buoyigc.h/.cpp`, object type `OT_buoy`) is a
**navigation / waypoint marker**, not a combat object: it loads a single
`"buoy"` decal (`LoadDecal("buoy","buoyicon",...)`, `buoyigc.cpp:39-43`), is
consumer-refcounted (terminates when its consumers drop to 0,
`buoyigc.h:76-87` — ships/missiles consume it as a target), carries a
`BuoyType` that sets its secondary name to the cluster name or `"waypoint"`
(`buoyigc.cpp:62-65`), and is **created by the CLIENT**
(`ClientConnection.CreateBuoy`, `WinTrek/console.cpp`) — it is carried on the
wire only as the embedded `DataBuoyIGC db` block inside `FM_CS_CHATBUOY(108)`
(`Messages.h:716-722`; `DataBuoyIGC.cs`).

In this port `DataBuoyIGC.cs` documents the same: *"buoys have no separate
server-side object lifecycle: they are pure client-side ephemeral target
markers."* The server never exports a buoy as a synced object; it only relays
`CHATBUOY(108)` verbatim through the chat router (`Server.cs:1887-1924` →
`ClientWorld.Events.ChatBuoys`, an event log). `ConnectionSession`/`Bot`/`Ship`
touch buoy only for that chat-dispatch routing. There is **no live-reachable
3D buoy** in this port — no creation path, no client nav/waypoint system.

Per the brief's step 3, a buoy renderer is therefore **skipped**: rendering an
object that never spawns as a synced 3D object would be premature. The real
prerequisite is a separate, larger future task: **client-side navigation /
waypoint markers (client buoy creation + target persistence)**; once those
exist, a `buoy` decal/marker visual (mirroring native's single "buoy" decal)
would follow. Buoy is named here explicitly as that future prerequisite.

## What changed (treasure only)

1. **`ClientCore/State/TreasureEntity.cs`** (new) — an id-keyed entity
   mirroring `MineEntity`: `TreasureId` (key, = `DataTreasureIGC.ObjectId`),
   `ClusterId`, `PayloadId`, `Amount`, `Code`, `Position`. Static (this port
   places treasures at rest), so `P0` is final; no size field (fixed 5.0).
2. **`ClientCore/State/ClientWorld.cs`** — a `Treasures` dict populated by
   `Apply(TreasureSpawn)` (fmid 28, upsert-by-key) and removed by
   `Apply(DestroyTreasure)` (fmid 30), plus the `case TreasureSpawn:` /
   `case DestroyTreasure:` arms in the `Apply(IFedMessage)` switch.
3. **`ClientCore/Session/ConnectionSession.cs`** — routes `TreasureSpawn.Fmid`
   (28) and `DestroyTreasure.Fmid` (30) to the world (the P264 lesson: without
   the `Apply(IFedMessage)` case the per-type `Apply` is dead code — both
   proven by must-fail tests that red when the arm is removed). Also added
   `SendDropPart(EquipmentType, byte)` — the client-side mirror of
   `Bot.SendDropPart`, the outbound half that puts a treasure in the scene in
   live play.
4. **`ClientCore.MonoGame/Rendering/TreasureMeshFactory.cs`** (new, pure) — a
   deterministic gold faceted gem (an octahedron: 6 vertices, 8 triangles,
   per-vertex gold gradient), no `System.Random`/time — byte-identical across
   calls, unit radius 1.
5. **`ClientCore.MonoGame/Rendering/TreasureRenderer.cs`** (new) — builds one
   shared `VertexBuffer`/`IndexBuffer` from the factory; draws each treasure as
   a self-lit opaque gem scaled by `DefaultRadius = 5.0f` (the native pickup
   radius), gently tumbling about local Y (native's random rotation).
6. **`Game1.cs`** — instantiates the renderer in `LoadContent`; in the render
   loop gathers `world.Treasures` under the Gate lock, filters to the local
   player's own sector (`ClusterId == mySector`, the mine/probe pattern), and
   draws each at its real position scaled to 5.0.

## The real wire messages used for tracking

- **Creation:** `FM_S_TREASURE(28)` (`Messages.h:253-255`) carrying the whole
  `DataTreasureIGC` (44 B) — the port's real first sends are the `DROP_PART`
  part-treasure path (`Server.cs:7014`) and the treasure-ack decline re-spawn
  (`Server.cs:7817`), both `SendSectorWide` to the treasure's sector.
- **Removal:** `FM_S_DESTROY_TREASURE(30)` (`Messages.h:257-261`) =
  `{treasureID, sectorID}` (8 B), broadcast whole-mission on pickup/expiry
  (`Server.cs:8824`; native `KillTreasureEvent`, `FedSrv.CPP:12493-12509`).
- **Trigger path proven live:** `FM_CS_DROP_PART(33)` from the client →
  `Server.HandleDropPart` creates the treasure + broadcasts `TREASURE(28)`.

## Scope boundary (unchanged by design)

- Probe, mine, chaff, building-effect rendering remain separate tasks.
- No treasure/buoy **simulation** changes — the spawn/expiry/movement
  systems are separate future tasks (this task is investigate + render only).
- Buoy rendering is **deferred** (see above), not built.
- Native reference source (~/Development/Allegiance) is read-only.

## Verification (real, not just build/test)

**Live proof** (`/tmp/p293verify`, evidence copied to `.verify_p293/`): boots a
real in-process server (teams:2) + a real headless `ConnectionSession` (the
true client wire path), readies to `Started`, mounts a real core weapon on the
spawned ship, and presses the real client key path
`SendDropPart(EquipmentType.Weapon, 0)`. Key log lines:

```
[droppart] 1 ship 8096: dropped Weapon@0 -> DROP_PART(33) shipID=8096 sector 1052 wide
[droppart] 1 ship 8096: created part treasure obj=5464 part=3 amount=20 -> TREASURE(28) sector 1052 wide
[p293] SERVER: live treasure 5464 code=Part part=3 amt=20 at (812.0,830.6,932.1)
[p293] CLIENT world.Treasures hydrated: code=Part part=3 amt=20 at (812.0,830.6,932.1)
[p293] server-vs-client position delta = 0.0000 (must be ~0)
```

The `CLIENT world.Treasures hydrated` line is the **real wire evidence**: the
`world.Treasures` table grew only because `ConnectionSession` routed the real
`TREASURE(28)` broadcast into `ClientWorld.Apply(TreasureSpawn)` (position
delta 0.0000 vs the server's authoritative treasure, `Code`/`part`/`amt`
matching the dropped weapon). The two PNGs
(`treasure_closeup_real_position.png`, `treasure_threequarter.png`) show the
real `TreasureRenderer` gold gem visible in the 3D scene at that real position
(image-verified: a faceted golden diamond with the RGB origin axes).

**Build:** whole solution `AllegianceCore.slnx`, 0 warnings / 0 errors.

**Tests:** `ClientCore.Tests` 149 (added 6 `ClientWorldTreasureTests` + 1
`TreasureDropLiveTests.SendDropPart_ClientWorldReceivesTreasureSpawn_OverTheWire`),
MonoGame.Tests 511 (added 5 `TreasureMeshFactoryTests`). The live wire test was
proven to FAIL when the `ConnectionSession` `case TreasureSpawn.Fmid:` is
removed, and the dispatch test when the `ClientWorld.Apply(IFedMessage)`
`case TreasureSpawn:` is removed (both must-fail-if-removed, reverted). Core.Tests
full suite (baseline 3163) run 2×: run 1 = 3158 pass / 5 fail, run 2 = 3162 pass
/ 1 fail — every failure is a documented live-network flake (StationsUpdateLiveTests,
LiveMoneyTests, CargoBayReloadTests — the latter passes green in isolation —
and the persistent LiveTurretGunnerBindingTests timing flake, the only expected
one). Core.Tests references only Core/Server/Bot/Lobby, and P293 changed only
ClientCore/ClientCore.MonoGame, so Core.Tests cannot be affected (verified: no
treasure symbols in any flaking file). ClientCore (149) + MonoGame (511) both
ran clean 2×.

## Commit
On `master` (not pushed — Domingo pushes). No side branch.
