# P720 — commands interface parity wave 3

Implemented the 14 native equipment-swap commands (weight **28**) through the shared flight input, cargo-selection model, and existing guaranteed SWAP_PART/RELOAD path. Base: `bae444b0`, branch `codex/p720`. The interrupted implementation is preserved in dispatcher WIP `52651ffd`. This continuation leaves its changes uncommitted in the working tree; no commits or pushes were made by this run.

## Scorecard before → after

The actual branch baseline was 88.39%, rather than the brief's approximate 88.8%. Both scorecard runs validate all 438 interface rows and 473 simulation rows. Only the 14 selected command statuses changed; weights and eligibility are unchanged.

| Area | Weighted before → after | Completed/eligible before → after |
| --- | --- | --- |
| commands | **533/591 (90.19%) → 561/591 (94.92%)** | **162/190 → 176/190** |
| commander | 81/81 (100%) → unchanged | 19/19 → unchanged |
| fedmsg | 443/515 (86.02%) → unchanged | 107/125 → unchanged |
| lobby | 39/51 (76.47%) → unchanged | 9/12 → unchanged |
| parts | 135/153 (88.24%) → unchanged | 34/40 → unchanged |
| screens | 79/91 (86.81%) → unchanged | 19/24 → unchanged |
| **Interface total** | **1310/1482 (88.39%) → 1338/1482 (90.28%)** | **350/410 (85.37%) → 364/410 (88.78%)** |

Simulation score remains 772/1333 (57.91%), 231/437 eligible rows complete. Raw [before](P720_assets/scorecard-before.txt) and [after](P720_assets/scorecard-after.txt) outputs are included. The edited parity CSV has Unix LF endings (zero CR bytes), normalizing its pre-existing mixed endings. Parsed comparison against base `bae444b0` confirms exactly fourteen semantic row changes, limited to status/evidence.

## Initial open commands, descending weight

| Weight | Native item | Initial status | Selected |
| ---: | --- | --- | --- |
| 4 | TK_ViewTurret | partial |  |
| 3 | TK_Throttle33 | partial |  |
| 3 | TK_Throttle66 | partial |  |
| 3 | TK_ConModeInvest | partial |  |
| 2 | TK_ToggleLODSlider | missing |  |
| 2 | TK_LODUp | missing |  |
| 2 | TK_LODDown | missing |  |
| 2 | TK_SwapWeapon1 | missing | yes |
| 2 | TK_SwapWeapon2 | missing | yes |
| 2 | TK_SwapWeapon3 | missing | yes |
| 2 | TK_SwapWeapon4 | missing | yes |
| 2 | TK_SwapTurret1 | missing | yes |
| 2 | TK_SwapTurret2 | missing | yes |
| 2 | TK_SwapTurret3 | missing | yes |
| 2 | TK_SwapTurret4 | missing | yes |
| 2 | TK_SwapMissile | missing | yes |
| 2 | TK_SwapChaff | missing | yes |
| 2 | TK_SwapShield | missing | yes |
| 2 | TK_SwapCloak | missing | yes |
| 2 | TK_SwapAfterburner | missing | yes |
| 2 | TK_SwapMine | missing | yes |
| 2 | TK_PauseTM | missing |  |
| 2 | TK_SkipTMGoal | missing |  |
| 2 | TK_CommandFinishTM | missing |  |
| 2 | TK_RollModifier | missing |  |
| 1 | TK_IncreaseTMClockSpeed | missing |  |
| 1 | TK_DecreaseTMClockSpeed | missing |  |
| 1 | TK_ResetTMClockSpeed | missing |  |

There were 28 open command rows, weight 58. Fourteen remain open, weight 30.

## Why these rows / earlier-wave boundary

These fourteen commands all call native `SwapPart`, and differ only in equipment type and hardpoint selection. Implementing them together gives the actual keyboard/UI path one faithful selection rule, with complete coverage across four fixed weapons, four turrets, and six single-slot equipment types. Throttle, training, LOD, roll-modifier, investment, and camera commands are separate systems and retain their existing status.

Read `P713_INTERFACE_WAVE.md` from `/home/domingo/allegiance-codex-p713/docs/reports/` and `P718_INTERFACE_WAVE.md` from `/home/domingo/allegiance-codex-p718/docs/reports/`; neither report exists in this checkout. P713 covers join/team/ready/wing state; P718 covers eight receive-side FEDMSG rows. Their rows are untouched. P641's generic selected-inventory mount/unmount/replace/drop flow is retained; this bundle implements the fourteen distinct native swap actions that were still missing.

## Native rules and classification

