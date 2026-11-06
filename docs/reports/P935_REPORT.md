# P935 — session time conversion and admin starting-tech overrides

Status: implementation and all targeted regression trials complete. Solution/AOT gates pass; the single Fast sweep is qualified below. The five-pair never-parks comparison passes in both lanes. No commits or pushes. Base: `bf1978d7`, lane `codex/p935`.

## Native inventory and time domains

Native references were re-grepped in the read-only native source tree. The complete IGC call-site inventory for these two site methods is:

| Direction | Native caller | Production port binding |
|---|---|---|
| Local → server | `Igc/buildingeffectigc.h:142` | `src/Allegiance.Core/Wire/ExportBuildingEffect.cs:134`: export the effect's original `StartTime`, through its mission site, as milliseconds. `Server.Docking.cs` calls this for both creation and later discovery/join exports. |
| Local → server | `Igc/shipIGC.h:1178` (`ClientShipUpdate`) | `src/Allegiance.Core/Wire/ClientShipUpdate.cs:116` (`FromLocalPose`), called by `Game1.Prediction.cs`. Both inner and outer C_SHIP_UPDATE times are the converted milliseconds. |
| Local → server | `Igc/shipIGC.h:1208` (`ClientActiveTurretUpdate`) | `src/Allegiance.Core/Wire/ActiveTurretUpdate.cs:152` (`FromLocalTime`), called by `Game1.TurretGunner.cs` and `tools/allegiance-mcp/BotManager.TurretGunner.cs`. Both inner and outer turret times are converted milliseconds. |
| Local → server | `Igc/treasureIGC.cpp:274` | `src/Allegiance.Core/Sim/Treasure.cs:47` (`ExportData`); all four server treasure send paths now use it, including join snapshots and first-visibility exports. |
| Server → local | `Igc/buildingeffectigc.h:50` | `ClientWorld.Apply(ExportBuildingEffect)` retains the converted envelope start. Animation and pruning use local simulation age, including time elapsed before receipt. |
| Server → local | `Igc/chaffigc.cpp:48` | `ClientWorld.Apply(CreateChaff)` converts Time0. Coasting, growth and pruning use that creation instant; the server now stamps native simulation milliseconds, rather than its unrelated tick index. |
| Server → local | `Igc/mineigc.cpp:44` | `ClientWorld.Apply(ExportMine)` converts Time0. Native `exportF && createNow` instead uses the current local simulation instant (`mineigc.cpp:37-44`). |
| Server → local | `Igc/probeigc.cpp:51` | `ClientWorld.Apply(ExportProbe)` converts Time0, with the same native createNow branch (`probeigc.cpp:44-51`). |
| Server → local | `Igc/treasureIGC.cpp:41` | `ClientWorld.Apply(TreasureSpawn)` and the core `Treasure` constructor convert the original creation instant; createNow uses now. Drift uses the same local simulation clock. |
| Server → local | `Igc/shipIGC.cpp:3112` (`ServerHeavyShipUpdate`) | This port's heavy pose stamps are tick indices, explicitly excluded from conversion by the brief. They remain byte-for-byte tick values. |
| Server → local | `Igc/shipIGC.cpp:3306` (`ServerActiveTurretUpdate`) | The port's relayed turret stamps likewise use the enclosing heavy message's tick reference. These tick indices remain unchanged. |

`Igc/igc.h:4760-4767` supplies identity defaults for the server. The actual network-client offset rule is `clintlib/clintlib.h:860-869`: add the session offset to export; subtract the same offset to import. Keeping server identity is intentional. Exporters now call the site, and nonzero-offset tests exercise that binding.

The two “ship-update formats” at the native export conversion sites are **client pilot and client active turret**, not server single/heavy telemetry: native `ServerSingleShipUpdate` exports time directly (`Igc/shipIGC.h:1188-1199`), and native heavy export uses its reference directly (`shipIGC.h:1154-1170`). The port's existing `ServerShipUpdate.Time` inside SET_CLUSTER, SINGLE_SHIP_UPDATE, SHIP_RESET and RELAUNCH_SHIP, heavy `TimeReference`/CompactTime, and relayed active-turret CompactTime remain tick-based. Identity-only conversion of these tick values would be incorrect. Generic EXPORT/OT_ship has no native Time field to convert.

