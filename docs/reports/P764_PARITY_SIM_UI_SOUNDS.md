# P764 — simulation wave: UI click/error sounds, shield activation sound, Reload callback, territory chain test

**Status: complete** for all 12 rows + the owed test. Every flipped row has a
test that pins the **call** at the trigger through a new sound-sink seam
(`Audio.IUiSoundSink`, implemented by `SoundManager`), not only the bundled
wave. Matrix (`docs/parity/sim-rules-matrix.csv`): the 15 rows below went from
`missing`/`partial` to `implemented`.

## The seam

Native plays every UI cue with a direct `trekClient.PlaySoundEffect(...)`
call at the trigger site. The port's triggers live in `Game1`, which headless
tests cannot construct normally — but they can via the P748
`RuntimeHelpers.GetUninitializedObject` discipline, so this wave:

- added **`src/Allegiance.ClientCore.Game/Audio/IUiSoundSink.cs`** — the UI
  one-shot methods (`PlayMouseOver`, `PlayMapClick`, `PlayError`,
  `PlayPositiveClick`, `PlayMountPart`, `PlayVoice(stem)`); `SoundManager`
  implements it (new waves wired: `ef03` = errorSound, sounddef.mdl:272;
  `accept` = positiveButtonClickSound, sounddef.mdl:313 — both were already in
  `Content/Sounds` + `Content.mgcb`, covered by `SoundAssetManifestTests`);
- added **`Game1._uiSoundOverride`** (test-only) + the `UiSounds` resolver;
  every new cue call routes through it, so
  `P764UiSoundSeamTests` can observe the call with a `RecordingUiSink`.

## Rows

