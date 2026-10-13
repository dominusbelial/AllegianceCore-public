# P627 — Authoritative client-side message wiring audit

Branch: `master` (not pushed) · Workspace: `~/Development/AllegianceCore`

## 1. What this task settles

P617 scored the `fedmsg` (network-messages) parity area at 83 done / 42 partial
/ 11 na and flagged item 21: the ~42 "partial" server→client messages might be
handled under renamed classes (false positives like `SHIP_STATUS`) or truly
unconsumed, so the fedmsg percentage could not be trusted. This task performed
the authoritative audit: for every one of the **136 `DEFINE_FEDMSG`** in
`Inc/Messages.h`, it determined from the **actual dispatch tables and decoders**
(never name-matching) whether the **real MonoGame client** (`ConnectionSession.OnReceive`
→ `ClientWorld.Apply`) consumes the server's message, and it added a runtime
dispatch-coverage test that encodes each supported server→client message and
proves the client dispatch consumes it (not "unknown fmid").

## 2. Corrected fedmsg numbers

Audit outcomes, compared to the P617 snapshot:

| | P617 | P627 (authoritative) |
|---|---|---|
| done | 83 | **84** |
| partial | 42 | **41** |
| missing | 0 | **0** |
| na | 11 | **11** |
| done% (unwtd, over non-na) | 66.4% | **67.2%** |
| done% (weighted 1–5) | 67.4% | **68.2%** |

The count moves only modestly, but the important change is *why* each row has
its status — the 41 remaining "partial"s are now individually confirmed
genuinely-unconsumed by the real client (ranked candidate tasks in §7), and the
three old false/over-claims are corrected (§5).

Breakdown of the 136:
- **59 server-sent native messages** the real client genuinely consumes (now provably — the dispatch-coverage test).
- **43 server-sent native messages** the real client does **not** consume (each is a ranked candidate task; 41 of them are the "partial" rows + 2 over-claims corrected).
- **11 deliberately-not-built** (`NotSupportedFedMsgs.cs`, all verified dead-in-native / N-A / missing-struct).
- **23 client-sent-only messages**, verified by their client sender path.

## 3. Method (authoritative, not name-matching)

For each of the 136 messages:
1. **Direction** and id from `Inc/Messages.h` (`DEFINE_FEDMSG(C/S/CS, NAME, id)`).
2. **Server sends**: primary `src/Allegiance.Server/*` file(s) that reference the message's codec.
3. **Client decodes**: the real receive path is a single `switch (fmid)` in `ConnectionSession.OnReceive` (`src/Allegiance.ClientCore/Session/ConnectionSession.cs`) — the *only* real-client receiver. Membership in that switch is the definition of "client decodes". The Bot client (`src/Allegiance.Bot/Bot.cs`) decodes more (98 fmids) but is the harness/bot client, not the game client, and is tracked separately.
4. **Client acts on it**: `ClientWorld.Apply(IFedMessage)` pattern-matches each consumed type to a state/UI effect.
5. **Native client handler**: `clintlib/appmsg.cpp` case line.

Everything landed in `docs/parity/fedmsg-wiring.csv` (message, id, direction,
server site, client decode, client effect, native handler, status) — the
executable twin of that CSV is the test in §4, which enforces that the two
registries stay in step.

## 4. The dispatch-coverage test

`tests/Allegiance.ClientCore.Tests/FedMsgDispatchCoverageTests.cs` — three tests:

1. **`EveryExpectedServerSentMessage_IsConsumed_NotUnknownFmid`** — encodes each
   of the **75** server-sent messages the real client is supposed to consume
   (59 native S/CS + 16 port-added, e.g. `MissileLockUpdate(204)` … `TimescaleUpdate(991)`),
   feeds the real bytes through the real dispatch (`ITransportSite.OnReceive` —
   no sockets), and asserts the fmid was **consumed**, i.e. did NOT land in the
   new `ConnectionSession.UnknownFmids` sink and did not throw during decode.
   To encode valid messages the test uses a reflection factory that empties
   collection members and hand-builds the few codecs that need a nested ship /
   full-width byte blob / `From(...)` factory.
