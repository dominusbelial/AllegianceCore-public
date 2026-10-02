# P494 — On dock, refill and upgrade to the desired loadout (P488b)

**Branch:** master · **Build:** 0 warnings / 0 errors (whole solution)
**Status:** committed on master, not pushed

## What this is

The named follow-up **P488b** to P488 (successor resolution). When a
player-piloted ship docks, native refills the ship's expendable pools and
re-buys the player's **desired loadout**, upgrading each part to the side's
best available successor as the side out-techs it. The port documents this as
not implemented (`src/Allegiance.Server/Server.Docking.cs:1087-1114`, the P117
carrier-landing write-up). This task ports it at the port's dock transition.

## Relationship

- **Native authority:** `FedSrv/FedSrv.CPP` — `RefillPart` (12589-12621),
  the desired-parts walk with `ErasePart(... ET_Magazine / ET_Dispenser /
  ET_ChaffLauncher ...)` and the fuel/ammo packs (12637-12945), `AlliedRearm`
  (12655-12812) and `LandOnCarrierEvent` (12814-13031). The desired-loadout
  storage is `CFSPlayer::m_ptDesiredLoadout` / `SaveDesiredLoadout()`
  (`fsship.h:475-524`), written on every BUY_LOADOUT (`FedSrv.CPP:6256`).
- **P488** (`a5320dfa`) brought the successor resolution into the port
  (`Allegiance.Core.Model.SuccessorUpgrade`, `SuccessorUpgrade.cs`), used at
  purchase in `ApplyLoadout` and at station build — this task reuses it at dock.
- **P441** priced the BUY_LOADOUT as the ship-value differential
  (`ShipValue(after) − ShipValue(before)`, `FedSrv.CPP:6182-6209`); this task
  composes that same pricing for the dock restock charge.

## Scope boundary / exclusions

- **Shop UI** — out of scope (unchanged).
- **Station successors** — P488 did them; unchanged.
- **NPC docking** — the restock is gated to player-piloted ships (native gates
  the desired-loadout walk on `IsPlayer()`, `FedSrv.CPP:12836`); NPCs keep
  their separate `UpgradeShip` path.

## Implementation

### New Ship primitives (`src/Allegiance.Core/Sim/Ship.cs`)
- `ReplacePartType(MountedPart, DataPartTypeIGC, short)` — replace a mounted
  part's type in place at the same mount (native `RefillPart` creates the
  successor at the same mount, `FedSrv.CPP:12614-12619`), re-realizing a
  `Weapon` (drops the old realized weapon, adds the new). Used by the
  successor upgrade.
- `SetShieldFraction(float)` — public write for the dock pool refill's
  `pshield->SetFraction(1.0f)` (`FedSrv.CPP:12952-12954`).

### Desired loadout (`src/Allegiance.Server/PlayerWallet.cs`)
- `PlayerWallet.DesiredLoadout` — the port of `CFSPlayer::m_ptDesiredLoadout`,
  stored on the wallet because it is connection-lifetime state like the balance
  (native keeps it on `CFSPlayer` alongside money). Written by
  `Server.SaveDesiredLoadout` after every successful BUY_LOADOUT (the free
  expendable-launcher part types currently mounted — `FedSrv.CPP:6256`,
  `fsship.h:475-498`), consumed by the dock restock.

### The dock restock (`src/Allegiance.Server/Server.Docking.cs`)
`RestockOnDock(LiveShip)`, invoked from `OnShipDocked`
(`src/Allegiance.Server/Server.Combat.cs`) at the dock transition for the
docking peer ship:
1. **Pool refill** (native `SetAmmo(SHRT_MAX)/SetFuel(FLT_MAX)/
   SetEnergy(MaxEnergy)/SetFraction(1.0)`, `FedSrv.CPP:12947-12955`):
   `Ammo = MaxAmmo`, `Fuel = MaxFuel`, `Energy = EffectiveMaxEnergy()`,
   `Fraction = 1.0`, `ShieldFraction = 1.0`.