| Row | Native site | Port trigger (all cited in the matrix row) | Call-pinning test |
|---|---|---|---|
| SIM-0328 | `sectormap.cpp:1362` | `AttemptTeleportTo` → "You are already in this cluster." + `PlayError` (pure branch order in new `Rendering/StarmapTeleport.cs`) | `TeleportIntoTheDockedCluster_PlaysError_AndPostsTheNativeLine` |
| SIM-0329 | `sectormap.cpp:1367` | same, "You do not have an appropriate station in this cluster." + `PlayError`; the not-docked race branch (`:1359-1361`) stays silent | `TeleportIntoAClusterWithoutAppropriateStation_…`, `TeleportAttemptWhileNotDocked_…` |
| SIM-0330 | `sectormap.cpp:1440-1443` | P752 already played it; the condition moved into the pure `StarmapHover.Move` (returns `HoverCue` = new cluster ≠ selection) and the call is pinned | `StarmapHoverTests` (cue on new cluster, none on repeat/empty/off-map) |
| SIM-0332 | `sectormap.cpp:1583-1587` | the click cue now fires for BOTH buttons (left `:1523`, right = the leader-mark path) before the branch routing | `RightClickClusterMark_PlaysTheClickCue_…`, `LeftClickCluster_PlaysTheSameClickCue_…` |
| SIM-0333 | `teampane.cpp:1173-1177` | `HandleTeamPaneControls`: the gift intent (selected other player + money > 0) cues `PlayPositiveClick` **before** the send | `TeamPaneGift_PlaysPositiveClick_AtTheNativeSite` |
| SIM-0334/0335 | `teamscreen.cpp:2206-2222` | `RequestSideSelectSide`: the side-select row confirm (the port's team screen; its join is `POSITIONREQ`) cues `PlayMapClick` | `SideSelectRowConfirm_PlaysTheTeamRowClickCue_AndSendsTheJoin` |
| SIM-0336 | `teamscreen.cpp:2434-2437` | team-pane player-list selection change cues `PlayMapClick` | `TeamPaneSelectionCycle_PlaysThePlayerListClickCue` |
| SIM-0285 | `WinTrek.cpp:9267-9269` | help toggle (extracted `ToggleHelpOverlay`) cues `PlayPositiveClick` on open AND close | `HelpToggle_PlaysPositiveClick_OnOpenAndClose` |
| SIM-0325 | `load.cpp:2233-2235` | **new feature**: Ctrl+0..9 in the docked shop saves the pending loadout to slot N (`ShopModel.SaveCustomLoadoutToSlot`) and cues `PlayPositiveClick`; posts "Saved \<hull\> loadout N" | `CtrlDigit_SavesTheLoadout_…` + `P764CustomLoadoutSlotTests` |
| SIM-0326 | `load.cpp:2236-2247` | plain 0..9 restores slot N (`ShopModel.RestoreCustomLoadout` — wholesale pending replacement; an empty slot restores an EMPTY pending loadout exactly like native `RefreshLoadout`, load.cpp:1453-1470) and cues `PlayMountPart` unconditionally; posts "Loaded \<hull\> loadout N" | same tests |
| SIM-0337 | `teleportpane.cpp:285` | P757 already recorded `BoardRefusedTurretsFull`; this wave pinned the drain leg end-to-end: event → `Game1.UpdateSoundEvents` → `PlayVoice("vo_sal_noturrets")` | `BoardNackTurretRace_DrainsAsTheNoTurretsVoice` (+ P638's wire tests) |
| SIM-0089 | `shieldIGC.cpp:86-93` | already implemented (P741): the client evaluates the 0.05 crossing on the streamed `ShieldFraction` (native clients do the same on the server-fed fraction) and plays `shieldup`; flipped after verifying the tests pin crossing AND no-repeat | `ClientWorldSoundEventTests.cs:534-617` |
| SIM-0233 | `igc.h:4923`; callers `shipIGC.h:610, 640` | `Ship.SiteReload` now routes the weapon-fire/fuel zero-transition blocks through `IIgcSite.Reload`; **`ServerHost.Reload` implements the real cargo pack drain** (the P361/P412 machinery — the port of `BaseClient::Reload`, clintlib.cpp:3438-3556, the only native implementation with real logic; FedSrv inherits the `false` default) and answers success. Magazine/dispenser/chaff keep the default `false` (no port depletion caller). A default site (NullIgcSite) falls back to the direct attempt so headless auto-reload is unchanged | `P764ReloadSiteTests` (drain+true, packless+false, default-site fallback, launcher types); `ServerHostIgcSiteDefaultsTests.Reload_returns_false` still passes |
| SIM-0107 (owed test) | `sideigc.cpp:325-347` | new `P764TerritoryChainTests` on a synthetic `MissionMap.FromLists` map: own station in A; ring A–B–C–D–A → B (1 hop, all neighbours territory), C (2 hops), D (3 hops, depth-limit boundary) all territory; pure line A–E–F–G–H → nothing is territory, H sits 4 hops out. Both structures are evaluated identically by the live recursive rule (the row notes document the native cached-loop order divergence as out of scope; no ring claim is made) | the file's two facts |

### Documented adaptations (not silent)

- **SIM-0325**: native cues only when `SaveCustomLoadoutFile()` (disk) succeeds;
  the port's slot store is in memory (P759), so a save always succeeds and
  always cues.
- **SIM-0334/0335/0336**: the port has no mouse-driven team screen; the
  side-select overlay (team rows) and the team pane's selection cycle are the
  keyboard equivalents, so the cues ride those trigger sites.
- **SIM-0328/0329 reachability**: `HandleDockedScreen` returns `false` in
  command camera (`Game1.Panes.cs:139`), so a docked player in command view with
  the teleport pane open (T) gets real map clicks — the Teleport branch is a
  live path; `AttemptTeleportTo` mirrors native's branch order exactly.
- The custom-loadout digit modifiers (`Ctrl`/`Shift`/`Alt`) are raw key reads
  in `HandleCustomLoadoutKeys`, allow-listed in `P625bActionMapGuardTests`
  (native reads them as raw vk flags too, load.cpp:2228/2236 — they are not
  rebindable commands).

## Out of scope (unchanged)

Ambient/weapon-loop rows (SIM-0345/0347/0348/0411), magazine-depleted rows
(SIM-0239/0267/0445/0452/0453), collision order (SIM-0062/0063), and the
callback rows owned by P760b. P761b's team-pane sort/server list rows were not
touched; the team-pane edit is the sound call only.

## Verification

All commands were run from this worktree; no commit/push.

- Whole-solution build: **0 warnings / 0 errors**.
- AOT publish of `src/Allegiance.Server` (linux-x64, `PublishAot=true`):
  **PASS**, 0 errors, no IL2026/IL3050
  ([log](/home/domingo/allegiance-evidence/P764/logs/aot.log)).
- New/affected tests **3× alone**:
  `P764UiSoundSeamTests` + `P764CustomLoadoutSlotTests` 17/17 ×3,
  `P764ReloadSiteTests` + `P764TerritoryChainTests` +
  `ServerHostIgcSiteDefaultsTests` 13/13 ×3, `StarmapHoverTests` 4/4 ×3,
  `P625bActionMapGuardTests` 1/1 ×3 (after allow-listing the modifiers).
- **ONE fast tier**: `tools/run-tests.sh --tier fast` → **PASS**, 9,358 tests,
  272.0 s ([summary](P764_assets/verification.json), raw log
  [fast-tier-final.log](/home/domingo/allegiance-evidence/P764/logs/fast-tier-final.log)).
  The first fast-tier run FAILED on
  `P625bActionMapGuardTests.NoRawKeyLiteralsOutsideAllowList_InGameInputFiles`
  — a real violation by this wave's raw modifier reads, fixed by the
  documented allow-list above (not a flake); the failing log is kept at
  [fast-tier-first.log](/home/domingo/allegiance-evidence/P764/logs/fast-tier-first.log).
- Showcase never-parks test (`P700ShowcaseLiveTests`) **5× alone** (sim/flight
  could be affected via the reload routing): 5/5, ~1m50s each, no drift.
- Regression suites re-run: `AutoReloadOnEmptyTests` + `CargoBayReloadTests`
  13/13, `ClientWorldSoundEventTests` + `ShipStatusAlarmSoundTests` 107/107,
  starmap/team-pane/shop/sound-asset suites 55/55, `P753SimUpdateTests` 18/18
  with P764.

## Evidence

- Small committed asset:
  [verification.json](P764_assets/verification.json) (fast-tier summary).
- Raw logs: `/home/domingo/allegiance-evidence/P764/logs/` (outside the repo;
  AOT, first fast-tier failure, final fast-tier log).

## Unfinished / notes

- Nothing unfinished from the brief's row list. The magazine/dispenser branch
  of native `Reload` (clintlib.cpp:3557-3650) has no port-side depletion
  caller — `ServerHost.Reload` keeps the native default `false` there, recorded
  in the matrix row and pinned by a test.
- `TestResults/tiers-…/` from the tier runs remain in the worktree (the
  runner's own output convention, as in previous waves).

## Review fixes (P764b)

Review round on the P764 work (base `pi/p764b` = `pi/p764` + `git merge master`,
which brought P760b/P765/P766; the merge was clean, no conflicts). Each fix has
a test that was verified to **fail before the fix** (checked by temporarily
reverting the production change, then restoring it):

1. **Reload fallback no longer rides the debounce** (`Ship.SiteReload`).
   The pre-fix code fell back to the direct `TryAutoReloadAmmo`/
   `TryAutoReloadFuel` attempt whenever the site answered `false`; with
   `ServerHost.Reload` that second attempt was suppressed only by the freshly
   re-armed `_timeReloadAmmo`/`_timeReloadFuel` deadline. Now the deadlines are
   snapshotted before `site.Reload` and the fallback fires only when the
   deadline did **not** move (the site made no attempt — native arms the
   deadline inside the zero-transition block *before* the ask,
   `shipIGC.h:608/638`, so a moved deadline is exactly "the site attempted").
   A DEFAULT site (igc.h:4923 `false`) keeps the fallback unchanged.
   Tests (`P764ReloadSiteTests`): `SiteReload_WithoutAPack_RaisesExactlyOneDepletedTrigger`
   (exactly one `AutoReloadTriggered(success:false)` with `now` past the
   debounce — two before the fix), `SiteReload_WithAPack_RaisesExactlyOneSuccessTrigger`,
   `SiteReload_WithADefaultSite_StillFallsBackToTheDirectAttempt`.

2. **SIM-0332 corrected** (`Game1.StarmapClick.cs`). The port's `mark` path is
   the Ctrl+right-click highlight (native `sectormap.cpp:1491-1510`), which
   plays **no** sound; native's mouseclickSound on a right-button hit
   (`sectormap.cpp:1583-1587`) belongs to the plain right-click console
   `PickCluster`, which the port does not implement. `RunStarmapClick` now cues
   only `if (!mark)`, and the inverted test
   `RightClickClusterMark_PlaysNoCue_TheConsolePickClusterIsNotPorted` pins the
   silence. The left-click cue (`sectormap.cpp:1523`) is unchanged. Matrix row
   set to **missing** with the note.

3. **Restoring a never-saved custom-loadout slot no longer wipes the loadout**
   (`ShopModel.RestoreCustomLoadout`). Native `RefreshLoadout` hands the
   (absent) cached list to `ReplaceLoadout`, which **buys the hull's default
   loadout** when there is no cached loadout link for that hull
   (`clintlib.h:1796-1800`); only a slot the player saved EMPTY restores empty.
   The port falls back to `DefaultLoadoutFor(eff)`; the doc comment now cites
   `clintlib.h:1796-1800` instead of the misleading `load.cpp:1453-1470`.
   Tests: `SaveRestoreRoundTrip_PerHullAndSlot_NeverSavedSlotRestoresTheDefault`
   (renamed; asserts the default via `ApplyDefaultLoadout` on a fresh model,
   plus the saved-empty-stays-empty companion) and the `CtrlDigit_…` seam test,
   which now restores an untouched slot (5) after saving (empty) slot 3.

4. **`AttemptTeleportTo` matches native** (`Game1.StarmapClick.cs`):
   - `FindFriendlyStation` now filters to own-side stations whose type
     `HasCapability(c_sabmRestart)` (native `sectormap.cpp:872-891`, check at
     `:881`; the same rule `TeleportPaneModel.cs:123` already applies) — a
     station type the client cannot resolve is not appropriate, matching the
     pane's keep-only-known-restart-types rule.
   - The turret branch (native `sectormap.cpp:1369-1374`): when
     `own.ParentId != -1` the port sends `SendBoardShip(Na)` before
     `SendDocked`, exactly like `ActivateTeleport`'s DisembarkAndTeleport leg
     (`Game1.Panes.cs`).
   Tests: `TeleportIntoAClusterWhoseStationLacksRestart_IsNotAnAppropriateStation`
   (error branch) and `TurretTeleportWireTests.TeleportFromATurret_DisembarksFirst_ThenSendsDocked`
   (in-process loopback session; asserts BOARD_SHIP(NA) precedes
   DOCKED(43) on the wire).

5. **SIM-0107's depth rule is actually pinned** (`P764TerritoryChainTests`).
   The two original cases pass even with the `depth + 1` / `depth >= 3`
   threading removed. New test `FiveClusterRing_TheDepthLimitBailsBeforeTheLoopCloses_NotTerritory`:
   a 5-cluster ring A–J–K–L–M–A whose 1-hop member J must be **non**-territory
   (its branch dives J(0)→K(1)→L(2)→M(3) and bails at native
   `sideigc.cpp:327`'s `currentDepth < 3`; unlimited depth would ride the ring
   back to A and crown J territory). Verified: the test FAILS with the guard
   removed while the two original cases still pass; guard restored. The wrong
   "D (3 hops, depth limit boundary)" comment is fixed (D is depth 2 in B's
   recursion; A's station makes its branch true before any depth check).

6. **SIM-0330's call site is now pinned** (`Game1.StarmapClick.cs`). The
   post-hit-test hover half was split into `RunStarmapHover(inside, hit,
   chatCluster)` so a headless test drives the actual
   `UiSounds?.PlayMouseOver()` trigger, not only `StarmapHover.Move`'s pure
   verdict: `StarmapHoverCue_PlaysMouseOver_AtTheCallSite` (cue on a new
   cluster, silence on repeat and off-map).

7. **Minor**: `Game1.Audio.cs` `UpdateSoundEvents` used `var snd = _sound!`,
   throwing when only the test sink override was installed and a non-sink
   event (e.g. STATION_DESTROYED) was queued. Now `var snd = _sound;` with
   per-case `snd?.` — silent no-ops outside production. Test:
   `SoundEventDrain_WithOnlyTheSinkOverride_NonSinkEventsDoNotThrow`.

### Matrix

- SIM-0332 → **missing** (plain right-click console `PickCluster` is not
  ported; the mark path is silent per `sectormap.cpp:1491-1510`).
- SIM-0107, SIM-0233, SIM-0326, SIM-0328, SIM-0329 stay **implemented** (fixes
  5, 1, 3, 4 landed); notes updated.
- SIM-0330 stays **implemented** with fix 6's call-site pin; notes updated.
- CSV re-validated: every row parses to exactly 16 columns
  (`python3 csv.reader` → `{16}`).

### Verification (P764b)

- Whole-solution build: **0 warnings / 0 errors** (after the clean master merge).
- AOT publish `src/Allegiance.Server` (linux-x64, `PublishAot=true`): **PASS**,
  0 IL warnings/errors.
- Fail-first checks: every fix above was verified against the pre-fix code
  (temporary revert → test fails → restore), including the brief-mandated
  depth-guard removal for fix 5.
- New/affected tests 3× alone: `P764ReloadSiteTests` +
  `P764TerritoryChainTests` + P361/P412 auto-reload + `FuelPoolAndAfterburnerTests`
  + `CargoBayReloadTests` + `ServerHostIgcSiteDefaultsTests` 37/37 ×3;
  `P764UiSoundSeamTests` + `P764CustomLoadoutSlotTests` +
  `TurretTeleportWireTests` + `StarmapHoverTests` 26/26 ×3;
  `SoundAssetManifestTests` 1/1 ×3.
- ONE fast tier: `tools/run-tests.sh --tier fast` → 9,393 tests, 271.8 s, with
  a single failure: `Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events`
  (expected 0 allocated bytes, observed 7,608) — the documented load flake
  (`docs/TEST_FLAKES.md`, observed 7,720 there); it passes **3/3 alone** and
  P764b touches nothing in that path, so the run is a PASS modulo flake.
- Showcase never-parks test (`P700ShowcaseLiveTests`) **5× alone** (the reload
  routing touches the sim): 5/5 at the base rate (~1m41–1m51 s each).
- Summary: [p764b-verification.json](P764_assets/p764b-verification.json);
  raw fast-tier log: `/home/domingo/allegiance-evidence/P764b/logs/fast-tier.log`.