Native C++ citations are relative to **`~/Development/Allegiance/src/`**. The separately shipped native input asset is **`~/Development/Artwork/defaultinputmap.mdl`**; its native loading path is `WinTrek/trekinput.cpp:125-126,415-430`. All fourteen selected rows have **native_intent=design**, recorded in their CSV evidence. All are active gameplay commands; none qualifies for `n/a`. No native bug was copied as an intended gameplay rule.

| Row | Native handler | Target / intent | Implementation and proof |
| --- | --- | --- | --- |
| TK_SwapWeapon1 | `WinTrek/WinTrek.cpp:9535` | fixed hardpoint 0; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapWeapon1`; **missing → done** |
| TK_SwapWeapon2 | `WinTrek/WinTrek.cpp:9541` | fixed hardpoint 1; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapWeapon2`; **missing → done** |
| TK_SwapWeapon3 | `WinTrek/WinTrek.cpp:9547` | fixed hardpoint 2; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapWeapon3`; **missing → done** |
| TK_SwapWeapon4 | `WinTrek/WinTrek.cpp:9553` | fixed hardpoint 3; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapWeapon4`; **missing → done** |
| TK_SwapTurret1 | `WinTrek/WinTrek.cpp:9559` | turret 0 + MaxFixedWeapons; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapTurret1`; **missing → done** |
| TK_SwapTurret2 | `WinTrek/WinTrek.cpp:9565` | turret 1 + MaxFixedWeapons; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapTurret2`; **missing → done** |
| TK_SwapTurret3 | `WinTrek/WinTrek.cpp:9571` | turret 2 + MaxFixedWeapons; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapTurret3`; **missing → done** |
| TK_SwapTurret4 | `WinTrek/WinTrek.cpp:9577` | turret 3 + MaxFixedWeapons; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapTurret4`; **missing → done** |
| TK_SwapMissile | `WinTrek/WinTrek.cpp:9604` | Magazine mount 0; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapMissile`; **missing → done** |
| TK_SwapChaff | `WinTrek/WinTrek.cpp:9610` | ChaffLauncher mount 0; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapChaff`; **missing → done** |
| TK_SwapShield | `WinTrek/WinTrek.cpp:9616` | Shield mount 0; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapShield`; **missing → done** |
| TK_SwapCloak | `WinTrek/WinTrek.cpp:9622` | Cloak mount 0; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapCloak`; **missing → done** |
| TK_SwapAfterburner | `WinTrek/WinTrek.cpp:9628` | Afterburner mount 0; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapAfterburner`; **missing → done** |
| TK_SwapMine | `WinTrek/WinTrek.cpp:9634` | Dispenser mount 0; **design** | `EquipmentSwapInput.Plan`; `P720EquipmentSwapTests` exact-byte TCP case and real-server case `SwapMine`; **missing → done** |

Shared native rules:

- `WinTrek/WinTrek.cpp:9075-9150`: flying and unparented only; fixed index must be below `MaxFixedWeapons`, turret index below `MaxWeapons - MaxFixedWeapons`; use the individual destination socket mask. The production `CanChangeFlightParts` gate also rejects docked/dead ships.
- `WinTrek/trekigc.cpp:2603-2719`: begin at current selected cargo, descend through -1…-5 and wrap; group duplicate packs/magazines/dispensers, and represent empty slots by the first empty mount. Choose different compatible cargo, or an empty slot when equipment is mounted. Full cargo with no different compatible part is a no-op. After a successful swap/reload, advance and select the next eligible cargo item. Native chaff uses `OT_dispenser` too (`Igc/dispenserigc.h:34-37`, `Igc/dispenserigc.cpp:45-47`), so duplicate chaff stacks share the same grouping; the continuation corrects that missed case and adds a three-launcher regression.
- `WinTrek/WinTrek.cpp:9089-9094`: when equipment exists send equipment → cargo; when the equipment slot is empty send cargo → equipment. This direction matters for the reload hook.
- `Inc/Messages.h:273-278`; `clintlib/clintlib.h:2371-2406`: guaranteed SWAP_PART(34), **10 bytes**, equipment short and signed source/destination mounts, with **no tail padding**. `Inc/messagecore.h:43,59,77-87,767-774` declares a four-byte, two-aligned base; the derived fields end at byte 10. An independent [C++ layout probe](P720_assets/native-swap-layout.cpp) [prints size=10, align=2, offsets=4/6/8/9](P720_assets/native-swap-layout.txt). Corrected the pre-existing port-only 12-byte encoder and its old test; this is a **port bug**, while native intent is **design**. The decoder retains its existing acceptance of extra framed bytes; older port receivers that require twelve bytes need upgrading before receiving the corrected ten-byte frames. Server fills the authoritative ship ID and relays sector-wide (`FedSrv/FedSrv.CPP:6459-6501`). Existing server handling is reused with the corrected shared codec.
- `clintlib/clintlib.h:2401-2406`; `clintlib/clintlib.cpp:3425-3678`: mounting into empty launcher/afterburner equipment follows SWAP_PART with RELOAD, scanning matching cargo from the last slot, respecting capacity and partial transfers. Replacement/unmount direction does not invoke that hook. Launcher capacity uses the existing native hull-capacity/expendable-size calculation (`Igc/launchertypeigc.h:130-145`).
- `clintlib/appmsg.cpp:1430-1566`: swap and reload receive paths mutate the named inventory in message order. The port applies the authoritative own echo once, retaining P641's established server-confirmed inventory convention. A temporary copy computes next selection without double-swapping ClientWorld. Reload echoes now consume cargo, update launcher amounts/readiness, or mount orphan launchers; pose/pool fractions continue to come from server updates. The existing successful-reload counter is retained.
- `WinTrek/trekinput.cpp:272-289,1506-1523` registers all fourteen commands and their help names. Native `~/Development/Artwork/defaultinputmap.mdl:62-71` assigns Ctrl+1…4 and Ctrl+5…0 to fixed weapons/equipment, leaving turrets unbound. These chords already control promotion and mouse settings in the port (`InputActionCatalog.cs:118-121,351-354`), so the new actions are unbound by default, visible/rebindable in F6, and preserve native chord suggestions. This is an explicit port input-design adaptation, not missing command dispatch.

## Implementation and test evidence

- `src/Allegiance.Core/Wire/SwapPart.cs`: native ten-byte frame, with independent golden TCP requests and corrected Core codec regression.
- `src/Allegiance.ClientCore.Game/Rendering/EquipmentSwapInput.cs`: all fourteen identities, input edge handling, priming/suppression, per-slot cargo selection, next selection, and ordered reload planning.
- `src/Allegiance.ClientCore.Game/Rendering/InputActionCatalog.cs:145`: F6/help identities and native suggestions; existing bindings preserved.
- `src/Allegiance.ClientCore.Game/Game1.FlightParts.cs:95`: actual flight handler and 34-before-129 send sequence, launch-entry priming, current hull/inventory resolution, and gameplay guards.
- `src/Allegiance.ClientCore/State/ClientWorld.FlightParts.cs`: ordered authoritative reload inventory updates needed by the next swap selection. `ClientWorld.cs` retains the existing `ReloadCount` signal.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P720EquipmentSwapTests.cs`: action JSON roundtrip and held/suppressed/primed input, selection and full-hold cases, per-turret masks/bounds, consumable grouping and capacity, partial/whole reloads, and receive-order model regression.

