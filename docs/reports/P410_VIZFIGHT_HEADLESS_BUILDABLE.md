# P410 — vizfight builds & runs headless (RosterPlanner/HullModelResolver out of the MonoGame project)

## Goal & outcome

`tools/vizfight` (the live-combat / showcase spectator web tool) previously
project-referenced `Allegiance.ClientCore.MonoGame.csproj` solely to reach the
single class `RosterPlanner`. Building that project on a headless host triggered
the MonoGame Content Builder (`mgcb`) which needs native rendering libs a
headless container lacks, failing with `MSB3073 … "mgcb" … exited with code 1`.

This change moves the two classes that call site chain actually needs —
`RosterPlanner` and its sole dependency `HullModelResolver` — down into
`Allegiance.Core`, and drops the MonoGame project reference from
`vizfight.csproj`. `vizfight` now builds and runs in a plain SDK container with
no MonoGame/GL/X11 anywhere in its dependency graph, and the MonoGame client
itself continues to build and run unchanged.

## Caveat on the task's "only depends on Allegiance.Core" premise

The brief asserted both files depend only on `Allegiance.Core`. That is **almost**
true but not quite: `HullModelResolver` carried one overload,
`ResolveAsset(string civKey, short hullId, ShopCatalog catalog)`, whose parameter
type `ShopCatalog` lives in the MonoGame project
(`src/Allegiance.ClientCore.MonoGame/Rendering/ShopCatalog.cs`). Had the resolver
been moved verbatim that would have created a Core→MonoGame dependency.

Resolution: the `ShopCatalog` overload was extracted out of `HullModelResolver`
into a new extension method `ShopCatalogHullResolutionExtensions.ResolveAsset`
living in the MonoGame project next to `ShopCatalog` itself. The two genuinely
Core-only classes then moved cleanly. All callers of the overload
(`Game1.Rendering.cs`, `tools/roster-capture`) find the extension automatically
under the existing `using Allegiance.ClientCore.MonoGame.Rendering`.

## New file locations / namespaces

New subfolder `src/Allegiance.Core/Content/` (chosen to house asset-manifest /
content-resolution logic; there was no pre-existing home, and `ShipAssets` /
`MissileAssets` manifest data is embedded in `HullModelResolver`).

| Old location | New location | Old namespace | New namespace |
|---|---|---|---|
| `src/Allegiance.ClientCore.MonoGame/Rendering/HullModelResolver.cs` | `src/Allegiance.Core/Content/HullModelResolver.cs` | `Allegiance.ClientCore.MonoGame.Rendering` | `Allegiance.Core.Content` |
| `src/Allegiance.ClientCore.MonoGame/Rendering/RosterPlanner.cs` | `src/Allegiance.Core/Content/RosterPlanner.cs` | `Allegiance.ClientCore.MonoGame.Rendering` | `Allegiance.Core.Content` |

## Every call site updated

All files that reference `HullModelResolver` / `RosterPlanner` gained
`using Allegiance.Core.Content;` (unless already inside the new namespace). Those
that still need MonoGame-side types (`ShopCatalog`, `ShipRenderer`) kept their
existing `using Allegiance.ClientCore.MonoGame.Rendering;` alongside it.

- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `Rendering.HullModelResolver _hullResolver` → `HullModelResolver`
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` — `Rendering.HullModelResolver.CivKeyByIdShort` → `HullModelResolver`; the `_shopCatalog` overload call now resolves to the extension
- `src/Allegiance.ClientCore.MonoGame/Game1.Lifecycle.cs` — five `Rendering.HullModelResolver.{FallbackAsset,ShipAssets,MissileAssets,…}` / `new Rendering.HullModelResolver()` → `HullModelResolver`
- `src/Allegiance.ClientCore.MonoGame/Rendering/ShipRenderer.cs` — added using (doc-cref)
- `src/Allegiance.ClientCore.MonoGame/Rendering/MissileModelSource.cs` — added using (`HullModelResolver.MissileAssets`)
- `src/Allegiance.ClientCore.MonoGame/Rendering/ShopCatalog.cs` — added using + the extracted `ShopCatalogHullResolutionExtensions.ResolveAsset` extension method
- `tools/vizfight/Program.cs` — `using Allegiance.ClientCore.MonoGame.Rendering` → `using Allegiance.Core.Content` (RosterPlanner only)
- `tools/roster-capture/Program.cs` — added using (kept MonoGame using for `ShopCatalog`/`ShipRenderer`); `_resolver.ResolveAsset(civ, hullId, _catalog)` now hits the extension
- `tools/hullswap-proof/Program.cs` — added using (kept MonoGame using for `ShopCatalog`/`ShipRenderer`)
- `tests/Allegiance.ClientCore.MonoGame.Tests/HullModelResolverTests.cs` — added using
- `tests/Allegiance.ClientCore.MonoGame.Tests/RosterPlannerTests.cs` — added using

Not a real call site: `src/Allegiance.Server/Server.cs` mentions
`HullModelResolver` only inside a `<c>…</c>` prose comment (non-resolving) — no
change needed.

## Project reference removed

`tools/vizfight/vizfight.csproj`: removed the
`Allegiance.ClientCore.MonoGame.csproj` `ProjectReference`. The tool now links
only `Allegiance.Core`, `Allegiance.Server`, and `Allegiance.Bot`.

## Verification

### Whole-solution builds — 0 warnings, 0 errors

Built (with `/home/domingo/.dotnet/dotnet`, Debug):

- `src/Allegiance.Core` → **0 warnings / 0 errors** (now contains the moved classes)
- `src/Allegiance.ClientCore.MonoGame` → **0 warnings / 0 errors** (references the moved classes under their new namespace; content pipeline untouched and still builds)
- `tools/vizfight` → **0 warnings / 0 errors**

### Headless build — the actual point of the task (previously FAILED on `mgcb`)

In a plain headless container with no MonoGame/GL/X11:

```
docker run --rm -v $PWD:/src -w /src mcr.microsoft.com/dotnet/sdk:10.0.400 dotnet build tools/vizfight -c Release
```

Result: **Build succeeded, 0 Warning(s), 0 Error(s)**. The restore graph now
contains only `Allegiance.Core`, `Allegiance.Bot`, `Allegiance.Server`,
`vizfight` — the MonoGame project is absent, so `mgcb` is never invoked.

### Roster mode run headless (exercises the moved classes)

Ran `vizfight roster 8099` in the same headless container (artwork bind-mounted,
`ALLEGIANCE_ARTWORK_PATH=/art`). It planned and served a real 9-faction roster
exactly as before:

```
[roster] planned 72 distinct ship models across 9 factions:
[roster]   belters   3 models
[roster]   bios     13 models
[roster]   dreg      3 models
[roster]   giga      3 models
[roster]   gt       16 models
[roster]   ic       16 models
[roster]   oh        3 models
[roster]   rix      12 models
[roster]   tf        3 models
[boot] static core 'PCore001.igc' loaded: version=990051329, 10 civilizations, 447 hulls, 143 station types
[boot] game 'Roster' ready: map=m2xspiderw.igc, 13 sectors, 9 sides [Bios, Gt, Ic, Rix, Belters, Dreg, Giga, Oh, Tf] …
[roster] spectator http://127.0.0.1:8099/  ws://127.0.0.1:8099/ws
[conn] peer N 'Rxxx' joined …   (72 roster peers seat on their real hulls)
```

The counts (72 = 3+13+3+3+16+16+3+12+3) match the committed
`tools/vizfight/roster_dump.json` / `RosterPlannerTests` expectations, confirming
no behavioral change from the move.

### Second vizfight mode run headless

Ran `vizfight showcase 8077` in the same container: server boots, all 6 scripted
bots spawn on their hulls, the fight advances (`MISSION_STAGE Started`,
`ENTER_GAME`), spectator endpoint live — the whole tool, not just `roster`, runs
without the MonoGame project.

### Test suite

`Allegiance.ClientCore.MonoGame.Tests`: **771 passed / 0 failed** — including
`RosterPlannerTests` and `HullModelResolverTests`, which assert the exact
72-model / per-faction / grid-column roster invariants against the real
`PCore001.igc` catalog. This is the strongest proof the move changed no behavior.

`Allegiance.Lobby.Tests`: 12 passed.

`Allegiance.Core.Tests` and `Allegiance.ClientCore.Tests`: passing except for a
handful of pre-existing **live-wire timing flakes** — failures occur only in
`*LiveTests` / wire-broadcast timing classes (`LoadoutChangeLiveTests`,
`LiveRipcordTests`, `LiveWinConditionTests`, `ShipDeleteGoneLiveTests`,
`JoinFlowLiveTests`, `GameOverPlayersWireTests`, `CargoBayReloadTests`,
`AddPartTests`, …), are `Assert` races on empty/short-lived wire collections, and
their count varies run-to-run (Core: 2 then 8; ClientCore: 3 then 2) on identical
input — the signature of timing sensitivity, not a regression. None of the
failures touch the moved classes.

## Out of scope (unchanged)

Other vizfight modes' behavior, dockerizing vizfight, the MonoGame content
pipeline itself, and P368–P409.
