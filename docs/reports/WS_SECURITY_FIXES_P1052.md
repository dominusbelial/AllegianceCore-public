# WebSocket security fixes — P1052

Status: **items 1–5 complete**. Security fixes, documentation and required gate runs
are finished. The full Fast and broad regression sweeps retain the existing failures
recorded below; they are **not green**. Changes remain in the working tree; no commits
or pushes.

| Item | Status |
|---|---|
| F1 trusted proxy physical admission | Done; final targeted tests pass 3× alone |
| F2 IPv6 /64 admission and credential budgets | Done; final targeted tests pass 3× alone |
| F3 verified public secure listing routes | Done; final targeted tests pass 3× alone |
| F4 desktop redirect refusal | Done; final targeted tests pass 3× alone |
| Proofs, regression gates and documentation | Done; all 89 cases pass 3× alone and in Fast; gate exceptions recorded below |

## Changes and evidence

- F1: only the physical `WebSocketAdmission` receives exact configured proxy exemptions.
  Those sockets still spend global attempt, live, pending and tracked-source budgets.
  Forwarded sources never receive exemptions; untrusted forwarding stays ignored.
  This avoids new configuration knobs and preserves meaningful per-client limits.
- F2: shared `SourceAddressBudget.Key` masks IPv6 to /64 (scope IDs do not split budgets),
  maps IPv4-mapped IPv6 to IPv4, and leaves IPv4 unchanged. Both admission layers and
  `Server.CredentialLimits` use it. PeerAddress and exact proxy trust retain actual addresses.
- F3: verified registrations only; WSS required, public host required except for a
  loopback registrant's local-development WSS host. Creation and refresh drop invalid
  optional values without rejecting registrations. Browser and desktop listing ingestion
  use the strict listing rule; trusted configuration retains WS loopback support.
  DNS names are checked syntactically without lookup: enrollment trusts an operator's
  route; it does not prove DNS ownership or prevent DNS rebinding. Local names and
  private, loopback, link-local and multicast IP literals are refused. The earlier
  loopback browser-listing
  proof no longer qualifies, including with local WSS: the UI retains its existing
  loopback-only join gate while listing input requires a public host. No public
  join or local listing bypass was enabled.
- F4: desktop uses `HttpMessageInvoker` / `SocketsHttpHandler.AllowAutoRedirect=false`;
  initial join and ordinary ConnectionSession resume share the path. The browser
  connect path remains
  unchanged: its WebSocket opening handshake uses redirect mode `error`.
- Original seven RWS proof names and assertions remain unchanged. The F4 loopback
  fixture now constructs its endpoint from trusted configuration, so it still reaches
  the redirect assertion under the stricter listing contract.

| Finding | Implementation locations |
|---|---|
| F1 | `src/Allegiance.Server/Transport/WebSocketServerTransport.cs:44`; exact proxy exemptions and retained global/pending/table caps in `src/Allegiance.Server/Transport/WebSocketAdmission.cs:15,21,33,35,51,55` |
| F2 | Shared /64 rule `src/Allegiance.Core/Transport/SourceAddressBudget.cs:11`; both admission lookups `WebSocketAdmission.cs:22,52`; TCP/WS credential key `src/Allegiance.Server/Server.CredentialLimits.cs:82` |
| F3 | Registration/refresh `src/Allegiance.Lobby/ServerRegistry.cs:150,175,439`; listing/config rules and host filter `src/Allegiance.Core/Transport/WebSocketEndpoint.cs:14,16,23,32,55,59`; desktop ingestion `src/Allegiance.ClientCore.Game/ServerListing.cs:81`; browser DTO/JSON ingestion `src/Allegiance.ClientCore.Browser/Pages/BrowserServer.cs:11` |
| F4 | Desktop invoker `src/Allegiance.Core/Transport/WebSocketTransport.cs:54`; unchanged browser connect at `:52`; session resume uses the same transport construction path |

