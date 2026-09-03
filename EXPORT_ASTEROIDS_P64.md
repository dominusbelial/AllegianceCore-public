# P64 — export asteroids to real WOPR clients

**Branch:** `master` (commit on top of `9fc49ed` P63)
**Scope:** server-side wire-export addition only. `AllegianceCore`-only, no
Windows VM, no native `Allegiance` repo involvement. Same shape as P35's own
warp/station export work.

## What this closes

Live testing (real WOPR bots, real server, P62+P63 both live) found the final
gap between *"a commander can see a docked miner"* (done) and *"a commander
dispatches that miner to mine"* (not yet): `CommanderResearchAndExpand
.UndockMinersAndStartMining`
(`~/Development/Allegiance/src/Wopr/Wopr/Strategies/CommanderResearchAndExpand.cs:1563-1568`)
searches
`GetClusters().Where(p => p.GetAsteroids().Where(r => r.GetName().StartsWith("He")).Sum(x => x.GetOre()) > 150)`
for a target cluster. `GetAsteroids()` returned nothing for every cluster,
always, because `AllegianceCore` had never sent asteroid data over the wire.

The map was swapped to `m2xspiderw.igc` (134 real rocks) back in the mining
arc, but `Server.cs`'s per-peer export loop still carried the original
**stale** comment (line ~1101) claiming "Asteroids ... are intentionally
skipped (none exist in this map ...)". That comment predated the map swap and
was never revisited. This commit exports the asteroids.

## Changes

1. **`src/Allegiance.Core/Data/DataAsteroidIGC.cs`** — added `Encode()`,
   the inverse of `Decode()`, producing exactly the documented 144 bytes
   (the class's own remarks already pin the exact offsets, incl. the
   corpus-measured 113-115 gap/offset correction). Encoder zero-fills the
   113-115 gap and every string field (13/13/13/25), mirroring
   `DataStationIGC.Encode`. Proven byte-round-trippable: the new test
   encodes+decodes all **973** corpus asteroid records and asserts every
   field component-wise (including the full 144-byte length).

2. **`src/Allegiance.Core/Wire/ExportAsteroid.cs`** — new `FM_S_EXPORT`(26)
   codec with `objecttype = 5` (`OT_asteroid` / `AGC_Asteroid`,
   `Inc/AGCIDL.idl:313`), structurally identical to `ExportStation.cs` /
   `ExportWarp.cs`: header(4) + var item(4) + objecttype(4) +
   `DataAsteroidIGC`(144) = **156** bytes total; `Encode`/`Decode`/`GetFmid`,
   plus `FromAsteroid(DataAsteroidIGC)`.

3. **`src/Allegiance.Server/Server.cs`** (per-peer export loop, ~1093-1118) —
   after warps and stations, iterate `sector.Asteroids` and
   `SendPeer(from, FedPacket.Pack(new[]{ ExportAsteroid.FromAsteroid(a).Encode() }))`
   for every asteroid of every sector. Same unconditional, one-time-at-join
   pattern as warps/stations (no fog-of-war gate — the codebase has no
   scouting/visibility system, `Server.cs:1105-1108`). Stale comment updated;
   the log line now counts asteroids (`OT_asteroid 5`). Treasure/mines/probes
   remain genuinely out of scope (absent from this map).

4. **`src/Allegiance.Bot/Bot.cs`** — decode `OT_asteroid(5)` EXPORTs into a
   new `AsteroidExports` list + `ResetAsteroidExports()`, so loopback tests can
   assert the peer actually received them.

5. **`tests/Allegiance.Core.Tests/AsteroidExportTests.cs`** — two proofs:
   (a) `DataAsteroidIGC` round-trips `Encode`↔`Decode` across all 973 corpus
   records; (b) a real loopback TCP `Bot` boot on `m2xspiderw.igc` (134 rocks)
   receives an `ExportAsteroid` (`objecttype=5`) for **every** asteroid of
   every sector, each byte-for-byte identical to the map's parsed
   `DataAsteroidIGC`, with at least one He3-named rock present.

## Build / test

`/home/domingo/.dotnet/dotnet test` (explicit path; the PATH `dotnet` is a
broken snap stub). 3 consecutive clean runs, all green:
`Passed! - Failed: 0, Passed: 2284, Skipped: 0, Total: 2284` (×3).

## What is proven here (wire level)

- The server now puts a real `FM_S_EXPORT(26)/OT_asteroid(5)` on the wire for
  every asteroid of every sector to every joining peer — loopback-verified
  byte-for-byte against the map's parsed `DataAsteroidIGC` (position, `Ore`/
  `OreMax`, `AsteroidId`, `Radius`, `ClusterId`, the real `"He..."` names from
  the map's own data, 25-char `Name`, `Fraction`).
- The codec is internally consistent: encode⇄decode round-trips across the
  whole 973-record asteroid corpus.

## What Claude Code's live-VM pass must confirm next (not this task)

- Does a real commander's `GetClusters().*.GetAsteroids()` finally return
  real, He3-named rocks (non-empty, name starting `"He"`)?
- Does `UndockMinersAndStartMining` finally find a valid target cluster
  (`Sum(x => x.GetOre()) > 150`) and issue a real dispatch order?
- The final payoff of the whole roadmap phase: watching a real miner undock
  and go mine.

Out of scope here (unchanged): `UndockMinersAndStartMining`,
`CommanderResearchAndExpand`, any WOPR/native-side file; per-tick ore-fraction
updates (already handled by the `[mine]`/ore-fraction mechanism on
`ServerShipUpdate`, P55's byte — the export is a one-time static snapshot at
join, matching every other EXPORT site in this codebase).
