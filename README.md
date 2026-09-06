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
(no-socket unit tests for the docking/loadout `Apply` cases), `DockLoadoutLiveTests`
(dock/undock and BUY_LOADOUT+fLaunch launch/undock round trips against a real
server), a teammate-movement unit test inside `ClientWorldUnitTests`,
`ClientWorldEventLogTests` (no-socket unit tests for the event log and mission
info), and `TeammateMovementLiveTests` (the restored teammate-movement
end-to-end test plus a chat-relay test). Added later still:
`ClientWorldRosterTests` (no-socket unit tests proving `Roster` upserts the
last ready state while `Events.PlayerReadyEchoes` accumulates history),
`PlayerReadyLiveTests` (SendPlayerReady round-trips to a whole same-side team,
updating both peers' `Roster`), and a real-part loadout test inside
`DockLoadoutLiveTests` (`BuyLoadout_WithRealParts_PopulatesShipLoadout_Live`
— BUY_LOADOUT with a genuine part list mounts a real part into
`ShipEntity.Loadout`, not an empty hull-only tail).

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

### `TeammateMovementLiveTests` — `[Collection("LiveNetwork")]`
Boots a real in-process server and seats **three** real `ConnectionSession`s
across **two** teams (round-robin → a guaranteed same-side pair plus a
cross-side enemy), then commands real forward thrust on the teammate (and the
enemy, so both are dirty every tick) and samples the **observer's own
`ClientWorld`** each server tick:

- the teammate's `Ships[id].Latest.Position` streams through ≥ 3 distinct
  positions (a spawn + two real movements) — proof of a live `SINGLE_SHIP_UPDATE`
  broadcast, not a single canned update
- the cross-side enemy never enters the observer's entity table at all
  (`DoesNotContain(enemyId, Ships.Keys)`)

This is the end-to-end teammate-movement test that was removed last task
because the read-only reference server only sent each ship's update to its own
peer. That server gap has since been fixed (the sibling repo's P85
`BroadcastShipStates` change), and this test now proves it from the client's
side of the wire. Also in this file: a chat-relay test where one session's
`SendChat` lands in the other's `EventLog.Chats` with the real sender ship id,
plus the sender's own echo.

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
| 23   | `ChatMessage` | Appends to `Events.Chats` — a transient event ("what just happened"), no entity-keyed upsert. |
| 108  | `ChatBuoy` | Appends to `Events.ChatBuoys` (chat targeting a buoy object). |
| 114  | `Payday` | Appends to `Events.Paydays` (per-side payday payout). |
| 105  | `KillShip` | Appends to `Events.Kills` (a death, victim + credited killer). |
| 45   | `PlayerReady` | Upserts `Roster.ReadyByShipId[ShipId] = FReady` (current state — last write wins) **and** appends to `Events.PlayerReadyEchoes` (the server's relay of a ready announce; arrival-order history — both halves kept, for different questions). |
| 38   | `MissionDef` | Sets `Mission` (static-core file/version, cookie, side-owner, stage) — mission state, distinct from any ship. |
| 37   | `MissionStageMessage` | Updates just `Mission.Stage` (whichever of `MissionDef`/`MissionStageMessage` applied last owns `Stage`). |

`ConnectionSession` dispatches each new fmid on the receive path, branching the
shared `ExportShip.Fmid` (26) on the raw 4-byte `objecttype` at offset 8 before
decoding — the same branching `Allegiance.Bot.Bot.cs` uses. The inbound cases
(`DockedNotify`(61), `BuyLoadoutAck`(5), `ChatMessage`(23), `ChatBuoy`(108),
`Payday`(114), `KillShip`(105), `PlayerReady`(45), `MissionDef`(38),
`MissionDef`(38), `MissionStageMessage`(37)) are dispatched straight to the world, and the
outbound sends (`SendDocked`, `SendBuyLoadout`, `SendChat`, `SendPlayerReady`)
mirror `Bot.cs` exactly. The team-lobby roster (`ClientWorld.Roster`, a
`TeamRoster`) is the current-state twin of the `PlayerReadyEchoes` history —
see below.

The event log (`ClientWorld.Events`) is deliberately NOT entity-table state:
chat/kills/paydays are pure arrival-order events with no durable identity to
key on, so they append (bounded at 500/list) rather than upsert — the same
flat-`List<T>` pattern `Bot.cs` uses for its `_chats`/`_chatBuoys`/`_paydays`/
`_kills`, while `Mission` is the small amount of *persistent mission* state
distinct from any one ship.

The team-lobby roster (`ClientWorld.Roster`, a `TeamRoster`) splits the
difference the native client draws too: PLAYER_READY(45) is both an event
("someone toggled") and current state ("are they ready now?"). The roster is
the **entity-table half** — keyed by ship id, upserted (`ReadyByShipId[ShipId]
= FReady`, last write wins), so a lobby queries `Roster.ReadyByShipId[id]` /
`TryGetValue(id)` for a snapshot of "who is currently ready" the way the native
`PlayerInfo::Update` sets `m_fmPlayerInfo.fReady` from the relay. It starts
empty (never null) and only holds ships that have actually toggled — absence
reads as "not ready". The `PlayerReadyEchoes` list stays as the append-only
**history half**; both are kept because they answer different questions.

### Not yet covered (honest starting point for the next delegated task)
Everything else the wire protocol carries passes through `ClientWorld.Apply`
unhandled today:

- **parts/mount detail beyond the mounted-tail** — BUY_LOADOUT/ACK with a
  non-empty part list now round-trips into `ShipEntity.Loadout` verbatim (proved
  live), but deeper part semantics (affinity, loadout re-arming, negative mount
  quantities) are not modeled
- **any outbound message beyond `LogonRequest`, `CommandControls` (controls),
  `Docked`(48, undock), `BuyLoadoutRequest`(4), `SendChat`(23), and
  `SendPlayerReady`(45)** — e.g. dock-request, ripcord, etc.
- **`IsMine` early-arrival caveat** — an `ExportShip` for a ship other than mine
  that lands before our `SET_CLUSTER` stays `IsMine=false`; once `SET_CLUSTER`
  assigns our own id it is marked `IsMine=true` (authoritative), so no persistent
  wrong `IsMine` occurs — no reconciliation logic is built
- beyond `IsMine`, the ship entity deliberately models a subset of fields (the
  `ServerShipUpdate` verbatim payload plus the coarse `SHIP_STATUS`/`EXPORT`/
  `PLAYERINFO`/loadout fields); finer weapon/armor/energy internals are not
  flattened into separate properties
