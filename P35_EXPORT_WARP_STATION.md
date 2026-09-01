# P35 — export warps + stations so bots can actually navigate

Branch `master`, on top of `28910ac` (P33). Date 2026-08-31.

## Summary

Closed the last data gap P34 identified: the client's clusters had empty
`GetWarps()`/`GetStations()` lists because `AllegianceCore` exported only the
ship and the sectors on join — so the native `FindTarget`
(`src/Igc/common.cpp:470`, station search `:500-501`, adjacent-cluster warp
walk `:677-707`) had nothing to search and returned NULL every time
(`P34:CI FindTarget leave target=00000000`), which made `ReturnToBase`'s
`SetCommand` *clear* the ship's command. This task exports warps and stations
the same way P30 exported clusters.

**Live outcome — real, verifiable advance, but the movement bar is NOT met.**
The warp/station exports are byte-correct on the wire, delivered to the
joining peer, and **imported by the native client** (debug.log:
`Station initialize 1051` / `Station initialize 1052`, **zero HACKER / zero
AV**). Critically, the P30 negative is resolved on one axis: **`[ctrl]`
lines now flow** — the server receives `FM_C_SHIP_UPDATE(24)` continuously
(In P30's 5-trial run: `no FM_C_SHIP_UPDATE(24)` in any trial; the observer
also read `cmd=c_cidGoto` throughout, still `c_cidGoto`). So `flyingF()`
(`clintlib.h:1198`) is true — the ship **acquired its cluster** and
`BaseClient::SendUpdate` now emits — the exact thing P30 could not achieve.
**But the ship's position still reads `(0,0,0)` and the observed
`max 1s-position-delta` is `0.0 units -> no significant movement`.**
This task's specific contribution (warp/station export) is complete and
verified; the final movement milestone remains blocked on a native-client
behavior (position application / command→thrust translation) that is outside
this C#-server task's scope — exactly the P30-stop-condition class, and this
task's own rule ("if the live test reveals something in the native client
also needs a change — stop and report").

---

## 1. The wire codecs

### `src/Allegiance.Core/Data/DataWarpIGC.cs` — added `Encode()` (methods at `:78-131`)

