# P769 — two owed items from today's merges: SIM-0197 pinning test, SIM-0109 drilling finish

Model: GLM-5.3 Flash. Branch: `pi/p769b` (worktree of AllegianceCore; the P769 work cherry-picked as `e60c762b`). Not committed — left in the working tree. Review fixes round: `P769B_BRIEF.md`.

## Review fixes (P769b)

Claude's review found four problems in the P769 state: SIM-0109's build regressed on real rock sizes, two shared flight primitives were changed unasked, the CSV weight column was overwritten, and the report overstated what was verified. What was done:

### 1. BLOCKER — builders now build on real rock sizes (radii 60 / 200 / 400)

**Baseline recorded on the unfixed code first** (`~/allegiance-evidence/P769B/p769b-baseline-unfixed-theory.txt`): the flight test turned into a Theory over radii 60/200/400 and run before any fix — radius 60 built (with the latch deep inside), radii **200 and 400 never built in 400 s**: the ship parked at the strip pole with v≈0, ~11–13 units off the axis, `drilling=False`. Matches the review exactly.

**Root causes found while fixing** (all traced, see `~/allegiance-evidence/P769B/`):

- The P769 hand-rolled pole seek switched to a centre seek 30 units from the pole and the managed seek's brake ramp (`SteerToward(goal, 0, T·vmax)` commands throttle ≤ −1 whenever `d < T·vmax/2`) parks the ship short of the pole — off the line, outside the latch band, forever.
- The P769 `|v| < 5` latch gate could never fire on a creeping approach.
- The stopped stand-in latch in `ApplyBuilderDrillingControls` latched **nose-away** ships mid-creep; the drill hold (roll 1, throttle 0 = 50 % forward thrust) then sling-shot the ship around inside the band and the build approach never settled.
- With the dodge law back at master's version, a constructor at rest inside any map rock's/station's bounding sphere dodge-jittered forever (the dodge override ran before the build approach every tick).

**The fix — `Ship.TryBuilderRockApproach` is now the native `Waypoint` build-approach state machine** (`Igc/common.cpp:1385-1429` OT_asteroid case → `DoApproach`, `common.cpp:1157-1320`):

