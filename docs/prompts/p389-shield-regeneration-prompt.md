# P389 — shield regeneration (the one piece P87 explicitly deferred)

## Why this task exists

**Correcting a stale premise first**: this port's README used to say
"Shields are deferred... the C++ no-shield branch is the only reachable
path" — that was true when written (pre-P87) but has been false since P87
landed (`docs/reports/SHIELD_DAMAGE_P87.md`) and the README wasn't updated
until this session. Shield damage absorption is real, ported, and already
live: `Ship.ApplyShieldDamage` (a faithful port of `CshieldIGC::ApplyDamage`,
including the multiplication-order subtlety), wired onto the wire byte
(`ServerShipUpdate.cs:123`), and shields are a real, buyable/lootable,
tech-gated part class today (`Server.ShipCommands.cs:1496-1543`,
`docs/reports/SHOP_TECH_GATE_P284.md`) — not a synthetic/edge-case
equipment type.

**The actual remaining gap, confirmed by this session's research**: P87's
own report explicitly deferred shield *regeneration* as "the next natural
follow-up" (§2.1) — and it is still completely unported. A shield that has
absorbed any damage stays down **forever**; there is no per-tick regrowth
anywhere in this codebase. This is the single biggest, most player-visible
gap in the whole shield mechanic.

## Confirmed real native mechanic

`~/Development/Allegiance/src/Igc/shieldIGC.cpp:59-96` (`CshieldIGC::Update`,
called every tick regardless of recent damage — nothing resets or
cooldowns it):
```cpp
SetFraction(m_fraction + GetRegeneration() * dt / GetMaxStrength());
```
- `GetRegeneration()` (`shieldIGC.h:106-111`) = `rateRegen *
  c_gaShieldRegenerationShip` — `rateRegen` is the equipped shield part's own
  stat (`DataShieldTypeIGC.RateRegen`, already decoded,
  `DataPartTypeIGC.cs:95-105`); `c_gaShieldRegenerationShip` is a side global
  attribute this port has no system for (the `GlobalAttributeSet`
  mechanism is unported repo-wide) — **treat it as `1.0`**, the exact same
  simplification this port already makes for the station-side equivalent
  (`Station.cs:367-380`, `Station.Regenerate`, which treats
  `c_gaArmorRegenerationStation`/`c_gaShieldRegenerationStation` as `1.0`
  with the identical "honest simplification, same spirit as payday
  constants" rationale — cite and follow that precedent exactly, don't
  invent a different one).
- `GetMaxStrength()` (`shieldIGC.h:112-117`) = `maxStrength *
  c_gaMaxShieldShip` — same side-global-attribute situation;
  `maxStrength` is `DataShieldTypeIGC.MaxStrength`, also already decoded.
  Treat `c_gaMaxShieldShip` as `1.0` too, same rationale.
- **Regeneration is unconditional every tick** — it is NOT gated on being
  above the `c_fcidDownedShield` (`= 0.1f`, already hardcoded in this port
  at `Ship.cs:3548`) threshold. That threshold only gates *absorption*
  behavior in `ApplyDamage` (a shield at or below it lets damage straight
  through); regen itself runs from any fraction, including `0.0`, climbing
  back up over time with no special-casing. Don't accidentally gate regen
  on the down-threshold — that would be a real behavioral bug, not a
  faithful port.
- `Clamp(fraction, 0, 1)` — `SetFraction` (`shieldIGC.h:126-147`) clamps to
  the valid range; this port's existing `ShieldFraction` property already
  documents the same 0.0-1.0 contract (`Ship.cs:337-342`), so reuse it
  as-is.

## Confirmed exact insertion point in this port

Ships tick via `Sector.Update`'s per-model loop
(`Sector.cs:600-604`: `foreach (var model in snapshot) model.Update(timeUpdate);`),
which for a `Ship` reaches the virtual `Ship.OnUpdate(double now, double dt)`
(`Ship.cs:5877`). **This is the correct insertion point** — not a new
Sector-level pass like `Station.Regenerate`/`ResolveStationRegen`
(`Sector.cs:636`/`2425`), because that pattern exists specifically because
stations are *not* `Model`s (per that method's own doc comment,
`Sector.cs:631-634`) and need a pass of their own; ships already get a
real per-tick `OnUpdate` call, so a new `Sector`-level pass would be
redundant and inconsistent with how every other per-ship tick behavior in
this file already works (e.g. the existing lifepod-oxygen-timer block
right at the top of `OnUpdate`, `Ship.cs:5879-...`).

## Scope

1. Add the regen tick inside `Ship.OnUpdate` (`Ship.cs:5877`), gated on
   having a mounted `Shield` part (reuse whatever check `ReceiveDamage`/
   `ApplyShieldDamage` already use to detect one — don't duplicate the
   detection logic, factor it out to a shared helper if it isn't already
   one). Formula: `ShieldFraction = Math.Clamp(ShieldFraction + rateRegen *
   dt / maxStrength, 0f, 1f)`, reading `rateRegen`/`maxStrength` from the
   mounted shield part's `DataShieldTypeIGC` stats.
2. Confirm and preserve: regen must run even when `ShieldFraction == 0`,
   and must NOT be gated on the `c_fcidDownedShield` threshold — verify
   this explicitly with a test (see Verification below), don't just assert
   it from this brief.
3. Log plainly on a real regen event if this port has an established
   precedent for that (check whether `Station.Regenerate` logs anything on
   every tick or only on notable transitions — match whatever convention
   already exists there rather than either spamming a log line every tick
   or inventing a new logging style).

## Explicitly out of scope

- The `GlobalAttributeSet`/side-attribute system in general — treating the
  two relevant attributes as `1.0` is the accepted, precedented
  simplification; do not build a general attribute system for this task.
- Hit-shape/collision-shape bookkeeping tied to `SetFraction`
  (`shieldIGC.h:126-147`) — no analogue in this port's collision model,
  permanently out of scope.
- Mount-ramp delay (`m_mountedFraction`, `shieldIGC.cpp:70-85`) — no part
  type in this port has a mount-ramp; not shield-specific scope.
- Sound/client cues (`activateSound` on crossing back into a usable range,
  `deactivateSound`/`salShieldsOfflineSound` on overload) — this port's
  server is headless/authoritative-sim; these are client-audio concerns
  with no existing "shield state changed" server→client signal to hang
  them on. If you judge a signal is easy and clearly useful to add, note
  it plainly in the report as a judgment call rather than silently
  skipping or silently adding it.
- P368-P388 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving regen runs unconditionally from `ShieldFraction == 0`
  (not gated on the down-threshold), climbing back toward `1.0` over real
  elapsed sim time at the equipped part's real `RateRegen`.
- A test proving a shield at partial fraction continues regenerating
  normally (not just the zero-edge case).
- A test proving a ship with no mounted shield is completely unaffected
  (no new field/behavior fires for it).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.
- A live/VM proof is likely unnecessary here (P87's own verification
  needed none for the analogous damage-absorption slice — a regen tick is
  fully observable via unit tests over elapsed sim time) — confirm this
  judgment yourself rather than assuming it from this brief; if you decide
  a live check adds real value, do it and say why.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P389_SHIELD_REGENERATION.md` in the repo root: the exact
   formula ported, the insertion point used, and the verification
   performed.