Final-source solution build: **0 warnings / 0 errors**. Final security inventory:
**89 passed / 0 failed / 0 skipped**, then **89/89 ×3** in 54 fresh method-isolated
processes (18 methods; each theory includes all its rows). This includes all seven
original RWS proofs and all 82 new cases: Core 19, lobby 50, desktop 10 and
source-compiled browser 10. Original RWS names/assertion lines were compared with
HEAD and match exactly. The common `localhost.localdomain` /
`localhost6.localdomain6` aliases are included in the private-host rejections.

An additional offline URI probe found deferred `Uri.IdnHost` conversion can throw
`UriFormatException` after `Uri.TryCreate` succeeds, or produce invalid DNS labels
from Unicode. Parsing now contains that as the existing `ArgumentException` invalid
endpoint contract and validates converted labels. New public/loopback registration
and both client JSON-ingestion rows prove omission without failing the listing;
A valid internationalized host remains supported. All 89 final cases passed
3× alone after this correction.

## Verification

| Check | Passed / Failed / Skipped |
|---|---|
| Full solution (desktop + both browser projects), final source | **0 warnings / 0 errors** |
| Seven original RWS proofs + 82 new cases, three times alone | **89 / 0 / 0 ×3**, 267 case executions in 54 fresh method-isolated processes |
| P1048 listener/mux/authority suite | **135 / 0 / 0** |
| P1048B client/route/receive/join suite | **49 / 0 / 0** |
| Broad Core server/transport/identity/credential/R1/reconnect selection | **820 / 1 / 2** (823 total), including the final source |
| Transport namespace within that selection | **389 / 1 / 0**; sole failure is the existing Perf stalled-stdout test below |
| Credential guard + dispatcher / relays / queue deadlines | **15 / 0 / 0**, **8 / 0 / 0**, **14 / 0 / 0** |
| P808 identity loopback / P813c reconnect | **3 / 0 / 0**, **16 / 0 / 0** |
| Desktop P897 / connecting UI | **774 / 0 / 0**, **12 / 0 / 0** |
| ClientCore ownership / receive-world containment selection | **20 / 0 / 0** |
| Entire lobby suite, including routes and identity | **262 / 0 / 3**, optional PostgreSQL integration skips |
| Entire browser seam suite, including source-generated ingestion | **33 / 0 / 0** |
| `tools/build-tools.sh --drop-bins` | **145 tool projects passed, 0 warnings / 0 errors** |
| Server Native AOT publish / native executable proof | **Release linux-x64 passed, 0 build/trim/AOT warnings; signed logon, side selection, ping and replay refusal passed** |
| `tools/run-tests.sh --tier fast --parallel 1 --no-build` | **15,453 / 4 / 7**, 15,464 total in 870.1 s; no timing offenders; 9/9 infrastructure checks |
| Glossary / parity / whitespace | Passed; 964 glossary paths, no new native-rule credit |
| Docker Compose config | Not run: Docker is not installed; mounted JSON parses and listener remains explicitly off |

`ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout`
failed in the broad sweeps with “peers must keep receiving per-tick updates while
stdout is stalled.” It passed **3/3 alone** on final source (0.447 / 0.462 / 0.461 s).
This existing Perf/shared-console family is already ledgered; the P1052 observation
is appended to `docs/TEST_FLAKES.md`. The broad gate is **not claimed green**.

The pre-IDN Fast sweep covered **15,451 cases in 865.7 s**: **15,441 passed /
3 failed / 7 skipped**, with no timing offenders and 9/9 infrastructure checks.
It preceded the deferred-IDN fix, which prompted the final-source sweep below. All 76
then-existing security cases passed. Its three gameplay observations were rerun
three times alone on the corrected source:

| Case | Sweep observation | Three isolated outcomes (seconds for the observed row) |
|---|---|---|
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | Observer never latched the death after 2,500 ticks | pass / pass / pass (1.345 / 1.321 / 1.338) |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump(enabled: False, dilation: "off")` | 14.900 outside [14.94, 15.06] | pass / **fail / fail** (1.876 / 1.851 / 1.897); all four theory rows selected per process |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | 9.05 s roster time outside [4, 8] | pass / pass / pass (0.519 / 0.542 / 0.556) |

These are existing TCP gameplay/clock fixtures; P1052 changes no simulation,
countdown, roster or death-latch rule. The P660 observation remains unresolved,
including the isolated failures, and is retained in `docs/TEST_FLAKES.md`.

## Final Fast sweep and isolated failures

The final-source sweep covered **15,464 cases in 870.1 s**: **15,453 passed /
4 failed / 7 skipped**. All **89 P1052 security cases passed**, including every
original RWS proof. There were **no timing offenders** against the calibrated
11.540 s limit, and the infrastructure checks passed **9/9**. Projects ran
serially. The seven skips are the existing project cases, with no new skips.

Every failing method was rerun in three fresh processes with artwork. Parameterized
methods retained their theory rows; the table reports the failed row's outcome.

| Exact case | Fast failure | Three repetitions alone (seconds) |
|---|---|---|
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | Observer never latched death after 2,500 ticks | pass / pass / pass (1.350 / 1.351 / 1.323) |
| `P590PodRescueTests.Diverter_intercepts_and_rescues_a_moving_pod` | Moving pod not intercepted after 800 ticks | pass / pass / pass (0.387 / 0.480 / 0.413) |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Return distance 229.7 → 217.9 did not meet the fixture's bound | pass / pass / pass (1.430 / 1.450 / 1.395) |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 5)` | 5.0000000000000009 outside [4.9000000000000004, 5] | **fail / fail / pass** (0.187 / 0.146 / 0.207); all three theory rows selected |

P739 remains the existing one-ULP timer-bound observation, including its isolated
failures. The earlier P660 clock observation also remains unresolved. These results
are in `docs/TEST_FLAKES.md`; no timing bound, assertion, quarantine, tier or game
rule was changed. Passing isolated retries do **not** make the failed sweep green.

[Sanitized build, suite, AOT, full Fast and isolation accounting](WS_SECURITY_FIXES_P1052_assets/verification.json)
contains both sweep summaries and every isolated outcome, including failures.

The two Core skips are the existing P760 dock-gate cases. No assertion, timeout,
quarantine, tier or numeric limit was changed to hide a failure.

Builds/restores use the installed SDK and preinstalled local NuGet package source,
a writable task HTTP cache, disabled build servers/node reuse/shared compilation.
Raw logs and TRX files stay in ignored `.scratch/p1052/`; sanitized final accounting
is linked above. Fast uses `--parallel 1 --no-build`; its infrastructure
checks passed. Calibrated per-case timing limits were 11.522 s for the earlier
sweep and 11.540 s for the final sweep. No public hosts, deployment or remote rigs were used.
No HUD/renderer or training behavior changed, so no visual/training-change gate applies.

## Defaults before / after

All numeric configuration defaults remain unchanged. The proxy exception is limited
to physical
per-source accounting; the effective client guard is never exempted.

