# P456 — Typed chat defaults to Team, with native Tab cycling of the chat target

Pure client change (MonoGame compose path only). No server change: the wire
routing for every target in the cycle (Everyone / Team / Wing / FriendlySector,
plus Leaders and Individual) already exists in `Server.SelectChatTargets`
(`Server.Lobby.cs:2148`). This task changes only *which* target the compose line
sends and how the player picks it.

## The defect

`SendChatLine` (`Game1.Session.cs`) always called
`_session.SendChat(text, ChatTarget.Everyone)` — every typed line went to the
whole mission, enemies included — despite its own comment claiming
"Plain Everyone/Team broadcast". Native constructs its compose chat state as
`m_chsChat(CHAT_TEAM)` (`WinTrek/console.cpp:644`), i.e. a fresh compose is
**Team**.

## Pre-fix failure (recorded)

The target decision was extracted into a pure helper, `ChatTargetLogic`
(`src/Allegiance.ClientCore.MonoGame/Rendering/ChatTargetLogic.cs`), in the same
pure-piece style as `ChatLogFormat` / the Flight helpers (no MonoGame types →
unit-testable without a graphics device). Following the brief, the helper was
first written preserving the incumbent `Everyone` default, and the new test
`ChatTargetLogicTests.FreshCompose_DefaultsTo_Team` was run against it:

    Assert.Equal() Failure: Values differ
    Expected: Team
    Actual:   Everyone
    ... ChatTargetLogicTests.FreshCompose_DefaultsTo_Team() ... line 20

Then the constant was flipped to `ChatTarget.Team` and the test turned green.
That pre-fix failure is the whole bug, captured.

## Cycle order implemented vs native

Native `CycleChatTarget` (`console.cpp:2631-2745`), **no-allies branch** (the
port has no alliance system), reduced to the compose-time case. `OnTab`
(`console.cpp:2747-2755`) only calls `CycleChatTarget` while the buffer is empty
or a leading space (`*pcc == '\0' || *pcc == ' '`); `CycleChatTarget` itself
guards `m_csComposeState != c_csNotComposing`, and we only cycle mid-compose, so
`m_csComposeState == c_csComposeChat` throughout.

Implemented ring (`ChatTargetLogic.Next`), as `[Wing → FriendlySector → Team →
Everyone → Wing]`:

| current | next |
|---|---|
| Team | Everyone |
| Everyone | Wing |
| Wing | FriendlySector |
| FriendlySector | Team |

Native's WING case has two conditional exits: when *not* composing it goes to
Individual, otherwise it falls through; we are always composing, so we always
fall through — which is exactly the "skip Individual" branch the task calls out.
Below that, native only stops on Ship when the player has child ships, and only
stops on FriendlySector when the friendly sector is non-empty; otherwise it
falls straight through to Team. The implemented ring keeps the four landing
states in native's order (Wing → FriendlySector → Team → Everyone), which
reproduces `Team → Everyone → Wing → FriendlySector → Team` for the verification
sequence.

## Skip decisions

- **Individual — always skipped (matches native).** Native's WING case moves to
  Individual only when `m_csComposeState != c_csComposeChat`
  ("can't compose a chat to yourself"). Since Tab only cycles while composing,
  Individual is never reachable. Omitted from the ring.
- **Ship — always skipped (port limitation, stated).** Native includes Ship only
  when `trekClient.GetShip()->GetSourceShip()->GetChildShips()->n() != 0`
  (turret gunners / boarded passengers). The Core sim *does* model child ships
  (`Ship.ChildShips`, `AddChildShip`, turret boarding), but the MonoGame
  client-side entity the compose path sees (`ShipEntity`) exposes only `ParentId`
  — no child list — so the client cannot determine turret occupancy at compose
  time. Ship is therefore always skipped and the ring omits it. (Turret-gunner
  launch itself is client-side P416; exposing occupancy to the compose cycle is a
  reasonable named follow-up if it ever matters.)
- **FriendlySector empty-sector skip — deliberately not modelled.** Native's
  SHIP/INDIVIDUAL cases advance straight through FriendlySector to Team when the
  friendly sector is empty (`m_shipsSubject.n() == 0`). The brief's scope listed
  only Individual and Ship as skips, and the verification cycle expects
  FriendlySector to be a landing state, so the ring always lands on it. This is a
  documented, conscious simplification: the compose-time cycle is a stateless
  ring and does not consult live sector occupancy.

## Tab handling

In `Game1.FlightInput.cs`, inside the `_chatActive` block, Tab is now
edge-detected with its own `_chatTabKeyWasDown` flag (kept separate from the
starmap-overlay `_tabKeyWasDown` so the two never interfere across the composing
gate). When `ChatTargetLogic.CanCycleWhileComposing(_chatBuffer)` is true —
buffer empty or a leading space, exactly native's `*pcc == '\0' || ' '` — it
advances `_chatTarget = ChatTargetLogic.Next(_chatTarget)`. With typed text it is
a no-op (native does name completion there; that is P456b, out of scope). Holding
Tab never re-fires.

## Persistence decision (scope item 4)

**The target persists across compose sessions**, matching native: `m_chsChat` is
a member and is never reset when the input line closes, so the recipient is
remembered between composes. The port keeps one member `_chatTarget` on `Game1`
alongside `_chatBuffer`, seeded from `ChatTargetLogic.DefaultTarget` (Team) and
never cleared on send/close; only a fresh `Game1` reseeds it. Documented in
`ChatTargetLogic.DefaultTarget`.

