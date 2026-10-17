# P721 — Parity wave 19: simulation SOUNDS, seventh bundle (own-ship thruster / interior / sidethrust loop bank)

## Summary

Ported the **own-ship thruster / interior / turn / sidethrust loop bank** from
native `TrekClient::UpdateSounds` (the interior/exterior bed block,
`trekigc.cpp:1307-1355`). **13 `sounds` rows → implemented** (12 of them as the
per-hull-class loop beds; SIM-0349 reconciled because the P465 per-mount burst
loop already serves it), and **1 marked `n/a` with reason** (SIM-0371 gunner
interior). **Two genuinely new one-shot-bearing loop beds** — the fighter/capital
**sidethrust** loop (`accelsmall`/`accelcapital`, gated on the backward/left/right
thrust buttons, native `PlaySidethrust()`, trekigc.cpp:953-956) and the
**capital** main-thruster/interior/turn beds (`accelcapital`/`lshipint`/
`accelsmall`) — resolve the native per-hull-type loop SoundIDs the previous
waves' fixed small-ship assets left un-differentiated. **No new server event**
(the client derives its own ship's hull class and thrust-button state from
state it already holds) and **no flight/simulation behaviour changes** (guarded
by the P700 regression gate). Two new `.ogg` (`accelcapital`, `lshipint`) enter
the MonoGame content pipeline.

Scorecard, `sounds` area (the denominator drops by SIM-0371's weight as it moves
to `n/a`):

| metric | before | after |
|---|---|---|
| weighted | 327/594 (55.05%) | **362/592 (61.15%)** |
| unweighted | 120/228 (52.63%) | **133/227 (58.59%)** |

## Why these rows

The bundle is one coherent family — **the own-ship ambient/positional loop bank**
in `TrekClient::UpdateSounds` — all rows sharing the same `PlaySoundIf` loop
mechanism and the per-frame sound driver in `Game1.FlightInput.cs`. Native plays
every one of these as a looping `SoundSite` and mutes the internal vs external
volume bus by camera view (`SetInternalVolume/SetExternalVolume`,
trekigc.cpp:1356-1366); the port keeps one loop instance per bed and picks it by
hull class + camera mode. The remaining families the task lists are weapons/
ordnance, station/economy, UI/screens and ambient/positional loops; the previous
waves already ported the one-shot members of the first three, leaving this loop
bank (and the weapon-mount loop block, SIM-0347/0348) as the largest remaining
`partial`/`missing` cluster.

## Implementation

### The loop-bank mapping (new `ShipLoopBeds`)

New pure mapper `src/Allegiance.ClientCore.Game/Audio/ShipLoopBeds.cs` pins each
bed's asset by hull class, exactly native's per-hull-type SoundIDs:

| bed | fighter | capital | native cite |
|---|---|---|---|
| interior | sshipint | lshipint | small/capitalShipInteriorSound, sounddef.mdl:1012/1019 |
| main thruster | accelsmall | accelcapital | small/capitalShipThrustInteriorSound, :1014/1021 |
| turn (maneuvering) | adjust | accelsmall | small/capitalShipTurnInteriorSound, :1016/1023 |
| sidethrust | accelsmall | accelcapital | Sidethrust/CapSidethrust(Interior)Sound, :1344/1346 |
| exterior | shipout | shipout (pitch 0.5) | smallShipExteriorSound, :1013/:1020 |
| afterburner | iafter | iafter | GetAfterburnerSoundID(true/false), trekigc.cpp:1320/1344 |

`SoundManager.Load()` resolves all beds through `ShipLoopBeds`; the tests pin the
exact mapping so a mis-read asset table fails fast.

### New: sidethrust loop (SIM-0376, 0377, 0382, 0383)

