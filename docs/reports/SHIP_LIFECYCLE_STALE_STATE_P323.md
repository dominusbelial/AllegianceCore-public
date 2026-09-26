# P323 — Two ship-lifecycle stale-state bugs: oxygen-death ghost ship, stale MiningAsteroid

Two MEDIUM findings (M6, M7) from the `zai`/glm-5.3 full-codebase server review
(`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-glm53-full-codebase.md`)
landed on `master` (commits on master only; **not pushed** — Domingo pushes).
They are the same shape of bug — a ship-state field isn't reset/cleared on a
specific real transition, leaving stale state that misleads later logic — so
they are batched as one task with separate commits.

Both fixes are in the **port** (`AllegianceCore`); native source at
`~/Development/Allegiance` was **not** modified (read-only reference).

---

## 1. M6 — oxygen-death leaves a live "ghost" ship when no friendly station exists

**Finding.** `Ship.OnUpdate()`'s oxygen branch (`Ship.cs:5630-5642`) raises
`Mission?.Site.KillShipEvent(now, this, null, 1.0f, …)` WITHOUT zeroing
`_fraction` or removing the ship from its sector — unlike the combat death path,
where `Ship.ReceiveDamage` zeroes the fraction (`Ship.cs:3199`) and
`Sector.ResolveCombatCollisions` removes the dead ship
(`if (ship.Dead) _models.Remove(ship)`, `Sector.cs:1579`). Then
`Server.BroadcastKillShip`'s no-friendly-station branch (`Server.cs:12452-12457`)
logged "no respawn, stays dead" and returned **without removing anything**. Every
peer got `KILL_SHIP`/`SHIP_DELETE`/explosion (so clients dropped the object), but
the server-side ship stayed alive in its sector's `_models` (and `_peerShips`),
still flying/mining/shooting and broadcast every tick — a permanent
server/client desync. The P279 writeup even flagged it: the fire-once guard was
only needed because "a no-friendly-station death leaves the pod in-sector."

**Fix** (`src/Allegiance.Server/Server.cs`). A new `FinalizeDeadShip(Ship)`
helper marks the ship Dead (`SetFraction(0f)`) and removes it from its sector
(`sec.Remove(ship)` + `Sector = null`) — matching the port's death contract and
native's unconditional `pship->SetCluster(NULL)` for a true death
(`FedSrv.CPP:12045`) plus the bot "delete the drone" path (`FedSrv.CPP:12084`).
It is called in **all four** "no respawn, stays dead" branches of
`BroadcastKillShip` (bot/no owning peer, no friendly station, side not in
mission sides, lifepod hull not in core). Idempotent: a combat death that later
gets removed by `ResolveCombatCollisions` is unaffected (the removal is then a
harmless no-op). `_peerShips` is deliberately left alone for a peer-owned victim
— consistent with the pre-existing combat-death-no-respawn behavior (the player
stays connected; native likewise leaves the connected player ship-less).

**Live proof.** `LiveShipDeathFinalizeTests.Oxygen_dead_lifepod_with_no_friendly_station_is_finalized_server_side`
boots a real in-process server; a peer-owned drifting Lifepod with its oxygen
timer test-compressed to 1 s and no friendly station to respawn at has its timer
expire. After the death the ship is asserted **Dead**, `Sector == null`, in **no**
mission sector's `_models`, and absent from **every** sector's real per-tick
ship-update candidate list — it is genuinely gone (no longer flying / broadcast),
not the pre-P323 ghost. The wire still carries `KILL_SHIP` (launcher NULL) +
`SHIP_DELETE`, and **no** `ENTER_LIFEPOD` arrives (no respawn). The no-friendly-
station branch is reached via `FindFriendlyStation` returning null (a null/side
owned-by-no-station both return null — the port's "team's last station destroyed"
stand-in; the branch is identical). Removing the fix leaves the ship never-Dead
and still in `_models` → test red (verified, restored).

## 2. M7 — full-bag miner keeps a stale `MiningAsteroid` reference

**Finding.** `Sector.ResolveMining()` (`Sector.cs:2668`):
`if (ship.Ore >= Ship.MineCapacity) continue; // bag full` executed **before**
the per-tick `ship.MiningAsteroid = null;` reset (`Sector.cs:2672`), so a miner
that fills its bag and departs (even minutes of cross-sector travel) kept
`MiningAsteroid` pointing at the departed rock. `BroadcastShipStates`'
`MiningMask` bit and `BroadcastOwnShipMining` both key off
`MiningAsteroid is not null`, so the client kept rendering mining feedback and
orienting toward a rock in another sector, and the P254 collision-exclusion
against that rock stayed active.

**Fix** (`src/Allegiance.Core/Model/Sector.cs`). The
`ship.MiningAsteroid = null;` reset is hoisted to the **top** of the per-ship
block, BEFORE the `Dead` / `Mission null` / **bag-full** / docked guards — the
reset now happens regardless of *why* mining isn't continuing this tick (a
qualifying rock below re-latches it, as before).

**Live/model proof.** `MiningTests.Full_bag_miner_clears_MiningAsteroid_despite_the_bag_full_guard`
constructs the exact post-departure stale state — a ship with
`Ore == Ship.MineCapacity` and `MiningAsteroid` pointing at a rock far away —
runs one mining `Update`, and asserts `MiningAsteroid == null` (and ore still at
capacity). Reverting the reorder keeps the stale pointer → test red (verified,
restored).

---

## Scope boundary / exclusions

- CRITICAL/HIGH findings (C1/C2/H1-H6, now P311-P318) — separate tasks.
- The other MEDIUM findings — P322 (cleanup batch: M1/M5/M9) and P324 (tick-cost
  batch) — separate tasks.
- Native `~/Development/Allegiance` — untouched.

## Build / test status

- Whole-solution build (Core + Server + Bot + Lobby + tests): **0 warnings,
  0 errors**.
- New tests green in isolation: `LiveShipDeathFinalizeTests` (M6),
  `MiningTests.Full_bag_miner_clears_MiningAsteroid…` (M7), plus the unaffected
  neighbours `LiveLifepodOxygenTests`, `LiveSuicideShipDeleteTests`,
  `GhostShipSectorMembershipTests`, and the rest of `MiningTests` (22 tests).
- Both fixes proven **must-fail-if-removed** (each reverted → its test red, then
  restored green).
- Full-suite runs (baseline 3266 → **3268** with the two new tests): see the
  commit message / final handoff for the run log. Expected interruptions only:
  the documented `LiveTurretGunnerBindingTests` persistent timing flake plus two
  live-network flakes (`CargoBayReloadTests` ×2, and
  `StationsUpdateLiveTests.Damaged_station…`) that were each confirmed to flap on
  the **pristine** tree (git stash -u) — environmental, not regressions.
