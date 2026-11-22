# P1072 — WebSocket bots

The MCP driver now supports explicit TCP, UDP and WebSocket selection, deterministic mixed fleets, strict opt-in CA trust and per-transport load records. No commit, push, package installation, deployment or remote load run was performed. All edits remain in the working tree.

The report was created before implementation on 2026-11-22. Base `bc8d5d34` includes the required P1071 merge. Work is within the five-hour cap. The timed comparisons and validation are complete. The Fast sweep remains red on four already-ledgered cases and six timing overruns; details and isolated follow-ups are below.

## 1. Transport selection

- `BotTransportOptions.cs` accepts `--transport`, `--ws-url`, `--ws-origin`, `--ws-fraction`, `--ws-ca-file` and the five corresponding `ALLEGIANCE_BOT_*` settings. CLI values win; the existing `ALLEGIANCE_TRANSPORT_PROTOCOL` remains a fallback, and an unconfigured driver remains TCP.
- `BotManager.Transport.cs` assigns by stable bot index, reserving probe/commander indices and striping worker indices. Choice has no random-name or connection-scheduling dependency and needs no growing history cache. The probe, plain idle/weave peers, combat seats, commander creation, upgrades and P1071 backfill all pass through it.
- Worker startup and worker recovery explicitly forward every transport option through `ProcessStartInfo.ArgumentList`; skirmish and supervisor entry points pass the same options. Existing P1071 single-driver backfill restrictions remain.
- ConnectionSession peers now use the normal owned connect/reconnect lifecycle. Frame-arrival observation stays before the decode gate. Reconnect freezes endpoint, Origin and the certificate callback. The mixed proof changes public WS properties after initial connection and resumes twice without changing route.
- Repeated desktop resumes now retain the initial canonical callsign, matching the browser's existing behavior; a resume greeting must not become the next logon's name. Parity tracking: `docs/parity/p1072-websocket-bots.csv`.
- The CA callback builds an X509 chain with `CustomRootTrust` against exactly the configured certificate, requires server-auth usage, checks the final root bytes and preserves TLS name/expiry/signature checks. Certificate downloads and internal-CA revocation lookup are disabled for this opt-in policy. Default system trust remains unchanged. Generated private keys stay in test memory; the temporary public certificate file is deleted.
- Route logs and `list_active_bots.transport` identify the choice; `get_perf_stats.transports` includes control peers and reconnect counts. Actual WS protocol is the URI's `ws` / `wss` scheme. Loopback identity fixtures also require a loopback WS route.

## 2. Measurement

Existing EgressAccounting and TickProfiler own the new records; there is no separate metrics service. `ALLEGIANCE_BENCHMARK_METRICS=1` enables transport frame sends, bytes, drops, per-peer queue-byte/entry high-water marks and independent disconnect-reason drains. Idle and fragment expiry get measurement labels while existing security disconnect reasons and behavior stay unchanged. TCP read deadlines get the same distinction.

Tick windows include a one-microsecond histogram, so `tools/ws-load-report.py` merges actual samples for p50/p95/p99 instead of averaging percentiles. Bot records include validated logon duration, local flying-pose arrival age, read age and reconnect attempts. Docked control peers have no flight-pose sample; plain idle/weave peers report ship-update-frame age. These ages are delivery gaps, not one-way network latency or rendering lag.

The reader is standard-library Python, prints a TCP/WS table and supports `--json`. Its fixtures cover mixed input, window selection, histogram merging, missing samples and malformed records. Guarded-spawner fixtures cover exact remote acknowledgments, environment routes and paths with spaces, and parse both Compose files with the existing P1061 YAML reader.

## 3. Loopback proofs

Artwork-backed tests boot a real server, ordinary P1071 backfill/showcase clients, two sides, an allowed HTTP loopback Origin and a loopback WS listener. No strategy, simulation, native game rule or shipped security default changed. The showcase never-parks 5× gate therefore does not apply; no training/HUD code changed.

