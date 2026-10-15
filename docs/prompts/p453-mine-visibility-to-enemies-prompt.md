# P453 — enemies can never see or scan-spot a minefield

## Why this task exists

A gameplay-parity pass found that in the port, a minefield is only ever sent
to the side that laid it. The enemy side never learns a field exists, even
with a scanner sitting on top of it. In the original game, mines are ordinary
side-visible objects. A side sees an enemy field once its own scanners pick
it up, which is how pilots route around fields or send someone to clear them.

- **Native mines are side-visible, predictable static objects.** They are
  created with `c_mtSeenBySide | c_mtPredictable | c_mtHitable | c_mtStatic`
  and `SetSignature(dataMineType->signature)`
  (`~/Development/Allegiance/src/Igc/mineigc.cpp:103-107`). So they take part
  in the normal per-side visibility sweep (`UpdateSideVisibility`) as
  targets.
- **Native exports a newly visible predictable object to the side that now
  sees it.** `ThingSiteImpl::ShowObject` (`FedSrv/FedSrv.CPP:10749-10778`):
  for a `c_mtPredictable` model, once a side can see it, "Tell everyone on the
  side about the newly visible static object" → `ExportObj(pmodel, type,
  NULL)` to that side's group. Separately, `fsmission.cpp:4232-4240` exports
  every mine a side `SeenBySide`s when that side's players enter the cluster.
- **Port.** Mines are left out of the visibility sweep, and their export is
  same-side only:
  - `Sector.ResolveCrossSideVisibility` (`src/Allegiance.Core/Model/Sector.cs:3240-3300`)
    adds ships, probes, stations and asteroids to the sweep, but **not
    mines**.
  - `BroadcastExportMine` (`src/Allegiance.Server/Server.Combat.cs:485+`, the
    audience described at `:359-406`) sends only to the layer's own peer plus
    same-side teammates.
  - Mine destruction (`BroadcastMineDestroyed`, wired at
    `Server.Lifecycle.cs:674`) reaches the same same-side audience.
  - `DataMineTypeIGC.Signature` is already decoded
    (`src/Allegiance.Core/Data/DataMineTypeIGC.cs:30`).
- **The precedent to copy is P132/P134.** P134 added asteroids to the sweep
  as target-only participants with no side; mines are the same idea but
  *with* a side. Probes already carry `SeenBySide` and a per-side audience
  (`Server.Broadcast.cs:1305-1390`, BroadcastProbesUpdate). The client
  already renders mines from EXPORT/OT_mine
  (`src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs:757-780`, P291) and
  applies MINE_DESTROYED.

## Scope

1. **Make mines visibility participants.** Add `Mine` to
   `ResolveCrossSideVisibility` as a **target** with its side, its position,
   its radius (the same field P452 settles on) and `MineType.Signature`.
   It is *not* a scanner: native mines aren't `c_mtScanner`. Give `Mine` the
   side-visibility state the other participants use (`SideVisibilityState`,
   `SeenBySide`), following whichever pattern `Probe`/`Station` use. The
   owner side always sees its own mines, as today.
2. **Export on first sighting.** When the sweep first flips a mine to seen
   for a side that isn't its owner, send that side's seated peers the same
   EXPORT(26)/OT_mine that `BroadcastExportMine` builds. Send it once per
   (side, mine), latched like the other one-shot exports. Keep the laying
   side's existing immediate export unchanged.
3. **Destruction reaches everyone who got the export.** Extend
   `BroadcastMineDestroyed`'s audience to include every side that was sent
   the mine. A client must never keep a phantom field after the mine is
   destroyed or expires.

## Explicitly out of scope

- The minefield damage model. That's P452, which should land first. If P452
  changed which radius field a mine uses, use the same one here.
- Periodic per-side mine health or strength updates (no MINES_UPDATE-style
  batch). Only first-sighting export and destruction.
- "Hidden again" semantics. Native doesn't un-export predictable static
  objects when sight is lost, so don't add removal on loss of sight.
- Client rendering changes. The existing mine renderer should just work on
  the enemy side. Confirm this, and fix only if it filters by side.
- Enemy-ship spotting reports (`ShipStatusSpotted`), which is a named
  follow-up. Also out: station capture (P450), station alerts (P451) and
  teleport probes (P454).
- Named follow-ups from the same parity pass, not scoped yet: armed probes
  (gun towers) and the in-flight turret seat switch.

## Verification

- **A wire-level test that fails on the unfixed code first.** Two loopback
  clients are on different sides in the same sector. Side A lays a mine.
  - With side B's ship out of scanner range of the mine (or the mine's
    signature too small at that distance), B's `ClientWorld.Mines` does
    **not** contain it.
  - Move B's ship into scanner range. B receives EXPORT/OT_mine exactly once,
    and the mine appears in B's `ClientWorld.Mines`. Record in the report
    that this part fails before the fix.
  - Destroy or expire the mine. B receives the destroy message and the mine
    leaves B's `ClientWorld.Mines`.
- A core-level test that a mine gets `SeenBySide(otherSide) == true` only
  when an enemy scanner is in range with line of sight, and that it is never
  a scanner itself.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Bare `dotnet` is a broken snap stub; always use the full
  path.
- Full test suite via `/home/domingo/.dotnet/dotnet test`, 3 consecutive
  clean runs. Only the pre-existing documented live-wire timing flakes are
  acceptable, and you must name any you see.

## Deliverables

1. The change plus tests, committed on **`master`** (not a side branch, not
   pushed; Domingo pushes). Check `git branch --show-current` before
   committing.
2. Report doc `docs/reports/P453_MINE_VISIBILITY_TO_ENEMIES.md`. Include the
   pre-fix failing test evidence, how mines joined the sweep, the export
   latch, the destroy audience, and whether the client renderer needed any
   change.
