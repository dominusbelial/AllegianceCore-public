# Mission init trace

_What a mission build needs from the static core and the map to produce
sides and sectors — traced from `src/Igc/missionigc.cpp` against
`BOOT_PATH.md`, 2026-08-28. Companion to `README.md`; precedes the static
`Mission` assembly in `src/Allegiance.Core/Model/Mission.cs`._

---

## 1. `Initialize` vs `CreateMission` vs `SetStaticCore` — roles and ordering

Three distinct steps. Each has one job, and the ordering in the server boot is
strict.

**`CreateMission()` — allocate the empty mission.**
`missionigc.cpp:2680-2683` is literally `return new CmissionIGC;`. It constructs
the blank `CmissionIGC` object and nothing else. No fields beyond the C++
constructor defaults are touched here.

**`CmissionIGC::Initialize(Time now, IIgcSite* pIgcSite)` — bind the host and
prepare the mission clock.** `missionigc.cpp:3055-3072`:
- `m_pIgcSite = pIgcSite` — stores the `IIgcSite` (the host seam).
- `m_lastUpdate = now` — stamps the mission clock (time basis for `Update`).
- `m_damageTracks.Initialize(now)`.
- Preloads five asteroid convex hulls: `HitTest::Load("bgrnd50"…"bgrnd03")`.
- Clears `m_sideTeamLobby`.

It does **not** build sides, sectors, or read any map/core. It is pure host
binding + housekeeping. Called once at boot on the gateway mission
(`FedSrv.CPP:9298`).

**`SetStaticCore` — attach the type universe.** It has no `CmissionIGC::`
implementation of its own in `missionigc.cpp`; it is invoked by
`LoadIGCStaticCore` (`missionigc.cpp:2390`). On a cold load the flow is
`missionigc.cpp:2420` `SetStaticCore(NULL)` (detach) →
`LoadIGCFile` (`:2422`), which calls `pMission->Import(now,-1,pData,datasize)`
(`:94-111`), which runs `CreateObject` (`:3299`) over every typed record and
materialises the static-core objects (types, civilizations, …) in the mission,
then the resulting core is cached and re-attached by pointer on later loads
(`:2400`). `SetStaticCore` is the load point for the ID-keyed type universe that
`StaticCore` mirrors; the actual object construction is the generic
`Import`→`CreateObject` path, the same one used for map files.

**Ordering contract** (from `ReceiveThread`, `FedSrv.CPP:9297-9306`):
1. `CreateMission()` — empty object.
2. `Initialize(now, site)` — bind host + clock (before the core/map loads).
3. `SetStaticCore` (via `LoadIGCStaticCore`) — attach the type universe.

After that the mission "holds" a static core but has no sides, no sectors, and
does not tick yet. Sides + sectors are produced later by `UpdateSides` +
`GenerateMission` (sections 2-3). **Conclusion:** `Initialize` is not the
"sides and sectors builder" the boot path wondered about — those are built by
`UpdateSides` and `GenerateMission`, which must run after both the site binding
and the static core attach.

---
