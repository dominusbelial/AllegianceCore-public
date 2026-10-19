# P740 — interface parity wave 5: FEDMSG and command leftovers

Ten open rows addressed on `codex/p740`: **eight completed, two n/a inventory corrections**. Interface parity moves **94.78% → 97.34%**. Changes remain in the working tree; no commits or pushes.

## Scope and predecessor boundary

Read P713, P718, P720 and P722, including their integration notes. Their completed lobby/wing/mission-entry, FEDMSG, equipment-swap, parts and screen rows are retained. The actual dispatch checkout differs from those reports' original baselines: parts is already complete; the only open lobby/screen entries are the subsequently reopened training rows. Those remain partial and outside this task's single-player boundary.

The selected rows share `ConnectionSession.OnReceive → ClientWorld.Apply`, flight inventory/pose state, and the real MonoGame input/action map. Six active messages complete the remaining inventory, objective, wallet-request, carrier and connection-info receive paths. Two command rows connect existing throttle arithmetic to real input. The two inactive native message definitions have no native gameplay behavior to reproduce.

`WARP_BOMB` remains partial. Its native receive handler calls `IwarpIGC::AddBomb` (`clintlib/appmsg.cpp:849-865`); that implementation adds pulses at both ends and schedules explosion behavior (`Igc/warpIGC.h:54-94,143-164`). It is a separate coherent effect/simulation dependency, rather than a reason to mark an active protocol n/a. `ACQUIRE_TREASURE` remains partial: parts require `HitTreasure` slot selection and a real `TREASURE_ACK` (`clintlib/appmsg.cpp:262-278`; `FedSrv/FedSrv.CPP:6094-6152`). Existing sound-only world handling is preserved. No broadcast pipeline, server simulation, security or single-player implementation was changed.

## Scorecard before → after

`python3 scripts/parity-scorecard.py` validates **438 interface rows and 473 simulation rows**. The two n/a changes remove eight eligible weight units; the eight completions add thirty credit units.

| Area | Weighted before | Weighted after | Complete/eligible before → after |
| --- | --- | --- | --- |
| commander | 81/81 (100.00%) | unchanged | 19/19 → 19/19 |
| commands | 561/591 (94.92%) | **567/591 (95.94%)** | 176/190 → **178/190** |
| fedmsg | 475/515 (92.23%) | **499/507 (98.42%)** | 115/125 → **121/123** |
| lobby | 43/47 (91.49%) | unchanged | 10/11 → 10/11 |
| parts | 153/153 (100.00%) | unchanged | 40/40 → 40/40 |
| screens | 86/89 (96.63%) | unchanged | 22/23 → 22/23 |
| **Interface total** | **1399/1476 (94.78%)** | **1429/1468 (97.34%)** | **382/408 → 390/406** |
| Simulation total | 1024/1289 (79.44%) | byte-identical | 325/421 → 325/421 |

Raw [before](P740_assets/scorecard-before.txt) and [after](P740_assets/scorecard-after.txt) scorecards, and the [initial row inventory](P740_assets/open-before.json), are retained.

## Native rules and implementation

All native file:line citations in this report and the changed CSV evidence are relative to **`~/Development/Allegiance/src/`**, read from the local native source tree. Active behavior is native **design**; the two exclusions are **inactive legacy protocol inventory**, not implementation credit. Existing sound-wave work is reused.

