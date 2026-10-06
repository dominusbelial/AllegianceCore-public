# P551 — Economy parity: bucket-donation CanBuy (+refund), cash-treasure wallet credit, dock ore-sale gate

Date: 2026-10-06 · Branch: `master` (not pushed) · Suite: 3× `scripts/run-tests.sh`

## Summary

Three economy-parity divergences from the P534 audit (re-verified by Claude's
2026-10-05 spot-check, wiki `allegiance-p1-p200-parity-audit`), each cited to
native and fixed failing-first:

1. **Bucket donation now honours native `Side::CanBuy` with a full refund.**
   The old port (`Server.Economy.cs` `HandleBucketDonate`) spent into the bucket
   whenever the donor's wallet covered the offer. Native wraps the **whole**
   spend in `if (pside->CanBuy(pbucket))` (`FedSrv.CPP:7152`) and, when the side
   cannot buy the bucket, refunds the entire `moneyGiven`
   (`FedSrv.CPP:7199-7208`) with no spend and no BUCKET_STATUS. Also: native
   sends the `c_didTeamMoney` development's BUCKET_STATUS to the **whole
   mission** (`FedSrv.CPP:7190-7196`), not just the side — the port sent it
   side-only (`Economy.cs:571`). Both fixed.
2. **Cash treasure credits the picker's (or autodonate target's) WALLET, not
   the side pot.** The old port did `side.Money += amount` while broadcasting
   `MONEY_CHANGE` with `sidTo = picker` — a client/server mismatch (the client
   showed the picker the money while the server put it in the pool). Native's
   `c_tcCash` branch of `HitTreasureEvent` (`FedSrv.CPP:13589-13616`) credits
   the autodonate target's wallet (or the picker's own when no target) with
   `sidTo = target`; only an NPC pickup pools to the side (`GiveSideMoney`).
3. **Dock ore sale is gated on `c_sabmUnload` AND own side.** Native **does**
   sell ore on dock, in `IShipIGC::SetStation` (`shipIGC.h:441-449`):
   `if ((m_fOre > 0.0f) && (s->GetStationType()->HasCapability(c_sabmUnload) &&
   (GetSide() == s->GetSide())))`. The port's dock sale (`ServerHost.cs:788`)
   lacked that gate (NPCs happened to pick `SabmUnload` stations, `Ship.cs:5780`).
   Added the gate; the mis-citing port comment at `ServerHost.cs:772-775` (which
   pointed at the *separate* NPC teleport-offload leg `shipIGC.cpp:1969-1990`)
   is corrected to `shipIGC.h:441-449`.

Out of scope (unchanged): multi-game mission money scoping; building cost via the
research bucket (P537); the refinery **teleport-offload** leg itself
(`shipIGC.cpp:1969-1990`) — ported only if small, else a follow-up; it is
listed as a follow-up here (substantial NPC-AI mining-leg change).

## Native references (all cited)

### 1 — Bucket-donation CanBuy gate + refund
- `src/Igc/sideigc.h:224-281` — `IsideIGC::CanBuy` (unwrap bucket → buyable;
  dev: `required <= ttbmSide || objectID == c_didTeamMoney`; station type:
  not if a successor is available; drone/ship: some owned station's
  `localTechs | ttbmSide` covers required).
- `src/FedSrv/FedSrv.CPP:7151-7153` — `mnyRefund = moneyGiven; if
  (pside->CanBuy(pbucket)) { ... }`.
- `FedSrv.CPP:7154-7168` — inside the gate: `AddMoney`, `SetMoney(-mnySpent)`,
  negative MONEY_CHANGE to the side group.
- `FedSrv.CPP:7190-7196` — BUCKET_STATUS audience: whole mission when the
  buyable is a development with id `c_didTeamMoney`, else the side group.
- `FedSrv.CPP:7199-7208` — full refund MONEY_CHANGE to the donor alone when
  `mnyRefund` (which stays = `moneyGiven` when CanBuy is false) is non-zero.