| Control | Before | After |
|---|---|---|
| Listener / bind / port / path | off / 127.0.0.1 / 6080 / `/game` | Same |
| Connections, each admission layer | 1024 global, 64/IP | 1024 global, 64/source; physical known-proxy sockets exempt only from 64/source |
| Pending handshakes | 256/layer | Same, including known proxies |
| Attempts, each layer | 2048 global, 128/IP per 60 s | Same; physical known-proxy sockets exempt only from 128/source |
| Tracked sources | 4096/layer, bounded and fail closed | Same |
| Source key | IPv4 unchanged, mapped IPv4 normalized, full IPv6 | IPv4 unchanged, mapped IPv4 normalized, IPv6 /64 |
| Handshake / authentication | 10 s / 60 s absolute | Same |
| Fragment progress / message / idle | 5 s / 30 s / 30 s | Same |
| Receive caps | 16 KiB pre-logon; min(64 KiB, TCP cap) authenticated; 256 segments | Same |
| Outbound server queue / entries | 32 MiB / 8192, includes in-flight | Same |
| Guaranteed age / send / close | 60 s / 30 s / min(2 s, send deadline) | Same |
| Client queue / entries / receive | 1 MiB / 256 / 64 KiB | Same |
| Client send / guaranteed age / receive progress / message | 30 s / 60 s / 5 s / 30 s | Same |
| Native packet / ingress bounds | CRC, cbmsg, 256 messages; 4 MiB ingress reservation, 96 B/event, 2048 events/peer, 8192/tick, 256/peer/tick | Same |
| Endpoint syntax / cap | 2048 characters; no userinfo, fragment, controls or backslashes | Same caps; converted IDN/DNS labels validated, deferred URI format errors become invalid endpoints |
| DNS-name syntax | URI parser only | Converted ASCII labels 1–63 characters, hostname ≤253; valid IDN supported; listing requires multiple labels and excludes local suffixes |
| Configuration shape bounds | 1–64 Origins; at most 64 known proxy IPs; simple path length 2–128 | Same |
| HTTP header / count / body | WS 16 KiB / 32 / 16 KiB; lobby body 16 KiB | Same |
| Origin / WS protocol | Required exact allowlist (empty default); only `allegiance-fed-v1`, binary | Same |
| Proxy forwarding | Empty known list; explicit IPs only, one symmetric hop; no forwarded Host | Same; no prefix-based trust |
| Plaintext development | Explicit opt-in, both endpoints loopback, no known proxies | Same |
| WS local privilege | Always false | Same |
| Credential guesses | Enabled; 3 failures/connection; 5/source/60 s; 15 s doubling to 300 s; table 4096 | Same values; IPv6 source grouped /64 for TCP and WS |
| Lobby HTTP rate limits | Per-IP/min: global 120; register, identity, find-player 30; chat 60; punch poll 90; queue 0 | Same |
| Lobby listing caps | 384 listings, 4/address; 90 s lease | Same |
| Identity POST CSRF / cookies | Own Origin when supplied or cookies present; JSON required; HttpOnly, SameSite Strict; HTTPS Secure | Same |
| Optional listing route | Syntax-valid WS loopback or WSS from any registrant | Verified only, WSS; public host except loopback-source WSS development; invalid dropped |
| Client listing route / configuration | Same rule: WSS or WS loopback | Listing public WSS only; configuration retains WSS or WS loopback |
| Desktop handshake redirect | Followed | Refused, including reconnect |

No numeric admission or rate limit was raised. CSRF, Origin, body caps and proxy
trust are unchanged. Only physical trusted-proxy per-IP accounting changes as F1
requires; effective client caps remain unchanged or tighter. No new environment variable, port, service, game rule or wire field was added.

## Native AOT evidence

The published server is a stripped x86-64 ELF; `ldd` lists only libc, libm and
the platform loader. No managed DLLs are present in the publish directory.
The existing `tools/p1048-ws-proof` exercised that exact binary on loopback with
an in-memory P-256 key: canonical identity, side selection with the same ship ID,
ping echo and single-use ticket replay refusal all passed. Its exact child PID
received SIGTERM and exited cleanly (5 ticks, zero overruns). GCC was selected
explicitly for the lane's installed native linker. No TLS/hosting or Docker image
execution claim is made.

## Unfinished

No implementation item remains unfinished. The broad gate retains its existing
stalled-stdout failure, and the final Fast gate retains the four gameplay/timer
failures above. P660 and P739 also failed isolated reruns and remain unresolved.
Docker is unavailable; `docker compose config` was not run. No public hosting,
TLS/CSP/lobby `/play/`, browser flight or WebTransport work was performed. The
listener remains default-off. All lane-started builds, tests, probes and the native
proof child have ended; the brief is retained only in ignored task scratch.

Native intent / Modern approach: retain existing game/wire behavior and CRC retirement
(`FedSrv/FedSrv.CPP:4591-4603`). No new native rule is ported or credited in a parity
CSV. The modern changes bound source budgets, authenticate route publication,
validate listing input, and refuse desktop handshake redirects on the optional,
default-off WebSocket transport.
