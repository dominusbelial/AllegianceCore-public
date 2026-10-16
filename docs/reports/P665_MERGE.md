# P665 — integrate P653 economy/loadout validation

2026-10-14. Integration base `385fbb25f0a7a197c76dcb2d02ec9c7dffc563d4` (`integ2`, P660 + wave 2 + P660b); incoming `597e205dbba10f21b690e2f53ad808d031704dac` (`wip/p653b`, P653/P653b/P653c); common base `32ceeccd10133e227e0ca0e21686f8ee1fb47fb9`. No commits or pushes.

**Outcome:** all conflicts resolved with both lanes retained. Final Core, ClientCore and Lobby suites are clean. MonoGame has one existing load/seating flake, confirmed **3/3 passing individually**, plus its three unchanged optional private-asset skips. The final affected-test matrix passes all **984 case executions**.

Read [P653C_FINISH](P653C_FINISH.md), [P660_NEXT_MATCH](P660_NEXT_MATCH.md), [P641_WEAPONS_PARTS_PARITY](P641_WEAPONS_PARTS_PARITY.md), the hostile-input audit, and TEST_FLAKES. All incoming changes, including branch-owned reports and evidence assets, are retained.

## Merge metadata and dispatcher handoff

The requested `git merge --no-commit wip/p653b` was attempted first. It failed before modifying files because the sandbox mounts the shared Git directory read-only: `cannot lock ref ORIG_HEAD ... Read-only file system`. The worktree's `.git` pointer and original branch/index were left intact.

To complete the actual three-way merge, created writable temporary Git metadata at `/tmp/p665-git`, reused the original object database via `objects/info/alternates`, copied the original index, and installed refs for the two existing commit IDs. Ran `git --git-dir=/tmp/p665-git ... merge --no-commit wip/p653b` against this same working directory. It produced the three expected conflicted files. The resolved merge remains uncommitted in that temporary metadata; all resulting files are present in the original working tree. The original Git status presents incoming additions as untracked and modifications as unstaged because its index cannot be written here.

Dispatcher: the temporary metadata preserves `MERGE_HEAD`, `MERGE_MSG`, `ORIG_HEAD`, and the fully resolved merge index. With write access to the actual worktree Git directory, copy the **three merge-state files** (`MERGE_HEAD`, `MERGE_MSG`, `ORIG_HEAD`) from `/tmp/p665-git` there, then stage `src`, `tests`, and `docs` from the working tree using the original index before recording the dispatcher-owned commit. Do not simply copy the temporary index: resolved blobs live in the temporary object store; staging with the original metadata writes them to the real object database. The pre-existing untracked `P665_BRIEF.md` is left untouched. Alternatively consume the working-tree changes through your normal WIP procedure. Neither operation was performed in this lane.

## Every conflict and resolution

Five conflict blocks in three files:

| File / block | Resolution | Why / native evidence |
| --- | --- | --- |
| `src/Allegiance.Core/Sim/Ship.cs`, `MountPart` admission | Combine P653's negative-amount rejection, hull quantity clamp, signed cargo routing and occupied/range rejection with wave 2's **equipped plus cargo** count at the byte-sized inventory ceiling. | Keeps hostile quantity/slot handling and prevents cargo from evading the port's 255-entry export limit. Native capacity: `~/Development/Allegiance/src/Igc/packigc.h:102-123`, `~/Development/Allegiance/src/Igc/launchertypeigc.h:130-145`; signed cargo: `~/Development/Allegiance/src/Igc/shipIGC.cpp:1516-1549`. The 255-entry ceiling is a port wire constraint. |
| `Ship.cs`, `TrySwapPartWithCargo` source/no-op | Validate source equipment identity and requested destination hardpoint before mutation; retain destination-to-source compatibility and the same-slot early return before batching. | Both halves of an exchange must fit; a legal same-slot echo must leave readiness intact. Native slot validation and echo: `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6466-6498`; hardpoints: `~/Development/Allegiance/src/Igc/hullTypeIGC.h:199-213`. |
| `Ship.cs`, `PlaceIntoHome` | Preserve free-spawn provenance while resetting moved parts to readiness zero. Cargo placement bumps the loadout version and raises a batched event. Equipment mounting retains the empty-shield and afterburner deactivation behavior. | Prevents a free starter gun from acquiring resale credit after a cargo round trip while retaining flight remount behavior. Native readiness reset: `~/Development/Allegiance/src/Igc/shipIGC.cpp:1557-1591`; shield reset: `~/Development/Allegiance/src/Igc/shieldIGC.cpp:121-139`; afterburner mounting/power: `~/Development/Allegiance/src/Igc/afterburnerIGC.cpp:91-164`. Free-spawn provenance is the port's accounting guard. |
| `src/Allegiance.Server/Server.Combat.cs`, LOADOUT_CHANGE parts | Keep complete equipped/cargo export and P653c's runtime 255 → native wire 240 conversion. Preserve P660b's command-depth suppression and hull export invalidation before suppression. | Ordinary snapshots contain final quantities, cargo and native fractions. PROMOTE/SWAP_PART continue publishing only their dedicated command echoes. Native fraction: `~/Development/Allegiance/src/_Utility/Utility.h:1311-1327`, `~/Development/Allegiance/src/Igc/shipIGC.h:819`; inventory export: `~/Development/Allegiance/src/Igc/shipIGC.h:804-824`; disabled per-event forwarding: `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:13530-13555`. |
| `src/Allegiance.Server/Server.Docking.cs`, hardware value/refund helpers | Use P653's widened paid-hull and refundable-part calculation, including cargo. Launchers price their remaining rounds, packs and free spawn parts yield no value. Consume hull credit and inventory before outward sends, then credit the player wallet once with a side-wide money notice. | Repeated boarding/teleport cannot mint starter hardware value or treasury money; wave 2 cargo stays accounted for. Native wallet refund: `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:2426-2443,7090-7105`; hardware value: `~/Development/Allegiance/src/Igc/shipIGC.h:396-406`; quantity prices: `~/Development/Allegiance/src/Igc/magazineigc.h:85-88`, `~/Development/Allegiance/src/Igc/dispenserigc.h:86-89`; free pack: `~/Development/Allegiance/src/Igc/packigc.h:80-83`. |

All abbreviated native paths above are under `~/Development/Allegiance/src/`.

## Cleanly merged behavior reviewed

- Keep all eleven P653 findings: H-01 donation/overflow validation; H-03 paid/free provenance and refund-once; H-06 real dock/alive/unboarded purchase gates; H-07 actual hardpoints and unique slots; H-08 quantity/ammo clamps; H-09 final purchase publication spanning launch/refill; H-20 bounded loadout decoder; H-22 legal flying inventory operations; H-18 constructor/rock commitment and travel; H-26 child/admin suicide; H-30 consumed treasure offers before mutation/send. The incoming P653c fixture corrections remain in place.
- Keep wave 2's flight commands, inventory echoes, mount readiness and role notices. `Server.ShipCommands` retains P660b's `try/finally` suppression scopes around PROMOTE and SWAP_PART. Native dedicated echoes: `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6354-6377,6459-6501`; client application: `~/Development/Allegiance/src/clintlib/appmsg.cpp:824-849,1430-1456`.
- Keep P660 rotation, current-time world assembly, intermission/readiness policy and client deadlines. Migration uses fresh docked ships with zero free-hull credit. Native assembly epoch: `~/Development/Allegiance/src/Igc/missionigc.cpp:3055-3059`; seat reset: `~/Development/Allegiance/src/FedSrv/fsmission.cpp:2604-2622`; restart deadline: `~/Development/Allegiance/src/FedSrv/fsmission.cpp:3539-3546`; initial hangar: `~/Development/Allegiance/src/FedSrv/fsmission.cpp:791-796`. The server's automatic-next-match policy remains the existing port adaptation.

## Clean-merge correction

Wave 2's `TryMountCargoPart` survived the textual merge unchanged. Its direct `Mission.Site.LoadoutChangeEvent` bypassed P653's outer purchase batch. A three-part purchase containing signed cargo therefore generated two loadout events/snapshots. Strengthened H-09 with a real cargo purchase and reproduced **Expected 1 / Actual 2** before the production correction (`/tmp/p665-cargo-red.log`). The helper now delegates to `MountPart` with the signed byte mount, sharing quantity/slot/count validation and `RaiseLoadoutChange` batching. It retains its public signature, return semantics and cargo bookkeeping. This closes H-09 for cargo purchases and applies H-08 to the helper as well.

The remaining validation rules map to native sources under `~/Development/Allegiance/src/` as follows. These are preserved checks; modern defenses are identified separately from literal native behavior.

