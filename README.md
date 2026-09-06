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
below for what each proves.

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

### Covered today — exactly three inbound message types
`ClientWorld.Apply(IFedMessage)` handles only these three (plus `LogonAck` in
`ConnectionSession` for connect proof):

| fmid | Message | What the entity table learns |
|------|---------|------------------------------|
| 7    | `SetCluster` | `MyShipId` on first arrival; upserts `Ships[Ship.ShipId]` with `IsMine=true`, `SectorId` (from the message's top-level `SectorId`), and `Latest = Ship` (**stored verbatim**, never re-flattened). |
| 78   | `SingleShipUpdate` | Upserts `Ships[OidTarget]` with `Latest = Ship` and `LastUpdateTick = Ship.Time`. |
| 90   | `ShipStatusMessage` | Upserts `Ships[ShipId]` with `HullId` / `SectorId` / `StationId` / `ParentId` / `State` / `Unknown` / `Detected` / `StateTime` (the real `ShipStatusMessage` field names, read directly from `Wire/ShipStatus.cs`). |

All field names come directly from the real `Allegiance.Core.Wire` codecs —
nothing is guessed.

### Not yet covered (honest starting point for the next delegated task)
Everything else the wire protocol carries passes through `ClientWorld.Apply`
unhandled today:

- **EXPORT** cluster / station / warp / asteroid object creation
  (`FM_S_EXPORT`, the `OT_cluster`/`OT_station`/`OT_warp`/`OT_asteroid` cases)
- **chat** — `ChatMessage`(23) / `ChatBuoy`(108)
- **docking** — `DockedNotify`(61), the dock/undock half of `Docked`(48), and the
  `BuyLoadout`-style launch path
- **loadout** — `BuyLoadoutRequest`(4) / `BuyLoadoutAck`, part mounts
- **teammate ship visibility** — one-shot `ExportShip`(26, `OT_ship`) +
  `PlayerInfo`(10) teammate exports/relays, and teammate `ShipStatus` broadcasts
- **mission info** — `ClusterInfo`(200), `MissionDef`, `MissionStage`, payday,
  kill-ship, player-ready relays
- any outbound message beyond `LogonRequest` and `CommandControls` (controls)

None of the above is wired into `ClientWorld`; the entity table today tracks
only a ship's id, "is mine", latest `ServerShipUpdate`, and the `ShipStatus`
fields. New inbound message types should be added as new `case`s in
`ClientWorld.Apply`, each upserting by ship id so the one-entity-per-id invariant
is preserved.