- Port: `Side.CanBuy(Bucket)` (`src/Allegiance.Core/Model/Side.cs`, new), the
  donation gate + mission-wide broadcast (`Server.Economy.cs`
  `HandleBucketDonate` / `BroadcastBucketStatusMissionWide`).

### 2 — Cash treasure wallet credit
- `src/FedSrv/FedSrv.CPP:13589-13616` — `tc == c_tcCash`:
  ```cpp
  CFSShip* pfsShip = (CFSShip*)(ship->GetPrivateData());
  if (pfsShip->IsPlayer()) {
      IshipIGC* pshipDonate = ship->GetAutoDonate();
      if (pshipDonate == NULL) pshipDonate = ship;
      CFSShip* pfsDonate = ...;
      pfmMoney->sidTo = pshipDonate->GetObjectID();
      pfmMoney->sidFrom = NA;
      pfmMoney->dMoney = amount;
      pfsDonate->SetMoney(pfsDonate->GetMoney() + amount);
      g.fm.SendMessages(CFSSide::FromIGC(pfsShip->GetSide())->GetGroup(), ...);
  } else {
      m_pfsMission->GiveSideMoney(ship->GetSide(), Money(amount));
  }
  ```
- Port: `Server.Docking.cs` `PerformTreasurePickup` (cash branch).

### 3 — Dock ore sale gate
- `src/Igc/shipIGC.h:441-449` — `SetStation` dock sale.
- `src/Igc/shipIGC.cpp:1969-1990` — the **separate** NPC teleport-offload leg
  (follow-up, not the dock-sale gate).
- `src/Igc/igc.h:744` — `c_sabmUnload = 0x01`; port `DataStationTypeIGC.SabmUnload`.
- Port: `ServerHost.cs` `DockWithStationEvent` (mine-sell block).

## Failing-first proofs

Each test failed on the pre-P551 code and passes now.

### 1 — Bucket donation CanBuy + refund
`LiveBucketTests.Donation_to_unbuyable_bucket_refused_and_full_refunded` (new):
a donor with a full wallet donates into the synthetic ship-type bucket, which a
starting Garrison + civ base techs cannot buy (`Side.CanBuy` == false — hull 504
'Bomber' required-techs probed unreachable at start). Asserts: nothing invested,
no spend MONEY_CHANGE, no BUCKET_STATUS, full refund MONEY_CHANGE
(`dMoney == donated`, `sidTo = donor`, `sidFrom = NA`), wallet never debited,
side treasury untouched. On the old code this donation invested + broadcast —
hence failing-first.
Also a whole-mission BUCKET_STATUS proof for the `c_didTeamMoney` development:
`ProsperityArtifactLiveTests` (team-money dev, already whole-mission by P302)
now goes through `BroadcastBucketStatusMissionWide`.

### 2 — Cash treasure wallet credit
- `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_...` (updated): a
  seated player picks up cash → the **picker's wallet** rises by the amount and
  the side pool is **untouched** (was: side pool rose). MONEY_CHANGE
  `sidTo = picker`, `sidFrom = NA`, side-wide.
- `LiveTreasurePickupTests.Cash_treasure_credited_to_autodonate_target_...` (new):
  with the picker's autodonate pointing at a same-side teammate, the **target's**
  wallet rises, the picker's does not, and MONEY_CHANGE `sidTo = target` — the
  wire agrees with the server state (the task's required server/client
  agreement).

### 3 — Dock ore sale gate
`P551DockOreSaleTests.Dock_ore_sale_honours_sabmunload_and_own_side_gate` (new,
direct `ServerHost.DockWithStationEvent`): an own-side `SabmUnload` station
sells (ore zeroed, treasury credited); an own-side **non-`SabmUnload`** station
sells nothing (ore kept); an **enemy**-side `SabmUnload` station sells nothing
(ore kept). On the old code all three sold — hence failing-first.

