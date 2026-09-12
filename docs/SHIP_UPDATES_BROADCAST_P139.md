# SHIP_UPDATES_BROADCAST_P139.md

**Task:** P139 — wire the LIGHT/HEAVY_SHIPS_UPDATE (175/176) broadcast: per
player, per tick, from P138's per-cluster candidate list, with target-lock
priority. The consumer that finally makes "see other ships" real in this port.
**Branch:** `master` (started at `d164870` P138; top now P139).
**Scope met:** `Server.BroadcastShipsUpdate` per-player assembly + 3 live
loopback tests.

---

## 0. Headline

`BroadcastShipsUpdate(uint tickIndex)` is called every `SimPass` (after
`BroadcastProbesUpdate`, `Server.cs:530-544`) and, for each connected seated
peer, assembles a `HEAVY_SHIPS_UPDATE(176)` from `Sector.GetShipUpdateCandidates()`
(P138) — the peer's command target (if a ship in-cluster) in the **first heavy
slot**, every remaining candidate minus the peer's own ship as heavy records,
the per-recipient scalar region filled correctly — and sends it to that peer's
**own connection only**. `LIGHT_SHIPS_UPDATE(175)` is built but never emitted
(decision §2). This is the first time one player's client is told about another
player's ship in this port.

Files touched (all additive / wiring, no P103-P138 logic changed):

| File | Change |
|---|---|
| `src/Allegiance.Server/Server.cs` | `BroadcastShipsUpdate` (+`TargetInCluster`, `Percent` helpers); per-tick call after `BroadcastProbesUpdate` |
| `src/Allegiance.Bot/Bot.cs` | capture collections + receive cases for fmid 175/176 (test observer) |
| `tests/Allegiance.Core.Tests/ShipsUpdateLiveTests.cs` | 3 live loopback tests |

---

## 1. Confirmations (each read in source)

### 1.1 Per-player, not per-side — CONFIRMED

Native `SendShipUpdates` assembles and sends per **player**
(`FedSrv.CPP:4029` loops the cluster's player list; the send target is
`pfsPlayer->GetConnection()`, `FedSrv.CPP:4189`). This port's
`BroadcastShipsUpdate` loops `_peerShips` (each seated peer) and sends to
`kv.Key` (that peer's `ConnectionId`) via `SendPeer` — the P118 `SET_MONEY`
per-player shape, **not** the P133-135 per-side batching (`connsBySide`).
Each player's message is assembled independently (own target, own position
reference, own fractions).

### 1.2 Target-lock resolution — REUSED, not invented

The port's existing "what is this ship's current command target" concept is
`Ship.CommandTarget` (`src/Allegiance.Core/Sim/Ship.cs:479`), the port of
`m_commandTargets[c_cmdCurrent]` (`shipIGC.h:2624`) already consumed by the
weapon-aim slice. `BroadcastShipsUpdate` reads `self.CommandTarget` and applies
the native `GetCommandTarget(c_cmdCurrent)` + cluster gate + `OT_`-switch
(`FedSrv.CPP:4081-4124`). No new targeting system.

### 1.3 `bpTargetHull`/`bpTargetShield` by target type — CONFIRMED (with a port limitation)

Native switch (`FedSrv.CPP:4084-4123`), mapped to the reachable C# types:

| Native case | Port mapping | bpTargetHull | bpTargetShield | heavy slot? |
|---|---|---|---|---|
| `OT_ship` | `case Ship ts` (`ts != self && ts.ParentShip is null`) | `ts.Fraction` | `ts.ShieldFraction` | **YES — first slot** |
| `OT_probe` / `OT_asteroid` / `OT_missile` | `case Probe p` | `p.Fraction` | none | no |
| `OT_station` | *(unreachable — see below)* | — | — | no |

**Port limitation (cited plainly):** `Ship.CommandTarget` is typed `Model?`,
and `Station` (`IStationIgc, ISideVisible`) and `Asteroid`
(`IAsteroidIgc, ISideVisible`) are **not** `Model` subclasses, so the native
`OT_station`/`OT_asteroid` target branches (which read
`pstation->GetFraction()/GetShieldFraction()` and `asteroid->GetFraction()`,
`FedSrv.CPP:4108-4122`) are **structurally unreachable** through this port's
command-target type. A `Missile` target *is* reachable (Missile is a `Model`)
but this port's `Missile` carries no damage-fraction concept (native
`IdamageIGC::GetFraction()` has no port equivalent), so it likewise leaves
`bpTargetHull` at the 255 "no target" sentinel. The ship and probe cases — the
ones actually expressible — are implemented faithfully and live-tested.

### 1.4 `pshipTarget->GetHasUpdate()` gate — SKIPPED (consistent with P138)

Native promotes the target ship to the first heavy slot only if
`pfsTarget->GetHasUpdate()` (`FedSrv.CPP:4102-4103`). P138 dropped the
recent-update timestamp gate (no per-ship "fresh update arrived this interval"
concept in this server-authoritative port), so every non-boarded flying
candidate counts as having an update — the target ship is always promotable
when it is a candidate. Cited in the method doc.

### 1.5 Tick cadence — MATCHED

Called every tick in the same `SimPass` pass that drives
`BroadcastShipStates`/`BroadcastStationsUpdate`/`BroadcastAsteroidsUpdate`/
`BroadcastProbesUpdate` (`Server.cs:509-544`), the port's established per-tick
cadence (P133-135 convention), not a new mechanism. The tick index
(`(uint)TickIndex`) serves as `timeReference` (`g.timeNow`, `FedSrv.CPP:3996`),
the same value `BroadcastShipStates` stamps `ServerShipUpdate.Time` with.

---

## 2. The heavy/light split decision (stated plainly)

