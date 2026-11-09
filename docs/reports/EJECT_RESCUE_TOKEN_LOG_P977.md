# P977 — native eject rescue targets and reconnect-token log redaction

## Scope and method

P971-04 and P971-07 only. The native references below were re-read at the cited lines on 2026-11-08. No server, wire-size, reconnect-thread-ownership, or HUD-visual change is included. `local.env` is present and supplies the artwork/native corpora for live tests.

## Native eject rule, re-derived

- On its own `EJECT`, native calls `FindTarget(pShip, c_ttStation | c_ttFriendly | c_ttAnyCluster | c_ttProbe, currentTarget, pcluster, ejectPosition, NULL, c_sabmRescue | c_sabmRescueAny | c_sabmLand)` and engages autopilot (`clintlib/appmsg.cpp:230-237`, call at `:233`). The lifepod friendly-base command uses the same capability mask and adds probes (`WinTrek/WinTrek.cpp:10219-10228`).
- `FindTarget` and `FindableModel` default ally mode to 1, explicitly own side plus allies (`Igc/igc.h:4628-4644`). `FindableModel` classifies own-side and allied models as `c_ttFriendly` in that mode (`Igc/common.cpp:269-273`). Its allied-station exclusion mask contains only restart/unload and, unless allied rip is enabled, ripcord (`Igc/common.cpp:305-326`), so it does not exclude the eject mask.
- A probe carrying `c_eabmRescueAny` satisfies the side test regardless of owner when the requested abilities include RescueAny (`Igc/common.cpp:282-286`). Capability matching is any-bit, not all-bits (`Igc/probetypeigc.h:143-146`). Other enemy probes and every enemy station remain ineligible.
- The probe capability query receives the same Rescue/RescueAny/Land mask. Thus the `0x20` Land/ShootStations alias also qualifies a friendly probe; only RescueAny bypasses the friendly-side requirement (`WinTrek/WinTrek.cpp:10219-10228`; `Igc/common.cpp:282-286,329-333`).
- With no usable current target, native adds `c_ttNearest` (`Igc/common.cpp:487-493`) and uses strict squared-distance replacement (`Igc/common.cpp:568-573,606-625`). Any eligible target in the current cluster ends selection. Otherwise it walks known warps breadth-first by cluster hop count, returns the first cluster bracket with a target, and measures candidates in a remote cluster from the destination-warp mouth (`Igc/common.cpp:648-655,658-729`). The world export is the port client's visibility boundary for models/warps.

## Changes

### Eject target selection

- `src/Allegiance.ClientCore.Game/Game1.Eject.cs:25-31` gives probes the same any-bit Rescue/RescueAny/Land mask native passes them. At `:58-100`, own/allied stations and probes use `ClientWorld.AreAllied`; only a RescueAny-capable probe bypasses that side gate. Enemy stations and ordinary enemy probes remain ineligible, and strict `<` preserves nearest-wins behavior.
- `Game1.Eject.cs:103-139` returns a current-cluster match first, otherwise performs a known-warp BFS and measures remote candidates from the destination-warp mouth. The real eject consumer passes `mine.SectorId` at `:163-166`. The optional no-cluster form remains only for older headless callers.
- The new live proof exposed one necessary companion fix: `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:3453-3469` had rejected allied stations from real bay-approach steering even after selecting one. It now applies native's own/allied `InGarage` gate (`Igc/shipIGC.cpp:732-735,843-855`). Before this correction the pod reached the allied hull but ground against it until oxygen expiry; afterward it enters the bay and the server performs the allied-lifepod re-home.
- Tests are `tests/Allegiance.ClientCore.MonoGame.Tests/P977EjectAlliedRescueTests.cs:35-301` and `P977AlliedEjectLiveTests.cs:25-178`. The live test uses three sides, a real alliance snapshot and allied-visibility exports, actual ejection, production autopilot controls and accepted native SHIP_UPDATE poses. It asserts the allied-dock receipt, own-base re-home, and arrival before oxygen expiry.
- The rules and evidence are tracked in `docs/parity/p977-eject-rescue.csv`; the existing lifepod glossary entry points to the new proofs/report.

### Reconnect-token logs and crash reports

`src/Allegiance.ClientCore/Session/ConnectionSession.cs:1318-1329` implements the server's non-reversible 16-bit `TokenTag` algorithm (FNV-1a over the four token bytes, avalanche, low 16 bits; `src/Allegiance.Server/Server.ReconnectGrace.cs:192-199`). The drop and success messages now emit that tag at `ConnectionSession.cs:1348-1349,1439-1440`; neither prints a token value. `CrashLog.Install` tees the same console stream to the rolling log and crash ring (`src/Allegiance.ClientCore.Game/Diagnostics/CrashLog.cs:91-108,134-148`), so removing the values at their two sources protects all three sinks without changing the logger. The real drop/park/resume test captures that stream and rejects hexadecimal and decimal token renderings while requiring the exact server-compatible tag (`tests/Allegiance.ClientCore.Tests/P813cReconnectClientTests.cs:126-160,181-190`).

## Failing-first evidence

- `P977EjectAlliedRescueTests` on the unchanged implementation: **3 passed / 7 failed / 0 skipped**. Allied stations/probes, any-side RescueAny, nearest selection, and the friendly Land-alias probe failed as expected. After adding the two native-ordering cases (with only a behavior-neutral optional parameter present), the complete failing-first result was **3 passed / 9 failed / 0 skipped**.
- `P813cReconnectClientTests.Auto_reconnect_resumes_the_same_ship_and_resyncs_the_world`, extended to capture the real drop/park/resume console stream: **0 passed / 1 failed / 0 skipped** on the unchanged implementation; the assertion found the exact `0xXXXXXXXX` resume token.
- `P977AlliedEjectLiveTests` initially selected the allied station correctly but failed to dock: the separate exact-own-side bay-approach gate left the pod grinding at the allied hull until oxygen expiry. This was the failing-first evidence for the `Game1.FlightInput.cs` companion correction; the corrected test passes.

