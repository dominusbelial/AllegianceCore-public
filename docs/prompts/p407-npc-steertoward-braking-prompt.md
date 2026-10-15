# P407 — NPC SteerToward never got P343's velocity-aware braking (needs a live repro first)

## Why this task exists

Found by the same kimi-k3 3D/flight/controls/navigation/HUD review as
P404-P406 (full report:
`~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`,
finding U1). **This is a "needs a live test" finding, not a confirmed
bug** — the code divergence is real (high confidence), but whether it's
actually player-visible is unconfirmed (medium confidence). Do the live
repro FIRST; only port the fix if it actually reproduces.

## Confirmed code divergence

P343 fixed the player autopilot's brake-oscillation with a
closing-speed stopping-distance term
(`src/Allegiance.Core/Sim/AutopilotSteering.cs:67-74`,
`closing²/decel + arrivalRadius`) — diagnosed at the time as "slamming
the brakes at the last instant … overshoot and oscillate" with a
naive distance-only ramp.

The server-side NPC steering law this autopilot mirrors
(`Ship.cs:4574-4612`, `SteerToward`) still uses that same naive
distance-only ramp (`distFactor = distance >= brakeStartDistance ? 1 :
…`) — exactly the shape P343 diagnosed as broken for the player case.
NPC miners returning to base at speed are the likely visible case:
approach the garrison hot, overshoot, orbit, re-approach.

## Scope

1. **Live-repro first**: in the debug topology (`liveSettings.
   debugMode`), watch a laden NPC miner's dock approach at full cruise
   speed. Does it overshoot and orbit before successfully docking?
2. **If it reproduces**: port P343's exact term
   (`closing²/decel + arrivalRadius`) into `Ship.SteerToward`
   (`Ship.cs:4574`) — the NPC already has its own `Velocity` on hand,
   so this is a straight copy of the client-side math, not a new
   derivation. Match `AutopilotSteering.cs:67-74`'s exact formula and
   constants unless something about the NPC context genuinely requires
   different tuning — if so, say why explicitly.
3. **If it does NOT reproduce** (e.g. NPC approach speeds are low
   enough that the naive ramp never actually overshoots in practice):
   say so plainly in the report, don't port the fix speculatively, and
   close this out as "investigated, not currently player-visible."

## Explicitly out of scope

- The player autopilot itself (`AutopilotSteering.cs`) — already
  correct per P343, don't touch.
- Any other NPC decision logic (mining target selection, selling,
  auto-launch) — only the steering/braking math within `SteerToward`
  is in scope.
- P368-P406 — unrelated, don't touch.

## Verification — real proof, not just build/test

- The live repro described above, performed and documented either way
  (reproduced-and-fixed, or investigated-and-not-currently-an-issue).
- If fixed: a unit test mirroring however `AutopilotSteering`'s P343
  fix was tested, adapted to `SteerToward`'s NPC context (approach at
  speed, assert no overshoot/oscillation past the arrival radius).
- If fixed, live re-check: the same laden-miner dock approach now docks
  cleanly without orbiting.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. Either the fix (committed on `master`, not pushed) or a documented
   "investigated, not reproduced" verdict.
2. Report doc `P407_NPC_STEERTOWARD_BRAKING.md` in the repo root: the
   live-repro result, the fix if one was needed, and the verification
   performed.
