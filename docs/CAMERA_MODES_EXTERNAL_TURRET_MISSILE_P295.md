# CAMERA_MODES_EXTERNAL_TURRET_AND_MISSILE — P295

Landing: P-series master, **not pushed** (Domingo pushes). Branch `master`,
committed directly (no side branch), working tree clean.

## Scope of this stage

Added two of the fourteen native camera modes that this port was missing
(its `CameraMode.cs` had only `Chase`/`Cockpit`; native's real `CameraMode`
enum is at `WinTrek/Treki.h:108-126`):

- **`cmExternalMissile`** — watch a just-fired missile in flight.
- **`cmExternalTurret`** — a dedicated view of the ship whose turret you have
  boarded as a gunner.

The other ten native modes are explicitly out of scope (see "Remaining follow-up").

---

## Native trigger research (cited, not guessed)

### cmExternalMissile — dedicated key toggle
- TrekKey constant: `TK_ViewMissile = 32` (`src/Igc/igc.h:255`).
- Activation: `case TK_ViewMissile: SetViewToCombatMode((GetCameraMode() !=
  cmExternalMissile) ? cmExternalMissile : cmCockpit)` (`WinTrek.cpp:9820-9824`)
  — a **dedicated key toggle**, not an automatic-on-fire.
- Tracking: in the `cmExternalMissile` case the camera each frame follows
  `pshipSource->GetLastMissileFired()` via `TrackCamera`
  (`WinTrek.cpp:7237-7268`), and when no missile is flying it falls back to the
  external chase view (`WinTrek.cpp:7259-7268`; the `ofInChase` overlay flag is
  also keyed off `m_pmissileLast == 0`, `WinTrek.cpp:6842`). `TrackCamera`
  itself is `WinTrek.cpp:6848-6878`.

### cmExternalTurret — declared but dead in native
- TrekKey constant: `TK_ViewTurret = 35` (`igc.h:258`).
- **The native handler is commented out**: `WinTrek.cpp:9856-9863` with the
  annotation *"This camera mode is not actually handled by the viewing code"*,
  and there is **no `case cmExternalTurret`** in the camera-update switch
  (`WinTrek.cpp:7097+`) — it falls through to the `default`/external branch.
- The real turret handling in native lives in the **internal (cockpit)** block,
  which seats the eye at the turret weapon position when the pilot is a boarded
  gunner (`pshipSource != GetShip()`, `GetParentShip()!=NULL`, `turretID != NA`;
  `WinTrek.cpp:7019-7058`).

**Consequence for the port (labelled honestly):** native has no functioning
third-person *external* turret camera — the external-turret view is a port-side
design decision, documented as such in `CameraMode.ExternalTurret`'s remarks.
Per the task instruction, the port's `cmExternalTurret` is an external chase
view centred on the **carrier** — the ship the pilot is gunning on — reusing the
port's existing turret-boarding state (`ShipEntity.ParentId` ≠ NA), the same
state P109/P273 use to identify a boarded gunner.

---

## What landed

1. **`Rendering/CameraMode.cs`** — added `ExternalTurret` and `ExternalMissile`
   with the enum's doc-comment discipline (native citations in each value's
   remarks; remaining-ten follow-up named in the enum summary).

2. **`Rendering/CameraCycler.cs`** (new, pure & unit-testable) — the `V`-key
   cycle. Native uses one key per view (`TK_ViewChase/TK_ViewMissile/
   TK_ViewTurret`…), but this port has always used a single `V` key and the task
   brief explicitly blesses "a cycle through available modes". Order:
   `Cockpit → Chase → ExternalTurret → ExternalMissile`, with the
   **external-turret view offered only while actually gunning** (skipped
   otherwise); a stale mode (pilot left the turret in the turret view) restarts
   at the first available rather than hanging.

3. **`Rendering/MissileCamera.cs`** (new, pure) — the `TrackCamera` port
   (`WinTrek.cpp:6848-6878`): eye rooted on the missile, offset
   `position1 − axis*2L + side*0.25L`, turned to face the other reference point,
   camera-relative rendering (the P207 convention shared by Chase/Cockpit).
   Native frames the missile + its target (or, in the no-target fallback, the
   ship it was fired from); this port's client wire has no missile target, so
   Game1 uses the missile + the local ship, matching native's fallback branch.
   Guarded against coincident points (native asserts the two positions differ).

