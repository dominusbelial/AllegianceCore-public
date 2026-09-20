# Probe Visibility Exception + Real Probe Sounds (P260)

## What this is

The **second** of the two tasks answering Domingo's question about original map
visibility, built on P259's `Side.CanSeeCluster` baseline. It wires the deployed
probe (`Sim/Probe.cs`, broadcast by P224's `DeployProbe`) as the real visibility
exception it is, plus its real sound cues:

- `Mission.CanSideSeeCluster` now counts a **live probe owned by the side** as an
  owned asset (alongside ships/stations): a probe physically occupying a cluster
  reveals it (the presence / distance-zero case), and the reveal lasts **exactly
  as long as the probe survives** — clearing the instant the probe is destroyed
  (`Probe.ReceiveDamage`) or its lifespan expires (`Probe.OnUpdate`), because the
  live query reads `Sector.Models` and a dead/expired probe has left it.
- Client-side probe audio in the MonoGame client, mirroring the P236 missile
  precedent:
  - the **deploy one-shot** on the local ship's own drop,
  - the **ambient scanning loop** while a probe owned by the player's own side is
    active in their current sector,
  - both **stop** when the probe is destroyed/expired (no more scanning).

## The brief's open question, settled from the real tree

The brief asked: does "side owns a probe in this cluster" reveal the **entire
cluster unconditionally**, or is it **limited to `ScannerRange`** from the probe's
own deployed position?

**Answer (verified, cited): ScannerRange-limited — not a blanket cluster-wide
uncover.**

Like P259, the cited `CFSSide::CanSeeCluster` (with its "owns a live probe"
branch) is from the original 2003 MS source, not the on-disk FreeAllegiance tree
(`src/Igc/sideigc.cpp` is 532 lines, no such symbol). The real modern-FA probe
mechanic is `CprobeIGC::InScannerRange` (`src/Igc/probeigc.h:163-191`):

```
bool InScannerRange(ImodelIGC* pModel) const {
    if (pModel->GetCluster() == pcluster) {              // confined to ITS OWN cluster
        if (pModel->GetFlag() == NA) {
            float m = m_probeType->GetScannerRange()      // the real ScannerRange
                    * pModel->GetSignature()
                    * GetSide()->GetGlobalAttributeSet().GetAttribute(c_gaScanRange);
            ...
            m /= pside->GetGlobalAttributeSet().GetAttribute(c_gaSignature);
            float r = GetRadius() + pModel->GetRadius() + m;
            rc = ((GetPosition() - pModel->GetPosition()).LengthSquared() <= r*r)
                 && LineOfSightExist(pcluster, this, pModel);
        } else rc = true;
    } else rc = false;
    return rc;
}
```

So a probe is a **per-object radar, gated to its own cluster** (the
`GetCluster() == pcluster` gate) and **bounded by its real `ScannerRange`** from
its own deployed position (signature × `c_gaScanRange` over the target's
`c_gaSignature`, plus line-of-sight). `DataProbeTypeIGC.ScannerRange` (byte offset
464) is that real field. There is **no** "one probe anywhere reveals everything"
mechanic — that reading of "uncover everything" is not what the on-disk native
does.

At the port's cluster abstraction, that faithfully manifests as the **presence
case**: a live owned probe physically sitting in a cluster reveals it (distance
zero), riding the same strategic-range proxy every owned asset shares — but a
probe never scans across clusters (the `InScannerRange` gate), so its reveal is
anchored to the cluster it occupies, and lasts exactly as long as the probe
lives. (For the avoidance of doubt, the probe also rides the P259
within-`VisibilityRange` neighbor proxy like ships/stations; the cluster it
occupies is always revealed via presence, which is the test scenario.)

## The real sounds, cited

- **Deploy / launch sound**: native plays the probe's deploy cue through the
  expendable-launcher dispatch —
  `clintlib.cpp:3245-3257`: `case OT_probeType: PlaySoundEffect(deployProbeSound, pShip)`
  (gated on the **local** ship, `pShip == m_ship`). `deployProbeSound =
  GainSound(SFXGain, ImportWave("dropobject"))` (`sounddef.mdl:369`) — a generic
  expendable-drop sound **reused for chaff, mine and probe alike** (lines 367-369).
  Native deliberately gives the probe **no** distinct launch sound; there is no
  probe-specific launch field in `DataProbeTypeIGC` (only `scannerRange`,
  `dtimeBurst`, `dispersion`, `accuracy`, `ammo`, `projectileTypeID`,
  `ambientSound`, `dtRipcord` — `igc.h:2003-2017`). Per the P236 single-real-default
  convention (the `dropobject` wave is absent from this port's content), the deploy
  cue maps to the port's existing single generic expendable-launch asset
  (`lrgslideout`) — faithfully mirroring that native reuses one generic deploy
  sound for all expendables.
- **Ambient scanning loop**: native's `DataProbeTypeIGC.ambientSound` (parsed at
  `missionigc.cpp:968`, exposed via `CprobeIGC::GetAmbientSound()` → probeigc.h:261-264)
  is a per-type SoundID unreachable client-side (no loaded core), so the scanning
  loop uses the documented single-real-default `erbeam` asset. The port's client
  captures the probe's real owner (`DataProbeExport.SideId`) and deployer
  (`DataProbeExport.ShipId`) from the wire to attribute the ambient to the
  player's own side and the deploy to the local ship.

## What changed

Server / sim (`Allegiance.Core`):
- `Mission.CanSideSeeCluster` — added the probe branch (a live owned
  non-dead `Probe` in a within-range sector counts as an owned asset), plus the
  sourcing remark. No P259 logic was rebuilt.

Client state (`Allegiance.ClientCore`):
- `ProbeEntity` — new live-probe world entity (ProbeId, SideId, LauncherShipId,
  ClusterId, Position).
- `ClientWorld` — new `Probes` table (upsert on `ExportProbe`, remove on
  `ProbeDestroyed`) and new `MySideId`, captured from the own-ship dummy EXPORT
  (the only wire source of the player's own side).
- `ConnectionSession` — routes `ExportProbe` (OT_probe in the EXPORT switch) and
  `ProbeDestroyed(85)` into the world.

Client audio (`Allegiance.ClientCore.MonoGame`):
- `ProbeAmbientSoundCurve` (pure distance→volume), `ProbeSoundPlanner` (pure
  deploy/ambient-start/update/stop transition tracker — the P236-shape minus the
  missile explosion).
- `SoundManager` — `_probeDeploy` (lrgslideout), `_probeAmbient` (erbeam),
  `PlayProbeDeploy` / `Start|Update|StopProbeAmbient`.
- `Game1.UpdateProbeSounds()` — drives the planner from the live in-sector probe
  set (own-side / own-deploy attribution) and replays commands, logging the
  `[monogame] audio: probe …` console surface.

## Verification

### Probe visibility exception (real live loopback, must-fail proven)
`tests/Allegiance.Core.Tests/ProbeClusterVisibilityTests.cs` (5 tests) drives the
real `Mission.Assemble`/`PlaceShip`/`DeployProbe` path over real corpus sectors:
a cluster outside `VisibilityRange` becomes visible once a probe is deployed there
and the scout departs; it stops being visible when the probe is **destroyed**
(`ReceiveDamage`) or **expires**; the reveal is scoped to the owning side; the
periodic `RefreshSideVisibility` adopts it. **Must-fail proven**: with the probe
branch commented out all 5 go red against the pre-exception behavior, then green
with it.

### Live audio proof (required) — real server + real MonoGame client
A disposable out-of-repo harness (`/tmp/p260_harness`) booted a **real**
`Allegiance.Server` in-process, spawned the **real** MonoGame GUI client as a
subprocess (direct-connect on display :0), let the mission start, then deployed a
real probe on the client's ship via `Ship.DeployProbe` (broadcasting a real
`EXPORT(26)/OT_probe`) and later destroyed it via `ReceiveDamage`. The client's
own console (redirected to a log) shows all three cues at the right moments:

```
[monogame] audio: probe deploy          ← on the local ship's drop
[monogame] audio: probe ambient start   ← own-side probe active in the client's sector
[monogame] audio: probe ambient stop    ← after PROBE_DESTROYED(85) when the probe died
```

(The server independently logged `[probes] side 0 sees 1 probe(s) …` while alive.)

### Build / test status
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- `Core.Tests`: **3085** total (was 3080) — **3084/3085**; the sole failure is the
  documented pre-existing `LiveTurretGunnerBindingTests` timing flake. Run twice.
- `Allegiance.ClientCore.MonoGame.Tests`: **318** (incl. 7 new
  `ProbeSoundPlannerTests`), 0 failures.
- `Allegiance.ClientCore.Tests`: **89** (incl. 4 new `ClientWorldProbeTests`),
  0 failures.

## Out of scope this task / natural follow-up

As the brief scoped, no UI/client-visible "which clusters can my side see" surface
was built — no starmap / fog-of-war overlay. The natural follow-up (named) is to
consume `Side.VisibleClusters`/`CanSeeCluster` (P259 + this probe exception) in a
starmap overlay so the commander sees which sectors their side has under
surveillance — cf. `STARMAP_OVERVIEW_P249.md`'s deferred per-side-coloring note.

Not pushed — Domingo pushes from `master`.
