# P1004 — lobby listing capacity, browser states and enrollment retries

Behavior changes are implemented on base `a87b00f3`. The report was drafted
before the gate. Build and isolated qualification pass; the single full Fast
sweep remains failing, and real-window captures are blocked as detailed below.
No commits, pushes, remote host operations or public lobby requests. Local corpus
and in-process/loopback fixtures only. Validation results and capture status follow.

## Findings and classifications

| Finding | Classification | Root cause / native reference | Fix |
|---|---|---|---|
| Empty running game advertises no team slots | Port bug | `src/Allegiance.Server/Server.MissionParams.cs:104,649` (baseline), `ParamsOf` leaves `BitAllowJoiners` false. Native `Igc/igc.h:1251` defaults `bAllowJoiners=true`; `FedSrv/fsmission.cpp:4044-4069` gates the side scan on it. | Seed the native default. Keep the stage, lock, active-side, restart-base, per-side cap and auto-accept scan. |
| Server listing says maxPlayers 0 | Port bug | `src/Allegiance.Server/LobbyHeartbeat.cs:231` (baseline), `PumpAsync` passes a literal 0; `ServerRegistry.cs` faithfully stores it. Native mission cap is `min(total cap, teams * team cap)`, `FedSrv/fsmission.cpp:4074-4078`. | Publish summed current mission capacity on the HTTP server row; expanded client mission rows use their individual cap. No inferred slot flags in the directory. |
| Browser ignores team-slot scan for status | Port bug | `src/Allegiance.ClientCore.Game/LobbyGameSort.cs:196` (baseline), `StatusKey` uses counts only. Native `WinTrek/gamescreen.cpp:1006-1021` uses `fAnySlotsAvailable`; the yellow/guaranteed branch is commented out. | Shared listing availability model uses the native scan, with count fallback for pre-P866 metadata; rows state open/full/no-team-slots. |
| Any HTTP/JSON fetch failure says unreachable | Port bug | `src/Allegiance.ClientCore.Game/Game1.Session.cs:842` (baseline), `FetchServersAsync` catch labels all exceptions unreachable. | Separate HTTP replies, malformed list, timeout and transport failure. Empty/filter-excluded/no-team-slot lists get distinct messages. |
| Required browser password is hard to discover; Enter only finishes editing | Port bug in P970 UX | `src/Allegiance.ClientCore.Game/Game1.ConnectScreen.cs:162` and `Game1.HostLan.cs:171` (baseline). Native `WinTrek/gamescreen.cpp:1498-1505` opens the password popup on refusal; `WinTrek/passworddialog.cpp:89-94` treats Enter as OK. | Enter on an empty protected selection focuses the password; Enter submits a typed required password. Existing authoritative refusal/retype path remains. |
| Full/red rows stay selectable | Native design | `WinTrek/gamescreen.cpp:1299-1305,1318-1325` forwards the selected mission to JoinMission without a slot gate. | Preserve request-entry behavior; availability is advisory and server admission remains authoritative. |
| Approval repeats every five minutes indefinitely | Port bug in modern replacement | P808a, `docs/reports/P808A_ACCOUNTS_IDENTITY.md`; device grants expire after five minutes, and `src/Allegiance.Server/LobbyHeartbeat.cs:397` (baseline), `EnrollAsync` clears expired authorization and starts again five seconds later. | Expiry/errors back off 5, 10, 20, 40, then 60 minutes between retries, with an explicit log. Loaded/approved credentials bypass retry logic. |
| HTTP directory/device enrollment replaces native lobby transport/trust | Era constraint | P808a replaces server lobby logon/token (`lobby/messagesls.h:34-47,79-81,116-119`) with operator approval and private persisted credentials. | Keep the existing identity protocol and approved-server behavior. |

The tester observation is consistent with the two server advertisement bugs and
weak browser guidance, but the listing alone cannot prove why nobody attempted a
join. The original browser did not gate joining on zero capacity or slot flags;
password admission and actual network reachability are distinct from directory
reachability.