## Behaviour rollback required for existing tests

Adding the native gate changed which donations/sales are legal, so several
existing tests that had assumed the ungated behaviour were updated to the
faithful case (each now green):
- `LiveBucketTests` small/oversized/full-funding donation tests now target a
  **buyable** development bucket (`BuyableBucket`) instead of the unbuyable
  ship-type bucket.
- `ClientWalletLiveTests.BucketDonation_...` likewise targets a buyable
  development.
- `TechTreeDevelopmentLiveTests`, `P446E2eTopSpeedLiveTests`,
  `P475E2eStationGaLiveTests`, `P477E2eMiningRateDevLiveTests`,
  `P467E2eGunDamageDevLiveTests` grant the development's **required techs**
  before funding (`GrantPrereq` / inline `.Or(TtbmRequired)`), simulating a
  player who has researched the prerequisites — the only way a real player can
  fund a dev, per `Side::CanBuy`.
- `MiningSellTests` synthetic stations now use a real `SabmUnload` station type
  (`MkStation`), so the unit dock-sell proves the own-side-Unload sale the gate
  admits.

## Suite

3 consecutive `scripts/run-tests.sh` runs on the final code (commit `02e58d54`).
Core total 3766 (baseline + 3 new P551 tests). Each run clean except the
documented / load-dependent live-wire flakes; each flake is in `docs/TEST_FLAKES.md`
or is the known rotating load-flake class, and none touches the
economy/treasure/dock paths changed here:

| Run | Lobby | ClientCore.MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1327/1327 | 543/545 (ReadyCheckLiveTests, CrossSideCombatVisibilityLiveTests) | 3762/3766 (LiveChatSectorWire, GameOverPlayersWire, ConquestTerritoryLive, CargoBayReload) |
| 2 | 12/12 | 1327/1327 | 545/545 | 3764/3766 (WarpBombTests, GameOverPlayersWire) |
| 3 | 12/12 | 1327/1327 | 545/545 | 3763/3766 (StationsUpdateLive, SideEliminationLive, LiveRelaunch) |

All failures are the documented live-wire / load-dependent timing flakes that
rotate run-to-run (see `docs/TEST_FLAKES.md`: `CargoBayReload`, `GameOverPlayers`,
`ConquestTerritoryLive`, `LiveRipcord`, `ReadyCheckLiveTests`,
`LiveChatSectorWireTests`, `SideEliminationLiveTests`, `LiveRelaunchTests`,
`StationsUpdateLiveTests`); `ReadyCheckLiveTests` and `CargoBayReloadTests`
re-ran green in isolation on a quiet box, confirming the load class
(`LiveChatSectorWireTests` is the ledger's intermittent live-wire relay flake —
unaffected by this task). The full economy / treasure / dock suite is green in
every run:
`LiveBucketTests` (6), `LiveTreasurePickupTests` (4), `P551DockOreSaleTests` (1),
`MiningSellTests` (6), `TechTreeDevelopmentLiveTests`, `P446E2eTopSpeedLiveTests`,
`P475E2eStationGaLiveTests`, `P477E2eMiningRateDevLiveTests`,
`P467E2eGunDamageDevLiveTests`, `ProsperityArtifactLiveTests` all passed in runs
1–3.

## Live loopback proof

The P528b miner economy loop (miners mine, warp home, dock at an own-side
`SabmUnload` station, sell, broadcast) still runs — covered by
`P528bMinerSellRegressionLiveTests`, `MinerDecisionCycleLiveTests`,
`NpcCrossClusterMinerDecisionLiveTests` and `MiningSellTests.Loopback_*`
(all green). A player picking up cash treasure sees their client HUD wallet
change match the server wallet — covered by the (updated) treasure Scenario 1
and the new autodonate-target test (MONEY_CHANGE `sidTo` == the credited
server wallet owner).
