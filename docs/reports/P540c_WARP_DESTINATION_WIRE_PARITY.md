# P540c — EXPORT_WARP wire parity: `destinationID` is the PARTNER warp id, not a sector id

## Summary

Restores full native wire parity for `FM_S_EXPORT`/`OT_warp`: a warp's exported
`DestinationId` is the id of its **PARTNER warp** (the other aleph of the pair),
and the client resolves the destination **sector** from that partner warp's
`ClusterId` — exactly as native does. The port's two remaining client consumers
that misread `DestinationId` as a destination sector are fixed, and a failing-first
suite pins the invariant on the real map.

## Why

Native `WarpDef` (`~/Development/Allegiance/src/Igc/igc.h:1722-1728`) declares
`WarpID warpID; WarpID destinationID; …` — **`destinationID` is a warp id, the
partner aleph.** The native client resolves it as a warp
(`Igc/warpIGC.h:129-135`): `m_destination = GetMyMission()->GetWarp(m_warpDef.destinationID)`,
and the destination **sector** is the partner warp's own `clusterID`
(`DataWarpIGC`, `igc.h:2506-2514`).

The first P540b iteration (`0dc429dd`) bent the wire the other way: it overwrote
the exported `DestinationId` with the resolved destination **sector** id
(`rw.Destination.Id`), calling the raw value "a different id space". That breaks
native parity — a native client / WOPR bot receiving EXPORT_WARP would look up a
**warp whose id equals a sector id**: the wrong aleph or none. That bend was
amended out of the final P540b (`5ab6ff30`, which keeps `OT_warp` native), but two
**client-side** misreads remained, both feeding the raw warp id in as a destination
sector:

- `Game1.FlightInput.cs` `AllWarps` — resolved the destination sector from the
  partner, **but fell back to `w.DestinationId` when the partner was unknown**
  (i.e. treated a warp id as a sector id — a guess, not a skip).
- `SpectatorFrame.cs:166` — `destSector = w.DestinationId` (still the raw misread).

P540b's live proof passed only because the then-bent **server** sent sector ids —
the classic circular case: the wire was bent to fit the client.

## Scope 1 — the wire (revert / confirm native)

`ExportWarp.FromWarp(Model.ResolvedWarp rw)` now exports the native record
unchanged (`new() { Warp = rw.Warp }`), so the on-wire `destinationID` is the
partner warp's id. No overwrite of `DestinationId` — confirmed at the final P540b
HEAD and **pinned by a new failing-first test**.

### Failing-first (demonstrated before the fix)

Temporarily re-introducing the P540b bend (emit `rw.Destination?.Id ?? …`) makes the
new real-map wire test fail exactly as predicted:

```
Assert.Equal() Failure: Values differ
Expected: 1052        <- the partner warp id (native)
Actual:   1057        <- the bent destination-sector id
Failed!  ExportWarp_RealMap_WireCarriesPartnerWarpId_NativeParity
```

Restored the native `FromWarp`; the test passes.

## Scope 2 — client-side resolution (the real bug)

New shared helper `src/Allegiance.Core/Model/WarpDestinationResolver.cs`:

```
public static short? DestinationSector(IReadOnlyDictionary<short, DataWarpIGC> warps, short warpId)
    // null unless the warp AND its partner are both known; then partner.ClusterId
```

Semantics: **destination sector = the partner warp's `ClusterId`; if the partner
isn't known yet, return null and the caller SKIPS the edge — never guess a warp id
as a sector id.** Consumers updated to use it:

- `Game1.AllWarps` (`Game1.FlightInput.cs`) — now skips a warp whose partner is
  unknown (the old `: w.DestinationId` guess fallback is gone).
- `SpectatorFrame.BuildSectorFrame` (`SpectatorFrame.cs:166`) — resolves
  `destSector` from the partner warp's `ClusterId`; a warp whose partner is unknown
  is skipped (never emitted with a phantom sector).

Audited every other `DestinationId` consumer across `src/` and `tools/`:

- `tools/warp-navigate-bot/Program.cs:191-200` — already resolves via the partner
  (`Warps[w.DestinationId].ClusterId == nextHop`). No change.
- `tools/allegiance-mcp/BotManager.cs:1144-1152` (`TryBuildWarpDestMap`) — already
  partner-resolved. No change.
- `tools/vizfight/Program.cs:2661-2670` (`TryBuildWarpDestMap`) — already
  partner-resolved. No change.
- `src/Allegiance.ClientCore.Explorer/Program.cs:522`, `tools/missiondump` — pure
  diagnostic prints of the raw field; not routing consumers. No change.
- `Mission.cs`, `CrossRefValidator.cs`, `DataWarpIGC.cs` — the server/model and the
  wire record itself; native and correct.

### Failing-first (demonstrated before the fix)

Temporarily making the resolver "guess" (`return warp.DestinationId` when the
partner is unknown) makes the never-guess test fail:

```
Assert.Null() Failure: Value of type 'Nullable<short>' has a value
Expected: null
Actual:   20        <- the warp id guessed as a sector
Failed!  WarpDestinationResolverTests.UnknownPartner_ReturnsNull_NeverGuessesWarpIdAsSector
```

Restored the skip-semantics; the test passes.

