# P389 — Shield regeneration (the one piece P87 explicitly deferred)

**Date:** 2026-09-26
**Repo:** `/home/domingo/Development/AllegianceCore`, branch `master` (tip `2c085f48`,
the "README: correct stale 'shields deferred' premise" commit).
**Scope:** retire the last missing half of the shield mechanic — per-tick
regeneration. P87 (`docs/SHIELD_DAMAGE_P87.md`) ported shield damage
*absorption* and explicitly deferred regeneration as "the next natural
follow-up" (§2.1). Until now a shield that absorbed any damage stayed down
**forever**. This stage adds the unconditional per-tick regrow.

---

## 1. The exact formula ported

Native `CshieldIGC::Update` (`src/Igc/shieldIGC.cpp:59-96`) is called on the
mounted shield every tick regardless of recent damage (nothing resets or
cooldowns it) and grows the fraction with one line (`shieldIGC.cpp:88`):

```cpp
SetFraction(m_fraction + GetRegeneration() * dt / GetMaxStrength());
```

- `GetRegeneration()` (`shieldIGC.h:106-111`) = `rateRegen ·
  c_gaShieldRegenerationShip`. `rateRegen` is the equipped shield part's own
  stat, already decoded as `DataShieldTypeIGC.RateRegen`
  (`DataPartTypeIGC.cs:96-105`). `c_gaShieldRegenerationShip` is a side-global
  attribute; this port has no `GlobalAttributeSet` system, so it is treated as
  **1.0** — the exact same honest simplification `Station.Regenerate` already
  makes for its `c_gaArmorRegenerationStation`/`c_gaShieldRegenerationStation`
  terms (`Station.cs:374-377`, `stationIGC.h:195-208`), and P87's
  `ApplyShieldDamage` makes for `c_gaMaxShieldShip`.
- `GetMaxStrength()` (`shieldIGC.h:112-117`) = `maxStrength ·
  c_gaMaxShieldShip`. `maxStrength` is `DataShieldTypeIGC.MaxStrength`, also
  decoded. `c_gaMaxShieldShip` likewise treated as 1.0, same rationale.
- `SetFraction` (`shieldIGC.h:126-147`) clamps the fraction to [0,1]; this
  port reuses that contract via `Math.Clamp`, the same defensive style
  `Station.Regenerate`/`Asteroid.Regenerate` already use.

So the ported formula, reading the real mounted shield's stats:

```csharp
_shieldFraction = Math.Clamp(
    _shieldFraction + st.RateRegen * dt / st.MaxStrength, 0f, 1f);
```

**Critical semantic preserved (and pinned by a test):** regeneration is
**unconditional** — it is NOT gated on the `c_fcidDownedShield` (= 0.1,
already hardcoded at `Ship.cs:3548`) threshold. That threshold only gates
*absorption* inside `ApplyDamage` (a shield at or below it lets damage straight
through); regen itself climbs from any fraction, including 0.0, with no
special-casing. Gating regen on the down-threshold would be a behavioral bug,
so the test suite forces a shield to exactly 0 and proves it still regenerates.

## 2. Insertion point

Ships tick via `Sector.Update`'s per-model loop
(`Sector.cs:600-604`), which reaches the virtual `Ship.OnUpdate(double now,
double dt)` (`Ship.cs:5877`). This is the correct insertion point — **not** a
new Sector-level pass like `Station.Regenerate`/`ResolveStationRegen`
(`Sector.cs:636`/`2425`), because that pattern exists only because stations
are *not* `Model`s (per `Sector.cs:631-634`'s own doc) and need a pass of
their own; ships already get a real per-tick `OnUpdate` call. A Sector-level
pass would be redundant and inconsistent with how every other per-ship tick
behavior (lifepod-oxygen timer, cloak sim, weapon loop) already works.

The regen is wired as a private `Ship.UpdateShield(float dt)` invoked from
`OnUpdate` directly after `UpdateCloak((float)dt)` (`Ship.cs:5987` region) —
i.e. where native `CshipIGC::Update` calls its mounted parts' `Update`, right
alongside the cloak sim. A ship with no mounted `EquipmentType.Shield` part
returns immediately (no-op).

**Shared detection seam (no duplicated logic):** both `ReceiveDamage`
(absorption) and the new `UpdateShield` (regeneration) now resolve the mounted
shield through one helper, `Ship.MountedShieldType()` — the first mounted
`EquipmentType.Shield` part's decoded `DataPartTypeIGC.Shield` type (or
`null`). `ReceiveDamage` was refactored to use it (it previously inlined the
same lookup at `Ship.cs:3379`), so the two halves of the mechanic can never
disagree on what counts as "a shield is mounted".