4. **`Game1.cs`**
   - `V` now calls `CameraCycler.Next(_cameraMode, IsTurretGunner())`.
   - The Draw camera-selection switch handles all four modes:
     * `ExternalTurret` → `TryGetGunnerCarrierPose` (reads `ParentId`, then the
       carrier's pose from `_poses`) → real `ChaseCamera` anchored on the
       carrier; falls back to the pilot's own chase view if not gunning / carrier
       unseen.
     * `ExternalMissile` → `TryGetLatestMissilePos` (the newest in-sector live
       missile, a client-side stand-in for native's `GetLastMissileFired()`,
       since the wire has no shooter/target) → real `MissileCamera`; falls back
       to chase when no missile is flying (faithful to native's fallback).
   - Target-picking (`CollectTargetCandidates`) keeps the non-cockpit → chase
     behaviour (POC approximation, unchanged).

5. **`Rendering/KeyBindingTable.cs`** — the `V` CAMERA/VIEW entry updated to the
   cycle (`cockpit -> chase -> turret (while gunning) -> missile-cam`), kept
   pure ASCII per that table's sprite-font constraint.

---

## Key/input choice & why
Kept a single `V` key, extended from a 2-way toggle to a cycle through the modes
available to the pilot's state. Native's one-key-per-view is acknowledged but a
cycle is the brief-sanctioned minimal slice and fits this port's existing input
idiom; the turret view is conditionally offered (it is meaningless when not
gunning) and the missile view always is (it degrades to chase, like native).

## Live proof (real wire + real cameras, offscreen PNGs)
Out-of-repo harness `/tmp/p295verify` (real in-process `Allegiance.Server`,
teams:2, real `ConnectionSession` clients). Screenshots in `.verify_p295/*.png`
(repo tree, untracked):

- **External-missile (real wire):** the boarder client pressed the missile key
  (`SendFireMissile` → real `FM_CS_FIRE_MISSILE(70)`); the server created a real
  homing missile (launcher 3→2) and the **client's `ClientWorld.Missiles`**
  hydrated it. Five client-observed flight positions sampled across the homing
  run show it closing (dist-to-target **429.5 → 359 → 283 → 212 → 138.6**) while
  the real `MissileCamera` eye tracked +X with the missile
  (eye.x −260 → +234). PNGs `p295_missile_flight0..4.png`; the terminal frame
  reads unambiguously as a missile-cam shot with the target looming.
- **External-turret (real wire):** the same boarder client **really boarded** a
  carrier peer's turret (`SendBoardShip` → `FM_C_BOARD_SHIP(103)`; server bound
  it as gunner at mount 2). The client's `ParentId` became `8096 == carrierId
  8096` over the real `SHIP_STATUS` stream. The real `ChaseCamera` (what Game1's
  `ExternalTurret` case uses) anchored on the carrier renders the carrier
  **centred/dominant in both** `p295_turret_posA/B.png` while a fixed-world
  fighter landmark shifts edge position — proving the camera follows the carrier
  you're gunning on, not the gunner or the decoy (frames also differ in bytes,
  proving the eye physically relocated with the carrier).

## Build / test status
- Whole solution (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- New MonoGame tests (8, all discriminating): `MissileCameraTests` (framing both
  points in frustum; eye tracks the missile as it flies; coincident + vertical-axis
  guards) and `CameraCyclerTests` (turret skipped when not gunning; full cycle;
  stale-mode recovery). **Must-fail verified** — temporarily reverting the cycle
  gating reddens `NotGunning_SkipsExternalTurret` (got `ExternalTurret`), and
  freezing the missile-cam eye reddens `Eye_TracksTheMissile`; both reverted.
- `Allegiance.ClientCore.MonoGame.Tests`: **519 passed / 0 failed** (was 511,
  +8).
- `Allegiance.ClientCore.Tests`: **154 passed / 0 failed**.
- `Allegiance.Core.Tests`: **3173 total**, run 2×. No camera change touches Core
  (this task is entirely in the MonoGame client). Run 1: 4 failures — all
  live-network/environmental (LiveTurretGunner documented flake; 3 station/
  asteroid live-visibility tests). Run 2: 3 failures — `LiveTurretGunnerBindingTests`
  (documented), `LiveChatSectorWireTests` (documented), `StationsUpdateLiveTests.
  Damaged_station_fraction` (**confirmed environmental**: still fails with these
  changes stashed on the pristine tree). No failure references any P295 symbol.

## Scope boundaries honoured
- No change to the turret-gunner boarding mechanic (P109/P273) or missile
  flight/homing simulation (P226) — both already worked; this task only added the
  missing cameras. Boarding parts-clearing is why the harness fires the missile
  *before* boarding.
- Native reference source (`~/Development/Allegiance`) not modified (read-only).

## Remaining follow-up (named, out of scope here)
The other ten native camera modes remain absent: `cmExternalTarget`,
`cmExternalBase`, `cmExternalOrbit`, `cmExternalStation`, `cmExternalFlyBy`,
`cmExternalFlyByTarget`, `cmExternalOverride`, and the two command views
(`cmExternalCommandView34` / `cmExternalCommandViewTD`). A real, sizable future
task — several involve the port's (still absent) command-view / external-target
systems, not just a camera rig.
