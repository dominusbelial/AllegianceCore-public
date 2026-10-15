# P496 — bots stop short of the aleph: fly *through* the warp (slow board hulls never transit), then the live capture proof (P450)

## Why this task exists

P490 (`4fca1711`, `docs/reports/P490_LIVE_PROOF_CAPTURE_ELIMINATION.md` §3)
proved elimination live, but **capture (P450) is still unproven**. The
boarding hull, a Hvy Troop Transport (id 26, accel 15.0, the largest radius),
never reaches the enemy garrison:

- **Normal map** (`m2xspiderw`, `debugMode=false`): it crosses its home
  sector, then **parks permanently** at the aleph. P490's trajectory:
  `t=76s pos [4287,2231,-881]` ("reaches the transit volume"), then
  `t=88s [4292,2234,-884]`, frozen from then on.
- **Debug map** (`debugMode=true`): it **never leaves its home sector**.

P490 attributed this to "hull maneuverability". **The code points at the
bot's approach law instead. Verify this; don't assume it.**
- The warp approach is `CombatFlyTo(bot, warpPos, 200f, 450f, 0)`
  (`tools/allegiance-mcp/BotManager.cs:1478`). In `CombatFlyTo` (~`:1440-1460`),
  `distFactor` drives throttle to **−1 (stop) once `dist <= arrival` = 200**.
- The server's transit trigger is a collision,
  `|ship − warp| ≤ warp.Radius + ship.Radius` (`src/Allegiance.Core/Model/Sector.cs`
  ~1420/~3124). P484 measured the aleph's transit radius at about **110**
  units.
- So every bot is commanded to **halt about 90 units outside the transit
  volume**. Fighters (accel 30) overshoot on momentum, so they cross anyway;
  the slow transport stops exactly where it's told. That would explain why
  only `c_habmBoard` hulls park, with P489's dodge law making it more visible
  by slowing everything near the hub.

The same `200/450` hop came from vizfight (P429 notes) and may be in
vizfight's copy too.

## Scope

1. **Confirm the mechanism with evidence.** Log the transport's distance to
   the warp centre, its throttle command, its speed, and
   `warp.Radius + ship.Radius` when it parks. Show it's the arrival stop,
   or find what it really is.
2. **Fix the warp approach so a ship flies *through* the aleph.** A warp
   hop has no "arrival" short of the transit sphere. Target the warp centre
   with no stop, or with arrival inside `warp.Radius + ship.Radius`, and keep
   P489's dodge pre-emption working. Apply the same fix in vizfight's ported
   copy if it has the same constants. Non-warp `CombatFlyTo` uses (combat
   pursuit, `:1317` with `40f, 220f`) keep their semantics, unless step 1
   shows they're wrong too.
3. **The debug-map "never leaves home" symptom.** Find its cause (dodge
   goal dominating in the home field, a seat or launch issue, or something
   else) and fix it if it's within the bot. If it's server-side, report it
   with evidence and a named follow-up.
4. **The live capture proof** (P490's unfinished item). Run
   `capture_attempt` on both maps. It must show:
   - `refusalConfirmed: true` at full shields;
   - shields below the downed-shield constant from the escort;
   - `captured: true`, with the observed station side change.

   If the run exposes a real P450 gate bug, report it and don't fix it.

## Explicitly out of scope

- Server flight physics and warp rules (both proven correct by P484).
- The dodge law itself (P489).
- Player autopilot (follow-up P489b).

## Verification

- A failing-first test that a slow board-capable hull, driven by the bot's
  warp-approach law, transits a real aleph. Use P484's
  `ClientWarpApproachLiveTests` setup with hull 26.
- The live `capture_attempt` outcome JSON for both maps, pasted into the
  report.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P496_BOT_WARP_APPROACH_AND_CAPTURE_PROOF.md`.
