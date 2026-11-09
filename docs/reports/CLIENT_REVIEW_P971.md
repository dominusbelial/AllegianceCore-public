# P971 — Independent desktop-client correctness review

## Method and coverage

Read-only review of the desktop client at the supplied master worktree. No production, test, or parity-document file was changed. I read the receive dispatcher and transport ownership in `ClientCore/Session`, the world/reset/entity code in `ClientCore/State`, the principal game/session/lifecycle/eject/render/input paths in `ClientCore.Game`, the thin host and the update code it calls. I also read the native client handlers and target-selection rules for the parity findings, plus the relevant parity matrix, prior client reports, and the open-flake ledger.

Approximate depth by requested area:

- **Concurrency / lifecycle:** deep review of session receive, keep-alive, reconnect, shutdown, game-thread state transitions, and world reset boundaries.
- **Trust boundaries:** deep review of packet/decode failure containment, replay input, join links, identity/resume secrets, and update staging/application; spot checks of ID/count consumers.
- **Native parity:** targeted comparison of eject/rescue targeting and game-over player handling; dispatch matrix used to avoid re-reporting known gaps.
- **Frame time / resources:** reviewed hot render/pose/HUD loops, per-ship cache lifetimes, GPU creation/readback sites, and synchronous network waits. No game window or synthetic load was used.
- **HUD:** compared the shared model and Classic/Modern renderer overrides and style-specific call sites; no style-only visual findings are included.

Small headless reproductions were built and run under `.scratch/p971/repro/` with a bounded `timeout`; their console output is in `.scratch/p971/repro.log`. They prove malformed-packet propagation, reset retention, and partial update application. Native line numbers below were re-read at the cited locations. Findings marked **confirmed** have either that reproduction or a complete source-level input/interleaving proof.

## Findings

| ID | Severity | Confidence | Area | File:line | One-line summary |
|---|---|---|---|---|---|
| P971-01 | Medium | Confirmed | Trust / receive | `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1520` | A CRC-valid malformed server message escapes a decoder and terminates the receive path |
| P971-02 | High | Confirmed | Reconnect / teardown | `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1348` | A timed-out reconnect-thread join lets the old thread reset or close a replacement session |
| P971-03 | Medium | Confirmed | Resource lifetime | `src/Allegiance.ClientCore/State/ClientWorld.cs:2096` | Per-ship interpolation and render maps survive every world reset and grow across matches |
| P971-04 | Medium | Confirmed | Native parity / eject | `src/Allegiance.ClientCore.Game/Game1.Eject.cs:66` | Eject autopilot excludes allied rescue points and native `RescueAny` probes |
| P971-05 | Medium | Confirmed | Update integrity | `src/Allegiance.Update/UpdateApplier.cs:145` | An apply error after one copy leaves a mixed build despite the “current build untouched” contract |
| P971-06 | Medium | Confirmed | Trust / memory | `src/Allegiance.ClientCore/State/ClientWorld.cs:5358` | Repeated game-over player frames append duplicates with unbounded quadratic copying |
| P971-07 | Medium | Confirmed | Credentials / logs | `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1336` | The reconnect bearer nonce is written verbatim to persistent client/crash logs |
| P971-08 | Medium | Confirmed | Lifecycle / frame time | `src/Allegiance.Core/Transport/TcpTransport.cs:462` | Join performs unbounded synchronous socket and HTTP waits on the UI thread |

## P971-01 — Malformed server messages escape the receive loop

**Scenario.** A server (or an on-path implementation speaking the game protocol) sends a packet with valid framing and CRC whose message header says `SET_CLUSTER(7)` but whose body is absent. `FedPacket.Unpack` accepts it; `SetCluster.Decode` throws. The exception escapes `ITransportSite.OnReceive`, so the transport receive loop terminates instead of dropping only the bad message and retaining the session. The same dispatch pattern affects most other message cases, not just this example.

**Evidence.** `FedPacket.Unpack` validates framing, not message-specific lengths. The dispatcher calls `SetCluster.Decode(m)` directly at `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1520`; only framing and a few selected decoders have local `try/catch` blocks (`:1495-1518`, `:1707-1713`). The codec explicitly throws for this input at `src/Allegiance.Core/Wire/SetCluster.cs:53-56`. TCP's `ReadDataLoop` catches that `ArgumentException` as `invalid frame` and exits (`src/Allegiance.Core/Transport/TcpTransport.cs:1308-1347`); the client read worker's `finally` then raises session loss at `:507-508`.

