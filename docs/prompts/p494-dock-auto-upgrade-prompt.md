# P494 — on dock, refill and upgrade to the desired loadout (native RefillPart / desired-loadout restock)

## Why this task exists

This is P488's named follow-up **P488b**. When a player docks, native
refills ammo, fuel and charges, and **re-buys the player's desired loadout**,
upgrading each part to the station's current successor (`GetSuccessorPartType`)
as the side out-techs it. See `FedSrv/FedSrv.CPP` ~12790-12900: `RefillPart`,
then the desired-parts walk with `ErasePart(... ET_Magazine / ET_Dispenser /
ET_ChaffLauncher ...)` and the fuel/ammo packs. The port documents this as
**not implemented** (`src/Allegiance.Server/Server.Docking.cs:1087-1114`).
P488 (`a5320dfa`) added successor resolution for purchases; reuse it. Native is at `~/Development/Allegiance/src/`.

## Scope

1. Read native's whole dock refill path and port it at the port's dock
   event:
   - refill ammo, fuel and energy as native does;
   - track and restore the desired loadout (native's `GetDesiredLoadout`:
     find where it's set, probably the last bought loadout);
   - upgrade each restored part to its successor, using P488's helper;
   - charge money where native charges, composing with P441's pricing.
2. The client sees the result through the existing loadout and ack
   messages. Check which message native uses to tell the player, and use
   the same.
3. Tests:
   - a docked ship gets its expendables refilled;
   - after the side researches an upgrade, docking upgrades the mounted
     part to its successor and charges the native amount.

## Explicitly out of scope

- The shop UI.
- Station successors (P488 did them).

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P494_DOCK_AUTO_UPGRADE.md`.
