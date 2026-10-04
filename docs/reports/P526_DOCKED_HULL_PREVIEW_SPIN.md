# P526 — Docked hull preview: native loadout spin (1 rad/s Y-spin + fixed π/8 X tilt)

Landing date: 2026-10-04 · Branch: `master` (committed, **not pushed**) ·
Task: `docs/prompts/p526-docked-hull-preview-native-spin-prompt.md`

## Summary

P519 built the loadout screen's rotating hull preview but took its **motion**
from the wrong native code: `DockedHullOrbit` advanced pitch/yaw/roll at
`cmExternalOrbit`'s rates (0.35/0.20/0.50 rad/s, WinTrek.cpp:7186-7191), which
is the **in-flight external-orbit camera**, and `Game1.Rendering.cs` composed
`RotX(pitch)·RotY(yaw)·RotZ(roll)` so the ship **tumbled on all three axes**.

P526 replaces that with **native's actual loadout-screen motion**: a level spin
about the ship's UP (Y) at **1 rad/s** plus a **fixed π/8 (22.5°) tilt about X**
applied after the spin (`LoadoutImpl::CreateGeoPane`, WinTrek/load.cpp:877-887).
It also drops the port's 0.12 Y eye offset so the eye sits at `(0, 0, 2.4)`
exactly like native (load.cpp:927-930). The three accumulating
`_hullPreviewPitch/Yaw/Roll` fields collapse to a single `_hullPreviewSpin`.

## Native reference (WinTrek/load.cpp)

`LoadoutImpl::CreateGeoPane` (load.cpp:877-887) builds the preview geo as

```cpp
new TransformGeo(
    new TransformGeo(
        m_pwrapGeo,
        new AnimateRotateTransform(
            new VectorValue(Vector(0, 1, 0)),            // spin axis = UP (Y)
            NumberTransform::Multiply(m_ptime, new Number(1.0))  // angle = time × 1.0 rad/s
        )
    ),
    new RotateTransform(Vector(1, 0, 0), pi/8)           // constant π/8 tilt about X
)
```

- **Spin** — inner `AnimateRotateTransform(Vector(0,1,0), Multiply(time,1.0))`
  is a plain Y-axis rotation at `angle = time × 1.0` = **1 rad/s**
  (`AnimateRotateTransform::Evaluate`, engine/transform.cpp:189-197 —
  `SetRotate(axis, angle)`).
- **Tilt** — outer `RotateTransform(Vector(1,0,0), pi/8)` is a **constant**
  rotation of π/8 about X (load.cpp:886-887).