Native parameter names are easy to confuse: AGC `MaxPlayers` is **per team**
(`AGC/AGCGameParameters.cpp:253-272`), while `TotalMaxPlayers` is the mission cap
(`:2318-2333`). `Igc/igc.h:1334-1336` defaults to 100 per team and total 0x7fff.
For two teams the advertised mission maximum is therefore 200. Passwords are
independent of the native slot scan. `ServerRegistry.cs:109,155,526-527` and
`ServerListing.cs:205-206` in the lobby only preserve/default incoming values;
they were not the source of either server-side error.

Current fix sites: `Server.MissionParams.cs:108,653`, `LobbyHeartbeat.cs:236,430`,
client `ServerListing.cs:91,158`, `LobbyBrowserStatus.cs:9,18`,
`Game1.ConnectScreen.cs:173`, `Game1.HostLan.cs:169`, and
`Connect/ConnectScreenLayout.cs:30` (banner/row separation).

## Browser behavior and proof

The Play Online menu enters `ClientState.Lobby` (`Game1.IntroScreen.cs:421`).
Classic and Modern both use the same `DrawLobby`, availability model and input
path; the flight HUD style does not change this screen's layout.

| Directory state | Browser and join behavior |
|---|---|
| Password game with free slots | Row shows `[password required] [team slots open]`, `0/200`, and the password requirement. Enter focuses the empty field; type the password and Enter submits. Wrong passwords return the existing P970 incorrect-password/retype message; the correct password reaches the roster and side selection. |
| Teams full | Row shows `[full]`; the banner says no open team slots and offers request entry/refresh. Join remains possible. Native `FedSrv/FedSrv.CPP:634-646` checks an explicitly finite **total** admission cap, which defaults to 0x7fff; the displayed team-derived cap is not itself that admission limit. A team-lobby entrant requesting a full side receives `TeamFull`. |
| Only games advertising no team slots | Rows remain visible with `[no team slots]`, including empty-but-unavailable games. Banner explains reported team availability; it never says the directory is unreachable. Enter still requests entry. |
| Only version-incompatible servers | Existing version filter hides them and explicitly says a newer client/update is required. |
| Every game excluded by filters | No rows; message says no games match the current filters and offers C to reset. |
| Lobby down / connection refused | `Cannot reach the lobby. Retrying...` plus R retry; saved/previously fetched rows are retained and can still be attempted. |
| Lobby answers HTTP 503 | `Lobby returned HTTP 503 (ServiceUnavailable). Retrying...`; no claim that the responding lobby is unreachable. |
| Malformed/null list or timeout | Invalid server-list response or request timeout, respectively; automatic retries continue. |
| Successful empty list | No games currently listed; waits for registrations. |

Real-window proof harness: `tools/p1004-lobby-proof/` (fourteen captures planned,
seven states in both styles). The attempted run exited **134** before the first
frame: `NoSuitableGraphicsDeviceException`, with `GL.GetString` unable to load a
graphics context. This container has no system OpenGL/EGL/GLX/OSMesa, DRI software
driver or Xvfb, and no corresponding binaries/packages were found in local
caches. No public download was attempted, as the brief prohibits contacting public
hosts. A local graphics-runtime/cache path was requested during validation.
**Screenshots produced/reviewed: 0/0; this requirement remains blocked.** No
synthetic image is substituted for a screenshot. Machine-readable evidence:
[P1004 graphics blocker](P1004_assets/graphics-blocker.json).

## Validation and gate

| Check | Passed / Failed / Skipped or result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** (final qualified build, 9.70 s) |
| `tools/build-tools.sh --drop-bins` | **136/136 tool projects built**, 0 warnings/errors; includes the new proof harness |
| P1004 server/directory/enrollment cases, 3× alone | **5/0/0 each run** |
| P1004 browser cases, 3× alone | **13/0/0 each run**, including both HUD styles |
| Updated existing browser cases, 3× alone | **4/0/0 each run** |
| Total new/updated isolated qualification | **66/0/0** across three repetitions of 22 cases |
| Related enrollment, P866 publication, P970 password, browser/filter and layout regressions | **56/0/0** |
| One full Fast sweep | **14,139/8/20**, 14,167 cases, 561.9 s; **FAIL**, with four timing overruns |
| Fast infrastructure | **9/0/0** |
| Glossary links / parity CSV validation / working-tree hygiene | **PASS**; 798 glossary paths checked |
| Required screenshots and visual review | **BLOCKED**, 0 produced / 0 reviewed; see graphics blocker above |

