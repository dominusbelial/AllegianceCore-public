# P779b-1 — medevac slice: rescue-capable scout drops a rescue probe, bot pods target it

Base: **master** `0cb2cb5febe3a9e266ed70cb5e2b8047250d60bf` (dispatch HEAD, re-checked before editing).
Branch/worktree: `pi/p779b1` (no commit, no push — changes left in the tree).

Bot-side only: production edits are in `tools/allegiance-mcp/**`, plus tests, this report and the
coverage row. **No change in `src/Allegiance.Core`, `src/Allegiance.Server`, `src/Allegiance.ClientCore*`,
or the wire.** P780's server half (`Sector.ResolveProbeRescueEvents` → `Server.PerformProbeRescue`) was
re-read and needed no change.

## Native rules (parity) with cites

1. **A probe rescues a lifepod on touch** (P780, already ported; not changed here). `Igc/shipIGC.cpp:896-909`:
   a lifepod hull touching an `OT_probe` whose type has `c_eabmRescueAny` (`:905`) — or `c_eabmRescue` when
   the pod's side is the probe's side or allied — calls `RescueShipEvent(pod, NULL)`. The probe is not
   consumed. Bits: `Igc/igc.h:781-782` (`c_eabmRescue = 0x2000`, `c_eabmRescueAny = 0x4000`).
2. **A pod's eject autopilot target** is the nearest **friendly**, **any-cluster** station **or probe**
   whose capabilities include rescue/land: `clintlib/appmsg.cpp:230-237`,
   `FindTarget(pShip, c_ttStation | c_ttFriendly | c_ttAnyCluster | c_ttProbe, …, c_sabmRescue | c_sabmRescueAny | c_sabmLand)`.
   The same rule classifies a rescue probe as a lifepod `Goto` target in `Igc/shipIGC.h:2141-2146`.
   The port already mirrors it for the human client in `src/Allegiance.ClientCore.Game/Game1.Eject.cs`
   (P540: `ChooseEjectRescueTarget`, `RescueStationMask`, `RescueProbeMask`); the bot pod pick now reuses
   that rule (own side or `world.AreAllied`, probe eabm rescue bits, any cluster).
3. **Who drops a rescue probe is NOT native AI.** Native has no AI that buys, mounts, carries or fires a
   rescue probe. `FedSrv`'s drone shell `CFSDrone` (`FedSrv/fsship.h:577-621`, `FedSrv/fsship.cpp:1418-1460`)
   only launches/docks and emits light/heavy ship updates — it has no purchase, mount or dispenser-fire
   path. The only probe deployer is the **human** `FIRE_EXPENDABLE` handler (`FedSrv/FedSrv.CPP:5749-5778`,
   `CreateObject(OT_probe, …)` at `:5771`). Consequently **who** the medevac scout is, **where** it drops
   and the drop gates below are a **showcase design choice**, not parity. Only rules 1 and 2 are parity.
4. Probes disappear on destruction/expiry (`Igc/probeigc.cpp:133,335-338`), so the pod re-evaluates its
   target from `cs.World.Probes` (which drops such entries) once per second while it flies.

## Verified core facts (test: `Rescue_launcher_is_chosen_from_civ_techs_and_prefers_lifespan_then_hp`)

| Probe | expendable | launcher part | eabm | lifespan | hp | price | buyable by (starting techs) |
|---|---|---|---|---|---|---|---|
| Rescue | 19 | 19 | `0x2000` (Rescue) | 1200 s | 25 | 250 | **Iron Coalition (25)** |
| Beacon | 51 | 73 | `0x2000` (Rescue) | 1200 s | 1 | 100 | **Technoflux (2)** |
| Sanctuary | 2 | 2 | `0x4000` (RescueAny) | 1800 s | 25 | 100 | **Belters (101)** |

Ga'Taraan (3) has no rescue bit in its starting techs → **no medevac scout on side 1** (asserted). P780's
`Rescue_probe_inventory_matches_the_core` already pinned these values; the new test pins the buyability
and the medevac ranking from `LauncherTtbmRequired ⊆ side techs` (`TechTreeBitMask.IsSubsetOf`).

## Before / after

