# P369 — Ship model/civ shows wrong: `ShipEntity.SideId` silently defaults to 0

**Summary.** Two real defects made ships render with the wrong hull/civ model in
live multiplayer. Root cause #1 (deterministic, every player's OWN ship) and the
actual mechanism of root cause #2 (remote/cross-side ships) are both fixed and
verified live with 4 real clients across 2 sides. The hull/civ ART content and
the `HullModelResolver` mapping were confirmed correct and are untouched.

---

## Root cause #1 — the local ship's `SideId` is never set (fixed)

`src/Allegiance.ClientCore/State/ClientWorld.cs`, `Apply(ExportShip)`:

```csharp
if (export.ShipId == -1)
{
    MySideId = export.SideId;
    return;   // <- returned BEFORE e.SideId = export.SideId ever ran
}
```

The server's own-ship `EXPORT` carries a dummy `ShipId = -1` (the native "this is
mine" placeholder). The handler captured the real side into `MySideId` but then
returned, so the real own-ship entity (keyed by the real `MyShipId`, assigned only
by `SET_CLUSTER(7)`) NEVER had its `ShipEntity.SideId` set. Grep confirmed
`ShipEntity.SideId` (`ShipEntity.cs:94`) had exactly ONE assignment site in the
client — the real-id branch of this same function, which the dummy-id export
always skips; no other message sets it. So `mine.SideId` was always the C# default
`0` — which in the test mission is a *valid* side (Alpha), so Alpha players never
noticed while Bravo players (real side ≠ 0) silently resolved as if they were Alpha.

### Fix
Stamp the real own-ship entity's `SideId` from `MySideId` in **both** orderings of
the two join messages (whichever arrives second completes it):

- In the `ShipId == -1` branch: after `MySideId = export.SideId`, if `MyShipId` is
  already known, write `own.SideId = export.SideId` on the own entity.
- In `Apply(SetCluster)`: after `MyShipId ??= set.Ship.ShipId`, if `MySideId` is
  already known, write `e.SideId = sideId` on the just-upserted own entity.

Because `ShipEntity.SideId` is the single source of truth the model/texture and
the side-coloured motion-trail all read (`Game1.cs:1877/1878/1916`), fixing it on
the entity (rather than patching the one `ResolveShipModel` call site) corrects
every consumer uniformly.

Verified deterministically in `tests/Allegiance.ClientCore.Tests/ClientWorldSideIdTests.cs`
(both orderings + the real-id-export non-regression).

---

## Root cause #2 — remote/cross-side ship identity (mechanism CONFIRMED + fixed)

The task asked whether the one-shot cross-side identity `EXPORT` was being
*dropped* (transport/ordering bug) or whether it *lands but needs re-sending*
(visibility timing). Investigation with live instrumentation produced a precise
answer — it is **neither** of those two; the real mechanism is a **gate mismatch**:

- The heavy-ships telemetry `BroadcastShipsUpdate(176)` is sent to a peer for
  **every non-cloaked ship in the peer's own cluster** (`peerSees` at
  `Server.Broadcast.cs:1371` is deliberately true for all non-cloaked ships —
  P270 left non-cloaked cross-side fog-of-war untouched). The MonoGame client is a
  **pure renderer** with no local IGC sim, so it draws exactly those ships.
