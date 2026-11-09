# P975 — Client robustness: contain malformed server messages, retire per-ship caches at world boundaries, bound the game-over roster

Base: master `a6d8bf47` (worktree `allegiance-hermes-p975`, branch `hermes/p975`).
Source findings: P971-01, P971-03, P971-06 of the independent client review
`docs/reports/CLIENT_REVIEW_P971.md` (Kimi K3.1 Pro, 2026-11-08). Each was
re-checked against master before the fix; each suggested fix was treated as a
suggestion, and the chosen fix is stated below with its reasoning.

Nothing was committed or pushed: the changes are in the working tree.
No native, server, wire-format, reconnect or update-apply code was touched
(P974/P976 own those).

---

## 1. What changed

| Finding | Production change |
|---|---|
| P971-01 malformed message ends the session | `ConnectionSession.DispatchInboundMessage` (the single contained dispatch) + `IsMandatoryFmid` + bounded counters/rate-limited log (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:146-186, 1543, 1580-2279`) |
| P971-03 per-ship caches survive world boundaries | one per-world container `ClientWorld.ShipCaches` + `Caches` + `RetireShipCaches`, called at all three boundaries (`src/Allegiance.ClientCore/State/ClientWorld.cs:385-421, 2211, 5304`; `src/Allegiance.ClientCore/State/ClientWorld.Interface.cs:184`); Game1's radius/side-colour/LOD maps now live in that container (`src/Allegiance.ClientCore.Game/Game1.cs:815-826`) |
| P971-06 game-over roster appends duplicates every frame | one row per player, updated in place, index + cap (`src/Allegiance.ClientCore/State/ClientWorld.cs:597-614, 5423-5458`) |

Supporting edits: the test registry of dispatched fmids / message factory is now
`internal` in `tests/Allegiance.ClientCore.Tests/FedMsgDispatchCoverageTests.cs:56,138`
(the fuzz table reuses it instead of duplicating 113 rows);
`docs/parity/fedmsg-wiring.csv:54` gains the one-row-per-player rule;
`GLOSSARY.md` §4 gains the two new headline concepts.

---

## 2. P971-01 — malformed server messages escaped the receive loop

### Reproduced on the base
`P975ReceiveContainmentTests.ReviewerRepro_ShortSetCluster_IsDropped_AndTheSessionSurvives`
fed the review's exact bytes (`04 00 07 00`, cbmsg=4, fmid=SET_CLUSTER(7), no body)
through `ITransportSite.OnReceive` and failed on the base run with:

```
Assert.Null() Failure: Value is not null
  Actual: System.ArgumentException: SET_CLUSTER needs >= 54 bytes, got 4
    at Allegiance.Core.Wire.SetCluster.Decode(ReadOnlySpan`1 buf) in src/Allegiance.Core/Wire/SetCluster.cs:56
    at Allegiance.ClientCore.Session.ConnectionSession.Allegiance.Core.Transport.ITransportSite.OnReceive(...)
       in src/Allegiance.ClientCore/Session/ConnectionSession.cs:1520
```

That is the whole bug: the exception left `OnReceive`, so
`TcpTransport.ReadDataLoop`'s `catch (ArgumentException) { _clientReadFailure = "invalid frame"; }`
(`src/Allegiance.Core/Transport/TcpTransport.cs`, `ReadDataLoop`) ended the read loop and the
client read worker's `finally` raised session loss. One malformed optional message
ended a usable session. The table-driven fuzz test failed the same way on the base
(0 of 595 feeds were contained: **every** feed escaped).

### What native did with a malformed message (classification)
Native's receive loop sanity-checks each message before dispatch:
`pfm->fmid > 0 && pfm->cbmsg >= sizeof(FEDMESSAGE) && pfm->cbmsg <= PacketSize()`
(`_Utility/Messages.cpp:947`). A message that fails is logged as `HACKER??`
(`:973`) and the REST of the packet is abandoned (`pfm = NULL`), **while the
connection lives**. With CRCs compiled in, both that path and a failed CRC
(`:997-1001`) call `OnBadCRC`, which on the client only logs and asserts
(`WinTrek/trekigc.cpp:3713-3718`): native never disconnects on bad input.

Two consequences for the port, stated explicitly:
- Native's gate is a *framing* check, not a body check: a CRC-valid message with a
  short body passes it and would crash native's own handler. The port validates the
  framing in `FedPacket.Unpack` already, so the port's containment sits one level
  lower — at the codec — which is strictly stronger and still "drop, do not
  disconnect".
- Native abandons the rest of the *packet* when one message fails its framing
  check, because it can no longer walk the packet. The port's messages are already
  framed and independently decoded, so a body-level failure drops **that message**
  only and continues with the rest of the batch. This is a deliberate divergence,
  documented here and in code.

### The fix
`OnReceive` now loops `if (!DispatchInboundMessage(m)) return;`
(`ConnectionSession.cs:1543`). `DispatchInboundMessage` (`:1580`) holds the whole
former switch verbatim, inside one `try`, with the world gate held by the caller:

- a decode **or** apply failure of a non-mandatory message is contained: the message
  is dropped, `MalformedMessageCount` is incremented, its per-fmid count is
  incremented in a bounded map (64 fmids max), and one log line is emitted
  (`fmid`, `len`, exception **type** — never the payload, never a credential) under
  a per-session rate limit (first 10, then one per 500).
- a mandatory-message failure is an explicit, deliberate disconnect
  (`NoteMalformedMessage(..., mandatory: true)` then `Shutdown()` + `RaiseLost()`),
  not an exception type leaking into the transport's read loop.
- the two pre-existing session-ending paths keep their exact semantics: they now
  `return false` and the caller abandons the rest of the batch
  (`JoinedMission` with a join error, own `QuitMission` departure).

### Mandatory-message list (deliberate policy)
| fmid | message | why mandatory |
|---|---|---|
| 2 | `LOGONACK` | The logon answer itself. Without it the session cannot exist; a corrupt ACK means the peer is not speaking this protocol. A deliberate port choice: native's client never disconnects on bad input (`OnBadCRC` only logs and asserts, `WinTrek/trekigc.cpp:3713-3718`). |

Everything else the client dispatches (121 messages) is a per-message drop: entity,
score, roster, warning and HUD streams are all re-sent, and native keeps the
connection. The list is code — `ConnectionSession.IsMandatoryFmid` (`:146`) — not
just prose, and the tests assert both sides of it.

### Half-applied messages — audit
Every case decodes into a local **before** it mutates anything; the fuzz proves it
behaviourally (world fingerprint identical for every rejected message). Five cases
mutate session/world state after the decode but before the world apply; they are
listed here because a *throwing apply* would leave that first mutation behind:

| case | mutation before `Apply` | safe because |
|---|---|---|
| `BuyLoadoutAck` | `_buyLoadoutAcks.Add(bla)` | a diagnostic arrival-order log; a later ACK supersedes it; no game state derives from it |
| `Ping` | `_pingEchoes.Add(...)` | a server-clock sample list; a later echo supersedes it |
| `AcquireTreasure` | `RecordPartOfferCargoFull()` | one independent counter for the "cargo full" sound; not part of any entity |
| `JoinedMission` | `_resumeRequest.DwCookie` | the cookie of the next resume attempt only |
| `DelPositionReq` | `_lastPositionRefusal` | the last refusal for the request UI; superseded by the next one |

`ClientWorld` contains no `throw` statement at all: its `Apply` methods are
dictionary upserts and field writes of records the codec has already validated
(fixed sizes, validated var-item offsets, validated counts). The only realistic
apply-time throw is a codec handing back a null nested record, which the same
containment catches.

---

## 3. P971-03 — per-ship interpolation/render state leaked across world boundaries

### Reproduced on the base
`P975WorldBoundaryCacheTests` failed on the base run:

```
PerShipInterpolationCaches_AreRetired_AtAllThreeWorldBoundaries: Assert.Equal() Failure: Expected 0, Actual 3
RepeatedRotationsWithChangingIds_KeepTheCacheCountsBounded:      Assert.Equal() Failure: Expected 0, Actual 4
ReusedShipId_GetsNoStaleInterpolationAfterABoundary:             "a reused ship id resolved a dead-reckoner from the retired match"
Game1PerShipRenderMaps_LiveInTheWorldContainer_AndRetireWithIt:  "ClientWorld has no per-world ship-cache container"
```

The review's `.scratch` repro (`reckoners=3, hermite=3` surviving
`BeginNextMission`/`Reset`) is reproduced exactly by the first two.

### The fix
One container per world — `ClientWorld.ShipCaches` (`ClientWorld.cs:385`), holding
the remote dead-reckoners, the Hermite histories, the P448b re-seed correction
statistics and Game1's three per-ship render inputs (radius, side colour, far-tier
LOD). `ClientWorld.Caches` (`:414`) exposes it; `RetireShipCaches` (`:420`)
publishes a **new** container (an atomic reference swap; a retired container is
never mutated again). It is called at the three wholesale boundaries:

