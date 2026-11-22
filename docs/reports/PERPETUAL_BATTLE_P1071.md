# P1071 — Perpetual public battle

Status: all three implementation items complete; final gate limitations are recorded below. Work ran 2026-11-22 02:46–04:43 UTC, within the 07:46 UTC five-hour cap (2026-11-21 local).
Changes remain in the working tree. No commits, pushes, package installs, deployments or non-loopback runs were performed.

Operator instructions: [Public perpetual battle](../PUBLIC_PERPETUAL_BATTLE.md).

## Item 1 — Play log

Implemented: default-off bounded asynchronous JSONL session and match logging, monthly retention and size limits, bot exclusion, and a standard-library report tool with fixture tests, also wired into `.github/workflows/ci.yml`. Native per-player scores survive rejoins; entry baselines prevent duplicate kill/death/ejection credit. The session live test passes, including verified account kind and no persisted account IDs/tickets. Only public callsigns and gameplay aggregates are persisted. Browser/desktop classification comes from the actual peer listener (`websocket` versus TCP/UDP), with unknown protocols recorded as `unknown`; no user-agent inference is used. Guest callsign changes or cleared browser storage count as new people; callsigns cannot establish unique physical people.

## Item 2 — Public configuration

Implemented the files-only two-side random-civilization perpetual example and operator guide. Storage reuses `server-identity:/server-identity` in the actual `docker/public/docker-compose.yml` (the brief calls it `compose.yml`); logs are under `/server-identity/plays/`. Existing `[matchup]` output already includes seed, civilization IDs and names, so no duplicate matchup log was added. The artwork-backed loopback proof has advanced the full 3600-second cap without cadence sleep, observed automatic next-match start after the configured intermission, and replayed both logged civilization seeds against the legal pool.

Native constraint discovered by booting the example: DiamondRing rejects two teams (`Igc/mapmakerigc.cpp:1930-1937`). The requested three-team rig cycle cannot be copied literally. A legal DoubleRing third entry replaces it; no native map validation is changed.

## Item 3 — Bot backfill

Implemented default-off MCP driver seat backfill, protected economy/commander roles, 30-second refill grace, launch wrapper and arithmetic tests. The N=3 artwork-backed loopback proof has passed: humans replace combat seats on both sides, refill waits through the 30-second wall-clock grace, protected clients remain, and the staying human retains a seat across rotation. The manager probe doubles as its side's protected commander so it consumes one baseline seat. The initial combat fleet uses the existing small-battle meeting-engagement option and showcase finale controller, with seat replacements inheriting the departed controller slot without resetting its clock. The controller bookkeeping now retains temporarily vacant slots and handles fresh clients' local generation numbers after rotation; existing combat/research algorithms are unchanged. Bot roles are assigned in `tools/allegiance-mcp/BotManager.cs:1157-1194` (`EnsureCommander`, commander cache; combat role assignment at `:637-638`), while `BotManager.Research.cs:74,128-132` drives the same protected client's research donations and constructor orders; `ResearchCommander.cs:185-219,235-247` selects development/drone/station buckets and constructor work. Combat removal inventory excludes those sessions, miner/builder hulls, non-combat duties, foreign bots and current leaders. Protected-economy priority follows the brief's explicit exception: if only protected bots remain, humans may take the side above N. Existing protected commanders are not removed merely to claim an all-human roster. This is port orchestration; no new native admission rule is added. Backfill logons omit the legacy dummy ticket so they work through real guest-enabled identity admission. `--client-version` (wrapper argument 3) supplies the installed bot release label; omission reports unknown `0.0.0`, preserving fail-closed minimum-version checks. The N=3 live fixture enables real identity verification with anonymous admission and the public minimum-version gate. Final observed eviction times were **0.90–0.91 seconds**; refill was observed after **31.07–31.12 seconds**, including the 30-second grace and the ordinary launch/roster confirmation.

Admission caveat: native `FedSrv/fsmission.cpp:4612-4642` counts all player clients but permits two extra low-rank players and preserves a bot-authentication exception. Thus imbalance 1 does not force a new guest onto the smaller team. After the first backfill restores equal total counts, automatic selection is also a tie and can select the same side again. The live backfill proof uses an explicit ordinary side-1 selection for the second human; claiming forced steering would be false. An additional real-server admission witness checks auto-selection when totals are actually unequal, counts bot and human clients together, and exercises the two-extra-newbie tolerance followed by a real TeamBalance refusal.

