# P784 (PAR-3) — chat and command composition: the missing compose paths, per-recipient order fan-out, channel focus

Base: `master` @ `19b32562` (`git log -1` at dispatch)
Branch: `pi/p784`
Native reference: `~/Development/Allegiance/src/`

## 1. The native compose state machine (mode/transition table)

`ConsoleDataImpl::ComposeState` (WinTrek/console.cpp:524-529) has five compose modes plus idle:

| State | Value | Native name | Enters via |
|---|---|---|---|
| NotComposing | 0 | `c_csNotComposing` | `QuitComposing` (console.cpp:1640-1649) |
| MouseCommand | 1 | `c_csComposeMouseCommand` | `PickShip` (:1776-1790), `PickObject` (:1927-1933), the drag rectangle (:2128-2131) |
| Chat | 2 | `c_csComposeChat` | `StartChat` (:2423-2431), `OnRecipientButton` from idle (:1185-1186), `PickCluster` idle branch (:1741-1743), `OnChar` Escape-from-Command (:2375-2380) |
| Command | 3 | `c_csComposeCommand` | `OnVerbButton` (:1264-1279), `OnChar` Ctrl+verb (:2343-2357), `PickCluster`/`PickObject` while composing (:1751-1752, :1928-1953) |
| Shell | 4 | `c_csComposeShell` | `OnChar` `'!'` from Chat with an empty buffer (:2388-2396) |
| Quick | 5 | `c_csComposeQuick` | `StartQuickChat` (:1508-1532), `'~'`/`` ` `` quits and opens the quick-chat menu (:2386-2387) |

Transition rules (each cited to its native site):

| Trigger | From → To | Native site |
|---|---|---|
| `StartChat` (TK_StartChat) | any → Chat (idle: `StartComposing`, composing: `SetComposeState`) | console.cpp:2423-2431 |
| `OnVerbButton` | Shell: ignored; else any → Command | console.cpp:1264-1279 (:1266 Shell gate) |
| `OnRecipientButton` | Shell: ignored; idle → **Chat** (starts a chat compose); composing: state kept, recipient retargeted | console.cpp:1134-1192 |
| `PickShip` / drag rectangle | idle → MouseCommand; composing: state kept | console.cpp:1776-1790, :2128-2131 |
| `StartQuickChat` | any → Quick | console.cpp:1508-1532 |
| `'!'` | Chat with empty buffer → Shell; with text: ordinary printable | console.cpp:2388-2396 |
| `VK_ESCAPE` | Command → **Chat** (typed text cleared); every other state → NotComposing | console.cpp:2375-2385 |
| `VK_RETURN` | empty buffer → NotComposing; text → state's send path → `QuitComposing` | console.cpp:2359-2368, :1604-1621 |
| `` ` ``/`~` | → NotComposing, then the main quick-chat menu opens | console.cpp:2386-2387 |
| `OnSwitchViewMode` | MouseCommand → NotComposing only | console.cpp:1031-1035 |
| Ctrl+<verb letter> | keystroke-taking, non-Command, empty buffer → Command | console.cpp:2343-2357 (gate :2335-2338) |
| `IsTakingKeystrokes` | typed characters accepted only from states > MouseCommand | console.cpp:2335-2338 |

Every state's send path ends in `QuitComposing` (`SendChat` :1604-1621, `SendQuickChat` :1568-1602). A Chat send relays the typed text; a Shell send calls `BaseClient::ParseShellCommand` instead of the chat relay (:1612-1614); any other send issues the armed verb + target (`m_cidVerb`, `m_pmodelTarget`, :1615-1620).

## 2. Per-recipient order fan-out (the cited loop)

`ConsoleDataImpl::SendChatInternal` (WinTrek/console.cpp:1528-1573) sends **one chat per subject** when the recipient is a group — the `CHAT_GROUP` subject loop :1546-1564 (first subject as `CHAT_GROUP`, each subsequent as `CHAT_GROUP_NOECHO`) — and a wing/group recipient's subject list is built by `ChatSubject::SetRecipient`: every same-side ship matching the wing (:398-406), every ship in the picked rectangle (:2128-2140), with Shift-click toggling single subjects (:2155-2160).

## 3. Leader-bold history

- `BaseClient::OnChatMessage` stamps `bIsLeader = ppi->IsTeamLeader()` (trekigc.cpp:4498; admin chats with no sender are leader too, :4500-4501) into `ChatInfo` (`clintlib/clintlib.h:235`).
- The chat pane draws each item with `TrekResources::GetFont(m_pFontSize, m_pchatInfo->IsFromLeader())` — the **bold** face for leader lines (chatpane.cpp:34,93).