## Scope 3 — the server side (confirmed native, no change)

The server has always resolved through the partner warp — nothing to change:

- `Mission.Assemble` (`Mission.cs:518-542`) resolves each warp's destination sector
  from the partner warp's owner cluster: `warpOwner[w.DestinationId]` → that sector
  (`partnerOwner`), falling back to a directly-named sector only as the corpus
  validator permits.
- `ServerHost.HitWarpEvent` (`ServerHost.cs:880-950`) transits via
  `rw.Destination` (the resolved sector) and `rw.Partner()` (the other warp),
  repositioning at `partner.Warp.Position` inside `destSector` — the exact native
  `GetWarp(destinationID)` resolution. Dangling/unknown edges are
  reported-not-fabricated (`destSector is null || partner is null` → no transit).

## Kept (independent P540b fixes, out of scope here)

The cross-sector goal, the eject cookie rotation, and `AutopilotSteering.Pursue` are
untouched — they are independent of the wire and remain in place.

## Wire check against native (real map)

The server's real map (`XXV_simple.igc`, the map the server boots in its live tests)
is loaded, the mission assembled with the server's own resolution, and each warp is
round-tripped through `ExportWarp.FromWarp(...).Encode() → Decode()`. The on-wire
`destinationID` equals the **partner warp's `warpID`** for **every** warp; three+
quoted (all 4 pairs shown):

```
EXPORT_WARP parity: warp 1051 (sector 1051) destinationID=1052 == partner warp 1052 whose ClusterId=1057 == destination sector 1057
EXPORT_WARP parity: warp 1052 (sector 1057) destinationID=1051 == partner warp 1051 whose ClusterId=1051 == destination sector 1051
EXPORT_WARP parity: warp 1053 (sector 1051) destinationID=1054 == partner warp 1054 whose ClusterId=1052 == destination sector 1052
EXPORT_WARP parity: warp 1054 (sector 1052) destinationID=1053 == partner warp 1053 whose ClusterId=1051 == destination sector 1051
EXPORT_WARP parity: warp 1055 (sector 1057) destinationID=1056 == partner warp 1056 whose ClusterId=1052 == destination sector 1052
```

Note warp 1051's sector happens to also be 1051 — but its `destinationID=1052` is a
**warp** id whose partner's `ClusterId=1057` is the **sector**. The two id spaces
coincide numerically on some maps and diverge on others, which is precisely why the
misread was insidious.

## Unit tests (failing-first)

- `tests/Allegiance.Core.Tests/WarpDestinationResolverTests.cs` (new) — partner →
  sector mapping; unknown-partner → null (never guess); unknown warp id / null table
  → null.
- `tests/Allegiance.Core.Tests/Wire/ExportWarpWireTests.cs` (extended) — synthetic
  `FromWarp` preserves the partner warp id (fails on the bend); real-map wire parity
  (above).
- `tests/Allegiance.ClientCore.Tests/SpectatorFrameWarpDestResolutionTests.cs` (new) —
  `BuildSectorFrame` reports the destination sector from the partner warp's cluster,
  and skips a warp whose partner is unknown.
- `tests/Allegiance.ClientCore.MonoGame.Tests/WarpRoutingRealMapTests.cs` (new) —
  on the real map, every router edge's `DestSector` is a real sector (partner's
  `ClusterId`, never a warp-id phantom), and the router picks a real warp in the
  current sector toward a reachable target — routing unchanged under the native wire.

## Verification

Whole-solution build: **0 warnings, 0 errors.**

Live proofs re-run with the native wire:
- `P540bPodAutopilotCrossSectorLiveTests` — **passed** (3 m 51 s; internal asserts
  on real transits / `accepted>0`, `rejected=0`, `SHIP_RESET=0` hold).
- `P541GotoFlyLiveTests` — **passed** (802 ms).

Full suite (`scripts/run-tests.sh`, 3 consecutive runs on the final code), measured
counts per run:

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1270/1270 | 458/459 | 3723/3726 |
| 2 | 12/12 | 1270/1270 | 459/459 | 3726/3726 |
| 3 | 12/12 | 1270/1270 | 457/459 | 3725/3726 |

The only failures are pre-existing live-wire load-flakes logged in
`docs/TEST_FLAKES.md` (P471 class) — none of them are P540c tests, and none
reproduce deterministically (run 2 on the identical code is fully green,
0 failures, exit 0). Attribution:

- Run 1 (ClientCore 458/459, Core 3723/3726): 4 transient load-flake failures
  (run 2 on the same code is 100 % green, so none are deterministic regressions;
  the class is the P471 live-wire flake family in `docs/TEST_FLAKES.md`).
- Run 3 (exact names recovered from the log):
  - `ClientCore…FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill`
    — `TEST_FLAKES.md` line 33/148, live-wire timing.
  - `ClientCore…ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_…` — residual
    ~1/15 timing flake (P482 correction, `TEST_FLAKES.md` line 46/112).
  - `Core…Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost_…`
    — `TEST_FLAKES.md` line 403, live-wire session-lost receipt timing.

Every P540c unit test and both live proofs pass in all three runs; MonoGame
(which hosts the P540b/P541 live proofs and the new `WarpRoutingRealMapTests`)
is 1270/1270 in all three.
<!-- SUITE -->