The scratch reproduction fed exactly `04 00 07 00` through the public replay entry point. It produced `ArgumentException: SET_CLUSTER needs >= 54 bytes, got 4`, proving that the decoder exception reaches the caller. A connection-loss flag is not per-message containment: one malformed optional/state message unnecessarily ends an otherwise usable game session. TCP turns this particular `ArgumentException` into `invalid frame`, so the demonstrated outcome is session loss, not a claim of process-wide memory corruption.

**Suggested fix.** Put per-message decode/apply failure containment around each switch iteration, with a bounded malformed-message counter/log. Drop an offending message/packet when it is safe to continue, and make any mandatory protocol-error disconnect an explicit, deliberate policy rather than an incidental exception type. Do not silently continue after partially applying a message. Add fuzz cases for every supported FMID covering short bodies, bad var-item offsets/counts, and oversize collections.

## P971-02 — Reconnect teardown has a cross-generation ownership race

**Scenario.** Auto-reconnect is inside `ConnectTransport` when the player explicitly reconnects or the host disposes the session. `StopReconnectLoop` waits only 1500 ms, then returns and clears `_reconnecting` even though the worker is still alive. The caller creates a new transport/world. When the old worker's blocking connect finishes, it sends its retained request and can enter its next attempt, where it clears `_world`, replaces `_transport`, or shuts down the transport now owned by the new connection. Dispose has the same problem: a worker that passes the 1500 ms join can finish connecting after disposal and leave a live socket behind.

**Evidence.** The reconnect worker performs blocking transport establishment and subsequent world/transport mutation at `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1375-1405`; later failure handling shuts down the shared `_transport` at `:1432-1435`. `StopReconnectLoop` uses a bounded join but unconditionally relinquishes ownership at `:1348-1356`. The normal explicit-connect path then detaches and resets the same fields at `:363-388`, while `ConnectTransport` itself blocks synchronously at `:400-418`. TCP establishment has no managed deadline (`src/Allegiance.Core/Transport/TcpTransport.cs:462-466`), so the 1500 ms timeout is reachable. There is no disposed flag or connection-generation check in the old worker to prevent these writes.

**Suggested fix.** Give every reconnect attempt a cancellation token and an immutable generation/transport owner. `Connect`/`Dispose` must cancel and await the old attempt before any replacement is published; every post-await worker action must verify that its generation is still current and that it still owns the transport. Never treat a timed-out join as proof that a thread stopped. A disposed session must fail closed and must not start or retain another socket.

## P971-03 — Per-ship interpolation/render state leaks across world boundaries

**Scenario.** A player sees ships in one match, rotates to another match, reconnects, or is flushed to the team lobby. The entity table is cleared, but the per-ID dead reckoners and Hermite histories remain. New matches with new IDs add more entries indefinitely. An ID reused before a fresh pose can also expose the old interpolation state. The Game1 side-colour/radius maps similarly retain every historical ID and can supply stale data when an unresolved ID is reused.

**Evidence.** `_remoteReckoners` and `_remoteHermite` are allocated per ship at `src/Allegiance.ClientCore/State/ClientWorld.cs:1226-1235` and `:1274-1287`. `ResetCore` clears `_ships` at `:2145` but never clears either map; `BeginNextMission` clears `_ships` at `:5237` and likewise omits them; `ClearTeamWorld` also leaves them alive (`src/Allegiance.ClientCore/State/ClientWorld.Interface.cs:151-179`). Removal occurs only for selected individual departure paths, not wholesale world retirement. Game1 writes `_shipRadiusById` and `_shipSideRgbById` at `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:2984-2986`, and the only consumers at `src/Allegiance.ClientCore.Game/Game1.Rendering.cs:705,782` have no corresponding reset/removal.

The scratch reproduction seeded three ships and read the private map counts: before reset `reckoners=3, hermite=3`; after `BeginNextMission`, `ships=0` but both map counts remained 3; after `Reset`, they were still 3.

**Suggested fix.** Clear every ship-keyed interpolation/render map at all three wholesale world boundaries, including correction statistics and stale side colours. Prefer generation-scoped caches or reuse one per-world container so retirement is automatic. Add retention tests over repeated rotations/reconnects with changing IDs and ID reuse.