`OT_warp(7)`, fixed 108 bytes. Encoder mirrors `Decode`'s field table
verbatim (offsets 0-105 + 2 trailing pad bytes to round to 108). Per the
reference exporter `CwarpIGC::Export` (`src/Igc/warpIGC.cpp:112-130`), which
copies the warp-definition and runtime position/orientation/signature/cluster
straight through with no normalization, there is **no always-false-on-export
field** for a warp (contrast cluster's `activeF`): every field is raw
passthrough, with the three string buffers (`TextureName`/`IconName` 13,
`Name` 25) zero-padded (not NUL-forced).

### `src/Allegiance.Core/Data/DataStationIGC.cs` — added `Encode()` (`:77-134`)

`OT_station(1)`, fixed 88 bytes. Encoder mirrors `Decode` verbatim (offsets
0-86 + trailing pad 87). Per `CstationIGC::Export`
(`src/Igc/stationIGC.cpp:89-113`) there is likewise **no
always-false-on-export field**: all geometry/ids/BP-fractions raw passthrough,
`Name` (25) zero-padded.

Both pinned by `Encode(Decode(b)) == b` field-wise for **every** corpus
record (5212 warps / 289 stations across all 111 maps) via the new
`DataWarpIGCRoundTripTests` / `DataStationIGCRoundTripTests` (exact mirrors
of `DataClusterIGCRoundTripTests`).

### `src/Allegiance.Core/Wire/ExportWarp.cs` / `ExportStation.cs` — new `FM_S_EXPORT(26)` codecs

Mirror `ExportCluster`'s envelope exactly:
```
ExportWarp   : cbmsg=120, fmid=26, VarItem, objecttype=OT_warp=7,  exportData=DataWarpIGC(108)
ExportStation: cbmsg=100, fmid=26, VarItem, objecttype=OT_station=1, exportData=DataStationIGC(88)
```
Both hold the already-decoded `Data/*` record and delegate fixed-payload
serialization to its `Encode()`. Factories: `ExportWarp.FromWarp(ResolvedWarp)`
(takes the raw `.Warp`, ignores `.Destination` — a resolution *result*, not
wire data), `ExportStation.FromStation(DataStationIGC)`.

### `src/Allegiance.Server/Server.cs` — send site (`:756-782`)

Immediately after the existing cluster-export send (`:749-754`, itself placed
after `PLAYERINFO` per P33's fix — not moved earlier), same peer/session, per
sector, in the reference server's own order
(`src/FedSrv/fsmission.cpp:4170-4222`): cluster → warps → stations. Loop:
`foreach sector { warp exports; station exports }`, so the wire order is
[13 clusters][38 warps][2 stations]. Unconditional — no `SeenBySide()`
fog-of-war gate, matching P30's simplification (`Server.cs:742-747`).

---

## 2. Tests (all 2211 pass; 3 consecutive clean runs)

- `DataWarpIGCRoundTripTests` / `DataStationIGCRoundTripTests` (new):
  corpus-wide `Decode(Encode(c)) == c` field-wise, every record, all 111 maps.
- `ExportWarpWireTests` / `ExportStationWireTests` (new): declared sizes,
  round-trip, a **hand-composed byte-exact buffer** (from the task's field
  tables, not the encoder), and rejection of wrong fmid / non-`OT` objecttype /
  truncated/blob-size mismatch.

`dotnet build`: 0 warnings / 0 errors. `dotnet test`: **2211 passing**
(3 consecutive clean runs).

---

## 3. Live proof

Fresh server (`--capture /tmp/p35_capture.bin`, map `XXV_simple.igc`,
13 sectors / 38 warps / 2 stations), rebuilt native client on the Windows VM
(T19 `f3a64d1b` confirmed present; ClintLib built explicitly per the
P33/P34 lesson — the raw `.lib` `AdditionalDependencies`, not a
`ProjectReference`), T14 harness `Wopr.exe -d 192.168.0.140 6073`,
`TeamDirectorDirectTest`.

Server log (representative):
```
[export] peer 1 't14Bot@BOT' -> 13 cluster(s): 1051:'Arrokoth', ..., 1063:'Sedna'
[export] peer 1 't14Bot@BOT' -> 38 warp(s), 2 station(s) (OT_warp 7 / OT_station 1)
[ctrl] 1 ship 8096 controls=(yaw 0.000 pitch 0.000 roll 0.000 thr 0.000) state=0x0   (continuous)
```
Client debug.log (abridged): `Station initialize 1051` / `Station initialize 1052`;
no `HACKER`, no AV.

Client observer (verbatim, both trials):
```
[t14] pos=(0.0,0.0,0.0) speed=0.0 distFromOrigin=0.0 delta1s=0.0 cmd=c_cidGoto   (persists ~30s)
[t14] Observation done. max 1s-position-delta = 0.0 units.  -> no significant movement
```

fedmsgdump on the capture (verbatim counts): 13 × `fmid=26 cbmsg=104`
(cluster, objtype 13), **38 × `fmid=26 cbmsg=120` (warp, objtype 7)**,
**2 × `fmid=26 cbmsg=100` (station, objtype 1)**, all CRC-verified; then
`SET_CLUSTER(7) sectorID=1051 pos=(720,730,920)`, and continuous
`SINGLE_SHIP_UPDATE(78)` broadcasting `pos≈(720,730,919.x)` — the server
knows where the ship is.

| trial | `[ctrl]` received | `Station initialize` | max 1s-position-delta | WOPR exit |
|---|---|---|---|---|
| 1 | **yes (continuous)** | 1051, 1052 | `0.0 -> no significant movement` | FINISHED |
| 2 | **yes (continuous)** | 1051, 1052 | `0.0 -> no significant movement` | FINISHED |

## 4. Honest diagnosis of the residual (not the export)

The gap P30 chased — "the ship never acquires its cluster, so `flyingF()` is
false and no `FM_C_SHIP_UPDATE` flows" — is now **closed**: `[ctrl]` flows
continuously and stations import. That is a real, measurable advance over
P30's 0/5. But the ship's client-side position still reads `(0,0,0)` and
thrust stays `0.000`, so the bot does not fly. That residual (position
application in the client ship / command→thrust in `ReturnToBase`) is a
native-client behavior, not a missing server export — the exports are
byte-correct, delivered, and imported. Per the task's rule, I stop here
rather than change `~/Development/Allegiance` (its fixes are intentionally
kept local).

## Files changed

- `src/Allegiance.Core/Data/DataWarpIGC.cs` — added `Encode()`.
- `src/Allegiance.Core/Data/DataStationIGC.cs` — added `Encode()`.
- `src/Allegiance.Core/Wire/ExportWarp.cs` — new, `FM_S_EXPORT(26)`/`OT_warp(7)`.
- `src/Allegiance.Core/Wire/ExportStation.cs` — new, `FM_S_EXPORT(26)`/`OT_station(1)`.
- `src/Allegiance.Server/Server.cs` — warp+station exports after cluster exports.
- `tests/Allegiance.Core.Tests/DataWarpIGCRoundTripTests.cs` — new.
- `tests/Allegiance.Core.Tests/DataStationIGCRoundTripTests.cs` — new.
- `tests/Allegiance.Core.Tests/Wire/ExportWarpWireTests.cs` — new.
- `tests/Allegiance.Core.Tests/Wire/ExportStationWireTests.cs` — new.
- `README.md` — document the new codecs + updated test count (2211).
- `P35_EXPORT_WARP_STATION.md` — this write-up.

Build clean (0 warnings / 0 errors); **2211** tests pass (3 consecutive runs).
No source changes to `~/Development/Allegiance` (T19 confirmed present, no
commit). Server stopped, port 6073 free.
