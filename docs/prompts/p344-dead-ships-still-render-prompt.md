# P344 — Dead ships keep rendering forever (ghost ships)

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Live flight-testing today (2026-09-22) against a `vizfight roster`
72-bot session found a real bug: a same-side ship that died (in this
case, one of the roster's Bios `Lifepod` bots, killed by its own
oxygen-exhaustion timer — a real, working, unrelated mechanic, see
`~/Development/AllegianceCore/src/Allegiance.Core/Sim/Ship.cs`'s
oxygen pass) stayed visible in the 3D client indefinitely: a frozen,
untextured/black hull sitting in the middle of the player's own-side
formation. Domingo's own words tracing the investigation live: "i
targeted the ship" / "it doesn't show with any ship actually" / "no,
it's grouped with my own Bios ships" — confirming the ghost was
same-side (so not a cross-side-visibility gap) yet permanently
untargetable.

**Root cause, confirmed by direct code reading — this is real, not a
guess:**

- `ClientWorld.Apply(KillShip m)` (`src/Allegiance.ClientCore/State/ClientWorld.cs:1078-1086`)
  deliberately keeps a dead ship's entry in `world.Ships`, only
  flipping `IsDead = true` — correct, intentional design (the comment
  explains why: "did ship X die" stays answerable by id).
- Every code path that offers a ship as a target correctly excludes
  dead ships: `CollectTargetCandidates` (`Game1.cs:4915`,
  `if (id == me || ship.IsDead || ship.Latest is not { } up) continue;`)
  and `ResolveTargetPosition` (`Game1.cs:5112`,
  `... && !ship.IsDead`).
- **But the actual per-frame RENDER pose-gather loop — the one that
  populates `_poses`/`_shipModelById`/`_shipTextureById`/`_shipScaleById`
  for every other ship, `Game1.cs:1782-1784`** — has no such check:
  ```csharp
  foreach (var (id, ship) in world.Ships)
  {
      if (id == world.MyShipId || ship.Latest is not { } up) continue;
      _poses[id] = new Flight.PredictedPose(up.Position, up.Orientation);
      ...
  ```
  A dead ship still has a non-null `ship.Latest` (its last real
  position update before dying), so it sails straight through this
  filter and gets drawn every single frame, forever, frozen at its
  last known pose — visible but permanently unable to be targeted
  (correctly excluded there), matching exactly what was observed live.

The specific "fully black" look is very likely a secondary rendering
symptom of a frozen, un-updating pose under the scene's fixed default
lighting from certain angles (nothing resets the dead ship's
`SideId`/`HullId`/texture on death — only `IsDead` changes, confirmed
by reading the same `Apply(KillShip m)` method) — not something that
needs separate root-causing. Once dead ships are excluded from the
render loop the same way they're already excluded from targeting, the
whole ghost-ship symptom (frozen, black, untargetable) goes away
together.

## Scope

1. **`Game1.cs:1782-1784`** (the per-ship pose-gather `foreach` loop
   over `world.Ships`) — add `ship.IsDead` to the existing skip
   condition, exactly matching the guard `CollectTargetCandidates`
   already uses one screen down:
   ```csharp
   if (id == world.MyShipId || ship.IsDead || ship.Latest is not { } up) continue;
   ```
2. Check for **any other per-ship rendering loop in `Game1.cs`** that
   iterates `world.Ships` (or reads `_poses`/`_shipModelById`/etc. built
   from it) and ALSO lacks an `IsDead` check — the damage-emitter
   gather (`GatherDamageEmitter`, right below the line you're fixing)
   and cloak-visual tracking sit in the same loop body, so fixing the
   loop's own top-level `continue` guard should cover them for free,
   but confirm this rather than assuming it — a separate loop
   elsewhere (radar blips, scoreboard, starmap icons, etc.) may have
   the identical omission and need the identical one-line fix. Fix
   every genuine instance you find; don't stop at the first one if the
   pattern repeats.
3. Confirm the own-ship path is unaffected — a player's OWN death
   already goes through `GAME_OVER`/respawn handling separately from
   this ghost-ship issue, out of scope here; don't touch anything
   gated on `id == world.MyShipId`.

## Explicitly out of scope this task

- Why the ghost specifically renders **black** rather than some other
  stale appearance — not required; the fix is to stop rendering dead
  ships at all, which eliminates the symptom regardless of its exact
  shading mechanism. Don't go hunting for a separate lighting/texture
  bug here.
- Any change to `ClientWorld.Apply(KillShip m)` or the entity-table
  keep-dead-ships-by-id design — that's correct, deliberate, documented
  design; the bug is purely that one rendering loop forgot to respect
  the flag other code paths already respect.
- The `vizfight roster` lifepod-oxygen-timeout mechanic itself — a
  real, working, unrelated feature; don't touch it.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: `vizfight roster <port>` (a small civ
  filter works fine too — `roster <port> bios`), connect a real
  MonoGame client, let a lifepod bot die from oxygen exhaustion (or
  trigger any other in-view same-side ship death you can arrange), and
  confirm the ship visually disappears from the client the moment it
  dies — no lingering frozen/ghost hull.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (the specific failing test NAMES vary run to run — that's
  the known signature; a fix here should not introduce a new
  consistently-failing test).

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. A short final summary: every loop you found and fixed (not just the
   one cited above, if more existed), the live-proof performed,
   build/test status.
