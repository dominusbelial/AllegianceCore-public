# P375 — extend same-side-only combat/state broadcasts to cross-side now that cross-side EXPORT exists

## Why this task exists

Two real, live-confirmed bugs, both tracing to the SAME root pattern:

1. **"I can't see the projectiles of the other ship [opponent]"** —
   confirmed via direct code read. `Server.Combat.cs:28-45`
   (`BroadcastProjectileInstance`) and `Server.Combat.cs:114-147`
   (`BroadcastProjectileHullHit`) are both explicitly documented as
   same-side-only by deliberate design, with the doc comment literally
   saying: *"Cross-side/enemy projectile visibility is NOT attempted
   (needs Phase 5's cross-side EXPORT first — not built)."* The same
   deferral language appears on the missile-fire broadcast
   (`Server.Combat.cs:~175`, "Cross-side/enemy visibility is NOT...").
2. **"I have the ship in front of me in Domingo1, I can't select it
   with T"** — confirmed via direct code read to be a real regression
   surfaced by P371 (though the underlying cause predates it).
   `ExportShip` (the ONLY source of a cross-side ship's identity, see
   P369) carries no `SectorId` field at all. The only OTHER source of
   `ShipEntity.SectorId` is `SHIP_STATUS(90)`, which is
   same-side-teammate-only by the same deliberate P58-era design
   ("Scope: same-side only"). So a cross-side ship's `SectorId` is
   PERMANENTLY stuck at the C# default `0` — the exact same bug SHAPE
   P369 already fixed for `SideId`, just a different field, never
   caught because nothing depended on a cross-side ship's `SectorId`
   until P371's new sector-scoped target-pick filter started reading
   it. An enemy ship directly in front of you fails
   `ship.SectorId == mySector` and can never be `T`-selected unless
   you happen to be in sector 0 yourself.

**The prerequisite all of these were explicitly waiting on — the
cross-side EXPORT/visibility infrastructure — now exists and is
confirmed correct** (P346/347/349, and P369's fix to the identity gate
mismatch). This task is the natural, ready-to-build follow-through the
original authors flagged as future work.

**A third, related live observation, also confirmed real**: Domingo1
and Domingo3 (cross-side) were face-to-face at close range — well
within any plausible detection/scanner range, ruling out a
distance-based edge case — yet from Domingo3's client, Domingo1's ship
was reported as **fully invisible** — not wrong-shaped (the P369 fallback-model case),
not present-but-unselectable (the P371/`SectorId` case above) —
genuinely never rendered at all, no model, nothing. Meanwhile real
ship-vs-ship COLLISION still worked correctly (the physics/damage
exchange happened), which makes sense since collision resolution is
purely server-authoritative and doesn't depend on what any client
renders — but it proves the server-side sim had the ship correctly
positioned in the shared sector the whole time, while Domingo3's
client had zero representation of it. This is a THIRD real symptom on
top of the two above, and may or may not share their exact root cause
— investigate it as its own question, don't assume it's automatically
explained by the `SectorId` fix. A likely candidate given P370 just
shipped: check whether a cross-side ship can get incorrectly latched
into P370's new `IsGone` state (or simply never receive a real
`Latest` pose at all) under some condition specific to cross-side
pairs, given cross-side ships have far fewer "presence" signals
(EXPORT + `HeavyShipsUpdate` only, never `SingleShipUpdate`) than
same-side teammates — read P370's report
(`P370_SHIP_LEFT_VIEW_NOTIFICATION.md`) for the exact set of signals
that set/clear `IsGone`, and check whether all of them reliably fire
for a genuinely-visible cross-side ship, not just same-side ones.

## Scope

1. **Fix the `SectorId` gap first** (higher priority — it's an active
   regression on the just-shipped P371 feature, breaking the single
   most common target-pick case, an enemy in front of you): give
   cross-side peers a real, kept-current `SectorId` for ships they can
   see. Two possible shapes — pick whichever is more correct/minimal
   given what you find:
   - Add `SectorId`/`ClusterId` to the cross-side `ExportShip` payload
     (a one-time identity fact) AND/OR
   - Extend a position/state telemetry message that ALREADY reaches
     cross-side peers (check `HeavyShipsUpdate(176)` — P369's report
     confirms this already flows cross-side once identity exists — does
     it carry sector info, or would it need to?) to keep `SectorId`
     current as a cross-side ship moves between sectors, not just a
     one-time value from EXPORT.
   Read `ShipEntity.SectorId`'s full set of consumers (found this
   session: the warp-draw loop, P371's target-pick filter, P370's
   sector-based hide/show logic) to make sure whatever you choose keeps
   ALL of them correct for cross-side ships, not just target-pick.
2. **Extend `BroadcastProjectileInstance`/`BroadcastProjectileHullHit`/
   the missile-fire broadcast to cross-side peers**, reusing the real,
   already-proven cross-side audience computation — read P369's
   `SweepCrossSideExports`/the aligned telemetry-vs-identity gate it
   built (`Server.Broadcast.cs`) and use the SAME "does this side have
   a seated peer in the shooter's sector, and is the shooter visible to
   them" condition, rather than re-deriving a new audience rule. Keep
   the existing same-side latch/audience logic for same-side peers
   unchanged — this only ADDS the cross-side audience alongside it.
3. Confirm the client correctly renders a cross-side projectile/missile
   once it receives these — check whether `Game1.cs`'s projectile/
   missile render code has any same-side assumption baked in that
   would need updating too (it may already be side-agnostic if it just
   renders whatever `ProjectileInstance`/`FireMissile` messages arrive
   — verify, don't assume).

## Explicitly out of scope

- P372/P373/P374 — separate, complementary (radar color, sound cues,
  threat warnings), don't touch.
- Redesigning the cross-side EXPORT/identity system itself — P369
  already built and fixed it; reuse it as-is.
- Any alliance/diplomacy concept — matches the existing project
  convention of no alliance system.

## Verification — real proof, not just build/test

- **Live proof, required**: real 2+ side multiplayer session with real
  weapon fire and missile launches. Confirm:
  - An enemy ship directly in view/in front of you IS selectable with
    the real target-cycle key, in the sector you're both actually in
    (the exact reported repro case).
  - You can SEE an opponent's projectile fire (bullets) and missile
    launches in real time, not just their damage results.
  - Same-side teammate projectile/missile visibility (already working)
    is unaffected — confirm with a same-side pair too.
  - A cross-side ship's `SectorId` stays correct as it actually moves
    between sectors (warp), not just correct once at first contact.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. Both fixes, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. Report doc `P375_CROSS_SIDE_COMBAT_VISIBILITY.md` in the repo root:
   the real mechanism chosen for cross-side `SectorId`, how the
   cross-side projectile/missile audience was computed (and that it
   reused P369's existing gate rather than re-deriving one), and the
   live verification performed for all four bullet points above.