- `ResetCore` — fresh connection / reconnect resume (`ClientWorld.cs:2211`)
- `BeginNextMission` — mission rotation (`ClientWorld.cs:5304`)
- `ClearTeamWorld` — leave to the team lobby (`ClientWorld.Interface.cs:184`)

Game1 keeps its call sites but no longer owns the maps: `_shipRadiusById`,
`_shipSideRgbById`, `_shipLodFar` are delegating properties into
`_session.World.Caches` (`Game1.cs:815-826`). Writes still happen in
`GatherRenderPoses` under the session gate; Draw-pass reads stay lock-free and, at
a boundary, see either the old container (harmless — that match is gone) or the new
empty one, never a mutation of a retired object.

This is the "prefer one per-world container so retirement is automatic" shape the
brief asked for: there is no per-id removal list to forget, and the Game1 maps
(which the review found were never cleared) are now retired by the same single
mechanism. Individual departure paths still remove single ids as before.

### Bounds, stated
`ShipCaches` entries are one per ship **in the current world**, so the count is
bounded by the mission's ship ids and reset to zero at every boundary. The test
`RepeatedRotationsWithChangingIds_KeepTheCacheCountsBounded` runs six rotations with
four new ids per round and asserts the counts never exceed the live ship count.

---

## 4. P971-06 — game-over player frames appended duplicates and recopied the list

