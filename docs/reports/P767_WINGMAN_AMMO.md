# P767 — SIM-0100: NPC wingman "getting ammo" latch

Status: **implemented** (steps 2–5 work end to end at the Core level and are pinned by tests; the
server-side dock resupply effect closes the loop in production). No commits; changes left in the
worktree `pi/p767` (dispatcher records the WIP commit).

## What was built

The native rule: when a wingman's weapon reload **fails** (no pack in the cargo hold), the offline
client that runs the sim picks the nearest friendly reload-capable target, sets the plan to goto it,
sends the team chat "Out of ammo, I'm getting resupplies." (salNoAmmoSound) and latches
`m_bGettingAmmo` (`BaseClient::Reload`, `clintlib/clintlib.cpp:3700-3717`, inside the
`!m_fm.IsConnected()` `else` at `:3697`). The latch clears once ammo arrives in
`CshipIGC::PreplotShipMove` (`Igc/shipIGC.cpp:1713-1720`), on any new plan
(`SetCommand(c_cmdPlan)`, `Igc/shipIGC.h:755-762`), and while it holds the resupply goto burns the
afterburner (`shipIGC.cpp:2752-2754`, applied at `:2830-2833` when throttle is 1) and skips the
in-range target scan.

**Native multiplayer never does any of this**: FedSrv inherits `IgcSite::Reload`'s `false` default
(`Igc/igc.h:4923`), so the wingman branch is unreachable there. The port runs the sim server-side in
both modes, so the offline/multiplayer distinction is an explicit gate (below).

## Per-step mapping (native cite → port site)

| Step | Native | Port | Test |
|---|---|---|---|
| Trigger (failed reload + wingman) | `clintlib.cpp:3700-3717` | `Ship.TryStartAmmoResupply` (`src/Allegiance.Core/Sim/Ship.cs:7595`), called from `TryAutoReloadAmmo`'s `Depleted` outcome (`Ship.cs:3504`) | `GateOn_WingmanRunsOut_PlansGotoToReloadStation_Latches_AndChatsOnce`, `GateOff_MultiplayerDefault_NothingChanges`, `WingmanWithAmmoPackInCargo_ReloadsFromCargo_NeverTrips`, `NonWingmanNpc_NeverTriggers` |
| Gate (offline only) | `clintlib.cpp:3697` + `Igc/igc.h:4923` | `Mission.NpcWingmanAmmoResupply` (`Model/Mission.cs:112`), default **false**; `GameConfig.NpcWingmanAmmoResupply` (`ServerConfig.cs:57`, source-generated JSON, no reflection) wired at `Server.Lifecycle.cs:1208`; set **true** in `SkirmishOptions.CreateConfig` (`SkirmishOptions.cs:111`) | gate-on/gate-off pair above |
| Target search | `FindTarget(c_ttFriendly|c_ttStation|c_ttNearest|c_ttAnyCluster [+c_ttShip if hull has c_habmLandOnCarrier], c_sabmReload)` (`clintlib.cpp:3706-3710`); OT_ship interprets `c_sabmReload` as `c_habmCarrier` (`common.cpp:541-551`) | `Ship.FindResupplyTarget` (`Ship.cs:7658`): own-sector nearest over stations (`DataStationTypeIGC.SabmReload`) and `c_habmCarrier` ships, then the established `FindNearestFriendlyStationAnyCluster` BFS widening for stations | `LandOnCarrierWingman_PrefersANearbyFriendlyCarrier`, `WingmanWithoutCarrierCapability_IgnoresTheNearbyCarrier` |
| Plan + chat + latch | `SetCommand(c_cmdPlan, pmodel, c_cidGoto)`, chat, `SetGettingAmmo(true)` (`clintlib.cpp:3711-3716`) | `PlanTarget`/`PlanCommandId` writes, then `_isGettingAmmo = true`, then `SendDroneChat(1409, "Out of ammo, I'm getting resupplies.")` — same order as native; salNoAmmoSound = 1409 per the generated catalog (`NativeVoiceCatalog.Generated.cs:187,976`) | `GateOn_...` |
| Exactly once per depletion | native re-runs only on the next failed `Reload` ask (a zero-transition) | the latch suppresses repeats; the ask itself is already P361-debounced (`shipIGC.h:604-608`) | second half of `GateOn_...` |
| Carrier flight leg | native targets the carrier ship directly | `Ship.GettingAmmoCarrier` slot consumed by `ApplyNpcAutopilot` (same-sector / ripcord / cross-cluster warp, mirroring the P652 repair-retreat branch); landing resupplies through the existing P117 `PerformRelaunch` ammo refill (`Server.Docking.cs:1236-1238`) | `LandOnCarrierWingman_...` |
| Clear on ammo (pre-plot) | `shipIGC.cpp:1713-1720` | `Ship.OnUpdate` pre-plot block (`Ship.cs:8631`): undocked, no parent, not building, `Ammo > 0` → latch + carrier slot off, `PlanTarget = null; PlanCommandId = CidNone`. Docked ticks return at the top of `OnUpdate`, so the latch survives the docked resupply and clears on the first **undocked** pre-plot | `AmmoArrives_PreplotClearsLatchAndPlan`, `LatchSurvivesWhileStillEmpty_UndockedTick` |
| Clear on a new plan | `shipIGC.h:755-762` | `DronePlanChanged` resets the latch + carrier slot (`Ship.cs:7535`) — plan writes run **before** the latch set, the native order; P767b: the trigger's plan writes are guarded so the accepted attack survives (see Review fixes below) | `NewOrderMidTrip_ClearsTheLatch` |
| Afterburner on the resupply goto | `shipIGC.cpp:2752-2754` → `:2830-2833` (throttle == 1) | end of `ApplyNpcAutopilot`: `if (_isGettingAmmo && Controls.Throttle >= 1f) StateM \|= AfterburnerButton` (`Ship.cs:5940`); the wingman combat leg is skipped while latched (it would both re-acquire targets and wipe the bit) | `ResupplyGoto_UsesTheAfterburner_AtFullThrottle` |

