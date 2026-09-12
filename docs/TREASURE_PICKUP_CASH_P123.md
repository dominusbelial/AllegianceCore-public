# P123 — Treasure pickup (cash-only) — TREASURE(28)/DESTROY_TREASURE(30)/ACQUIRE_TREASURE(162)/TREASURE_ACK(163)/TREASURE_SETS(89)

Repo: `/home/domingo/Development/AllegianceCore`, branch `master`.
Baseline at start: `b7fdacf` (P122, doc-only). Baseline test count going in: **2575**
(re-confirmed by running the suite). Final count: **2596** (21 new tests), all green.

## What was built

1. `Data/DataTreasureIGC.cs` — decode of the individual treasure's wire-export
   struct (`DataTreasureIGC`, `igc.h:2517-2529`), 44 bytes. Distinct from the
   already-decoded `DataTreasureSetIGC` (the static-core catalog record): a
   treasure is a runtime object, so this decode is measured against the native
   struct layout, not a `.igc` corpus record — the two traps called out: a
   one-byte `TreasureCode` (a `char`, `igc.h:2086`, NOT a 4-byte enum) and a
   trailing `bool createNow`, which size the struct to 44, not 48.
2. The five codecs:
   - `Wire/TreasureSpawn.cs` — FM_S_TREASURE(28), embeds the full
     `DataTreasureIGC` (48 bytes = header + 44). **Name note:** the class is
     `TreasureSpawn`, not `Treasure`, because the port's sim object is already
     `Sim.Treasure` (mirroring `Mine`/`Probe`); keeping both named `Treasure`
     would be ambiguous in the host.
   - `Wire/DestroyTreasure.cs` — FM_S_DESTROY_TREASURE(30), 8 bytes
     (treasureID + sectorID).
   - `Wire/AcquireTreasure.cs` — FM_S_ACQUIRE_TREASURE(162), 10 bytes (char
     code + 1 interior pad + id + amount).
   - `Wire/TreasureAck.cs` — FM_C_TREASURE_ACK(163), 6 bytes (Mount char +
     1 pad; `c_mountNA` = −6).
   - `Wire/TreasureSets.cs` — FM_S_TREASURE_SETS(89), FM_VAR_ITEM array of 32-
     byte `TreasureSetRecord`s + `cTreasureSets`, with the chance blob omitted
     per the message's own comment (`Messages.h:624-628`). The record is the
     measured 32-byte `DataTreasureSetIGC` head (name[25]+id+count+zone+pad).
3. `Sim/Treasure.cs` — the static, pickup-able treasure model (port of
   `CtreasureIGC` reduced to cash pickup; no movement integration, no expiry
   this slice — both tied to the deferred spawn system, stated in remarks).
4. Sector pickup pass: `Sector.ResolveTreasurePickups` + `DrainTreasurePickups`
   (Model/Sector.cs) — the port's point-in-sphere stand-in for
   `CtreasureIGC::HandleCollision`.
5. Server wiring: `Server.PerformTreasurePickup` + `BroadcastTreasureDestroyed`,
   drained on the sim thread (Server.cs), and the join-sync `TREASURE_SETS`
   broadcast.
6. Bot capture of `TREASURE_SETS`/`DESTROY_TREASURE` for the loopback tests.
7. Tests: `Wire/TreasureWireTests.cs` (18 round-trip/byte-level) and
   `LiveTreasurePickupTests.cs` (3 live loopback proofs).

## Findings

### Pooled-money simplification — CONFIRMED and applied
Native `HitTreasureEvent` (FedSrv.CPP:13589-13616) splits the cash credit:
a **player** credits its autodonate target's per-player wallet
(`pfsDonate->SetMoney(...)`, broadcasts MONEY_CHANGE to the whole side's peer
group with `sidTo = pshipDonate`, `sidFrom = NA`), a **non-player** calls
`GiveSideMoney(ship->GetSide(), Money(amount))`. This port has no per-player
wallets and no autodonate — one pooled `Side.Money` per side (Model/Side.cs:52,
P118) — so the two branches collapse to the same side-pooled credit: credit the
picking ship's side by `amount`, broadcast `MONEY_CHANGE` side-wide via P118's
`BroadcastMoneyChangeSideWide(sideId, amount, ship.Id)` (`sidTo` = the picking
ship, the degenerate autodonate target; `sidFrom` = NA). Applied uniformly in
`Server.PerformTreasurePickup`.

