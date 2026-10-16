# P666 — simulation merge resolution

2026-10-14. File edits only; the merge index is intentionally left for Claude to
stage and commit. No Git command that writes was run.

## Conflict resolutions

All 19 conflict blocks in the eight files named by `P666_BRIEF.md` are resolved.

| File | Resolution |
| --- | --- |
| `docs/TEST_FLAKES.md` | Retained the complete P653c/P665 entries and the complete P651/P629 entries, in addition to the existing historical ledger. |
| `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` | Kept P660's single `UpdateMissionCountdownSound` call, which also runs before the flight-state dispatch, and added P650's separate ripcord countdown with simulation scale and native −7 dB gain. Removed the obsolete duplicate inline mission-countdown block. Existing flight-parts commands and P629 warning/audio updates remain. |
| `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb` | Kept `receivedobjective.ogg`, `boundsalert.ogg`, `vo_sal_boundryexceeded.ogg`, and `vo_sal_sectorcrowded.ogg`. Every one of the 424 build entries exists and no entry is duplicated. |
| `src/Allegiance.Core/Sim/Ship.cs` | Combined all nine blocks: mount changes retain `FreeSpawn` and clear readiness, shields discharge and afterburners deactivate on remount; cargo-launcher RELOAD keeps provenance but starts at zero readiness; reload reset notifications use the batched loadout seam; magazine lock honors float mounting, strict load deadline and old-lock firing order; shields/cloaks use actual float readiness; afterburners retain sub-byte mounting progress. Removed the silently merged second mounting ramp so each flight tick advances progress once. Kept P653 mount/hardpoint, quantity, docked and refund checks, P650 constants and update order, and P629 penalties. |
| `src/Allegiance.Core/Sim/Weapon.cs` | Kept native float readiness before activation/firing, including clearing shot/burst flags while unready. Mount moves still deactivate through the existing ship primitives. |
| `src/Allegiance.Server/Server.Lifecycle.cs` | Assemble with `_simNow` to preserve next-match clocks, then apply configured `CrowdingPenaltyEnabled` to the newly assembled mission. |
| `tests/Allegiance.ClientCore.Tests/MissileFireLiveTests.cs` | Select a hull with real capacity for three decoded missiles, then give the fixture sufficient scanner range/signature to satisfy native visibility gates. Retained real TCP homing/fire and 3→2 ammo assertions. |
| `tests/Allegiance.Core.Tests/CargoBayReloadTests.cs` | Retained H-08 legal quantities and ammo-drain setup. Assert the exact zero-readiness edge in the observer's authoritative loadout receipt; allow the simulation to continue mounting during asynchronous TCP delivery. Synchronous reset, subsequent ramp, docked and parent gates remain covered in P641 tests. |

## Native interaction decisions

Native citations below are relative to the local Allegiance `src/` checkout;
the cited source was inspected directly.

- `Igc/weaponIGC.h:33–48`, `Igc/magazineigc.cpp:85–123`, and
  `Igc/shieldIGC.cpp:65–90` use a float mounting accumulator and require 1.0.
  One shared mounted-part float now owns readiness. It advances once per flight
  tick; docked/boarded ships and cargo do not advance (`Igc/shipIGC.cpp:266–271`).
  The afterburner advances only in its fuel-consuming pass
  (`Igc/afterburnerIGC.cpp:103–125`), and quantity replacements preserve its float.
- `_Utility/Utility.h:1311–1331` defines `BytePercentage` as **round(float × 240)**.
  `MountedPart.FractionMounted` now implements that native view of the float,
  rather than dividing by 255 while other merged consumers divided by 240.
  Readiness checks use the float even when the rounded byte has reached 240.
  Legacy internal callers passing 255 normalize to fully mounted. The redundant
  255→240 adapter in `Server.Combat.cs` is removed; ACKs, loadout changes and
  relaunches share the same native serialization. Repair checks actual float
  readiness, including when the byte has already rounded to full.
- Every mount move resets readiness (`Igc/shipIGC.cpp:1567`), including replacing
  an empty launcher from cargo (`FedSrv/FedSrv.CPP:6607–6616`). Launcher loading
  starts at `lastUpdate + 1/MountRate + LoadTime`
  (`Igc/magazineigc.h:71–78,138–149`). Loaded zero MountRate is preserved.
  Ammo and launcher reloads retain their native resets
  (`FedSrv/FedSrv.CPP:6574–6581,6637–6638`). Free spawn provenance survives moves,
  reloads and quantity edits; marking spawn inventory and PROMOTE now also retain
  the float mounting fraction and magazine deadline
  (`Igc/shipIGC.cpp:3519–3532`).