| | Before (master `0cb2cb5f`) | After |
|---|---|---|
| Scout loadout | every scout's dispenser slot scores a probe launcher 0 when `(eabm & 0x6000) != 0` (`TechLoadoutSelector.SupportScore`) → no bot ever carries a rescue probe | on **one** bot per side (the medevac scout), the Dispenser slot takes the side's best rescue launcher (longest Lifespan, then HP); every other bot keeps the exclusion |
| Bot pod target | `DrivePodHome`/`PickPodHomeStation` consider only restart-capable **friendly stations** (P560), picked once per life | `PickPodTarget` adds friendly rescue-capable **probes** next to those stations, same route-aware order (fewest hops, then Euclidean), re-evaluated once per second; a probe target is flown **into** (touch rescue), a station target keeps the P575 bay approach |
| Rescue probe use on the rig | **0** probe rescues vs 173 ship rescues (batch 19, 3 h) | medevac scout drops one when (a)–(d) hold; logs `[p779b] medevac`; pods log `[p779b] pod bot … -> rescue probe` |

## Design (showcase, not parity)

* `MedevacScoutsPerSide = 1`. Per side, the lowest **stable bot id** among that side's scout-role bots
  whose civ can buy a rescue-capable launcher (`PickMedevacScouts`). The id is the roster id
  (`_botSeq`, assigned at spawn), **not** the ship id (server re-allocates ship ids on a reseat), so the
  pick is stable across a respawn. Eligibility is decided from core data only: the probe type's
  `LauncherTtbmRequired ⊆` the side's observed techs (`PickRescueProbeLauncher`). No civ name/id branch.
  A side whose civ cannot buy one logs once: `[p779b] no medevac scout for side S: civ C has no buyable
  rescue-capable probe launcher`.
* Loadout ranking: `Lifespan * 1000 + min(999, HitPoints)` → Belters Sanctuary (1800 s) beats
  Rescue/Beacon (1200 s); Rescue (25 hp) beats Beacon (1 hp). Implemented as the
  `rescueProbe` exception in `TechLoadoutSelector.SupportScore`; the rescue launcher is still subject to
  the normal native purchase gates (`Create`/`Legal`: tech, dock-local, mount, successor, price).
* Drop gates (`ShouldDropMedevacProbe`; all must hold): (a) the sector has **no own-side station**;
  (b) `≥ MedevacMinOwnPods (2)` own-side lifepods flying there **or** `≥ MedevacMinRecentEjects (3)`
  own-side pods first observed there in the last 60 s; (c) no own-side rescue-capable probe within
  `MedevacSpacing = 1500`; (d) `now - LastRescueProbeAt ≥ max(8, launcher LoadTime)`. Drops at the bot's
  own position with `SendFireExpendable(EquipmentType.Dispenser)` (same as the scouting branch), logs
  `[p779b] medevac bot=… side=… sector=… pods=… ejects60=… probe=<expendable id>`. The eject signal is
  what the bot already sees (lifepods in `cs.World.Ships`, first-seen stamped per sector) — a client
  never receives a native eject log.
* Non-medevac bots never drop a rescue launcher (gate on `bot.MedevacScout`), so the design stays at one
  medevac scout per side.

## Where

* `tools/allegiance-mcp/BotManager.Medevac.cs` (new): constants, `MedevacCandidate`,
  `PickMedevacScouts`, `IsScoutRole`, `ShouldDropMedevacProbe`, `RefreshMedevacScouts`,
  `LogNoMedevacScout`, `ObserveOwnPods`, `MaybeDropMedevacProbe`.
* `tools/allegiance-mcp/TechLoadoutSelector.cs`: `PickRescueProbeLauncher`; `rescueProbe` ranking
  parameter on `Select`/`SupportScore`.
* `tools/allegiance-mcp/BotManager.TechLoadouts.cs`: `medevacScout` parameter on `PickTechLoadout`;
  relaunch repick passes `bot.MedevacScout`.
* `tools/allegiance-mcp/BotManager.Dispensers.cs`: a rescue probe launcher goes to the medevac branch
  (never the scouting branch, even when the gate blocks).
