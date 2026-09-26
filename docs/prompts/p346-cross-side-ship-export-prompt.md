# P346 — Broadcast ship identity (EXPORT) cross-side, not just same-side

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Domingo's stated goal, live 2026-09-22: "we need to fix the cross side
visibility, the goal is to render all hulls for a nice picture" —
wanting to see all 72 distinct P287 ship models simultaneously in a
`vizfight roster` session, which spreads them across all 9 factions
(sides). Investigation this session found the real, precise gap —
**not** a missing feature from scratch, but one missing half of an
already-built one:

- **Already real, already landed, already tested** (roadmap:
  `~/Documents/Domingo-Llm-Wiki/pages/allegiance-fedmsg-roadmap.md`,
  search "P131"–"P139", all landed 2026-09-10): a full cross-side
  visibility foundation — `Sim/SideVisibilityState.cs`
  (`SetSideVisibility`/`GetCurrentEye` per model per side),
  `Sim/VisibilityMath.cs` (`InScannerRange`/`LineOfSightExist`),
  `Sector.ResolveCrossSideVisibility` (the real per-tick sweep, P132,
  called from `Sector.Update`), and **`Server.BroadcastShipsUpdate`**
  (P139) which already sends `HEAVY_SHIPS_UPDATE`(176)/
  `LIGHT_SHIPS_UPDATE`(175) to every connected player individually,
  **confirmed side-agnostic with real live cross-side tests** — every
  ship in the same cluster, any side, no `SeenBySide` gate (matching
  native's own `SendShipUpdates`, `FedSrv.CPP:3738-4216`, which has no
  such gate either — P138's own scope-correcting finding).
- **The gap**: `HeavyShipUpdateEntry`/`LightShipUpdateEntry`
  (`src/Allegiance.Core/Wire/HeavyShipsUpdate.cs`/`LightShipsUpdate.cs`)
  carry only **live telemetry** — `ShipId`, `Controls`, `Time`,
  `Position`, `Orientation`, `Velocity`, `TurnRates`, fractions, `Power`
  — confirmed by direct read, no `HullId`/`SideId`/name field
  anywhere. That's correct and matches native (position updates are
  cheap and frequent; identity is a separate, rare message). The
  **identity** message — `EXPORT` (carries `HullId`, name, the
  fields needed to even know what to render) — is currently **only
  ever broadcast same-side**: `Server.Broadcast.cs:461`, logged as
  `[teamexport]`, sent when a teammate's ship is first seen by other
  peers **on the same side**. There is no cross-side counterpart at
  all — confirmed by grep, zero other EXPORT-broadcast call sites.
  This is why, even once a client decodes `HEAVY_SHIPS_UPDATE`
  (P347, a separate already-scoped follow-up task), it still wouldn't
  know a foreign ship's hull — it would only have a `ShipId` and a
  position with no identity to resolve a model from.

## Scope

1. **Add a cross-side ship EXPORT broadcast**, triggered by the
   already-built P132 visibility sweep (`Sector.ResolveCrossSideVisibility`)
   transitioning a given (ship, side) pair from not-visible to visible
   — mirroring the existing same-side `[teamexport]` broadcast
   (`Server.Broadcast.cs:461` and its surrounding method) as your
   direct template: same `EXPORT`/`ExportShip` wire message shape,
   same "send when a ship first becomes visible to a recipient" event
   model, just triggered by the P132 cross-side sweep's own
   true-transition instead of the same-side join/spawn path.
2. **Detecting the transition**: `SideVisibilityState.SetSideVisibility`
   (`Sim/SideVisibilityState.cs:81-84`) does not itself report whether
   this call is a transition — the caller must read the prior value
   (`GetCurrentEye(sideId)`) before calling `SetSideVisibility` to know
   whether this is a genuine false→true edge. `Sector.ResolveCrossSideVisibility`
   (`Model/Sector.cs`, search for it) is the one place that already
   calls `SetSideVisibility` in its sweep loop — that's where the edge
   detection belongs, queuing a "newly visible" event for the host to
   turn into a real EXPORT broadcast after `Mission.Update` returns
   (matching this port's own established pattern of Core-side pending-
   event lists drained by the Server host — see `_pendingRipcordAborts`
   in `Model/Sector.cs` for a direct precedent to copy, or
   `_pendingStationRescues`/`ResolveRescueEvents`'s pending-list-drain
   shape, same file).
3. **Send target**: the newly-seeing SIDE's connections only (not
   whole-mission, not the ship's own side) — same "per-side send
   target" distinction `STATIONS_UPDATE`(P133)'s own commit message
   called out explicitly as a real, confirmed choice, not copied
   blindly from a whole-mission sibling.
4. **What to send**: the same `ExportShip`/EXPORT wire shape the
   existing same-side path already builds — real `HullId`, real name,
   whatever else that message already carries for a same-side export;
   don't invent a new leaner message, reuse the existing codec.

## Explicitly out of scope this task

- **Client-side decoding of any of this** — `HEAVY_SHIPS_UPDATE`/
  `LIGHT_SHIPS_UPDATE` position telemetry AND the new cross-side EXPORT
  this task adds — is a separate, already-scoped follow-up
  (`~/p347-cross-side-client-render-prompt.md`, queued right behind
  this one). This task is server-broadcast-only; verify with a real
  loopback `Bot`/`FltBot` (already confirmed to decode both `EXPORT`
  and `HeavyShipsUpdate`/`LightShipsUpdate`, `Allegiance.Bot/Bot.cs:63-64,783-787`
  per the roadmap), not the MonoGame client.
- **`SideVisibilityState`/`VisibilityMath`/`Sector.ResolveCrossSideVisibility`'s
  own existing logic** — don't change the visibility DETERMINATION
  (scanner range, line-of-sight, the sweep cadence) — only add the
  edge-triggered EXPORT send on top of what it already correctly
  computes.
- **Any change to the same-side `[teamexport]` path** — it stays
  exactly as-is; this is a genuinely separate, additional broadcast,
  not a replacement or refactor of the existing one.
- **`vizfight`/`Allegiance.ClientCore.MonoGame`** — this is a pure
  `Allegiance.Server`/`Allegiance.Core` task.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: a real loopback test (or `vizfight` roster/
  explore session) with two ships on DIFFERENT sides in the same
  cluster — confirm each receives a real EXPORT for the OTHER once
  P132's sweep marks them mutually visible (real scanner range/line-
  of-sight, not both spawned already-visible with no transition to
  observe), with the correct `HullId` for each. Confirm a SAME-side
  teammate still gets the existing `[teamexport]` path unchanged
  (no regression, no duplicate double-export).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (failing test names vary run to run — that's the known
  signature, see recent roadmap entries for the current flake list).

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. A short final summary: where the transition-edge detection landed,
   the send-target confirmation, the live cross-side EXPORT proof
   performed, build/test status.