## Port changes (before → after)

**Before** (HUD-10 state): production reached only `ComposingChat` and `ComposingQuick` (`LiveComposeState`, Game1.ConsoleModes.cs); the mouse-command/command/shell compose paths were absent; the command bar's recipient/wing selections were display-only; chat history carried no leader flag.

**After** — all edits in `src/Allegiance.ClientCore.Game/**`:

| Change | Files |
|---|---|
| New pure compose state machine `ComposeMachine` (states = `NativeComposeState`, every transition from §1) + `ShellCommands` (the `!mute`/`!unmute` parse, clintlib.cpp:2277-2311, names resolved against the world roster) | `Rendering/ComposeMachine.cs` (new) |
| Live compose state: `_composeState` drives `LiveComposeState`, the compose-pane slide/art, and the sound gates; `_chatActive` now means "a keyboard composer owns typing" (Chat/Shell, the port's reading of `IsTakingKeystrokes`) | `Game1.cs`, `Game1.ConsoleModes.cs`, `Game1.SkirmishObserver.cs` |
| `HandleChatCompose`/`OnTextInput`: Enter sends per the state (empty quits; Shell runs the shell command; Chat/other sends chat) then quits; Escape routes through the machine (Command steps back to Chat); `'!'` switches Chat→Shell on an empty buffer; `` ` ``/`~` quit into the quick-chat menu | `Game1.FlightInput.cs`, `Game1.Session.cs` |
| Command bar: a verb click arms the Command compose (`OnVerbButton`); a recipient click maps the native columns (Me/Sector/Team/All/WingN, console.cpp:13-18) through `OnRecipientButton` and opens a chat compose from idle (`CommandBarLayout.RecipientChatTarget`) | `Game1.Commanding.cs`, `Rendering/Hud10Layouts.cs` |
| Mouse-command compose: a left pick of a friendly ship or a non-empty drag rectangle starts MouseCommand (console.cpp:1776-1790, :2128-2138); an empty rectangle or a console-mode switch quits it (console.cpp:1031-1035); an issued order ends the compose (console.cpp:1618-1620) | `Game1.MousePick.cs`, `Game1.Commanding.cs` |
| Per-recipient fan-out `Game1.CommandFanout.Recipients` (pure): drag group wins; else a wing recipient expands to every same-side wing member **including self** (native's subject sweep, console.cpp:398-406); else the single commandee. `TryIssueOrderToCommandee` loops one existing `FM_CS_CHATMESSAGE(23)` Individual order per recipient — the port of SendChatInternal's per-subject loop (the port's server routes drone/queued orders on Individual-family targets only, so the loop runs client-side with the port's existing order message; no new wire fields) | `CommandFanout.cs` (new), `Game1.Commanding.cs` |
| Leader-bold: `BuildChatLinesAndColors` flags each line whose sender is the side leader (`TeamLeaderShipIdFor(sender side) == sender ship`, the port's `SET_TEAM_LEADER` roster standing in for `PlayerInfo::IsTeamLeader()`); `HudChat.LeaderLines` carries the flags and `ChatRenderer` draws those lines in the native `LargeBoldFont` face (`HistoryFontKind`) | `Game1.Session.cs`, `HudModel.cs`, `Rendering/ChatRenderer.cs` |
| Compose focus + channel: the compose-line recipient label resolves the actual recipient name (side/wing/cluster/ship name like `UpdateComposedChat`, console.cpp:1299-1338 — `ChatTargetLogic.ResolvedLabel`); a Wing/Individual/Sector compose fills `OidRecipient` from the sender's own ids like `BaseClient::SendChat` (clintlib.cpp:2312-2367); the Alt+C channel-filter mode's channel set follows a mid-compose Tab recipient cycle (`AddChannel`/`UpdateContents`, chatpane.cpp:340-345) | `Game1.Session.cs`, `Rendering/ChatTargetLogic.cs`, `Game1.FlightInput.cs` |

Documented adaptations (no new wire fields, nothing out of scope broken): the port's Command compose is armed by the command bar and completes by clicking a target (native's typed name-completion in Command state stays out of scope per P456b); Escape also quits a MouseCommand selection (native only defines that quit on view-mode switches, console.cpp:1031-1035); `ShellCommands` reuses the port's `ClientWorld.SetPilotMuted` store (the ModerationModel's target) since `ParseShellCommand`'s mute is client-local state.

## Tests

New: `tests/Allegiance.ClientCore.MonoGame.Tests/P784ComposeTests.cs` (30 tests):

- **State machine** — every mode transition from every state (§1 table), `TakesKeystrokes`, the quitting triggers as a Theory.
- **Shell** — `!mute`/`!unmute` by name (case-insensitive), five drop cases; headless production flow: `'!'` switches to Shell only on a fresh compose, Enter-in-Shell runs the mute against a real `ConnectionSession` world and quits, stray text after quit is dropped.
- **Fan-out** — pure group/wing/single resolution; **loopback wire proof** (`Fanout_issues_one_order_message_per_recipient_on_the_wire`): a real server + three peers; the wing-0 fan-out is resolved over the sender's real world and the production per-recipient loop puts one `CidAttack` `CHATMESSAGE(23)` per recipient on the wire — the teammate and the sender each receive exactly one order addressed to them, the enemy none.
- **Leader-bold** — `HistoryFontKind` face choice; the headless production `BuildChatLinesAndColors` flags the leader's line and not a wingmate's.
- **Recipient mapping** — the native command-bar columns → chat targets.

**Which tests failed on the unfixed code** — with the twelve edited production files temporarily reverted to `HEAD` (raw log `~/allegiance-evidence/P784/before-tests.log`), five of the reflection-driven tests failed: `Enter_in_the_shell_state_runs_the_shell_command_not_chat` and `Typing_bang_on_a_fresh_compose_switches_to_the_shell_state` (no `_composeState` field — NPE on read), `Typing_bang_after_text_stays_a_printable_character`, `Escape_in_the_command_state_steps_back_to_chat`, and `Chat_history_marks_the_side_leaders_lines_bold` (the old `BuildChatLinesAndColors` returns a 2-tuple without leaders). The pure-machine/`CommandFanout`/font/recipient tests reference APIs that only exist after the fix (compile-gated). After restoring the fix: **30/30 pass**.

## Verification results

| Check | Result |
|---|---|
| Whole-solution build (`AllegianceCore.slnx`) | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | **all 70 tool projects built, 0/0** |
| `P784ComposeTests` ×3 alone | **30/30 passed each run** (and 2 more after the before/after experiment) |
| Affected suites (P761 compose, P757 chat pane, ChatTarget*, Hud10/11/12, P587 mouse pick, CommandOrderResolver, P675/P728 command, P770 chat-key sound, FlightInput, P640 command camera) | **285/285 passed** |
| One fast tier (`tools/run-tests.sh --tier fast`) | **PASS — 9996 tests, 284.1 s** (`TestResults/tiers-20261023T010631.253322Z/summary.json`) |
| `python3 scripts/parity-scorecard.py` | **validates** (parity-matrix 668 rows, sim-rules-matrix 473 rows) |

## Evidence

- In-repo summary: this report (no new report assets needed).
- Raw logs (outside the repo): `~/allegiance-evidence/P784/`
  - `before-tests.log` — the reverted-code run (5 failures + compile gates)
  - `after-tests.log` — the restored-code run (30/30)
  - `fast-summary.json` — fast-tier runner summary

## Matrix update

`docs/parity/parity-matrix.csv`, four rows → `done`:

- `hud / Chat history` — leader-bold wired (trekigc.cpp:4498; chatpane.cpp:34,93).
- `hud / Chat composition and recipient` — all five compose states reachable through the native inputs, transitions ported as `ComposeMachine`.
- `hud / Command recipient verb and wing controls` — verb/recipient clicks drive the machine; per-recipient order fan-out ported.
- `commander / Team chat / Compose recipient and team rendering` — resolved recipient label, recipient-id conversion, channel filter follows the compose recipient, leader-bold history.

## Unfinished / notes

- Native Command-state name completion (typed verb + target name with uppercase remainder, console.cpp:1295-1323) stays out of scope (P456b already recorded it); the port's Command compose completes by clicking.
- `Radar command subjects` (row 433) is untouched: the port's radar still does not consume the compose subject lists; the recipient selections now exist (groups/wings) but the radar-side wiring is a separate row.
- The wing fan-out includes nameless same-side system ships exactly like native's subject sweep (console.cpp:398-406); the server drops their copies (no connection), so they cost one wasted relay, as native.
