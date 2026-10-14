# P456 — typed chat defaults to Team, with native Tab cycling of the chat target

## Why this task exists

From the 2026-09-29 gameplay-parity pass, spot-verified by the
coordinator: **every typed chat line goes to the whole mission, enemies
included.**

- Port: `SendChatLine`
  (`src/Allegiance.ClientCore.MonoGame/Game1.Session.cs:417-431`) always
  calls `_session.SendChat(text, ChatTarget.Everyone)`. Its own comment
  says "Plain Everyone/Team broadcast", but only Everyone is ever sent.
- Native: the typed-chat recipient defaults to Team. The console's chat
  state is constructed as `m_chsChat(CHAT_TEAM)`
  (`~/Development/Allegiance/src/WinTrek/console.cpp:644`).
- Native switches the target with **Tab while the typed text is empty**.
  `OnTab()` → `CycleChatTarget()` when `*pcc == '\0' || *pcc == ' '`
  (`console.cpp:2747-2755`, bound at `console.cpp:2389-2390`). With text
  typed, Tab does name completion instead (`console.cpp:2757-2995`).
- `CycleChatTarget()` (`console.cpp:2631-2745`), in the no-allies branch,
  cycles Wing → (Individual only when not composing a chat) → (Ship only
  if you have child ships/turret gunners) → FriendlySector → Team →
  Everyone → back to Wing. Read the exact order from the source.
- The port server already routes every target this needs:
  Everyone, Team, Wing (P396), FriendlySector, Leaders and Individual
  (`src/Allegiance.Server/Server.Lobby.cs:1856-1940`, `SelectChatTargets`).
  This is a pure client change.
- The wire enum lives at `src/Allegiance.Core/Sim/IgcEnums.cs:12-30`.

## Scope

1. **Default typed chat to `ChatTarget.Team`.** Keep the current target in
   client state, not as a hardcoded argument at the send site.
2. **Tab cycles the target while the compose buffer is empty (or holds
   only a space)**, in native's no-allies order, skipping the steps whose
   native condition doesn't apply:
   - Individual: native skips it while composing a chat.
   - Ship: skipped unless the player has child ships. Check whether the
     port models turret children; if it doesn't, skip Ship always and say
     so in the report.

   Tab with text typed must not cycle. Native does name completion there;
   see out of scope.
3. **Show the current target on the compose line** (for example `[TEAM] >`),
   so the player can see who will receive the message. Use the existing
   `ChatRenderer` compose-line draw.
4. Decide whether the target persists between compose sessions (native's
   `m_chsChat` persists, since it's a member), match that, and document it.

## Explicitly out of scope

- Tab **name completion** / Individual-recipient matching
  (`console.cpp:2757-2995`). Named follow-up: "P456b chat Tab name
  completion".
- Allies targets (`CHAT_ALLIES`): the port has no alliance system.
- Quick-chat targets and quick-chat commands. P457 covers those.
- Any server change. Routing for all these targets already exists.

## Verification

- Extract the target decision and the cycle into a pure, testable helper
  in the same pure-piece style as `ChatLogFormat`/`FlightInput`. First
  write a test asserting that a fresh compose sends to **Team**. **Run it
  against the unfixed behaviour and record the failure** (Everyone) in the
  report. For example, have the extracted helper initially preserve the
  current Everyone default, watch the test fail, then flip it.
- Cycle-order tests: Team → Everyone → Wing → FriendlySector → Team, with
  Individual and Ship skipped under their native conditions. Tab with
  text typed doesn't change the target.
- A loopback wire test: a client composing default chat produces
  `CHATMESSAGE(23)` with `ChatTarget=Team`, and an enemy-side Bot does not
  receive it.
- Build with `/home/domingo/.dotnet/dotnet build`: 0 warnings, 0 errors.
- Full suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean
  runs. Only the documented live-wire timing flakes are acceptable; name
  any you see.

## Deliverables

1. The fix and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` first.
2. Report doc `docs/reports/P456_TYPED_CHAT_DEFAULTS_TO_TEAM.md`. Cover the
   pre-fix failure, the cycle order as implemented versus native, the Ship
   and Individual skip decisions, and the named P456b follow-up.