**Logging:** none. The port's regen precedent (`Station.Regenerate`,
`Station.cs:381-397`) is silent on every tick — it only updates the fraction.
Matching that convention, `UpdateShield` writes no per-tick log line and
invents no new logging style.

## 3. Scope boundary / explicit exclusions (unchanged from the brief)

- `GlobalAttributeSet` / side-attributes in general — not built here; the two
  relevant terms are the precedented 1.0 simplification (cite
  `Station.Regenerate` + P87 `ApplyShieldDamage`, both identical).
- Hit-shape/collision-shape bookkeeping tied to `SetFraction`
  (`shieldIGC.h:126-147`) — no analogue in this port's collision model.
- Mount-ramp delay (`m_mountedFraction`, `shieldIGC.cpp:70-85`) — no part type
  in this port has a mount-ramp.
- Sound/client cues (`activateSound` on crossing back above 5%,
  `deactivateSound`/`salShieldsOfflineSound` on overload) — this server is
  headless/authoritative; there is no "shield state changed" server→client
  signal to hang them on. Judgment call, stated plainly: I judged adding a new
  owner-only wire signal for shield-state changes to be out of proportion to
  this task (the wire byte 41 already carries the live fraction to the client
  every update, which a future client HUD can render directly), so none was
  added here. Not silently skipped; flagged as a conscious scope line.
- P368–P388 — untouched.

## 4. Verification

**Tests (TDD, RED→GREEN→must-fail), all in `CombatShieldTests.cs`** — the four
new tests sit beside the six P87 absorption tests:

1. `Shield_regenerates_from_zero_unconditionally_over_real_time` — mounts a
   real shield, forces its fraction to exactly **0** (≤ the 0.1 down
   threshold), pumps real sim ticks, and asserts it grew to
   `rateRegen·totalDt/maxStrength` from the decoded `RateRegen`. **This is the
   explicit "regen is not gated on the down-threshold" proof.**
2. `Shield_at_partial_fraction_continues_regenerating` — starts at 0.5 (above
   the threshold), asserts additive growth to `0.5 +
   rateRegen·totalDt/maxStrength` (not just the zero edge-case).
3. `Shield_regeneration_clamps_at_full_charge` — pumps far past full, asserts
   the fraction pins at 1.0 and never overshoots.
4. `Ship_without_mounted_shield_is_unaffected_by_regen` — no `MountPart`;
   pumps 200 ticks, asserts `ShieldFraction` stays exactly 0 and the parts list
   still contains no `EquipmentType.Shield`.

- **RED confirmed:** before implementation, tests 1 and 2 failed (fraction
  stayed 0 / 0.5). Green afterwards.
- **Must-fail proven:** temporarily commenting out the `UpdateShield((float)dt)`
  call re-failed tests 1 and 2; the call was restored (working tree clean of
  the probe).
- **Whole-solution build** (`/home/domingo/.dotnet/dotnet build
  AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
- **Full suite** (`dotnet test AllegianceCore.slnx`, run twice): every failure
  across both runs is a documented/pre-existing live-wire timing flake —
  Lobby 12/12, MonoGame 761/761 both runs. The Core.Tests and ClientCore.Tests
  failures were each verified environmental:
  - `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
    — the documented **persistent** machine-state flake (fails 3/3 in isolation
    and on the pristine tree; skill: "don't chase it").
  - `LiveWinConditionTests.Board_refused_once_mission_over`,
    `LiveTreasurePickupTests.Rescue_ship...`,
    `LiveRipcordTests.Re_target_activates_to_requester_only`,
    `InboundFloodSafetyLiveTests.Oversized_packet...` — all green in isolation.
  - `ClientCore.Tests` `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`
    and `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`
    — both re-fail **identically on the pristine tree** (verified via
    `git stash`), proving they are pre-existing entity-table/live-wire timing
    flakes unrelated to this change.
- No VM / no native-repo change was warranted: a per-tick regen is fully
  observable over elapsed sim time by unit tests (P87's analogous
  damage-absorption slice needed none either). This judgment was confirmed,
  not assumed — the four tests above exercise the real mounted-shield stats
  over real pumped sim time, so a live proof would add no information.

## 5. Files changed

- `src/Allegiance.Core/Sim/Ship.cs` — `MountedShieldType()` (shared
  shield-detection seam), `UpdateShield(float dt)` (the regen tick); the
  `OnUpdate` call site; `ReceiveDamage` refactored onto the shared seam.
- `tests/Allegiance.Core.Tests/CombatShieldTests.cs` — +4 P389 tests (10 total
  in the class).
- `P389_SHIELD_REGENERATION.md` — this report.

Suite counts (Allegiance.Core.Tests): the class went 6 → 10; the project's
observed total on run 1 was 3376 (baseline 3372 + 4).
