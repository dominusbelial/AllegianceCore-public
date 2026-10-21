# P772 — UNPINNED ROWS REPORT (SIM-0331, SIM-0338, SIM-0231)

Base: master `5f4c2f82`, branch `pi/p772`, worktree `~/allegiance-pi-p772`. SIM-0195 dropped per brief.
No commits made — all changes left in the working tree.

---

## Row 1 — SIM-0331 (starmap left-click `mouseclickSound`) — matrix only + two pins

**Native cite:** `WinTrek/sectormap.cpp:1520-1524` — on a left-button press (`0 == button`) with
`pClusterFound != NULL`, `trekClient.PlaySoundEffect(mouseclickSound)` plays **first**, then
`SelectCluster` and the ripcord / teleport-pane / view-cluster / goto branches (`:1526-1545`).
No cluster → no sound (the cue is inside `if (pClusterFound)`, `:1522`).

**Production site:** `HandleStarmapClick` → `ExecuteStarmapClick`
(`src/Allegiance.ClientCore.Game/Game1.StarmapClick.cs:150-160`) → `RunStarmapClick(cluster, mark)`
(`:206`). `RunStarmapClick` returns early for `cluster < 0` (no sound), then
`if (!mark) UiSounds?.PlayMapClick();` (`:230-231`) **before** the `StarmapClick.Decide` switch.
`UiSounds` is `_uiSoundOverride ?? _sound` (`Game1.cs:538-543`); the production sink is
`SoundManager.PlayMapClick` (`Audio/SoundManager.cs:1324`). P764 already pinned the call site.

**What changed:** nothing in production — the row was partial only for lack of a pin. Two tests
added; both **pass on today's code** (I do not claim they failed before — there was no fix to fail):

- `P764UiSoundSeamTests.LeftClickCluster_PlaysTheCue_OnAViewClusterBranch` — docked ship,
  teleport pane closed, `RunStarmapClick(2, false)` → `Decide` = ViewCluster (a branch WITH an
  action); asserts `sink.Calls == ["mouseclick"]` and `_selectedSectorId == 2`.
- `P764UiSoundSeamTests.NoClusterHit_PlaysNoCue_AtAll` — `RunStarmapClick((short)-1, false)` →
  `sink.Calls` empty (native `if (pClusterFound)`).

**Matrix:** `port_status` → `implemented`; port_cite now points at `RunStarmapClick`; evidence
lists the P764 test plus the two new names; notes appended with the P772 pinning remark.

---

## Row 2 — SIM-0338 (teleport pane board-a-ship `personalJumpSound`) — seam routing + pin

**Native cite:** `WinTrek/teleportpane.cpp:296-320` (`OnButtonTeleport`). Early return on no
destination / no mission / ship in a cluster (`:301-303`). OT_ship destination:
`DisembarkAndBoard`/`BoardShip` (`:311-319`), **then** `trekClient.PlaySoundEffect(personalJumpSound)`
(`:319-320`). The station branch (`:322-352`) plays **no** sound. Wave `jumpin`
(`sounddef.mdl:274`).

**Production site:** `ActivateTeleport`
(`src/Allegiance.ClientCore.Game/Game1.Panes.cs:971-1001`), called from the docked key handler
(`:177`) and the touch handler (`:693`). The `BoardShip` case played
`_sound?.PlayPersonalJump()` directly — bypassing the P764 `IUiSoundSink` seam, so no headless
test could observe it.

**What changed:**
1. `IUiSoundSink` gained `void PlayPersonalJump();` (doc line cites `teleportpane.cpp:319-320`,
   wave `jumpin`, `sounddef.mdl:274`). `SoundManager` needed no change (already implements the
   interface; `PlayPersonalJump` at `SoundManager.cs:1166`). The only other implementer,
   `RecordingUiSink` in `P764UiSoundSeamTests.cs`, records `"personaljump"`.
2. `ActivateTeleport`'s `BoardShip` case now calls `UiSounds?.PlayPersonalJump()` **after** the
   `SendBoardShip` call(s) (native order: board `:311-319`, then sound `:319-320`).
   `TeleportToStation` stays silent.

