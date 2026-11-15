# P1028 — Lobby career results: unbounded values and int overflow (R1 §4 lead)

You are a coding agent (OpenCode CLI, free model) in an isolated lane: a git worktree of AllegianceCore, a public C#
port of the space game FreeAllegiance. Claude (the orchestrator) reviews your work. This is a **small, narrow task**:
stay inside the files named here.

## Work narrowly
- Create the report `docs/reports/LOBBY_CAREER_BOUNDS_P1028.md` early and update it as you go.
- Use `rg -n` to find code and read ≤ 150-line ranges; never print whole large files.
- Build/test only what this task touches (commands below). Do not run the full suite.

## The lead (from `R1_REPORT.md` §4, "Career results")
An enrolled game server posts match results to the lobby (`src/Allegiance.Lobby/Career/CareerEndpoints.cs`,
`CareerContracts.cs`, stored by `CareerStore.cs`). The review suspected that (a) the server can post unbounded values
(kills, deaths, minutes, score…) for any account, and (b) summed minutes are parsed/accumulated as `int` in
`CareerStore.cs`, so large or repeated values can overflow (wrap negative or throw). Posting requires an
operator-issued server secret, so this is defence in depth against a buggy or compromised game server — Medium/Low.

## Do
1. **Confirm or refute** each part with a test against the real store/endpoint (look at
   `tests/Allegiance.Lobby.Tests/P808CareerTests.cs` and `P855SquadResultTests.cs` for how results are posted in tests).
   If a part is not real, say why with `file:line` and stop on that part.
2. **Fix what is real:** validate each numeric field of a posted result on entry (reject negatives, clamp or reject
   absurd values with a named constant per field and a short reason; pick limits no real match can exceed — say how you
   chose them, e.g. a match cannot last longer than N minutes), and make the totals overflow-safe (`long` or checked
   arithmetic with a clear failure, whichever fits the existing storage; if you change a stored type, make sure existing
   rows still load).
3. A rejected result must be a 4xx with a clear message and must not partially apply.
4. Existing career/squad tests must still pass.
5. Tests in a new file `tests/Allegiance.Lobby.Tests/P1028CareerBoundsTests.cs`.

## Commands (SDK `~/.dotnet/dotnet`)
- Build: `MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 ~/.dotnet/dotnet build AllegianceCore.slnx
  --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` — 0 warnings / 0 errors.
- Tests: `~/.dotnet/dotnet test tests/Allegiance.Lobby.Tests --no-build --filter "FullyQualifiedName~P1028"` 3×, then
  the whole `tests/Allegiance.Lobby.Tests` project once.

## Rules
Read `AGENTS.md` first. No `git stash`, no commits, no pushes, no personal paths/IPs/hostnames in any file, never
`pkill -f`. Leave changes in the working tree; do not leave this brief in the change set.

## Report
Per part: confirmed or not (with the test), root cause `file:line`, fix, limits chosen and why, tests with
Passed/Failed/Skipped counts, anything unfinished. Footer: "Native intent" (career stats are a port/lobby feature —
say so if native has no equivalent) and "Modern approach".
