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

## 2. Where do sides (teams) come from?

**Mission parameters, not the map and not (directly) the core.** The map file
contains no side records, and the core contains only civilizations. Sides are
synthesized at mission-setup time from `MissionParams` + side names + the core.

`CmissionIGC::UpdateSides(now, pmp, sideNames)` (`missionigc.cpp:3734-3793`)
builds one side per team:
- `for (sid = GetSides()->n(); sid < pmp->nTeams; sid++)` — count from
  `MissionParams::nTeams` (`:3749`).
- `GetCivilization(pmp->rgCivID[sid])` (`:3751`) — **the civilization comes
  from the static core**, selected by the per-team `MissionParams::rgCivID[]`
  array (so core+params together decide it, not the map).
- Each side is a `DataSideIGC` with `sideID = sid` (`:3755`),
  `civilizationID = pmp->rgCivID[sid]` (`:3757`), a colour from a hardcoded
  six-entry palette (`:3741-3747`), and its **name from `sideNames[sid]`**
  (`:3762`) — a caller-supplied per-team name array.
- Redundant sides above `nTeams` are torn down (`:3782-3790`); invariant
  `GetSides()->n() == pmp->nTeams` asserted (`:3792`).

Who calls it and where the names come from: `CFSMission` calls
`UpdateSides(..., &m_misdef.misparms, m_misdef.rgszName)` (`fsmission.cpp:1855`,
also `:212`), where `m_misdef.misparms` is the `MissionParams` and
`m_misdef.rgszName` the per-team name buffer on the mission definition —
derived from the map's advertised friendly name plus per-team names
established by the host/lobby, not parsed from the `.igc`. `AddSide`
(`missionigc.cpp:3608-3637`) additionally auto-creates the hidden
`SIDE_TEAMLOBBY` pseudo-side from the first civilization when the first real
side appears.

**Bottom line:** a side is `(id = 0..nTeams-1, name, civilization-from-core)`.
The civilisations and ids are mission-parameter driven; the names are
caller/lobby driven. Neither a `StaticCore` nor a `MissionMap` alone can
derive a side's civilisation or name — so the static `Mission.Assemble` must
take the side specifications (ids + names + civilisation ids) as a parameter
rather than invent them. This is noted and reflected in the design (section 5 /
`Mission.cs`).

---

## 3. Where do sectors come from?

`OT_cluster` records are the sectors, wrapped by `CclusterIGC` objects that
carry extra mutable state. Sectors are loaded by
`CmissionIGC::GenerateMission` (`missionigc.cpp:3830`), which either imports a
custom map file or synthesises one:
- `LoadIGCFile(pmp->szCustomMapFile, this)` (`:3903`) → `Import` (`:3269`) →
  `CreateObject(OT_cluster,…)` (`:3299`, `new CclusterIGC` at `:3325`) →
  `CclusterIGC::Initialize` → `AddCluster` (`:3683`). Each `DataClusterIGC`
  record becomes one sector.
- If no custom map file, `ImapMakerIGC::Create(now, pmp, this)` (`:3905`)
  generates clusters procedurally. Not applicable to the shipped corpus (every
  one of the 111 maps is a file).

`GenerateMission` then populates each cluster (`:4011-4054`): it substitutes
station types that fit each side's dev techs (`:4019-4045`), calls
`PopulateCluster(...)` (`:4048`), and finally `pcluster->SetActive(true)`
(`:4053`).

**Are the `OT_cluster` records the sectors directly, or is extra state built?**
Both. Each sector is one `CclusterIGC` built from one `DataClusterIGC`, but the
object is not a passive record: it aggregates its contents and holds runtime
state. Membership is by `clusterID`:
- warp `CwarpIGC::Initialize` does `pMission->GetCluster(dataWarp->clusterID)`
  then `SetCluster(cluster)` → `cluster->AddWarp(this)` (`warpIGC.cpp:29,49`;
  `clusterIGC` `AddWarp`), so a sector's warps are the records whose
  `ClusterId` equals its `ClusterId`.
- stations and asteroids likewise aggregate onto their cluster by
  `clusterID`.

For the static `Mission`, a `Sector` therefore wraps the `DataClusterIGC` and
its **resolved** contents: the warps/stations/asteroids whose `ClusterId`
equals the sector's `ClusterId`, plus the home-sector flag (`bHomeSector`,
`DataClusterIGC` field — surfaced already by `MissionMap`). Warp destinations
resolve through the sector graph (section 5).

---

## 4. What must exist before `CmissionIGC::Update` is legal — §10 cross-check

`BOOT_PATH.md` §10 proposes a "smallest legal set": trekCore + static core + ≥1
CFSMission + its site + a side/cluster. The trace confirms the shape and pins
down the sequencing that §10 omits:

