# P349 — Cross-side ship identity EXPORT: per-recipient catch-up (fixes late joiner)

Relationship: direct follow-up to P346 (`1c1fb7a`, cross-side ship identity EXPORT)
and P347 (`d7abf80`, MonoGame client decode of the cross-side visibility sync).
Scope boundary is exact: the visibility DETERMINATION, the client decode, and the
HullModelResolver are all confirmed correct and untouched; this task changes only
WHO the server tells about an already-correctly-computed visible ship, and WHEN.

## Root cause (confirmed, not guessed)

The cross-side identity EXPORT introduced in P346 was delivered by an
edge-triggered **transition QUEUE**: `Sector.ResolveCrossSideVisibility` queued a
`(ship, sideId)` pair only at the exact false→true moment a ship became visible to
a non-own side, and the host drained it to whichever peers were connected to that
side **at that exact tick**. Once sent and drained, the event was gone forever —
there was no per-recipient latch, no re-check, no "peers I haven't told yet".

The same-side [teamexport] path (`BroadcastShipStates`, `Server.Broadcast.cs`)
does NOT have this flaw: every tick it re-evaluates every connected peer against
every teammate ship it hasn't yet been told about (the `_teammateExportSent`
per-`(peer, ship)` set), so a peer joining at any time is caught up on its very
next tick.

`vizfight roster` connects all 72 bots and makes them mutually visible to each
other's sides well before a human spectator typically connects last. By the time
the human's side-0 presence is seated, most cross-side visibility transitions to
side 0 had already fired — to the bios bots that connected earlier — and were gone
forever. The human viewer consequently got almost no cross-side EXPORT and its
client rendered "65 of my same ship" (the same fallback model). That is the
structural gap, exactly matching the observed boot order.

## Mechanism shape chosen: per-tick catch-up SWEEP; transition queue REMOVED

`Step("crossSideExportSweep", SweepCrossSideExports)` replaces the old
`Step("crossSideExports", … DrainCrossSideExports …)`.

