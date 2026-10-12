# Native base-construction orientation & docking-door placement — research for issue #189

Scope: determine exactly what the native Allegiance client does with base orientation at
build time, so the AllegianceCore port can decide whether to send orientation to the server
or keep it a client-side preview of the native default.

Native tree: `/home/domingo/Development/Allegiance/src`
Port tree:   `/home/domingo/Development/AllegianceCore/src`

All file:line citations below were read directly from the source.

---

## 1. The native build cursor / "build on" interaction

There is **no 3D station ghost, no door preview, and no player-choosable orientation** in the
native build path. The "build on" interaction is a *command verb + target asteroid*:

- The build action is a command id constant `c_cidBuild = 9` (`Igc/igc.h:910`).
- The player selects the build verb in the client console: `SetVerb(c_cidBuild)`
  (`WinTrek/console.cpp:1771`); the verb is subsequently exercised against whatever target the
  crosshair/cursor points at (`console.cpp:1945`, `console.cpp:2216` — the build verb only acts
  when the selection is on the same sector).
- The default-order resolution turns "Builder-role ship + asteroid target" into a Build order:
  `cid = (pt == c_ptMiner) ? c_cidMine : c_cidBuild;` (`clintlib/clintlib.cpp:2212`,
  also `clintlib.cpp:2399`), and the server's own drone order picker does the same
  (`Igc/shipIGC.h:2087`, `shipIGC.h:2095`).
- Placement is simply "the rock's position becomes the station's position": a constructor flies
  to the asteroid, collides, and a `building effect` (a shrink-wrap that grows into the station)
  is created at the asteroid's position (`Igc/shipIGC.cpp:881-884` → `CreateBuildingEffect`;
  `Igc/buildingeffectigc.h`). Nothing about orientation is presented to the player beforehand.

**Conclusion for (1):** native maps a rock to a placement as *position = asteroid position,
orientation = whatever the build-completion routine decides (section 3)*. There is no pre-build
orientation preview and no way to rotate it. Issue #189's door markers / ghost preview / right-click
rotate are all net-new for the port (confirmed absent in the port too, see §5).

---

## 2. Does the client ever send a base orientation to the server? — **No.**

The build order travels as a command whose payload is only *(commandID, target objectType,
target objectID)* — the target is the asteroid. **No orientation/quaternion/yaw field exists in
the order.** Evidence:

- The player/AI order is carried as a chat/command with the commandID + target model only:
  `SendChat(pshipTo, ..., c_cidBuild, targetRock)`, then
  `pshipTo->SetCommand(c_cmdCurrent, pmodel, c_cidBuild)` / `SetCommand(c_cmdAccepted, ...)`
  (`clintlib/clintlib.cpp:2239-2265`). The same shape is used by the Wopr commander AI:
  `SendChat(..., (sbyte)CommandID.c_cidBuild, targetRock.GetObjectType(), targetRock.GetObjectID(), ...)`
  (`Wopr/Wopr/Strategies/CommanderResearchAndExpand.cs:569`).
- `SetCommand` accepts only *(slot, target-model, commandID)* — no orientation argument anywhere
  in the command path (`Igc/shipIGC.h:2050-2410`, `shipIGC.cpp:4014-4109`).

**The server derives/computes orientation itself, at build COMPLETION, from scratch — it never
receives it.** The constructor does not even create the station until the building effect matures:
- Building effect completion calls the site hook `BuildStation(...)` (`Igc/buildingeffectigc.h:348-351`),
  destroying the rock and spawning the station.
- Server implementation `FedSrvSiteBase::BuildStation` (`FedSrv/FedSrv.CPP:11374-11399`) sets a
  **fixed, hard-coded orientation**:
  ```cpp
  ds.forward.x = ds.forward.y = 0.0f;   ds.forward.z = 1.0f;   // forward = +Z
  ds.up.x      = ds.up.z = 0.0f;        ds.up.y      = 1.0f;   // up      = +Y
  ds.rotation.axis(ds.forward); ds.rotation.angle(0.0f);       // identity rotation
  ```
  (`FedSrv.CPP:11390-11395`). Note the developer's own admission above it:
  `//NYI decide on the correct hack for the placement of space stations` (`FedSrv.CPP:11345`).

So the **server** always spawns a constructor-built station facing +Z, upright (+Y), no roll.