| Validation | Native file:line / classification |
| --- | --- |
| H-01 donation sentinel and cycles | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:5523` contains the inherited amount-check bug; negative-gift and checked-recipient arithmetic are modern defenses. |
| H-06 alive/unboarded/real-station buying | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6153-6172`; `~/Development/Allegiance/src/Igc/shipIGC.h:992-1000`. |
| H-07 per-slot compatibility and occupation | `~/Development/Allegiance/src/Igc/hullTypeIGC.h:163-176,199-213`; `~/Development/Allegiance/src/Igc/shipIGC.h:1029-1031`. |
| H-08 ammo and launcher/pack quantities | `~/Development/Allegiance/src/Igc/shipIGC.h:597-601`; `~/Development/Allegiance/src/Igc/magazineigc.h:107-113`; `~/Development/Allegiance/src/Igc/packigc.h:102-123`; `~/Development/Allegiance/src/Igc/launchertypeigc.h:130-145`. Widened arithmetic prevents managed short/int overflow. |
| H-09 one final purchase publication and undock arming | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6264,13183-13191`; `~/Development/Allegiance/src/FedSrv/fsship.cpp:694-735`; `~/Development/Allegiance/src/Igc/stationIGC.cpp:449-459`. |
| H-20 loadout count bound | `~/Development/Allegiance/src/FedSrv/fsship.h:32`; `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6180`. |
| H-22 flying slot/type/range gates | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6302-6313,6466-6483,6515-6516`; `~/Development/Allegiance/src/Igc/hullTypeIGC.h:199-213`. |
| H-18 constructor arrival/consumption | `~/Development/Allegiance/src/Igc/shipIGC.cpp:2810-2827`; `~/Development/Allegiance/src/Igc/buildingeffectigc.h:294-297,312-353`. Direct drone BUILD authorization and exclusivity defend the port-added command path; native drone commands are `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:7281-7322`. |
| H-26 child/CanCheat suicide | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6338`. |
| H-30 consume treasure buffer | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6148`; consumption before potentially throwing managed sends is the port correction. |

## Integration regressions

Updated H-09's swap leg to distinguish the final batched **site event** from wire publication: one event, zero LOADOUT_CHANGE packets, exactly one SWAP_PART echo with authoritative ship and signed mount, and the complete final inventory. Purchase now includes a signed cargo entry and still asserts exactly one complete LOADOUT_CHANGE, native full fraction 240, and actual post-launch pack quantities. This resolves the incoming audit's obsolete swap-publication expectation against P660b without relaxing the purchase audit.

Added two paid/free rows of `P665_Flight_roundtrip_resets_readiness_preserves_refund_provenance_and_restores_publication`. They send real handler packets and assert legal same-slot readiness preservation, equipment↔cargo readiness reset, paid/free provenance in both locations, functional weapon restoration, command-only echoes, restored ordinary snapshot publication/native fractions, and repeated boarding with no extra wallet or treasury credit.

The broad isolation matrix also reproduced P641's launcher-reload fixture failure **3/3**: it installed ten cargo rounds on a decoded hull with capacity five, then expected seven after transferring three. P653's clamps correctly left two. The fixture now gives that hull capacity for ten rounds using the real chaff expendable size (`~/Development/Allegiance/src/Igc/launchertypeigc.h:130-145`). It retains the exact 7/5 quantity assertions, second reload, fractional mounting reset, dock/parent hold and eventual full readiness assertions. No clamp or expectation was relaxed.

A full-suite failure also exposed an existing role-selection bug in `LiveChatSectorWireTests`: `inFarout[0]` was assumed Alpha, so Beta arriving first caused the code to choose an Alpha as the “lone Beta” and find only one “Alpha.” It failed in the initial full Core run and 2/3 isolated reruns. Resolve the declared Alpha/Beta roles from authoritative mission side IDs instead; retain all five Individual/cross-side/sector/friendly-sector/Group routing assertions and the existing quarantine trait. The corrected test passes 3/3 individually; Core is rerun after the fixture change. No product chat routing changed.

## Verification

Real VSTest, loopback TCP, artwork/native resources are enabled via gitignored `local.env`. No rig/221/200v200 work is run.

Final build: `dotnet build AllegianceCore.slnx --no-restore -m:8 -v:q` — **0 warnings, 0 errors** (2.28 s). The initial build restored dependencies with a writable `NUGET_HTTP_CACHE_PATH=/tmp/p665-nuget-http`.

