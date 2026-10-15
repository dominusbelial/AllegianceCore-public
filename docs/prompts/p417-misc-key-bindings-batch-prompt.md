# P417 — small leftover key-binding batch (target self/nothing/command, screenshot)

## Why this task exists

Same key-binding survey as P413-P416. These are the small, low-risk
leftovers explicitly deferred from P415 as not worth forcing into that
task's main scope.

## Confirmed native commands

- `TK_TargetSelf` ("Target Self") — targets the player's own ship.
  Trivial: whatever this port's target-selection state already is, set
  it to the player's own ship id.
- `TK_TargetNothing` ("Target Nothing") — clears the current target/
  autopilot-target, mirroring however `DisengageAutopilot`/target-clear
  already works in this port (`Game1.FlightInput.cs`).
- `TK_TargetCommand` ("Target Command") — native's shortcut to target
  whatever the current command console target is (a console-mode
  concept native has and this port doesn't share 1:1). **Investigate
  first**: check whether this port has any equivalent "current command
  target" concept at all before deciding whether this is portable or
  should be documented as no-equivalent.
- `TK_TargetCenter` ("Target Center") — target whichever candidate is
  closest to the reticle/crosshair, not closest in raw distance. Check
  whether `Game1.Targeting.cs`'s existing candidate ranking already has
  a "closest to view center" concept it can reuse (the frustum/tier
  ranking logic already there may be close to this) or needs a small
  addition.
- `TK_ScrnShot` ("Take a screen shot") — a real, simple, standalone QoL
  feature: save the current frame to disk. No server/wire dependency at
  all — purely a MonoGame client-side render-target capture.

## Explicitly out of scope

- Everything already covered by P413 (cloak), P414 (ripcord), P415
  (friend/foe targeting family), P416 (turret seats).
- `TK_ConMode*`/`TK_ToggleGrid`/`TK_CycleRadar` — confirmed this session
  to have no real equivalent UI-mode or radar-mode concept in this port;
  don't build new UI infrastructure to support them, that's real scope
  creep beyond "add a key to an existing mechanic."
- P368-P416 — unrelated, don't touch.

## Scope

Add each of the 5 items above that has a real, findable backing concept
in this port already (per the investigation notes for `TargetCommand`/
`TargetCenter`); for any that genuinely don't, document plainly why and
skip rather than build new infrastructure. Add every new binding to
`KeyBindingTable.cs`'s help overlay.

## Verification — real proof, not just build/test

- Live proof for each item actually built: press the key, confirm the
  real effect (own ship targeted, target cleared, screenshot file
  written to a sensible path, etc.).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. Whichever items get built, committed on `master` (not pushed —
   Domingo pushes).
2. Report doc `P417_MISC_KEY_BINDINGS.md` in the repo root: what was
   built vs. skipped and why, plus the verification performed.
