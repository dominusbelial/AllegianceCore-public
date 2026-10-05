# P534 — Economy & tech parity audit (P1–P200, part 4)

Read-only audit (part 4 of 8 of the P1–P200 parity pass). Scope: the commits
in `docs/audits/p1-p200/economy.txt` (36 commits) as they relate to **economy &
tech**: payday amount/cadence, starting money, purchase prices and refunds,
tech/development costs, times and prerequisites, investment (bucket funding),
treasure values, money-transfer rules, and the mining/offload flow (excluding
the three He3 mining *constants* — mine-rate, capacity, value — which P528
fixes).

Method: every non-MATCH row was verified by reading both native
(`~/Development/Allegiance/src`) and port (`~/Development/AllegianceCore/src`)
source. Line numbers are cited for both sides. Items already reported by the
2026-09-29 parity pass (`~/Documents/Domingo-Llm-Wiki/raw/2026-09-29-parity-C-economy.md`)
are marked FIXED/OPEN against **current** code — later P-tasks (P441 pricing,
P443 starting money/income, P444/P445 BuildingTechs, P458/459 bucket
completion, P478 dev-cost scaling, P197-199 money, P201 flags) fixed most of
them.

Net picture: the 2026-09-29 economy gaps are overwhelmingly **fixed**. Buying
now debits money, starting money and station income exist, BuildingTechs is
computed, buckets spawn real constructors/drones, and per-player wallets are
real. Four divergences remain, all small: (1) the bucket-donation `CanBuy`
gate is still missing, (2) a cash treasure credits the **side treasury**
instead of the **picker's wallet**, (3) ore is sold **on dock** rather than by
**teleport-offload at a refinery** (amount/distribution still match), (4) a
couple of minor fidelity cuts (predecessor-bucket sharing, ACQUIRE_TREASURE on
cash).

---

## Findings

