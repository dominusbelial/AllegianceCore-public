# P266 — Predictive lead / aim indicator (lead reticle)

## Relationship to native

Native renders a predictive LEAD reticle in `WinTrek/indicator.cpp:199-360`
(`RenderLeadIndicator`): when a command target is selected, it solves where the
mounted weapon must point so a round intercepts the MOVING target, and draws a
dedicated lead icon at that predicted aim point — genuinely offset from the
target's current on-screen position — with a hit-probability colour ramp and an
in-range/out-of-range surface swap. The reticle is gated by the hull ability bit
`c_habmLeadIndicator = 0x800` (`src/Igc/igc.h:737`), or — for a hull lacking that
bit — by a friendly relay station (`c_sabmRemoteLeadIndicator = 0x80`,
`igc.h:751`) or friendly relay ship (`c_habmRemoteLeadIndicator = 0x100`,
`igc.h:734`) that has the target in scanner range (`indicator.cpp:254-315`).

## The real math — solveForLead / solveForImpact

Native's prediction is `solveForLead` (`src/Igc/common.cpp:121-149`), which
delegates to `solveForImpact` (`common.cpp:59-119`). It solves for the smallest
positive root `t` of the intercept quadratic

    0 = |ΔP|² − r²  +  2·t·(ΔV·ΔP − r·s)  +  t²·(|ΔV|² − s²)

where `ΔP = targetPos − shooterPos`, `ΔV` is the target's velocity (absolute
projectile) or the target's velocity relative to the shooter (non-absolute,
`common.cpp:144-146`), `s` is the projectile speed, and `r` the target radius.
`t` is the time-to-intercept; the lead aim DIRECTION is
`(ΔP + t·ΔV) / (t·s + r)` (`common.cpp:108`).

This port did NOT re-derive the physics — the faithful port of
`solveForImpact` (`Ship.SolveForImpact`, P168) and of `solveForLead`
(`Ship.SolveForLead`, P169, used by the wingman fire gate) already existed in
`src/Allegiance.Core/Sim/Ship.cs`. P266 reuses them unchanged via a new public
wrapper `Ship.SolveLead(target, weapon, skill, out direction)` (returns `t`) so
the server's per-tick broadcast can drive the indicator off the exact same
physics.

## Port architecture

Native's client solved the lead from its own local IGC simulation
(`trekigc`/`indicator.cpp`). This port's MonoGame client is a **pure renderer
with no static core** — it has neither the live velocities nor the projectile
speed to solve the quadratic. So, following the exact P230/P255/P264
owner-only-per-tick precedent, the SERVER computes the lead and streams it:

- New owner-only wire codec `Wire.LeadIndicatorUpdate` (**FMID 210**, above
  native's real max 200 and the port's 204-209), 34 bytes fixed: `shipID`,
  `targetShipID`, `inRange`, `green` (div-240), `leadWorldPos`, `targetPos`.
- `Server.BroadcastLeadIndicator()` (per tick): for each peer's ship, if it has a
  `Ship` command target (`c_cmdCurrent` → `Ship.CommandTarget`) in its own sector
  with a mounted weapon, computes `t`/`direction` via `Ship.SolveLead`, the lead
  world point = `shooter + direction·dist`, `inRange = (t ≤ weapon.GetLifespan())`
  (native `indicator.cpp:224`), and the hit-probability `g`. Streams NA/no-lead
  otherwise so the client clears the reticle.
- `Server.CanShowLeadIndicator(shooter, target)`: the hull/relay gate. Direct:
  `shooter.HullType.HasCapability(HabmLeadIndicator)`. Else friendly stations with
  `SabmRemoteLeadIndicator` / friendly non-boarded ships with
  `HabmRemoteLeadIndicator` within `VisibilityMath.InScannerRange` of the target
  (reusing the P131/P132 visibility machinery — the relay exception was cleanly
  reachable with data already available, so it is BUILT, not a follow-up).
- Client: `ClientWorld.LeadIndicator` (`LeadIndicatorState`) + dispatch; `Game1`
  projects `leadWorldPos` and `targetPos` to screen (`ComputeLeadIndicator` +
  shared `ProjectToScreen`) into a `LeadIndicatorView`; `HudRenderer.Draw` draws a
  distinct OPEN DIAMOND (rotated-square outline + centre dot) at the lead point
  and a faint hollow ring at the target's current centre.

## The green→red hit-probability ramp

