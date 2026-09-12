# P45 — Exploration Spectator Backend (real ScoutExploreMap-ready WOPR AI)

**Repo:** `AllegianceCore` (branch `master`), `tools/vizfight`. Pure C# — no Windows
VM, no native `Allegiance` repo involvement. This task has no native-repo
counterpart.

**Commit:** P45 introduces the `explore` sub-mode of the P43/P44 spectator tool.
It re-points the *same* in-process server / real-cadence tick / WebSocket hub at
real `ScoutExploreMap` WOPR AI instead of `livecombat`'s scripted head-on fight:
every joining peer gets a weapon-less **Scout** hull, and the spectator stream
exports each ship's live **sector** plus one-time static **sector geometry**
messages. No new AI, no sim-logic changes — this is a read-and-export layer on
behavior proven in T14→P42 (autonomous exploration, warp transit, side
assignment).

---

## How to run

```bash
# from the AllegianceCore repo root:
dotnet run --project tools/vizfight -- explore [webPort]
#   webPort defaults to 7717; the server *transport* port is ephemeral (printed at boot).
```

Example:
```bash
dotnet run --project tools/vizfight -- explore 7799
```

Requires the stock artwork tree (ArtPath defaults to
`/home/domingo/Development/Artwork`): the static core `PCore001.igc` and the map
`XXV_simple.igc` (13 sectors, 38 warps, 2 stations).

- **Transport port** (where *bots / WOPR clients connect*): ephemeral — printed
  on boot as `[explore] server transport listening on 127.0.0.1:PORT`.
- **Spectator WebSocket + page**:
  `ws://127.0.0.1:<webPort>/ws` and `http://127.0.0.1:<webPort>/`

The tool spawns **no** bots. It accepts however many peers connect (any number,
including zero for a long while) and runs indefinitely until Ctrl-C.

---

## Mode switch (chosen: `explore` argument, combat kept intact)

The existing P43/P44 combat backend is preserved as the **default**
(`vizfight [webPort]`); `explore` selects the new backend. Rationale:

- Both backends share the identical server-boot / `Server.Tick()` real-cadence /
  WebSocket-fan-out machinery (`Program.cs:136-169` `SpectatorHub`,
  `Program.cs:201-237` `HandleWs`, `ExploreMain` at `Program.cs:543` reuses the
  same Kestrel + hub bootstrap), so the marginal cost of a second mode is a
  switch, not new plumbing.
- The combat demo already proved its job (4/4 kills, P43/P44). Keeping it behind
  the default does not regress P44; deleting it bought nothing since the two
  modes share ~all of the spectator infrastructure.

Deviation from the brief's "either is fine": I picked **keep both**, because the
shared machinery means the mode arg is genuinely cheap.

---

## What the spawner forces (and why)

`InjectExplorerSpawn` (`Program.cs:422-462`) replaces the stock spawner:

- **Hull:** `core.HullTypes.Values.First(h => h.Name.Contains("Scout"))` — the
  same selection P40 (`scoutsweep`) and P42 (`warptrial`) used. In PCore001.igc
  this is **"Lxy Scout" (HullId 92)** — the *first* hull whose name contains
  "Scout", so not the "Adv Scout" a skim of the brief might assume. The boot log
  prints the actual name (`[explore] hull 'Lxy Scout' (HullId 92) selected`).
  Without this force, the stock default spawn hull is "Adv Miner"
  (`livecombat/Program.cs:34` comment), and `ScoutExploreMap.IsCurrentShipAScout()`
  (a pure hull-name-contains-"Scout" test) would be **false**, dropping every real
  bot to the `ReturnToBase()` fallback instead of its real exploration logic.
- **No weapon.** This is exploration, not combat; `livecombat`'s `Fight` /
  `MountWeapon` logic is **not** reused at all. A weapon-less Scout can never
  fire, so the P43/P44 `kill` message is structurally impossible and is absent
  from the explore schema.
