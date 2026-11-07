# P946 — intro menu arrow keys

The task's report is `INTRO_KEYS_P946.md` at the worktree root (the filename the brief names); this file is the
`docs/reports/` pointer required by the lane rules.

Captures (Classic + Modern, 800×480 real-backbuffer proof frames): `docs/reports/P946_assets/`.
Proof tool: `tools/p946-intro-keys-proof/`. Tests: `tests/Allegiance.ClientCore.MonoGame.Tests/P946IntroKeysTests.cs`,
`tests/Allegiance.ClientCore.MonoGame.Tests/IntroLayoutTests.cs`.

Summary: Up/Down always move the intro menu selection (they were ignored while the pilot-name field had focus), the
field shows a highlight box, underline and vertical caret while it owns typed text, the footer carries a one-line hint
(Tab part hidden when an identity account makes Tab a no-op), the Find Server `N` shortcut no longer steals a typed
`n` from the password field, and Direct Connect names/highlights the field that owns typing. The mapping and focus
rules live in `IntroScreenModel`, so they are proven headless.