Native's ramp lives in the cockpit-view crosshair colouring
(`indicator.cpp:341-357`): `g = (f<1) ? 1−0.75f : (f<2) ? 0.5−f/4 : 0` with
`f = l²/r²` (screen-space distance from lead to reticle over the target's screen
radius). The port has no screen-space quantities server-side, so it computes the
ANGULAR equivalent `f = (θ/angularRadius)²` where `θ` is the angle between the
ship's current forward aim (the firing solution) and the solved lead direction,
and `angularRadius = atan2(targetRadius, distance)` — the same piecewise, in the
pure `Ship.LeadHitProbability` (unit-tested). Render colour `(1−g, g, 0)` (green
when the aim is aligned with the lead, through amber to red as it drifts); when
out of weapon range the diamond renders in native's dimmed grey
(`0.33,0.33,0.33,0.5`, `indicator.cpp:333-335`) in place of its in/out surface
swap. This is an honest stated adaptation of an exact-but-recoverable native
formula (screen-space → angular).

## Static crosshair: supplement, not replace

Native keeps a centre reticle and additionally draws the lead icon at the
predicted aim point (the centre crosshair's colour is driven by lead alignment,
`indicator.cpp:337-357`, but the reticle itself stays). The port therefore keeps
the existing round crosshair untouched and ADDS the distinct diamond lead reticle
— an addition, not a replacement. No weapon-fire mechanics are changed; this is a
pure aiming-aid overlay.

## Live proof (real offset)

A throwaway off-repo harness booted a real in-process server (PCore001.igc),
seated a real bot whose own ship was mutated into a `c_habmLeadIndicator`
fighter with a ~2000 u/s weapon, placed a laterally-moving enemy (140 u/s in +Y)
as its command target, and read the REAL `LeadIndicatorUpdate(210)` the server
streamed over the real wire:

```
[lead] peer 1 ship 8096 target 7001 inRange 1 green 0.00 -> LEAD_INDICATOR_UPDATE(210)
leadWorld=(1514.7,502.6,510.4) targetPos=(1500.0,445.2,518.0) inRange=True green=0.000
worldOffset=|lead-target| = 59.7 units, swung +Y by 57.4
```

The lead world point is 59.7 units from the target's current position, swung
toward the target's motion — the real `solveForLead` prediction, not a marker on
the target. As the shooter's aim was held fixed while the target moved, the live
server log streamed the ramp down over real wire ticks: `green 0.92 → 0.67 →
0.27 → 0.07 → 0.00`.

The real `HudRenderer.DrawLeadIndicator` was then rendered to offscreen
`RenderTarget2D`s (`.verify_p266/lead_red_misaligned.png` /
`lead_green_aligned.png`): an open DIAMOND at the projected lead point, a faint
RING at the projected target centre, on-screen centre-to-centre offset **~42 px**
(projected from the real 59.7-unit world offset). Vision-verified: red diamond at
~(480,250), white ring at ~(480,295), clearly separate.

## Build & test status

- Whole-solution build: **0 warnings, 0 errors**.
- New deterministic unit tests (`Allegiance.Core.Tests`): `LeadIndicatorTests`
  (7: `LeadHitProbability` ramp piecewise + `Ship.SolveLead` lateral-intercept),
  `Wire.LeadIndicatorUpdateWireTests` (5: round-trip / no-lead / green clamp /
  exact bytes / bad-fmid) — **12/12 pass in isolation**.
- New live test `LeadIndicatorLiveTests.Moving_enemy_command_target_yields_a_real_offset_lead_over_the_wire`
  (real server + real bot + real wire): asserts the received lead is genuinely
  offset (>15 units) and swung toward the target's motion, and that clearing the
  command target clears the indicator; fails if the broadcast or the solve is
  removed. **Passes in isolation.**
- Full `Allegiance.Core.Tests` suite (3117 total): run 3×, passing 3116 / 3115 /
  3116. Every failure is a pre-existing full-suite timing flake, none caused by
  P266: `LiveTurretGunnerBindingTests` (confirmed by running the clean P265
  baseline, which fails the same way — environmental, not this change) and one
  transient `AddPartTests.Part_pickup_decline...` (passes 3/3 in isolation).
  The 13 new P266 tests pass 13/13 in isolation and in every full run.
- `Allegiance.ClientCore.Tests` (117 total): clean rerun (one transient
  `LiveChatSectorWireTests`-style timing flake on a run, green on rerun).
- `Allegiance.ClientCore.MonoGame.Tests`: **342 passed, 0 failed** (rendering is
  additionally covered by the offscreen visual proof).

## Scope boundaries

- No change to weapon-fire mechanics — pure aiming-aid overlay.
- The relay scanner exception IS built (data was cleanly reachable via
  `VisibilityMath`). The heal-on-friendly gating of native `indicator.cpp:206-217`
  is not (this port's weapons are all damaging projectiles; noted, out of scope).
- The `g_bCheatLeadIndicator` debug cheat is not ported.
