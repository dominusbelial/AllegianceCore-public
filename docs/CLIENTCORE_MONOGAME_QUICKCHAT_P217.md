# P217 — The real quick-chat voice-macro system ("SonicChat")

## Summary

P217 builds the original's quick-chat voice-macro system into the MonoGame
client: a real nested single-keypress menu bound to **R** (free in flight)
whose leaf commands send a real `CHATMESSAGE(23)` with the command's **`voiceOver`**
index set to its sonic-chat index, and whose receive path plays the real `.ogg`
voice line (alongside the caption text already shown by P214's chat log) for any
incoming quick-chat. Zero new wire work — the `voiceOver` field and the chat
relay existed end-to-end since P214; this stage populates it with a real
nonzero value and voices it on the other end.

## What "the real system" is (confirmed, not re-derived)

- **Send** (`WinTrek/WinTrek.cpp:958-1010`, `OnQuickChatMenuCommand`): picking a
  leaf menu item calls `SendQuickChat(sonicChatIndex, targetType, commandId,
  abilityMask)`; `sonicChatIndex` is `pqcc->GetSonicChat()->GetIndex()` and is
  what goes on the wire as the chat's `voiceOver` field.
- **Receive** (`WinTrek/trekigc.cpp:4508-4536`): when `idSonicChat != NA`, the
  client shows the caption (`GetSonicChatText`) alongside the message AND plays
  the real audio (`PlaySonicChat`, `WinTrek.cpp:1095-1111`).
- **Data** (`/home/domingo/Development/Artwork/quickchat.mdl`, 1572 lines):
  every quick-chat line is a `SonicChat([ SonicChatVoice(soundIdent, "Caption") ])`;
  the **wire index** is the explicit number from the `sonicChats = [ (N, name), … ]`
  list (native `DoSetIndex` at `WinTrek.cpp:1083` assigns exactly that number as
  `GetIndex()`/`m_vsonicChats[id]`); the nested menu tree is the `QuickChatMenu`
  / `QuickChatMenuItem` declarations (single char + caption + a menu-or-command
  node ref). Sound identifiers resolve to real files via `sounddef.mdl`'s
  `SerializedSound(…ImportWave("stem")…)`.

## Parser design decision — parse at runtime

Chosen **runtime parsing** over a build-time generated table: the verbatim
`quickchat.mdl` + `sounddef.mdl` are shipped as Content (text, alongside the
compiled assets) and parsed once at startup by `QuickChatMdlParser`. Rationale:
the original files stay the single auditable source of truth, there is no
fragile codegen step to audit, and the resulting in-memory `QuickChatDatabase`
serves both the send menu and the receive voiceOver→caption/sound resolution.
The parse is trivial (two files, once). This matches P213's precedent of reading
`sounddef.mdl` directly, just at runtime here.

### Index scheme — explicit numbers, not list position

The wire `voiceOver` value is the **explicit** index from the `sonicChats`
list, not the list position. The indices are deliberately non-contiguous (holes
at 238 and 242–299, index 235 reused after a commented-out `youSuckSonicChat`,
a resequenced 300+ batch at the end). The parser keys by the explicit number
exactly as native's `m_vsonicChats[id]` does; a received `voiceOver` resolves
through the same index→entry map. This makes sender and receiver agree with
each other and with the native numbering.

### Comment handling

`//` comments (whole-line and trailing, outside string literals) are stripped,
so commented-out menu items and `sonicChats` entries are excluded exactly as the
original ignores them.

## Content pipeline

- Copied the two real source files to `Content/quickchat.mdl` and
  `Content/sounddef.mdl`; the `.csproj` copies them to the output `Content/` dir
  (`<None … CopyToOutputDirectory="PreserveNewest">`) so the parser can read them
  at startup next to `Content.Load`'s compiled assets.
- Cross-referenced the **menu tree** against the voices to ship ONLY what the
  menu actually reaches: **288 distinct `.ogg` voices** (289 reachable sonic
  chats − 1 that shares a file), copied into `Content/Sounds/` and registered in
  `Content.mgcb` (each `OggImporter → SoundEffectProcessor`, the exact P213
  pattern). The brief's "well under 232" guess is **wrong** — the real menu tree
  references 288 distinct voices (a mix of `vo_player_*` and `vo_ma_*`, since
  some quick-chat lines use `vo_ma_*` announcer-style clips). Two referenced
  voices (`vo_ma_woopie`, `vo_player_ISPdrop`) do **not** exist in the artwork
  tree; their entries are parsed but unshippable, so they degrade to caption-only
  (lazy load logs once and stays silent).

## Implementation

- `QuickChat/` (new, pure logic, no MonoGame types — unit-testable):
  - `QuickChatMdlParser.cs` — the line-oriented parser (regex, comment-aware).
  - `QuickChatEntry.cs` — wire index + caption + resolved asset stem.
  - `QuickChatMenu.cs` — `QuickChatMenu`/`QuickChatMenuChoice`/`QuickChatSendAction`.
  - `QuickChatTarget.cs` — the targetType mask → `ChatTarget` mapping.
  - `QuickChatDatabase.cs` — index→entry table + `MainMenu` + `ByVoiceOver(index)`.
- `Rendering/QuickChatMenuRenderer.cs` (new) — draws the current menu level's
  items with their real shortcuts/captions (HUD glass-panel aesthetic).
- `Audio/SoundManager.cs` — `PlayVoice(stem)`: lazy one-shot, cached per asset,
  `_voiceFailures` set so a missing clip logs once; disposed in `Dispose()`.
- `Game1.cs` — binds **R**; menu state machine (open at `mainMenu`, descend into
  submenus, send leaf, Escape back-one/close — never quits); receive-side
  `ProcessIncomingQuickChatVoices()` plays each newly-arrived quick-chat's voice.
  Menu-open and chat-compose both neutralise flight input (a modal consumes the
  keyboard), matching P214's existing `composing` gate.

## Send path

`SendQuickChatLeaf(send)` → `_session.SendChat(send.Entry.Caption, send.Target,
voiceOver: (short)send.Entry.Index)`. The **sent text is the sonic-chat caption**
(the receiver displays it; native sends no text and looks the caption up from the
index instead — same displayed result, and this port's wire requires text).
`send.Target` is `QuickChatTarget.Resolve(ttMask)`.

### Target collapse to Team

Every leaf resolves to **`ChatTarget.Team`**. Native sends to its *current chat
recipient* (default team in combat) and uses the `targetType` mask mainly to find
the command's target *model* (console.cpp:1543-1590), not to pick a distinct wire
`ChatTarget` per line. Per the brief, individual-ship targeting and ability-mask
gating are out of scope, and `Team` is the safe default — many lines are team
intel ("Our base is under attack. Request assistance.") that must not go to
`Everyone`. Mask is still parsed+stored so the mapping is auditable, not an
omission.

## Receive path (and the self-echo de-dup)

P214 already renders `Events.Chats` into the log and the server relays the
sender's own echo (so sent macros appear in the log). `ProcessIncomingQuickChatVoices()`
walks the log forward (a watermark reference; on eviction from the 500-entry
window it re-anchors on the newest to avoid a re-play burst) and voices a chat
iff its `voiceOver` resolves to a real entry **and** `m.Message == entry.Caption`.
That index+caption check is what distinguishes a genuine quick-chat (spoken
caption sent) from a typed chat (index 0 with arbitrary text). Because the sender
is **not** voiced locally at the send site and the echo is voiced by this single
receive path, a locally-sent macro is played exactly **once** (confirmed live) —
no double-play, satisfying the brief's de-dup requirement.

## Out-of-scope adherence

- `P216` announcer cues: untouched (its files/game-over path left as landed).
- No command/gameplay EFFECT beyond the voice+caption message (no
  training-acknowledgment / `CommandID` machinery — the port has no training
  missions). `commandId` is sent as 0.
- `Flight/`, `Rendering/ChaseCamera.cs`/`CockpitCamera.cs`/`ShipRenderer.cs`/
  `Starfield.cs`/`PoseConverter.cs`: untouched.
- No individual/whisper quick-chats, no ability-mask gating (broadcast Team
  semantics only).

## Tests (`tests/Allegiance.ClientCore.MonoGame.Tests/QuickChatMdlParserTests.cs`)

11 new tests, all against real content (embedded verbatim excerpts with real
symbols/values + a `Parses_TheRealShippedDataset` test that locates and parses
the actual shipped `Content/*.mdl` files, asserting the full real invariants:
310 index entries, 13 menus, 22 mainMenu choices, index 15 = "Affirmative!" /
`vo_player_affirmative`, index 7 = "Attack enemy fighter" / `vo_player_attack`,
and the two unshippable voices still resolving to their stems). Coverage:
wire-index mapping, sounddef stem resolution, unknown-sound → empty stem,
commented `sonicChats`/menu items excluded, submenu descent, leaf → the
command's own sonic chat, target collapse to Team, symbol-on-preceding-line.

- **Must-fail-if-removed verified**: temporarily breaking
  `QuickChatTarget.Resolve` to `Everyone` turned 3 tests red
  (`MainMenu_TopLevelQuickItems_AreLeafCommands`, `EveryLeafCommand_MapsToTeamTarget`,
  `Descending_ThenLeaf_SendsTheCommandsOwnSonicChat`); reverted to green. The
  parser tests themselves inherently fail if the parser logic is removed.
- `dotnet test tests/Allegiance.ClientCore.MonoGame.Tests` → **121 pass, 0 fail,
  clean across 3 consecutive runs**.

## Verification

- `dotnet build AllegianceCore.slnx` → **0 warnings / 0 errors**, whole solution.
- Live check against a fresh `Allegiance.Server` (temp `teams:1` config on port
  6793, both peers on side Alpha → `Started`), two real GUI clients
  (`alpha-send`, `bravo-rec`) on display `:0` (PipeWire/OpenAL present). No
  keyboard-injection tool exists on this Wayland host, so the send was driven by
  a **temporary auto-send diagnostic** (removed before commit) that called the
  *real* `SendQuickChatLeaf` path for `mainMenu '1'` (Affirmative) on the
  alpha peer once, exactly as the R+1 keypress would; the receive-voice proof
  used a temporary `SoundState` probe (a sibling `SoundEffectInstance`, removed
  before commit):
  - Sender log: `quick-chat >> #15 "Affirmative!" (Team)` then
    `voice 'vo_player_affirmative' -> SoundState.Playing` (its own relay echo).
  - Receiver log: `voice 'vo_player_affirmative' -> SoundState.Playing`.
  - Server wire log: `[chat] 1 ship 8096 side 0 'Affirmative!' target 5 -> 2 peer(s)`
    — real `ChatTarget.Team` (5) relayed to both same-side peers, with the real
    caption text on the wire; the receiver's caption gate firing is what allowed
    playback, proving the receiving side had the real caption text.
  - **Honest limitation:** the host has audio devices but no speakers/capture
    tool, so I verified `SoundState.Playing` transitions and zero exceptions —
    not that it "sounds good". No local double-play of the sender's own macro
    (exactly one play per peer). Domingo will do the real listening test.
- Wider `Allegiance.Core.Tests`: 3034 total, 2 failures — both the documented
  pre-existing machine-state flakes
  (`LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  and `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`),
  which `Allegiance.Core.Tests` provably cannot be affected by this client-only
  stage (it references Core/Server/Bot/Lobby only, never the MonoGame client)
  and which fail persistently in isolation as documented.

## Assets used (all from `/home/domingo/Development/Artwork`)

- `quickchat.mdl` (1572 lines) + `sounddef.mdl` — the real quick-chat/menu/sound
  data, shipped as Content and parsed at runtime.
- 288 distinct `vo_player_*.ogg` / `vo_ma_*.ogg` voices reachable from the real
  menu tree → `Content/Sounds/`, registered in `Content.mgcb`.
- Two menu-referenced voices do not exist in the artwork tree (`vo_ma_woopie`,
  `vo_player_ISPdrop`) — caption-only, graceful no-audio.
