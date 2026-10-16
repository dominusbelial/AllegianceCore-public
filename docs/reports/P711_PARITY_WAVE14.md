# P711 — Parity wave 14: simulation SOUNDS, second bundle

## Summary

Ported **16 open `sounds` rows** — the ship low-status / combat feedback cues
and the mission-clock milestone VOs. Like P710, every cue rides the
established `ClientSoundEvent` → `Game1.Audio.UpdateSoundEvents` →
`SoundManager` one-shot path plus two new per-frame loop gates, driven by
wire/state the client already holds (the local ship's own `ServerShipUpdate`
fractions, `RIPCORD_DENIED(186)`, `RipcordActive`, `MiningAsteroidId`, and the
mission clock). **No new server event was required** and **no flight/simulation
behaviour changes** (guarded by the P700 regression gate).

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 163/594 (27.44%) | **198/594 (33.33%)** |
| unweighted | 53/228 (23.25%) | **69/228 (30.26%)** |

## Why these rows

All 16 rows share one client-sim sound region — native
`WinTrek/trekigc.cpp:1264-1293` (the per-frame local-ship status block) — plus
the ripcord-denied wire path and the `WinTrek.cpp:11157-11169` mission-clock
checkpoints:

- **Ship low-status / combat feedback one-shots** (SIM-0361..0366) — the four
  fraction-low alarms and the two no-ammo/no-fuel attempt cues, all
  `StartSound` one-shots edge-triggered on the local ship's own streamed
  fractions (`trekigc.cpp:1264-1275`).
- **Ripcord-active + mining loops** (SIM-0368, SIM-0370) — `PlaySoundIf` loop
  gates at `trekigc.cpp:1287,1293`.
- **Ripcord-denied** (SIM-0425, SIM-0467) — `salNoRipcordSound` on
  `FM_S_RIPCORD_DENIED(186)` (appmsg.cpp:970) and the local no-anchor check
  (clintlib.h:1175).
- **Mission-clock milestones** (SIM-0299, SIM-0300..0303) — `countdownStart`
  plus the 10/5/1-min and 30-s `vo_time_*` VOs (`WinTrek.cpp:11130,11157-11169`).
- **SIM-0410** corrected to implemented — the incoming-missile warning loop
  (`missileLockSound`/`missilelockonme`) was already built by P264 via
  `MISSILE_TRACK_WARNING(208)`; the matrix row was stale `missing` and is now
  honestly marked implemented (this is a matrix correction, not new code).

## Implementation

### Ship low-status alarms (SIM-0361..0366)

New pure edge detector `ShipStatusAlarms.Classify(prev, next)` reproduces native
`trekigc.cpp:1264-1275` exactly: a fraction falling below its low threshold
(from ≥threshold) fires its one-shot once; the two attempt latches
(`TryingToFireWeaponWithoutAmmo` / `TryingToFireAfterburnersWithoutFuel`,
trekigc.cpp:829-889) fire on the false→true edge. `ClientWorld.UpdateOwnStatusAlarms()`
samples the local ship's own `ServerShipUpdate` each frame (under the gate),
classifies the edge, and records the one-shot `ClientSoundEvent`. This uses the
exact wire data the HUD already reads, so no extra wire is needed.

| row | native | wave (sounddef.mdl) | event |
|---|---|---|---|
| SIM-0361 | hullLowSound (trekigc.cpp:1264, hull `<.15`) | vo_sal_hullcritical (:374) | `HullLow` |
| SIM-0362 | ammoLowSound (:1266, ammo `<.15`) | outofammo (:371) | `AmmoLow` |
| SIM-0363 | fuelLowSound (:1268, fuel `<.15`) | fuellow (:372) | `FuelLow` |
| SIM-0364 | energyLowSound (:1270, energy `<.05`) | vo_sal_lowenergy (:373) | `EnergyLow` |
| SIM-0365 | salNoAmmoSound (:1273, fire-with-no-ammo edge) | vo_sal_noammo (:812) | `NoAmmo` |
| SIM-0366 | salNoFuelSound (:1275, boost-with-no-fuel edge) | vo_sal_nofuel (:811) | `NoFuel` |

### Ripcord-active + mining loops (SIM-0368, SIM-0370)