| item | native file:line | port file:line | verdict | severity | proposed fix |
|---|---|---|---|---|---|
| Payday cadence | `missionigc.cpp:2691` `c_dtBetweenPaydays=60.0f`; `fsmission.cpp:2253,2514-2519` (phase-keep `+= dt`) | `Mission.cs:298` `SecondsBetweenPaydays=60.0`, `:632-650` phase-keep | MATCH | – | none |
| Payday base income | `missionigc.cpp:2722` `c_fIncome=600.0f` (→ `c_fcidIncome`, `:2773`) | `Mission.cs:307` `IncomePerPayday=600.0f` | MATCH | – | none |
| Payday income formula `×(1+civ.IncomeMoney)` | `fsmission.cpp:2536` | `Mission.cs:636-637` | MATCH | – | none |
| Payday **station income** Σ owned stations' `Income` | `fsmission.cpp:2538-2546` | `Mission.cs:643-645` + `StationIncomeFor` (reads live `StationRuntimes`) | MATCH (was MISSING in 2026-09-29 — `Mission.cs:592` "server has no stations" now stale; FIXED by P443) | – | none |
| Payday **payout split** (delta=floor(pot/np), PAYDAY(114) to side group, deduct delta·np, credit each player's autodonate-or-self) | `fsmission.cpp:2294-2326` `DoPayday(IsideIGC*)` | `Server.Economy.cs:1071-1137` `OnPayday` | MATCH | – | none |
| Live "525/600 per minute" | base is 600 (`c_fIncome`), cadence 60s — both native and port agree; a live 525 would come from accumulated-treasury remainders, not a port divergence | `Mission.cs:307`/`Server.Economy.cs` | MATCH (value & cadence verified against source) | – | verify live separately (out of read-only scope) |
| Starting money seed `StartingMoney×(fStartingMoney+civ.BonusMoney)`, ≤0 → 0, `DoPayday()` right after | `fsmission.cpp:1992-2004,2278`; `c_fStartingMoney=16000` `missionigc.cpp:2697` | `Mission.cs:705-725` (`16000×(1.0+BonusMoney)`, ≤0→0) | MATCH (FIXED by P443). `fStartingMoney` mission-param default not present in this native checkout (only `AGCGameParameters.cpp:1058,1074` + `newgamescreen.cpp:194,1009` reference it), so port adopts **1.0 as a named stated default** — honestly labelled. | LOW | if the real `fStartingMoney` default is ever recovered, drop it in (`Mission.cs:332`) |
| Buy **pricing & refund** `cost=new.GetValue()-old.GetValue()`, wallet gate, side-wide MONEY_CHANGE | `FedSrv.CPP:6182-6207` (incl. `TrapHackBoot(money>=cost)`) | `Server.ShipCommands.cs:2524-2539` (projected cost), `:2620-2635` (actual cost debit/refund + `BroadcastMoneyChangeSideWide`) | MATCH (was MISSING in 2026-09-29; FIXED by P441) | – | none |
| Buy **server-side tech gate** (hull `CanBuy`, part gate, successor upgrade) | `shipIGC.h:999-1040` `m_station->CanBuy`, `GetSuccessor` | `Server.ShipCommands.cs:2438-2460` (drone-hull refusal + `StationSellsHull`), part gate `:2445-2478`, successor `:2499-2507` (P488) | MATCH (FIXED by P441/P488). Non-docked ships keep ungated behaviour — same scope as native (no station to consult) | – | none |
| **Bucket-donation `CanBuy` gate** — native spends only inside `if(pside->CanBuy(pbucket))`, else full refund to donor | `FedSrv.CPP:7150-7151` (donate + `mnyRefund` full → `:7199-7208` full refund) | `Server.Economy.cs:508-572` `HandleBucketDonate` — no `CanBuy` check; donation accepted whenever wallet covers it | **MISSING** (2026-09-29 #5, still OPEN) | **MED** | gate on `side.Techs` satisfying the bucket's development prerequisite (BuildingTechs now available, `Side.cs:110-131`); reject-with-full-refund when unmet (mirror `FedSrv.CPP:7150`) |
| **Cash treasure pickup** credits picker's **personal wallet** (autodonate-or-self) + side-wide MONEY_CHANGE | `FedSrv.CPP:13589-13606` (`pfsDonate->SetMoney(+amount)`) | `Server.Docking.cs:955-960` `side.Money += amount` (pooled treasury) for **all** ships | **DIVERGES** | **MED** | cash branch: credit `live.Wallet.Money` via autodonate target (or self) + side-wide MONEY_CHANGE `sidFrom=NA` (`Server.Docking.cs:950-963`); keep pooled path only for non-player pickers |
| `ACQUIRE_TREASURE(162)` sent to picker for **cash** too | `FedSrv.CPP:13625-13641` (sent for every `tc != c_tcPart`, incl. cash) | `Server.Docking.cs:848-853` explicitly not wired (documented out-of-scope follow-up) | **MISSING** (self-reported) | LOW | one send to the picker's connection in the cash branch |
| Treasure **value** is data-driven (`c_tcCash`: `amount=treasureID`, corpus 500…10000) | `treasureIGC.cpp:91-92`; `DataTreasureSetIGC` corpus | `DataTreasureIGC.cs:91-92` + tests build cash treasure from real corpus catalog | MATCH | – | none |
| Ore **offload mechanism** — native sells at a **refinery** (`c_sabmTeleportUnload`) by **teleport-offload mid-flight**; port sells **on dock** at any friendly dock | `shipIGC.cpp:1969-1990` | `ServerHost.cs:784-805` (proceeds at `:797`, ore zeroed `:803`) in `DockWithStationEvent` | **DIVERGES** (mechanism/trigger; amount & distribution MATCH) | LOW/MED | add a teleport-unload/refinery-capability leg, or document dock-sell as the deliberate stand-in for NPC-miner flow |
| Ore sale **amount & distribution** `ore×value×GA[MiningYield]` → `GiveSideMoney` (pot+= then `DoPayday`) | `shipIGC.cpp:1985-1990`; `FedSrv.CPP:13647` → `fsmission.cpp:1777-1787` | `ServerHost.cs:797-801` (`side.Money += proceeds; PaydayEvent(side, proceeds)` → `OnPayday`) | MATCH (He3 value/capacity/rate constants themselves fixed by P528, out of this slice) | – | none |
| **BuildingTechs** = OR of owned stations' `EffectTechs`, fed into `Techs=Building|Development` | `sideigc.h:133-158, 274-287` | `Side.cs:110-131` (`BuildingTechs` computed from `Station.Type.TtbmEffects`, `Techs` union) | MATCH (was MISSING in 2026-09-29 #7; FIXED by P444/P445) | – | none |
| Development-bucket **price/time** GA scaling `×GA[c_gaDevelopmentCost]/[c_gaDevelopmentTime]`, exempting `c_didTeamMoney` | `bucketigc.cpp:66-75`; `igc.h:620-621` | `Bucket.cs` `ForSide` (P478) — same scaling, same `c_didTeamMoney` exemption | MATCH (FIXED by P478) | – | none |
| **Drone + station buckets** exist, gated on reachable techs | `sideigc.cpp:204-255` `CreateBuckets` (drone then station types) | `Bucket.cs` `ForSide` — dev buckets + drone-type + station-type buckets appended after (P458), each gated on `ReachableTechs` | MATCH (was MISSING in 2026-09-29 #6; FIXED by P458) | – | none |
| **Bucket completion effects** — dev → `DevelopmentCompleted`; station-type → constructor drone ("requesting rock"); drone-type → real-hulled NPC + where-to-go chats | `FedSrv.CPP:11259-11276` (dev), `:11343-11372` (station), `:11518-11546` (drone) | `Server.Economy.cs:657-682` dispatch → `CompleteDevelopment` (`:945`), `CompleteStationType` (`:721`), `CompleteDroneType` (`:900`+) | MATCH (was "detect-but-don't-act" in 2026-09-29; FIXED by P458/P459) | – | none |
| **Predecessor-bucket sharing** — a cheaper GA-only dev with an identical attribute set reuses the existing bucket (dev cost curve) | `bucketigc.cpp:41-63` | `Bucket.cs` `ForSide` — no predecessor lookup | **MISSING** (minor fidelity cut) | LOW | share a bucket when a cheaper GA-only dev has an identical `GlobalAttributeSet` |
| Synthetic **ship-type bucket** price/time (5800 / 30s) | hull price + `TimeToBuild` (corpus measures `TimeToBuild==0` for every hull) | `Bucket.cs:301-304` `SyntheticPrice/SyntheticTimeToBuild`, clearly labelled | **STAND-IN** (labelled; corpus can't supply a usable build curve) | LOW | none urgent — documented |
| **Autodonate** set/clear/toggle, leader re-target on departure | `fsship.cpp` `CFSPlayer::SetAutoDonate`; `fsmission.cpp:2015-2020` (everyone donates to team leader) | `Server.Economy.cs:34-172` (`SetAutoDonate`, `HandleAutoDonate`), join auto-donate + `SyncMoneyStateToJoiner` | MATCH (was N/A in P122; FIXED/implemented by P197) | – | none |
| **Join SET_MONEY(115)** = per-player wallet (not side treasury) | `fsmission.cpp:2015` (per-player balance, starts 0) | `Server.Economy.cs:423-442` `SyncMoneyStateToJoiner` (sends `live.Wallet.Money` per teammate) | MATCH (was "sends side treasury" in 2026-09-29 #3; FIXED by P197) | – | none |
| **PLAYER_DONATE(67)** wallet→wallet transfer, no-op on insufficiency | `fsship.cpp:1336-1338` | `Server.Economy.cs:74-75` (+ `HandlePlayerDonate` `:310-395`) | MATCH (was N/A in P122; implemented) | – | none |
| **REQUEST_MONEY(182)** — forward-only to whoever holds the team's funds, never transfers | `FedSrv.CPP:5361-5406` (forwards to richest holder; no transfer) | `Server.Economy.cs:228-290` `HandleRequestMoney` (resolves richest on side, forwards; no transfer) | MATCH (FIXED by P199) | – | none |
| **BUCKET_DONATE spends from the donor's own wallet**, negative side-wide MONEY_CHANGE, `sidFrom=sidTo=donor`, excess refund `sidFrom=NA` | `FedSrv.CPP:7148,7160-7168,7199-7208` | `Server.Economy.cs:541-572` | MATCH (FIXED by P198) | – | none |

---

## Top fixes (ranked, sized as candidate P-tasks)

1. **Bucket-donation `CanBuy` gate (MED, smallest slice).** In
   `HandleBucketDonate` (`Server.Economy.cs:508`), after resolving `bucket`,
   gate on the side's `Techs` satisfying the development's prerequisite —
   when unmet, refund the full `MoneyGiven` to the donor (native
   `FedSrv.CPP:7150` + `:7199-7208`) instead of accepting the spend.
   BuildingTechs already exists (`Side.cs:110-131`), so the gate is a pure
   comparison + a refund branch. One function + one test.

2. **Cash treasure credits the picker's wallet (MED).** In
   `PerformTreasurePickup` cash branch (`Server.Docking.cs:950-963`), replace
   `side.Money += amount` with a credit to the picking player's autodonate-
   or-self wallet + side-wide MONEY_CHANGE `sidFrom=NA` (native
   `FedSrv.CPP:13589-13606`); keep the pooled path only for non-player
   pickers. Optionally wire the cash `ACQUIRE_TREASURE(162)` in the same task.

3. **Ore offload leg — refinery teleport-unload (LOW/MED, larger slice).**
   Add the `c_sabmTeleportUnload` station-capability + in-flight teleport-
   offload sale (native `shipIGC.cpp:1969-1990`) alongside the existing
   dock-sell (`ServerHost.cs:784-805`). Lower priority: the current dock-sell
   already matches the amount/distribution; only the trigger differs.

4. **Predecessor-bucket sharing (LOW, polish).** Share a bucket when a cheaper
   GA-only development has an identical `GlobalAttributeSet` (native
   `bucketigc.cpp:41-63`). Rare in practice; deferrable.

---

## Coverage

Economy/tech commits in the slice, checked against **current** source (line
numbers as of `bdbb062a`):

Deep (each traced through current code + native counterpart):
- **P51** payday, **P55/P56** mining accumulate + sell, **P61** loadout /
  pricing, **P118** MONEY_CHANGE/SET_MONEY, **P119/P189** buckets + tech tree,
  **P198** bucket-donate-from-wallet, **P197** per-player money + autodonate +
  join SET_MONEY, **P199** REQUEST_MONEY, **P122** PLAYER_DONATE/autodonate,
  **P123/P144/P145** treasure + part-drop/add, **P156/P158/P159/P160** miner
  decision-cycle / cross-cluster / contention / PilotType (behaviour arms of
  the mining flow).

Brief / covered by siblings (out of the economy lens): **P26–P31** (EXPORT /
BUY_LOADOUT_ACK / MISSIONDEF / NEW_SHIP_INSTANCE join-burst framing — see
P532 wire-semantics and P533 session audits), **P40/P47/P49** (SCoutExploreMap
status/clusterinfo/ghost-prune — flight), **P110** (TELEPORT_ACK — wire, P532),
**P120** (ASTEROID_MINED — wire codec, P532), **P124** (SIDE_TECH_CHANGE/
SIDE_ATTRIBUTE_CHANGE/POSTER — their *consumers* audited here via
CompleteDevelopment), **P127** (LOGOFF — session, P533), **P130/P193**
(RANK_INFO/PlayerScore — session/scoring, P533), **P174** (Lobby app — not
economy). **P460-family mining-constant fixes (P528) deliberately excluded.**

All native line numbers re-derived this pass (`LC_ALL=C` grep/sed against
`Igc/`); no claim rests on the 2026-09-26 graphify graph.
