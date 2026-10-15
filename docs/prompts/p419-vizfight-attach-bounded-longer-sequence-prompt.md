# P419 — vizfight `attach`: keep the fleet near home, run a longer varied sequence, more ships

## Why this task exists

Live-tested P418's new `attach` mode this session (`vizfight attach
192.168.0.221 6073 <webPort> <botCount>`, watched side by side with a
real MonoGame client on the same server — confirmed working, a real
"proof the port works" moment). Domingo's live feedback watching it run:

1. *"they are going to infinity, we need to figure out a longer
   sequence"* / *"and they should not go too far"* — the bots drift
   farther and farther from the starting station over time with nothing
   pulling them back, and there is no real narrative progression, just
   one endless loop.
2. *"longer combat sequence with more ships"* — scale the fleet up and
   make the show last longer with more going on.

## Confirmed root cause of the drift

`tools/vizfight/Program.cs`, the `AttachPhase.Flight` case
(~line 2610-2637): every bot's `ControlData` each tick is
```csharp
double yaw = dir * Math.Sin(now * ab.WeaveFreq + ab.WeavePhase) * ab.WeaveAmp;
double throttle = 0.55 + 0.25 * Math.Sin(now * ab.WeaveFreq * 0.7 + ab.WeavePhase * 1.3);
```
`throttle` is always positive (0.30-0.80), so every bot accumulates real
net forward distance every tick, forever — the yaw weave changes
heading slightly but never turns anyone around. There is no boundary
check, no "steer back toward the starting point" term, and no state
after `Flight` besides `Held` (an inert no-op case). This is a single,
open-ended phase with no leash and no further progression — exactly
what "going to infinity" and "no longer sequence" describe.

## Scope

### 1. A real leash — bounded, tracked, well-scoped

Compute each bot's distance from a fixed anchor (the fleet's real
launch/station position, already known from the `Launch` phase) each
tick. Below a threshold radius, keep the existing weave/cruise feel
unchanged. Past it, blend in a real "steer back toward the anchor" yaw
term (proportional to how far past the threshold, capped) so ships
visibly curve back rather than snapping or teleporting. Pick a radius
that keeps the fleet within the same rendered view the web page/
MonoGame client already frame (check `wwwroot/index.html`'s camera/
zoom defaults for a sane real number, don't guess blind).

### 2. A longer, more varied sequence — real beats, not more idle time

Extend the `AttachPhase` enum with real additional phases the fleet
cycles through repeatedly instead of sitting in `Flight` forever.
P418's own report already identified real wire-only beats that were
"possible but not driven": alliance/vote/donate
(`SendChangeAlliance`/`SendVote`/`SendPlayerDonate`, all real, already-
proven wire commands elsewhere in this codebase — check `Bot.cs` for
the exact send methods). Build a real repeating cycle, e.g.:
`Flight (bounded weave+fire, ~30-45s) → Regroup (a visible formation
change back toward the anchor) → a real mining pass (send one or two
bots to a real nearby asteroid — reuse whatever this session's
extensive NPC-mining wire path already proves works, e.g. commanding
movement toward a real `DataAsteroidIGC` position from the world state
already tracked) → Dock/Relaunch (a real docked-then-relaunched beat,
reusing the existing `Launch` phase's `SendBuyLoadout` call) → back to
Flight`, looping for the life of the process. Each phase transition
should narrate a real event through `AttachEvent` (the existing
narration helper), matching the style already used for `Flight`'s
15s "X real ships visible" narration.
Don't fabricate a beat that isn't backed by a real wire action — if a
beat native's own showcase used (flag capture, guaranteed kills) still
isn't wire-only achievable on an external server, leave it out and say
so, exactly as P418's own report already did for those two.

### 3. More ships — and make this double as a real stress-test tool

Domingo's own framing: `attach` with a bigger fleet should double as
this port's actual stress-testing setup for the real server, not just a
visual bump for the demo. Two real, distinct things to get right:

- **A legible default** for the side-by-side demo case — raise it
  meaningfully from today's small default, staggering spawn positions/
  weave phases so a bigger fleet doesn't degenerate into illegible
  ship-soup.
- **A real, much higher count genuinely usable for load testing** —
  expose it as the existing `botCount` CLI arg already allows (no new
  flag needed if the arg is already unbounded), and actually push it
  during verification, watching the server's own real per-tick cost
  signal (`[overrun] tick cost X ms exceeded period Y ms`, already a
  real log line this session has seen fire under load) rather than
  judging purely by eye. Report the real highest bot count you verified
  running cleanly (no overruns, no dropped connections) alongside the
  count actually recommended as the demo default — they don't have to
  be the same number.

## Explicitly out of scope

- Real cross-side kills — already confirmed not wire-only achievable on
  an external production server (P418's own honest finding); don't
  attempt to work around the friendly-fire gate.
- Any change to the `showcase`/`explore`/`roster`/`proof` self-hosted
  modes, or to the web frontend (`wwwroot/index.html`) itself, unless a
  genuinely small, clearly-justified tweak is needed to display a new
  phase's narration cleanly.
- P368-P418 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving the leash steering term only activates past the
  threshold radius and correctly reduces net distance-from-anchor over
  simulated ticks (not just "doesn't crash").
- **Live proof, required, against the real production server
  (`192.168.0.221:6073`), the same one a real MonoGame client is
  connected to**: run `attach` with the new higher bot count for at
  least several minutes and confirm — the fleet visibly stays within a
  bounded area near the starting station (screenshot or describe the
  web view at t=0, t=2min, t=5min showing it hasn't drifted away), the
  phase cycle visibly progresses through more than just `Flight` (real
  narrated events for each new phase), and both the web view and a real
  MonoGame client connected to the same server show the same larger
  fleet performing the same longer sequence at the same time.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P419_VIZFIGHT_ATTACH_BOUNDED_SEQUENCE.md` in the repo
   root: the leash formula chosen, the new phase cycle and which beats
   were included/excluded and why, the bot-count decision, and the live
   verification performed.
