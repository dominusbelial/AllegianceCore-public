# P164 — BUILDINGEFFECT_DESTROYED(181) wire (Stage 2, Builder/station-construction)

Date: 2026-09-11 · Branch `master` on `02afa12` (P163) + P164 · Repo root
AllegianceCore port.

## What this lands

The first fmid in the whole Builder/station-construction arc: the server's
`BUILDINGEFFECT_DESTROYED(181)` interruption notice, wired onto the P163
building-effect mechanic's existing interruption branch. No creation-side wire
(this port has no client that renders a `BuildingEffect` — the creation notice
is explicitly out of scope, see Exclusions). Purely the *destroyed* notice fired
when a Builder dies mid-build.

The wire struct is a single field:
```
DEFINE_FEDMSG(S, BUILDINGEFFECT_DESTROYED, 181) { AsteroidID asteroidID; } END_FEDMSG
```
(`Inc/Messages.h:936-938`) — header(4) + asteroidID(2) = 6 B fixed, no var
items. The port's `FedMessageDumper` already mapped fmid 181 to
`BUILDINGEFFECT_DESTROYED` (`src/Allegiance.Core/Wire/FedMessageDumper.cs:45`),
so the number never moved; this task just gave it a codec and a sender.

## The send site, mirrored

Native sends it from `FedSrvSiteBase::KillBuildingEffectEvent`:
```cpp
// FedSrv.CPP:12484-12492
void FedSrvSiteBase::KillBuildingEffectEvent(IbuildingEffectIGC* pbe)
{
  BEGIN_PFM_CREATE(g.fm, pfmBuildingEffectDestroyed, S, BUILDINGEFFECT_DESTROYED)
  END_PFM_CREATE
  pfmBuildingEffectDestroyed->asteroidID = pbe->GetAsteroid()->GetObjectID();
  g.fm.SendMessages(m_pfsMission->GetGroupRealSides(), FM_GUARANTEED, FM_FLUSH);
  pbe->Terminate();
}
```
Notes confirmed from the trigger side in P163's own code: the interruption is
the `CshipIGC::Terminate` → `BuilderTerminated()` → `KillBuildingEffectEvent`
chain, and native only takes this path on the **interruption** branch — a build
that reaches its completion deadline transforms in place and sends nothing. So
this task wires only the interruption branch of
`Sector.ResolveBuildingEffects`, exactly as P163 scoped it.

## Scope item 1 — the wire message type

`src/Allegiance.Core/Wire/BuildingEffectDestroyed.cs` (new, 6 B fixed):
- `const ushort Fmid = 181`, `const int FixedSize = 6`.
- single `public short AsteroidId` field (the port's wire-naming convention for
  asteroid ids, matching `AsteroidDestroyed.AsteroidId` /
  `AsteroidMined.AsteroidId` / `AsteroidsUpdate.AsteroidId`).
- `Encode`/`Decode` follow `DestroyTreasure.cs`'s exact pattern: `WireWriter`
  header-cbmsg-placeholder + `WriteU16(Fmid)` + `WriteI16(AsteroidId)` +
  `WriteU16At(0)` layout-drift guard; `Decode` validates fmid + cbmsg then reads
  the single short. This is structurally the same shape as the P123
  `DESTROY_TREASURE(30)` codec (`DestroyTreasure.cs`, whole-mission broadcast) —
  the precedent the task named; not a new invention.

## Scope item 2 — the pending-interruptions accumulate/drain pair

Follows the established "Core resolver accumulates, Server drains after
`Mission.Update()`" bridge exactly as `_pendingTreasurePickups` +
`DrainTreasurePickups` (`Sector.cs:953-958`) and the ripcord pair
(`DrainRipcordTeleports`, `Sector.cs:621-626`; `DrainRipcordAborts`,
`Sector.cs:964-969`):
- `private readonly List<short> _pendingBuildingEffectInterruptions = [];`
  (`Sector.cs:177`) — accumulates the interrupted build's **target asteroid id**.
