# P256 — Warps get their own autopilot-target priority tier

Fix for: Domingo's gameplay-sense finding — "navigation is not easy, I can't target
alephs to autopilot to other sectors, I should be able to autopilot to anything."

This is entirely this port's own client-side targeting UX (P212's own doc comment
calls it "the intended POC scope"), not a native mechanic — so no native-fidelity
research was needed; this is a pure client-side priority-ordering fix, diagnosed
directly from the code.

## Root cause (as confirmed in the brief, re-verified in the live tree)

`Game1.CollectTargetCandidates` already gathered warps into the autopilot candidate
pool and steered to them correctly (`TargetKind.Warp` in `ResolveTargetPosition`,
`AutopilotSteering`). The bug was purely **selection priority**: when no ship is in
view, stations, asteroids AND warps were all dumped into one single pool and sorted
purely by screen-distance-to-crosshair. With ~134 asteroids per map vs ~2-4 warps per
sector, asteroids dominated that order, so cycling with `T` made you step past many
rocks before ever reaching an aleph — "I can't target alephs."

## Fix — tier ordering (highest first), and why

1. **Ships** (rank 0 — unchanged, not touched) — a moving target is almost never
   more centered on the crosshair than one of ~134 static rocks; live-confirmed at
   P212. This tier's logic is untouched; only the ordering below it changed.
2. **Warps** (rank 1 — new) — sparse but are the whole reason to hit T/G (travel
   between sectors); must not be buried behind the asteroid field.
3. **Stations** (rank 2 — new) — decided YES, they earn their own tier above
   asteroids, for the exact same crowding reason as warps: this map has only 2
   stations vs 134 asteroids, so without their own tier stations would be buried the
   same way warps were. Symmetric, low-risk, and stations are primary navigation
   destinations (dock / refuel / buy). Stated here per the brief's "your call."
4. **Asteroids** (rank 3) — the most numerous static scenery; offered only when no
   ship, warp or station is in view.

When the highest present tier has at least one in-view member, ONLY that tier is
offered (everything below it suppressed outright — mirroring the established
ship-priority pattern), then sorted nearest-to-crosshair within that tier.

## Implementation

`CollectTargetCandidates` keeps its camera/view setup and gathering filters
(self/dead/no-update ships), but the collection + priority-tier ordering moved into a
new headless internal core `Game1.SelectCandidates(objects, projector, out …, out
winningTier)` (plus the `CandidateObject` carrier type) — the single testable source
of truth for the ordering. Steering, ship-tier logic and `AutopilotSteering` are
untouched. Types went `private → internal` and `InternalsVisibleTo` was added so the
live-verify harness (and the MonoGame test project) can drive the real ordering core.
The `[monogame] target-pick:` console log now also reports `tier=` and the ordered
candidate preview.

Out of scope, correctly untouched: `AutopilotSteering` steering math, ship-priority
logic, the P254 ship↔asteroid collision fix, P255 mining visual (both already landed
on master before this task — 43f85e0 / b8c1f7e — noted, not depended on).

## Verification

- Build: whole solution 0 warnings / 0 errors.
- Tests: full suite run 2x; totals Lobby 12 / MonoGame 287 / ClientCore 85 / Core
  3074 (unchanged — no tests added). Only the documented pre-existing
  `LiveTurretGunnerBindingTests` timing flake appeared. See /tmp/p256_test1.log,
  /tmp/p256_test2.log.
- Live proof (real session, .verify_p256/README.md): real in-process server on
  m2xspiderw (44 warps, 134 asteroids) + real ConnectionSession + real ChaseCamera +
  real `SelectCandidates`. Across 1760 qualifying viewpoints (>=1 warp & >=2 asteroids
  & no ship in the 75deg frustum): OLD P212 locked an asteroid before any warp in
  **1760/1760**; NEW offers the warps-only list (tier=warp) in **1760/1760** — the very
  first T/G now reaches a warp immediately.

Committed on `master` (not pushed — Domingo pushes).
