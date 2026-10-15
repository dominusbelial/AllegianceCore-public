# P423 — targeted asteroids should show a health bar and a real He3/ore-level readout

## Why this task exists

Domingo, watching old Allegiance footage: *"our asteroids are missing
the health bar, they should display the level of He3."* Traced against
native — two real, distinct, confirmed gaps.

## Confirmed real native mechanics

**1. The generic target health bar is NOT ship-only — this port's own
code is wrong about that.** `HudRenderer.cs:827-830`'s own comment
claims *"matching native's own 'if it is a ship' gate"* — false.
Native's real function, `ModelData::GetPercentHitPoints`
(`~/Development/Allegiance/src/WinTrek/consoledata.cpp:202-221`), gates
only on `(pmodel->GetAttributes() & c_mtDamagable) != 0` — a generic
"is this object damageable" check, independent of object type.
Confirmed asteroids are damageable and expose the same `GetFraction()`
interface as ships (`Igc/asteroidigc.h:172-178`, backing
`trekigc.cpp:3855`'s `ds.bpHull = pasteroid->GetFraction()` export) —
so native genuinely shows this bar for a targeted asteroid too, not
just ships.

**2. A separate, asteroid-specific ore readout also exists in native.**
`ModelData::GetOre()` (`consoledata.cpp:449-460`) is gated specifically
on `OT_asteroid`, returning the real remaining ore
(`GetAsteroid()->GetOre()`) — a distinct UI data-binding from the
generic hit-points fraction. **This is real ore content, not structural
health** — confirmed by reading `asteroidigc.h`: an asteroid's
`GetFraction()`/`m_fraction` is decremented by weapon damage
(`m_fraction -= amount / hitpoints`, `asteroidigc.h:143-157`) — pure
structural integrity — while ore/He3 content is a completely separate
value (`GetOreFraction()`/`GetOre()`). Domingo's "level of He3" ask maps
to this second, distinct readout, not the health bar.

## Confirmed port gaps

- `HudRenderer.cs:827-848`'s target-info readout is real but
  incorrectly restricted to ships only, based on a documented-but-false
  premise about native's own gate (see above) — extend it to any
  damageable target, matching native's real `c_mtDamagable` gate.
- **No per-asteroid ore/depletion data reaches the MonoGame client over
  the wire at all.** Checked `ClientWorld.cs:935-944`: the only
  `OreFraction` field decoded there is a **ship's own cargo-hold**
  fraction (a miner's current cargo load), a completely different
  value from an asteroid's own remaining resource content. Trace
  whether the real wire protocol already carries live per-asteroid ore
  depletion in some other message this port hasn't wired up yet, or
  whether it needs to be added — don't assume either way, find the
  real answer.

## Scope

1. Trace the real wire message(s) carrying live asteroid state to the
   client (this session's own server-side mining logs already show
   real live depletion — `Sector.cs`'s mine-tap/regeneration handling,
   `[mine] asteroid ... tapped: yielded X of Y requested` — so the
   *server* tracks this faithfully; the question is what reaches the
   client). Decode/add whatever's needed so a real per-asteroid ore
   fraction is available client-side.
2. Fix `HudRenderer.cs`'s target-info gate: show the health bar for any
   damageable target (ship, station, asteroid — whatever this port's
   own damageable-object concept covers), not ships only.
3. Add a distinct ore/He3-level readout specifically when the target is
   an asteroid, alongside (not replacing) the health bar — matching
   native's real two-separate-readouts shape. Match this port's
   existing readout style (reuse `DrawBar` or whatever primitive the
   existing health bar already uses, for visual consistency).

## Explicitly out of scope

- Any change to the actual mining mechanic itself (already correct,
  confirmed extensively this session) — this task is display-only,
  reading real existing/newly-wired state, not changing simulation
  behavior.
- P368-P422 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving the wire-decoded (or newly-added) per-asteroid ore
  fraction matches the real server-side value for a known asteroid.
- **Live proof, required**: target a real asteroid mid-depletion (mine
  it partway, matching this session's own established live-testing
  pattern) and confirm both a real health bar and a real, correctly
  decreasing ore-level readout appear in the HUD; target a ship and
  confirm the health bar still works exactly as before (no regression).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P423_ASTEROID_HEALTH_AND_ORE_HUD.md` in the repo root:
   the real wire-data trace/fix, the HUD gate fix, the new ore readout,
   and the verification performed.