2. **Successor upgrade of mounted parts** — `RefillPart` semantics: iterate
   the mounted parts (backwards so new parts aren't revisited,
   `FedSrv.CPP:12665`); for each whose base is buyable under the side's
   `development | building` techs (the native `RefillPart` mask,
   `FedSrv.CPP:12819` — no station-local gate), replace it with its furthest
   buyable successor via `SuccessorUpgrade.PartSuccessor`, resetting the
   amount (SHRT_MAX for free parts).
3. **Desired-loadout re-buy** — mount each saved desired part the ship no
   longer carries (upgraded to its successor) into a free mount slot.
4. **Charge (P441 composition)** — `cost = ShipValue(after) − ShipValue(before)`;
   on a positive cost, debit the wallet and broadcast the side-wide
   `MONEY_CHANGE(60)` via the existing `BroadcastMoneyChangeSideWide`
   (`FedSrv.CPP:6195-6209`). Affordable-gated (soft-fail): an unaffordable
   paid upgrade / paid re-buy is skipped (free refill still happens), the
   port's log-and-continue convention where native's BUY_LOADOUT would
   TrapHackBoot.

## Verification

### Tests (`tests/Allegiance.Core.Tests/DockAutoRestockLiveTests.cs`, +3)
Fail-first (each red when `RestockOnDock` is not wired into `OnShipDocked` —
verified by temporarily disabling the call; all 3 went red):
1. `Docked_ship_get_its_expendables_refilled` — drain every pool, cross the
   dock radius, assert all pools restored to full on the dock event.
2. `Docking_upgrades_mounted_part_to_successor_and_charges_after_research` —
   a mounted part stays its base under base tech; after the side researches the
   successor's tech a second dock replaces it with the furthest-buyable
   successor, charged the P441 differential.
3. `Docking_rebuys_paid_desired_launcher_and_charges_the_native_amount` — a
   paid launcher saved in the desired loadout but missing from the ship is
   re-bought on dock and the wallet debited its price with a side-wide
   `MONEY_CHANGE`.

### Measured core-data finding (important divergence, verified not assumed)
On PCore001 **every** part-successor chain preserves its base's price exactly:
across all 105 candidate base+launcher chains, **0** have a leaf whose price
differs from its base (all successors are free). So a successor **upgrade**
alone never changes ship value and the faithful native restock charge for it is
**0**. The positive "charges the native amount" is therefore exercised through
the **desired-loadout re-buy of a paid part** (test 3), which is the half of
the brief's charge that the core actually supports. Test 2 documents the
successor-upgrade charge as the exact P441 differential (0 on this core).

### Runs
- Whole-solution build (`/home/domingo/.dotnet/dotnet build`): **0 warnings /
  0 errors**.
- `scripts/run-tests.sh` (P471, sequential assemblies) — **3 consecutive runs**,
  only documented/environmental live-wire flakes interrupted, every one
  confirmed green in isolation:
  - **Run 1:** Lobby 12/12, MonoGame 1085/1085, ClientCore 402/403, Core
    3695/3698 → 4 flakes (`FullSessionKill…CrossSideKill`,
    `MineCrossSideVisibilityLiveTests…`, `LiveRipcordTests.First_activation…`,
    `LiveChatSectorWireTests.Individual_AllSector…`).
  - **Run 2:** Lobby 12/12, MonoGame 1085/1085, ClientCore 403/403, Core
    3697/3698 → 1 flake (`GameOverPlayersWireTests.Conquest_win…`).
  - **Run 3:** Lobby 12/12, MonoGame 1085/1085, ClientCore 403/403, Core
    3695/3698 → 3 flakes (`ShipsUpdateLiveTests.Cross_side…`,
    `LiveWinConditionTests.Board_refused_once_mission_over`,
    `LiveRipcordTests.First_activation…`).
- **Suite grew by 3** (the new dock-restock tests): Core 3695 → 3698.
- Every observed flake: (a) passes in isolation, (b) references **zero** P494
  symbols, and (c) the one that appeared correlated
  (`FullSessionKill…CrossSideKill`) was proven to also flap ~2/4 on the
  **pristine** tree (git stash, rebuild) — all are the pre-existing
  live-wire timing flakes catalogued in `docs/TEST_FLAKES.md`, not regressions.

## Commit
Committed on `master` (confirmed via `git log -1`), **not pushed**
(`git branch --show-current` = master).