For every connected peer and every ship its side currently SEES cross-side
(P132's live `SeenBySide` latched state — the determination itself is untouched),
send the `EXPORT(26)` iff a new per-recipient set `_crossSideExportSent[(peer,
shipId)]` doesn't already contain the pair. One mechanism covers BOTH cases the
one-shot queue could not reconcile at once:

- **Ship just became visible** — the P132 sweep flips `SeenBySide(side)` true; the
  per-tick catch-up sees it the same/next tick and EXPORTs it to every peer on
  that side.
- **Peer just joined an already-informed side** — the newcomer is a fresh
  (peer, ship) key, so the next tick's catch-up sends it every cross-side ship its
  side already sees.

Keeping the old transition queue in ADDITION would be redundant, not
complementary: the sweep already observes the same "now visible" condition (the
latched flag), so the queue would only duplicate sends that the latch then has to
deduplicate. The Core side therefore no longer tracks transitions at all — it only
keeps the `SeenBySide` flag correct. `_pendingCrossSideExports`,
`DrainCrossSideExports()` and the queueing inside `ResolveCrossSideVisibility`
are all removed.

## Real per-recipient bookkeeping

`_crossSideExportSent` is keyed on `(recipient ConnectionId, shipId)` — the exact
shape of `_teammateExportSent` — because multiple peers share a side (vizfight's
side 0 = Bios = the human + several Bios bots) and each individual connection
needs its own "have I told THIS peer" latch. A ship seen by a side is exported to
EVERY peer on that side (the side's sensor net is shared), but each pair latches
independently.

## Disconnect cleanup (scope item 4)

`_crossSideExportSent` gets the same `RemoveWhere(e => e.Recipient == id ||
e.ShipId == departedShipId)` in `ReleasePeer` (`Server.Lifecycle.cs`) as
`_teammateExportSent`, so it cannot grow monotonically with connection churn.
Added `CrossSideExportSent` to `ConnectionScopedCacheCounts()` and to the
`ConnectionCacheCleanupTests.AllDrained` invariant; the new late-joiner live test
also asserts the latch populates then drains to 0 on disconnect.

## Out of scope (unchanged)

- `Sector.ResolveCrossSideVisibility` / P131 `InScannerRange` /
  `LineOfSightExist` — visibility determination untouched.
- Client decode (P347) and `HullModelResolver.ResolveAsset` — confirmed correct.
- The same-side [teamexport] path itself — reference/copy only, not modified.
- Native reference source (`~/Development/Allegiance`) — read-only.

## Verification

### Late-joiner live proof (the reported scenario, reproduced exactly)

Ran `vizfight roster <port>` (full 72-hull, no civ filter) on
`m2xspiderw.igc`. All 72 bots connected, launched via `SendBuyLoadout(fLaunch)`,
became mutually cross-side visible, and were parked ("full roster parked"). Only
THEN did a real MonoGame client connect (`--capture … chase`) as the late-joining
human viewer — seated on side 0 (Bios scout), the same ordering as the bug report.

Server log corroboration (quantitative, grep of `roster.log`):

- `grep -c '\[xexport\] side 0 <- peer 73 '` → **58** — the late viewer (peer 73,
  side 0) received **58 cross-side EXPORTs**, one per distinct fleet ship visible
  to side 0.
- Those 58 exports span **18 distinct hull IDs** (18 distinct per-civ/per-model
  hulls) — genuinely distinct identities, inverting the "sea of the same ship"
  failure.
- The run's first export to peer 73 is `[xexport] side 0 <- peer 73 ship 8109 …
  hull 78`, and so on across all 9 factions' grid columns.

Captured 39 live frames over ~36s (all distinct file sizes = a live, non-frozen
scene). Vision inspection of the frames and magnified crops shows several visibly
distinct hull silhouettes simultaneously — a compact swept-wing fighter, an
elongated horizontal-hull vessel with an angular wing/appendage, a larger
elongated warship, and a complex multi-part craft — consistent with the 18
distinct models delivered. Frames live in `.verify_p349/`.

### Early-joiner regression (already-working case stays correct)

The unit tests pin both sides:

- `CrossSideExportLiveTests.Cross_side_ship_becoming_visible_triggers_export` —
  a peer connected from the start still gets its cross-side EXPORT promptly when
  the foreign ship comes into range.
- `Same_side_teammate_still_gets_teamexport_no_cross_duplicate` — the same-side
  [teamexport] fires exactly once, and the cross-side sweep provably never fires
  for a ship's own side (no duplicate).
- `Late_joining_peer_on_an_already_informed_side_gets_caught_up_cross_side`
  (new) — the mini reproduction of the bug: B becomes visible to side 0 before C
  joins; C (late, same side as A) still receives B's EXPORT; A is not re-sent
  (latch suppresses the duplicate); and the cross-side latch drains to 0 on
  disconnect. **Must-fail proven**: commenting out
  `Step("crossSideExportSweep", …)` reds this test (and the becoming-visible
  test) while the same-side test stays green.
- `ConnectionCacheCleanupTests` — extended with the `CrossSideExportSent` drain
  invariant.

### Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- `Allegiance.Core.Tests` full suite run **2×**: 3267/3274 then 3271/3274, only
  the pre-existing live-wire timing flakes failing, with **names varying run to
  run** (run 1: TurretGunner/StationRescue/Ripcord/ChatSectorWire/GameOverPlayers
  +2; run 2: TurretGunner/ProjectileBroadcast/ConquestTerritory). Each suspect was
  confirmed pre-existing by running on the **pristine P348 tree** (e.g.
  `ProjectileBroadcastTests.Real_weapon_fire…` fails identically on pristine;
  `GameOverPlayers…Conquest…` is intermittent — passes then fails). None reference
  the P349 symbols; none are in the cross-side/cache classes touched here.
- Cross-side/cache/visibility test classes (9 tests) pass clean in isolation.

## Files

- `src/Allegiance.Core/Model/Sector.cs` — removed the P346 transition queue
  (`_pendingCrossSideExports`, `DrainCrossSideExports`, and the queueing inside
  `ResolveCrossSideVisibility`); visibility determination unchanged.
- `src/Allegiance.Server/Server.cs` — added `_crossSideExportSent`
  `HashSet<(ConnectionId, short)>`; added `CrossSideExportSent` to
  `ConnectionScopedCacheCounts()`.
- `src/Allegiance.Server/Server.Lifecycle.cs` — disconnect cleanup for the new set.
- `src/Allegiance.Server/Server.Broadcast.cs` — replaced the transition-queue
  step with `Step("crossSideExportSweep", SweepCrossSideExports)`;
  `BroadcastCrossSideExport(ship, sideId)` became `SweepCrossSideExports()` +
  per-recipient `SendCrossSideExport(conn, ship, seeingSide)`.
- `tests/Allegiance.Core.Tests/CrossSideExportLiveTests.cs` — far-apart spawn for
  the edge tests (avoids a join-time latch under the new semantics) + the new
  late-joiner test.
- `tests/Allegiance.Core.Tests/ConnectionCacheCleanupTests.cs` — drain invariant.