### Server-side resupply effect (port adaptation, stated)

This port's station-dock restock is player-gated (`Server.Combat.cs:2606-2613`, native
`FedSrv.CPP:12836`), so a docked NPC wingman would never get ammo and the trip would not close. To
make the row real end to end, `ServerHost.DockWithStationEvent` now refills the ammo pool when a
**latched** NPC wingman docks at a `c_sabmReload` station (`ServerHost.cs:1219-1239`) — a **port
addition**: native has no drone station-dock restock (native multiplayer never latches at all —
`igc.h:4923`; offline native resupplies by landing). Its shape mirrors the unconditional part-walk
+ scalar reset of the native **player/carrier** restock seam — `FedSrvSiteBase::LandOnCarrierEvent`
(`FedSrv.CPP:12817-12830, 12855-12858`), the carrier-land refill, not a drone station dock — and
the port's own P117 carrier-land core refills exactly this way (`Server.Docking.cs:1236-1238`).
The latch stays set while docked and clears on the first undocked pre-plot; the P381
auto-launch undocks the wingman ~10 s later and the normal decision loop resumes.

## Documented reductions (honest, not silent)

1. **Carrier candidates are searched in the ship's own sector only.** Native's single `FindTarget`
   widens `c_ttAnyCluster` to remote clusters for ships too (`common.cpp:648-700`); the port's
   carrier leg has no cross-cluster BFS of its own, so a cross-cluster reload *station* is found
   instead. Stations keep the full cross-cluster BFS.
2. **The accepted order does not resume verbatim after a station resupply.** The port's dock
   transition clears `PlanTarget`/`AcceptedTarget` at dock (P386, `ServerHost.DockWithStationEvent`)
   — a pre-existing behavior — so after the resupply undock the wingman re-decides its default
   order, rather than resuming the paused accepted command like native's "check for running" path.
   The latch/plan clear itself is native-faithful and tested.
3. **The carrier resupply effect** rides `PerformRelaunch` (landing = relaunch with full pools, P117)
   — there is no "sit docked on a carrier" state in the port.

## Out of scope (per brief)

Magazine/dispenser/chaff depletion rows (SIM-0239/0267/0445/0452/0453), fuel, server broadcast rows,
bot/commander code, P766 constructor/goto code.