Build workaround: `--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`. Restore sources were restricted to the local
NuGet cache; NuGet audit was not disabled and no live advisory refresh was
requested. No Docker is installed; this task adds no service, store, port or
production environment setting and makes no Compose changes.

The full Fast command was run **once**, as requested, with a **100-minute outer
bound**: `tools/run-tests.sh --tier fast --no-build --results-dir
.scratch/p1004/fast`. All 18 new cases passed in that sweep. Four failing existing
cases needed the expected new slot labels or the selected-row collections that
`GetUninitializedObject` bypasses. Those fixtures/expectations are now updated
and passed three times alone. No production behavior was altered after the
sweep, and no second full sweep is claimed.

A pre-qualification Core fixture run returned **13/1/0** because its new game
configuration omitted required side names; that fixture was corrected before
qualification. These task-related failures are distinguished from unrelated
failures, rather than entered as flakes.

The four unrelated failures (gunfire allocation, missile partial-tip receipt,
remote death receipt and NPC auto-launch mining), plus the two additional slow
strategy methods, were each run three times alone:

| Method | Sweep observation | Isolated Passed/Failed/Skipped, three runs | Longest case seconds per run |
|---|---|---|---|
| `P994GunfireBatchTests.Record_build_has_no_steady_path_allocations` | Expected zero allocated bytes; observed 3024. Not reproduced alone; cause unconfirmed. | **1/0/0; 1/0/0; 1/0/0** | 0.009 / 0.009 / 0.009 |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | Missing partial tip at lock 1.000; 37.835 s. Existing receipt family; fails again in one isolated run. | **1/0/0; 0/1/0; 1/0/0** | 1.491 / 38.575 / 1.767 |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | B never saw A's death/IsDead latch. Existing receipt family; not reproduced alone. | **1/0/0; 1/0/0; 1/0/0** | 1.316 / 1.309 / 1.349 |
| `NpcAutoLaunchLiveTests.Docked_npc_miner_auto_undocks_after_cooldown_then_mines` | Ore remained zero after auto-undock; 31.274 s. Previously ledgered family; not reproduced alone. | **1/0/0; 1/0/0; 1/0/0** | 3.441 / 2.827 / 3.430 |
| `P1000CivProfileTests.Every_loaded_civ_has_immutable_specs_and_legal_routes` | Passed, but civ 3 took 12.261 s in the sweep. All ten theory cases run per isolated repetition. | **10/0/0; 10/0/0; 10/0/0** | 1.834 / 1.843 / 1.800 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | Passed, but civ 5 took 15.503 s in the sweep. All ten theory cases run per isolated repetition. | **10/0/0; 10/0/0; 10/0/0** | 4.483 / 4.462 / 4.469 |

Those 18 isolated method executions produced **71/1/0** across 72 cases. Passing
repeats do not erase sweep failures. Details are recorded in
[TEST_FLAKES.md](../TEST_FLAKES.md). No thresholds, tiers or unrelated behavior
were changed. The full Fast gate remains **FAIL**; the visual gate remains
**BLOCKED**.

Portable receipts: [qualification](P1004_assets/qualification.json),
[unrelated isolated cases](P1004_assets/reproductions.json),
[graphics blocker](P1004_assets/graphics-blocker.json).
Raw build logs, TRX and console output remain in ignored `.scratch/p1004/`.
The brief was moved into that ignored scratch directory; it is absent from the
change set. All started build, proof and test processes have ended. No commits
or pushes were made.

## Native intent

Publish actual mission capacity and team acceptance opportunities. A password,
full team or locked game must not be described as a failed directory connection.
The selected mission can still receive an entry request and provide the reason.

## Modern approach

HTTP metadata, shared Classic/Modern explanatory text, a discoverable password
field, and bounded device-enrollment retries preserve those intentions without
requiring the native Zone/DirectPlay services.
