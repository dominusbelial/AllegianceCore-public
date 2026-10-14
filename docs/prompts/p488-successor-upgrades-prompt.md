# P488 — successor upgrades: stations, hulls and parts upgrade to the side's best available version (native GetSuccessor…)

## Why this task exists

Native upgrades what you get once your side has out-teched it. The port
decodes the data but never uses it. Step S7 of the 2026-09-29 economy
parity pass, never scoped until now. Native is at
`~/Development/Allegiance/src/`.

- **Stations.** `FedSrvSiteBase::BuildStation` does
  `pstationtype = pstationtype->GetSuccessorStationType(pside)` ("Allow for
  an upgrade to the station being available", `FedSrv/FedSrv.CPP:11380`). A
  constructor ordered to build a station type the side has since upgraded
  builds the successor. The successor pointer comes from data
  (`stationtypeigc.h:185-195`).
- **Hulls and parts at purchase.** `PurchaseShipLoadout` walks successors:
  `m_station->GetSuccessor…` (`Igc/shipIGC.h:1004`, `:1033`). The
  port's P441 prompt deferred this.
- **Dock auto-upgrade.** Native's desired-loadout restock walks
  `GetSuccessorPartType()` on dock (`FedSrv.CPP:12831-12854`). The port
  documents it as **not implemented** (`src/Allegiance.Server/Server.Docking.cs:1087-1114`).
- **Obsolete items in the shop.** Native's hangar hides hulls the station
  `IsObsolete` (`WinTrek/load.cpp:1603-1606`, `:1817-1820`). P470 ported the
  `GroupID >= 0 && CanBuy` half of that predicate. Check whether
  `!IsObsolete` was ported too.

**The port decodes the data but never uses it:**
`DataHullTypeIGC.SuccessorHullId` (`DataHullTypeIGC.cs:41,353`) and
`DataPartTypeIGC.SuccessorPartId` (`DataPartTypeIGC.cs:60`). Check where the
station-type successor field is decoded.

## Scope

1. **Station successor at build time.** In the port's BuildingEffect
   completion, build `GetSuccessorStationType(side)` with native's exact
   semantics (successor chain and the tech condition). Test it: a side that
   researched the upgrade gets the successor station from a constructor
   ordered for the base type.
2. **Hull and part successors at purchase.** In `ApplyLoadout`, mirror
   `shipIGC.h:1004,1033`: a requested item the station considers superseded
   is bought as its successor. Show the effect in the `BUY_LOADOUT` ack and
   in P473's harness reporting.
3. **Obsolete filter** in the client shop and the server buy gate, if
   not already present.
4. **Dock auto-upgrade** (`FedSrv.CPP:12831-12854`) only if it fits the
   session. Otherwise it's the named follow-up **P488b**, and say so.

## Explicitly out of scope

- The investment UI (P487).
- New tech data. Use the real `PCore001` successor chains, and list the
  chains you tested in the report.

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P488_SUCCESSOR_UPGRADES.md`.