### Reproduced on the base
`P975GameOverRosterTests` failed on the base run with:

```
NativeChunks_ProduceOneRowPerPlayer_AndDuplicatesDoNotGrowTheRoster: Expected 100, Actual 200
ARepeatedPlayerRecord_UpdatesItsRow_LastValueWins:                    Expected 2, Actual 4
HostileDistinctNames_AreBounded:                                     "a hostile GAME_OVER_PLAYERS frame grew the roster to 5000 rows"
```

i.e. every frame appended (duplicates included) and `[.. old, .. new]` allocated a
fresh array of the whole history each time.

### The fix
`ClientWorld.Apply(GameOverPlayers)` (`ClientWorld.cs:5436`) now folds each record
into ONE row per player, in arrival order, using a name→row index
(`_gameOverPlayerRowByName`, `:607`): a repeated name updates its row in place
(last value wins, exactly native's sequential `UpdateScore`), a new name appends one
row, and the roster is capped at `MaxGameOverPlayers = 999` (`:613`) — native hosts
advertise `nMaxPlayers < 1000` (the listing rule mirrored by
`IntroFindServerList.MaxListedMaxPlayers`), so a broken/hostile peer cannot grow it
past the port's real player ceiling. No allocation and no list copy per frame; the
`IReadOnlyList` instance is stable, which the test asserts with `Assert.Same`.

Reset sites (`ResetCore`, `Apply(GameOver)`) clear the list, the index and the
dropped-record counter together.

### Same content as today
`GameOverScreenShowsTheSameRowsAsTodaysAccumulation_ForATwoChunkGame` builds a
native-shaped 100-player roster (two 50-player chunks queued from the END of the
roster, `FedSrv/fsmission.cpp:3132-3141`), feeds it through the real world, and then
builds the actual screen rows with `GameOverTablesLayout.BuildPlayers` — the exact
call `Game1.Rendering.cs:2129` makes — asserting one row per player, identical
arrival order, names, sides and scores. The game-over screen renderer has no
`HudStyle` branch (`GameOverScreenRenderer`), so there is no Classic/Modern
divergence to test or screenshot; the shared `GameOverTablesLayout` model is the
only source for both.

---

## 5. Tests

New files:

| file | tests | what it pins |
|---|---|---|
| `tests/Allegiance.ClientCore.Tests/P975ReceiveContainmentTests.cs` | 4 | reviewer repro; mandatory LOGONACK disconnect; table-driven fuzz over **all 122 dispatched fmids** (595 malformed feeds: header-only, declared-oversize, bad var-item offsets, truncated-half, var-item overflow) — no escape, no session loss, world unchanged for each of the **292 rejected** feeds; only 3 codecs tolerate every degenerate shape (`EnterGame`, `EndSpoofing`, `RipcordDenied` — no required body field); counters bounded + log rate-limited |
| `tests/Allegiance.ClientCore.Tests/P975WorldBoundaryCacheTests.cs` | 4 | all three boundaries retire the interpolation caches; six rotations with changing ids stay bounded; a reused id gets no stale pose/Hermite history; the Game1 render maps live in the world container and retire with it |
| `tests/Allegiance.ClientCore.Tests/P975GameOverRosterTests.cs` | 4 | one row per player across native chunks; duplicates update, never append, no recopy; hostile distinct names bounded; screen content identical for a 2×50-chunk game |

All 12 tests are **failing-first on the base** (12 failed / 0 passed on
`a6d8bf47`, evidence: `~/allegiance-evidence/P975/base-p975.trx`) and pass after.

---

## 6. Gate

Everything below ran in this worktree on the final tree. `local.env` is present
(`ALLEGIANCE_ARTWORK_PATH` and `ALLEGIANCE_NATIVE_SRC_PATH` set), so the corpus-backed
suites **ran** instead of skipping; the only skips are the three optional private
Meshy-pack cases the MonoGame project marks itself.

| gate step | result |
|---|---|
| `dotnet build AllegianceCore.slnx` (0/0 required) | **Build succeeded — 0 Warning(s), 0 Error(s)** |
| `tools/build-tools.sh` (every `tools/*/*.csproj`, as `ci.yml`) | **all 123 tool projects built**, 0 warnings / 0 errors |
| new tests, 3× alone | **12 passed / 0 failed / 0 skipped** in each of three runs (181 / 185 / 196 ms) |
| affected existing tests, 3× alone | 29 cases: the 28 non-P807 cases are **28/28** in each of three runs; P807 alone is **2 pass / 1 fail** (see below) |
| one full Fast sweep (`tools/run-tests.sh --tier fast`) | 13,632 cases in 548.4 s: **13,626 passed / 2 failed / 3 not executed (skipped)** |
| 200v200-scale client interpolation test, 3× alone | `DecisionSnapshotAllocationTests.HeavyUpdateDecodeApplyAndPublish_StaysUnderPerShipAllocationCeiling` — the 200v200 heavy-update decode/apply/publish through the per-ship hot path — **pass 3/3** |
| showcase never-parks, 5× vs the base rate | **5 passed / 0 failed**, walls 147 / 183 / 149 / 187 / 304 s at load 5.9–13.0. Recorded base rate: 5/5 (P945/P965; P967 recorded 4/5 with an arrival-assertion failure) |
| per-frame cost added | **none** — measured below |

**Sweep failures (both pre-existing, both ledgered, neither on a P975 path):**
- `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire`
  (38.1 s, "lock never reached full + partial tip (lock 1.000)"): the open
  partial-tip receipt family (P933/P935/P966), **1 pass / 2 fail** alone on this tree.
- `NpcWingmanDefendLiveTests.Defender_disengages…` (`Assert.Same`: target null):
  **3 pass / 0 fail** alone — load flake in a sweep that shared the host with two
  concurrent lane sweeps (load 4.3–19.6).
- Both are recorded in `docs/TEST_FLAKES.md` under the new *P975 validation*
  section, together with P807's standalone result.

**P807 (`P807_394_peer_handoff…`) and load:** it fails 2/3 inside a combined
29-case run (phase slice 97.8 / 80.5 ms vs scaled 78.2 ms) and fails 1/3 alone
("declare sim work 157.6 ms over the scaled 150 ms budget" at load 27.1). This is
the exact single-max-sample timing family already ledgered for P960/P965 (base
2 passed / 1 failed, changed 0/3), and every P975 change is client-side while the
measured phase is the server's handoff tick. `docs/TEST_FLAKES.md` records it.

**Per-frame cost (measured, Release, 20 000 iterations after a warm-up, transport-free):**

| path | cost |
|---|---|
| game-over duplicate 50-player frame | **1,624 ns/frame, 0 B allocated per frame**, roster stays at 100 rows (before: a whole-history allocation + O(n) copy every frame) |
| valid `SET_CLUSTER(7)` dispatch | 1,182 ns/message, 488 B/message — the world entity-upsert path this task did not change; the containment wrapper itself allocates nothing |
| malformed `SET_CLUSTER(7)`, contained | 1,721 ns/message, 553 B (one exception object, malformed input only); 20 001 malformed messages produced **50 log lines** (first 10 + one per 500) |

The per-ship caches add no per-frame work: the hot path performs the same dictionary
operations through one field dereference, and the 200v200 per-ship allocation
ceiling still passes (3/3).

**Diff size note.** `ConnectionSession.cs` shows ~1 460 changed lines, but the
switch was moved verbatim into `DispatchInboundMessage` and re-indented by four
columns; `git diff -w` shows the substantive change as **+143 / −33**.

**HUD rule.** No screen or HUD layout changed. The only screen this task touches is
the game-over player table, whose *content* is proven identical (§4) and whose
renderer has no `HudStyle` branch, so there is no Classic/Modern pair to capture.

### Evidence (outside the repo, `~/allegiance-evidence/P975/`)
- `base-p975.trx` — the failing-first run on master `a6d8bf47` (12 failed / 0 passed).
- `fix1..6-p975.trx` — the fix-by-fix progression; `new-*.trx` / `newfinal-*.trx` —
  the new tests 3× alone (before / after the final edits).
- `affected-*.trx`, `affectedfinal-*.trx`, `p807-*.trx`, `p807final-*.trx` — the
  affected existing families and P807's isolated repeats.
- `neverparks-1..5.trx` — the five showcase never-parks runs.
- `missilelock-*.trx`, `wingman-*.trx` — the two sweep failures isolated 3×.
- `summary.json`, `summary.txt`, `*-main.log` — the one full Fast sweep.
- `harness/p975-bench.cs`, `harness/p975-fuzzcount.cs` — the scratch measurement and
  fuzz-count programs used for the numbers above.

---

## Native intent

Native's client is a full IGC world: it sanity-checks each inbound message and drops
a bad one while keeping the connection (`_Utility/Messages.cpp:947-994`), it owns no
client-side per-ship interpolation cache that could outlive the mission (a rotation
destroys the mission and its ships wholesale, `clintlib.cpp:4193-4224`), and it
applies endgame score records to the players that exist in its own player list
rather than accumulating arrivals (`clintlib/appmsg.cpp:3094-3110`; the server chunks
at 50, `FedSrv/fsmission.cpp:3132-3141`). Where native relied on era-specific
assumptions (its framing-only check, its crash-on-short-body handlers), the port
keeps the *intent* — drop, do not disconnect; one row per player; state dies with
the world — and implements it defensively.

## Modern approach

Contain every untrusted decode at one dispatch point with bounded counters and a
rate-limited log that never carries payload; make the mandatory set explicit in
code; give every ship-keyed cache one owner that is replaced wholesale at a world
boundary so retirement cannot be forgotten by a call site; and keep server-driven
collections deduplicated, indexed and capped. All three fixes live in the shared
client model, so both HUD skins and the headless bot world see the same corrected
state.
