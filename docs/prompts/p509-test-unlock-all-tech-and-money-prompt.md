# P509 — test unlock: grant sides all tech (+ test money) through the real tech path, so every hull and weapon can be playtested

## Why this task exists

**Domingo's live playtest (2026-10-03):** "our next move should be to bring more
ships, or we won't be able to test all the features, i can't test many of the
weapons from a scout". His docked shop offers **2 hulls / 15 parts / 9
launchers** (client log line `hangar/loadout screen (2 hulls, 15 parts, 9
launchers offered)`). That's **correct**: P441/P444/P468 made tech gating
real, so a fresh side's garrison sells starter hulls only. To reach bombers,
capital ships and the full weapon roster, a player must research and build
techbases, which is impractical for feature testing.

**Native has no "unlock all" option.** Its mission params only *restrict*
tech: `bAllowDevelopments` (`Igc/sideigc.cpp:117`, `FedSrv/fsmission.cpp:1948`,
client `clintlib.h:548`) and `bAllowSupremacyPath` / `bAllowTacticalPath` /
`bAllowExpansionPath` (`Igc/asteroidigc.cpp:183-195`). Native is at
`~/Development/Allegiance/src/`. This is therefore a **port extension for
testing**, in the same spirit as P473's `liveSettings.testHarness`: explicit,
off by default, loudly logged.

**Why P473's harness isn't enough:** it skips the tech gate **on the server**
for `mcp-`-prefixed connections only. The client shop's tech filter (P468,
fails closed) still hides every item the side lacks tech for. So a human
would get money but **still not see the hulls**. The unlock must grant the
**side** real tech through the same path research uses:
- P189's `CompleteDevelopment` ORs the bits into `Side.DevelopmentTechs` and
  broadcasts `SIDE_TECH_CHANGE(75)` / `DEV_COMPLETED(8)` /
  `SIDE_ATTRIBUTE_CHANGE(76)`.
- `CanBuy` / `StationSellsHull` check `TtbmRequired ⊆ station.TtbmLocal |
  side.Techs` (P283/P441/P444).

Then the server, the shop (P468) and the investment screen (P487) all agree.

## Scope

1. A **`liveSettings`** option (P378's settings home), for example
   `testUnlock { enabled: false, allTech: true, money: <int> }`.
   When enabled, at mission start (and after a P439/P480 rotation into a
   fresh mission):
   - every side gets **all tech bits** that any real core buyable requires.
     Derive the set from the core, OR-ing every hull, part, launcher,
     station-type, drone and development `TtbmRequired`, rather than
     blindly setting every bit. Grant it through the real tech path so
     clients get `SIDE_TECH_CHANGE`. Decide, citing native semantics,
     whether developments with **global-attribute effects** should also be
     marked completed. The default is **no**: unlock buyability only, so
     flight and combat stats stay unmodified. State the choice;
   - every player wallet gets `money` at seat, and after P443 starting money,
     so purchases are affordable. Purchases still go through real pricing.
   - Log a loud boot banner, plus one line per side granted.
2. **Still faithful where it matters:** P470's `GroupID >= 0` drone-hull
   filter still applies (players still can't buy drones). `IsObsolete`
   (P488) still hides out-teched items. Note in the report that granting all
   tech makes successors visible and their predecessors obsolete, and
   whether that hides anything a tester would want. If it does, report it;
   don't hack around it.
3. **Committed configs:** add the option, enabled, to
   `docker/compose.server.debugmode*.json`. **Don't** enable it in
   `docker/compose.server.json`; Domingo decides whether 221 runs with it.
4. **Proof:** a live wire test with the option on. A Bot connects, the side
   has the granted techs, the Bot buys a real tech-gated capital-class hull
   and a gated weapon through `BUY_LOADOUT`, and both are granted and charged.
   Also show in a client-level test (`ClientWorld` / `ShopCatalog`) that the
   shop's offered hull count goes from the starter set to the full player
   roster; report the numbers.

## Explicitly out of scope

- Changing real tech or economy rules when the option is off. That must be
  byte-identical to today.
- New ship art. Meshy regeneration for GigaCorp, OH, TF and Dreg is a
  separate, older follow-up.
- The own-ship model (P507/P508).

## Verification

- Failing-first tests for the grant and for shop visibility.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P509_TEST_UNLOCK.md`.