**Tests (both new, in `P764UiSoundSeamTests.cs`; `ActivateTeleport` driven by reflection):**
- `BoardShipActivate_PlaysPersonalJump_ThroughTheSink` — ship 11 docked at
  station 42 on a turret-slot hull (synthetic `DataHullTypeIGC` HullId 447, MaxWeapons 2 /
  MaxFixedWeapons 1); cursor 0 (ship row) → asserts `sink.Calls == ["personaljump"]`,
  `_teleportActive` false, `_launchSent` true.
  **Failed on the unfixed code** (1 run): `Expected: ["personaljump"] / Actual: []` — the call
  sat on the null-in-headless `_sound`, the sink recorded nothing.
- `TeleportToStationActivate_PlaysNoCue` — `MoveCursor(+1)` to the station row → `sink.Calls`
  empty (native station branch is silent). **Passes before and after.**

**Matrix:** `port_status` → `implemented`; port_cite → `ActivateTeleport BoardShip branch ->
UiSounds.PlayPersonalJump (after SendBoardShip)`; evidence keeps the TeleportPaneModelTests
model-level trigger and adds the two new names; notes appended.

---

## Row 3 — SIM-0231 (`IIgcSite::GetCluster`) — first production caller routed through the site

**Native cite:** `Igc/igc.h:4909-4913` — default `GetCluster(pship, pmodel)` =
`pship->CanSee(pmodel) ? pmodel->GetCluster() : NULL`. Production caller `CshipIGC::FindRipcordModel`
(`Igc/shipIGC.cpp:3718`): goal pick at `:3775-3790` uses `pmodelGoal` (`m_commandTargets[c_cmdCurrent]`,
falling back to `c_cmdAccepted`) **only if** `pmodelGoal->SeenBySide(pside) && pigc->GetCluster(this,
pmodelGoal) == pcluster` (`:3784-3785`).

**Production site:** `Server.FindRipcordModel` (`src/Allegiance.Server/Server.cs:1100`) built the
friendly-probe nearness goal at `:1112-1124` from `ship.CommandTarget ?? ship.AcceptedShipTarget`
with a raw `switch` on `Probe`/`Ship` sector and no visibility check — an enemy command target the
side had never seen still steered which friendly probe the player rips to.

**What changed (`Server.cs:1112-1126` only):** the `switch` is replaced by

```csharp
if (goalModel is not null &&
    _host.GetCluster(ship, goalModel) is Sector goalSector && goalSector.Id == target.Id)
    goal = goalModel.Position;
```

commented with `shipIGC.cpp:3784-3785`. Native also conjuncts `SeenBySide(pside)`;
`ServerHost.GetCluster`'s `CanSee` (own side OR `SeenBySide` OR in scanner range,
`ServerHost.cs:1820`) is a superset, and the P132 sweep marks own-side models seen
(`Sector.cs:4618-4621`) and in-range models seen — but an `EquipmentTarget` wrapper's own
`SideVisibility` is never swept (`EquipmentTarget.CanSee` reads the body), so a raw
`goalModel.SeenBySide(side)` would wrongly reject station goals; the extra conjunct is not added.
Bonus over the old switch: goal models of type `EquipmentTarget`/`Mine`/`Missile`/`Treasure` now
resolve their cluster through the site's `ClusterOf`. Core navigation legs
(`Ship.cs` `CrossClusterWarpGoal`/`TryRipcordOrDefer`, `FindPath`, GotoPlan, plan ripcord) are
**not** routed — Core tests run on `NullIgcSite`, whose `GetCluster` returns null.

