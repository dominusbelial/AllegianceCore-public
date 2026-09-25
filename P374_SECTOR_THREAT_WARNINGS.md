# P374 — Sector/Cluster Threat Warning System (sound + starmap visuals + status text)

Native's sector-threat mechanic — the `ClusterWarning` state that drives the
threat **sounds**, the **flashing starmap icons**, the **flashing sector
outline** and the **warning text / status line** — had zero equivalent in this
port. This stage ports the whole feature, both halves (sound AND visual) sharing
one real trigger, exactly as the brief requires.

The brief ruled three things OUT (do not build): a `salBoundryExceeded`/
`salSectorCrowded` visual (sound-only in native), a persistent low-hull flash
(native's only flash is the one-shot `DamageFlashTracker` this port already
ports), and an off-screen danger-direction arrow (doesn't exist in native).
None of those were built.

---

## 1. The real `ClusterWarning` severity/type list, ported

Native defines it in `src/Igc/igc.h:4367-4384` and collapses an `AssetMask` to it
in `src/Igc/common.cpp:2490-2526`. The port reproduces the enum and the
priority-ordered collapse 1:1 in
`src/Allegiance.Core/Sim/ClusterWarnings.cs`:

```
AssetMask bits (igc.h:4350-4364): Fighter 0x01, Miner 0x02, Builder 0x04,
  Station 0x08, EnemyFighter 0x10, EnemyMiner 0x20, EnemyBuilder 0x40,
  EnemyAPC 0x80, EnemyStation 0x100, EnemyCapital 0x200, EnemyCarrier 0x400,
  Carrier 0x800, EnemyTeleport 0x1000, EnemyTeleportShip 0x2000, EnemyBomber 0x4000

ClusterWarning levels (highest priority wins):
 0 NoThreat            8 BomberInCluster
 1 EnemyFighterInCluster  9 EnemyCarrierInCluster
 2 EnemyMinerInCluster   10 CapitalInCluster
 3 EnemyBuilderInCluster 11 TeleportInCluster
 4 CombatInCluster       12 TransportInCluster
 5 MinerThreatened       13 StationThreatened
 6 BuilderThreatened     14 StationTeleportThreat
 7 CarrierThreatened     15 StationCaptureThreat
```

`GetWarning(am, bInvulnerableStations)` follows native's branch order exactly
(APC+capture, teleport, bomber-near-station, ... down to lone enemy fighter).
`GetWarningText(cw)` returns the 16 native strings verbatim ("Enemy fighter
spotted", "Miner at risk", "Station at risk of capture", …).

`bInvulnerableStations`: this port decodes no `misparms.bInvulnerableStations`;
native's shipped default is **false**, so the port passes `false` (stations are
vulnerable → the capture/teleport/bomber threat branches are active). Documented
in the classifier.

**Sound mapping (native `c_vAlertSounds`, trekigc.cpp:344-352, sounddef.mdl:758-769):**

| level | native sound | real wave |
|---|---|---|
| 5 | minerThreatenedSound | `vo_detect_miner` |
| 6 | constructorThreatenedSound | `vo_detect_builder` |
| 7 | carrierThreatenedSound | `vo_sal_carrieratrisk` |
| 8 | bomberDetectedSound | `vo_sal_bombersighted` |
| 9 | carrierDetectedSound | `vo_sal_carrierdetected` |
| 10 | capitalDetectedSound | `vo_sal_capitalsighted` |
| 11 | teleportDetectedSound | `vo_sal_teleportsighted` |
| 12 | transportDetectedSound | `vo_sal_transportsighted` |
| 13/14/15 | stationThreatenedSound | `vo_sal_stationrisk` |
| control | sectorSecuredSound / sectorLostSound | `vo_sal_sectorsecured` / `vo_sal_sectorlost` |

All 11 real `.ogg` waves were copied from the artwork tree
(`/home/domingo/Development/Artwork`) into `Content/Sounds/` and registered in
`Content/Content.mgcb`.

## 2. The real trigger, wired into THIS port's data model

Native computes the per-sector warning **client-side** each frame from the ships
and stations *it* knows about. This port's MonoGame client is a renderer of
server-authoritative state with **no static core** (no hull capability bits) and
**no knowledge of enemy ships in distant sectors** — so it cannot reproduce the
client-side computation. Following the brief's direction to "reuse
Sector.cs/Server.cs, don't build parallel tracking," the trigger is computed
**server-side from data the server already tracks**:

- `Sector.Models` (physical ship presence) → each `Ship`'s `PilotType`
  (builder/miner/carrier/layer) and `DataHullTypeIGC.HasCapability(...)`
  (`HabmBoard/CaptureThreat/ThreatToStation/Fighter/IsRipcordTarget/Lifepod`) →
  the exact `GetAssetMask` branch native uses (igc.h:5361-5405).
- `Sector.StationRuntimes` `.SideId` → `AmStation`/`AmEnemyStation` per side.
- friendly = same side (no alliance concept in this port; the allied leg
  collapses to own-side — the established simplification).

A new `Server.BroadcastSectorThreats()` SimPass step
(`Step("clusterWarnings", ...)`) recomputes each sector's mask **per side with a
seated peer** every tick and sends a new **this-port-only FMID 214**
`ClusterWarningUpdate` (10-byte layout: `sectorId@4, warning@6, secured@8,
lost@9`) to **that side's members only** when their view of the sector changed
— a warning-level change and/or a sector-secured/lost station transition. Missing
per-sector state defaults to no-threat, so a side's first real threat is always
reported; state is pruned when a side's last peer leaves so a re-populated side
re-learns current threats.

The client (`ClientWorld.Apply(ClusterWarningUpdate)`) then reproduces native's
`UpdateClusterWarnings` reactions:
- stores the current level per sector (`ClusterWarningFor`) → starmap flashing
  icons / hover text;
- on the **increase edge** (`cwNew > cwOld && cwNew > NoThreat`) fires the alert
  sound cue + posts the **`"%s in %s"`** status line (trekigc.cpp:349-350);
- on `secured` posts `"%s secured."` + secured cue; on `lost` posts `"%s lost."`
  + lost cue (trekigc.cpp:375-381). Status lines are bare system lines
  (`SidSender=-1`), the same shape P230's lock tips use, so they render in the
  chat panel with no prefix.

Game1 drains the cues into `SoundManager.PlaySectorAlert` (which maps level →
the real wave above, plus secured/lost), and the starmap draws:
- **flashing per-severity threat icons** on the on-flash phase (native blits each
  tier's distinct `*WarningBmp` only while `m_bFlashFrame`, sectormap.cpp:1055-1082)
  — because the exact icon ART is not available to this port, each tier gets a
  distinct colour + primitive shape instead (red triangle ≥ station-threatened,
  magenta diamond ≥ capital, orange disc ≥ bomber, yellow square ≥
  miner/builder-threatened, light-blue cross ≥ combat). Documented simplification.
- **flashing cyan selected-sector outline** on the off-flash phase (native's
  `GetHighlight() && !m_bFlashFrame` cyan tint, sectormap.cpp:1044).
- **warning text surfaced for the selected sector** — this port's keyboard-only
  client has no mouse hover, so the selected sector is the hover-equivalent of
  native's `GetClusterWarningText` on hover (sectormap.cpp:523-534), in addition
  to the posted status line.

The blink cadence: native toggles `m_bFlashFrame` every 2 pane events; the event
rate is unspecified, so the port uses a fixed ~400 ms phase (~2.5 Hz) computed
from wall time in `Game1.DrawStarmap`. Documented.

## 3. Live verification performed

**Automated real multi-client session over the real wire** —
`tests/Allegiance.Core.Tests/ClusterWarningLiveTests.cs` uses the established
live loopback harness (a real booted `Server` on `XXV_simple.igc`, 2 real
TCP-connected `Bot` clients on opposing sides, real sim ticks via
`server.TickSim()`). Scenario — exactly the brief's: **an enemy ship entering a
sector that contains a friendly station**:

1. Boot the server, 2 clients join (one per side, round-robin) and are assigned
   ships.
2. Locate a sector containing a friendly-side station.
3. Reset both clients' `ClusterWarningUpdate` accumulators (dropping any startup
   "secured"/no-threat priming).
4. Relocate the **enemy** ship into the friendly station's sector
   (`Mission.PlaceShip`).
5. Pump real ticks; **assert the FRIENDLY client receives a real
   `ClusterWarningUpdate(214)` for that sector at a level > NoThreat** — a real,
   decoded, causally-after-the-entry warning produced by the real server trigger.

Observed result: **passed**. The friendly client received the decoded
`ClusterWarningUpdate` for the invaded station sector above no-threat the tick
after the enemy entered (in the run-topology it surfaced as `CombatInCluster`,
an enemy fighter now co-located with the friendly station + friendly presence).
This is the real trigger condition in the port's own data model, proven over the
real wire to a real connected client. (In that same topology the enemy side also
correctly computed its own view — both sides' bots shared the sector, so both
legitimately saw the other's ship as an enemy there.)

Also verified by unit tests (all passing):
- `ClusterWarningsTests` (Core) — the full 16-level severity ladder, the
  invulnerable-station collapse, `GetWarningText`, and the `c_vAlertSounds` stem
  mapping.
- `ClusterWarningUpdateWireTests` (Core) — 10-byte codec round-trip + exact bytes.
- `ClientWorldClusterWarningTests` (ClientCore) — per-sector store, increase-edge
  alert + `"%s in %s"`, steady/decrease suppression, `secured`/`lost` lines + cues,
  drain, reset.
- Whole-solution build: **0 warnings, 0 errors**.
- Full test suite run multiple times: only the **pre-existing documented live-wire
  timing flakes** fail (`Live*` wire tests — verified pre-existing by stashing the
  P374 changes and re-running: they fail on clean HEAD too). No P374 test flakes.

### Honest scope note on the graphical pixel/audio check

The flashing-icon rendering, flashing outline, selected-sector warning text and
the real sound playback are all implemented and compile clean, and the data that
drives them is verified by the unit + live-loopback tests above. Confirming the
**pixels** (a screenshot of the flashing icon) and **audibly** hearing each
distinct cue requires running the real graphical MonoGame client (GUI window +
audio device) with a driven threat scenario; that end-to-end GUI run is not
captured in this session. The trigger, the wire, the per-sector state the
renderer consumes, the chat-line surfacing and the sound-cue plumbing are all
exercised for real.

## Files

- `src/Allegiance.Core/Sim/ClusterWarnings.cs` — classifier (new).
- `src/Allegiance.Core/Wire/ClusterWarningUpdate.cs` — FMID 214 codec (new).
- `src/Allegiance.Server/Server.Broadcast.cs` — `BroadcastSectorThreats` +
  SimPass step + per-side state.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` + `ClientClusterWarningCue.cs`
  — consumption, store, cues, status lines.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — dispatch case.
- `src/Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs` — alert/control cues.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — cue drain + starmap wiring.
- `src/Allegiance.ClientCore.MonoGame/Rendering/StarmapRenderer.cs` — flashing
  icons / outline / warning text.
- `Content/Content.mgcb` + 11 `Content/Sounds/*.ogg` — real waves.
- Tests: `ClusterWarningsTests`, `ClusterWarningUpdateWireTests`,
  `ClusterWarningLiveTests` (Core), `ClientWorldClusterWarningTests` (ClientCore).