**Subtlety worth recording:** the **client's own local copy of the IGC sim** runs the same building
effect and calls its own `WinTrekClient::BuildStation` (`WinTrek/trekigc.cpp:3841-3863`), which
instead aligns the station to the **asteroid's** orientation:
```cpp
ds.forward = pasteroid->GetOrientation().GetForward();   // trekigc.cpp:3847
ds.up      = pasteroid->GetOrientation().GetUp();        // trekigc.cpp:3848
ds.rotation.axis(ds.forward); ds.rotation.angle(0.0f);    // trekigc.cpp:3849-3850
```
Because the client creates the station locally (usually before the server export arrives) and
`CstationIGC::Initialize` treats a re-export of an already-known station as "black magic time" —
it only updates hull type / name / fractions, **not** position or orientation
(`Igc/stationIGC.cpp:31-49`) — the orientation the player *visually* sees on a freshly built base
is effectively the **asteroid's orientation** (client-local), whereas the server's authoritative
DB/authoritative record is the fixed **+Z** default. Neither path ever involves a player-supplied
value.

**Conclusion for (2):** the native wire/order carries no orientation; the server always decides
(fixed +Z/up +Y) when the constructor completes. There is nothing to "send" — orientation is not a
transportable field of the native build protocol.

---

## 3. Native default orientation for a newly built base

Two co-existing "defaults", depending on which layer you ask:

