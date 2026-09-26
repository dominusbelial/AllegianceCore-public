# P345 — Add a HUD bracket for the player's own `T`/`G` target selection

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Live flight-testing today (2026-09-22), Domingo's own words: "also
there is no visual feedback that the target is selected... the square
thing should be there" — referring to the missile-lock target bracket
(P230) that already exists for a *different* targeting concept, and
correctly expecting the same kind of visual for the player's own
click-and-go autopilot target (P212, `T` to pick a target, `G` to
engage autopilot toward it).

**Confirmed by direct code reading**: `_autoTarget` (the T-selected
target, `Game1.cs:390`) currently has exactly one piece of feedback —
the window title bar (`RefreshWindowTitle`, `Game1.cs:~5170`,
`Window.Title = _autoPilotEngaged && _autoTarget is { } t ...`) — and a
console log line on selection/engage. **No in-viewport HUD element at
all.** This is a completely separate system from the missile lock
bracket:

- Missile lock: `world.MissileLock` (server-authoritative,
  `MISSILE_LOCK_UPDATE(204)`), rendered via `ComputeLockBracket`
  (`Game1.cs:4702-4725`) → `_lockBracket` (`LockBracketView?`,
  `Game1.cs:404`) → drawn by `_hudRenderer.Draw(...)`
  (`Game1.cs:3675`). Its bracket style/color is driven by
  `MissileLockFeedback.Classify(lockState.LockFraction)` — a *lock
  progress* concept (charging → full lock) that doesn't apply here.
- Player autopilot target: `_autoTarget` (purely client-side, an
  instant pick from `CollectTargetCandidates`/`CycleTarget`, no
  server-side "lock" concept at all) — has no equivalent projection/
  draw path.

## Scope

1. **Add a projection method** for `_autoTarget`, mirroring
   `ComputeLockBracket`'s exact math (`Game1.cs:4702-4725`): resolve
   the target's live world position (reuse the existing
   `ResolveTargetPosition(AutopilotTarget, out SimVec)`,
   `Game1.cs:~5091-5097` — already handles all four `TargetKind`s:
   Ship/Station/Asteroid/Warp), transform by `view*proj` relative to
   `cameraEye`, reject behind-camera/out-of-frustum, convert to
   viewport pixel coordinates — same pattern, new method (don't
   overload/repurpose `ComputeLockBracket` itself; keep the two
   concepts textually separate since they can be simultaneously
   active and visually distinct).
2. **Give it a plain view struct** (no lock-progress state to encode —
   just screen position, plus whatever `HudRenderer` needs to draw a
   simple bracket: color, maybe a "selected" vs "engaged" visual
   distinction is a nice-to-have if it falls out naturally, but the
   core requirement is just: a visible box/bracket appears at the
   selected target's screen position). Look at how `LockBracketView`
   is defined and how `HudRenderer` consumes it
   (`_hudRenderer.Draw(frame, _cameraMode, GraphicsDevice.Viewport,
   _lockBracket, _leadIndicator, ...)`, `Game1.cs:3675`, and the
   `HudRenderer`/`HudFrame` types) to decide the minimal-diff way to
   add a second bracket parameter without disturbing the existing
   missile-lock rendering path.
3. **Wire it into the existing per-frame update** where
   `_lockBracket = ComputeLockBracket(...)` is currently set
   (`Game1.cs:2346`) — compute and store the new target-selection
   bracket alongside it, null when `_autoTarget` is null or doesn't
   project into view (same pattern as the lock bracket's own
   null-handling).
4. **Visual distinction from the missile-lock bracket is required** —
   they can both be on screen at once (you can have an autopilot
   target selected AND a separate missile lock on something else, or
   even the same object) and must be told apart at a glance. A
   different color and/or shape/style is enough; don't overthink this,
   just don't make them visually identical.

## Explicitly out of scope this task

- Any change to the missile-lock system itself (`ComputeLockBracket`,
  `MissileLockFeedback`, `world.MissileLock`) — read it for the
  pattern, don't modify it.
- P344 (dead ships still rendering) — a separate, already-scoped task;
  if you're picking both up in the same session, treat them as
  independent commits, don't conflate the two fixes into one.
- Any settings/toggle to disable the new bracket — not asked for.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: fly a real MonoGame client, press `T` to
  select a target, confirm a bracket appears at its screen position
  and tracks it as it (or the camera) moves; confirm it disappears
  when the target goes out of view/frustum and reappears when it comes
  back; confirm it's visually distinguishable from the missile-lock
  bracket if both are shown at once; confirm pressing `T` again to
  cycle targets moves the bracket to the new target.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (failing test names vary run to run — that's the known
  signature).

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. A short final summary: the visual style chosen and why, the live
   proof performed, build/test status.
