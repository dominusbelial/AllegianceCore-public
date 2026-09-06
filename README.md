# AllegianceClientCore

A from-scratch **client-side** reimplementation of the network/game-state layer
for a 2004-era multiplayer space game, built on top of the wire codecs in the
sibling repository **AllegianceCore** (which is a read-only reference and
dependency here — nothing under it is modified).

This repo is the first real slice of a *client* that models actual game state:
an **entity table keyed by object id**, fed only from real wire traffic against
a real in-process AllegianceCore server. It is deliberately not the full
protocol — see [Scope](#scope) for exactly what is and is not covered.

Sibling repos (both read-only from this one's perspective):
- `/home/domingo/Development/AllegianceCore` — the server-side wire codecs
  (`Allegiance.Core`) and a real in-process test server (`Allegiance.Server`)
  that the integration test boots.
- `/home/domingo/Development/Allegiance` — the native client; not used here.

---

## Building

From the repo root:

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build
```

`Directory.Build.props` is copied verbatim from AllegianceCore
(net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors=true, LangVersion
latest), so warnings fail the build. No NuGet packages beyond the xunit /
coverlet / Microsoft.NET.Test.Sdk set that AllegianceCore's own test project
uses (matched exactly, see `tests/Allegiance.ClientCore.Tests/*.csproj`).

## Testing

```bash
dotnet test
```

Both suites run against a real in-process server and real loopback TCP; see
below for what each proves. Added later: `ClientWorldDockLoadoutTests`
(no-socket unit tests for the new docking/loadout `Apply` cases),
`DockLoadoutLiveTests` (dock/undock and BUY_LOADOUT+fLaunch launch/undock round
trips against a real server), and a teammate-movement unit test inside
`ClientWorldUnitTests`.

### `ClientWorldUnitTests` — no sockets, no server
Constructs `SetCluster`, `SingleShipUpdate`, and `ShipStatusMessage` instances
directly (the same construct-then-round-trip style AllegianceCore's
`Wire/*Tests.cs` uses) and feeds all three through `ClientWorld.Apply` with the
**same ship id**. Proves the core claim of this task: three different message
types targeting one id upsert **one** `ShipEntity`, never three.

- `world.Ships.Count == 1` (not 3)
- `world.MyShipId` set from the `SetCluster` call
- `world.Ships[id].Latest.Position` reflects whichever `ServerShipUpdate` was
  applied last
- `world.Ships[id].SectorId` reflects the `ShipStatusMessage` value
- the *same* `ShipEntity` instance is upserted (identity preserved), not replaced

### `JoinFlowLiveTests` — `[Collection("LiveNetwork")]`
Contains its own `[CollectionDefinition("LiveNetwork", DisableParallelization =
true)]` (xUnit collection definitions do not cross assemblies). Boots a real
in-process `Allegiance.Server.Server`, connects a real `ConnectionSession` over
real loopback TCP, logs on, waits for the ship assignment, commands nonzero
forward thrust, and pumps several real server ticks — then asserts against the
real server's output:

- `world.MyShipId == world.Ships.Keys.Single()`
- `world.Ships[id].IsMine == true`
- `world.Ships[id].SectorId` equals the server's real `SetCluster.SectorId`
- after the thrust command, `world.Ships[id].Latest.Position` has actually
  changed from its initial value and `LastUpdateTick` has advanced — this is
  what proves the entity table tracks a real, ticking server, not a canned
  fixture.

---

## Project layout

```
src/
  Allegiance.ClientCore/            the client library (references AllegianceCore's Core only)
    State/ClientWorld.cs            pure domain model — the entity table (no I/O, no locking)
    State/ShipEntity.cs             one ship, accumulated incrementally from wire messages
    Session/ConnectionSession.cs    ITransportSite wrapping TcpTransport, drives ClientWorld
  Allegiance.ClientCore.Explorer/   console smoke-test tool (binds a real in-process server)
tests/
  Allegiance.ClientCore.Tests/      xunit: ClientWorldUnitTests + JoinFlowLiveTests
```

### `Allegiance.ClientCore.csproj`
References **only** `../../../AllegianceCore/src/Allegiance.Core/Allegiance.Core.csproj`.
A client library has no business depending on a server.

### `Allegiance.ClientCore.Tests.csproj`
References your own `Allegiance.ClientCore`, **plus** AllegianceCore's
`Allegiance.Core` **and** `Allegiance.Server` — the server reference exists
ONLY so tests can boot a real in-process server; production code never
references it.

### `Allegiance.ClientCore.Explorer`
Analogous to AllegianceCore's `tools/livecombat` / `tools/chatdemo`: boots a
real in-process server, connects a `ConnectionSession`, logs on, commands
forward thrust, and prints a periodic one-line world snapshot (ship count, my
ship, my sector, my position, latest tick). It is a manual smoke-test
convenience, not part of automated verification. Requires the artwork tree at
`/home/domingo/Development/Artwork`.

```bash
dotnet run --project src/Allegiance.ClientCore.Explorer
```

---

## Scope

### Covered today
`ClientWorld.Apply(IFedMessage)` handles these inbound types (plus `LogonAck` in
`ConnectionSession` for connect proof). The ship rows all upsert by ship id into
one `ShipEntity` per id; the EXPORT object rows upsert into their own id-keyed
dictionaries (replace-on-arrival — EXPORT is one-shot per object per join, so
last-write-wins is correct, no diffing needed):

| fmid | Message | What the entity table learns |
|------|---------|------------------------------|
| 5    | `BuyLoadoutAck` | Upserts `Ships[MyShipId]` with `HullId` and `Loadout` (the ack's expanded-part tail, mount order; empty list = hull-only loadout). Routed only to the requesting peer (Server.cs ApplyLoadout → SendPeer(from)), so it always concerns our own ship. A join-burst adopt-a-hull ACK (empty tail) precedes any request of ours, so `Loadout` is `[]` from join onward. |
| 7    | `SetCluster` | `MyShipId` on first arrival; upserts `Ships[Ship.ShipId]` with `IsMine=true`, `SectorId`, and `Latest = Ship` (**stored verbatim**). |
| 78   | `SingleShipUpdate` | Upserts `Ships[OidTarget]` with `Latest = Ship` and `LastUpdateTick = Ship.Time`. Applied **unconditionally** — a non-self `OidTarget` (teammate) is tracked too, exactly like our own. |
| 90   | `ShipStatusMessage` | Upserts `Ships[ShipId]` with `HullId` / `SectorId` / `StationId` / `ParentId` / `State` / `Unknown` / `Detected` / `StateTime`. |
| 61   | `DockedNotify` | Upserts `Ships[MyShipId].StationId` — the *authoritative* dock/undock latch (the native client's sole `SetStation` site, appmsg.cpp:1113), applied in addition to the `StationId` SHIP_STATUS already carries. Routed only to the docking peer. |
| 10   | `PlayerInfo` | Upserts `Ships[ShipId]` (create-if-absent) with `Player` attached and `CharacterName` if empty. |
| 26   | `ExportShip` (OT_ship=0) | Upserts `Ships[ShipId]` with `HullId` / `SideId` / `CharacterName`; `IsMine = (ShipId == MyShipId)` computed at upsert time — this is how a ship **other than mine** first enters the table. The server's own-ship `ShipId=NA(-1)` placeholder is skipped (it has no real id), so it never creates a phantom `Ships[-1]`. |
| 26   | `ExportCluster` (OT_cluster=13) | Upserts `Clusters[ClusterId]`. |
| 26   | `ExportStation` (OT_station=1) | Upserts `Stations[StationId]`. |
| 26   | `ExportWarp` (OT_warp=7) | Upserts `Warps[WarpId]`. |
| 26   | `ExportAsteroid` (OT_asteroid=5) | Upserts `Asteroids[AsteroidId]`. |
| 200  | `ClusterInfo` | Populates `Graph` (a `ClusterGraph` holding the message's own `Clusters`/`Warps`) exactly once. |

`ConnectionSession` dispatches each new fmid on the receive path, branching the
shared `ExportShip.Fmid` (26) on the raw 4-byte `objecttype` at offset 8 before
decoding — the same branching `Allegiance.Bot.Bot.cs` uses. The two new inbound
cases (`DockedNotify`(61), `BuyLoadoutAck`(5)) are dispatched straight to the
world, and the two new outbound sends (`SendDocked`, `SendBuyLoadout`) mirror
`Bot.cs` exactly.

### Not yet covered (honest starting point for the next delegated task)
Everything else the wire protocol carries passes through `ClientWorld.Apply`
unhandled today:

- **chat** — `ChatMessage`(23) / `ChatBuoy`(108)
- **teammate ship movement (end-to-end)** — the CLIENT already models it:
  `ConnectionSession` feeds every inbound `SINGLE_SHIP_UPDATE(78)` to
  `ClientWorld.Apply` unconditionally, and `Apply` upserts `Ships[OidTarget]`
  with `Latest` regardless of whether the id is ours — verified by a passing
  unit test (`SingleShipUpdate_ForNonSelfShip_MovesTeammateEntitysLatest`).
  What a live two-ship test proved is that the read-only reference **server**
  never broadcasts teammate movement to observers: it sends each ship's
  `SINGLE_SHIP_UPDATE` only to that ship's own peer (`Server.cs` BroadcastShipStates,
  `SendPeer(kv.Key, …)`), so no teammate-position stream exists for a client to
  observe. Fixing that is a server change under `AllegianceCore/` (read-only
  here), not a client defect — hence this stays listed until the server
  broadcasts teammate updates. Our own `ShipId=NA(-1)` EXPORT is intentionally
  skipped (no real id), so our own entity's `SideId` stays `0` until a real-id
  export targets it; self-side is currently read from `PlayerInfo.ISide` instead
- **mission info** — `MissionDef`, `MissionStage`, payday, kill-ship, player-ready
  relays (`ClusterInfo` is covered above)
- **`IsMine` early-arrival caveat** — an `ExportShip` for a ship other than mine
  that lands before our `SET_CLUSTER` stays `IsMine=false`; once `SET_CLUSTER`
  assigns our own id it is marked `IsMine=true` (authoritative), so no persistent
  wrong `IsMine` occurs — no reconciliation logic is built
- any outbound message beyond `LogonRequest`, `CommandControls` (controls),
  `Docked`(48, undock), and `BuyLoadoutRequest`(4) — e.g. chat sends