Final individual matrix: **282 methods / 328 fact-or-theory cases**, each invoked **three times alone**: **984 case executions passed, zero failed, zero skipped**. Each invocation filters one fully qualified method; all its declared theory rows run. This includes every incoming new/modified test class, the affected flight/promotion/rotation classes, late clock initialization, the finale generation barrier, the complete inventory ceiling, and countdown/game-over presentation. The opt-in local P660 showcase was explicitly enabled and passed 3/3. Methods run before the final cargo-helper build were repeated against the final build.

| Assembly | Selected methods | Case executions passed (3×) | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: |
| Core | 217 | 705 | 0 | 0 |
| ClientCore | 40 | 138 | 0 | 0 |
| MonoGame | 25 | 141 | 0 | 0 |

Initial isolation findings are retained in [P665_RESULTS.json](P665_RESULTS.json): the corrected reload fixture failed 3/3 before its capacity fix; the documented `Lane=LiveTiming` boarding test passed 2/3 initially and **3/3** on final reruns. No failures were discarded from the record.

Full suites run sequentially, with **no filters**, including LiveTiming and live sockets, using the final build. `ALLEGIANCE_LIVE_P660_SHOWCASE=1` also enables the full local showcase in the ClientCore suite. Existing optional asset prerequisites retain their configured values. Logs/TRX are in `/tmp/p665-results`; durable per-case outcomes are in [P665_RESULTS.json](P665_RESULTS.json).

### Final suite numbers

| Suite | Passed | Failed | Skipped | Total | Wall seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Core | 4199 | 0 | 0 | 4199 | 274.65 |
| ClientCore | 895 | 0 | 0 | 895 | 270.91 |
| MonoGame | 2119 | 1 | 3 | 2123 | 264.83 |
| Lobby | 12 | 0 | 0 | 12 | 0.95 |

The remaining MonoGame failure is `ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy`: its named sender landed on the minority side, failing the fixture's same-side-teammate premise before the chat assertion. It passed **pass / pass / pass individually after the first complete full-suite pass**, meeting the brief's load-flake rule. It is also in TEST_FLAKES history. No assertion, trait or product behavior was changed for this case.

The three MonoGame skips are `MeshyPackTests.Pack_built_content_directory_exists`, `.Pack_catalogue_has_the_full_meshy_set`, and `.Pack_catalogue_merge_yields_identical_resolution_to_before`: `ALLEGIANCE_MESHY_PACK_PATH` is not configured. No artwork/native or ordinary live-loopback test was skipped.

Initial full Core was **4197 passed / 2 failed / 0 skipped**. `StationsUpdateLiveTests.Damaged_station_fraction_reflected_in_next_update` passed 3/3 alone and passes the final full Core run. The other failure was the chat fixture role bug described above; after correction it passed 3/3 alone and the final full Core run. The initial and final records are both retained. ClientCore, MonoGame and Lobby need no repeat because the last change was confined to that Core test fixture; their product source and own fixtures were unchanged.

Reproduction of the full pass (from this checkout, using the existing external resource configuration):

```sh
set -a
source ./local.env
set +a
export ALLEGIANCE_LIVE_P660_SHOWCASE=1
dotnet build AllegianceCore.slnx --no-restore -m:8 -v:q
dotnet test tests/Allegiance.Core.Tests --no-build
dotnet test tests/Allegiance.ClientCore.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
dotnet test tests/Allegiance.Lobby.Tests --no-build
```

Individual invocations use `--filter FullyQualifiedName=<one method>` three times, with TRX logging. [P665_RESULTS.json](P665_RESULTS.json) records every final selected method, all three outcomes/counters, initial isolation failures, full-suite counters, skip names/reasons and initial full-suite failure reruns. `/tmp/p665-results` retains the actual TRX/log files. The failed cargo-purchase diagnostic is `/tmp/p665-cargo-red.log`. The results were not obtained from a custom assertion runner or by excluding timing tests.

Final source/test/P665-report whitespace checks are clean, and the temporary merge index has no unresolved entries. A whole-import whitespace check also flags **12 inherited lines** in branch-owned evidence: two trailing-space log lines and ten CRLF CSV lines (`P575_assets/bots_dock/mcp_out.log`, `P578_assets/mcp_out.log`, `docs/stress-test-videos/metrics.csv`). Those evidence files are retained as imported; this is not claimed to be a clean whole-import whitespace check.

HEAD remains `385fbb25f0a7a197c76dcb2d02ec9c7dffc563d4` in both the original and temporary metadata. Changes, reports and the resolved temporary merge are left uncommitted. No pushes or rig runs were performed.