- Phase 1 (off the on-track band, offset² ≥ 50): seek the **strip rest point** — the ±axis pole at the rest radius `contact + 10` (native DoApproach goal, `common.cpp:1170,1187,1202-1219`; the ±axis strips, `common.cpp:1399-1400`) — the pole on the ship's side (native's best-strip tie order resolves to the first ±axis pole for off-line ships, `common.cpp:1239-1249`). The managed creep (`SteerToward` d/T ramp) settles the ship **on the line** at the rest point, which is what native's velocity-managed braking does.
- Phase 2 (on the line, inside the drill band distance² < (contact+25)², `shipIGC.cpp:2288-2296`): **pivot** — kill throttle, turn the nose onto the centre (native's on-line facing, `common.cpp:1294-1296`), the native pivot branch (`common.cpp:1312-1318`).
- **Completion/latch**: the drill block's stopped stand-in latches only when the nose faces the rock (native's charge gate `cosForward² > 0.999²`, `common.cpp:1302-1311`; native latches on gotoplan completion, `shipIGC.cpp:2810-2817`); a stopped misaligned builder gets the pivot instead of the latch. The latch is **positional + alignment** (on the line within radius + 25, ≈ the rest point), never on speed alone — the P769 `|v| < 5` gate is gone (master's stopped idiom restored as the velocity pre-gate). Latched, the hold (yaw 0 / pitch 0 / roll 1 / throttle 0) pushes the ship into contact and the drill-gated collision registers the build (`shipIGC.cpp:871-893`, `:874`).
- **Dodge masks for the build approach** (the one place fix 1 genuinely needed a dodge change, done the native way, minimal): native's seek/coast phases carry at most `c_gpmDodgeShips` and the on-line pivot/charge are `c_gpmNoDodge` (`common.cpp:1262-1264, 1281-1291, 1302-1318`) — a builder flying its BUILD approach now dodges **ships only** while off the line and nothing at all on the line; rocks and stations are never dodge obstacles for it. `ShipDodgeLaw` itself is back at master's version byte-for-byte.

**Test**: `P769BuilderCollisionBuildTests.Constructor_flies_from_its_station_and_builds_through_the_drilling_collision` is a Theory over radii **60, 200, 400**; each must build through the collision path (drilling debris in the creation tick) within a bounded time AND latch no more than a few units inside the rock surface (the latch position is sampled on the first drilling tick). Green 5/5 consecutive runs plus 3× in the gate.

**SIM-0109 → `implemented` holds**: all three radii build through the native-shaped approach — no `TryStartBuilderBuild` carve-out for axis rocks (it stays reduced to the axis-less/P652 parked-reservation case).

### 2. BLOCKER — CSV weight column restored

`docs/parity/sim-rules-matrix.csv`: SIM-0109 weight `4` and SIM-0197 weight `3` restored (the P769 edit had overwritten the weight field with test citations, which broke `scripts/parity-scorecard.py` — "invalid weight" — and `P674ParityScorecardTests`). Test citations moved to the evidence/notes fields; SIM-0109's stale evidence (P766-only) refreshed to the current tests; SIM-0197's evidence/native_class fields un-swapped while there. Edited as text, LF, `{16}` fields per row, exactly the 2 rows changed vs the P769 state. `python3 scripts/parity-scorecard.py` validates both matrices and prints the scorecards; `P674ParityScorecardTests` 8/8 ×3.

### 3. MAJOR — the unrequested dodge-law change reverted

`src/Allegiance.Core/Sim/ShipDodgeLaw.cs` restored to master's version exactly (no diff vs the base). Both P769 skips (shallow-overlap and separating-ship) are gone, `NoSolutionT` with them. The builder-side need is covered by the mask-based build-approach exclusion above, which is what native itself does. `ShipDodgeLawTests` (including the pinned `Already_inside_obstacle_dodges_straight_away_from_centre`) green.

### 4. MAJOR — the `SteerToward` dead-behind change narrowed

The P769 broad form (fixed `+yaw`, pitch zeroed, for every rear-hemisphere goal — NPC turns up to 270°) is reverted; only the degenerate tie-break remains:

```csharp
if (forward * dir < 0f && body.LengthSquared() < 1e-4f)
    body = new Vector3(0f, 1f, 0f);
```

Test (`P769bReviewFixTests.Rear_hemisphere_goals_turn_either_way_and_exactly_behind_still_turns`): goals 120° off to either side produce **opposite** yaw signs and each commanded yaw carries the nose toward its goal (verified by applying the yaw the way `ExecuteMove` does, `ShipKinematics.Step`: `orientation.Yaw(rate·dT)`); an exactly-behind goal still turns (deterministic full yaw).

### 5. MINOR — SIM-0197 refactor leftovers

`Game1.OnTargetKilled`: the source/cluster gate is now computed **inside the session lock** via `TargetKilledSourceInCluster(world, own)` on the raw own ship (the gunner→parent mapping the test pins is now the production path); the inline `source` resolution block is gone; no `world.Ships` reads outside `_session.Gate`. The 10 `P769TargetKilledRetargetTests` stay green.

### 6. Report honesty corrections

- The P769 claim "**Constructors still build** — verified" held **only for radius 60** (the review's sweep showed 200–400 never built). Corrected here: the evidence is now the 60/200/400 Theory on the fixed code.
- The P769 fast-tier evidence could not have come from the final CSV state (the weight column was broken, so P674 failed on it). The gate below was re-run end-to-end on the **final** working-tree state.
- The P769 report and the `TryBuilderRockApproach` doc comment described phase 1 as "closest point on the axis line" while the code sought the fixed pole and switched phases by a 30-unit radius. The code and the doc now agree (strip rest point; the phase split is the native on-line band).

## 1. SIM-0197 — `Game1.OnTargetKilled` retarget rule pinned (partial → implemented)

Unchanged from P769: the decision is extracted into the pure static `Game1.DecideTargetKilledRetarget(currentCommand, killed, victimIsEnemy, sourceInCluster, select, autopilotEngaged, planTarget)` returning `TargetKilledDecision(Acted, Pick, Reengage)`; `OnTargetKilled` gathers inputs and applies the side effects. Native `WinTrekClient::TargetKilled` (`WinTrek/trekigc.cpp:2934-2974`). Pinned by `tests/Allegiance.ClientCore.MonoGame.Tests/P769TargetKilledRetargetTests.cs` (10 tests): not the current target → no-op; source not in a cluster → no-op (gunner → the parent carrier via the real `ClientWorld.Apply` wire path); enemy mask rescue > miner/builder > nearest; friendly mask single abm=0 pass; the dead model never re-picked; autopilot re-engages only when the plan target was the victim. P769b: the cluster gate moved inside the lock (fix 5).

## 2. SIM-0109 — the drilling latch starts the build, as native does (partial → implemented)

Native: drilling begins on goto arrival (`Igc/shipIGC.cpp:2810-2822`); the building effect is created **only** by a drilling builder colliding with its rock (`shipIGC.cpp:871-893`, drill-mask gate at `:874`); the build approach is `Waypoint::c_oEnter` → `DoApproach` down the rock's spin axis (`shipIGC.cpp:4014-4016` → `common.cpp:1385-1429`, rest radius `r_rock + r_ship + 10`, `common.cpp:1187`).

**Changes (in the brief's order):**

a. **`src/Allegiance.Core/Model/Sector.cs`** — the drilling-builder collision branch is gated on `ship.IsDrilling` (native `:874`): effect + drilling debris only for a latched builder. The approach exemption (never bounce off the build target) is kept exactly as it was.

b. **`src/Allegiance.Core/Sim/Ship.cs`** — the build approach is `Ship.TryBuilderRockApproach`, now the native DoApproach state machine (see the Review fixes section for the P769b shape and the failure modes it removed). The stopped latch stands in for gotoplan completion with the native charge alignment; the collision registers the build.

c. **`Ship.TryStartBuilderBuild`** — reduced to rocks WITHOUT a usable spin axis (native asserts the axis, `common.cpp:1178`); the P652 parked-reservation case is named in `P769BuilderCollisionBuildTests.Parked_reservation_constructor_builds_through_the_stopped_sphere_fallback_only`. With the P769b approach fix no carve-out for axis rocks is needed.

**Constructors build at every native buildable size** — verified on the final code:
- `P769BuilderCollisionBuildTests.Constructor_flies_from_its_station_and_builds_through_the_drilling_collision(60|200|400)` (Theory): a bucket-spawned constructor undocks, flies to the ordered rock, latches drilling outside the surface and the station gets built through the drilling collision (debris in the creation tick; full completion asserted: rock removed from Mineable, `StationRuntimes` entry with the right type/side). 5 consecutive runs + 3 gate runs green.
- `P652ConstructorBuildTests` (all 4), `P762DroneRuleTests` (all), `P766SimDrillSpinTests` (all 7) green — the P766/P762 latch fixtures were amended to the native charge alignment (stopped+aligned latches; stopped misaligned pivots, `common.cpp:1312-1318`), which is a strictly more faithful pin than P766's orientation-blind latch.

**Amended test**: `P762DroneRuleTests.Drilling_collision_creates_one_building_effect_and_hull_hit_debris` (P769) uses the native order (arrive on-axis aligned → latch → contact). Assertions unchanged (exactly one effect + drilling debris from the collision).

## Verification (gate — final working-tree state)

- Whole-solution build `dotnet build AllegianceCore.slnx -c Release`: **0 warnings, 0 errors**.
- AOT publish (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true`): clean, no IL2026/IL3050.
- Affected tests 3× alone, final code state:
  - Core `P652*|P762*|P766*|P769*|P769b*|ShipDodgeLaw*|P674ParityScorecard*`: 122/122 × 3;
  - MonoGame `P769*|P760*`: 10/10 × 3;
  - ClientCore `P760*|P762*|ClientAutopilotDodge*`: 8/8 × 3;
  - the 60/200/400 build Theory additionally 5 consecutive standalone runs.
- ONE fast tier (`tools/run-tests.sh --tier fast`): **9718 tests, PASS** (272.8 s; `~/allegiance-evidence/P769B/fast-tier-summary.json`).
- Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`, constructors fly in it): **5/5 PASS** (~1 m 49 s each — no slowdown vs the base rate).
- `python3 scripts/parity-scorecard.py`: both matrices validate; `P674ParityScorecardTests` 8/8 ×3.
- Raw logs: `~/allegiance-evidence/P769B/` (gate results, unfixed baseline, fast-tier summary).

## CSV

`docs/parity/sim-rules-matrix.csv`: SIM-0109 and SIM-0197 → `implemented`, **weights 4 / 3 restored**, test citations in the evidence fields, notes updated (P769 + P769b). LF endings, `{16}` fields per row, exactly 2 rows changed vs the P769 state.

## Out of scope / not done

- Wingman ammo (P767), station broadcast (P768), HUD renderers, server broadcast files, `tools/allegiance-mcp` — untouched, per the brief.
- Stated simplifications (documented in the code): the strip seek uses the port's managed creep instead of native's velocity-managed braking; the completion gate uses rest velocity where native gates on turn rates (the port's proportional face law wobbles around the facing instead of settling the rates); the build approach dodges ships only while off the line and nothing on the line (native's masks, `common.cpp:1262-1318`).
- The build approach's initial turn can be wide (the bucket constructor undocks facing away and dog-legs out before converging on the strip) — native's velocity-managed GotoPlan turns tighter; the build still completes well inside the 400 s bound at every tested radius.