* `tools/allegiance-mcp/BotManager.cs`: `PodTargetPick` + pure `PickPodTarget` (P560
  `PickPodHomeStation` now delegates with no probes, so P560 cases are unchanged), `RepickPodTarget`,
  probe fly-in in `DrivePodHome`, `BotId`/medevac fields, medevac flags on spawn/upgrade, roster refresh
  wrapper, 1 s refresh in the loop.
* `tools/allegiance-mcp/BotManager.Lifecycle.cs`: reset the new pod/medevac latches per life; roster
  refresh on removal.
* Tests: `tests/Allegiance.ClientCore.Tests/P779b1MedevacTests.cs`,
  `…/P779b1MedevacDropTests.cs`, `…/P779b1MedevacLiveTests.cs`.
* Coverage: `~/allegiance-scratch/coverage/MECHANISM_COVERAGE.md` row **P04** (bots column / rig grep /
  "slice 1 done") and the P779b plan row. Rig count left at 0 for Claude to fill after deploy, as asked.

## Tests and red-before evidence

New tests (all in `Allegiance.ClientCore.Tests`, artwork-backed ones skip without the corpus):

`P779b1PodTargetTests` (pure rule):
1. `Own_rescue_probe_in_pod_sector_beats_a_station_two_hops_away`
2. `Enemy_side_rescue_probe_is_ignored`
3. `Own_side_sanctuary_probe_is_a_candidate`
4. `Allied_rescue_probe_is_a_candidate`
5. `Probe_that_cannot_rescue_is_ignored`
6. `Unreachable_probe_is_skipped_like_an_unreachable_station`
7. `Fewer_hops_still_beats_euclidean_distance_across_kinds`
8. `With_no_probe_the_pick_is_the_P560_station_pick` (all five P560 station cases + the null case)

`P779b1MedevacTests`:
9. `Rescue_launcher_is_chosen_from_civ_techs_and_prefers_lifespan_then_hp`
10. `Medevac_scout_loadout_carries_the_rescue_launcher_on_that_bot_only`
11. `Lowest_id_capable_scout_per_side_is_the_only_medevac_bot_and_survives_a_respawn`
12. `At_most_MedevacScoutsPerSide_is_one`

`BotFixRegressionTests` (new P779b-1 partial, real `MaybeUseDispensers` + wire-applied world):
13. `Gate_a_own_station_in_the_sector_blocks_the_drop`
14. `Gate_b_blocks_with_one_pod_and_no_recent_eject_signal`
15. `Gate_b_recent_ejects_can_substitute_for_live_pods_then_all_true_drops_once`
16. `Gate_c_own_rescue_probe_within_spacing_blocks_the_drop`
17. `Gate_d_pacing_blocks_until_max_8s_loadtime`
18. `All_gates_true_sends_exactly_one_fire_expendable`
19. `A_non_medevac_scout_never_drops_its_rescue_launcher`

Live loopback (Tier Live):
20. `P779b1MedevacLiveTests.Pod_flies_into_an_own_side_rescue_probe_and_is_rescued_across_600u` — real
    MCP combat bot ejected into a real lifepod in a **non-home** sector; a real type-19 Rescue probe
    deployed 600 u away through P780's FIRE_EXPENDABLE helper recipe (mount 778 + retry, deployer resolved
    by its own ship id); the pod's controller logs the probe target, the server logs
    `rescued by rescue probe` and docks. Observed in ~4 s.

### Which fail on the unfixed code

Method used: the pure seams (`PickPodTarget`, `PickMedevacScouts`, `PickRescueProbeLauncher`,
`ShouldDropMedevacProbe`, `MedevacScoutsPerSide`) do not exist on base, so tests 1–12 are red-before by
construction (they cannot compile against master, the same failing-first shape as P560/P780). For the
integration tests I additionally reverted only the **behaviour** (probe list forced empty in
`RepickPodTarget`, `rescueProbe=false` in `PickTechLoadout`, medevac branch disabled) and ran:

| Test | Base-behaviour result |
|---|---|
| 10 `Medevac_scout_loadout_carries_the_rescue_launcher_on_that_bot_only` | **FAIL** — no rescue launcher in the loadout |
| 15 `Gate_b_recent_ejects_…` | **FAIL** — 0 FIRE_EXPENDABLE where 1 then 2 are expected |
| 16 `Gate_c_own_rescue_probe_within_spacing_blocks_the_drop` | **FAIL** — the allowed second half drops nothing |
| 17 `Gate_d_pacing_blocks_until_max_8s_loadtime` | **FAIL** — no drop at the 8 s floor |
| 18 `All_gates_true_sends_exactly_one_fire_expendable` | **FAIL** — 0 drops |
| 20 live `Pod_flies_into_an_own_side_rescue_probe…` | **FAIL** — pod never touches the probe (60 s timeout) |
| 13 `Gate_a…`, 14 `Gate_b_blocks…`, 19 `A_non_medevac_scout…` | pass trivially on base (they assert **no** drop); they guard the fix once it exists |
| 9, 11, 12 | test the new pure API directly → red-before by compile |

## Verification (measured)

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | 0 warnings / 0 errors |
| `tools/build-tools.sh` | **all 70** tool projects built |
| New unit/live tests 3× alone (20 tests per run: 12 unit + 7 gate + 1 live) | **20/20 pass, 3/3 runs** (~4 s each) |
| P560 pod-home + P560 warp-leg + P539 pod lifecycle (`DockedPod_Buys…`, `PodLife_Ack…`, `NewLife_Clears…`) + P699 pod-home live, 3× alone | **12/12 pass, 3/3 runs** (1 m 36 s each) |
| P780 model + live (`FullyQualifiedName~P780PodRescueProbe`), 3× alone | **13/13 pass, 3/3** (~0.5 s each) |
| Full Fast tier (`tools/run-tests.sh --tier fast`, one run) | **PASS — 9965 tests, 284.0 s** (Lobby 24, MonoGame 3213+3 not executed, ClientCore 1650, Core 5063, quarantine 2+10) |
| `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` 5× | **5/5 pass** — 1 m 51 s, 1 m 51 s, 1 m 51 s, 1 m 56 s, 2 m 05 s. Historical base rate is likewise 5/5 (e.g. HUD-14/HUD-21 reports: 5/5, ~1 m 50 s); no regression |
| Flakes | none observed; nothing added to `docs/TEST_FLAKES.md` (3× isolation rule satisfied) |

The live test is robust to the bot relaunching immediately after the dock: it watches the captured
server log for `rescued by rescue probe`, not the transitory `ship.StationId`.

## Rig evidence recipe (Claude, after deploy)

```bash
# 1) probe rescues actually happen (> 0)
docker compose logs --since 30m server | grep -c "rescued by rescue probe"
# 2) the medevac scout exists on side 0 (Iron Coalition) and never on side 1 (Ga'Taraan)
docker compose logs --since 30m server | grep -c "\[p779b\] medevac"          # > 0
docker compose logs --since 30m server | grep "\[p779b\] medevac" | grep -c "side=1"   # = 0
# 3) per-match oxygen deaths vs rescues, compared with batch 19 (933 oxygen deaths vs 216 rescues, 0 probe rescues)
docker compose logs --since 3h server | grep -c "\[oxygen\]"
docker compose logs --since 3h server | grep -c "\[rescue\]"
docker compose logs --since 3h server | grep -c "\[p779b\] pod bot .* -> rescue probe"
```

Expected: probe rescues > 0; `[p779b] medevac` present on side 0 and absent on side 1; pod probe-target
lines present; a drop in the oxygen-death/rescue ratio versus batch 19. If the rig's research plan does
not start Iron Coalition with the rescue bit in its techs, `PickRescueProbeLauncher` returns null and the
side logs the one-shot "no medevac scout" note — that is the designed honest outcome, not a silent skip.

## Out of scope / still open

* No server/sim/wire changes; P780's follow-ups (probe bounce and possible double rescue) stay open.
* Salvage/loot (the other half of P779b), rescue drones, rescue-by-ships (P03) and the oxygen timer were
  not touched.
* Research plans that buy rescue tech (no civ can research it anyway) were not added.
* The scouting probe choice, its spacing rules, and any civ-name special case were not changed.