For completeness, the separate BaseClient message layer also calls these functions at `clintlib/clintlib.cpp:1863,1892,3199` and `clintlib/appmsg.cpp:369,863,1041,1151,1185,1584,2096,2412,2696,2707`. These cover outer pilot/turret times, missile fire, ballots, warp bombs, own-ship corrections, heavy-message references and mission deadlines. P935 binds both outer client update stamps to their converted inner stamps; it preserves existing server-domain deadline/missile and tick-pose policies and does not claim other parity rows.

The session implementation is `src/Allegiance.ClientCore/Flight/OwnShipServerClock.cs:13,73-85`, now an `IIgcSite`. It integrates local simulation time at the advertised rate before rate changes, including pause, then applies the native add/subtract offset. It never divides by a paused rate. Native millisecond encoding uses `src/Allegiance.Core/Sim/IgcTime.cs:6-8`, matching `floor(seconds * 1000 + .5)` in `zlib/ztime.h:144-159`; large local epochs and half-millisecond boundaries have regression coverage. The legacy millisecond estimator retains its original subtraction-before-conversion arithmetic, avoiding a one-millisecond cancellation error. `ClientWorld` owns an instance fed by the real connection's PING path. Reconnect resets it; world rotation does not reset the connection's epoch. Early construction/chaff/treasure exports retain their original timestamps and bind when the first actual clock sample arrives. Local fixtures without a session sample preserve their existing fallback behavior. Probe/mine raw `Time0ServerSeconds` diagnostics retain the original wire values, including createNow exports; their simulation anchors independently follow the native createNow branch. Lifetime presentation uses those converted local anchors through the shared model, common to both HUD styles; no HUD layout or new interface screen was added.

## Admin overrides and generation

Native rules:

- `FedSrv/fsmission.cpp:2718-2733`: allocate two arrays on demand, with six zeroed masks each; preserve existing arrays on later calls.
- `FedSrv/AdminGame.cpp:312-328` and `FedSrv/AllSrvModuleIDL.idl:262`: an admin can alter a bit only in STAGE_NOTSTARTED and for a native side slot 0–5. This includes an unused slot in a smaller game. The port also bounds bit input to the 400-bit mask before indexing it.
- `FedSrv/fsmission.cpp:225-236`: admin game parameters copy altered/value pairs for sides with changed bits.
- `FedSrv/fsmission.cpp:1954` passes both arrays to generation. `Igc/missionigc.cpp:3885-3898` computes `(altered & newSetting) | (defaults & ~altered)`, then updates initial techs and creates buckets. Defaults include the civilization/no-development choice and allowed paths (`missionigc.cpp:3867-3883`). File-map station compatibility follows afterward (`missionigc.cpp:4011-4049`).

Port: `src/Allegiance.Core/Model/TechBitOverrides.cs:12-56` implements allocation, bit setting, the mask formula and independent copies. `Mission.TechOverrides.cs:10-42` guards the operation and finalizes starting techs/buckets. `Mission.Assemble` accepts masks before its initial bucket creation. `Server.Lifecycle.cs:1425` supplies masks from startup parameters, or carries the previous game's edited masks on rotation. `Server.Lobby.cs:2062` finalizes pending console changes at the native countdown/generation boundary and sends updated CREATE_BUCKETS/BUCKET_STATUS to seated clients. For file maps, authored station choices are restored before prerequisite replacement; generated maps keep their current civilization-selected stations. Without masks the finalizer returns without changing any tech or bucket object.

Production operator surfaces (documented in `docs/GUIDE.md`):

```text
override-tech <game-index> <side> <bit> on|off
```

The existing bounded stdin queue dispatches this on the simulation owner thread (`src/Allegiance.Server/Server.Console.cs:103-115`). It reports accepted/refused. Game index starts at 0 in status order. Startup parameters use `games[].techBitOverrides`, e.g. `[{"side":0,"bit":399,"setting":true}]` (`ServerConfig.cs:52-62`). They deserialize with the existing source-generated JSON context. No service, port, store or environment variable was added.

## Row verdicts and regression evidence

