# P347 — MonoGame client: decode cross-side ships, render + target them

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Direct continuation of `~/p346-cross-side-ship-export-prompt.md`
(should land first — this task assumes its server-side cross-side
EXPORT broadcast is already on `master`). Domingo's goal: "we need to
fix the cross side visibility, the goal is to render all hulls for a
nice picture" — seeing all 72 distinct P287 ship models (spread across
9 sides in a `vizfight roster` session) simultaneously in the real
MonoGame client, not just the spectator webpage (which already shows
all 72 as 2D icons by reading server state directly, bypassing the
client entirely).

**The real, confirmed gap** (both halves checked this session by
direct grep, zero hits either way): `Allegiance.ClientCore`/
`Allegiance.ClientCore.MonoGame` never decode `HeavyShipsUpdate`/
`LightShipsUpdate` (`FM_S_HEAVY_SHIPS_UPDATE`(176)/
`FM_S_LIGHT_SHIPS_UPDATE`(175)) at all — **only** the test-harness
`Allegiance.Bot` does (`Bot.cs:63-64,783-787`, built alongside P139 as
this port's stand-in "reference client-side consumer", confirmed
correct and complete by the roadmap's own P139 entry). Today,
`ClientWorld.cs`'s `world.Ships` (what `Game1.cs` renders and offers as
targets, via `Apply(SingleShipUpdate)`/`Apply(ExportShip)`,
`ClientWorld.cs:604-609`/`~624+`) is populated **only** from the
same-side-only `SingleShipUpdate`/`ExportShip` messages — cross-side
ship data, even after P346 makes the server actually send it, never
reaches this structure at all.

## Scope

1. **Add wire decode support** for `HeavyShipsUpdate`/`LightShipsUpdate`
   in whatever this port's client-side message-dispatch layer already
   uses for every other inbound message (find the existing pattern —
   likely near wherever `SingleShipUpdate`/`ShipStatusMessage` are
   currently decoded/dispatched into `ClientWorld.Apply(...)` calls).
   `Allegiance.Bot/Bot.cs:63-64,783-787` is a real, working reference
   for the decode shape (though `Bot` just stores raw messages in a
   list for test assertions — this task needs the REAL consumption
   path, merging into live client state, not `Bot`'s test-capture
   shape).
2. **Merge into `ClientWorld`**: for each `HeavyShipUpdateEntry`/
   `LightShipUpdateEntry` (real fields: `ShipId`, `Controls`, `Time`,
   `Position`, `Orientation`, `Velocity`, `TurnRates`, fractions,
   `Power` — see `src/Allegiance.Core/Wire/HeavyShipsUpdate.cs`/
   `LightShipsUpdate.cs` for the exact accessors), `Upsert(shipId)` and
   update `.Latest`/`.LastUpdateTick` the same way
   `Apply(SingleShipUpdate)` already does (`ClientWorld.cs:604-609`) —
   this is a live-position refresh, same shape, just a different
   source message and a wider (cross-side) audience. **A cross-side
   ship's identity (`HullId`, name, `SideId`) comes from P346's new
   cross-side `EXPORT` broadcast, not from this message** — make sure
   whatever `Apply(ExportShip)` already does for identity fields
   (`ClientWorld.cs:624`+) applies equally regardless of which side
   sent it; don't gate identity application on same-side-ness anywhere
   you find it.
3. **Confirm the render/targeting path needs no further change** — 
   `Game1.cs`'s pose-gather loop (`Game1.cs:~1782`, already fixed by
   P344 to skip `IsDead`), `CollectTargetCandidates`
   (`Game1.cs:~4915`), and `ResolveShipModel`/`ResolveShipTexture`
   (which resolve civ via `CivKeyForShipSide`, `Game1.cs:~5247`+, keyed
   off each ship's own real `SideId` from its roster entry — already
   correct per-ship, not assuming the viewer's own side) all iterate
   `world.Ships` generically already — once cross-side ships are really
   in that dictionary with real `HullId`/`SideId`, they should render,
   target, and resolve the CORRECT per-civ model/texture automatically,
   with no further Game1.cs change needed. Verify this is actually
   true rather than assuming it; fix anything that turns out to
   silently assume same-side-only (e.g. any leftover comment-driven
   assumption from before this feature existed).

## Explicitly out of scope this task

- **P346's server-side broadcast work** — assumed already landed;
  don't re-implement or second-guess it, just consume what it sends.
  If it hasn't actually landed yet when you start, stop and say so
  rather than building against a guessed wire shape.
- **Any HUD/UI change for distinguishing friend from foe visually** —
  not asked for; a Bios ship and a foreign ship both render with their
  own correct civ skin already (via `ResolveShipTexture`'s existing
  per-ship `SideId`-keyed resolution) — that's enough differentiation
  for this task's "see all the hulls" goal. Don't add IFF coloring,
  hostile-highlighting, or radar changes.
- **`vizfight`'s own `--sameSide` or civ-filter flags** — a
  session-earlier, now-abandoned dead end (forcing every bot onto one
  side breaks per-civ model/texture resolution, confirmed and reverted
  live this session) — this task makes that workaround unnecessary
  entirely; don't resurrect it.
- **Cloak visibility, scanner-range HUD indicators, or any other
  detection-adjacent UI** — out of scope; this task is purely "make
  cross-side ships that the server now legitimately sends actually
  show up," nothing about how detection itself is presented.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: `vizfight roster <port>` (no civ filter —
  the full 72-hull, 9-side session), connect a real MonoGame client as
  the existing Bios-seated viewer, and confirm ships from OTHER
  factions (not just the 13 Bios ones) now render with their correct
  distinct models/textures and are `T`-targetable. A full screenshot
  or description confirming multiple factions' hulls are simultaneously
  visible is the actual deliverable Domingo asked for ("render all
  hulls for a nice picture").
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (failing test names vary run to run — that's the known
  signature).

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. A short final summary: the decode/merge approach, confirmation the
   render/targeting path needed no changes (or what it needed), the
   live full-roster proof performed, build/test status.
