# P497 — a shields-down board hull can't reach the dock point: fix the capture reachability, then the final live P450 proof

## Why this task exists

P496 (`6e7e4dc4`, `docs/reports/P496_BOT_WARP_APPROACH_AND_CAPTURE_PROOF.md`
§4) got the Hvy Troop Transport to the enemy garrison and proved P450's
**refusal at full shields** live. The escorts then drove the shields **below
the 0.1 downed constant**, yet `captured` stayed **false** on both maps. P496
measured the boarder being held at **~500-585 u** from the station centre,
while the dock/capture trigger needs **`DockRadius` = 200 u**
(`src/Allegiance.Core/Model/Sector.cs:1501`, documented there as "deliberately
not a faithful port of `InGarage`").

**The port already anticipated this buried-dock-point geometry, so the
bug is probably narrower than P496 concluded:**
- Same-side ships are excluded from station collision **wholesale**, so
  friendly docking can reach the 200 u point inside large bases' 302-540 u
  hulls (`Sector.cs` ~2545-2560, ~2745-2755).
- A **different-side `c_habmBoard` hull with the station's shields below
  `DownedShieldFraction`** "is excluded so an infiltrator reaches its dock
  point" (~2755-2760; the capture path is in `ResolveDockCollisions`,
  ~3133-3152, with P450's gate).

So once the shields are down, the boarder **should** pass through the hull
to the dock point. **Find out why it doesn't.** Candidate causes:
1. the exclusion isn't firing: wrong shield value or timing, side
   resolution, an ordering problem with the collision pass, or P466's
   station-ramming collision (`846544d6`) adding a path that ignores the
   exclusion;
2. the bot never re-approaches after the full-shield refusal. It could be
   parked at its standoff or ramming distance, or `capture_attempt`'s
   choreography could be stuck in its first phase;
3. something else, found with evidence.

**Native** (at `~/Development/Allegiance/src/`): docking and boarding use
`CstationIGC::InGarage(pship, position)` (`Igc/stationIGC.cpp:385+`), real
garage geometry built from the station type's **land slots** (fighter vs.
capital: `GetLandSlots()` / `GetCapLandSlots()`). The callers are
`shipIGC.cpp:843` (friendly dock) and `:853` (board, after the downed-shield
gate at `:849-851`). A ship docks by flying into the bay, not by reaching the
centre.

## Scope

1. **Diagnose with evidence.** In a live `capture_attempt` (normal map,
   P473 harness on), log per tick once the shields are down:
   - the boarder's distance to the station centre;
   - its throttle and target;
   - whether the collision pass excludes the pair, and which branch it takes;
   - the station's shield fraction as `ResolveDockCollisions` sees it.
   Name the cause.
2. **Fix it at the root.**
   - If it's the exclusion or ordering (cause 1), fix the server path with a
     native citation, and add a failing-first loopback test: a shields-down
     hostile board hull flown in open flight from outside the hull captures.
     Note that P450's existing capture test may place the ship directly
     inside; this test must **fly in**.
   - If it's the bot (cause 2), fix the preset or bot law.
   - **Porting native `InGarage` land-slot geometry is in scope only if**
     the land-slot data is already decoded, or cheap to decode, **and** it's
     needed to make capture reachable. Otherwise keep the documented
     `DockRadius` approximation and name `InGarage` as a follow-up.
   - **Don't break friendly docking.** The existing docking tests (P53,
     P381, P482) must stay green.
3. **The final live P450 proof.** Run `capture_attempt` on the normal map
   and, if reachable, the debug map. The proof must show:
   - `refusalConfirmed: true`;
   - shields below 0.1;
   - **`captured: true`**, with the station's observed side change.

   That closes the parity pass's last live-proof gap.

## Explicitly out of scope

- The bot warp approach (P496) and dodge (P489).
- P450's gate logic itself, unless step 1 proves it's at fault.
- Station-ramming damage tuning.

## Verification

- The failing-first open-flight capture test, or the bot-side test if the
  cause is the bot.
- Existing docking, capture, rescue and ramming tests stay green.
- The live `capture_attempt` outcome JSON, pasted into the report.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P497_CAPTURE_REACHABILITY.md`.
