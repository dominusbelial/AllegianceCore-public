# P409 — HUD polish batch: thrust-gauge color, leader-command overlap, silent P333-disable warning

## Why this task exists

Found by the same kimi-k3 3D/flight/controls/navigation/HUD review as
P404-P408 (full report:
`~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`,
findings Q1-Q3). Quality/polish items, no correctness claim — bundle
them as one small batch like P340 did for the prior review's B4/B5/U3.

## Q1 — throttle gauge colored like a health bar (prior review's note, never addressed)

`HudRenderer.cs:425`: every gauge's percentage is painted
`frac <= 0.5f ? Warning : Accent`, including the Thrust gauge — an idle
throttle (axis −1 → fraction 0) reads "0%" in warning red, exactly the
"0% WARNING" look the 2026-09-21/22 review already flagged as wrong
(a throttle is an axis position, not a depletable resource).

**Fix**: pass the `GaugeKind` into the color choice and always use
`Accent` for `Thrust`; keep the warning ramp for hull/shield/energy/
fuel/ammo (those genuinely are depletable resources where "low = bad"
is correct).

## Q2 — leader-command line overlaps the speed readout

`DrawLeaderCommand` draws centered at `(w/2, h−108)`
(`HudRenderer.cs:536`); `DrawSpeed` draws the big speed numeral at
`(w/2, cy = h−108)` (`HudRenderer.cs:434`) — the same anchor. When the
team leader has a same-side bot T-selected, the two strings overprint.

**Fix**: move the leader-command line up (e.g. to `h − 140`) so it
clears the speed numeral.

## Q3 — P333 ship scaling silently disables when the hull-length table is missing

`HullLength` (`Game1.Rendering.cs`) returns 0 when the catalog is
unavailable, which `ScaleForLength` then treats as scale 1 (fallback,
unnormalized) — with no log line. The 2026-09-21/22 review's item 2d
already asked for a one-line "P333 ship scaling DISABLED" warning in
this path; it never landed.

**Fix**: add one `Console.WriteLine` (edge-gated — log once, not every
frame/lookup) in the catalog-load failure path, naming plainly that
P333 scaling has silently fallen back to unnormalized rendering.

## Scope

Land all three fixes above. Each is small and independent; land them
together as one batch (matching the P340 precedent for bundling this
review's own small polish items).

## Explicitly out of scope

- Any other gauge/HUD element not named above.
- The warning-ramp threshold itself (`frac <= 0.5f`) for the resource
  gauges that keep it — only Thrust's color rule changes.
- P368-P408 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: confirm the Thrust gauge shows its normal
  accent color at idle (not warning red); confirm the leader-command
  line no longer overlaps the speed numeral when a same-side bot is
  T-selected as team leader; confirm the P333-disabled warning prints
  exactly once when the hull-length catalog is deliberately made
  unavailable (and not at all in the normal case).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The three fixes, committed on `master` (not pushed — Domingo
   pushes).
2. Report doc `P409_HUD_POLISH_BATCH.md` in the repo root: each fix
   and the verification performed.
