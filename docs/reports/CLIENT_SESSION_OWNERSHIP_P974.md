# P974 — client session ownership

Base: `a6d8bf479019e22df9ccb4d6f9c5cfe7b08565b0`. Codex lane; changes remain in the working tree. No commits, pushes, stashes, or remote-machine operations.

## Confirmed races on the base

`ConnectionSession.cs:1342-1350` sets the reconnect stop flag, clears the worker reference, waits only 1500 ms, and clears the reconnecting flag regardless of whether the thread stopped. The reconnect worker blocks in transport establishment at `:1389`; afterwards it sends logon, reads the shared ACK, and rearms keep-alive (`:1405-1430`). Its failure handler shuts down the shared transport (`:1433-1434`). Explicit `Connect` detaches/replaces that field and resets the world (`:367-399`). An old worker that outlives the timed thread join can therefore operate on the replacement connection. `Dispose` has no disposed latch (`:2193-2200`). TCP calls uncancellable `TcpClient.Connect` (`TcpTransport.cs:466`).

`Game1.Session.cs:1266-1305` runs transport connect, synchronous identity HTTP, the 5000 ms ACK wait, and the 2500 ms join-answer wait on the game thread. Update and Draw cannot run or process Escape during those calls.

## Design and implementation

The connection generation is an owner object, separate from the public connection counter. Each owner retains its cancellation source, worker completion, transport, and per-dial callback site. Explicit connect, automatic reconnect, initial UI join, shutdown, and disposal all use this ownership chain. Publishing a successor owner immediately rejects stale callbacks, but the successor cannot reset the world or publish its socket until the preceding operation and transport have actually drained. Cancellation never substitutes for completion.

| Change | Production source |
|---|---|
| Cancel and drain predecessor; protect publication, cleanup, callbacks and post-await mutations | `src/Allegiance.ClientCore/Session/ConnectionSession.Ownership.cs:10,48,62,126,139` |
| Dispatch task continuations asynchronously so a caller can dispose from its continuation without blocking an earlier cleanup continuation | `ConnectionSession.Ownership.cs:29` |
| Connect and reconnect use the same owner; reconnect preserves mission identity and retained logon request | `src/Allegiance.ClientCore/Session/ConnectionSession.cs:348,358,388,1311` |
| Receive-side mission departure and core rejection return before cleanup joins their read thread; loss notifications run outside the world lock after draining | `ConnectionSession.Ownership.cs:249,256`; `ConnectionSession.cs:1383,1725,1754` |
| Keep-alive and sends check the current owner; disposal is terminal, cancels/drains establishment, closes its transport and joins keep-alive | `ConnectionSession.cs:434,1232,1274,2129,2143`; `ConnectionSession.Ownership.cs:221` |
| Client DNS/TCP uses `TcpClient.ConnectAsync` with cancellation and a ten-second ceiling; a late socket is disposed before receive/send workers start | `src/Allegiance.Core/Transport/TcpTransport.cs:470,490,541,634` |
| UDP handshake, TCP fallback, NAT introduction and policy DNS propagate cancellation | `src/Allegiance.Core/Transport/ITransport.cs:29`; `UdpTransport.cs:95`; `TransportFactory.cs:32`; `NatTraversalClient.cs:96`; `NetworkLink.cs:13` |
| One 15-second overall UI deadline covers transport-policy DNS, connection, identity ticket, ACK and mission answer; silence is a timeout, never admission | `ConnectionSession.Ownership.cs:166,190,204` |
| Capture immutable UI choices, start the join task, poll completion and process Escape on the game thread; wait for cleanup before aborting; preserve password retry and failure destinations | `src/Allegiance.ClientCore.Game/Game1.Session.cs:1188,1230,1330`; `Game1.FlightInput.cs:263` |
| Keep Connecting drawn with a fitted status line and Escape hint in both HUD styles; cancel on lifecycle pause/window disposal; create a fresh session for a later join after terminal disposal | `Game1.Session.cs:1093,1230,1481`; `src/Allegiance.ClientCore.Game/Game1.cs:2155` |

The synchronous library entry points remain wrappers for existing tools. The asynchronous UI workflow never calls the legacy bounded blocking ACK/join-answer waits. Production TCP/UDP/traversal operations respect cancellation; a custom synchronous transport that ignores cancellation remains owned until its actual return. There is no timed thread-join escape hatch. Server hosting, wire messages, resume-token format and token logging are unchanged.