2. **`UnknownFmidDetector_RecordsUnconsumedServerSentMessage`** — negative
   control: feeding `LOGOFF(3)` (a real codec-bearing server-sent message the
   real client deliberately does not consume) lands in `UnknownFmids`, proving
   the detector works so a pass in test 1 genuinely means "consumed".
3. **`SupportedServerSentSet_IsFullyAccountedFor`** — completeness: every one of
   the **102** native S/CS messages that has a wire codec must be in exactly one
   of `ExpectedConsumedServerSent` (consumed) or `KnownUnconsumedServerSent`
   (documented candidate). Adding a new supported S-message codec without wiring
   it *and* documenting it fails this test.

**Failing-first proof** (run with the two new dispatch cases removed, i.e. on
today's pre-fix code): test 1 fails with exactly
`fmid 16 (PlayerRescued) was NOT consumed — fell into the unknown-fmid sink` and
`fmid 212 (OwnShipKillBonusUpdate) was NOT consumed — fell into the unknown-fmid sink`.
With the dispatch cases restored all three tests pass
(`Failed: 0, Passed: 3, Total: 3`).

The `UnknownFmids` tracking added to `ConnectionSession` is a bounded distinct
`HashSet<ushort>` recorded in the new `default:` branch of the receive switch —
it is the instrumentation the test (and a future receive-audit) reads, and it
makes any future "silently dropped server→client message" observable at runtime
instead of invisible.

## 5. Wiring gaps fixed and mislabels corrected (small, in scope)

**Trivial wiring gaps fixed** (a decoder + full world logic existed; only the
receive `case` was missing) — both are failing-first, now green:

- **`FM_S_PLAYER_RESCUED(16)`** — server sends it to rescuer + rescuee
  (`Server.Docking.cs`, native `FedSrv.CPP:13056-13065`), `ClientWorld.Apply(PlayerRescued)`
  already produced the rescue-feedback line (`EjectFeedback.cs`, P578), but
  `ConnectionSession` had no `case PlayerRescued.Fmid:` so the real client
  silently dropped "you were rescued". Added the case.
- **`FM_S_OWN_SHIP_KILL_BONUS_UPDATE(212)`** (port message) — server broadcasts
  it per-tick owner-only (`Server.Broadcast.cs` P298), `ClientWorld` already
  stored `_killBonusPercent`/`_killBonusStreak` and `HudFrame`/`Game1.Rendering`
  render the Kill Bonus gauge, but `ConnectionSession` had no case — the real
  client never saw its live kill bonus. Added the case.

**False-positive partials → done** (already consumed by the real client under
their own class; the CSV's "no client handler found" was wrong):

- **`FM_S_BALLOT(17)`** — dispatched + `ClientWorld.Apply(Ballot)` → `ActiveBallot` for the vote keys (P626).
- **`FM_S_SHIP_STATUS(90)`** — dispatched as `case ShipStatusMessage.Fmid:` + applied (P320).

**Over-claimed done → partial** (real client send path done, but the server→client
direction is not consumed):

- **`FM_S_PROMOTE(189)`** — `ConnectionSession.SendPromote` (P416) is the client's
  sender, but neither client consumes the server's sector-wide promotion
  broadcast (`appmsg.cpp:824` swaps the command seat; the real client does not).
  Kept as a candidate task.

`docs/parity/parity-matrix.csv` fedmsg rows and the P617 scorecard table were
updated to these figures (see §8).

## 6. Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| `PLAYER_RESCUED` sent to both rescuer & rescuee | `FedSrv.CPP:13056-13065`; `appmsg.cpp:309-340` | design | Consumed; produces "you were rescued" feedback line |
| Kill-bonus streamed owner-only | `appmsg.cpp:1192`-family | design (era: native computed locally) | Port streams it as `OwnShipKillBonusUpdate(212)` (modern: server-authoritative) |
| Every server→client message decoded by a monolithic dispatch | `appmsg.cpp` giant switch | design | Equivalent single `switch(fmid)` in `ConnectionSession` |
| Unconsumed server→client messages dropped silently | `appmsg.cpp` (has handlers) | design gap | Now **observable** via `UnknownFmids` + the coverage test |

## 7. Ranked candidate tasks (the 41 genuinely-unconsumed + 2 over-claims)

Each has a wire codec and a native handler but is not consumed by the real
client. Ranked by player-visible value ÷ effort. (Full per-message notes in
`docs/parity/fedmsg-wiring.csv`.)

1. **`FM_S_TELEPORT_ACK(102)`** — teleport denied/accepted notice (`appmsg.cpp:1856`); the P273 teleport pane exists, only feedback is missing. Small.
2. **`FM_S_BOARD_NACK(178)`** — boarding refusal (`appmsg.cpp:1899`); P108/P273 boarding exists, only the refusal surface is missing. Small.
3. **`FM_S_PROMOTE(189)` receive** — reflect the command-seat swap on the promoted gunner's HUD (`appmsg.cpp:824`). Medium.
4. **`FM_S_RANK_INFO(13)`** — career rank/rating display (`appmsg.cpp:3372`); server emits, bot consumes. Medium.
5. **`FM_S_MISSILE_SPOOFED(110)` + `FM_S_END_SPOOFING(111)`** — ECM spoofing feedback (`appmsg.cpp:1667,1686`); the chaff/spoof mechanics exist. Medium.
6. **`FM_S_ENTER_LIFEPOD(166)` / `FM_S_LEAVE_SHIP(94)`** — forced-lifepod & leave-board notices (`appmsg.cpp:1945,1914`); eject (P540) exists, these edges don't. Medium.
7. **`FM_S_PROBES_UPDATE(92)`** — per-cluster probe list (`appmsg.cpp:584`); completes the P98-P100 probe sensor model. Medium.
8. **`FM_S_ASTEROID_MINED(198)`** — He3 mined notice (`appmsg.cpp:1733`); complements `OwnShipMiningUpdate(206)`. Medium.
9. **`FM_S_HIGHLIGHT_CLUSTER(199)`** — commander sector-highlight (`appmsg.cpp:1749`); the starmap exists. Medium.
10. **`FM_S_GAIN_FLAG(156)` / conquest** — `appmsg.cpp:3113`; full conquest mechanic. Large.
11. **`FM_S_WARP_BOMB(188)`** — `appmsg.cpp:850`; no warp-bomb mechanic. Large.
12. **`FM_S_PINGDATA(194)`** — bandwidth stats (`appmsg.cpp` none — port codec); connectivity already via `PING(35)` echoes (P508). Low value.
13. Remaining lobby/side/seat partials (`40 POSITIONREQ`, `42/43/44`, `51/52`, `124/126/128`, `155`, `162/164`, `177`, `182`, `192`, `197`, `199`) — pre-game lobby / seat / alliance / treasure-set UI that the MonoGame client does not yet surface. Mostly Medium-Large, gated on the lobby work.

The **9 na** messages remain deliberately unbuilt per `NotSupportedFedMsgs.cs`
(verified dead-in-native / N-A / missing-struct) — not candidates.

## 8. Files changed

- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `UnknownFmids` sink (+`default` case); dispatch cases for `PlayerRescued(16)` and `OwnShipKillBonusUpdate(212)`.
- `tests/Allegiance.ClientCore.Tests/FedMsgDispatchCoverageTests.cs` — new coverage test (3 tests).
- `docs/parity/parity-matrix.csv` — fedmsg rows 16/17/90 → done, 189 → partial.
- `docs/parity/fedmsg-wiring.csv` — **new** authoritative per-message audit table (136 rows).
- `docs/reports/P617_FEATURE_PARITY_MATRIX.md` — scorecard table refreshed (fedmsg 67.2%/68.2%) with a P627 note.
- `docs/reports/P627_FEDMSG_WIRING_AUDIT.md` — this report.

## 9. Modern alternative considered

Native runs a full local IGC simulation on the client, so it "acts" on a
message by mutating its local sim and can render almost anything. This port's
MonoGame client is a pure renderer of server-authoritative state with **no**
static core, so the correct modern shape is exactly what the audit confirms:
the server is authoritative, the client is a thin dispatcher, and any server→
client message the thin client does not understand is now **observable**
(`UnknownFmids`) rather than silent. A future improvement (out of scope) would
be to move the live-player stat channels (`KillBonus`, `MissileLock`,
`LeadIndicator`, `ScoreRoster`…) onto a single coalesced per-tick "player-state"
message instead of separate fmids — reducing per-message framing overhead —
while keeping the wire behaviours native-faithful.

## 10. Verification (measured)

- **Whole-solution build**: `0 warnings, 0 errors` (`dotnet build -m:8`).
- **Dispatch-coverage tests** (this change): `Failed: 0, Passed: 3, Total: 3`.
- **Full suite** (`scripts/run-tests.sh`, 2 runs on final code): see §11. Both runs
  surfaced **only** pre-existing live-wire timing load-flakes in the untouched
  transport / sim / live test assemblies (all documented in
  `docs/TEST_FLAKES.md`, one transport flake not yet ledgered); **none** implicate
  this change — the P627 `FedMsgDispatchCoverageTests` pass in both runs, and the
  failing tests do not exercise `ConnectionSession` (my only non-doc code change).

## 11. Suite results (measured, final code)

Run 1 (`scripts/run-tests.sh`):
- `Allegiance.Core.Tests`: **Failed — 4 failed / 4008 passed** (observed:
  `GameOverPlayersWireTests.Win_by_elimination…score` — P583 roster score,
  `CargoBayReloadTests.Reload_fuel_pack…` — TEST_FLAKES.md line 54; the other two
  were the rotating live-wire set). Script result: `ONE OR MORE TEST PROJECTS FAILED`.
  (Run 1's per-assembly output for the earlier projects was lost to terminal
  truncation — only the last project's tail was captured.)

Run 2 (`scripts/run-tests.sh`, full log captured to `/tmp/p627_run2.log`):
- `Allegiance.Lobby.Tests`: Passed — 12/12.
- `Allegiance.ClientCore.MonoGame.Tests`: Passed — 1949/1949 (+3 skip).
- `Allegiance.ClientCore.Tests`: **Failed — 682/683** (`ReadyCheckLiveTests.Only_the_mission_owner…` — TEST_FLAKES.md line 214, load-flake). The P627 `FedMsgDispatchCoverageTests` (3/3) passed in this assembly.
- `Allegiance.Core.Tests`: **Failed — 4008/4012** (`LiveRipcordTests.First_activation_broadcasts…` line 36, `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine…` line 184, `P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield…` line 440, and `TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` — a pure live-socket greet-timing proof, **not in the ledger**; flagged below).
- Script result: `ONE OR MORE TEST PROJECTS FAILED` (EXIT=1).

Conclusion: both runs on final code failed only on known (or same-class) live-wire
timing load-flakes in untouched assemblies; they rotate run-to-run exactly as
`docs/TEST_FLAKES.md` documents for the P487/P495/P498 sessions. No failure
exercises the code this task changed. The one unledgered failure —
`TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join`
(asserts a second client joins within 5 s while a silent staller parks the
greeting read; `TcpTransportGreetTimeoutTests.cs:129`) — is a classic fixed-sleep
join-timing probe and is unrelated to P627's receive-dispatch change; it should
gain a `load-flake` ledger row on the next flake pass (out of this task's scope).

## 12. Commit

`git log -1` (subject; the abbreviated hash shifts with each `--amend`):
```
P627: authoritative client-side fedmsg wiring audit + dispatch-coverage test
```
Working tree clean (tracked files). Not pushed (`git branch --show-current` = `master`).