| Row | Result | Native evidence and implemented behavior |
| --- | --- | --- |
| 87 FIRE_EXPENDABLE (weight 4) | partial → done | `Inc/Messages.h:619-622`; `clintlib/appmsg.cpp:1617-1642`: in-game, known launcher's mounted dispenser at mount zero, decrement one; missing ship/launcher is ignored. `ClientWorld.FlightInterface.cs` updates both chaff and mine/probe dispenser inventory; cargo and exhausted launchers are untouched. |
| 156 GAIN_FLAG (4) | partial → done | `Inc/Messages.h:861-866`; `clintlib/appmsg.cpp:3113-3175`: retain/clear the named carrier's flag; stolen-flag/artifact critical text and existing voices; secured-tech text only for our team, without changing its flag. Preserve native's existing `sideidFlag > 0` enemy-announcement predicate. Waiting-for-restart begins on GAME_OVER and ends on Starting/Started (`appmsg.cpp:2326,2353,3051`), independently of dismissing the results UI. `ShipEntity.CarriedFlagSideId`, world feedback and session dispatch implement those paths. |
| 164 ADD_PART (4) | partial → done | `Inc/Messages.h:881-884`; `clintlib/appmsg.cpp:1384-1409`: in-game, known unparented ship and known part; merge only the same part in the named equipment socket/cargo slot, otherwise create it. New mounted fraction is zero (`Igc/shipIGC.cpp:1557-1567`); existing mount readiness survives an amount merge. Unknown types, occupied different-type cargo and invalid cargo mounts are ignored. |
| 182 REQUEST_MONEY (4) | partial → done | `Inc/Messages.h:940-944`; `clintlib/appmsg.cpp:2629-2638`: resolve the sender on our **side**, not merely anywhere in the mission roster. The existing callback's 15-second request, queued-order replacement, non-paying receipt and explicit wallet-clamped approval remain (`WinTrek/WinTrek.cpp:10627-10652,10728-10759`). New receive scope checks feed that P709 implementation; its real-server PLAYER_DONATE proof is rerun. |
| 192 RELAUNCH_SHIP (4) | partial → done | `Inc/Messages.h:988-996`; `clintlib/appmsg.cpp:132-176`: unparent, replace hull/mounted/cargo loadout, restore ammo/fuel/energy, adopt position/velocity/orientation; only self takes the cookie, repaired-at-carrier voice and carrier camera. Native camera duration is three seconds (`WinTrek/WinTrek.cpp:6480-6533`). Existing SHIP_RESET reseeding and remote dead reckoning apply the discontinuous pose immediately. The direct native voice template is reused (sound id 1399). |
| 194 PINGDATA (4) | partial → done | `Inc/Messages.h:1006-1013`; `WinTrek/WinTrek.cpp:11075-11087`: update existing player records; only the `shipID=-1,ping=1` marker opens the report after the data. Native display clamps and integer averages are preserved (`WinTrek.cpp:6007-6135`), including the exact `>1000` and `>100` predicates. `SendPlayerPingsRequest` emits REQPINGDATA, as native's menu does (`WinTrek.cpp:6150-6155`). F6 exposes the unbound PlayerPings action; the real game renders a report pane, pages through every row and closes on the same action. |
| TK_Throttle33 (3) | partial → done | `WinTrek/WinTrek.cpp:10945-10950`: persist axis -0.3. Action catalog, actual `ApplyThrottleKeys`, release of the joystick throttle latch, launch priming and flight controls now connect the already-existing preset arithmetic. |
| TK_Throttle66 (3) | partial → done | `WinTrek/WinTrek.cpp:10951-10956`: persist axis +0.3. Same shared input path; simultaneous presets preserve zero → 33 → 66 → full priority (`WinTrek.cpp:10939-10962`). |
| 31 NEW_SHIP_INSTANCE (4) | partial → n/a | `Inc/Messages.h:262-264` only declares this legacy shape. A scan of native FedSrv/clintlib/WinTrek finds no producer or handler. Active object creation is EXPORT (`FedSrv/FedSrv.CPP:2022-2043`; `clintlib/appmsg.cpp:80-105`). Inventing an additional ship-creation path would not port a native rule. Existing unused codec is retained. |
| 89 TREASURE_SETS (4) | partial → n/a | `Inc/Messages.h:624-628` declares a legacy catalog; native `WinTrek/WinTrek.cpp:11090-11094` explicitly handles it as **NYI**, and native FedSrv has no producer. The port's existing custom catalog producer/codec remains for compatibility; this exclusion does not certify that custom layout as a native wire layout. |

Explicit port adaptations:

