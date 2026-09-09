# P118 — MONEY_CHANGE(60) + SET_MONEY(115): the first Phase 4 task

Date: 2026-09-09
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`)
Task: add the two economy-sync codecs, wire them into the port's real
money-mutation and join-sync sites, and (as the phase's first "read first"
pass) confirm the architecture before writing any code.

---

## 1. The load-bearing finding — CONFIRMED

**This port tracks money at the `Side` level only.** `Side.Money` is a
`public long` (Model/Side.cs:52), the port of the native FedSrv accumulator
`m_rgMoney[sideID]` (fsmission.cpp:61,3504). A grep across the whole port's
money mutations confirms there is **no per-ship/per-player money field
anywhere**:

```
src/Allegiance.Core/Model/Mission.cs:299      side.Money += income;      (payday accrual, fsmission.cpp:2536)
src/Allegiance.Server/ServerHost.cs:450       side.Money += proceeds;    (ore-sale credit, shipIGC.cpp:1969-1990)
src/Allegiance.Server/Server.cs:2400          side.Money += refund;      (board refund, FedSrv.CPP:2426-2443)
src/Allegiance.Server/Server.cs:3633          rs.Money += refund;        (teleport refund, FedSrv.CPP:7090-7105)
src/Allegiance.Server/Server.cs:4773          side.Money -= delta*np;    (payday drain, fsmission.cpp:2309)
```

Native, by contrast, tracks money **per player** (`CFSPlayer::GetMoney()` /
`SetMoney()`), with the shared side pool being purely conceptual. This port
made the opposite, simpler choice — one pooled number per side. **This is a
standing, already-committed design fact, not a gap to fix in this task** (it
matches P110's own report citing "ShipValue → side treasury"). It directly
shapes how both messages are populated (Section 3), since their native
per-player semantics don't map 1:1.

## 2. Native message definitions (verified against source)

`Inc/Messages.h:441-445` (MONEY_CHANGE, 60):
```cpp
DEFINE_FEDMSG(S, MONEY_CHANGE, 60)
  Money     dMoney;
  ShipID    sidTo;
  ShipID    sidFrom;    //== NA     means a server initiated change (cheat, probably)
END_FEDMSG              //== sidTo  means a player initiated change (purchase, etc.)
                        //otherwise means a transfer from -> to
```
`Inc/Messages.h:751-754` (SET_MONEY, 115):
```cpp
DEFINE_FEDMSG(S, SET_MONEY, 115)
    Money   money;
    ShipID  shipID;
END_FEDMSG
```

**Wire layout** (computed from the native struct arithmetic, default MSVC
packing — both messages sit outside Messages.h's `#pragma pack(1)` regions at
:636-642 / :917-925; `Money` = `AGCMoney` = 32-bit long (AGCIDL.idl:246);
`ShipID` = `ObjectID` = 16-bit short (igc.h:477,480); base `FEDMESSAGE` = two
16-bit words cbmsg+fmid (messagecore.h:43,59,76-87)):

```
MONEY_CHANGE(60):  0 cbmsg | 2 fmid | 4 dMoney(i32) | 8 sidTo(i16) | 10 sidFrom(i16) = 12 B
SET_MONEY(115):    0 cbmsg | 2 fmid | 4 money(i32)  | 8 shipID(i16)| 10-11 2 pad bytes  = 12 B
```

`SET_MONEY` is 12 B, **not 10**: its largest member (the 4-byte Money long)
forces the compiler to round `sizeof(FMD_S_SET_MONEY)` up to 12 with two
trailing pad bytes after the short `shipID`. The native FM framework transmits
`cbmsg = sizeof(...)` verbatim (messagecore.h:714,724), so 12 bytes genuinely
go on the wire. The port's `SetMoney` codec includes the two zero pad bytes to
stay byte-faithful (same trailing-pad rule the existing `MissileLaunchData`
codec already applies, and the same padding discipline `JoinedMission`/`ShipDelete`
established).

## 3. Native call sites and the port's honest mapping

### MONEY_CHANGE — verified real call sites
Grepped `FedSrv/FedSrv.CPP` and `fsmission.cpp` directly (P118 session):