Each command has two network proofs: the actual headless `Game1.SwapFlightEquipment` method sends through a real `ConnectionSession`/TCP transport to a byte-capturing loopback host, which checks independent golden request bytes (including CRC), ordered repeated requests, own echo mutation exactly once, and docked/parented/non-flight refusal fenced by a subsequent valid request; a separate real `Server` fixture verifies production command handling and authoritative returned inventory for all fourteen commands. Four additional TCP cases pin empty equipment's SWAP_PART-before-RELOAD ordering and independent native reload entry bytes, with actual receive-side cargo consumption. No graphics device or mock transport is involved.

## Verification

The final whole-solution build passed with **0 warnings / 0 errors** (7.07 seconds). The final affected cohort passed **78 methods × 3 isolated runs = 234 invocations / 642 passing cases**, with **zero failures and zero skips**. The standalone showcase never-parks gate passed **10/10**; no base-rate comparison was needed.

Exactly **two complete, unfiltered suite sweeps** ran serially after that build:

| Sweep | Project | Passed | Failed | Skipped |
| ---: | --- | ---: | ---: | ---: |
| 1 | Core | 4725 | 0 | 0 |
| 1 | ClientCore | 1121 | 0 | 0 |
| 1 | MonoGame | 2297 | 0 | 3 |
| 1 | Lobby | 19 | 0 | 0 |
| 2 | Core | 4723 | 2 | 0 |
| 2 | ClientCore | 1121 | 0 | 0 |
| 2 | MonoGame | 2297 | 0 | 3 |
| 2 | Lobby | 19 | 0 | 0 |
| **Total** | | **16,322** | **2** | **6** |

Both full-suite failures occurred in Core sweep 2. **Each passed 3/3 fresh exact-method isolated runs after all eight suite invocations finished**, with unchanged assertions and deadlines:

| Method | Full-suite observation | Final isolation |
| --- | --- | --- |
| `P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic` | 199/200 pods docked; one oxygen expiry (`Expected: 0`, `Actual: 1`, test line 160). | **3/3 pass** |
| `MapGenLiveProofTests.GeneratedSingleRing_PeerFliesThroughWarp_ToRingNeighbour` | `never crossed: sectors=[0]` (test line 119). | **3/3 pass** |