Native plays `SidethrustInteriorSound`/`SidethrustExteriorSound`
(accelsmall) for a fighter and `CapSidethrustInteriorSound`/
`CapSidethrustExteriorSound` (accelcapital) for a capital, gated on
`PlaySidethrust()` = any of backward/left/right held (trekigc.cpp:953-956,
1326-1354). Native reuses **one** loop instance (`m_pSideThrustInteriorSound`)
for both the interior and exterior rows — only the hull class changes the asset.
The port adds a single sidethrust bed (`_sideThrustSmall`/`_sideThrustCapital`
selected by hull class), gated in `SoundManager.Update` on a new
`sidethrustActive` input. The per-frame driver computes it from the local
ship's `StateM` (backward|left|right) and hull class (`_hullById[HullId]`
`HasCapability(HabmFighter)`).

### Per-hull beds for the existing thruster/interior/turn loops (SIM-0372..0381)

The main-thruster, interior-hum and turn beds were previously fixed small-ship
assets; they now resolve by hull class via `ShipLoopBeds` (`Update` picks the
engine/interior bed by `capitalHull`; `UpdateTurnThruster` picks the turn bed).
The afterburner bed (`iafter`) and exterior bed (`shipout`) are unchanged
(they are the same asset for both classes). This upgrades the eight `partial`
rows to faithful `implemented`.

### Reconciliation (SIM-0349 burst)

Native's `m_vpBurstSound[mount]` loop (trekigc.cpp:999-1004) is already served
by the P465 per-mount burst loop (`SoundManager.StartWeaponBurst`/`StopWeaponBurst`,
driven per-frame at `Game1.FlightInput.cs:2985-2992`); the matrix was stale
`partial` and is corrected.

### n/a with reason (SIM-0371 gunner interior)

