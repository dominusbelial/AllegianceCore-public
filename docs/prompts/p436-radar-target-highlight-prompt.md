# P436 — highlight the currently-selected target on the radar

## Why this task exists

Domingo, live-testing missiles: *"pressing u not doing anything if the
target is not on my view i dont get a direction or a cue to follow."*
Investigated end to end:

- **U works** (P415's `SelectFilteredTargetForKey` — confirmed real,
  logs `[monogame] target enemy ship: <name>` on success, `no enemy
  ship in sector` on failure) — but that feedback is a CONSOLE line
  only, invisible while playing.
- **The radar is real, wired, and drawing every frame**
  (`Game1.Rendering.cs:1478-1577` gathers ship/station/warp contacts
  every frame and calls `_radarRenderer.Draw`; `RadarBracketRenderer`/
  `RadarBracketLayout`/`RadarProjection`, P250, faithfully port native's
  `RadarImage::DrawBlip` on-screen/edge-clamp mechanic,
  `radarimage.cpp:286-427`).
- **The actual gap**: every ship contact renders identically — a small
  side-colored square (`RadarBracketRenderer.DrawContact`,
  `RadarContactType.Ship` case) — with NO distinct treatment for the
  player's own currently-selected target (`_autoTarget`). Native has a
  dedicated **target LOD tier** for exactly this
  (`c_rlTarget`, one of `radarimage.cpp`'s real detail-level modes,
  confirmed this session), and this port's own `RadarBracketRenderer`
  doc comment explicitly flags it as a deferred scope item: *"no LOD
  modes... those are named follow-ups."* This is that follow-up.

## Scope

1. Give the radar contact for the player's own current target
   (`_autoTarget`, the same field P415/U-key selection already writes)
   a real, distinctly different visual treatment from an ordinary
   contact — bigger, a highlighted ring/outline, or a brighter/pulsing
   variant (your call on the exact look, but it must be unmistakably
   different from every other blip, on-screen AND when edge-clamped
   off-screen — the off-screen case is the actual reported problem).
   Reuse `RadarBracketLayout`'s existing on-screen/edge-clamp placement
   math unchanged; this is a rendering-only addition to
   `RadarBracketRenderer`, not a new placement mechanic.
2. Wire `Game1.Rendering.cs`'s contact-gathering loop
   (`~line 1478-1577`) to mark whichever contact corresponds to
   `_autoTarget`'s id as the highlighted one (a new field on
   `RadarContact`, e.g. `IsSelectedTarget`, defaulting false).
3. Real, minimal on-screen feedback for the "no candidate found" case
   too (P415's console-only `no {noun} in sector` message) — a brief
   HUD text line or similar the player can actually see while flying,
   matching this port's existing HUD-text conventions (don't invent a
   new toast/notification system if an existing pattern already fits).

## Explicitly out of scope

- Any change to `RadarBracketLayout`'s placement/opacity math — already
  correct and faithful, don't touch.
- Any other radar LOD tier native has (`c_rlAll`/`c_rlDefault`/
  `c_rlMinimal`/`c_rlNone`) — this task is specifically the target
  highlight, not a full LOD system.
- Any change to P415's target-selection logic itself (`Game1.Targeting.cs`,
  `SelectFilteredTargetForKey`) — already correct, this task only makes
  its result visible.
- The friendly-target color-confusion Domingo also flagged tonight (a
  friendly ship's target box reading an unexpectedly non-blue color) —
  a separate, smaller cosmetic item; note it in the report if you
  happen to find the exact cause while in this code, but don't treat it
  as required scope here.

## Verification — real proof, not just build/test

- **Live proof, required**: connect a real MonoGame client to a real
  server with real bots, press U to select a real enemy that is
  currently off-screen, and confirm the radar shows an unmistakable,
  distinctly-different marker for that specific contact (screenshot or
  describe clearly) — not just "a marker exists somewhere among many
  identical ones."
- A test proving the contact-highlight-selection logic in isolation
  (given a contact list + a selected target id, the right one is
  flagged) — pure, no graphics device needed, matching this port's
  established pure-logic-layer testing style.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The radar target-highlight feature + the visible no-candidate
   feedback, committed on `master` (not pushed — Domingo pushes).
2. Report doc `docs/reports/P436_RADAR_TARGET_HIGHLIGHT.md`: the exact
   visual treatment chosen and the live verification performed.