Four existing reflection-based test classes were updated to await `CaptureTransportSetup` rather than invoke the removed synchronous helper: `tests/Allegiance.ClientCore.MonoGame.Tests/P897ClientTransportTests.cs:87`, `P936InternetJoinTests.cs:43`, `P944JoinLinkUiTests.cs:103`, and `P947LobbyPrecedenceTests.cs:302`. Their transport/lobby assertions are retained. P947 now disposes its test session. The existing pure message-dispatch test now awaits the post-teardown loss notification (`tests/Allegiance.ClientCore.Tests/P718InterfaceMessageTests.cs:170`), while retaining immediate loss/world-clear and packet-order assertions; it passes 3/3 alone.

## Failing-first evidence

The two ownership regressions ran against base behavior with only a transport-factory test seam added: **0 passed / 2 failed / 0 skipped**. The delayed reconnect ignores cancellation and completes after the old 1500 ms join window. The base both publishes a replacement too early and returns from Dispose before the connector ends. The UI regression uses a real loopback TCP listener that never answers logon: **0 passed / 2 failed / 0 skipped**, Classic and Modern, with BeginConnect blocked for 5.023 / 5.006 seconds. Raw evidence is retained under [red results](../../.scratch/p974/evidence/red/).

The existing `P718InterfaceLoopbackTests.Own_mission_departure_closes_actual_session_socket_and_delivers_reason_once` caught a receive-thread lock cycle in the first ownership implementation. A bounded ten-second hang capture reproduced it before the receive-side fix. Mission departure now requests shutdown, returns to release the world lock/read thread, and notifies only after teardown. Two further regressions prove that mission-departure and reconnect-expiry loss handlers can synchronously dispose. The final departure/ownership/core-rejection/reconnect group passes **30/0/0 ×3**.

The final new tests cover late reconnect completion and stale callbacks, disposal during real socket establishment, terminal disposal, cancellation during identity and UDP handshake, missing mission admission, TCP shutdown before worker publication, the standalone TCP deadline, responsive UI polling/Escape, password retry from all three origins, refused logon, and joining again after logoff. **22 cases pass three isolated repetitions: 66 passed / 0 failed / 0 skipped**, including the explicitly Live ten-second TCP deadline test. Sources: `tests/Allegiance.ClientCore.Tests/P974SessionOwnershipTests.cs`, `tests/Allegiance.Core.Tests/Transport/P974ClientConnectTests.cs`, and `tests/Allegiance.ClientCore.MonoGame.Tests/P974ConnectingUiTests.cs`.

## Classic and Modern captures

The proof tool (`tools/p974-session-proof/Program.cs:25,51`) opens the production Game1 renderer, connects to a real loopback listener that never answers, captures the pending join, feeds Escape through the production input handler and captures the return to Direct Connect. At the pending capture it records 45 updates and an incomplete join task; the origin capture follows at update 131. Each run is bounded and exits normally. Use separate scratch `XDG_CONFIG_HOME` directories for repeatable profiles and `SDL_VIDEODRIVER=offscreen`; the final captures are 800×480.

I opened all four final images. Both pending images show the complete loopback endpoint, `P974Pilot`, and `ESC - cancel`. Both cancelled images show Direct Connect with address/password fields, remember-password OFF and the Enter/Escape hints. The generic connection screen is intentionally shared by the two styles.

| State | Classic | Modern |
|---|---|---|
| Pending | [capture](P974_assets/p974_connecting_classic.png) | [capture](P974_assets/p974_connecting_modern.png) |
| Cancelled to origin | [capture](P974_assets/p974_cancelled_classic.png) | [capture](P974_assets/p974_cancelled_modern.png) |

## Gate

The gate is complete, with the existing non-task failures below explicitly retained. `local.env` is present and artwork is available; skipped counts are explicitly reported below. The lane rules select the complete Fast tier for the workstation suite, with affected Live cases repeated separately.

