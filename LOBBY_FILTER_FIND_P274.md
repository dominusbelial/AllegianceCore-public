# P274 — Server-browser filter + find (native FilterDialogPopup / FindDialogPopup)

Landing: this repo `master` (Domingo pushes). Client-only — no server/Core change.

## Native citations (read-only source, never modified)
- `WinTrek/gamescreen.cpp:371` — `TRef<EditPane> m_peditGameName;` the game-name
  substring edit box in `FilterDialogPopup`.
- `WinTrek/gamescreen.cpp:510` — `m_pparent->m_strGameNameSubstring = m_peditGameName->GetString();`
  the filter value is taken verbatim from that edit box.
- `WinTrek/gamescreen.cpp:997-999` — the filter predicate (in `GameScreenFilter`):
  `if (!m_strGameNameSubstring.IsEmpty() && (ZString(pgame->Name()).ToLower()
  .Find(m_strGameNameSubstring.ToLower()) == -1)) return false;` — a case-insensitive
  SUBSTRING match on the server name; a server is excluded when the substring is absent.
- `WinTrek/gamescreen.cpp:531-608` — `FindDialogPopup`, the separate simpler
  jump-to-name dialog; `gamescreen.cpp:916` empties the substring on load.

## Scope decision (brief: "scope the filter to whatever ServerListing genuinely carries")
`ServerListing` (this port's client mirror of the lobby's read projection) carries:
`Id, Name, Address, Port, PlayerCount, MaxPlayers, MissionName, CoreFile, Version,
RegisteredUtc, LastSeenUtc`. It does NOT carry native's game-mode-type metadata
(conquest/deathmatch/prosperity/countdown/artifact/flags/territorial) nor lives /
scores-count / developments / zone — those `FilterDialogPopup` combos are
explicitly out of scope (named follow-up if someone later surfaces that metadata
server-side). The safe, real, faithful minimum is native's name-substring filter,
ported as a case-insensitive substring match; this port extends it to the other
genuinely-carried string columns shown in the row (`MissionName`, `Address`) so it
is useful on real listings.

## What was built (client-only, small text filter — not a new modal framework)
- `src/Allegiance.ClientCore.MonoGame/ServerListing.cs` — new pure,
  headless-testable `Matches(string needle)`: empty/whitespace → no filter;
  otherwise case-insensitive (`OrdinalIgnoreCase`) substring over `Name` /
  `MissionName` / `Address`. Equivalent of native `gamescreen.cpp:997-999`.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — lobby filter state:
  - `_visibleRows` (indices into `_servers` that pass the filter) + `_filterBuffer`
    (`StringBuilder`) + `_filterActive`; `_selectedIndex` is an index INTO
    `_visibleRows`, so with no filter active behaviour is byte-identical to before.
  - `F` toggles the mode (mnemonic: **F**ind — the native `FindDialogPopup`);
    the opening `F` keystroke never leaks into the buffer because MonoGame pumps
    `Window.TextInput` BEFORE this frame's `Update` poll, so OnTextInput's gate
    sees `_filterActive` still false. Key chosen because it is unclaimed
    everywhere in `KeyBindingTable` (grep-verified: in-use letters are
    A C D E G H K M N Q R S T V W; F is free) and maps to native's find concept.
  - typing narrows live via the REAL `OnTextInput` handler (same text-input path
    the in-flight chat line uses — reused, not reinvented), `Backspace` deletes
    the last char (mirrors chat backspace), `Esc` clears the filter instead of
    quitting while it is active (the Update Escape chain), `Enter` still joins —
    via `_servers[_visibleRows[min(_selectedIndex, …)]]` so it always joins the
    highlighted FILTERED server, never row 0 of the whole list.
  - `RebuildVisibleRows()` + `ClearFilter()` helpers.
- `src/Allegiance.ClientCore.MonoGame/Rendering/KeyBindingTable.cs` — documented
  the `F` find binding + the Esc-clears filter nuance in the LOBBY group (kept
  pure-ASCII for the H overlay).
- `DrawLobby` — iterates `_visibleRows`, shows a `Filter: <text>_` line + `N/M`
  count when active, a "no servers match" banner when the filter empties the
  list, and swaps the footer to the typing help while filtering.

## Live proof (real production methods, offscreen, /tmp/p274verify)
The harness instantiates the real `Game1` (ClientState.Lobby), points its private
SpriteBatch / HUD spritefonts / GraphicsDevice at a real harness device + the
client's real fonts, seeds `_servers` with a 5-server advertisement set (the real
`ServerListing` shape), drives the filter through the REAL `OnTextInput` /
`RebuildVisibleRows` / `ClearFilter` handlers, and captures the REAL `DrawLobby`
to offscreen PNGs:

| capture | filter | visible | servers |
|---|---|---|---|
| 1_full | (none) | 5 | Alpha Prime, Bravo Station, Alpha Two, Charlie Dome, Beta Outpost |
| 2_typed_alpha | `alpha` | 2 | Alpha Prime, Alpha Two |
| 3_typed_alpha_p | `alpha p` | 1 | Alpha Prime |
| 4_backspaced | `alpha` (deleted ` p`) | 2 | Alpha Prime, Alpha Two (re-widened) |
| 5_typed_bravo | `bravo` | 1 | Bravo Station (different subset) |
| 6_esc_cleared | (cleared) | 5 | all again |

Identical states yield byte-identical PNGs (sanity: `4_backspaced` == `2_typed_alpha`;
`6_esc_cleared` == `1_full`). Vision-confirmed: the filter line renders
(`Filter: alpha_ 2/5`) and the visible rows shrink to exactly the matching servers.

Enter still joins the correct FILTERED selection: with filter `beta` the visible set
collapses to 1 and the Enter resolution `_servers[_visibleRows[_selectedIndex]]`
(on the live real instance) yields exactly `Beta Outpost @ 192.168.0.148:7741`.
(Keyboard state can't be synthesized in a headless harness, so the literal
`ks.IsKeyDown(Enter)` gate is not polled; the exact resolution it feeds BeginConnect
is exercised verbatim.)

Screenshots: /tmp/p274verify/{1_full,2_typed_alpha,3_typed_alpha_p,4_backspaced,5_typed_bravo,6_esc_cleared}.png

## Build / test
- Whole-solution build: 0 warnings, 0 errors.
- MonoGame suite: 415/415 (incl. 8 new `LobbyFilterTests` for `ServerListing.Matches`).
- Lobby suite: 12/12. ClientCore suite: 123/123.
- Core suite (3126 tests), 2 full runs: run 2 = 3125/3126, the sole failure being
  the documented pre-existing `LiveTurretGunnerBindingTests` timing flake (the
  only expected one); run 1 = 3124/3126, which added one other transient
  machine-state live-network flake on top. No Core code changed here, so these
  are not regressions (consistent with the P273 baseline of "3125/3126, the sole
  failure being LiveTurretGunner").