These meet the brief's explicit **load-flake** rule; the isolated passes do not establish either failure's underlying cause or a fix. Both are recorded in `docs/TEST_FLAKES.md` and the [failure/isolation index](P720_assets/failure-isolation.json). No test assertion, deadline, quarantine, simulation, or transport change was made for these observations. The six skips are the same three unavailable private Meshy-pack checks in each MonoGame sweep. Skips are counted from TRX `UnitTestResult` outcomes because its aggregate `notExecuted` counter reports zero despite those three `NotExecuted` results.

Both sweeps enable `ALLEGIANCE_LIVE_P660_SHOWCASE`, `ALLEGIANCE_LIVE_P542_PROOF`, `ALLEGIANCE_LIVE_CAPTURE_PROOF`, and `ALLEGIANCE_LIVE_P539_BASELINE`. The latter two include two capture windows of up to 300 seconds and two 240-second baseline recordings per sweep; these local live proofs were executed. No rig/221 or remote 200v200 run was performed.

The [final audit](P720_assets/audit.json) verifies **258 raw TRX hashes**, all **1,656 source fingerprints**, **107 test DLL hashes**, exactly two sweeps of all four test projects, all affected-method triplets, all ten standalone parking passes, both failure isolations, and the fourteen semantic CSV changes with **zero CR bytes**. Source and binaries are unchanged since the final build. The [audit script](P720_assets/audit.py), [build metadata](P720_assets/certification-build.json), [build log](P720_assets/build.log), [validation summary](P720_assets/validation-summary.json), [invocation ledger](P720_assets/verification.json), [affected certificate](P720_assets/affected-certificate.json), [affected cohort](P720_assets/affected-methods.json), and [serial runner](P720_assets/verify.py) are included. Full final TRX/log files remain under `.verify-p720/final/`.

The inherited overlapping attempts are excluded from certification and preserved in the [raw-TRX diagnostic inventory](P720_assets/development-verification.json), [inherited ledger](P720_assets/inherited-verification.json), and [inherited certificate](P720_assets/inherited-affected-certificate.json).

Development checks: the initial restore failed because NuGet's HTTP cache was read-only. Setting `NUGET_HTTP_CACHE_PATH=/tmp/p720-nuget-cache` allowed the unchanged package audit/restore to run. Early compile failures were fixture API/nullability mistakes, corrected before certification. The first new-test run failed 17/33 cases: status fixtures omitted hull IDs and three next-selection expectations did not account for empty-slot preference. Corrected the fixtures/expectations against native source and hardened the new handler's missing-hull lookup. The second exploratory new-test run passed 51/51. Its raw logs/TRX are under `/tmp/p720-dev/`; development build logs are `/tmp/p720-build-dev*.log`.

No assertions or deadlines were relaxed. Quarantine settings, transport/broadcast pipeline, simulation rules, economy/research, sounds, minimap/3D renderer/FSR, security, Android, and installer behavior are unchanged. No remote rig/221 or 200v200 run was performed.

The first candidate used the existing 12-byte SWAP_PART codec. A subsequent native struct-layout audit exposed its two unnecessary tail bytes. Its isolated certificate (72 methods, 216 invocations, 612 passes) and five completed passing parking runs are retained as **pre-layout exploratory evidence**, excluded from final certification. A sixth parking run was interrupted to correct the codec before any full-suite sweep began. This interruption is not a failed assertion or a load flake. The final build/cohort includes the corrected ten-byte layout and additional affected swap/receive-path regressions.

Verification-driver correction: interrupting the outer execution session did not stop its child Python driver. That stale driver overlapped the corrected-code triplets/parking and entered a Core sweep before its next-project start was blocked. These attempts are retained as diagnostic evidence and excluded from final certification. The runner now has a per-workspace exclusive lock, an explicit stop file, child process-group shutdown, and atomic ledger replacement. Final certification uses `.verify-p720/final/`, starts from an empty ledger after both earlier drivers have exited, and never shares result filenames with the inherited attempts. It includes all four solution test projects (Core, ClientCore, MonoGame, Lobby) in each of its two sweeps. The suite runner resumes by label, refusing ambiguous or incomplete saved results instead of repeating completed sweeps.

Continuation review: corrected duplicate chaff grouping against the native dispenser object type and added `Selected_duplicate_launcher_stack_normalizes_to_first_group_representative` for missiles, chaff, and mines. The first continuation verifier was stopped before any parking or suite gate (26 completed invocations plus one deliberately terminated invocation; no failed assertion). Its [ledger](P720_assets/pre-chaff-verification.json), [build](P720_assets/pre-chaff-build.log), and [DLL hashes](P720_assets/pre-chaff-certification-build.json) remain separate; raw artifacts are under `.verify-p720/pre-chaff/`. Final certification was restarted after the corrected build and completed as recorded above.
