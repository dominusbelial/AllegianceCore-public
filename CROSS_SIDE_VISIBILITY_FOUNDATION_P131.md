# P131 — cross-side visibility foundation: per-side detection state + scanner-range + line-of-sight (no wiring)

Date: 2026-09-10
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`, HEAD `95127ee` P130 at start)
Task: the real cross-side visibility/scanning foundation (`SeenBySide`) that unblocks `PROBES_UPDATE`(92),
`ASTEROIDS_UPDATE`(179), `STATIONS_UPDATE`(57) and all of Phase 5 (multi-ship cross-side visibility). This task
builds the three primitives — per-side detection state, the real scanner-range formula, and the real line-of-sight
test — unit-tested in isolation and wired into **nothing**. The four consumers are separate, later tasks.

---

## Bottom line

Three real, self-contained primitives now exist in `src/Allegiance.Core/Sim/`, each ported from the native source
read directly this session and each tested in isolation against real corpus data (real hull/station/probe lengths
and scanner ranges, real asteroid positions):

1. `SideVisibilityState` (`Sim/SideVisibilityState.cs`) — the per-model, per-side visibility state (a
   visible-flag + current-eyed-flag per `SideId`), mirroring the native `ThingSiteImpl::SideVisibility
   m_rgSideVisibility[c_cSidesMax]` but as an idiomatic dictionary. **State transition only** — no
   `ShowObject`/`HideObject`, no territory refresh, no broadcast.
2. `VisibilityMath.InScannerRange(...)` (`Sim/VisibilityMath.cs`) — the real
   `IscannerIGC::InScannerRange` detection formula for ships/stations/probes (every ship, station and probe is an
   unconditional scanner for its own side — confirmed at `clusterIGC.h:126,148,219`, `shipIGC.cpp:3700`,
   `stationIGC.h:275`; all three carry `c_mtScanner`, `probeigc.cpp:115`). Global-attribute divisors simplified to
   `1.0` per the established precedent (below).
3. `VisibilityMath.LineOfSightExist(...)` — the real asteroid-occlusion cone test
   (`Igc/common.cpp:2324-2398`), ported faithfully with no simplification.

Suite 2645 → **2659** (+14), 3 consecutive clean full-suite runs (no flakes fired this time).

---

## 1. Why this is the foundation (and why it is deliberately small)

Four roadmap items block on a real `SeenBySide`: `PROBES_UPDATE`(92), `ASTEROIDS_UPDATE`(179), `STATIONS_UPDATE`(57),
and all of Phase 5 (whose own roadmap entry already names cross-side visibility as its real prerequisite). Each is a
**separate, later task**. This task was explicitly not scoped as one giant task — that repeats the mistake the arc
already corrected for `RELAUNCH_SHIP` (a P114 foundation had to land before P115/P116/P117 could split safely).
Everything here is the smallest correct foundation those four can build on: the state container, the range formula,
and the occlusion test — each proven in isolation first.

---

## 2. Per-side detection state — `SideVisibilityState`

Native shape read in full this session: `FedSrv/FedSrv.CPP:10692-11172` (`ThingSiteImpl`) declares
`SideVisibility m_rgSideVisibility[c_cSidesMax]` at `FedSrv.CPP:11172`, each element holding a visible-flag and a
current-eyed-flag. The model accessors `SeenBySide(side)`/`GetCurrentEye(side)` route straight into them
(`Igc/modelIGC.h:110-132`).

Port decision: a `Dictionary<short, ...>` keyed by `SideId` instead of a fixed `c_cSidesMax` array, because this port
has no side-count bound — missions carry an unbounded, data-driven side set (`Model/Side.cs`). Two parallel
dictionaries (visible / current-eye) hold one `bool` per side.

`SetSideVisibility(sideId, bool)` reproduces the native flag transitions of `FedSrv.CPP:11153-11171` exactly:
assign the visible flag the new value and sync the current-eyed flag to the same value in the same call
(native `FedSrv.CPP:11168`). It fires **none** of the native side effects:

- `ShowObject`/`HideObject` (`FedSrv.CPP:11163-11167`) — the wire broadcasts; that is the wiring tasks' job, not
  this state container's.
- the station-territory refresh (`FedSrv.CPP:11170`) — this port has no territory system.

Two sides are fully independent (separate dictionary keys); setting one never touches another. Tests confirm set-true,
clear-after-true, untouched-reads-false, and two-sides-independence.

---

## 3. `InScannerRange` — the real detection formula, divisors to 1.0

Native read in full: `Igc/stationIGC.h:308-328` (station scanner; the structurally identical formula at
`Igc/shipIGC.h:341-364` and `Igc/probeigc.h:163-180`):

```
r = GetRadius() + pModel->GetRadius()
    + (scannerType.GetScannerRange() * pModel->GetSignature())          // m, stationIGC.h:315
        / pside->GetGlobalAttributeSet().GetAttribute(c_gaSignature)     // target side divisor, :317-319