| Row | Verdict | Proof |
|---|---|---|
| SIM-0156 (1) | done, production | Both native client update codecs use a nonzero session site; building and treasure exporters use the model's original creation time through the mission site. `P935TimeConversionTests.Both_client_export_formats_convert_the_inner_time_and_roundtrip_the_wire`; `Model_exports_convert_creation_time_through_the_mission_site_at_a_nonzero_offset`. |
| SIM-0157 (1) | done, production | All native object import branches use session conversion, including createNow, early import, pause/resume and delayed receipt; tick-domain ship stamps remain explicitly unchanged. `P935TimeConversionTests` covers every branch and domain distinction. |
| SIM-0523 (2) | done, production | Console and startup parameters reach lazy paired masks and the generation boundary. `P935TechOverrideTests` verifies set, clear, last-setting wins, side isolation, unused valid slot, invalid input, refusal after start, no-mask identity, initial bucket/station choices, source-generated config and rotation ownership. |

Actual pre-change proof ran against the unchanged baseline assemblies (not an identity-only mock). Repro source: `.scratch/p935/before/Program.cs`; outputs: before (`.scratch/p935/fail-before.log`) and after (`.scratch/p935/pass-after.log`). The same proof source was rerun against the final binaries.

| Scenario | Required | Before | After |
|---|---:|---:|---:|
| Delayed building progress (2 s into 40 s) | 0.05 | 0 | 0.05 |
| Delayed chaff X (spawn 10, speed 10, age 2) | 30 | 10 | 30 |
| Building export (client start 1000 s, server offset −900 s) | 100000 ms | 1 (tick index) | 100000 ms |
| Treasure imported creation in the client epoch | 998 s | 98 s | 998 s |
| Console override operation | accepts before start | usage only | accepted; queued until generation |

The added tests use the real wire codecs, client dispatch and production queued console. The console proof uses real ready-up over loopback for generation, rather than calling the override API only in a test. A separate synthetic-core test proves mask application precedes initial-tech catalog creation and file-map station compatibility. Existing own-clock, treasure-motion, chaff and building-effect tests are included in affected checks.

## Scorecard

Before: `python3 scripts/parity-scorecard.py` validated all matrices; simulation **2119/2144 (98.83%)**, **794/805 (98.63%)** completed eligible rows. Target rows were partial, with SIM-0523 test-only.

After: `python3 scripts/parity-scorecard.py` validates all matrices; simulation **2123/2144 (99.02%)**, **797/805 (99.01%)** completed eligible rows. Only SIM-0156, SIM-0157 and SIM-0523 changed: four additional production points and three rows. All other rows remain untouched. Full outputs: before (`.scratch/p935/score-before.txt`), after (`.scratch/p935/score-after.txt`).

## Gates and retained evidence

The baseline solution build passed with 0 warnings and 0 errors. `local.env` is present and loaded for corpus-backed proofs; focused pre-gate results have **Skipped=0**. NuGet's default HTTP cache is read-only in this lane, so build/test commands use a task-local `NUGET_HTTP_CACHE_PATH` and `TMPDIR` under `.scratch/p935/`; no cache workaround is committed.

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | Final build: **0 warnings, 0 errors**. `.scratch/p935/gate-build-final.log`. |
| `tools/build-tools.sh` | Final retry: **all 116 tools pass, 0 warnings/errors**. Log (`.scratch/p935/gate-tools-clean.log`). One earlier refresh hit an MSBuild worker exit (MSB4166); the affected p808c tool rebuilt cleanly alone, then the entire tools script passed with build-server/node reuse disabled. No source workaround was needed. |
| Server Linux x64 AOT publish | **Pass**, no IL2026/IL3050 diagnostics. `.scratch/p935/gate-aot-final.log`. The actual native executable also loaded startup masks, accepted a queued console override, and exited cleanly: smoke result (`.scratch/p935/aot-smoke-result.json`). |
| New/affected checks, three trials alone | **372/372 pass, 0 skipped**, in 87 isolated invocations. Each new method ran separately three times; theory cases are included. Includes existing large-epoch clock, P786 wire diagnostics, P930 assembly harness, live own-flight, console, construction, treasure and native site/generation tests. Raw results: `.scratch/p935/isolated/`. |
| ONE Fast-tier run | **13183 pass, 15 fail, 3 skipped** across 13201 cases; four executions exceed the calibrated 10-second limit. Summary (`.scratch/p935/fast/summary.json`), runner log (`.scratch/p935/gate-fast.log`). Details below. |
| Never-parks, five base / five changed | **5/5 pass in each lane, 0 skips**. Detailed observations below. |
| Glossary / scorecard / whitespace | Glossary paths and all parity matrices validate; only the requested three rows changed; CSV has no CR bytes. `git diff --check` passes. |