| Native site | Meaning | Port disposition |
|---|---|---|
| `FedSrv.CPP:2435-2441` | Board refund; sent to **the whole side's group** (`CFSSide::FromIGC(pside)->GetGroup()`), sidTo = refunded ship, sidFrom = NA | **WIRED** → port board-refund `Server.cs:2375` |
| `FedSrv.CPP:7202-7206` | Teleport refund; sidFrom = NA | **WIRED** → port teleport-refund `Server.cs:3581` |
| `FedSrv.CPP:2945-2960` | `"gimme"` cheat console command | **N/A** — no cheat/admin console in this port (same precedent as SUICIDE's P103 cheat-gate relaxation) |
| `FedSrv.CPP:6203-6207`, `7162-7166` | Purchase-cost deduction (buy loadout/hardware) | **N/A** — see purchase-flow finding below |
| `FedSrv.CPP:7227-7239` (+ donation, `fsmission.cpp:6131`, `FedSrv.CPP:7673`, `13602`) | Donation / leadership-transfer | **DEFERRED** — needs the bucket/donation system (Phase 4's 64/65/67/112/116), not built |

**Purchase-deduction finding (the task's open conditional, resolved):** the
port's buy-loadout path is `ApplyLoadout` (`Server.cs:3622-3690`), and it
**does not charge money at all** — it grants hull/parts free (the whole body has
no `Money` reference; it clears and re-mounts the requested parts, then ACKs).
The money-mutation grep in Section 1 confirms there is **no `Money -= cost`
site anywhere in the port**. So there is no real purchase/hardware-cost
deduction site to wire `MONEY_CHANGE` to. Stated plainly: **nothing was
invented** — the task explicitly forbade building a purchase flow just to fire
this broadcast, and no such existing site exists.

**Payday is confirmed NOT a `MONEY_CHANGE` site:** the payday accrual lives in
`fsmission.cpp:2536` (DoPayday payout at :2294-2326) and the greps in Section 2
show no `MONEY_CHANGE` in that function or nearby (the only `fsmission.cpp`
occurrence is the leadership-transfer at :6131, deferred). The port's payday
path (`Mission.cs:299` accrual → `OnPayday` `Server.cs:4674` broadcast)
correctly fires only `PAYDAY(114)`, not `MONEY_CHANGE`. No double-firing was
added.

### SET_MONEY — the single native call site
Verified the sole real caller: `CFSMission::SendMissionInfo(CFSPlayer*,
IsideIGC*)` (`fsmission.cpp:4108-4310`), invoked from
`CFSMission::AddPlayerToSide` (`fsmission.cpp:539`, call at :736) when a
joining/syncing player is told about the world after the mission has started.
Inside SendMissionInfo (fsmission.cpp:4270-4299) it loops every **other** ship
on the side and sends one `SET_MONEY` per teammate with that teammate's
per-player money (`pcp->GetMoney()`) and shipID.

**Port mapping (decision stated plainly):** the pooled port has one number per
side, so the native per-teammate loop collapses to **a single `SET_MONEY` sent
to the joining player**, carrying the side's one shared treasury value, with
`shipID` = the joining player's own ship (the only real ship in this pooled
model, and the address the client handler `appmsg.cpp:1367-1379` uses to set a
player's money). This is wired at the port's real join-sync point — the join
burst's PLAYERINFO side-seating step (`Server.cs:~1238`), the port analog of
AddPlayerToSide's "the player now has a side" moment.

## 4. What was actually wired (with citations)

1. `Wire/MoneyChange.cs` — `MONEY_CHANGE(60)` codec, FixedSize 12.
2. `Wire/SetMoney.cs` — `SET_MONEY(115)` codec, FixedSize 12 (2 trailing pad).
3. **Board refund** `Server.cs:2400`/`2407`: after `side.Money += refund`, calls the
   new side-wide broadcast `BroadcastMoneyChangeSideWide(side.Id, refund,
   boarder.Id)` — dMoney = refund, sidTo = boarded ship, sidFrom = NA.
4. **Teleport refund** `Server.cs:3633`/`3638`: after `rs.Money += refund`, same
   broadcast `BroadcastMoneyChangeSideWide(rs.Id, refund, ship.Id)`.
5. New helper `Server.cs:2719` `BroadcastMoneyChangeSideWide(short sideId,
   int dMoney, short sidTo)` — iterates `_peerShips` for peers whose
   `Ship.SideId == sideId` and sends to each (the established side-peer
   iteration OnPayday/PlayerReady already use), sidFrom hard-set to `NA = -1`.
6. **Join sync** `Server.cs:1257`: after PLAYERINFO seats the player, sends
   one `SET_MONEY` to the joining player with `Money = (int)side.Money` and
   `ShipId = shipId`, resolved via `_missions[0].Sides`.
7. Test instrumentation: `Bot.cs` now captures `MoneyChanges` / `SetMoneys`
   and exposes `SendBoard(short sidParent)` for the live refund test.

## 5. Verification (all passed, real loopback / in-process server)

New tests (14): `Wire/MoneyChangeSetMoneyWireTests.cs` (12 codec round-trip +
byte-exact + reject cases) and `LiveMoneyTests.cs` (2 live proofs):

1. **SET_MONEY join** — pre-seeded each side's `Money` to a distinct nonzero
   value, joined a real bot, and asserted it received exactly one `SET_MONEY`
   with `money` = its side's real `Money` and `shipID` = its own ship. Live
   output: `[setmoney] peer 1 'joiner' -> SET_MONEY(115) money=656565 shipID=8096`.
2. **MONEY_CHANGE board refund** — a three-bot topology (carrier + boarder on
   side 0, outsider on side 1). The boarder boarded the carrier (surrendering
   hull 504 = 500 credits); asserted both same-side peers received
   `MONEY_CHANGE dMoney=500 sidTo=<boarder> sidFrom=NA`, the treasury was
   credited +500, and the **other-side peer received none**. Live output:
   `[moneychange] side 0 -> MONEY_CHANGE(60) dMoney=500 sidTo=8097 sidFrom=NA to 2 peer(s)`.

Full suite: **2537 passed, 0 failed** (P117 checkpoint was 2523; +14). Three
consecutive clean full-suite runs recorded. One transient `LiveChatSectorWireTests`
timing flake fired mid-way and was rerun clean — the documented pre-existing
~1-in-8 flake, not a P118 regression.

## 6. Next Phase 4 tasks

This unblocks / leaves open:
- **Donation / bucket system** (AUTODONATE 116, CREATE_BUCKETS/BUCKET_STATUS
  64/65, GET_AVAILABLE_BUCKETS 67, 191) — the donation half of the MONEY_CHANGE
  family (`FedSrv.CPP:7227-7239`), explicitly deferred; the pool is a deposit
  surface for it. This is the most direct continuation.
- **STATIONS_UPDATE / STATION_CAPTURE / STATION_DESTROYED** — station/economy
  depth; no station-state broadcast or capture mechanic exists yet.
- **Purchase/loadout costing** — the port currently grants loadouts free
  (ApplyLoadout); adding real part/hull prices to the buy path (native
  `FedSrv.CPP:6203-6207`, `7162-7166`) would finally give `MONEY_CHANGE` its
  negative-dMoney purchase site.