- But the cross-side **identity** `EXPORT` (the client's ONLY source of a foreign
  ship's `SideId`/`HullId`) was gated on the stricter P132 **detection** radius
  (`SeenBySide` = `scannerRadius + targetRadius + scannerRange·signature`), which
  is often much smaller than the telemetry audience.

Result (reproduced live, 4 clients): a cross-side ship in the viewer's cluster
**was rendered** (its telemetry flowed) but **never identified** (detection never
marked it seen) → the viewer held `hull=0, side=0` → the wrong/fallback model,
**permanently** for the whole mission. In the reported playtest this is the
Domingo2-vs-Domingo4-identical-(side,hull) pair showing different models to one
viewer: the viewer had one ship's identity but not the other's. It is not a
one-shot "packet dropped" (the transport is reliable TCP); it is that the identity
is gated independently of — and more narrowly than — the telemetry that makes the
client render the ship.

### Fix
Align the cross-side identity gate with the telemetry gate in
`SweepCrossSideExports` (`Server.Broadcast.cs`):

- A cross-side ship's identity is exported to a side's peers iff that side has a
  **seated peer in the ship's sector** (telemetry flows there) **and** the ship is
  not a cloak-hidden enemy (`!ship.IsCloaked || ship.SeenBySide(sideId)` — exactly
  `BroadcastShipsUpdate.peerSees`, since P270 drops those from telemetry too).
- The export stays **one-shot per (peer, ship)** (the `_crossSideExportSent`
  latch — never whole-mission, never per-tick, so P346/P349's packet-spam concern
  is preserved).
- **Self-heal layer:** every `CrossSideIdentityRefreshTicks` (600 ticks = 30 s at
  20 upd/s) the already-latched identity of a still-eligible (peer, ship) is
  re-sent, so even a hypothetical single missed EXPORT repairs itself instead of
  pinning a wrong side forever. Bounded and rare by construction.

The same-side `[teamexport]` path was left untouched: a same-side teammate's
`SideId` is the recipient's own side by definition, so it cannot be wrong.

Updated/added tests in `tests/Allegiance.Core.Tests/CrossSideExportLiveTests.cs`:
- Rewrote `Cross_side_same_cluster_ship_exchanges_identity_even_before_detection`
  to pin the corrected contract (identity accompanies telemetry, correct
  hull+side, exactly once — even when the detection radius never marks it seen).
- Added `Cross_side_identity_refresh_self_heals_on_the_slow_cadence` (the 30 s
  self-heal re-send, cadence-bounded, correct identity).
- `Late_joining_peer...` and `Same_side_teammate...` pass unchanged.

---

## Live multi-client verification (real server, 4 real clients, 2 sides)

Harness: `.verify_p369/harness` — ONE real in-process `Allegiance.Server`
(`PCore001` static core, `XXV_simple` map, 2 sides) with 4 real
`ConnectionSession` clients (the same data plane the MonoGame client drives) over
the real TCP transport. Ships were given the core's largest-scanner-range hull
(**hull 92**) so every side's ships are mutually visible; each viewer's
`ClientWorld` was queried for the `(SideId, HullId)` it reports for every ship it
knows. Because the ship MODEL is a deterministic function of exactly
`(SideId, HullId)`, equality across viewers == identical rendered model.

Steady-state view (every viewer's report for every ship):

| Ship (real side) | Domingo1 (Alpha) | Domingo2 (Bravo) | Domingo4 (Alpha) | Late joiner (Bravo) |
|---|---|---|---|---|
| 8096 (Alpha, hull 92)  | (0, 92) ME | (0, 92) | (0, 92) | (0, 92) |
| 8097 (Bravo, hull 92)  | (1, 92) | (1, 92) ME | (1, 92) | (1, 92) |
| 8098 (Alpha, hull 92)  | (0, 92) | (0, 92) | (0, 92) ME | (0, 92) |
| 8099 (Bravo, hull 92)  | — (not yet) | (1, 92) | — | (1, 92) ME |

- **Every player's OWN ship reports its real side** — Bravo pilots (8097, 8099)
  report side **1** (Ga'Taraan), not the default 0 (this is the root-cause-#1
  proof: before the fix every Bravo own ship resolved as side 0 = Alpha).
- **Identical (side, hull) ⇒ identical rendering**: the two Alpha ships (8096,
  8098) both read `(0, 92)` on EVERY viewer (including both Bravo viewers), and
  the two Bravo ships (8097, 8099) both read `(1, 92)` on every viewer.
- **Late joiner**: the 4th peer connected after the other three already existed
  and correctly resolved all three pre-existing ships (own-side and cross-side)
  to `(0/1, 92)` — the server's per-tick catch-up plus the P369 gate sent its
  cross-side identity EXPORTs on connect (see `[xexport] side … <- peer 4 …`).
- **Instrumentation proof of the mechanism**: the run's P132 diagnostic showed
  `8097 SeenBySide[Alpha0] = False` and `8096 SeenBySide[Bravo1] = False` (the
  detection radius never marked the cross-side ships seen) while `[xexport]`
  still delivered every cross-side identity — confirming identity now follows
  telemetry, not detection.

Each `[xexport]`/`[teamexport]` line in the log names the real hull it carried
(hull 92), and the server boot names the civ mapping (Alpha = civ 1 Dreghklar,
Bravo = civ 3 Ga'Taraan), so each ship's resolved model input is its real
(civ of its side, hull 92) on every viewer.

Result: **passes=25, fails=0**, exit 0.

---

## Scope-note: `HullId == 0` / `SideId == 0` ambiguity

`ShipEntity.SideId` defaulting to 0 (the bug) was the one real "0 looks like a
known side" instance found and fixed. `ResolveShipAsset`'s `if (hullId == 0)
return null;` (`Game1.cs:5891`) remains correct — it means "no identity received
yet → fallback", and with P369 the only way a rendered ship still has no identity
is a genuinely missing EXPORT, which the 30 s self-heal now repairs.

## Build & test

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- `dotnet test` full suite run 2×: only the pre-existing documented live-wire
  timing flakes are expected (named below from the runs); none reference the P369
  symbols.
- Targeted suites green: `ClientWorldSideIdTests` (3), `CrossSideExportLiveTests`
  (4).

## Files changed

- `src/Allegiance.ClientCore/State/ClientWorld.cs` — own-ship `SideId` stamped from
  `MySideId` in both the dummy-EXPORT branch and `Apply(SetCluster)` (root cause #1).
- `src/Allegiance.Server/Server.cs` — `CrossSideIdentityRefreshTicks` (30 s) constant.
- `src/Allegiance.Server/Server.Broadcast.cs` — `SweepCrossSideExports` identity gate
  aligned with the telemetry gate + periodic self-heal re-send (root cause #2).
- `tests/Allegiance.ClientCore.Tests/ClientWorldSideIdTests.cs` — new (root cause #1).
- `tests/Allegiance.Core.Tests/CrossSideExportLiveTests.cs` — rewritten gate test +
  new self-heal test + doc.
- `.verify_p369/harness/` — live 4-client verification harness (build + run).