return sameCluster && distSq <= r*r && LineOfSightExist(cluster, this, pModel);
```

**Confirmation of every simplification, plainly:**

- **Signature divisor → `1.0`.** `GetGlobalAttributeSet().GetAttribute(c_gaSignature)` is the same
  `GlobalAttributeSet`/tech-tree-adjacent infrastructure this port has now confirmed absent **four separate times**
  (P117, P119, P121, P124). It is simplified to `1.0` (no attenuation), exactly as those tasks did — no new
  global-attribute infrastructure is built for one divisor.
- **Probe scan-range factor → `1.0`.** The probe's formula additionally multiplies by
  `GetSide()->GetGlobalAttributeSet().GetAttribute(c_gaScanRange)` (`probeigc.h:165`) — the same absent
  infrastructure, also `1.0`.
- **Flag-carrier branch omitted (finding below).** The `GetFlag() == NA` always-visible early-return is dead in this
  port and is not reproduced.
- **Same-cluster** compared via the port's cluster ids (`Sector.Id` for ships/probes, `ClusterId` for stations) —
  the analogue of native `pModel->GetCluster() == GetCluster()` pointer compare (`stationIGC.h:311-312`).
- **Radii** use each type's real named value (`Station.Radius` / `ProbeType.Radius`) and the ship sphere
  `0.5 × HullType.Length` convention already established by P113/P114/P116.

**Flag-carrier finding (Phase 6 not built).** Native `GetFlag()` (`shipIGC.h:289`) returns `m_sidFlag`, a
capture-the-flag ownership id; `InScannerRange` treats any flag-carrier as always visible. This port has **no flag
concept anywhere** — no model exposes a `Flag` field, and Phase 6 capture-the-flag is not built. So every model is
effectively `NA` and only the normal branch could ever run. Building the flag branch would be a dead branch, so it is
omitted outright and documented here rather than guessed at.

Also confirmed this session: **probes really do scan** — `CclusterIGC` registers every new probe as a scanner
(`Igc/clusterIGC.h:219`), and a probe's `InScannerRange` uses the identical formula (`probeigc.h:163-180`), so the
three scanner-equivalent types share one implementation here. Tests exercise a real ship scanner, a real station
scanner (type 2, Radius 302.5 / ScannerRange 2400) and a real probe scanner (`utl23`, Radius 6 / ScannerRange 500).

---

## 4. `LineOfSightExist` — the real asteroid-occlusion cone test, ported faithfully

Native read in full: `Igc/common.cpp:2324-2398`. It is a **genuine but self-contained geometric test** — occlusion
against asteroids **only** (it iterates `pcluster->GetAsteroids()` and nothing else), a cone/angle-subtended check
between the eye and the target, testing whether any closer asteroid's silhouette blocks the line. Not full raycasting
against arbitrary geometry; a bounded, implementable algorithm given this port's existing asteroid position/radius
data (P102). Ported **faithfully with no simplification** — it needs none, it is already self-contained.

Algorithm (as ported, `VisibilityMath.LineOfSightExist`):
- normalise the eye→target ray; `visibleAngle = asin(targetRadius / dist)` (target's angular extent at the eye);
- for each asteroid nearer than the target and in front of the eye (positive projection onto the ray):
  - `covered = asin((asteroidRadius * 0.5) / distToObstacle)` — the native's "dense core" half-radius scale
    (`common.cpp:2368`);
  - `maxSep = acos(cosSeparationAngle) + visibleAngle`;
  - if `maxSep < covered` (strict `<`, native `common.cpp:2377`) the target is occluded → `false`;
- otherwise → `true`.

Tests cover: clear line (true), blocked by the real asteroid (false, hand-derived geometry below), obstacle behind
the eye (true), obstacle beyond the target (true), and obstacle far off-axis (true).

---

## 5. Test evidence (all against real corpus data)

Real bodies decoded from the `.igc` corpus via the shared `PayloadDecodeTests` helpers (`PayloadDecodeTests.cs`):
scanner `faohshcar` (Length 300 → ship radius 150, ScannerRange 3600, Sig 1.5), target `dreghpod` (Length 4 → radius
2, Sig 0.5), station type 2 (EventCore, Radius 302.5, ScannerRange 2400), probe `utl23` (Radius 6, ScannerRange 500),
and the real occluder asteroid id 3200 (`m2xspiderw.igc`, radius 644 at `[3758.2, 1133.8, 3165.1]`).

Hand-derived occlusion (the way P114's `InGarage` tests hand-derived half-spaces): scanner and target placed one
radius (±644) either side of the asteroid centre on the x-axis through it, so the asteroid sits dead-centre on the
eye→target ray. Eye→obstacle = 644, dense-core half radius = 322 → covered angle `asin(322/644) = 30°`; target
visible angle `asin(2/1288) ≈ 0.089°`; separation 0 (collinear) → `maxSep ≈ 0.089° ≪ 30°` → decisively occluded.
Detection radius 1952 (150 + 2 + 3600·0.5) ≥ 1288, so the target **is in range** — the false result is purely
occlusion, asserted two ways (`InDetectRange` true, `InScannerRange` false).

---

## 6. Verification

- No VM needed; pure unit tests, no live `Bot`/server, matching P114's isolated-predicate style for `Ship.InGarage`.
- Full suite: 2645 → **2659** (+14). Three consecutive clean runs (`2659` passed each) after confirming the P130
  baseline; no `LiveChatSectorWireTests`/`LiveCombatWireTests` timing flakes fired this time.
- Committed only after the 3-clean bar (below).

## 7. Scope respected

Deliberately **not** done here: wiring into `PROBES_UPDATE`/`ASTEROIDS_UPDATE`/`STATIONS_UPDATE`/Phase 5; the
per-cluster-per-side periodic detection sweep that actually calls `InScannerRange`/`LineOfSightExist`/
`SetSideVisibility` together on a schedule; `ShowObject`/`HideObject` effects and player-score spotting; real
global-attribute/tech-tree infrastructure; any change to P103–P130 code (read for context only).

---

## 8. Concrete next task

**The periodic per-cluster-per-side detection sweep** — the thing that actually drives the three primitives together
on a schedule: for each cluster, for each side, for each side-carrying model, evaluate
`InScannerRange(scanner, target)` (which already embeds `LineOfSightExist`), feed the result into that model's
`SideVisibilityState.SetSideVisibility(sideId, visible)`, and detect changed flags so the (still-later) wiring can
fire `ShowObject`/`HideObject`. It will map each model's geometry into `VisibilityMath.SightBody` (ship sphere
`0.5×Length`, `Station.Radius`, `ProbeType.Radius`) and read each cluster's asteroids as the occluder set
(`Sector.Asteroids`, P102 data). Once that sweep exists, `PROBES_UPDATE`(92), `ASTEROIDS_UPDATE`(179),
`STATIONS_UPDATE`(57) and Phase 5 become real, independently-tractable tasks for the first time — each consuming the
same foundation proven here.
