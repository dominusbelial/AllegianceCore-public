# P585 — aleph glimpse: make the resting glimpse readable (stronger, larger, fades in on approach), and add a TRANSIT PULSE (the aleph flares and the glimpse opens wide whenever any ship crosses it in your sector) with a lens-flare burst. Non-native, opt-in

## Why this task exists

P576-B added the aleph destination glimpse (`--aleph-glimpse on`, default
off; `Rendering/AlephGlimpseRenderer.cs`): the destination cluster's sky,
faintly composited into the funnel centre under the f02 star core. P577 adds
the HD sky tier, so the glimpse can show HD destinations.

Domingo, live (2026-10-08, 221 @ `17760edb`):
- "I'm not sure if I was able to see the glimpse; I think I'm getting
  teleported before touching the star." Transit triggers on the warp's
  collision sphere (radius 100 on this map) before you reach the centre
  (native, unchanged), and the resting glimpse (~15–35% at the centre, under
  the bright core) is **too subtle** to notice while approaching.
- "I also imagined I would be able to see that glimpse when ships cross over
  the warp in my current sector, because that should be visible on the lens
  flare."

**Native has no such effect:** `WinTrekClient::ShipWarped`
(`WinTrek/trekigc.cpp:2976-2991`) is an empty, commented-out body. The only
native transit visual is the teleport sparks around a ship in its last ~3 s
(`thinggeo.cpp:~651-659`, ported as `Rendering/WarpTransitParticles.cs`). So
everything here is a **non-native, opt-in** addition under the same
setting.

## Scope

1. **Resting glimpse tuning:** stronger and larger (a clearly visible
   destination-sky disc inside the inner funnel, below the star core's
   brightest part), **fading in with approach distance** (e.g. invisible past
   ~3000 u → full at ~400 u), keeping the P562/P568 funnel flow and the f02
   core on top. Named constants with rationale; tune against real-window
   shots from 2000/800/400 u.
2. **Transit pulse:** when **any ship** (yours, teammates, enemies, bots)
   crosses an aleph in the **viewer's current sector** (find the client-side
   signal: the ship leaving the sector via that warp, e.g. the
   departure/cluster-change message the client already receives, or the warp
   proximity at removal; cite it), that aleph: (a) brightens its funnel briefly
   (~0.6–1.0 s, ease-out), (b) **opens the glimpse wide** (larger radius, high
   opacity) showing the destination sky, then settles back, (c) emits a quick
   **lens-flare burst** from the funnel centre (screen-space streaks/halo, only
   when the aleph centre is on screen and not occluded: use the P547 depth
   for occlusion). Arrivals through an aleph into your sector get the same
   pulse on the arrival aleph (if the signal exists). Several simultaneous
   transits stack sensibly (cap).
3. All under `--aleph-glimpse on` (+ a settings entry); default stays off.
   Works with Native and HD skies (P577). Pin render states; **never switch
   render targets mid-frame** (P582b lesson); camera-relative placement (P573
   and P576b lessons).

## Proof (real window, P572 tooling + MCP bots)

- Glimpse at 2000/800/400 u (fade-in), Native and HD sky.
- **Transit pulse:** a bot crossing an aleph while the camera watches from
  ~800 u: a frame strip at 0/0.1/0.25/0.5/1.0 s showing the flare and the
  glimpse opening and settling; one with several bots crossing; the player's
  own transit.
- Frame time with 3 pulses at once (no hitch).
- Tests: the fade-in curve, the pulse envelope, the transit-signal detection
  (fires for a ship leaving via warp W in my sector, not for other sectors),
  default off.
- Report `docs/reports/P585_ALEPH_GLIMPSE_AND_TRANSIT_PULSE.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