## Gate / verification (all measured on this machine, 2026-10-21)

- Whole-solution build: **0 warnings / 0 errors** (Debug and Release).
- AOT publish (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64
  --self-contained -p:PublishAot=true`): **0 warnings / 0 errors**, no IL2026/IL3050. Config JSON stays
  source-generated (`ServerJsonContext`); the new `GameConfig` property is a plain `bool` with a
  declared default (guarded-JSON convention — absent field keeps the default).
- New tests `tests/Allegiance.Core.Tests/P767WingmanAmmoTests.cs` (10 cases): **3× alone, 10/10 each**.
- Affected suites **3× alone**: `P764ReloadSiteTests` (7/7 × 3, the P764 reload seam),
  `AutoReloadOnEmptyTests` (4/4 × 3, P361/P412 auto-reload — the trigger's host path),
  `P674ParityScorecardTests` (8/8 after the matrix edit) — all pass every run.
- ONE fast-tier run: `tools/run-tests.sh --tier fast` — **PASS, 9419 tests, 272.7 s**
  (first attempt failed only `P674ParityScorecardTests` because the SIM-0100 CSV row's weight column
  was shifted; fixed and re-run clean — both attempts' logs kept).
- Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  NPC flight affected): **5× alone, 5/5** — times 113 / 111 / 112 / 112 / 112 s wall (in-test duration
  ~1 m 50 s each; the TT-1 base rate was ~95–101 s on an idle machine — no park event in any run).

## Evidence

- `~/allegiance-evidence/P767/` — raw test logs (p767-run*, p764-run*, p361run*, neverpark-run1..5,
  fast-tier.log; outside the repo, per the evidence-size rule).
- In-repo: this report only (≤ 2 MB rule; no assets needed — no screenshots cited).

## Review fixes (P767b)

Fix round on this task from Claude's review. Base: `pi/p767b` = `pi/p767` (6005c0cc) + `git merge
master` (P761, P768, HUD-13/14/15). Native rule restated: the trigger writes ONLY the plan slot
(`SetCommand(c_cmdPlan, pmodel, c_cidGoto)`, `clintlib.cpp:3711-3712`); the accepted slot is
untouched and the post-trip "check for running" plan logic resumes the accepted order
(`shipIGC.cpp:1936-1939`); a new plan clears the latch (`shipIGC.h:755-762`).

1. **An accepted attack order was lost.** `TryStartAmmoResupply` wrote `PlanTarget = st` unguarded;
   the setter calls `ForgetAcceptedWingmanAttack()` unless `_changingRetreatPlan` is set, wiping
   the remembered accepted attack target. Fix: the plan writes now run inside
   `_changingRetreatPlan = true; try { … } finally { _changingRetreatPlan = false; }` — the same
   pattern as the damage-retreat writes (`Ship.cs:8188`, `:8205`) — so the accepted order survives
   the trip; the latch set still comes after, native's order (`clintlib.cpp:3711-3716`). Because
   that guard makes `DronePlanChanged` (`Ship.cs:7535`) return early, its new-plan resets
   (`IsRunningAway`, `_orderBeforeRetreat`, `_retreatRepairShip` — native SetCommand(c_cmdPlan)
   ends running-away, `shipIGC.h:758-760`) are done by hand in the guarded block.
   Test: `AcceptedAttackOrder_SurvivesTheResupplyTrip_AndResumesAfterAmmoArrives` — deplete → latch
   set with the attack target still remembered → refill (clear-on-ammo pre-plot) → tick → the
   attack target is back. This test, not `AmmoArrives_PreplotClearsLatchAndPlan` (which only
   checks the plan was cleared), is what backs the "accepted order resumes" claim.
2. **The carrier leg never set the plan slot.** The old `PlanTarget` stayed and running-away
   wasn't reset, so the running-away branch outranked the carrier branch; the SIM-0110 recovery
   then copied the legal `AcceptedTarget` into the empty `PlanTarget`, whose
   `DronePlanChanged` cleared the latch and the carrier on the next tick. Fix: the carrier leg now
   writes `PlanTarget = null; PlanCommandId = CidGoto` inside the same guarded block (a real plan
   write, running-away reset by hand), before setting `_gettingAmmoCarrier` and the latch; and the
   accepted-order promotion at `Ship.cs:8841` is skipped while `_isGettingAmmo` is set.
   Test: `CarrierLeg_WritesThePlanSlot_KeepsTheLatch_AndFliesToTheCarrier` — accepted **station**
   order, far reload station, near friendly carrier → deplete → tick 3× → latch still set,
   `GettingAmmoCarrier` is the carrier, full-throttle afterburner flight closing on the carrier.
3. **CSV row SIM-0100**: evidence column `traced` → `tested`; the "SkirmishHost sets true" cite
   now points at the real line, `src/Allegiance.SinglePlayer/SkirmishOptions.cs:111`
   (`NpcWingmanAmmoResupply = true` in `SkirmishOptions.CreateConfig`); port-cite line numbers
   refreshed for the Ship.cs edits. Only the SIM-0100 line edited as text (LF endings, 16 columns
   re-verified).
4. **Over-claims corrected** (here and in the row note): the station-dock ammo refill
   (`ServerHost.cs:1219-1239`) and the dead-carrier latch drop (`Ship.cs:8633` — a latched
   wingman whose resupply carrier dies drops the latch; native has no carrier slot at all) are
   **port additions**, not native. `FedSrv.CPP:12817-12858` is `FedSrvSiteBase::LandOnCarrierEvent`
   — the **player/carrier** restock seam, not the drone station dock; the cite above now says so.

### P767b verification (all measured on this machine, 2026-10-21)

- Whole-solution build: **0 warnings / 0 errors** (Debug and Release).
- AOT publish (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r
  linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot`): **0 warnings / 0 errors**, no
  IL2026/IL3050; native binary produced.