**Tests (new, in `tests/Allegiance.Core.Tests/LiveTeleportProbeRipcordTests.cs`, reusing
`Setup`/`DeployViaWire`/`MountProbeLauncher`/`MoveRipperToWait`/`PumpPastActivationDelay`):**
- `Unseen_enemy_command_target_does_not_seed_the_probe_goal` — two teleport probes (p1 older at
  `(-3000,0,0)`, p2 newer at `(3000,0,0)`), enemy command target at `(-2900,0,0)`, then — with no
  tick after `PumpPastActivationDelay` — `enemy.SideVisibility.SetSideVisibility(0, false)` and
  `FindRipcordModel` invoked by reflection. Asserts the pick is `RipcordTarget(Probe, p2.Id,
  1052)` (no goal → most recent).
  **Failed on the unfixed code** (1 run): anchored on p1 (oid 5464) instead of p2 (oid 5465) —
  the raw switch took the unseen enemy's sector as the goal.
- `Seen_enemy_command_target_seeds_the_probe_goal` — same setup, `SetSideVisibility(0, true)` →
  pick is p1 (the goal still works through the site). **Passes before and after** (guards against
  over-blocking).
- Existing `ServerHostIgcSiteDefaultsTests.GetCluster_*` re-run: all pass (see gate numbers).

**Matrix:** `port_status` → `implemented`; port_cite → `ServerHost.cs:1820;
Server.cs:1126` (stale `:1371` cite fixed); `port_site_binding` → concrete override + production
caller routed through the site; evidence keeps the five `ServerHostIgcSiteDefaultsTests` names and
adds the two new `LiveTeleportProbeRipcordTests` names; notes appended.

---

## Measured gate numbers (all measured in this worktree, `.dotnet/dotnet`)

| Gate | Result |
|---|---|
| Whole-solution build (`dotnet build AllegianceCore.slnx`) | **0 warnings / 0 errors** |
| Pre-fix runs (prod changes stashed, tests present) | SIM-0338 test 1 **FAIL** (`["personaljump"]` vs `[]`); SIM-0231 test 1 **FAIL** (p1 5464 vs p2 5465); SIM-0331 pins + SIM-0338 test 2 + SIM-0231 test 2 **PASS** on unfixed code (4-test filter: 3 passed / 1 failed; 2-test filter: 1 passed / 1 failed) |
| AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | clean — no errors, no IL2026/IL3050 warnings |
| `P764UiSoundSeamTests` alone ×3 | 24 passed / 0 failed, ×3 (152/144/138 ms) |
| `TeleportPaneModelTests` alone ×3 | 12 passed / 0 failed, ×3 (71/62/61 ms) |
| `LiveTeleportProbeRipcordTests` alone ×3 | 9 passed / 0 failed, ×3 (652/661/664 ms) |
| `LiveRipcordTests` alone ×3 | 13 passed / 0 failed, ×3 (~1 s each) |
| `ServerHostIgcSiteDefaultsTests` alone ×3 | 7 passed / 0 failed, ×3 (151/147/153 ms) |
| ONE fast-tier run (`tools/run-tests.sh --tier fast`) | **PASS** — 9641 tests, 271.8 s (3055+24+1579+4983+2+10 collected; MonoGame 3 NotExecuted = artwork skips) |
| Showcase `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` ×5 | **5/5 PASS** (~1 m 50 s each) |

Raw fast-tier logs: `~/allegiance-evidence/P772/summary.json` (29 MB tier dir NOT committed;
no task evidence exceeded the 2 MB rule, so `docs/reports/P772_assets/` holds nothing).

## Matrix edit check

`docs/parity/sim-rules-matrix.csv`: exactly 3 lines changed (`git diff --stat`), LF endings kept,
one-off parse: 474 rows, every row exactly 16 fields, three rows now `implemented`.

## Unfinished / notes

- Nothing unfinished from the brief's scope. SIM-0195 untouched (dropped). Core GetCluster legs
  (`common.cpp:1021/1873`, `shipIGC.cpp:4156`) deliberately not routed (NullIgcSite).
- One mid-run correction: the first post-fix `P764UiSoundSeamTests` 3× run failed 1 test due to a
  stale test binary (`--no-build` against the pre-fix `Allegiance.ClientCore.Game.dll`); after
  rebuilding, 3/3 green. Recorded for honesty; not a code flake.
