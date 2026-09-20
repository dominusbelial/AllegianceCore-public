# P267 — Wire the remaining decoded-but-unwired ambient/event sounds

## Relationship to native

The port's `DataHullTypeIGC` / `DataStationTypeIGC` / `DataDevelopmentIGC`
records faithfully decode the native sound-id fields from the core
(`PCore001.igc`) but, until now, never played them client-side. Native plays
them in the WinTrek client — the per-object `ShipSoundSite` / `AmbientSoundSite`
in `src/WinTrek/trekigc.cpp` (ships, stations) and the one-shot handlers in
`src/clintlib/appmsg.cpp` (station destroyed/captured, development complete).

The port's MonoGame client carries **no static core** (it knows only the core
filename), so per the established single-real-default convention (P232 weapon,
P236 missile, P237 warp, P260 probe, P264 warning) each sound is wired to a
**real default asset** drawn from the demo core's *actual* values (decoded
below from PCore001.igc and mapped through `Artwork/sounddef.mdl`), keyed by
the real wire event that triggers it.

## Real core values → assets (decoded from PCore001.igc via StaticCore)

- Small-ship hull family (id 140/180/220/260/300/340) = `sshipint` /
  `shipout` / `accelSmall` / `accelSmall` / `adjust` / `adjust`
  (`sounddef.mdl:1980-1996`). Capital family (160/200/240/280/320/360).
- Stations: `ExteriorSound=380` = `ILStationExternalSound` (loop = `shipout`);
  `DestroyedSound=860-867` = `garrisonDestroyedSound`…; `EnemyDestroyed=900s`;
  `Captured=880s`; `EnemyCaptured=920s`; `InteriorAlert=460`;
  `UnderAttack=820s`; `Critical=840s`; `Completion=800s`;
  `ConstructorNeedRock=740s`; `ConstructorUnderAttack=760s`;
  `ConstructorDestroyed=780s`.
- Developments: `CompletionSound=940-945` = `newResearchCompletedSound`
  (`vo_sal_researchcomplete`), `newStationUpgradeSound`, etc.

## Per-sound table

| Sound (core id → asset) | Real native trigger (citation) | Status |
|---|---|---|
| Hull `InteriorSound` (140 → sshipint) | Always-on pilot interior ambience in the player's ship, `trekigc.cpp:1312-1313` | **Already-wired** (cockpit hum `sshipint`, P213) — no change |
| Hull `ExteriorSound` (180 → shipout) | Always-on positional loop per in-flight ship, `trekigc.cpp:1337` | **Wired** (`shipout` loop in chase/exterior view) |
| Hull `MainThruster{Int,Ext}Sound` (220/260 → accelSmall) | While `m_fThrustSoundLevel>0.01`, `trekigc.cpp:1314-1316,1338-1340` | **Already-wired** (engine loop `accelsmall`, P213); exterior twin shares the same asset (`sounddef.mdl:1014-1015`) |
| Hull `ManuveringThruster{Int,Ext}Sound` (300/340 → adjust) | While `m_fTurnSoundLevel>0.01`, pitch `0.5+lvl/3`, `trekigc.cpp:1317-1319,1341-1343,:1167-1169` | **Wired** (turn loop driven by commanded yaw/pitch/roll) — **LIVE-PROOFED** |
| Hull hardpoint `InteriorSound`/`TurnSound` (gunner/turret) | Gunner interior `trekigc.cpp:1107,1310`; turret traverse `:990-996` | **Flagged**: no turret-boarding flow in the POC → unreachable |
| Station `ExteriorSound` (380 → shipout) | Per-station positional ambient, `trekigc.cpp:1465-1468` (`AmbientSoundSite(GetExteriorSound())`) | **Wired** (distance-based station ambient loop) |
| Station `InteriorSound` (420/445) | Played when the player is docked in the station, `trekigc.cpp:5076` | **Flagged**: docked/hangar interior audio state deferred (POC flyable focus) |
| Station `DestroyedSound`/`EnemyDestroyedSound` (860s/900s) | One-shot on `STATION_DESTROYED(59)`, allied→`Destroyed` else→`EnemyDestroyed`, `appmsg.cpp:548-551` | **Wired** — **LIVE-PROOFED** (enemy destroyed) |
| Station `CapturedSound`/`EnemyCapturedSound` (880s/920s) | One-shot on `STATION_CAPTURE(58)`, `sidOld==ours`→`Captured`, `sidNew==ours`→`EnemyCaptured`, `appmsg.cpp:674-677` | **Wired** |
| Station `InteriorAlertSound`/`UnderAttackSound`/`CriticalSound` (460/820s/840s) | Decoded in the core; **never played by the native client checkout** (no `Get*` call site in WinTrek/clintlib) | **Honestly flagged**: no citable client trigger |
| Station `ConstructorNeedRockSound`/`ConstructorUnderAttackSound` (740s/760s) | Native plays the need-rock in the constructor-mining flow `shipIGC.cpp:2320`; under-attack VO is server-side `FedSrv.CPP:12127` — no client ambient-site consumption | **Honestly flagged**: undetermined client trigger |
| Station `ConstructorDestroyedSound` (780s) | Used in the destroyed-sound VO selection for constructor hulls, `appmsg.cpp:2879` | **Honestly flagged**: `STATION_DESTROYED` carries only StationId — the client can't classify the object as a constructor |
| Station `CompletionSound` (800s) | Played by the construction **building-effect** object, `buildingeffectigc.h:321` | **Honestly flagged**: the POC models no building-effect object |
| Dev `CompletionSound` (940-945 → vo_sal_researchcomplete) | One-shot on `DEV_COMPLETED(8)`, `appmsg.cpp:3318-3327` (`PlaySoundEffect(GetCompletionSound())`) | **Wired** — **LIVE-PROOFED** |

