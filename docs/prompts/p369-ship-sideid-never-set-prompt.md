# P369 — ship model/civ shows wrong: `ShipEntity.SideId` silently defaults to 0 instead of the real side

## Why this task exists

Live 4-client playtest (2026-09-24, real remote server on 192.168.0.221,
4 real MonoGame clients across 2 real sides): the local player's own
ship, AND some remote ships, render with the wrong hull/civ model —
confirmed as a real data bug, not a civ-art difference, via two
independent pieces of live evidence:

1. **Domingo4 (Bravo/Ga'Taraan) and Domingo2 (also Bravo/Ga'Taraan,
   same real `SideId`+`HullId`=13) showed DIFFERENT rendered models to
   the same viewer (Domingo1).** Since they are provably the identical
   (side, hull) pair, no civ-art explanation is possible — this can
   only be a per-ship data bug.
2. **Every player's own ship consistently renders wrong/inconsistent
   with their real civ.** Confirmed root cause below — this one is not
   a race, it's unconditional.

## Root cause #1 — own ship's `SideId` is never set (confirmed, not a guess)

`src/Allegiance.ClientCore/State/ClientWorld.cs`, `Apply(ExportShip export)`,
~line 757-770:

```csharp
if (export.ShipId == -1)
{
    MySideId = export.SideId;
    return;   // <-- returns BEFORE e.SideId = export.SideId ever runs
}
```

The server's own-ship `EXPORT` deliberately carries a dummy `ShipId =
-1` (the native "this is mine" placeholder trick). This handler
correctly captures the real side into `ClientWorld.MySideId` — but
`MySideId` is a **separate property** from `ShipEntity.SideId`, and the
early `return` means the real own-ship entity (keyed by the real
`MyShipId`) NEVER gets its `SideId` field touched by this message.

Grep-confirmed: `ShipEntity.SideId` (`ShipEntity.cs:94`) has exactly
**one** assignment site in the entire client — `ClientWorld.cs:~780`
(`e.SideId = export.SideId;`), inside the real-id branch of this SAME
function, which the own-ship's dummy-id export always skips. No other
message (`SET_CLUSTER`, `SHIP_STATUS(90)`, `SingleShipUpdate`,
`HeavyShipsUpdate`) ever sets it either (confirmed by grep — `Apply
(ShipStatusMessage)` sets `HullId`/`SectorId`/`StationId`/`ParentId`/
`State`/etc. but never `SideId`).

Consequence: `mine.SideId` (read at `Game1.cs:1877`,
`ResolveShipModel(mine.SideId, mine.HullId)`, for your OWN ship) is
**always** the C# default `0` — which happens to be a real, valid side
in this test mission (Alpha), not an "unknown" sentinel. That's why
Alpha players never noticed (0 coincidentally equals their real side)
while Bravo players silently get resolved as if they were Alpha.

## Root cause #2 — a remote ship's `SideId` has exactly one, never-retried source

For a ship that ISN'T yours, `SideId` only ever arrives via the same
`Apply(ExportShip)` real-id branch — sent from the server exactly ONCE
per `(recipient, ship)` pair, latched in `_teammateExportSent`/
`_crossSideExportSent` (`Server.Broadcast.cs`), which are `HashSet.Add`-only
and never expire or retry (already flagged as a fragile pattern
elsewhere this session — the docked-ship cross-side visibility gap has
the same shape). If that one message is ever missed, delayed past a
viewer's first render of that ship, or otherwise doesn't land, that
ship's `SideId` on that ONE viewer's client silently stays at `0`
forever too — explaining the Domingo2-vs-Domingo4 mismatch: one of
them simply never got a clean `EXPORT` landed on Domingo1's client.

## Scope

1. **Fix root cause #1 first** (deterministic, 100% reproducible, the
   bigger-impact bug since it affects literally every player's own
   ship): make the own-ship's real `ShipEntity.SideId` actually get set
   from `MySideId` once known — either by having the `ShipId == -1`
   branch ALSO update the real own-ship entity's `SideId` (if
   `MyShipId` is already known at that point — check ordering against
   `SET_CLUSTER`, which is what actually assigns `MyShipId`), or by
   having `Game1.cs`'s own-ship resolution read `world.MySideId ??
   mine.SideId` instead of `mine.SideId` alone. Pick whichever is more
   correct/consistent with how the rest of the codebase treats
   `ShipEntity.SideId` as the single source of truth — don't just
   patch the one call site if the underlying entity's field should
   really carry the right value for other consumers too (check for
   other reads of `mine.SideId`/the local ship's `SideId` before
   deciding).
2. **Investigate root cause #2's actual failure mode** before deciding
   how to fix it: is the one-shot `EXPORT` genuinely being dropped
   (a real transport/ordering bug), or does it land but something
   about visibility timing (`SeenBySide` re-evaluated, cluster
   entry/exit) means it needs to be RESENT later and currently isn't?
   The existing `_teammateExportSent`/`_crossSideExportSent` latches
   were a deliberate one-shot-only design (per their doc comments) —
   understand why before just deleting the latch (that could
   reintroduce the packet-spam problem P349/P62 were solving). A safe
   fix likely involves periodically re-checking/re-sending for any
   `(recipient, ship)` pair whose `SideId` the recipient doesn't
   correctly have yet, rather than blindly resending everything
   forever.
3. Confirm whether `HullId == 0`/`SideId == 0` need to be distinguished
   from "genuinely resolved to side/hull 0" anywhere else in the
   codebase (0 is a real valid id in this mission, so any code that
   currently treats field-default-0 as "not yet known" elsewhere may
   have the same class of bug — note it in the report if you find
   another instance, but only fix the two root causes above unless a
   third is trivially the same one-line fix).

## Explicitly out of scope

- The already-confirmed-correct hull/civ ASSET content (`gt-scout.fbx`,
  `dreg-scout2.fbx`, etc.) — these were directly rendered and verified
  correct and distinct this session. Nothing wrong with the art or the
  `HullModelResolver` mapping logic itself.
- P368's spawn-sector fix, P357-P367 — unrelated, don't touch.
- The docked-ship cross-side visibility gap noted earlier this session
  (same *shape* of bug — one-shot message, no cross-side retry — but a
  different message/subsystem). Don't fix that one here unless it's
  genuinely the same code path (it isn't, per earlier investigation).

## Verification — real proof, not just build/test

- **Live proof, required**: reproduce the exact scenario that surfaced
  this — at least 3 real clients, 2 on one side (e.g. Alpha) and 1+ on
  the other, all with the SAME default hull (no custom loadout, so any
  mismatch is unambiguous). Confirm:
  - Every player's own ship renders as THEIR real civ's correct model
    (not just "looks plausible" — cross-check the specific asset name
    logged/resolved, or compare the rendered shape against the real
    `<civ>-scout` model file for their actual side).
  - Two ships known to share the identical real (side, hull) render
    IDENTICALLY to every other viewer, including a late-joining 3rd/4th
    peer.
  - A LATE-joining peer (connects after other ships already exist)
    correctly resolves pre-existing ships' models too, not just future
    ones.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. Both fixes, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. Report doc `P369_SHIP_SIDEID_NEVER_SET.md` in the repo root: the
   real mechanism confirmed for root cause #2 (was the export dropped,
   or does it need periodic re-checking), what was changed for each
   root cause, and the live multi-client verification performed (name
   the exact real hull/civ each ship resolved to, for every viewer).