## P971-04 — Eject rescue selection drops native allied and `RescueAny` targets

**Scenario.** In an allied game, the player's own side has no visible rescue-capable station, but an allied rescue station is known. On eject, this client returns no target and the pod drifts until oxygen expiry; native selects the allied station and engages autopilot. Separately, native's `RescueAny` probe exception can make a rescue probe eligible regardless of side, while this client rejects it before checking that capability.

**Evidence.** The port requires exact side equality for both stations and probes at `src/Allegiance.ClientCore.Game/Game1.Eject.cs:66` and `:79`, before evaluating their rescue bits at `:67` and `:80`.

Native calls `FindTarget` with `c_ttFriendly`, station/probe types, any cluster, and rescue/rescue-any/land capabilities at `clintlib/appmsg.cpp:230-238`. `FindTarget` defaults its ally mode to 1 and passes it through to `FindableModel` (`Igc/igc.h:4632,4642`), explicitly meaning “your side + allies”. `FindableModel` classifies allied models as friendly in that mode at `Igc/common.cpp:269-273`; its station exclusions cover restart/unload/ripcord, not rescue (`:305-326`). Its probe `c_eabmRescueAny` alternative is independent of the side mask at `:282-286`.

This is a **bug**, not a visual adaptation: target selection and subsequent survival behaviour differ from native.

**Suggested fix.** Accept exact-own-side or allied stations using `ClientWorld.AreAllied`, and reproduce the separate `RescueAny` probe eligibility rule rather than applying exact-side filtering first. Add native-cited unit cases for an allied station, an allied rescue-any probe, and a non-allied rescue-any probe, plus a live allied-eject completion case.

## P971-05 — Update application is not failure-atomic

**Scenario.** A staged update contains two changed files. The first copy succeeds; the second staged file is missing, unreadable, has the wrong hash, or hits a disk/write error. `ApplyPending` returns failure but leaves the first new file beside the second old file. The next boot repeats the apply before loading the app, so a persistent error can leave the client in a version-mixed crash/boot loop.

**Evidence.** Files are copied directly into the running application tree one at a time at `src/Allegiance.Update/UpdateApplier.cs:145-159`. The catch at `:176-181` reports “keeping the current build” but performs no rollback. The pending marker remains, and `ClientUpdateBootstrap` invokes the applier before initialization at `src/Allegiance.ClientCore.MonoGame/ClientUpdateBootstrap.cs:47-50`.

The scratch reproduction staged valid `first.dll` and omitted `second.dll`. The result was `applied=False`, but the application root contained `first=new-first` and `second=old-second`, directly demonstrating the mixed tree.

**Suggested fix.** Pre-verify every staged file before the first mutation, then apply through a backup/rollback transaction or an atomic directory swap with a recovery manifest. Do not report the old build as preserved unless all prior copies/modes/deletions have been restored. Add injected failures at every copy/delete/mode boundary.

## P971-06 — Game-over player input grows without bound and is repeatedly recopied

**Scenario.** A faulty or hostile server sends valid `GAME_OVER_PLAYERS(63)` frames repeatedly, including duplicates. Every frame appends all entries and allocates a new array containing the entire old and new history. N entries therefore cause unbounded memory retention and O(N²) cumulative copying; the game-over renderer later processes the inflated list too.

**Evidence.** `ClientWorld.Apply(GameOverPlayers)` uses `[.. _gameOverPlayers, .. players.Entries]` at `src/Allegiance.ClientCore/State/ClientWorld.cs:5358-5361`. There is no identity check, mission-player bound, or duplicate suppression. `GameOverPlayers.Decode` accepts any whole number of 60-byte records within the packet (`src/Allegiance.Core/Wire/GameOverPlayers.cs:231-252`). The list is cleared only on `GAME_OVER`/reset.

Native does not accumulate these records: its handler finds each named existing player and updates that player's score at `clintlib/appmsg.cpp:3094-3111`; native chunks the ordinary send at 50 players (`FedSrv/fsmission.cpp:3132-3141`). This is a modern trust/resource bug, not required parity with native.

**Suggested fix.** Update one row per actual mission player instead of retaining an arrival history, ignore unknown/duplicate identities, and enforce a mission-roster/native-chunk bound. At minimum reject repeated frames after the expected roster is complete and avoid whole-list copying with a pre-sized map.