- Native magazine updates require strict `TimeLoaded < now`, launch with the old
  lock, and permit partial/zero lock (`Igc/magazineigc.cpp:103–123`). The separate
  player-request path retains P651's half-second early-dud rule
  (`FedSrv/FedSrv.CPP:5422–5434,5468,5507–5510`), including requests during the
  mounting ramp: native's wire handler checks amount and loading time, while
  local/held fire checks mounting. Removed the merged unconditional mount gate
  that incorrectly blocked early requests. H-08 capacity and visibility
  remain enforced. P651's synthetic fixtures now provide ten-round magazine
  capacity and actual weapon hardpoints, preserving their original firing,
  depletion, timing and lock assertions under P653 validation
  (`Igc/hullTypeIGC.h:163–213`; `Igc/launchertypeigc.h:130–145`).
- Reload/readiness notifications use `RaiseLoadoutChange`, preserving P653's
  outer transaction batching (`FedSrv/fsship.cpp:694–735`) and P660b's command
  suppression: SWAP_PART/PROMOTE keep their native echoes without redundant
  LOADOUT_CHANGE publication (`FedSrv/FedSrv.CPP:6497–6498`).
- P629's loaded crowding slots and lens-adjusted OOB damage remain in place
  (`Igc/clusterIGC.cpp:163–197`; `Igc/shipIGC.cpp:368–408`), along with warning
  wire feedback and the included audio. P650's core constants, He3 regeneration,
  weapon energy order and simulation-scaled ripcord audio are retained. Mission
  countdown uses the P660 stage/time handler once; ripcord uses its own native
  change detector (`WinTrek/trekigc.cpp:1016–1075`; `sounddef.mdl:353–362`).

## Additional interactions exposed by the full suites

The initial unfiltered Core pass reported **4270 passed / 3 failed**, and
ClientCore reported **908 passed / 4 failed / 1 existing opt-in skip**. The
diagnostic MonoGame pass completed with **2125 passed / 0 failed / 3 existing
optional-pack skips**. These
failures were investigated and corrected; they are not called load flakes:

| Case | Correction |
| --- | --- |
| `P641BoosterPredictionTests.Latched_boost_release_and_removal_match_authoritative_flight` (2 rows) | `OwnShipSimulator` now advances afterburner power before movement and again in the fuel pass, matching P650/native `Igc/shipIGC.cpp:2945–2957,358–359`. Mounting stays a float, advances only once, and honors zero/small loaded rates. Own-ship wiring applies the side thrust GA, while fuel uses raw part thrust (`Igc/afterburnerIGC.h:113–116`; `Igc/afterburnerIGC.cpp:150`). Retained server/client trajectory comparisons through activation, release and removal, adding native 239/240 and sub-byte-step coverage. |
| `P651EquipmentLiveTests.Static_target_order_lock_and_not_ready_state_roundtrip_over_TCP` (station/warp) | Restore native early-player-fire behavior described above; give both live P651 fixtures legal ten-round magazine capacity. Retain real dud creation, static-target lock, readiness, and production bot kill assertions. |
| `LiveWinConditionTests.Board_refused_once_mission_over` | The spawner assigned carrier/boarder by logon drain order, but the test sent from a named connection that could actually own the carrier. Resolve the boarder from its assigned ship in both control and refusal legs. |
| `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another` | Resolve mission-A/B peers from actual server placements and wait for the asserted GAME_OVER_PLAYERS receipts in the existing condition/deadline. Stage completion alone precedes asynchronous wire delivery. |
| `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` | Resolve mission peers from actual placements and require both mission-A peers' GAME_STATE/GAME_OVER receipts. Retain the empty mission-B assertions and existing deadline. |

The last three cases each pass **3/3 exact-method isolated runs** after all
diagnostic suites finished and the final normal build completed. Their existing
`Lane=LiveTiming` traits remain. No new quarantine or skip was introduced. Predictor verification additionally covers
zero/small mount rates and equal fuel cadence with different thrust GA bonuses.
The affected predictor/live-missile client group passes **13/13**.