| Proof | Result |
|---|---|
| All-WS 2v2 | Four WS sessions; real combat shots; existing 120-second simulated match cap ends the match; auto-next starts the second match. |
| Mixed 10v10 | Exactly ten TCP and ten WS peers, including protected control seats; one combat client per route resumes twice; every route stays unchanged. |
| Admission | Real upgrades admit 64 under the live per-IP default, and 100 with test limits 128 live / 256 attempts. The source ledger accepts 128 attempts and refuses the next; raised limits accept a full 200-attempt initial/reconnect wave. |
| CA pin | Real manager logon over generated-CA WSS succeeds; another CA and system-default trust refuse it with WebSocketException. Name mismatch and missing certificates are refused. |

For 100 WS bots from one source, use **128 live connections/IP and 256 attempts/IP/window** in the experiment config. Default 64 live stops a simultaneous fleet at 64; default 128 attempts cannot cover 100 logons plus 100 immediate reconnects. Control peers and retries need headroom. Global and pending limits remain enforced; there is no bot exemption. Only the existing exact trusted-proxy physical-IP exception remains.

The metrics-enabled mixed proof produced this real table (about 14 seconds including admission and repeated reconnects; it is a functional proof, not the timed A/B):

| Metric | TCP | WS |
|---|---:|---:|
| Unique clients | 10 | 10 |
| Sent frames | 24,178 | 24,541 |
| Sent carrier bytes | 1,407,914 | 1,387,715 |
| Dropped frames | 0 | 0 |
| Queue bytes high-water | 25,150 | 21,905 |
| Queue entries high-water | 372 | 354 |
| Logon p50, ms | 21 | 22 |
| Flying update age p50 / p95 / p99, ms | 10 / 24 / 26 | 10 / 24 / 26 |
| Reconnect attempts | 2 | 2 |
| Forced test disconnects | 2 ReadErrorOrEof | 2 ReadErrorOrEof |

### Timed lane comparison

Completed serially: all-TCP then all-WS, 10 total seats per side, 300 measured wall seconds each after combat warm-up. Seed 1072, Brawl, fixed civs 25/25, identical server settings and 50 ms ticker sleep; Linux x86_64, SDK 10.0.404. Server and bots share one lane process/machine. These are **lane numbers, not rig numbers**. The raw-WS A/B has no TLS hop; the separate CA proof tests TLS correctness.

| Metric | All TCP | All WS |
|---|---:|---:|
| Measured wall seconds | 300 | 300 |
| Connections | 20 | 20 |
| Measured ticks | 5,730 | 5,764 |
| Tick p50 / p95 / p99, ms | 2.033 / 3.505 / 5.908 | 1.885 / 2.597 / 4.200 |
| Sent frames | 1,036,224 | 1,021,877 |
| Sent carrier bytes | 83,177,849 | 79,446,733 |
| Dropped frames / bytes | 0 / 0 | 0 / 0 |
| Queue bytes high-water | 5,423 | 5,181 |
| Queue entries high-water | 32 | 31 |
| Logon p50 / p95 / p99, ms | 42.000 / 76.000 / 112.000 | 40.000 / 62.000 / 121.000 |
| Flying update age p50 / p95 / p99, ms | 29.000 / 50.000 / 96.000 | 27.000 / 49.000 / 143.000 |
| Flying pose-age samples | 5,309 | 5,256 |
| Reconnect attempts | 0 | 0 |
| Disconnect reasons | {} | {} |

Machine-readable evidence: [loopback-summary.json](WEBSOCKET_BOTS_P1072_assets/loopback-summary.json). Raw logs remain in the lane temporary evidence directory; no artwork paths or runtime log contents are included in the saved JSON. Both comparison tests passed in about 5m22s including warm-up.

Sends/drops count carrier frames and bytes include carrier framing; they measure socket acceptance, not delivery or encrypted-proxy overhead. Queue high-water marks are individual-peer maxima, not fleet totals. Tick percentiles describe the entire server. Independent battles can differ in combat timing, so one small localhost pair does not establish a rig performance ranking.

## 4. Rig-ready files and guide