- **Camera** — `SetPosition(Vector(0, 0, 2.4f * radius))` (load.cpp:927-930),
  **no Y offset**. (The port's old eye at `(0, 0.12, 2.4)` added a small Y
  native doesn't have.)

### Composition order and tilt sign (reasoning recorded)

Native nests `TransformGeo(TransformGeo(geo, spinY), tiltX)`: the **spin is
applied first**, the **tilt second**. In XNA's row-vector convention
(`v · M`, leftmost factor applied first) that maps to

```
world = RotY(spin) · RotX(π/8)
```

`DockedHullOrbit.World(spin)` returns exactly
`Matrix.CreateRotationY(spin) * Matrix.CreateRotationX(TiltRadians)`.

**Tilt sign.** The camera sits on +Z (`(0,0,2.4)`), looking toward the origin
down −Z, up = +Y. Applying `RotX(θ)` to the ship's UP pole (0,1,0) under XNA's
row-vector `TransformNormal` yields `(0, cos θ, sin θ)`: for **θ = +π/8** the
pole's Z component is **+sin(π/8) ≈ +0.383 — toward the camera**, so the viewer
sees the ship's **top**. This is exactly native's positive `pi/8`, and is the
natural hangar view, so the sign is settled as **+π/8** from source *and*
confirmed geometrically. (θ = −π/8 would flip the pole to −Z and show the
bottom.)

The port already maps native rotations literally in the same right-handed,
row-vector XNA space — e.g. `NoseAlignmentCorrection = CreateRotationY(-90°)`
(`ShipRenderer.cs:42`) and the P520 per-asset overrides are applied verbatim —
so a native `RotateTransform(Vector(1,0,0), pi/8)` maps directly to
`Matrix.CreateRotationX(+π/8)` with no sign flip.

**Why "pitch and roll never accumulate".** The single advancing quantity is the
Y spin angle; there is no pitch or roll accumulator at all. The unit test
`No_pitch_or_roll_accumulate_constant_tilt_level_spin` proves the consequence:
the local UP pole maps to the **same fixed direction `(0, cos π/8, sin π/8)` for
every spin angle** — the ship spins about one fixed axis (the turntable) at a
constant 22.5° tilt; it cannot rock, roll, or tumble.

## The change

- `Rendering/DockedHullOrbit.cs` — reworked from the 3-axis `cmExternalOrbit`
  rates to the native loadout spin: `SpinRate = 1.0f` (rad/s, load.cpp:883-885),
  `TiltRadians = π/8` (load.cpp:886-887), `Advance(spin, dt)` (advances only the
  Y angle), and `World(spin) = RotY(spin) · RotX(π/8)`.
- `Game1.Rendering.cs` (`DrawDockedHullPreview`) — calls `DockedHullOrbit.World`
  for the world matrix and moves the eye to `(0, 0, EyeDistance(1f))` (no Y
  offset). Doc comment re-cited to load.cpp.
- `Game1.cs:570` — `_hullPreviewPitch/Yaw/Roll` → single `_hullPreviewSpin`,
  doc comment re-cited to load.cpp.
- `Rendering/DockedHullPreviewFraming.cs` — removed `EyeHeightY` (0.12) and
  updated the motion-divergence note (now matched, not divergent).
- `tools/roster-capture/Program.cs` — dropped the 0.12 eye offset from the P525
  `hullpreview` mode and added a new **`spin`** mode for P526's visual proof
  (below).

## Unit tests — `DockedHullOrbitTests.cs`

Failing-first: the new API (`SpinRate`, `TiltRadians`, `Advance(spin,dt)`,
`World(spin)`) and the 1 rad/s native rate are incompatible with the old
three-axis `cmExternalOrbit` code — the old helper had no `World`, no `SpinRate`,
and advanced three angles, so these tests cannot compile/pass against it.

- `Spin_advances_at_native_loadout_rate` — 1 rad/s (load.cpp:883-885).
- `Spin_advances_frame_rate_independently` — 0.5 s ⇒ 0.5 rad.
- `Spin_compounds_to_the_same_total_regardless_of_step_size` — 16×0.0625 s
  compounds to the same total as 1×1.0 s.
- `Tilt_is_constant_over_time` — tilt is π/8 and unchanged by advancing.
- `Composition_is_spin_about_y_then_fixed_tilt_about_x` — `World(spin)` equals
  `RotY(spin)·RotX(π/8)`; the swapped order measurably differs (order matters).
- `No_pitch_or_roll_accumulate_constant_tilt_level_spin` — UP pole pinned to
  `(0, cos π/8, sin π/8)` across a full revolution (fixed axis, constant tilt,
  top shown; no pitch/roll accumulation).
- `Hull_selection_drives_the_preview_hull_id` — kept from P519 unchanged.

## Headless visual proof (`roster-capture spin`)

`tools/roster-capture`'s offscreen path (same as P525) renders one hull through
the **exact** new preview transform and eye at a fixed time series
t = 0, π/2, π, 3π/2 s (⇒ spin 0°/90°/180°/270° at 1 rad/s) and stitches a 2×2
contact sheet. Assets: `docs/reports/P526_DOCKED_HULL_PREVIEW_SPIN_assets/`.

| asset | renderR | normalise scale |
|---|---|---|
| `ic-fighter` | 100.50 | 0.0100 |
| `ic-carrier` | 95.63 | 0.0105 |

**Contact sheet: `P526_DOCKED_HULL_PREVIEW_SPIN_assets/p526_spin_ic-fighter.jpg`**
(shows a level turntable spin about the vertical axis with a constant ~22.5°
lean in all four frames, top deck toward the viewer; nose faces viewer →
left → away → right at 0°/90°/180°/270°). `p526_spin_ic-carrier.jpg` is the
same proof for a large hull (carrier) to show the motion generalises.

Programmatic check: all four cells contain the ship (5.4–12.9k lit pixels each)
and the four frames differ pairwise (390–561 differing pixels at 64×64) — four
distinct spinned orientations, not duplicates.

## Verification

**Whole-solution build** (`/home/domingo/.dotnet/dotnet build`):
`0 Warning(s), 0 Error(s)`.

**Full suite, 3 consecutive runs** (`scripts/run-tests.sh`, P471):

| run | Lobby | MonoGame | ClientCore | Core | failures (all pre-existing live-wire load-flakes, TEST_FLAKES.md) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1225/1225 | 438/438 | 3706/3708 | `Core…LiveTreasurePickupTests.Rescue_ship_…moneychange…` (:157); `Core…LiveStationRescueTests.Lifepod_reaching_…` (:156) |
| 2 | 12/12 | 1225/1225 | 437/438 | 3706/3708 | `ClientCore…ShipDeleteGoneLiveTests.SameSide_WarperLeavesSector_…` (warper family :51); `Core…StationUnderAttackAlertLiveTests.Second_hit_within_throttle…` (family :28/:375); `Core…CargoBayReloadTests.Reload_fuel_pack_…` (:54) |
| 3 | 12/12 | 1225/1225 | 438/438 | 3705/3708 | `Core…SideEliminationLiveTests.Eliminating_side2_…` (:39); `Core…ShipsUpdateLiveTests.Ship_leaving_cluster_…` (family :245); `Core…CargoBayReloadTests.Reload_NA_terminates_…` (:53-54) |

The **P526 MonoGame assembly passed 1225/1225 in all 3 runs**. Every failure is a
pre-existing live-wire **load-flake** in the untouched `Allegiance.ClientCore` /
`Allegiance.Core` assemblies (documented in `docs/TEST_FLAKES.md`, lines cited
above); they rotate member-to-member run-to-run under full-suite socket load and
touch no ship rendering. The P526 change touches only `Allegiance.ClientCore.Game`
(rendering) and the MonoGame test assembly.

## Deliverables

- `Rendering/DockedHullOrbit.cs` — native loadout spin helper (reworked)
- `Game1.Rendering.cs` / `Game1.cs` — preview uses the spin; eye Y offset removed
- `Rendering/DockedHullPreviewFraming.cs` — `EyeHeightY` removed
- `tests/.../DockedHullOrbitTests.cs` — 7 tests (6 new P526 + 1 kept P519)
- `tools/roster-capture/Program.cs` — new `spin` mode (contact sheet)
- `docs/reports/P526_DOCKED_HULL_PREVIEW_SPIN.md` (this report) +
  `docs/reports/P526_DOCKED_HULL_PREVIEW_SPIN_assets/` (2 contact sheets)

Out of scope (unchanged, per brief): P525 framing/clear/depth; the in-flight
`cmExternalOrbit` camera mode; ship models/orientation/textures; native's
loadout backdrop images (`loadoutpanemp` + civ images, load.cpp:851-856 — a
separate visual task).