Two new looped `SoundEffectInstance`s in `SoundManager` (`countdownstart` for
`ripcordOnLoopSound`, `miningsound` for `miningSound`), gated per-frame by
`Game1.UpdateShipStatusAlarms()`: the ripcord loop plays while
`ClientWorld.RipcordActive`, the mining loop while `MiningAsteroidId != -1` —
the exact `PlaySoundIf(..., bIsPlayersShip && fRipcordActive())` /
mining predicates (`trekigc.cpp:1287,1293`).

### Ripcord-denied (SIM-0425, SIM-0467)

`Apply(RipcordDenied)` now records a `RipcordDenied` one-shot
(`salNoRipcordSound` = `vo_sal_noripcord`, appmsg.cpp:970) in addition to the
existing system chat line. SIM-0467 (the client-side no-legal-anchor check,
clintlib.h:1175) shares the same cue — in this always-server-authoritative
client the denied state reaches us as `RIPCORD_DENIED(186)`, so both rows map
onto one faithfully-represented, wire-testable path.

### Mission-clock milestones (SIM-0299, SIM-0300..0303)

Extended the P680 `MissionCountdownSound` detector: at 600/300/60/30 s remaining
it emits a new `Milestone` stem (`vo_time_10min/5min/1min/30sec`) before the
per-second tail — native `countdown10min/5min/1min/30Sound`
(`WinTrek.cpp:11157-11169`). `SoundManager.PlayCountdownMilestone` plays them;
`countdownStart` (SIM-0299) was already wired by P216 and is now correctly
marked implemented.

## Failing-first

Every row was `missing` (or, for SIM-0410, already built but stale in the
matrix). The new tests cannot pass on the pre-change tree: `ClientSoundEventKind`
had no `HullLow`/`AmmoLow`/`FuelLow`/`EnergyLow`/`NoAmmo`/`NoFuel`/`RipcordDenied`
members, `ShipStatusAlarms`/`ShipStatusAlarmState` did not exist (compile error
in `ShipStatusAlarmSoundTests`), and `MissionCountdownCue` had no `Milestone`
field. Verified by stashing `src/` and rebuilding: the pre-change tree fails to
compile the new cues (`CS0117`, `ClientSoundEventKind` missing members). After
the change all 31 new/added tests pass.

## Tests (new/added)

`tests/Allegiance.ClientCore.Tests/ShipStatusAlarmSoundTests.cs` (12 tests):
- `Classify`: hull crossing emits exactly HullLow; low-low pair does not
  re-fire; all four low alarms fire independently; no-ammo/no-fuel fire on
  the false→true edge; a persisting no-ammo does not re-fire.
- `ClientWorld.UpdateOwnStatusAlarms` wire tests: own hull drop records HullLow;
  firing an empty magazine records NoAmmo; no status change records nothing;
  a REMOTE ship's hull drop does not trigger local alarms (`bIsPlayersShip`).
- `RipcordDenied` wire records the salNoRipcord event.
- Loop gates: `RipcordActivate`/`RipcordAborted` toggle `RipcordActive`;
  `OwnShipMiningUpdate` sets/clears `MiningAsteroidId`.

`tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs` (added):
- `MilestoneAssetFor` resolves the four native checkpoints (600/300/60/30) and
  rejects non-checkpoints.
- `TimedGame_EmitEachCoarseMilestone_ThenTheTailTicks` walks a 700 s timed game
  through 10min/5min/1min/30s and then the countdown10 tick, once each.
- Wave-bundled test: each milestone `.ogg` present in `Content/Sounds` and
  registered in `Content.mgcb`.

Both new classes pass **3/3 in isolation**; the affected sound/chat/countdown/
dispatch classes 168/168 (MonoGame) and 42/42 (ClientCore).

## Real assets

All 11 new waves were copied from the artwork tree into
`src/Allegiance.ClientCore.MonoGame/Content/Sounds/` and registered in
`Content.mgcb` (verified they compile to `.xnb` in the built content):
`vo_sal_hullcritical`, `outofammo`, `fuellow`, `vo_sal_lowenergy`,
`vo_sal_noammo`, `vo_sal_nofuel`, `vo_sal_noripcord`, `miningsound`,
`vo_time_10min`, `vo_time_5min`, `vo_time_1min`, `vo_time_30sec`.

## Verification

- Whole-solution build: **0 warnings, 0 errors** (Debug, verified).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **10/10 pass (Release, 0 failures)**. The change records one-shot sound events
  and gates two cosmetic loops only — it cannot alter bot flight.
