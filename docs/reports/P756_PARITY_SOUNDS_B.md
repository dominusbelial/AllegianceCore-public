# P756 — Simulation sounds wave B

**Scope:** the sounds rows P755 left open, focusing on the P743 correctness-pass follow-ups (S31, S34, S35). Interface/HUD rows, siege/finale, training and HD textures were out of scope per the brief.

**Coordination:** checked unmerged `pi/p755` and `pi/p755b`; both touch only SIM-0294/0295/0298, so those were skipped. `pi/p757`/`pi/p758` do not touch the rows or functions edited here.

## Scorecard

```text
Before (master):
  sounds | 83.45% (459/550) | 80.19% (170/212)
  TOTAL  | 81.62% (1048/1284) | 79.24% (332/419)

After (this branch):
  sounds | 85.09% (468/550) | 81.60% (173/212)
  TOTAL  | 82.32% (1057/1284) | 79.95% (335/419)
```

Net change: **+9 weighted points in sounds**, **+3 completed rows**.

## Rows completed

| Row | Native | Port change | Tests |
|-----|--------|-------------|-------|
| SIM-0247 | `Igc/shipIGC.cpp:1193-1200` collisionSound | `CollisionFeedback.SelectSoundSources` now yields one `CollisionSoundSource` per party at the shared impact point; `Game1.Audio.UpdateCollisionSounds` plays one cue per party. Ship-rock collisions skip the NA rock party. | `CollisionFeedbackTests.Sound_selection_yields_one_source_per_party_in_local_sector` |
| SIM-0276 | `WinTrek/WinTrek.cpp:8295-8316` salAutopilotDisengageSound | Added `Game1.ShouldPlayAutopilotDisengageCue` rule, called from `DisengageAutopilot` for the manual-input branch; pins exactly-one-cue per engaged→off transition. | `AutopilotDisengageSoundTests.DisengageCue_Fires_WhenAutopilotWasEngaged`; `AutopilotDisengageSoundTests.DisengageCue_Silences_WhenAutopilotWasAlreadyOff`; `MouseFlightControllerTests.MeaningfulStickMove_PastTwiceDeadzone_Disengages` |
| SIM-0380 | `WinTrek/trekigc.cpp:1337-1344` GetManuveringThrusterExteriorSound | Added `HullLoopSoundMap`, `ShipLoopBeds.TurnExterior`, separate interior/exterior turn loop instances in `SoundManager`, and `UpdateTurnThrusterExterior`. `Game1.FlightInput` drives interior in cockpit view and exterior in chase/exterior view, resolving beds from the hull type's SoundIDs. | `P721ShipLoopBedsTests.TurnExterior_ResolvesHullSoundId_WithClassFallback` |

## Verification

- Whole-solution build: **0 errors, 0 warnings**.
- New/affected tests run 3× alone: all passed 3/3.
  - `AutopilotDisengageSoundTests` (2 tests)
  - `CollisionFeedbackTests` (5 tests)
  - `P721ShipLoopBedsTests` (17 tests)
  - `TurnThrusterSoundTests` (5 tests)
  - `P721ThrusterLoopSoundTests` (1 test)
- Fast-tier suites (run individually because `tools/run-tests.sh --tier fast` exceeded the 10-minute budget on this loaded workstation):
  - `Allegiance.ClientCore.MonoGame.Tests --filter Tier=Fast`: 2581 passed, 3 skipped
  - `Allegiance.ClientCore.Tests --filter Tier=Fast`: 1482 passed
  - `Allegiance.Core.Tests --filter Tier=Fast`: 4887 passed
  - `Allegiance.Lobby.Tests --filter Tier=Fast`: 19 passed
- AOT publish was **not required**: only `Allegiance.ClientCore.Game` and test files changed; `src/Allegiance.Server` and `src/Allegiance.Core` were unchanged.

## Rows intentionally left open

- SIM-0294/0295/0298: touched by unmerged `pi/p755`/`pi/p755b`; skipped per coordination rules.
- SIM-0239, SIM-0267, SIM-0445–SIM-0453: remain blocked by missing per-magazine/pickup wire state; no era/platform constraint, so not marked `n/a`.
- SIM-0347, SIM-0348, SIM-0345, SIM-0411: remain open; they require additional wire state or multi-object ambient infrastructure beyond this wave's scope.
- Interface/HUD sound rows (SIM-0272–SIM-0274, SIM-0285, SIM-0317–SIM-0338): out of scope.

## Native file:line citations

- `~/Development/Allegiance/src/Igc/shipIGC.cpp:1193-1200`
- `~/Development/Allegiance/src/WinTrek/WinTrek.cpp:8295-8316`
- `~/Development/Allegiance/src/WinTrek/trekigc.cpp:1337-1344`
