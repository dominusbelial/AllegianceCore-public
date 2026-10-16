# P395 — cockpit HUD gauges render top/bottom-inverted from native ("horns" instead of a clustered ring)

## Why this task exists

Live-tested this session. Domingo: "the original implementation of gauges
would form kind of a circle shape around the center targeting system,
ours look like arms and legs... ours look like horns" — specifically,
THR (throttle) and FUEL currently render **above** the crosshair like two
horns, while native's real HUD clusters its gauges tightly around/below
the crosshair. Two screenshots were compared live this session: the real
native HUD (`docs/reference-images/native-hud-reference.jpg`) shows a
compact cluster (Armor/Energy/Shield bubbles nested close together below
the crosshair, two large arc gauges framing the top, a distinct compass/
speed dial further below) — nothing like this port's current wide,
spread-to-the-corners layout with THR/FUEL sticking up top.

## Confirmed likely root cause — a coordinate-convention mistake, not a positioning typo

`HudRenderer.cs:341-352`'s gauge-placement table carries this comment:
```csharp
// (kind, fraction, native X offset, native Y offset) — offsets from the
// training overlay; the Y sign is inverted to viewport y-down space
// (native's "+93.5" means "up" → subtract in screen coords).
```
This assumes native's 2D screen-offset `Point` class (used in
`WinTrek/indicator.cpp:515-568`, the training-overlay callout positions —
`ThrottleTrainingOverlay` at `Point(-126.5, +93.5)`,
`EnergyTrainingOverlay` at `Point(+126.5, -112.5)`, etc.) is **Y-up**
(math convention, positive = up), requiring negation for MonoGame's
Y-down screen space. **This session's live evidence suggests that
assumption is backwards** — 2D screen/UI overlay coordinates in a
Windows-era codebase are overwhelmingly conventionally **Y-down** already
(matching GDI/DirectDraw/cursor-position conventions), the same as
MonoGame's own screen space, meaning **no inversion should have been
applied at all**. Confirmed live: the port's current negated values put
Throttle/Fuel at `oy = -93.5` (renders **above** crosshair — the observed
"horns"), while native's raw, un-negated value is `+93.5`. If native's
convention is already Y-down, using the raw value directly would put
Throttle/Fuel **below** the crosshair instead — matching the reference
screenshot's actual layout far better than the current inverted result.

**This needs empirical confirmation, not just this reasoning** — the
`GetScreenRectValue()->GetValue().YMax() - 1 - point.Y()` flip found at
`WinTrek.cpp:8482` shows native itself sometimes explicitly flips Y
between different coordinate subsystems, so don't assume either direction
dogmatically. Verify by trying the un-inverted values first (removing the
negation entirely) and rendering a live comparison against
`docs/reference-images/native-hud-reference.jpg` before concluding.

## Scope

1. Investigate whether `indicator.cpp`'s training-overlay coordinates are
   even the right source for the REAL, always-on cockpit HUD's gauge
   positions in the first place — they're tutorial-callout coordinates
   (used once, during Mission 2, to point an arrow at each gauge with a
   label), not necessarily identical to the gauge's actual rest position
   during normal gameplay. Search for a separate, non-training-overlay
   cockpit HUD drawing site in native (`WinTrek/`) that might define the
   real always-on gauge layout with its own (likely tighter, more
   clustered) coordinates — check files with names suggesting the actual
   HUD/cockpit rendering, not just the tutorial overlay system. If no
   better source exists and the training-overlay coordinates really are
   the only real reference, proceed with fixing the sign convention as
   described above instead.
2. If a genuine "real cockpit HUD" coordinate source is found, prefer it
   over the training-overlay numbers entirely and rebuild
   `HudRenderer.cs`'s placement table from it.
3. If the training-overlay numbers are the best available source, fix the
   Y-sign handling in `HudRenderer.cs:344-352` per the finding above, and
   correct the misleading code comment to state the real, verified
   convention (don't leave a comment asserting something now known to be
   wrong).
4. Whichever path: the six gauges should end up visually clustered near
   the crosshair, roughly matching the proportions in
   `docs/reference-images/native-hud-reference.jpg` (a compact group, not
   spread to near the corners of a 680px-tall viewport) — a live
   screenshot comparison is the real bar, not just "the sign changed."

## Explicitly out of scope

- Redesigning the gauge textures/visuals themselves (the arcs, the bubble
  shapes) — this task is about *position*, not re-skinning. If achieving
  a truly native-faithful look also needs different-shaped gauge textures
  (e.g. the big sweeping arcs visible in the reference image, vs. this
  port's current small circular bubbles for every gauge), note that
  plainly as a separate follow-up rather than scope-creeping into a full
  texture redesign here.
- Any other HUD element (speed dial, roll indicator, weapon status list,
  kill bonus, etc.) — this task is scoped to the six `DrawGauges` gauges
  (Thrust/Fuel/Energy/Ammo/Hull/Shield) only.
- P368-P394 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: launch a real client, dock/undock or just fly,
  and take a screenshot of the HUD. Compare it directly against
  `docs/reference-images/native-hud-reference.jpg` and describe in the
  report how the new layout compares (closer cluster around the crosshair,
  no more "horns" sticking up past the top of the group) — an honest
  visual comparison, not just "the numbers changed."
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected (a pure layout/rendering change should
  not affect these at all — if it does, investigate before assuming it's
  unrelated).

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P395_HUD_GAUGE_LAYOUT.md` in the repo root: what the real
   root cause turned out to be (coordinate-convention fix, or a better
   native source found), and the live before/after screenshot comparison
   against the reference image.