- Full suite `scripts/run-tests.sh`, exactly 2 runs (recorded per run below).
  Both runs' **only** failure is the already-documented P700 gate load flake
  (`docs/TEST_FLAKES.md:814`, in code NOT touched by P711), confirmed **3/3 in
  isolation** (Debug) in this session.

### Suite run 1 (recorded as it happened)
- Lobby 19/19.
- MonoGame 2239 pass + 3 skip.
- ClientCore 1082 pass + 1 skip + **1 fail** —
  `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
  (documented load flake, TEST_FLAKES.md:814; **3/3 in isolation**; unrelated to
  P711 — verified 10/10 in the Release gate above).
- Core 4610 pass.
- Quarantine: ClientCore 3 + Core 18, all green.
- `run-tests.sh` exit 1 (driven solely by the P700 flake).

### Suite run 2 (recorded as it happened)
- Lobby 19/19.
- MonoGame 2239 pass + 3 skip.
- ClientCore 1082 pass + 1 skip + **1 fail** — the same documented P700 gate
  load flake (not P711 code; 3/3 in isolation).
- Core 4610 pass.
- Quarantine: ClientCore 3 + Core 18, all green.
- `run-tests.sh` exit 1 (driven solely by the P700 flake).

Both failures are the single documented pre-existing load flake; P711's code
contributes zero failures across both full-suite runs.

## Files

- `src/Allegiance.ClientCore/State/ShipStatusAlarms.cs` — new pure edge
  detector (`ShipStatusAlarmState`, `ShipStatusAlarms.Classify`).
- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — 7 new event kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `UpdateOwnStatusAlarms()`
  per-frame driver + state, `Apply(RipcordDenied)` sound event, alarm-state
  reset on reconnect.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — 11 new one-shots +
  `Play*` methods, `UpdateRipcordLoop`/`UpdateMiningLoop`, milestone map +
  `PlayCountdownMilestone`, disposal.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — dispatch cases +
  `UpdateShipStatusAlarms()` per-frame driver.
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — drive
  `UpdateShipStatusAlarms()`, consume `MissionCountdownCue.Milestone`.
- `src/Allegiance.ClientCore.Game/Audio/MissionCountdownSound.cs` /
  `CountdownSound.cs` — milestone emission + `MilestoneAssetFor`.
- `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb` + 12 new `.ogg` waves.
- `tests/.../ShipStatusAlarmSoundTests.cs` (new), `P680CountdownSoundTests.cs`
  (milestone tests added).
- `docs/parity/sim-rules-matrix.csv` — 16 rows → `implemented`.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| fraction-low alarm one-shots on own ship | trekigc.cpp:1264-1270 | design | identical edge-triggered one-shots from the real waves |
| no-ammo/no-fuel attempt cues | trekigc.cpp:1272-1275, 829-889 | design | identical false→true-edge one-shots |
| ripcord-active loop | trekigc.cpp:1287 | design | gated loop on ClientWorld.RipcordActive |
| mining loop | trekigc.cpp:1293 | design | gated loop on MiningAsteroidId |
| ripcord-denied cue | appmsg.cpp:970, clintlib.h:1175 | design | RIPCORD_DENIED(186) one-shot (local check is server-authoritative here) |
| mission-clock milestones | WinTrek.cpp:11130,11157-11169 | design | milestone VOs emitted once at each checkpoint |

## Modern alternative considered

Native computes the low alarms every client-sim frame against its own IGC
`m_pship`. This port's pure-renderer client has no client-side ship sim, but it
already receives the exact fractions on the local ship's own
`ServerShipUpdate`, so the edge detector samples those on every wire refresh —
player-identical (the thresholds fire on the same fraction movement) and costs
zero extra wire. The alternative — rebuilding a client-side sim just to drive
four VO alarms — was rejected as pure waste. The loop gates (ripcord/mining)
are cheap `PlaySoundIf` analogues identical to the existing bounds/missile
warnings, so no further abstraction was warranted.

## Limitations

- SIM-0467's local no-legal-anchor pre-check is folded onto the server's
  `RIPCORD_DENIED(186)` path (this client delegates ripcord legality to the
  server); the cue is faithfully represented and wire-tested, but there is no
  separate client-side anchor evaluation.
- SIM-0410 was already implemented by P264 (incoming-missile warning loop); the
  matrix is corrected, no new code for it.