- Solution build: zero warnings / zero errors.
- Tools: all 124 projects build.
- Linux x64 server NativeAOT publish: success, no IL2026/IL3050 warnings.
- New tests: 66 passed / 0 failed / 0 skipped across three repetitions.
- Reconnect/password/join-link/server-shutdown/UDP/NAT targeted families: three repetitions, initially 397 passed / 2 failed / 0 skipped (including new tests). The two P813c receipt races each pass 3/3 alone (six additional passes); see `docs/TEST_FLAKES.md`.
- Updated reflection callers and writable-profile check: **150 passed / 0 failed / 0 skipped** (50 cases ×3).
- Fast suite: **13,635 passed / 3 failed / 3 skipped**, 13,641 executions in **964.5 seconds**; infrastructure and coverage checks pass. The three skips are the optional private Meshy pack. The gate returns nonzero for the known failures and timing overruns below. The complete run uses the runner’s existing `TT1_LOCK_PATH` override and one project at a time after the default lock blocked the bounded queue attempts. A directory-descriptor alias keeps physical temp files in workspace scratch while preserving the outside-repository semantics of `PlayerBundlePathsTests` (9 cases ×3 pass); the product resolver is unchanged. A preliminary sweep was stopped after it exposed the receive-thread lock cycle, old reflection helper callers, and a test trying to write the read-only default profile; the production lock cycle and callers were fixed, and final verification uses a scratch XDG profile.
- Showcase never-parks: base **5 passed / 0 failed / 0 skipped**; patched **5 passed / 0 failed / 0 skipped**. Both use pool transport, native updates/GotoPlan, parallel broadcast and server GC. No synthetic load.
- Supplementary training smoke: client-core Fast/Live filter **126 passed / 0 failed / 0 skipped**. The much larger real-window matrix was bounded and stopped; its existing Classic reminder prompt-snapshot failure (already recorded under P945 in `docs/TEST_FLAKES.md`) reproduces **0 passed / 3 failed / 0 skipped alone**. The actual lesson docks and completes; the old proof rejects a repeated booster prompt in its five-entry snapshot. No training rule or training source was changed, and the complete training matrix is not claimed as passed.
- Glossary: 682 distinct paths validate. P974 parity CSV: five rows validate with LF endings. Diff whitespace and working-tree secret/personal-path scans pass. Four screenshots were opened and reviewed; all task media and the public JSON summary are below 2 MB. All started runs are bounded, have ended, and task-local build servers are shut down.


The completed sweep's three failures are the known missile-tip receipt timeout,
voluntary-logoff quit-receipt race and auto-seat receipt-clock boundary. Their
isolated results are respectively **0/3/0**, **3/0/0**, and **2/1/0**. The
missile case also takes 38.468 s against the Fast timing limit; the other slow
execution, P777 warp-exit measurement, passes three times alone in
9.394/9.424/9.452 s after its 11.264 s sweep execution. The existing P966 ledger
contains clean-base controls for missile tips and auto-seat. The earlier chat
allocation failure passes 3/3 alone and passes in the completed sweep. No server,
missile-lock, side-select, flight or training rule was changed to mask these
failures. Exact follow-ups are recorded in `docs/TEST_FLAKES.md` and the JSON
summary.

Raw test/build logs are retained in the worktree's [scratch evidence directory](../../.scratch/p974/evidence/); the small public [gate summary](P974_assets/gate-summary.json) contains summarized counts and gate metadata. The sandbox permits evidence writes in the worktree, so raw logs are not placed in the external evidence directory. Scratch binaries, dump and caches are removed before handoff. An initial SDK restore attempted default NuGet vulnerability metadata and failed on the read-only global cache; subsequent gates disable NuGet auditing and use cached dependencies.

## Native intent

Native explicitly says a retry must dump the preceding connection, then calls Reinitialize before joining (`clintlib/clintlib.cpp:1298-1306`). Failed logon remains owned by the origin screen (`WinTrek/introscreen.cpp:1676-1691`, `WinTrek/gamescreen.cpp:1652-1657`). A bad password reopens the password prompt and retains the browser (`WinTrek/gamescreen.cpp:1496-1504`). Own departure disconnects before clearing the mission (`clintlib/clintlib.cpp:3805-3848`), and a missing/mismatched core disconnects during JOINED_MISSION (`clintlib/appmsg.cpp:2161-2166`). Every cited native location was re-grepped locally. Preserved rules and modern lifecycle additions are tracked in `docs/parity/p974-client-session.csv`.

## Modern approach

Cancellable I/O, an overall deadline, generation-bound callbacks, serialized ownership retirement, and asynchronous game-thread completion preserve the native join/failure intent while keeping Update and Draw interactive. They add no server policy or protocol messages. Reconnect grace remains the existing opt-in port extension.