## Regression verification

The focused Core integration group passes **90/90**, with no skips. It includes
P641 mounting/reset gates, all P653 audit cases, P650 simulation rules, P651
rules, and live cargo reload tests. Four additional Core P666 cases cover:

1. Free spawn marking, quantity clamping and promotion preserve float progress
   and the magazine deadline.
2. Cargo-launcher RELOAD resets readiness and keeps free provenance, then waits
   through the mount/load delay before firing.
3. Afterburner quantity changes preserve sub-byte progress until truly ready.
4. Local missile fire waits for mounting; an early player request consumes a dud
   even during the mounting ramp, matching native's separate server path.

P650's afterburner and repair fixtures now assert native full-mount byte 240,
with an explicit float-1.0 assertion for the afterburner. P641's sub-byte reset
fixture uses intervals that exercise native rounding while still exposing a
stale accumulator. P651 also checks that a gun whose wire byte rounds to full
cannot fire before its float reaches 1. No quarantine traits or deadline
inflation were added.

`NUGET_HTTP_CACHE_PATH=/tmp/p666-nuget-http dotnet restore AllegianceCore.slnx`
succeeds with package auditing enabled. The cache override accommodates the
sandbox's read-only home cache; no project configuration was changed for it.

`dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings / 0 errors**.

The final assemblies run sequentially with `dotnet test tests/<assembly>
--no-build --logger trx --results-directory /tmp/p666-results`, without test
filters. ClientCore additionally uses `ALLEGIANCE_LIVE_P660_SHOWCASE=1` so the
normally opt-in 15v15 next-match proof executes. It **passes** in 21.77 seconds:
30 real MCP bots and both human sessions remain connected through rotation,
all 30 bots reset to the next mission, and both finale sides rearm. No ClientCore
case is skipped in the final run.

## Final unfiltered suites

| Assembly | Passed | Failed in full run | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Core | 4271 | 3 | 0 | 4274 |
| ClientCore | 918 | 1 | 0 | 919 |
| ClientCore.MonoGame | 2125 | 0 | 3 | 2128 |

**Result: green under the required 3/3-alone rule.** All four full-run failures
passed all three exact-method isolated retries after the three final suites
finished. There are no remaining reproducible failures. These unchanged tests
were not weakened or newly quarantined:

| Full-run failure | Observed symptom | Exact-method isolation |
| --- | --- | --- |
| `Core: LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Observer had received the mount snapshot; the owner's asynchronous receipt collection was still empty. | 3/3 pass |
| `Core: Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking` | Flood suppression completed before the healthy bot's first heavy-update receipt. | 3/3 pass |
| `Core: Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Raw peer's `OnPeerJoined` did not arrive within its existing 5-second wait. | 3/3 pass |
| `ClientCore: CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` | Initial warp-out setup did not observe Bravo in another sector; existing `Lane=LiveTiming` case. | 3/3 pass |

The three MonoGame skips are unchanged `MeshyPackTests` cases for the unavailable
optional private built pack: `Pack_built_content_directory_exists`,
`Pack_catalogue_merge_yields_identical_resolution_to_before`, and
`Pack_catalogue_has_the_full_meshy_set`. Artwork/native-source and live TCP tests
ran; no filters excluded either corpus or the existing LiveTiming lane.

All source/test fixes preceded the final normal build and final suite runs.
Only documentation/evidence was edited afterward. The earlier candidate build
used `--artifacts-path bin/p666-candidate` inside this checkout, preserving valid
repository/asset discovery while the diagnostic binaries finished running.
Final suite and isolation verification use the normal output directories.

Durable counters, failures, exact retry outcomes, fixture verification and
commands are in [P666_RESULTS.json](P666_RESULTS.json). Raw full-suite TRX files:
`/tmp/p666-results/final-Core.trx`, `final-ClientCore.trx`, and
`final-ClientCore.MonoGame.trx`. Retry files use `final-retry-<case>-<attempt>.trx`;
corrected fixture proofs use `fixture-<case>-<attempt>.trx` in that same directory.
The readable ledgers and build logs are also there.

Final `grep -rn '^<<<<<<< \|^>>>>>>> ' src tests docs` finds no matches
(expected exit 1). No exact conflict separator lines remain. `git diff --check`
is clean. Claude can stage the resolved files and record the merge; this task
made no Git metadata changes.