| Layer | Value | Citation |
|---|---|---|
| **Server authority** (what the sim/DB records, what all clients' authoritative world converges to) | `forward=(0,0,1)`, `up=(0,1,0)`, rotation angle 0 | `FedSrv/FedSrv.CPP:11390-11395` |
| **Client visual** (local sim, what the player most often sees first) | asteroid's forward/up (random-ish per rock), rotation angle 0 | `WinTrek/trekigc.cpp:3847-3850` |

The spawned station's orientation is transported on the wire as part of the `OT_station` export
(`DataStationIGC` carries `forward`, `up`, `rotation`); the receiving object builds its orientation
from those two vectors — `Orientation o(dataStation->forward, dataStation->up); SetOrientation(o)`
(`Igc/stationIGC.cpp:57-62`), and `Export()` writes them back out (`stationIGC.cpp:89+`).

**Recommendation implication:** the deterministic, reproducible "native default" for the port to
mimic client-side is the **server default: forward = +Z, up = +Y, yaw 0**. That is also what the
port's own server actually emits (see §5), so it is the correct baseline for a build-cursor preview.

---

## 4. Where the docking doors physically sit — model frames, rotated by orientation

The docking doors are **not** a separate geometric marker; they are **named mount frames inside the
station's `.mdl` model**, read at station-type load time, and then **rotated by the station's
orientation at runtime**.

- Load-time: the station type's constructor loads the model and scans named frames:
  - Launch slots — frames named `launch1`, `launch2`, … :
    ```cpp
    const FrameDataUTL* pfd = pmhb->GetFrame(bfrLaunch);            // "launch1..N"
    m_positionLaunches[n]  = pfd->position * scale;                  // stationtypeigc.cpp:61
    m_directionLaunches[n] = pfd->forward;                           // stationtypeigc.cpp:62
    ```
    (`Igc/stationtypeigc.cpp:40-66`).
  - Landing/docking bays — frames named `capgarage1A..` (and by extension the fighter garage):
    ```cpp
    bfrLand[9]='1'+m_nLandSlots; bfrLand[10]='A';                   // "capgarage1A.."
    m_positionLandPlanes[...][...] = pfd->position * scale;         // stationtypeigc.cpp:91
    m_directionLandPlanes[...][...] = pfd->forward;                 // stationtypeigc.cpp:92
    ```
    (`Igc/stationtypeigc.cpp:68-94+`). So **door direction = the model frame's `forward`, scaled to
    gameplay units**, and each frame's `position` gives the door's location in the model's local frame.
- Runtime: the dock/launch direction the player actually flies into is that model-frame direction
  **transformed by the station's current orientation**:
  ```cpp
  Vector direction = GetLandDirection(i,j) * orientationStation;   // InGarage  stationIGC.cpp:419
  Vector point     = GetLandPosition(i,j)  * orientationStation;   //           stationIGC.cpp:420
  ```
  (`Igc/stationIGC.cpp:419-420`; launch analogue `GetLaunchDirection(slot) * o` at
  `stationIGC.cpp:351`, and `FedSrv/FedSrv.CPP:12985`.)

**Conclusion for (4):** door direction = station-model `.mdl` frame `forward` rotated by the
station's orientation. There is no door marker object; the door is purely a model attribute. Rotating
a station (a yaw) rotates the door direction with it — which is exactly why a build-cursor preview
needs to draw the door markers at `GetLandPosition * orientation` pointing along
`GetLandDirection * orientation`, so that rotating the preview moves the doors believably.

**Port equivalents (already present, so the preview can be implemented faithfully):**
- The port exposes parsed `.mdl` frames: `MdlFrame` / `MdlFile` (`Allegiance.Core/Data/MdlFile.cs:43,62`);
  ship renderer exposes `FramesFor(modelStem)` (`Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs:149-155`).
- The port already computes landing-bay geometry from model frames: `HullLandingBay` scans
  `CvhFile.Frames` (`Allegiance.Core/Collision/HullLandingBay.cs:49,167`).
- The port's dock planner reproduces the native transform
  `centers[i] = GetLandPosition(i,0) * orientation` and consumes `bay.Direction`
  (`Allegiance.Core/Sim/DockApproach.cs:58,167-185`).

---

## 5. Port match & recommendation table

**Port build order (matches native — no orientation):** the port's order message
`OrderChange` (`Allegiance.Core/Wire/OrderChange.cs`) is `FM_CS_ORDER_CHANGE(93)` with fields
`ShipID, CommandId, Command, ObjectType, ObjectID` only — **no orientation field**
(`OrderChange.cs:193` `CidBuild = 9`, encode at `:213`, decode at `:235`). The client's
`CommandOrderResolver.Resolve` emits exactly `(CidBuild, IgcObjectType.Asteroid, targetId)`
(`Allegiance.ClientCore.Game/CommandOrderResolver.cs:42-49`). This is bit-for-bit the native shape
(§2) — there is nowhere to put a yaw, matching native.

**Port station spawn (matches the native SERVER default exactly):** the port's build-completion
`CompleteBuild` (`Allegiance.Core/Model/Sector.cs:4274-4320`) → `BuildStationRecord`
(`Sector.cs:4328-4343`) hard-codes
`Up=[0,1,0], Forward=[0,0,1], Rotation=identity` — the direct port of `FedSrv.CPP:11390-11395`.

**Client world already carries orientation:** `ClientWorld.Apply(ExportStation)` stores the full
`DataStationIGC` incl. `Up/Forward/Rotation` (`Allegiance.ClientCore/State/ClientWorld.cs:1909`;
`Allegiance.Core/Data/DataStationIGC.cs:21-32`), so the client already knows a station's orientation
and can compute door directions. (The port's MonoGame client currently has **no** build-cursor station
preview, door markers, or rotation input — all three are greenfield for #189.)

### Recommendation table

| Decision | Recommendation | Rationale / citation |
|---|---|---|
| Send orientation to the (real FreeAllegiance) server? | **Nothing.** Do not attempt to send orientation. | Native build order carries only commandID+target asteroid (§2); the native server `BuildStation` ignores any orientation and hard-codes `forward=+Z, up=+Y` (`FedSrv.CPP:11390-11395`). There is no wire field; the server will always reset it. |
| Native default to mimic in the preview | **forward = +Z, up = +Y, yaw = 0** (the native server default) | Deterministic and identical to what the port's own `BuildStationRecord` emits (`Sector.cs:4333-4335` ↔ `FedSrv.CPP:11390-11395`). Alternative cosmetic option: asteroid-aligned (`trekigc.cpp:3847-3850`) if parity with the native *client's visual* is desired instead — but for the port's own server the +Z default is what the base will actually look like. |
| Right-click rotate (±90° yaw) — implement how? | **Client-side preview only** by default: rotate the ghost + redraw door markers; do **not** persist it in a build order. | The real server ignores orientation; persisting would mislead (the finished base snaps back to +Z). Matches native's model where the player never controls orientation (§1–§2). |
| If the port's **own** server should honor a chosen yaw (self-hosted, not talking to native) | Extend it deliberately: (a) add an optional yaw to `OrderChange` (new field, wire is currently fixed-size at `OrderChange.cs:213`), and (b) apply it in `BuildStationRecord` by composing the yaw into `Forward`/`Rotation` (`Sector.cs:4332-4335`). Document the divergence from native. | Only viable when the port's server is the source of truth; the native server cannot participate. |
| Door-marker source for the preview | `GetLandPosition(i,0) * orientation` for position, `GetLandDirection(i,0) * orientation` for direction — i.e. the station `.mdl` `capgarage1A`/`launch1` frames transformed by the (possibly yaw-rotated) preview orientation. | Native `stationtypeigc.cpp:61-62,91-92` + `stationIGC.cpp:351,419-420`; port already parses frames (`MdlFile.cs:62`) and computes bay geometry (`HullLandingBay.cs:167`, `DockApproach.cs:58`). |

### Bottom line for the port
- The native client **never sends** base orientation; the server fixes it at build completion
  (`forward=+Z, up=+Y, yaw 0`). The port already replicates this exactly on its own server
  (`Sector.cs:4333-4335`).
- Issue #189's preview + right-click rotate should therefore be implemented **purely client-side**:
  a translucent station ghost oriented at the native default (yaw 0 = forward +Z), with red/green
  door markers computed from the `.mdl` dock frames transformed by that orientation, and right-click
  cycling a preview-only ±90° yaw. Nothing new goes on the wire.
- Matching native is confirmed on the two relevant seams: the build *order* has no orientation field
  (both native and port), and the spawned *station* uses the same fixed default (both native server
  and port `BuildStationRecord`).