**Decision: every candidate gets a HEAVY record; the LIGHT tier is not
populated and `LIGHT_SHIPS_UPDATE` is therefore never emitted.**

Reasoning: native's heavy-vs-light assignment is the *output* of the
deviation-sorted bandwidth budget — `qsort(..., CmpShipDeviation)`
(`FedSrv.CPP:3923`) then heavy/light/turret budgets of 2/2/2
(`FedSrv.CPP:3844-3846`). P138 explicitly deferred that whole budget to P141.
Without the deviation sort there is no principled basis to choose *which*
ships are light versus heavy, so the simplest honest choice is to send every
candidate as heavy — exactly what the P136 codec's `HeavyShipsUpdate.FromShips`
already does, and what P138 §5 names as its recommended default. This is **not**
silent under-building: it is the native structure with only the traffic-shaping
piece (deferred) removed. Consequences:

- `LIGHT_SHIPS_UPDATE` is never sent, matching native's
  "forward light only when non-empty" rule (`FedSrv.CPP:4183-4188`) — with an
  empty light list, native would not send it either.
- `HEAVY_SHIPS_UPDATE` is sent every tick to every seated peer, even with zero
  entries, matching native's explicit rule — "Send the heavy ship update
  whenever the player is flying since it has hitpoint information"
  (`FedSrv.CPP:4179-4180`): the message's scalar region (own fractions +
  target hull/shield) is meaningful even for a lone player.

P141 can reintroduce the light tier (and the deviation budget) without touching
this wiring shape — the LIGHT codec and the Bot observer are already in place.

---

## 3. Turret sub-lists (P137) — DEFERRED with reason

`ServerActiveTurretUpdate[]` (heavy's active-turret relay) and the inactive
`ShipID[]` (light's inactive-turret list) are **not wired**; both lists are left
empty.

Reason: native relays the *stored* per-player turret update —
`pfsShip->GetPlayer()->GetActiveTurretUpdate()` (`FedSrv.CPP:4062`), and the
candidate set is the list of ships with a *recent* turret update
(`rgActiveTurrets`). This port stores no per-ship `ClientActiveTurretUpdate`
(`HandleActiveTurretUpdate`, `Server.cs:2592-2629`, applies the update to
`gunner.Controls`/`Orientation` but retains no raw copy for later relay) and has
no "recent turret update" bookkeeping. Wiring the relay faithfully would require
a P109 data-model addition (store last turret update per ship + recent-ness),
which is more than this task's core scope allows — so it is deferred with this
reason, and the codec's empty lists round-trip correctly. The P141 budget task
(or a P109 follow-up) is the natural home for it.

---

## 4. Verification

- Baseline re-confirmed first: **2684 passed / 0 failed** before any change
  (matches P138's checkpoint).
- New live tests: **3/3** `ShipsUpdateLiveTests` pass, each a real loopback
  booted 2-bot / 2-side server (no VM):
  1. **Cross-side, cluster-membership** — A (side Alpha) and B (side Bravo) in
     the same cluster each receive a `HEAVY_SHIPS_UPDATE` containing the other
     ship, and never their own (self-exclusion; no double-send, since own state
     rides `SingleShipUpdate`). Logged: each peer `x1` = the other ship.
  2. **Locked target priority** — `B.CommandTarget = A`; B's update has A in
     `HeavyShipEntries[0]` and `bpTargetHull`/`bpTargetShield` = div-240 of A's
     real `Fraction`/`ShieldFraction` (logged: `240/240`, `0/240` — A is undamaged
     with no shield mounted). Target appears exactly once (not duplicated).
  3. **Leaving the cluster** — B removed from the sector's model list (the
     port's own removal idiom, `Server.cs:1066-1067`); A's next update no longer
     contains B (entries logged empty).
- Full suite: **2687 passed / 0 failed** (2684 + 3 new). Three consecutive clean
  runs before commit (see commit message run log); the three documented timing
  flakes (`LiveChatSectorWireTests`, `LiveCombatWireTests`,
  `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`) are
  unrelated to P139 (none of the new tests touch timing; they are deterministic
  loopback broadcasts) and are re-run if they fire.
- No VM used. No P103-P138 behaviour changed — the only edits are an additive
  broadcast method + Bot observer + a new test file.

---

## 5. Remaining work (named concretely)

1. **P140 — receive/decode on the peer.** Decode 175/176 in the receiving
   transport and apply to `Ship` instances (the counterpart to native
   `ProcessShipUpdate`, shipIGC.cpp:3097-3113). This task sends; P140 consumes.
   Note: for a real client the target-promotion/slot-0 convention and the
   heavy-record decoding are both exercised; for this port's in-process harness
   the delivery is already observable via the Bot's `HeavyShipsUpdates`.
2. **P141 — the deviation-sorted bandwidth budget (optional).** Reintroduce the
   heavy/light split: the `CmpShipDeviation` sort (`FedSrv.CPP:3923`) and the
   2/2/2 heavy/light/turret budget (`FedSrv.CPP:3844-3846`), which would
   populate the currently-empty LIGHT tier and (optionally) the turret relay
   lists deferred in §3.
3. **(Recorded limitation, not a task)** — the native `OT_station`/`OT_asteroid`
   target-scalar branches are structurally unreachable because this port's
   `Ship.CommandTarget` is `Model?` and `Station`/`Asteroid` are not `Model`
   subclasses (§1.3). If a station/asteroid-targeted scalar is ever wanted, the
   command-target type (or a widened target resolution) would need to accept
   non-Model targets first.

---

## 6. Session stats

Logged via
`python3 ~/Development/allegiance-tools/session_stats.py 20260910_062726_bc846f`
(see shell history / commit log for the id used).