- **SideId round-robin.** `ship.SideId = mission.Sides[spawned % mission.Sides.Count].Id`
  mirrors the default-spawn reference (`Server.cs:1137`). This is **mandatory**
  (P42's found-and-fixed bug): a custom spawner that omits `SideId` makes the
  server skip a real client's `EXPORT(26)`, so a real native client cannot
  hydrate its own ship before `SET_CLUSTER`. Loopback tests mask this; the real
  Wopr.exe bots P46 launches will not.
- **Position/sector** mirror the stock default spawn (`Server.cs:1119-1121`):
  staged `x = 912·(1+peerIndex)`, `(y,z)=(731,921)`, into `mission.Sectors[0]`
  (= sector **1051 "Arrokoth"** on XXV_simple). The seeding counter is the
  tool's own because the `ShipSpawner` signature carries no peer count — the
  same approach vizfight's combat spawner already uses for its `placed` counter.

Join log (independent terminal proof): `[explore] peer N joined: ship <id>,
hull <name>, side <s>`.

Sector **change** is not re-logged by this tool — the server's existing
`[warp] ship ... crossed ...` line (`ServerHost.cs:205-206`, P41's own log) is
left as the sole crossing record, per the brief.

---

## WebSocket JSON schema (P46 consumes this exactly)

**URL:** `ws://127.0.0.1:<webPort>/ws`

### 1. `tick` — per sim tick (≈20 Hz, one frame per tick)

```json
{"type":"tick","simTime":0.25,"ships":[
  {"id":8096,"hull":"Lxy Scout","side":0,"sector":1051,
   "pos":[912,731,921],"fwd":[0,0,-1],"hp":1,"alive":true}
]}
```

Per-ship fields:

| field | type | meaning |
|---|---|---|
| `id` | short | ship id |
| `hull` | string | hull name (kept from P43/P44 — frontend never guesses it's a Scout) |
| `side` | short | `Ship.SideId`, `-1` if none |
| **`sector`** | short | **NEW** — the ship's live sector id (`Ship.Sector.Id`, `Sim/Ship.cs:85`), per-tick-correct via P41's `HitWarpEvent` reassignment |
| `pos` | float[3] | world position |
| `fwd` | float[3] | forward (identity-normalized, no `-0`) |
| `hp` | float | hull fraction 0..1 (`Ship.Fraction`) — kept; a Scout still has one |
| `alive` | bool | `!Ship.Dead` |

Removed vs combat: **no `kill` message** (weapon-less Scout can never fire —
structurally impossible). Everything else retained.

### 2. `sector` — one-time static geometry, once per newly-seen sector

Sent lazily (not all 13 up front): the first tick any ship is observed in a
sector emits that sector's geometry **once**, then caches it so it is never
re-built or re-sent per tick.

```json
{"type":"sector","id":1051,"name":"Arrokoth",
 "warps":[
   {"id":1051,"pos":[320,-5155.5557,1613.2656],"radius":100,"destSector":1057},
   {"id":1053,"pos":[3733.3333,3911.111,606.72705],"radius":100,"destSector":1052}
 ],
 "stations":[]}
```

| field | type | meaning |
|---|---|---|
| `id` | short | sector id (`Sector.Id`) |
| `name` | string | sector name (`Sector.Name`) |
| `warps[]` | array | this sector's warps (`Sector.Warps`, `Model/Sector.cs:41`) |
| `warps[].id` | short | warp id (`DataWarpIGC.WarpId`) |
| `warps[].pos` | float[3] | warp world position (`ResolvedWarp.Warp.Position`) |
| `warps[].radius` | short→number | warp collision radius (`DataWarpIGC.Radius`) |
| `warps[].destSector` | short | resolved destination **sector** id (`ResolvedWarp.Destination.Id`), `-1` for a dangling edge |
| `stations[]` | array | this sector's stations (`Sector.Stations`, `Model/Sector.cs:44`) |
| `stations[].id` | short | station id (`DataStationIGC.StationId`) |
| `stations[].pos` | float[3] | station world position (`DataStationIGC.Position`) |

**Confirmed against §3 of the brief**, with two noted details:

- **`destSector` resolves to the destination *sector* id, not the partner warp
  id.** The corpus encodes a warp's `destinationID` as the *partner warp*
  (`ResolvedWarp.Partner()`, `ResolvedWarp.cs:51-52`); the brief's sample
  `"destSector":1052` was illustrative. This tool exports
  `ResolvedWarp.Destination.Id` — the **sector** the warp leads to. On
  XXV_simple, warp 1051 in Arrokoth resolves to **sector 1057 "Haumea"**, and
  the live transit below proved that exactly.
- **Additional robustness beyond the brief:** the cached sector messages are
  flushed to each newly-connected WebSocket client (`hub.Seeder`,
  `Program.cs:556-562`), so a spectator that connects *after* a sector was first
  observed still receives its geometry without waiting for re-entry. The cache
  still guarantees no per-tick resend.

### Schema footnotes
- `radius` is the raw `short` from `DataWarpIGC` cast to a JSON number.
- Sector geometry reads only the static per-sector lists (`Warps`/`Stations`);
  nothing is computed new, and stations/warps that are absent (e.g. Arrokoth has
  no stations on this map) serialize as `[]`.

---

## Verification (real captured frames — loopback, not fabricated)

Using the same loopback-managed `Bot` harness as `WarpTransitServerTests` /
`livecombat`, against the **actual running `vizfight explore` binary**, a real
WebSocket client captured these frames (verbatim from the wire):

**(a) Joins as a Scout** — the tool log:
```
[explore] hull 'Lxy Scout' (HullId 92) selected for all peers
[explore] peer 1 joined: ship 8096, hull Lxy Scout, side 0
```

**(b) spawn-sector geometry + per-ship `sector` field** (real frames):
```json
{"type":"sector","id":1051,"name":"Arrokoth","warps":[{"id":1051,"pos":[320,-5155.5557,1613.2656],"radius":100,"destSector":1057},{"id":1053,"pos":[3733.3333,3911.111,606.72705],"radius":100,"destSector":1052}],"stations":[]}
{"type":"tick","simTime":0.25,"ships":[{"id":8096,"hull":"Lxy Scout","side":0,"sector":1051,"pos":[912,731,921],"fwd":[0,0,-1],"hp":1,"alive":true}]}
```

**(c) a real warp transit end-to-end** — the harness commanded the Scout toward
warp 1051 (whose position it read from the received sector message) and drove it
through the collision volume. The server's existing P41 log fired, the per-ship
`sector` field changed, and a **second** `sector` message arrived for the new
sector:
```
[warp] ship 8096 crossed from 'Arrokoth' (1051) to 'Haumea' (1057) via warp 1051
[warp] peer 1 ship 8096 -> SET_CLUSTER sector 1057 (...)
```
```json
{"type":"sector","id":1057,"name":"Haumea","warps":[{"id":1052,"pos":[-3484.4443,3804.4443,-1581.3357],"radius":100,"destSector":1051},{"id":1055,"pos":[3946.6667,3520,1391.2435],"radius":100,"destSector":1052},{"id":1058,"pos":[-3555.5557,-4053.3333,-283.76486],"radius":100,"destSector":1059},{"id":1061,"pos":[3271.111,-4302.222,-172.18161],"radius":100,"destSector":1060}],"stations":[]}
```
harness summary: `ship sector at end = 1057`, `distinct sector messages received: 2 [1051,1057]`,
`RESULT: PASS — real warp transit proved end-to-end`.

This proves: Scout hull spawn (a), lazy one-time `sector` geometry (b), and — via
real P41 warp transit through the real tool — the per-ship `sector` field
changing **and** a second `sector` message arriving for the new sector (c).

**Important, stated plainly:** this backend *expects real `Wopr.exe` bots to
connect from outside* (Claude Code's own follow-up job P46, not this task). A
loopback `Bot` proves the plumbing — `EXPORT(26)`/hydrate/SET_CLUSTER all work,
the schema is correct, transit works — but it is **not itself "real AI
exploring."** Real `ScoutExploreMap` autonomy (connecting, joining, target
selection, crossing real warps) is only exercised when real Wopr.exe processes
run against this backend in P46.

---

## Build / test status

```
dotnet build tools/vizfight/vizfight.csproj   → 0 warnings, 0 errors
dotnet test  tests/Allegiance.Core.Tests/…     → 3 consecutive runs, 2232/2232
                                                (run 1 fresh build; runs 2-3 --no-build)
```

Only `tools/vizfight/Program.cs` changed. No `src/Allegiance.Core` or
`src/Allegiance.Server` product logic was touched (the only addition there would
have been is exactly the accessibility-only pattern P43 already used — none was
needed here). Sector reassignment, warp-transit, and side assignment are all
pre-existing and proven (P41/P42); this task only **reads and exports** that
state.