The one Fast sweep caught three task compatibility regressions: two existing tests expected raw mine/probe `Time0ServerSeconds` diagnostics even when createNow is set, and P930's reflection harness expected the original seven-parameter mission-assembly entry point. Both compatibility paths were restored while preserving converted simulation anchors and a separate rotation-override helper. The affected P786 class passes **3/3 trials (9 cases)** and P930 passes **3/3 trials (75 cases)** after the corrections. The complete new/affected isolation set was refreshed against the final build. The full tier was not repeated, honoring the requested single run.

The other **12 failed cases** and the timing overruns are non-task failures with three isolated results in the [P935 ledger entry](../TEST_FLAKES.md). Persistent failures: five bundle-path fixtures find ancestor local.env under mandatory task-local TMPDIR; the read-only profile-restoration fixture; the known defender null-target assertion; and the known missing missile-tip pair. The profile test passes 3/3 with writable task-local XDG_CONFIG_HOME. Nanite credit and the miner-cycle test pass 3/3 alone. Position-request and dock-status histories remain intermittent (pass/fail/pass and fail/pass/pass). Both timing-only SC2 support and P814 gunner cases pass 3/3 below 10 seconds. No unrelated implementation or assertion was changed.

The **three skips** are the existing Meshy-pack content fixtures; `local.env` is loaded and every task/affected isolation has Skipped=0. No training logic changed. No remote hosts were contacted.

### Both HUD styles

The existing `tools/hud16-proof` ran against the final binaries in both styles
(exit 0 for each). These are real Game1 backbuffer captures after a production
probe EXPORT over loopback: Classic (`.scratch/p935/hud-Classic/radar-icons-probe-classic-1920x1080.jpg`)
and Modern (`.scratch/p935/hud-Modern/radar-icons-probe-modern-1920x1080.jpg`).
Both images were opened and inspected. Both show the yellow probe contact with
its **231 m** label below the center reticle; Classic has curved cockpit gauges,
while Modern shows separate numeric 100% gauges. The screenshots confirm both
presentation paths consume the same live contact; nonzero-offset age and
createNow behavior are established by the tests above. Captures remain in
ignored task scratch; no new media is added to the public working-tree changes.

### Never-parks comparison

The counted trials use copied dispatch-base assemblies versus the final working-tree assemblies (SHA-256 hashes are retained in validation.json), with native ship updates, pooled transport, immutable/encode-once snapshots, parallel builds, native GotoPlan and server GC explicitly pinned. P863 budget defaults are unchanged. Per-trial machine load samples are retained beside the TRX files. Preliminary ordinary-fixture warm-up trials are excluded from this comparison.

**Baseline 5/5 pass; working tree 5/5 pass; 0 skips.** The observed failure rate is 0/5 in each lane.

| Trial | Base observation (s) | Base longest idle (s) | Working observation (s) | Working longest idle (s) |
|---|---:|---:|---:|---:|
| 1 | 214.4 | 2.016 | 190.9 | 2.308 |
| 2 | 196.2 | 2.114 | 202.5 | 1.906 |
| 3 | 237.2 | 0.000 | 257.6 | 1.211 |
| 4 | 229.4 | 1.909 | 191.7 | 2.004 |
| 5 | 183.4 | 0.605 | 231.7 | 2.208 |

All ten observations stay below the same five-second parking bound. The maximum observed idle is 2.114 s on base and 2.308 s in the working tree. Recorded one-minute host-load samples range from 1.40 to 7.43. Five trials per lane support this local behavior check.

Raw results and launch script: `.scratch/p935/run-never-parks.sh`. No synthetic load was generated.

## Native-intent footer

**Time conversion — design.** Independent machines have independent monotonic epochs; local/server conversion is necessary. Native's offset arithmetic remains appropriate. The port's simulation-rate integration handles its intentional time dilation without substituting a tick count for native Time. A modern synchronized clock estimator could reduce network jitter, but it does not replace domain conversion and is outside this parity task. Receipt-anchored late construction/chaff were port bugs corrected here.

**Tech overrides — design with an era-specific admin surface.** Two sparse masks distinguish “inherit the default” from “force off,” while preserving an explicit “force on.” The native COM/IDL administrator entry point reflects its era; the existing local console and source-generated startup configuration provide the same operation without adding a new UI. An immutable per-match parameter snapshot could simplify ownership in a new engine; independent mask copies preserve native behavior within the port's rebuilt-world lifecycle.