- Accumulation happens in the P163 interruption branch of
  `Sector.ResolveBuildingEffects` — the `if (effect.Builder.Dead)` block —
  which now records `effect.Target.Data.AsteroidId` before removing the effect
  (`Sector.cs:2476-2479`). The `Data.AsteroidId` is the port's asteroid
  object-id as it already rides `ASTEROIDS_UPDATE`/`ASTEROID_DESTROYED`, i.e.
  the port analogue of native `pbe->GetAsteroid()->GetObjectID()`.
- `public IReadOnlyList<short> DrainBuildingEffectInterruptions()`
  (`Sector.cs:975-981`) — snapshot + clear, the same idiom as every other drain.

## Scope item 3 — the broadcast helper + call site

`Server.BroadcastBuildingEffectDestroyed(short asteroidId)`
(`Server.cs:4664-4678`) mirrors `BroadcastTreasureDestroyed`
(`Server.cs:4626-4639`) exactly: build the msg, `FedPacket.Pack`, then iterate
`_peerShips` **without any side filter** calling `SendPeer` per peer
(`foreach (var kv in _peerShips) { SendPeer(kv.Key, packet); n++; }`), plus a
log line. That unfiltered `_peerShips` sweep is the port's rendering of native
`GetGroupRealSides()` = whole-mission, every real side — confirmed by reading
the treasure helper, not assumed.

Call site: the host's `SimPass` (the tick loop, `Server.cs:436` `TickSim()` →
`SimPass()`), inserted immediately after the P123 treasure drain
(`Server.cs:507-516`), in the same tick slot as the ripcord/treasure/carrier
drains, before the `BroadcastShipStates` observation send. Same
`foreach mission → foreach sector → foreach drained-item` shape as every other
drain there.

## Scope item 4 — the live wire test

`tests/Allegiance.Core.Tests/LiveBuildingEffectWireTests.cs` (new) — the
`LiveTreasurePickupTests` live-loopback precedent (real in-process server, real
TCP bots): seat two observer bots (one per side), inject a synthetic asteroid +
a standalone Builder (not a bot's ship) via the same `PlaceAsteroid`/`PlaceShip`
injection P163's `BuildingConstructionLiveTests` uses, start the build with
`Sector.StartBuildingEffect`, kill the Builder with a real lethal
`ReceiveDamage` (the P163 `KillShip` recipe), tick, and assert every peer — same
and cross side — received `BUILDINGEFFECT_DESTROYED(181)` carrying
`rock.Data.AsteroidId`.

Plus `tests/Allegiance.Core.Tests/Wire/BuildingEffectDestroyedWireTests.cs`
(new): pins the 6-byte layout, round-trips the single field, and emits exact
bytes via the declarative `ByteBuf` builder — the `Wire.TreasureWireTests`
DestroyTreasure-section pattern.

`Allegiance.Bot/Bot.cs` gains a `_buildingEffectDestroyed` capture
(`Bot.cs`), accessor `BuildingEffectDestroyeds` + reset, and a
`case BuildingEffectDestroyed.Fmid:` decode arm, so the live test can observe
the arrival — the same recording the Bot already does for `DestroyTreasure`.

## Verification

- New codec tests (4) + new live wire test (1): all pass.
- P163 `BuildingConstructionLiveTests` (2) + P123 `LiveTreasurePickupTests` (3)
  pass unmodified — the added drain call alongside the ripcord/treasure ones in
  the same tick loop changed nothing for them.
- Full suite run (3 consecutive clean runs) recorded below.
- No VM used; `ps aux | grep qemu` shows no active VM during verification.
- Suite size: 2789 (P163 checkpoint) + 5 new = 2794 going in.

## Explicit exclusions (confirmed, not skipped)

- **No creation-side wire or visibility broadcast for a new `BuildingEffect`.**
  Native's creation rides a generic visibility/object-export path; this port
  has no visual-effects system and no client renders one (established through
  the P131-139 visibility work and P163's own deferral). Singular target is the
  *destroyed* notice.
- **Builder AI decision layer** (`Ship.PilotTypeBuilder` default-order choice)
  — a separate, later Stage 3, closely mirroring P161's Layer pattern.
- **No change to P103-P163 code** beyond the new wire type, the
  accumulate/drain pair, and the broadcast helper + its call site.