## P971-07 — Resume credentials are persisted in plaintext logs

**Scenario.** With opt-in reconnect grace enabled, the client logs the player name, endpoint, and exact resume nonce when a drop starts and again after success. Console output is mirrored to `client.log` and the crash ring, so ordinary support log sharing discloses the second factor needed to claim the parked seat. The same nonce is retained for subsequent reconnects, so the successful-resume log does not make it harmless.

**Evidence.** The client prints `resumeToken`/`_resumeToken` in full at `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1333-1338` and `:1425-1429`. It reuses that value in later LOGONREQ messages at `:1398-1405`. `CrashLog.Install` tees console output (`src/Allegiance.ClientCore.Game/Diagnostics/CrashLog.cs:91-108`), and `AppendSink` persists it to the rolling log at `:134-148`. The server-side contract confirms that matching name + token is the resume credential; this is not merely diagnostic public data. Native has no reconnect-grace credential; this is a port-extension trust bug.

**Suggested fix.** Remove token values from all client logs and crash reports (log only attempt count and a non-reversible correlation prefix if needed). Rotate the nonce after a successful resume when the protocol permits, and replace the 32-bit `Random.Shared` nonce with at least 128 bits from a CSPRNG in a future wire revision. Document log redaction for existing captures.

## P971-08 — The UI thread performs unbounded join waits

**Scenario.** The player selects a server whose TCP connect stalls at the OS/network layer, or whose logon/identity response is slow. The entire game Update stops inside `BeginConnect`: input, Escape/cancel, drawing, and the connection-failure screen cannot run until the synchronous calls return. TCP establishment itself has no managed timeout, so this is not limited to the explicit 5 s + 2.5 s waits.

**Evidence.** `Game1.BeginConnect` executes on the game thread and calls `_session.Connect` at `src/Allegiance.ClientCore.Game/Game1.Session.cs:1266`, synchronous identity `JoinTokenAsync(...).GetAwaiter().GetResult()` at `:1280`, `WaitForAck(5000)` at `:1283`, and `WaitForJoinAnswer(JoinAnswerWaitMs)` at `:1301` (the constant is 2500 ms at `:1337`). `ConnectionSession.ConnectTransport` calls `_transport.Join` synchronously (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:413-418`), and the TCP implementation uses blocking `client.Connect(address, port)` with no timeout at `src/Allegiance.Core/Transport/TcpTransport.cs:462-466`.

**Suggested fix.** Run the join workflow asynchronously with a cancellable `ConnectAsync` deadline, and stage only completion/failure back onto the main thread. Keep the Connecting screen interactive with a real cancel action and one bounded deadline covering connect, identity ticket, ACK, and join answer. The same cancellation/ownership primitive should be shared with P971-02.

## Areas not covered

- Exhaustive fuzzing of every wire codec and every render entity ID; the malformed SET_CLUSTER case establishes the dispatch-level containment bug, not a complete codec audit.
- Full 200v200 allocation/performance benchmarking on the rig, GPU timing, or real-window Classic/Modern screenshot capture. No remote host was contacted and no synthetic load was generated.
- Full native parity re-derivation for every HUD element, training lesson, flight model, replay training action, and career/squad screen. These were sampled where they intersected the findings; they were not line-by-line certified here.
- Android, Lobby internals, MCP bots, and server correctness, except small reads to confirm client protocol/credential semantics.

## Native intent

Native runs a full client IGC world, drops into lifepod autopilot using `FindTarget` with allied targeting enabled, and applies endgame score records to existing players rather than accumulating them. Native has no reconnect-grace nonce or modern signed-update transaction. Where this review found a gameplay difference (P971-04), it is classified as a **bug**. Malformed-input containment, bounded memory, thread ownership, secret redaction, and failure-atomic updates are modern correctness requirements even where native used less defensive era-specific code.

## Modern approach

Use one explicit owner/generation per transport attempt; contain every untrusted decode; retire per-world state wholesale; keep all join waits asynchronous and cancellable; make update application transactional; bound and deduplicate server-driven collections; keep credentials out of logs. Preserve native behaviour through the shared model, and present both HUD skins from that same corrected state rather than fixing symptoms separately in Classic and Modern.