- Native FIRE_EXPENDABLE and ADD_PART skip self because the native client mutates optimistically (`appmsg.cpp:1390,1624-1625`). This port sends without mutating inventory and applies the authoritative own echo once, matching its established swap/reload convention. No new inventory prediction was added.
- Native throttle presets are held-key polling; the port keeps its existing edge/latch input convention, composition suppression and launch priming. Both presets release the joystick throttle latch on a new press (`WinTrek.cpp:10948,10954`), so a stationary analog throttle cannot replace the chosen value. Both new presets remain unbound by default, matching their absence from native's default map; they are discoverable and persistently rebindable in F6.
- Native ping information is an MML popup (`WinTrek.cpp:6129-6135`); the port uses its existing text-panel renderer with sixteen report lines per page. Raw ping/loss values are retained separately from display values. The existing server omits the requester and currently reports zero placeholders for other peers; no measured RTT/loss is fabricated. The native display then shows its minimum 10ms, as before. This wave changes the client interface, not the server's measurement implementation.
- The carrier camera reuses the existing override-camera framing with a per-request duration; existing eject requests keep their five-second default (`WinTrek.cpp:6501`). No new renderer or simulation subsystem was introduced.

The receive registry now accounts for **114 consumed messages (97 native + 17 extensions) and 5 unconsumed native messages**. REQUEST_MONEY was already dispatched despite stale inventory evidence; its completion here adds the missing same-side guard and network evidence. GAIN_FLAG previously had world sound logic without session dispatch; this wave completes the route and state/text behavior. Remaining unconsumed entries are LOGOFF, NEW_SHIP_INSTANCE, TREASURE_SETS, ACQUIRE_TREASURE and WARP_BOMB, with the inactive cases documented.

## Wire and UI verification

`P740FlightInterfaceTests` independently writes native golden layouts for all six selected active messages, including signed cargo mounts and RELAUNCH_SHIP's padded var-item, float vectors and identity quaternion. Golden frames cross a real `TcpTransport` into an actual connected `ConnectionSession`; a separate TCP packet fences receive ordering. Tests assert resulting world mutations, not merely that FMIDs were recognized. Codecs are unchanged.

Additional encoded-message tests cover pre-ENTER_GAME refusal, unknown recipients/types, equipment vs cargo socket matching, part merging and own echoes, both dispenser types and exhaustion, flag clear/tech/artifact/enemy/restart cases, wallet approval and enemy-request rejection, own vs remote carrier relaunch (cookie, full pools, cargo split, three-second camera and repaired voice), and ping marker/clamp/snapshot/departure/reset behavior. A real production server answers `SendPlayerPingsRequest`; the fixture waits for actual player identity before requesting, as native's FindPlayer requires.

`P740FlightInputTests` invokes actual headless Game1 handlers without a graphics device. It verifies persisted rebinding, held/composed/flight-entry edge suppression, preset priority and the translated control axis. The ping input sends an independent golden **`04 00 C3 00`** request over TCP; the real receive marker opens the pane, and the same action closes it. A forty-player report verifies the rendered page model reaches the final average without overflowing the panel.

Development failures are retained outside the final certificate. The first compile attempt exposed an ambiguous test type and nullable assertion; subsequent checks exposed missing GAIN_FLAG dispatch, an overly strict side lookup for worlds whose own ship already had a side, missing carrier sector fallback, and a direct voice looked up as a sonic-chat index. Those implementation issues were fixed. Test corrections included an already-populated ordering fence, waiting for player identity and respecting the existing server's requester exclusion, and initializing the six mandatory GAME_OVER side names. No existing assertion, receipt deadline, quarantine trait or suite was relaxed.

The first certificate attempt completed **83 methods × 3 isolated runs / 360 case passes / zero failures**, then its launcher was deliberately interrupted during the first showcase, before any broad suite. The child survived that interruption; the next diagnostic driver overlapped it and reused a shared ledger. Both were stopped, their locks were verified free, and every overlapping attempt was excluded before starting final certification in a fresh directory. The final runner uses a task-wide exclusive lock, an explicit STOP flag, isolated subprocess groups and resumable result labels. No broad suite had started during recovery; see [diagnostic transition](P740_assets/diagnostic-transition.json). Final source review identified the missing native joystick handoff for the new throttle presets; both now release the existing joystick latch and the production-handler test verifies that interaction. Earlier results and all diagnostic showcases are excluded from final validation credit; see [pre-joystick summary](P740_assets/pre-joystick-summary.json). A discovery-only invocation that selected a nested transport helper executed zero cases; it is preserved in [discovery-invalid.json](P740_assets/discovery-invalid.json), excluded, and discovery now selects actual top-level test methods.

