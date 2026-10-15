# P410 — move HullModelResolver/RosterPlanner out of the MonoGame project so vizfight builds headless

## Why this task exists

Domingo wanted to run `tools/vizfight` (the live-combat/showcase spectator
web tool) on the deploy host (192.168.0.221) to view it from a browser.
`vizfight.csproj` project-references `Allegiance.ClientCore.MonoGame.csproj`
(the desktop client), and building that project on a headless host (a
one-off `mcr.microsoft.com/dotnet/sdk:10.0.400` container, no X11/GL) fails:

```
error MSB3073: The command ""dotnet" "mgcb" /quiet /@:".../Content.mgcb"
/platform:DesktopGL ..." exited with code 1.
```

MonoGame's Content Builder (`mgcb`) tries to compile the client's textures/
models at build time and needs native rendering libraries a headless
container doesn't have. Confirmed live on 221 this session.

## Confirmed real fix — the dependency is accidental, not structural

Traced exactly why `vizfight.csproj` needs the MonoGame project at all:
it's a project reference purely to reach **one class**,
`RosterPlanner` (`src/Allegiance.ClientCore.MonoGame/Rendering/
RosterPlanner.cs`), used only by vizfight's `roster` display mode.
`RosterPlanner`'s own doc comment already says what it is: "Pure P305
roster-planning logic (headless-testable; no GraphicsDevice)." It in turn
depends on exactly one other class, `HullModelResolver`
(`.../Rendering/HullModelResolver.cs`).

**Confirmed, both files' only real dependency is `Allegiance.Core`** — no
MonoGame/XNA types anywhere in either file (checked every `using` and
every cross-reference; the only other name `RosterPlanner` uses,
`CivModelPrefixes`, is a `static readonly` field defined inside
`RosterPlanner` itself, not a separate class). They live in
`Allegiance.ClientCore.MonoGame` for historical/organizational reasons
only, not because they need anything from it.

## Scope

1. Move `HullModelResolver.cs` and `RosterPlanner.cs` from
   `src/Allegiance.ClientCore.MonoGame/Rendering/` into
   `src/Allegiance.Core/` (pick a sensible subfolder — e.g. a new
   `Allegiance.Core/Content/` or alongside wherever similar asset-manifest
   logic already lives; check `HullModelResolver`'s own references to
   `ShipAssets`/manifest data for a natural home). Update their namespace
   from `Allegiance.ClientCore.MonoGame.Rendering` to the real new
   location's namespace.
2. Update every other real call site to the new namespace (confirmed via
   grep this session — verify yourself, don't trust this list blindly):
   `src/Allegiance.ClientCore.MonoGame/Rendering/MissileModelSource.cs`,
   `Game1.Rendering.cs`, `Game1.cs`, `Game1.Lifecycle.cs`,
   `Rendering/ShipRenderer.cs`, and `tools/vizfight/Program.cs`.
3. Remove the `Allegiance.ClientCore.MonoGame.csproj` project reference
   from `tools/vizfight/vizfight.csproj` entirely (it should no longer be
   needed — confirm by building).
4. `Allegiance.ClientCore.MonoGame` itself keeps working unchanged (it
   already references `Allegiance.Core`, so the moved classes are still
   reachable from it — just via the new namespace).

## Explicitly out of scope

- Any other vizfight mode's actual behavior (explore/showcase/roster/
  proof/stress) — untouched, this is purely a namespace/project-reference
  move.
- Dockerizing vizfight with its own Dockerfile/compose entry — explicitly
  not requested; the goal here is just "buildable and runnable headless,"
  which unblocks running it via a plain SDK container or a future
  Dockerfile equally.
- Any change to `Allegiance.ClientCore.MonoGame`'s content pipeline itself
  — untouched; the point is that `vizfight` no longer triggers it at all.
- P368-P409 — unrelated, don't touch.

## Verification — real proof, not just build/test

- Whole-solution build: 0 warnings, 0 errors (confirms the MonoGame client
  itself still builds correctly with the moved classes referenced under
  their new namespace).
- **Required, the actual point of this task**: build and run `vizfight`
  in a plain headless container with NO MonoGame/GL/X11 dependencies —
  e.g. `docker run --rm -v <repo>:/src -w /src
  mcr.microsoft.com/dotnet/sdk:10.0.400 dotnet build tools/vizfight` (or
  the equivalent `dotnet run`) succeeds cleanly where it previously failed
  on the `mgcb` step. Confirm the `roster` mode specifically still works
  correctly (it's the one that actually exercises the moved classes) —
  run it and confirm a real roster is planned/served exactly as before.
- Run at least one other vizfight mode (e.g. `showcase`) headless too,
  confirming the whole tool — not just `roster` — now builds and runs
  without the MonoGame project in its dependency graph.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The move + all call-site updates + the dropped project reference,
   committed on `master` (not pushed — Domingo pushes).
2. Report doc `P410_VIZFIGHT_HEADLESS_BUILDABLE.md` in the repo root:
   exact new file locations/namespace, every call site updated, and the
   headless build+run verification performed.
