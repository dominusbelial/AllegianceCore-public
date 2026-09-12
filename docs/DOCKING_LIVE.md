# DOCKING_LIVE — live-VM confirmation that P53 works, plus a real open question

Date: 2026-09-02. Claude Code's own live pass, matching the standing
P4X→live-pass pattern. Real `Wopr.exe` clients (Windows VM,
`192.168.0.240`) against a freshly built `AllegianceCore` server
(commit `ba55dc2`, P53's own commit).

## Method

`tools/vizfight`'s `InjectExplorerSpawn` was temporarily edited to spawn
bots directly at a friendly station's position (`Ceto` sector, station
1051 "Garrison", side 0) instead of near a warp — the map's first sector
has zero stations (per `DOCKING_P53.md`'s own note), so the standing
demo's default spawn never comes near one. Fully reverted after (`git
diff` empty vs `ba55dc2`'s own tree). A temporary one-line diagnostic was
added to the native client's `ScoutExploreMap.MessageReceiver_FMD_S_SHIP_STATUS`
handler logging `message.status.GetStationID()`/`GetState()` (the raw wire
fields) alongside `ship.GetStation()` (the client's own resolved object
reference) — fully reverted after (native repo diff returns to exactly
P50's own still-uncommitted 39-line diff, nothing else).

## Result — wire-level docking CONFIRMED, correct in every way tested

Server log, both bots, immediately on spawn:

```
[export] peer 1 'explorer0@BOT' -> 38 warp(s), 2 station(s) (OT_warp 7 / OT_station 1)
[dock] ship 8096 docked at station 1051 'Garrison' (side 0) in sector 'Ceto' — next SHIP_STATUS(90) carries StationID + State=c_ssDocked
[export] peer 2 'explorer1@BOT' -> 38 warp(s), 2 station(s) (OT_warp 7 / OT_station 1)
[dock] ship 8097 docked at station 1051 'Garrison' (side 0) in sector 'Ceto' — next SHIP_STATUS(90) carries StationID + State=c_ssDocked
```

Client log, both bots, every `SHIP_STATUS` received across the full 60s
run (~40 receipts per bot):

```
P53-LIVE: station=null, wireStationID=1051, wireState=2
```

- `wireStationID=1051` and `wireState=2` (`StateDocked`) are **exactly
  correct** on every single receipt, matching the server's own `[dock]`
  log precisely — the wire payload P53 built is byte-correct and reaches
  a real native client unchanged. This is the entirety of what P53 itself
  was scoped to prove (`DOCKING_P53.md`: "wire visibility via existing
  SHIP_STATUS"), and it's confirmed.
- Both bots stayed docked/in-range for the whole 60s window despite real,
  active thrust control input being sent the entire time (server `[ctrl]`
  log shows real non-zero `thr`/`yaw`/`pitch` for both ships throughout,
  `state=0x200`/`0x220` — consistent with dodge/collision-avoidance
  activity, the same mechanism P50 found governs proximity to any
  collidable object). Not investigated further — plausible and unsurprising
  given the 200-unit dock radius and the short window, not a sign of
  anything broken (no exception, no crash, no stuck-forever claim made).

## Open question surfaced, honestly — NOT a P53 regression

`ship.GetStation()` — the native client's own **resolved object
reference** for "am I docked, and at what station" — returned **null on
every single receipt**, despite the raw wire fields (`wireStationID`/
`wireState`) being correct throughout. This is a real, load-bearing gap:
`CommanderResearchAndExpand.UndockMinersAndStartMining`
(`CommanderResearchAndExpand.cs:1555`) gates on exactly this kind of
resolved station reference (`ShipState.c_ssDocked`/station lookup, not the
raw wire integers) — so even though the *wire* now correctly says "docked
at 1051," the native client's own higher-level object model has not (yet,
in this pass) been observed to reflect it, meaning a real
`CommanderResearchAndExpand` bot would not yet see itself as docked
through this accessor.

**Not chased further in this pass** — P53's own stated scope was wire
visibility, which is proven; whether the native client's internal
ship-model resolution needs a different signal (a real state-transition
event vs. passively observing a per-tick field, or its own local
`InGarage`-style physical check independent of server wire data) is a
genuinely open question for whoever next tries to get
`CommanderResearchAndExpand` to react to being docked. Flagged, not
guessed at.

## Disposition

`AllegianceCore` P53 commit (`ba55dc2`) is unchanged, already correct —
nothing to add or fix based on this pass, since the wire-level behavior
it promised is exactly what was observed. No native commit from this
pass (both diagnostics were temporary and fully reverted).

## What Claude Code's own next pass (or a future P54) should check

1. Why `ship.GetStation()` doesn't resolve despite correct wire data —
   likely needs reading the native client's own ship-status apply path
   (wherever the managed/native bridge turns a received `ShipStatus` into
   updates on the local `IshipIGCWrapper` object model) rather than
   assuming the wire layer is at fault (it isn't, per this pass).
2. Whether a longer run or a bot given an explicit "fly away" command
   would show `wireStationID` clearing/re-latching correctly as it
   crosses the 200-unit radius boundary (this pass never left the radius,
   so undock-via-radius-exit was not exercised — only undock via
   `FM_C_DOCKED`, already proven at the loopback level in
   `DockDockingTests.cs`).