An integration review also found that the actual Game1 frame runs `UpdateMainMenu` before the ping handler, consuming shared arrow-key edges even while the menu is closed. Ping navigation now has separate edge state, its regression runs both handlers in production order, and an open report suppresses flight input through the existing modal/composition guard. The final UI rebuild is **0 warnings / 0 errors**. All 91 focused methods and five showcases are revalidated on that build. The already-completed Core broad pass is retained because all 26 of its dependency DLLs are byte-identical and it has no Game assembly dependency; this avoids a second Core sweep. ClientCore and MonoGame broad passes run after the rebuilt UI checks. [Dependency and revalidation record](P740_assets/ui-revalidation.json).

## Validation results

<!-- P740-VALIDATION-BEGIN -->
Whole-solution Release build: **0 warnings / 0 errors**. [Build/source/test-binary fingerprints](P740_assets/build-fingerprints.json) identify the final source and binaries; raw build log: `/tmp/allegiance-evidence/P740/build-final-metadata.log`. `src/Allegiance.Core` and `src/Allegiance.Server` are unchanged, so the conditional AOT-publish gate does not apply.

Final focused certificate: **91 distinct methods × 3 fresh exact-method isolated runs = 273 invocations / 384 passing cases / 0 failures / 0 skips**. The original runtime cohort is supplemented by **147 final metadata-fix invocations / 435 passing cases / 0 failures**, covering the native-default catalog contract, input persistence, new presets and equipment-swap integration. [Metadata correction and selected methods](P740_assets/metadata-revalidation.json). This includes every new method, native codecs, receive registry, money approval and its real-server test, departure/reset paths, carrier/respawn feedback, action-map guards, throttle arithmetic and joystick latch regressions. [Selected methods](P740_assets/focused-methods.json), [complete invocation ledger](P740_assets/verification.json).

The five scheduled final-build showcases recorded **4 passes / 1 failure**. The failed run logged a **5.02s idle interval** and a ticker-thread cleanup timeout; its cleanup assertion can mask an earlier parking assertion. The method then passed **3/3 fresh isolated runs after all broad suites**. A separate **five-run HEAD baseline** provides the requested rate comparison: base failures **0/5**, task failures **1/5**. These small samples do not establish a cause or a failure-rate increase. [Baseline comparison](P740_assets/baseline-comparison.json).

Completed showcase runs had real shots/hits/kills. Minimum arrivals among completed runs: **25/30**; maximum sampled pause among completed runs: **2.005s**. The failed run is retained separately rather than included in those successful-run extrema. [Per-run metrics](P740_assets/showcase-metrics.json).

Exactly **one unfiltered broad pass** ran for each required assembly, with no overlapping suite processes:

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4877 | 2 | 0 |
| ClientCore | 1396 | 1 | 0 |
| ClientCore.MonoGame | 2437 | 2 | 3 |

Core completed before the last UI-only page-navigation adjustment. All **26 Core dependency DLLs remained byte-identical**, and Core has no Game assembly dependency, so its one broad pass is retained. After the final UI rebuild, all **91 focused methods and five showcases ran fresh**; ClientCore and MonoGame then ran once each. [UI revalidation and dependency hashes](P740_assets/ui-revalidation.json). The local P660, P542, capture and P539 baseline opt-ins were enabled. Ordinary live/loopback and LiveTiming cases ran; no rig/221/200v200 opt-in was enabled. No second broad sweep was run.

