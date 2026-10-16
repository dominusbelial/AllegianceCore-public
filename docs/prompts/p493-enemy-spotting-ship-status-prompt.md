# P493 — enemy spotting: a side learns the status of enemy ships its probes and teammates see

## Why this task exists

Native reports an enemy ship's status to **every side that has seen it**,
not just its own. Native is at `~/Development/Allegiance/src/`.
- `CFSShip::ShipStatusRecalculate` (`FedSrv/fsship.cpp:405-425`) calls
  `ShipStatusSpotted(pside)` (`:237-285`) for every side in the ship's
  cluster that currently sees it, plus its own side.
- `ShipStatusHidden` (`:287+`) covers losing sight of it.

This is what makes probes and scouts **eyes for the team**: the sector map
and radar know where enemies were last seen, and in which sector.

In the port, `SHIP_STATUS(90)` is **same-side only**. See
`src/Allegiance.Server/Server.Broadcast.cs` (`MakeOwnShipStatus` ~795,
teammate sends ~463/629, NPC ~772). The 2026-09-24 notes record the
consequence: cross-side ships' sector had to be derived from
HeavyShipsUpdate (P375). The parity pass flagged this as "probes give no
remote vision of enemies".

## Scope

1. Port the spotted/hidden recalculation. When a side's visibility of an
   enemy ship changes (through the existing P270/P476 visibility rules:
   probes, ships and scan range), send that side the ship's status with
   native's fields and semantics. Send the hidden transition the same way.
   Use native's trigger points and cadence; don't send every tick.
2. On the client, cross-side status entries must update the enemy ship's
   last-known sector and state (`ClientWorld.Apply(ShipStatusMessage)`).
   That data has to reach whatever already renders known enemy positions,
   such as the sector-map threat data. Check whether P374/P460's map uses it,
   and wire the minimal piece if needed.
3. A live test: a side-0 probe in an empty sector spots a side-1 ship
   entering it. Side 0's peers then receive that ship's status with the
   correct sector, and get the hidden transition when it leaves or the probe
   dies.

## Explicitly out of scope

- Changing the visibility rules themselves (P270/P476).
- Alliances.

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
2. The report `docs/reports/P493_ENEMY_SPOTTING.md`.