### `c_habmRescue` reuse + side-agnostic + pilot-type — CONFIRMED
Pickup is gated on `GetPilotType() >= c_ptPlayer && GetBaseHullType()->HasCapability(c_habmRescue)`
(treasureIGC.h:83-98) — the **same `c_habmRescue` (0x02) bit** P114/P115 already
decoded and used for `RescueShipEvent` (DataHullTypeIGC.cs:98). No new capability
decode. **No side check** in the gate, and a cash treasure carries no side
(treasureIGC.cpp:128-149 sets a side only for flags) — side-agnostic confirmed:
any rescue-capable player ship on any side picks up any cash treasure. The only
side involvement is the *credit target* (the picking ship's own side). The
pilot-type leg collapses to always-true in this port (every Ship is a player
ship; `Ship.cs` has no pilot-type field, wire messages hard-code `c_ptPlayer`=10),
so the operative gate is the rescue capability. The pass still requires the ship
to be sided/missioned so the host has a side to credit (defensive; never trips
for a real server ship).

### Real spawn-trigger finding — it is RUNTIME, not mission-static
The `TREASURE(28)` spawn broadcast's send side is a **per-tick rate-based
regenerator**, not mission-static placement:
- `CFSMission::Update` accumulates treasure credits per cluster:
  `treasures = GetPendingTreasures() + dt · (bHome ? nPlayerSectorTreasureRate : nNeutralSectorTreasureRate)`
  (fsmission.cpp:2554-2582), spawning `floor(treasures)` via
  `GenerateTreasure(timeNow, pcluster, tsi)` with fractional remainder carried
  over. `tsi` = `pmp->tsiPlayerRegenerate` (home) / `pmp->tsiNeutralRegenerate`
  (neutral) — the designated treasure-set ids from the mission parameters.
- `CmissionIGC::GenerateTreasure` (missionigc.cpp:4125-4208) pulls
  `pts->GetRandomTreasureData()` — a **chance-weighted random pick** from the
  set's `TreasureData` chance table — computes a random position in a squashed
  disk (kept 500-ish from ships), and `CreateObject(OT_treasure,...)`, which is
  the path that broadcasts TREASURE(28).
- The message comment's hint is **CONFIRMED**: the chance weights are consumed
  entirely server-side by `GetRandomTreasureData`; clients never receive them
  (hence "ignore pargChance"). Zone/mission-start placement also calls
  GenerateTreasure (common.cpp:3093/3104) and the flag path respawns via it
  (FedSrv.CPP:13868).
Per the task's explicit scope, this task used a **single explicitly-placed test
treasure** (mirroring the mine/probe test setup); the full randomized
spawn-table algorithm is the named next task.

### Out-of-scope findings
- `c_tcFlag` (5): Phase 6 capture-the-flag. `KillTreasureEvent` respawns flags
  via `RespawnFlag` (FedSrv.CPP:12505-12506); `HitTreasure`'s flag leg sets the
  ship's flag id (shipIGC.h:1348-1353); `GAIN_FLAG(156)` is the broadcast. Not
  built here (roadmap says Phase 6).
- `c_tcPart` (1): requires Phase 1's still-unbuilt general parts drop/grant
  system. `HitTreasure` mounts the part into a free hardpoint or cargo slot,
  playing pickup/cargo-full sounds and returning the mount or `c_mountNA`
  (shipIGC.h:1245-1315). `TREASURE_ACK(163)`'s mount field is the client's
  acknowledgement of this docking. Separate, later dependency.
- Generic `ship->HitTreasure(tc, oid, amount)` fallback (FedSrv.CPP:13617-13618;
  shipIGC.h:1239-1358) covers: `c_tcPart` (mount, above), `c_tcDevelopment`
  (ForceComplete a bucket, shipIGC.h:1317-1325 — needs the still-unbuilt
  development/tech infra), `c_tcPowerup` (restore hull/shield/energy/fuel/ammo,
  shipIGC.h:1326-1346), `c_tcFlag` (set flag id). Investigated, not built.

### `DataTreasureIGC` decode precision
`sizeof(DataTreasureIGC)` = 44 bytes: p0(12)+v0(12)+lifespan(4)+time0(4)+
objectID(2)+treasureID(2)+clusterID(2)+amount(2)+treasureCode(char,1)+
createNow(bool,1), rounded to the 4-byte largest-member alignment. The char
`treasureCode` (not a 4-byte enum) is the trap that keeps the size at 44, not 48.

## Verification
- 21 new tests, all passing; full suite 2575 → **2596** green.
- Live loopback (in-process server + real TCP bots), confirmed by server logs:
  - `ship 8096 (rescue) picked cash amount=500 -> side0 money 0+500=500`
  - `MONEY_CHANGE(60) dMoney=500 sidTo=8096 sidFrom=NA to 2 same-side peer(s)`
  - `DESTROY_TREASURE(30) whole-mission to 3 peer(s)`, treasure removed from sector
  - `TREASURE_SETS(89) 1 set(s)` at join-sync mirroring `StaticCore.TreasureSets`
  The cash amount 500 is a genuine PCore001 treasure-set `Cash` entry
  (corpus-measured), not a fabricated value.
- No Windows VM needed; loopback Bot/in-process-server only, as scoped.

## Immediate next task
The real randomized chance-weighted spawn-table algorithm — `CmissionIGC::GenerateTreasure`
(missonigc.cpp:4125) plus the per-tick rate accumulator (fsmission.cpp:2554-2582),
mission params `tsiPlayerRegenerate`/`tsiNeutralRegenerate`/
`nPlayerSectorTreasureRate`/`nNeutralSectorTreasureRate`, and `GetRandomTreasureData`
chance weighting. That is what actually emits `TREASURE(28)`; this task's wired
pickup then completes the lifecycle (pickup → MONEY_CHANGE + DESTROY_TREASURE).
Following that, the other treasure codes (`c_tcPart` after Phase 1's parts
system, `c_tcPowerup`, `c_tcDevelopment`, then Phase 6 `c_tcFlag`).