Every failing method below passed **3/3 fresh individual runs after all suites**, qualifying as a load flake under this brief. The original failures remain in the ledger and raw TRX; [TEST_FLAKES.md](../TEST_FLAKES.md#p740-observation-2026-10-18) records them. No assertion, receipt deadline, quarantine or production code was changed to clear them.

- `Allegiance.ClientCore.Tests.ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` — **3/3** isolated passes.
- `Allegiance.ClientCore.MonoGame.Tests.ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy` — **3/3** isolated passes.
- `Allegiance.Core.Tests.Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost_AndClearsConnected_WhenServerDies` — **3/3** isolated passes.
- `Allegiance.Core.Tests.Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` — **3/3** isolated passes.
- `Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` — **3/3** isolated passes.

MonoGame also found a deterministic native-default catalog defect: throttle33/66 used explicit empty native bindings instead of the catalog's null representation of native-unbound commands. The original method failed **3/3** isolated runs on the old binary. The metadata is corrected; the whole solution rebuilt **0 warnings / 0 errors**, and the final metadata cohort above passed. This is a fixed product issue, **not a load flake**. The original broad and isolated failures remain visible; no broad suite was repeated. Runtime control defaults, protocol, server/simulation behavior and existing assertions are unchanged.

Actual skipped result names (TRX outcomes, rather than its unreliable aggregate notExecuted counter):

- ClientCore.MonoGame: `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_built_content_directory_exists`
- ClientCore.MonoGame: `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_has_the_full_meshy_set`
- ClientCore.MonoGame: `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_merge_yields_identical_resolution_to_before`

Final audit: source and test-binary fingerprints match the tested build; HEAD is unchanged; exactly ten parity rows changed; simulation CSV is byte-identical to base; edited source/CSV/report files use LF; `git diff --check` passes. [Audit/summary and native inactive-message scan](P740_assets/summary.json), [reproducible runner](P740_assets/verify.py).

Raw logs/TRX and all excluded development attempts are outside the repository at **`/tmp/allegiance-evidence/P740/`**, the retained Core sweep under **`certified/`**, and final focused/showcase/ClientCore/MonoGame/isolation results under **`final-ui/`**. The sandbox permits external writes only under `/tmp`, so the brief's preferred `~/allegiance-evidence/P740/` destination could not be used. Only compact JSON, scorecard text and verification scripts are in `P740_assets/`; total evidence is below the **2 MB** task limit. No raw log, TRX or archive was added to the repository. No commits or pushes were made.
<!-- P740-VALIDATION-END -->

## Initial open inventory, descending weight

All entries below were partial unless marked missing. No parts rows were open. The training entries are the only P722-area leftovers in this checkout and remain outside this lane.

| Weight | Area | Native item | Initial status |
| --- | --- | --- | --- |
| 4 | commands | TK_ViewTurret | partial |
| 4 | fedmsg | 156 GAIN_FLAG | partial |
| 4 | fedmsg | 162 ACQUIRE_TREASURE | partial |
| 4 | fedmsg | 164 ADD_PART | partial |
| 4 | fedmsg | 182 REQUEST_MONEY | partial |
| 4 | fedmsg | 188 WARP_BOMB | partial |
| 4 | fedmsg | 192 RELAUNCH_SHIP | partial |
| 4 | fedmsg | 194 PINGDATA | partial |
| 4 | fedmsg | 31 NEW_SHIP_INSTANCE | partial |
| 4 | fedmsg | 87 FIRE_EXPENDABLE | partial |
| 4 | fedmsg | 89 TREASURE_SETS | partial |
| 4 | lobby | Training missions (tutorial) | partial |
| 3 | commands | TK_ConModeInvest | partial |
| 3 | commands | TK_Throttle33 | partial |
| 3 | commands | TK_Throttle66 | partial |
| 3 | screens | Training screen | partial |
| 2 | commands | TK_CommandFinishTM | missing |
| 2 | commands | TK_LODDown | missing |
| 2 | commands | TK_LODUp | missing |
| 2 | commands | TK_PauseTM | missing |
| 2 | commands | TK_RollModifier | missing |
| 2 | commands | TK_SkipTMGoal | missing |
| 2 | commands | TK_ToggleLODSlider | missing |
| 1 | commands | TK_DecreaseTMClockSpeed | missing |
| 1 | commands | TK_IncreaseTMClockSpeed | missing |
| 1 | commands | TK_ResetTMClockSpeed | missing |

The two active FEDMSG rows still open are ACQUIRE_TREASURE and WARP_BOMB. Twelve command rows remain open, including training controls, LOD controls, airborne investment, roll modifier and the deliberately extended/disabled-native turret camera. Existing training work and all simulation matrix rows are unchanged.