## Secrets review

The client audit covers `src/Allegiance.ClientCore`, `src/Allegiance.ClientCore.Game`, and `src/Allegiance.ClientCore.MonoGame`, including reconnect nonces, lobby identity/session bearers, join tokens, logon `ZoneTicket`, game passwords, CD keys, Steam tickets, request URIs, and exception/log call sites. Searches included case-insensitive `password|secret|token|ticket|credential|cdkey|steamid|drmhash|auth|bearer|nonce|apikey|privatekey`, all `Console.Write*` sites, and a multiline statement scan for credential fields. The two reconnect statements were the only direct credential-value emissions found. The post-fix scan finds only their `TokenTag(_resumeToken)` calls (a tag, not the value). Password logs report only requirement/remember-state; identity/join tickets and session bearers are assigned to request payloads/headers but not printed; `LobbyGameClient` logs a tokenless catalog URI. No additional production redaction was needed.

Historical rolling logs/crash reports created before this fix can already contain resume tokens and should be treated as sensitive, rotated/deleted when shared, and followed by a fresh reconnect token/session. This task does not rewrite existing user files.

## Verification

Counts below use **Passed / Failed / Skipped**. A final focused rerun after the last defensive warp-data/comment cleanup also passed **16 / 0 / 0** target-selection cases and **1 / 0 / 0** allied live case.

- New eject unit tests, 3× alone: **36 / 0 / 0** total (12 each run).
- Allied live eject/autopilot/dock test, 3× alone: **3 / 0 / 0** (one each run; artwork present, so no skips).
- P813c client reconnect tests, including the extended park/resume log assertion, 3× alone: **9 / 0 / 0** (three each run).
- P813c Core reconnect-grace tests, 3× alone: **42 / 0 / 0** (14 each run). An initial runner invocation with missing restore assets returned without executing tests; those empty invocations were discarded and rerun after restore, never counted as passes.
- Updated pre-existing `EjectRescueTargetTests`, 3× alone: **12 / 0 / 0** (four each run). Its enemy-RescueAny expectation was contrary to the native rule and failed in the broad sweep before correction.
- Whole-solution `~/.dotnet/dotnet build AllegianceCore.slnx`: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **123 / 123 tool projects built**, every project 0 warnings / 0 errors.
- Parity/glossary checks: `scripts/parity-scorecard.py docs/parity/p977-eject-rescue.csv` validated four rows; `tools/check-glossary-links.py` checked 681 paths successfully; `git diff --check` clean.
- One `tools/run-tests.sh --tier fast`: **13,624 / 5 / 3** out of 13,632 cases; nine infrastructure checks passed. `local.env` was present, so the only **Skipped count is 3**, the optional absent private Meshy-pack cases. One failure was the outdated RescueAny expectation above. The other four were isolated three times each: HUD formatted-readout allocation **3 / 0 / 0**, miner cycle **3 / 0 / 0**, kill-bonus receipt **3 / 0 / 0**, and side-select deadline **0 / 3 / 0**. The side-select 32.x-second boundary is already ledgered and unchanged; the three pass-alone failures are load flakes. Full details and timing offenders are recorded in `docs/TEST_FLAKES.md` (P977 section). This was one full-suite invocation, not a claim of a green broad suite.
- Showcase never-parks 5×: **5 / 0 / 0**, matching P961's recorded unchanged-base **5/5** rate. Walls were 168.5 / 466.9 / 151.8 / 172.4 / 169.4 seconds; each reached 24/30 with real combat. Longest stopped-with-target intervals were 2.004 / 1.812 / 2.007 / 2.205 / 1.905 seconds, all below the five-second bound.

Raw TRX/logs are under the ignored `.scratch/p977/` directory (`repeat-trx/`, `nontask-trx/`, `showcase-trx/`, build logs, and failing-first receipts). The broad-suite receipt is `TestResults/tiers-20261109T082904.709886Z/summary.json`.

## Follow-up: resume-token upgrade (not implemented here)

Use a versioned reconnect extension carrying at least a 128-bit token from `RandomNumberGenerator`, never `Random.Shared`, and rotate it after every successful resume. The server should return the replacement only over the authenticated resumed connection, invalidate the old token atomically, retain name+token binding, and keep only a bounded hash/tag for diagnostics. This changes the port-only `LOGONREQ.ResumeToken` extension and requires a corresponding resume-ack extension plus server persistence changes. Mixed-version peers must negotiate capability/version: old clients and servers understand only the current 32-bit value, so silently widening the existing field would break wire compatibility. Keep the legacy 32-bit path only during an explicit compatibility window, or reject resume cleanly when both sides cannot negotiate the new extension; never truncate a 128-bit token to fit the old field.

## Native intent

Native sends a lifepod to the nearest eligible rescue point using own-side plus allied targeting, the side-independent RescueAny-probe exception, and current-cluster then known-warp breadth-first ordering. Native has no reconnect-grace bearer token.

## Modern approach

Preserve that native targeting rule while treating reconnect credentials as secrets: emit only a correlatable non-reversible tag in console, rolling-log, and crash-ring paths. A future negotiated wire extension should provide CSPRNG entropy and rotation without ambiguous legacy decoding.