`docker/rig/compose.websocket-experiment.yml` is an explicit `-f`, separate-project experiment. No existing rig Compose override was present in this checkout; none was created or overwritten. The Caddy sidecar uses the public stack's image family and one-hop header overwrite policy (`docker/public/`). It has an internal CA and a fixed exact proxy address in the documentation range. The server's 6080 port stays private; host port 8443 supplies TLS. Experiment source limits are 128/256, TCP has comparison headroom, and benchmark/civ seeds are fixed at 1072.

`bots-spawn-perpetual.sh` and the new `bots-spawn-load.sh` accept all transport options and refuse non-loopback TCP or WS targets, including environment-supplied routes, without exact `--i-mean-it-host HOST` acknowledgments. The ordinary perpetual script keeps its five-seat default; the load wrapper uses ten seats per side. Root Compose comments and `docker/README.md` document the bot-host and experiment settings.

`docs/WEBSOCKET_LOAD_TEST.md` covers CA certificate export, TCP → WS → 50/50 runs, 100-client budgets, later VPS use, raw-log selection and interpretation. Browser WASM frame time and rendering cost still require real browsers and a small Playwright fleet. No transport performance optimization was applied.

## Gates and limitations

- Solution build: **0 warnings / 0 errors**, including browser projects (latest build 9.01 s).
- `tools/build-tools.sh`: **152/152 tool projects**, all 0 warnings / 0 errors; invoked through the installed SDK with build servers disabled.
- New .NET cases: **24 × 3 alone = 72 passed**, 33 isolated invocations; targeted cleanup checks repeated afterward.
- Python tests: **9 × 3 alone = 27 passed**; additional final suite 9/9.
- Existing transport/security regressions: **210/210 passed**, including unchanged P1048/P1052, TCP keep-alive and inbound-deadline tests.
- Existing watchdog/supervisor/reconnect regressions: **53/53 passed**.
- Glossary paths: **1,099 checked**; parity CSV: **2 rows validated**; diff whitespace and shell syntax checks passed.
- Static deployment validation: existing P1061 checker **45 checks passed**, plus the new experiment-shape fixture.
- Timed comparisons: **both passed**, 300 measured wall seconds each.
- Full Fast sweep: **15,971 cases in 659.9 s: 15,960 passed / 4 failed / 7 not executed**. Infrastructure and coverage checks passed; six executions exceeded the calibrated 11.568 s limit. This sweep is **not green**.
- All nine distinct failed/over-limit cases were rerun **3× alone**: eight cases passed 3/3 and their timings returned under the limit. FullSession failed all three lane isolates at its already-ledgered side-export ordering assertion (line 229, expected -2 / actual 0).
- An untouched P1071 base source export (`bc8d5d34`), rebuilt in temporary storage, reproduced the identical FullSession failure: **pass / fail / pass** (2.336 / 1.553 / 2.010 s). No product/test changes were made for these baseline failures. Exact outcomes and timings are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md).

**Unfinished checks:** the Fast gate is not clean for the baseline reasons above. Actual Compose/Caddy runtime validation is also unavailable: `docker compose config --quiet` was attempted for root and experiment Compose files, but this lane has no Docker executable. Package installation is forbidden. YAML/config/Caddy shape checks passed, but actual Docker Compose/Caddy runtime validation remains for an equipped operator. No rig, lane-bots or VPS action was attempted.

## Native intent

Native has no WebSocket transport or MCP bot driver. These bots stand in for DirectPlay joining (`clintlib/clintlib.cpp:1317-1320`) and hosting (`FedSrv/AdminServer.cpp:463-470,525-532`), using the port's existing FedPackets and game rules. The initial canonical-name intent is `clintlib/appmsg.cpp:39-40`.

## Modern approach

Use explicit browser-compatible routes, a narrow opt-in CA root, deterministic mixed fleets and existing profiler/egress infrastructure. Prove ordinary client lifecycle and security on loopback, preserve deployment defaults and prepare separately approved TLS/proxy load experiments.