- Both new tests fail **before** the fix (verified by reverting `Ship.cs` and rebuilding: 2 failed /
  10 passed) and pass after.
- P767 tests (`P767WingmanAmmoTests`, now 12 cases) **3× alone: 12/12 × 3**. Affected suites
  **3× alone**: `P652EconomyDroneAiTests` (retreat/repair, 64/64 × 3), `P762DroneRuleTests` +
  `P766SimDrillSpinTests` (drone-plan/accepted-slot, 12/12 × 3), `P674ParityScorecardTests`
  (8/8 after the matrix edit).
- ONE fast-tier run (`tools/run-tests.sh --tier fast`): 9599 tests, 271.0 s, **1 failure** —
  `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` (19 vs 20
  sockets/threads), **3/3 alone** afterwards → load flake per the rule; same
  reconnect-fixture class already in `docs/TEST_FLAKES.md` (P673/P647 entries). No Allegiance.Core
  failure.
- Showcase never-parks
  (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`): **5× alone,
  5/5** — 112 / 113 / 109 / 111 / 112 s wall, in line with P767's 111–113 s base rate; no park
  event in any run.

### P767b evidence

- `~/allegiance-evidence/P767B/` — raw logs (p767b-ammo-run1..3, p767b-p652-run1..3,
  p767b-droneplan-run1..3, p767b-retriedconnect-run1..3, p767b-neverpark-run1..5 (.log/.time),
  p767b-fast-tier.log); outside the repo, per the evidence-size rule.
- `.scratch/` used for the AOT output only, in the worktree, never committed, deleted at the end.

## Review fixes (P767c)

Second fix round on Claude's review of P767b. Base: `pi/p767c` = master + P767 (c4c7f6e6) + P767b
(5b234cb4); P767b's two asked-for fixes confirmed correct. Two probe tests showed the trip was still
broken in production. No commits; changes left in the worktree (dispatcher records the WIP commit).

1. **Blocker — the station-dock refill never fired.** `ServerHost.DockWithStationEvent` cleared
   `PlanTarget`/`PlanCommandId` (the P386 clear) **before** the refill gate read
   `sh.IsGettingAmmo` — but those setters call `DronePlanChanged()`, which clears
   `_isGettingAmmo` (`Ship.cs:7548`), so the gate was always false: a latched wingman docked,
   undocked with 0 ammo, and re-tripped forever (P767b's `IsRunningAway = false` reset guarantees
   the clearing branch runs). Fix: the resupply decision
   (`bool resupply = sh.IsNpc && sh.IsGettingAmmo && <c_sabmReload check>`) is now computed
   **before** the plan clear (`ServerHost.cs:1187`) and the refill happens on `resupply`
   (`ServerHost.cs:1252`). **Latch-clear choice (stated):** the latch clear rides the P386 plan
   clear's own `DronePlanChanged` — the same effect as native's
   `SetCommand(c_cmdPlan, NULL, c_cidNone)` clearing `m_bGettingAmmo` for a sub-player pilot
   (`shipIGC.h:758-763`) — not an extra explicit clear in the host; if the dock lands while running
   away (clear branch skipped), the undocked clear-on-ammo pre-plot drops it instead
   (`shipIGC.cpp:1713-1720`, `Ship.cs:8639`). No re-trip either way: the refill leaves
   `Ammo = MaxAmmo`, so `TryAutoReloadAmmo` never sees the Depleted zero-transition.
   Test: `StationDock_ResuppliesTheLatchedWingman_AndDoesNotRetrip` — server-level, through the real
   `host.DockWithStationEvent(ship, new DockStation(...))` path (not a direct `Ammo =` assignment).
   **Failed before the fix** (Ammo 0 after the dock), passes after, and the following undocked
   ticks stay quiet (chat count stays 1, latch stays clear).
2. **Major — the carrier leg lost the latch whenever an enemy was visible.** On the carrier leg
   `PlanTarget` is null (the goto rides `PlanCommandId`), so the
   `else if (PlanTarget is null)` default-order block still ran and
   `DecideWingmanDefaultOrder()` picked the visible enemy via `SetWingmanAttackOrder`, whose
   `DronePlanChanged` cleared the latch and the carrier. Native never reaches that path mid-trip:
   while getting ammo the plan **is** the carrier/station goto and therefore legal, so the
   `!LegalCommand(m_commandIDs[c_cmdPlan], …)` recovery does not fire — the accepted-order
   promotion (`Igc/shipIGC.cpp:1936-1939`) and `PickDefaultOrder` (`:1943`) sit inside
   `shipIGC.cpp:1934-1945` and only run for an illegal plan. Fix: the default-order block is now
   `else if (PlanTarget is null && !_isGettingAmmo)` (`Ship.cs:8868`); the accepted-order promotion
   just above it already carried the same guard from P767b. Other paths checked: the only other
   `SetWingmanAttackOrder` caller is the player command dispatch
   (`Server.ShipCommands.cs:1894` — a real new order, correctly clears the latch like native's
   SetCommand); `UpdateWingmanTravel`, `UpdateWingmanCombat` and `UpdateWingmanDefend` were already
   skipped while latched (P767), and the P151 take-cover call sits inside the now-guarded
   plan-null block.
   Tests: `CarrierLeg_WritesThePlanSlot_KeepsTheLatch_AndFliesToTheCarrier` **extended** with a
   visible enemy in the sector (`enemy.SideVisibility.SetSideVisibility(0, true)`) — **failed
   before the fix** (latch dropped, attack order taken), passes after (latch + carrier intact, no
   attack target, still closing on the carrier). `StationLeg_VisibleEnemy_KeepsTheTrip` added for
   the station leg; it passes on both sides of the fix (the station plan is a legal non-null plan,
   so the recovery never ran there) — it is a regression pin, not a fail-first proof, stated as
   such.
3. **Minor — incomplete new-plan reset.** The guarded trip-start block reset only `IsRunningAway`,
   `_orderBeforeRetreat` and `_retreatRepairShip`; native `SetCommand(c_cmdPlan)` for a sub-player
   pilot also resets `m_fractionLastOrder`, `m_timeRanAway`, `m_targetFirstNotSeen` and
   `m_checkCooldown` (`shipIGC.h:758-763`). The block now mirrors `DronePlanChanged`'s remaining
   resets by hand: `_fractionLastOrder = Fraction; _timeRanAway = LastUpdate;
   _targetFirstNotSeen = null; _wingmanCheckCooldown = 0;` (`Ship.cs:7636-7642`).
4. **Minor — comment over-claim reworded.** The `ServerHost` resupply comment no longer says native
   realizes the refill through `FedSrv.CPP:12817-12830/12855-12858`; it now states plainly that
   this is a **port addition** (native has no drone station-dock restock) whose shape mirrors the
   player/carrier `LandOnCarrierEvent` seam (`FedSrv.CPP:12817-12830` RefillPart loop,
   `:12855-12858` scalar reset; the port's P117 carrier-land core refills the same way,
   `Server.Docking.cs:1236-1238`).
5. **Row SIM-0100** (`docs/parity/sim-rules-matrix.csv`): kept `implemented`/`tested` — fixes 1 and
   2 pass with fail-first tests; cites refreshed (Ship.cs:8868 guard, Ship.cs:8639 pre-plot,
   ServerHost.cs:1187/1252 refill) and the note now says a **station dock clears the accepted
   order** (P386 reduction 2), so "accepted order resumes" holds only for the carrier/no-dock
   path, and the "(tested)" suffix on the undocked carrier-land resume was removed (no carrier-land
   resume test was added). Only the SIM-0100 line edited as text; LF endings and exactly 16 columns
   re-verified (474 rows, all 16-wide).

### P767c verification (all measured on this machine, 2026-10-21)

- Whole-solution build: **0 warnings / 0 errors** (Debug and Release).
- AOT publish (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r
  linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot`): exit 0, **0 warnings / 0
  errors**, no IL2026/IL3050; native binary produced.