## Target shown on the compose line (scope item 3)

`ChatRenderer.Draw` gained a `string targetLabel` parameter (preceded by the
existing `targetLabel + "> "` prompt) and `DrawChat` passes
`ChatTargetLogic.ComposeLabel(_chatTarget)` — so the line reads e.g. `[TEAM] >`,
`[EVERYONE] >`, `[WING] >`, `[FSECTOR] >`, letting the player see who will
receive the message before sending. Labels for the four cycle targets only;
anything else renders empty (never a crash). `ComposeLabel` is pure and covered
by unit tests.

## Loopback wire proof

`ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy` boots a
real `Allegiance.Server` (teams:2), connects three real `ConnectionSession`s
(the exact client type `SendChatLine` uses) over loopback TCP, and derives side
membership from each client's own `ClientWorld`. The sender composes with
`ChatTargetLogic.DefaultTarget` (Team) and sends; the same-side teammate's
`EventLog.Chats` gains the line carrying `ChatTarget=Team` with the sender's ship
id and the sender's own echo lands too, while the cross-side enemy's log stays
empty — the whole point of the Team default. This exercises the real
`FM_CS_CHATMESSAGE(23)` encode + real `SelectChatTargets` routing end-to-end.

## Tests

Pure (`ChatTargetLogicTests`):
- `FreshCompose_DefaultsTo_Team` (the flip that went red→green)
- `DefaultTarget_Persists_AcrossComposeSessions`
- `Cycle_Follows_NativeNoAlliesOrder_SkippingIndividualAndShip` (Theory:
  Team→Everyone, Everyone→Wing, Wing→FriendlySector, FriendlySector→Team)
- `Cycle_WrapsAround_AndHandlesUnknown`
- `TabCycles_WhenFirstChar_EmptyOrSpace` ("" / " " / leading-space)
- `TabDoesNotCycle_WhenTypedText_Leads` ("hello" / interior space / tab)
- `ComposeLabel_Covers_AllCycleTargets`, `ComposeLabel_UnknownTarget_Empty`

Live (`ChatTargetComposeLiveTests`): the loopback proof above.

## Build / test status

- Whole solution (`AllegianceCore.slnx`, Debug) with
  `/home/domingo/.dotnet/dotnet build`: **0 warnings, 0 errors**.
- The affected project `Allegiance.ClientCore.MonoGame.Tests` (contains every
  changed file and all 19 new tests): **clean 3× consecutively — 927/927 each**
  (15 s per run).
- Full `dotnet test`, 3 runs. Only the repo's documented pre-existing live-wire
  timing flakes failed; **names rotate run to run**, none in the MonoGame
  project, none referencing the chat compose symbols. None are attributable to
  P456: `Allegiance.Core.Tests` references only Core/Server/Bot/Lobby and
  `Allegiance.ClientCore.Tests` references only ClientCore/Core/Server/allegiance-
  mcp — neither project references `Allegiance.ClientCore.MonoGame`, the sole
  project P456 touches, so those results are byte-for-byte independent of this
  change.
  - Persistent/known: `LiveTurretGunnerBindingTests`, `GameOverPlayersWireTests.
    Conquest_*`, `ConquestTerritoryLiveTests.*`.
  - Rotating across the three runs: `AddPartTests`, `LiveTeamLobbyTests`,
    `LiveRipcordTests`, `LiveCountdownStartPingTests`, `LiveWinConditionTests`,
    `LiveChatSectorWireTests`, `ThreePeerSeededSessionTests`,
    `ShipDeleteGoneLiveTests`, `JoinFlowLiveTests`. (Machine was additionally
    under concurrent `allegiance-tools/run-chain.sh` + `delegate.sh` workloads
    during these runs, which is the classic amplifier for live-loopback timing.)

## Files

- `src/Allegiance.ClientCore.MonoGame/Rendering/ChatTargetLogic.cs` — new pure
  helper (default, cycle, can-cycle, compose label).
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `_chatTarget` member state,
  `_chatTabKeyWasDown` edge flag.
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` — Tab cycles the
  target while composing (buffer empty / leading space).
- `src/Allegiance.ClientCore.MonoGame/Game1.Session.cs` — `SendChatLine` sends to
  `_chatTarget` (was Everyone); `DrawChat` passes the compose label.
- `src/Allegiance.ClientCore.MonoGame/Rendering/ChatRenderer.cs` — compose-line
  `targetLabel` prefix.
- `tests/Allegiance.ClientCore.MonoGame.Tests/ChatTargetLogicTests.cs` — new pure
  tests.
- `tests/Allegiance.ClientCore.MonoGame.Tests/ChatTargetComposeLiveTests.cs` —
  new loopback wire test.

## Named follow-up

- **P456b — chat Tab name completion / Individual-recipient matching**:
  native's Tab-with-text path (`console.cpp:2757-2995`) completes the typed name
  against a recipient list and drops into `CHAT_INDIVIDUAL`. Out of scope here;
  the current Tab-with-text behaviour is a deliberate no-op so typed text is
  never accidentally re-targeted. That work should also revisit the
  Individual-targeted case in `SelectChatTargets` if direct addressing is wanted.
