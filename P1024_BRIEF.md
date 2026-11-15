# P1024 — R1 F9–F13: lobby hardening (lookalike callsigns, stale game requests, STUN budget, chat, stats)

Owner findings: **F9, F10, F11, F12, F13** — all in `src/Allegiance.Lobby/` (F9 also `Core/Identity/JoinTokens.cs`).
Report name: `R1_LOBBY_LIMITS_P1024`. Proof tests: `tests/Allegiance.Lobby.Tests/R1LobbyReviewTests.cs`.

- **F9:** `JoinTokens.ValidCallsign` rejects format/invisible and non-ASCII space characters (soft hyphen U+00AD,
  no-break space U+00A0, zero-width chars, etc.); the uniqueness key in `IdentityStore` becomes a shared "skeleton"
  (strip invisibles, collapse whitespace, case-fold) also used by `RegisteredCallsignCache`. Existing registered names
  must keep working — say what happens to an already-stored name that would now be invalid (it must still be able to
  log in; just no new lookalikes).
- **F10:** `GameCreationStore` — expire requests by age/state, drop them when their listing disappears, remove
  cancelled rows, small per-account cap on live requests.
- **F11:** `NatObservationStore` — per-source limit first; charge the global bucket only for requests that will be
  answered.
- **F12:** `DirectoryChat` — mutes accept only well-formed account ids and are bounded in total; `server:<id>` channels
  only for ids in the registry (or evict channels with no history) so made-up channels cannot lock out real ones.
- **F13:** `ServerRegistry` — clamp `PlayerCount`, `MaxPlayers`, `CurrentGames`, `MaxGames` on register/refresh; stats
  sum cannot overflow.
- Unskip all lobby R1 tests; add edge tests per finding. Run the whole `Allegiance.Lobby.Tests` project 3× too.

---
## Common to every R1 fix story (P1022–P1026)

**Source:** Fable's review `R1_REPORT.md` (repo root) and its proof tests, which are already on master but skipped:
`tests/Allegiance.Core.Tests/R1ReviewTests.cs` and `tests/Allegiance.Lobby.Tests/R1LobbyReviewTests.cs`
(`[SkippableFact(Skip = "R1 finding Fn — pending fix")]` / `[Fact(Skip = …)]`). Claude has re-run every one of them
unskipped and confirmed each fails for the reason the report gives. Read your findings' sections in full first.

**Definition of done for each finding you own:**
1. Remove the `Skip` from that finding's proof test(s) **without weakening them** (same inputs, same assertion
   meaning). If a test's expectation turns out to be wrong for the fix you chose, explain why in the report and change
   it minimally — never just raise a limit until it passes.
2. Fix the product code; add focused tests for the edges (legit client unaffected, limit boundary, config off/on).
3. **Legitimate play must not change:** normal clients, bots (MCP/`Allegiance.Bot`), the showcase/skirmish presets and
   the existing live tests must keep passing. Name the existing test classes you ran that cover the touched paths.
4. Any limit gets a named constant or `server.json` setting with a sane default, documented in the report.
5. Native: say for each fix whether it is native parity or a deliberate port hardening (the report says which), cite
   native `file:line` (re-grep), and update `docs/parity/*.csv` rows where a behaviour vs native changes.

**Lane environment:** you run in an MS-A2 Debian 13 container with your own clone; your worktree is the current
directory (own branch). SDK `~/.dotnet/dotnet`. Artwork `/srv/corpus/Artwork` and native reference
`/srv/corpus/Allegiance/src/` are READ-ONLY (`local.env` points at them). Docker is not installed. Base: Claude's
master at dispatch. Do not leave the brief file in the change set.

**Rules:** read `AGENTS.md` and `GLOSSARY.md` first. Public-repo hygiene (no personal paths/IPs/hosts), **never
`git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/<id>/`, do not commit or push,
do not contact the rig, mini, Mac, VPS or any public host. Every process you start must end before you finish.
Build-hang workaround: `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`. Do not disable
NuGet audit. Stay inside your findings — other R1 findings are separate stories running in parallel; do not touch
their tests. **Time:** 8 hours max; write the report first, then gate.

**Gate (light):** build 0/0 + `tools/build-tools.sh`; new/unskipped tests 3× alone; **one** full fast suite
(`tools/run-tests.sh --tier fast`, ≥ 90 min bound). Non-task failures: 3× alone + `docs/TEST_FLAKES.md`.

**Report:** `docs/reports/<NAME>_<ID>.md` — per finding: root cause (file:line), fix, native citation + parity
verdict, tests (unskipped + new), config/limits; then test counts (Passed/Failed/Skipped) and the gate. Footer:
"Native intent" and "Modern approach".