- Fail-first confirmed on the unfixed code: `StationDock_ResuppliesTheLatchedWingman_AndDoesNotRetrip`
  and the enemy-extended `CarrierLeg_…` **failed** (2 failed / 12 passed across the suite);
  after the fixes the suite is 14/14.
- `P767WingmanAmmoTests` (now 14 cases) **3× alone: 14/14 × 3**. Affected suites **3× alone**:
  `P652EconomyDroneAiTests` (retreat/repair, 64/64 × 3), `P762DroneRuleTests` +
  `P766SimDrillSpinTests` (drone-plan, 12/12 × 3), `P674ParityScorecardTests` (8/8 × 3 after the
  matrix edit).
- ONE fast-tier run (`tools/run-tests.sh --tier fast`): **PASS, 9717 tests, 275.3 s**.
- Showcase never-parks
  (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  Allegiance.ClientCore.Tests): **5× alone, 5/5** — 113 / 114 / 114 / 114 / 114 s wall (in-test
  1 m 50 s each), in line with the P767b base rate (109–113 s); no park event in any run.

### P767c evidence

- `~/allegiance-evidence/P767C/` — raw logs (p767c-fast-tier.log, p767c-neverpark-run1..5.log,
  p767c-aot.log); outside the repo, per the evidence-size rule. Small run summaries in
  `.scratch/` (worktree, git-excluded, deleted at the end).
- In-repo: this report section and the CSV row only (≤ 2 MB rule; no assets needed).

## Claude review corrections (at merge)
- **The station-dock refill is not a port addition, and the round-2 "blocker" premise was wrong.** The native offline
  client (the only mode that latches) refills at dock: `WinTrekClient::DockWithStationEvent` →
  `CstationIGC::RepairAndRefuel` (`WinTrek/trekigc.cpp:4124-4128`, `Igc/stationIGC.cpp:480-497`, `c_sabmReload`).
  The port already refilled at **launch** (`ChangeStation` → `RepairAndRefuel`, `FedSrv.CPP:13189`, P760), and a
  reviewer probe on the pre-P767c code showed a real dock + launch gives ammo 1000, latch clear, no re-trip. The
  `StationDock_…` test skips the launch (sets `StationId = null` directly), which is why it failed first. The P767c
  dock-time refill is kept: it is redundant with the launch refill but matches the offline client's timing (ammo
  only). Items 1 and 4 above and the notes calling it a "port addition" are superseded by this.
- **Unreachable carrier:** the pre-plot block now also drops the latch when the carrier is out of space
  (`Sector == null`) or changed side, not only when it died — native finds such a plan illegal and `SetCommand`
  clears the latch.