Native plays the hardpoint `interiorSound` when the player is seated as a gunner
in a parent ship (`GetGunnerInteriorSoundID`, `GetParentShip()`/`GetTurretID()`,
trekigc.cpp:1096-1111). This renderer's flight model always pilots the local
ship — it has no passenger/gunner-attach-to-parent state — so the row is `n/a`
with the reason recorded (like P719's SIM-0395/0413).

## Failing-first

The genuinely-new pieces are failing-first: `ShipLoopBeds` did not exist before
P721, so `P721ShipLoopBedsTests` cannot compile on the pre-change tree (CS0246,
the same mechanism P711/P716/P717 documented); and `accelcapital.ogg` +
`lshipint.ogg` were absent, so `P721ThrusterLoopSoundTests` (asset-bundling)
fails before the waves/mgcb are added. After the change all pass. The 12
per-hull loop-bed rows are MonoGame-`Game1`-driven, so their evidence is the
pure `ShipLoopBeds` mapping test + the asset-bundling test + the wiring audit
(the same `verified` evidence class the P716/P719 Game1-command rows use);
SIM-0349 is a wiring-audit reconciliation of existing P465 code.

## Tests

`tests/.../P721ShipLoopBedsTests.cs` (new, 10 cases): pins the fighter/capital
asset for every bed (main-thruster, interior, turn, sidethrust) plus exterior=
`shipout` and afterburner=`iafter`, cited to sounddef.mdl.

`tests/.../P721ThrusterLoopSoundTests.cs` (new): `accelcapital.ogg` and
`lshipint.ogg` present in `Content/Sounds` and registered in `Content.mgcb`.

New tests run 3× clean (11/11 each). The suite's own `P700ShowcaseLiveTests`
regression gate ran 10/10 (see Verification).

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0
  errors** in both Debug and Release.
- Failing-first: see above (new `ShipLoopBeds` type is a pre-change compile
  error; new waves absent pre-change break the asset test).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **10/10 pass (Release, 0 failures)**. The change records/gates client-side
  audio-loop state only — it cannot alter bot flight.
- Full suite `scripts/run-tests.sh`, exactly 2 runs (recorded per run below).

### Suite run 1 (recorded as it happened)
- Lobby: passed (runner continued).
- MonoGame 2303 pass + 3 skip + **1 fail** —
  `MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship`
  (live autopilot center-rank target sampling returned warps instead of a
  settled ship target; **3/3 in isolation** here; unrelated to P721 — code not
  touched; added to `docs/TEST_FLAKES.md`).
- ClientCore 1176 pass + 1 skip, 0 fail.
- Core 4753 pass, 0 fail.
- Quarantine: ClientCore 3 green; Core 1 flake
  (`LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried`,
  already in `docs/TEST_FLAKES.md`) passes in isolation — reported not failing.
- `run-tests.sh` exit 1 (driven solely by the documented MonoGame load-flake,
  which passes 3/3 in isolation).

### Suite run 2 (recorded as it happened)
- Lobby 19/19, 0 fail.
- MonoGame 2304 pass + 3 skip, 0 fail (the run-1 flake did not recur).
- ClientCore 1176 pass + 1 skip, 0 fail.
- Core 4752 pass + **1 fail** —
  `CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate`
  (known load-flake, already listed in `docs/TEST_FLAKES.md` satisfying P697's
  load-flake rule; **3/3 in isolation** here; unrelated to P721 — code not touched).
- Quarantine: ClientCore 3 + Core 18, all green.
- `run-tests.sh` exit 1 (driven solely by the documented Core load-flake, which
  passes 3/3 in isolation).

Across both runs P721's code contributed **zero failures**. Run 2 was clean
through Lobby/MonoGame/ClientCore and its only failure is a pre-existing,
documented Core live load-flake verified 3/3 in isolation.

## Files

- `src/Allegiance.ClientCore.Game/Audio/ShipLoopBeds.cs` — new per-hull loop-bed
  mapping (SIM-0372..0383).
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — per-hull beds in
  `Update`/`UpdateTurnThruster`, new sidethrust loop, load/dispose.
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — per-frame `capitalHull`
  + `sidethrustActive` inputs to the sound driver.
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/accelcapital.ogg`,
  `lshipint.ogg` (new) + `Content/Content.mgcb` registrations.
- `tests/.../P721ShipLoopBedsTests.cs`, `tests/.../P721ThrusterLoopSoundTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 14 rows updated (13 → implemented,
  SIM-0371 → n/a; LF kept).
- `docs/TEST_FLAKES.md` — documents the run-1 live load-flake.
- `docs/reports/P721_PARITY_WAVE19.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| sidethrust loop (fighter/capital) | trekigc.cpp:953-956, 1326-1354 | design | one loop bed gated on backward/left/right, asset by hull class (accelsmall/accelcapital) |
| per-hull main-thruster/interior/turn beds | trekigc.cpp:1312-1344; sounddef.mdl:1012-1024 | design | same beds, asset resolved by hull class via ShipLoopBeds |
| interior vs exterior volume-bus select | trekigc.cpp:1356-1366 | design | port drives bed by camera mode (cockpit hum in cockpit, exterior in chase) instead of two live buses |
| burst loop per mount | trekigc.cpp:999-1004 | design | already P465 StartWeaponBurst/StopWeaponBurst; matrix corrected |
| gunner interior | trekigc.cpp:1096-1111 | design | n/a — renderer has no passenger/gunner-attach state |

## Modern alternative considered

Native plays every bed as a separate looping SoundSite and mutes the internal/
external volume bus by camera view; a modern engine keeps one instance per bed
and selects it by state — which is exactly what the port does (one instance per
bed, selected by hull class + camera mode), player-identical and cheaper. No
audio-engine upgrade was warranted for a handful of cockpit beds. The capital
turn sound sharing accelsmall (a native sounddef quirk, capitalShipTurn uses
accelsmall) is reproduced faithfully rather than "fixed".

## Limitations

- SIM-0371 (gunner interior) is `n/a`: it needs the passenger/gunner-attach
  state (`GetParentShip()`/`GetTurretID()`) that this renderer's flight model
  does not represent — the local ship is always piloted.
- SIM-0347 (weapon activate loop) and SIM-0348 (manned-turret turn loop) remain
  `partial`: they need a per-mount continuous-charge and manned-turret-rotation
  state the renderer's fire model does not track (it drives per-shot + burst).
- Interior/exterior rows share the same underlying bed assets (as native does);
  the volume-bus camera distinction is approximated by camera-mode gating of the
  interior/exterior beds, not by two live simultaneous loops.