## Validation

All runs used local loopback servers and the installed artwork/native corpus. .NET runs used the requested SDK executable, dedicated NuGet HTTP cache and disabled build servers.

| Check | Result |
| --- | --- |
| Final solution build | **0 warnings / 0 errors** |
| Final `tools/build-tools.sh` | **152/152 tools**, 0 warnings/errors |
| New .NET tests, each selected alone three times | **63 passed / 0 failed / 0 skipped**: 21 unique cases, comprising 16 Fast and five Live |
| Settling-window regression, additional isolated cohort | **3/0/0** |
| Python report fixture tests, three isolated runs | **9/0/0**; final CI-command recheck **3/0/0** |
| Final-source showcase never-parks, five serial runs | **5/5**; 24/30 ships reached objectives each time, with real shots, hits and kills; maximum stopped-with-target interval **2.105 s** |
| Earlier never-parks cohort | **5/5**, maximum interval **2.807 s** |
| Final full Fast sweep | **15,945 passed / 0 failed / 7 not executed**, 15,952 total; **9/9** infrastructure checks; **overall timing gate failed**, six passing cases over calibrated 11.531 s limit |
| Final sweep's six timing methods, each selected alone three times | **81/0/0**, including every theory row; all below the calibrated limit |
| Glossary / parity / public deployment static checks | **1,092 paths / 4 rows / 45 checks**, all pass |
| Shell syntax / whitespace / repository hygiene | Pass; no new media, secrets, personal emails or absolute home paths |
| Actual `docker compose config` | **Not run successfully:** Docker CLI is absent (exit 127). Both compose documents parse and are structurally identical to HEAD; changes are comments only. No package was installed. |

The real backfill fixture keeps both 30-second refill graces in real time and checks two rotations, including a refill while the registered finale controller awaits re-arm. To bound test duration it advances only the existing eight-minute discovery timer under its owner pause gates; it does not bypass station visibility or admission rules. A departure during the new-match roster-settling window retains its grace. Other Live cases prove the actual public 3600-second match cap and 30-second automatic intermission using accelerated simulation ticks, replay two logged legal civilization seeds, exercise native numeric admission and newbie tolerance, and check real WebSocket guest and verified-account session lifecycles, launch observation, score deltas and bot exclusion.

The final Fast sweep completed in **558.4 s**. Its six timing offenders are existing ledgered selection/catalog/artwork-loading families; all functional assertions passed. The earlier sweep had **15,943 passed / 1 failed / 7 skipped**, with the known Nanite wire-credit failure and two timing overruns. Those three methods passed **39/0/0** on three isolated reruns. Both sweeps and their reruns are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). No scoring, repair, profile, assertion, timing threshold, tier or quarantine policy was changed. The timing gate remains red; isolated passes are triage evidence, not a green full-sweep claim.

Raw evidence remains ignored under `.scratch/`: `p1071-solution-build.log`, `p1071-tools-build.log`, `p1071-new-tests/`, `p1071-backfill-unit-final/`, `p1071-new-tests-final-summary.json`, `p1071-settling-window-alone/`, `p1071-never-parks-final/`, `p1071-never-parks-final-results.json`, `p1071-fast/`, `p1071-fast-isolated/`, `p1071-fast-final/` and `p1071-fast-final-isolated/`.

No implementation item remains unfinished. The outstanding validation limits are the existing Fast timing failures and the missing Docker CLI. Deployment, external-host operation and capacity tuning remain outside this task.

## Native intent

Reuse native numeric side counts and exceptions (`FedSrv/fsmission.cpp:4612-4642`), creation-time random draws (`FedSrv/fsmission.cpp:175-198`, `Igc/igc.h:1280-1281`), kill/death/ejection counters (`Igc/igc.h:5940-5943`, port `PlayerScore.cs:119-135`), old-player score recovery (`FedSrv/fsmission.cpp:378-401`), voluntary LOGOFF (`FedSrv/FedSrv.CPP:5787-5802`) and match reset/result ordering (`FedSrv/fsmission.cpp:3225-3263,3427-3454`). Parity inventory: `docs/parity/p1071-perpetual-battle.csv`; these rows reuse existing native behavior or identify modern orchestration, and claim no new native rule.

## Modern approach

Play analytics, local log retention and external client bot backfill are port orchestration, disabled unless configured. Bots remain external clients. No deployment or remote run is performed.