**§10 says** "*sides are created during mission setup (relies on static core
civilizations)*" and "*A mission with `nTeams>=2` and the map's sectors is the
unit that ticks*". Both hold. What the trace adds:

1. The **site must be bound before anything**, via `Initialize`
   (`missionigc.cpp:3058`). `Update` uses the site for cluster/object host
   callbacks; a mission with no site cannot tick. (Cf. `Terminate`'s
   `GetIgcSite() == nullptr` guard, `:3077`.)
2. The **static core must be attached first** (`SetStaticCore` via
   `LoadIGCStaticCore`). `GenerateMission` pulls constants
   (`GetFloatConstant(c_fcidAmountHe3)`, `:4005`) and `UpdateSides` resolves
   `GetCivilization(pmp->rgCivID[sid])` (`:3751`) — both need the core.
3. **Sides must exist before `GenerateMission`**: `GenerateMission` iterates
   `m_sides` to create buckets and set dev techs (`:3861-3899`), and assumes
   `m_sides.n() > 0` (`pctConquest = 100 / m_sides.n()`, `:3862`). §10's "a
   side" is right, but it understates the side's *ready* state: each side
   needs its **civilisation** (from the core), **buckets** (`CreateBuckets`,
   `:3898`), and **dev-tech / initial-tech** masks (`:3884-3894`) — all set by
   `UpdateSides` + `GenerateMission`.
4. **Clusters must exist, be populated, and be active** before `Update`:
   `GenerateMission` runs `PopulateCluster` and `SetActive(true)` per cluster
   (`:4048,4053`). §10's "the map's sectors" understates that a *generated*
   sector is only tick-ready after `GenerateMission` finishes.
5. `GenerateMission` requires a valid `MissionParams` (`m_missionParams = *pmp`,
   `:3844`), supplied via `CFSMission` (`fsmission.cpp:1844` `SetMissionParams`).

So the corrected smallest legal set before `Update` (the tick) is:

```
Initialize(now, site)            → host bound, clock stamped
  ↓
SetStaticCore (LoadIGCStaticCore)→ type universe present
  ↓
SetMissionParams(pmp)            → mission params present
  ↓
UpdateSides(now, pmp, names)     → sides exist, each with civilisation
  ↓
GenerateMission(now, pmp, …)     → buckets + dev/initial techs, sectors loaded,
                                   populated, active, stations type-valid
  ↓
Update(now)                      → legal to tick
```

**§10 verdict: correct in substance, incomplete in sequence.** It names the
right ingredients (mission + site + core + sides + sectors) but omits
`SetMissionParams`/`UpdateSides`/`GenerateMission` — the three calls that turn
those ingredients into tick-ready state. Ships are **not** required: an empty
mission with no players still ticks (cf. `ResetMission`, `:3804`).

---

## 5. Pure data assembly vs `IIgcSite` callbacks

Split along whether a step needs the host seam or only the two documents.

**Pure data assembly — portable now (what `Mission.Assemble` builds):**

- Reading `OT_cluster`/`OT_warp`/`OT_station`/`OT_asteroid` records — already
  `MissionMap`.
- Grouping warps/stations/asteroids into the sector named by their `ClusterId`
  (membership rule, section 3).
- **Warp-destination resolution:** the engine resolves a warp's partner lazily
  via `GetWarp(m_warpDef.destinationID)` (`warpIGC.h:129-141`) and the
  destination **sector** is that partner warp's cluster (or the sector directly
  when `destinationID` names a cluster — the corpus validator accepts either,
  `CrossRefValidator.cs:139-143`). This is a pure relation over
  `MissionMap.Warps`/`Clusters`: `dest sector = (warp named by DestinationId) →
  its ClusterId, else the cluster named by DestinationId, else dangle`.
- Side skeleton `(id, civilization)` from `MissionParams::rgCivID[]` looked up
  in the core — the *relation* is pure, but the *choice* of `rgCivID` and the
  names are external, so `Assemble` receives side specs as a parameter.
- Sector→sector connectivity, home-sector flags, counts.

**Needs `IIgcSite` / runtime — defer (not in `Mission.Assemble`):**

- Preloading collision hulls (`HitTest::Load`, `missionigc.cpp:3064-3068`).
- Constructing the physically-instantiated objects (positions, hit-test, model
  mounts) — `CreateObject` builds real `CclusterIGC`/`CwarpIGC`/… objects with
  a site and a cluster site.
- Randomised aleph repositioning (`:3913-3976`), He3 budget (`:3978-4009`).
- Station-type substitution against a side's dev techs (`:4011-4046`),
  `PopulateCluster` generating asteroids/treasures (`:4048`), territory
  counting (`:4075-4101`).
- Buckets and dev/initial-tech setup (`:3860-3899`).

All of the latter mutate sim state or ask the host for collisions/pulses/build
effects — out of scope for a static world assembly.

---

