# P175 — stop hardcoding `/home/domingo/...` artwork/native-source paths

Domingo caught this directly (not delegated — implemented in-session):
`/home/domingo/Development/Artwork` (the separate asset checkout) and
`/home/domingo/Development/Allegiance` (the native C++ reference tree) were
hardcoded absolute paths baked into the repo — a real blocker for anyone
else who clones it ahead of the eventual open-source release. The initial
sweep found 5 locations; Domingo caught that this undercounted badly — a
full grep found **86 individual test files** each carrying a byte-for-byte
identical copy-pasted line, not just the 5 originally spotted. Total: 92
files touched.

## What changed

### The 86 test files (`tests/Allegiance.Core.Tests/*.cs`)

Every file had:
```csharp
private const string ArtRoot = "/home/domingo/Development/Artwork";
```
Mechanically replaced (one `sed` pass across all 86, then verified with a
fresh grep) with:
```csharp
private static readonly string ArtRoot = TestArtRoot.Value;
```
New shared helper, `tests/Allegiance.Core.Tests/TestArtRoot.cs`:
```csharp
internal static class TestArtRoot
{
    public static readonly string Value =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
        ?? "/home/domingo/Development/Artwork";
}
```

**One file didn't match the mechanical pattern** —
`CarrierLandingTriggerTests.cs` used the path as a *method default
parameter value* (`ProductionStyleArtPathCache(string artPath =
"/home/domingo/Development/Artwork")`), not a field. A default parameter
value must be a compile-time constant in C#, so `TestArtRoot.Value` (a
`static readonly` field) can't be substituted directly. Fixed by hand:
changed the parameter to `string? artPath = null` with
`artPath ??= TestArtRoot.Value;` inside the method body. Confirmed its one
call site (`Server.cs:249`) passes no argument, so behavior is unchanged.

**One file needed a second fix beyond the mechanical pass** —
`LiveRankTests.cs` had `private const string RanksFile = ArtRoot +
"/ranks.txt";`, a `const` derived from `ArtRoot`. Once `ArtRoot` became
`static readonly` (no longer a compile-time constant), this failed to
compile (`CS0133`). Changed `RanksFile` to `static readonly` too.

### The 5 originally-found one-offs

- `Corpus.cs` / `CvhCorpus.cs` — `Roots` array now reads
  `ALLEGIANCE_ARTWORK_PATH`/`ALLEGIANCE_NATIVE_SRC_PATH` with the old
  hardcoded values as fallback defaults. Also fixed a real silent-failure
  trap found while here: a missing root was previously just skipped
  (`if (!Directory.Exists(root)) continue;`), so a contributor without
  either tree got an **empty corpus with no error** — tests iterating
  `Corpus.Files`/`Corpus.Theory()` would silently do nothing instead of
  failing meaningfully. Now `Discover()` throws a `DirectoryNotFoundException`
  naming both env vars if *neither* root exists. Confirmed via grep that no
  existing test depended on `Corpus.Files` potentially being empty before
  making this change.
- `src/Allegiance.Server/ServerConfig.cs:43` — `ArtPath`'s default value,
  same `??` pattern.
- `tools/vizfight/Program.cs:35` — `ArtRoot` changed from `const` to
  `static readonly` (a `const` can't call `Environment.
  GetEnvironmentVariable`).
- `tools/corpusvalidate/Program.cs`'s `DefaultRoots()` — same `??` pattern;
  this tool already had a `--root` CLI override, so this only improves the
  default's convenience.

### `server.json` — untouched; new example file added

`src/Allegiance.Server/server.json` is Domingo's own live, checked-in dev
config, read as-is by existing workflows — not edited. Added
`src/Allegiance.Server/server.example.json` (same shape, `artPath` replaced
with `/path/to/Artwork`) as a starting point for a new contributor.
`README.md` gained a short "Artwork/native-source paths" section
documenting both env vars and the example file.

## Verification

- `dotnet build` (both whole-repo and scoped to
  `tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj`) — clean,
  0 warnings, 0 errors (`TreatWarningsAsErrors=true` repo-wide, so this
  matters).
- Final grep sweep confirming completeness:
  - `"/home/domingo/Development/Artwork"` appears in exactly one file left:
    `TestArtRoot.cs`'s own fallback default.
  - `"/home/domingo/Development/Allegiance"` appears in exactly two files
    left: `Corpus.cs`/`CvhCorpus.cs`'s own fallback defaults.
- 3 consecutive full-suite `dotnet test` runs on
  `Allegiance.Core.Tests.csproj` (scoped deliberately — a second, unrelated
  delegated task was mid-flight on `Allegiance.Lobby`/`Allegiance.Lobby.
  Tests` in the same working tree at the time, so verification was scoped
  to avoid any interference from that in-progress work): run 1 clean
  (2816/2816), run 2 clean (2816/2816), run 3 hit one failure —
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_
  audience`, one of the five documented pre-existing flakes — confirmed
  flaky in isolation too (7/8 passed alone), correlated with two concurrent
  QEMU VMs running at the time (one at ~11% CPU). Run 4 (retry) clean
  (2816/2816). Zero novel failure modes across all four attempts.
- Explicit behavioral smoke tests: (a) `ALLEGIANCE_ARTWORK_PATH=/nonexistent
  ALLEGIANCE_NATIVE_SRC_PATH=/nonexistent` correctly throws the new loud
  `DirectoryNotFoundException` naming both env vars (22 tests failed with
  that exact message, confirming the override is actually read and the new
  guard actually fires); (b) `ALLEGIANCE_ARTWORK_PATH` set explicitly to the
  real path behaves identically to the unset default (confirmed via
  `BotFlightTests`, 5/5 clean).

Only `Allegiance.Core.Tests`-scoped files, `src/Allegiance.Server/
ServerConfig.cs`, `src/Allegiance.Server/server.example.json`, `tools/
corpusvalidate/Program.cs`, `tools/vizfight/Program.cs`, and `README.md`
were touched — `AllegianceCore.slnx` and everything under `src/Allegiance.
Lobby`/`tests/Allegiance.Lobby.Tests` (the other in-flight task) were left
alone and staged/committed separately.