## Implementation

- **Client world**: `ClientWorld` now records transient one-shot sound events
  from the real wire (`STATION_DESTROYED(59)`, `STATION_CAPTURE(58)`,
  `DEV_COMPLETED(8)`) as `ClientSoundEvent`s, with the own-vs-enemy variant
  decided from the local side (collapsed to same-side — the client has no
  alliance graph, documented). Destroyed stations leave the world table (which
  also ends their ambient loop via the planner's disappearance pass); captured
  stations re-side.
- **ConnectionSession**: new receive cases `StationDestroyed.Fmid`,
  `StationCapture.Fmid`, `DevCompleted.Fmid` → `world.Apply(...)` (the P264
  hub rule: a per-type Apply alone is dead code without the case in the
  `Apply(IFedMessage)` switch).
- **Pure audio**: `TurnThrusterSound` (control→turn level, native formula),
  `StationAmbientSoundCurve` (distance→volume), `StationSoundPlanner`
  (enter/continue/leave→Start/Update/Stop, mirroring the P237 warp planner).
- **SoundManager**: maneuvering-thruster `adjust` loop, hull exterior `shipout`
  loop, per-station exterior `shipout` ambient loops, the four station event
  one-shots, and the dev-completion chime; graceful degradation preserved.
- **Game1**: drives the turn/exterior sounds from the local ship's real
  controls, the station ambient from in-sector stations, and drains the world's
  sound events into the audio manager with `[monogame] audio:` console lines.
- **Content**: `adjust`, `shipout`, `vo_{destroy,capture}{,_enemy}garrison`,
  `vo_sal_researchcomplete` copied into `Content/Sounds/` and registered in
  `Content.mgcb`.

## Live proof (real server + real ConnectionSession, real UDP)

Evidence: `.verify_p267/live_proof.log`. A real in-process `Allegiance.Server`
(XXV_simple, PCore001) + a real `ConnectionSession` client reached Started and
flew, then each wired category fired at its real trigger:

1. **HULL/AMBIENT** (turn): commanding the local ship a real yaw turn reached
   the client on the `SINGLE_SHIP_UPDATE` (received yaw 0.598) →
   `TurnThrusterSound` level = 1.000, audible=true (native
   `m_fTurnSoundLevel>0.01`, `trekigc.cpp:1316`).
2. **TECH CHIME**: the server's real `BroadcastDevCompleted` (the method
   `CompleteDevelopment` calls on a real completion) emitted `DEV_COMPLETED(8)`
   devId=1 → the client recorded `DevCompleted devId=1`
   (native `appmsg.cpp:3326`).
3. **STATION-EVENT**: the real `ServerHost.KillStationEvent` of the enemy
   station 1052 emitted `STATION_DESTROYED(59)` whole-mission → the client
   recorded `StationEnemyDestroyed stationId=1052`
   (native `appmsg.cpp:548-551`).

`RESULT: PASS — all three categories fired at their real triggers`.

## Out of scope (named follow-up)

`DataPartTypeIGC.cs:87-114` weapon/shield/afterburner `Activate`/`Deactivate`/
`Engage`/`Disengage` sounds are **not** wired here — they need a non-redundancy
check against this port's existing weapon-fire/afterburner audio first (a real
double-sound risk), which is the designated follow-up task.

## Build / test

- Whole-solution build: **0 warnings, 0 errors**.
- New tests (isolated, green): 14 MonoGame (`TurnThrusterSound` 5,
  `StationAmbientSoundCurve` 4, `StationSoundPlanner` 5) + 6 ClientCore
  (`ClientWorldSoundEventTests`).
- Full suite: run 2× — see below; only the documented `LiveTurretGunner`
  timing flake is expected.
