# CI-2 — `CleanCloneSkipGuardTests`: same verdicts, a fraction of the time

**Base:** master `43d11eb5` (worktree `allegiance-pi-ci2`, branch `pi/ci2`).
**Scope:** `tests/Allegiance.Core.Tests/CleanCloneSkipGuardTests.cs` only (plus this report,
`docs/reports/CI2_assets/equivalence.txt` and the `docs/TEST_FLAKES.md` entry). No `src/`,
`tools/`, tier-runner, limit or other test changed. Still a syntax-only Roslyn scan; no
`CSharpCompilation`/`SemanticModel`, no on-disk parse cache, no `git stash`.

Host: 24 hardware threads, load average 1–3 during the runs (other agents), 2026-10-21/22.

## 1. Result

| Measurement | Before (master `43d11eb5`) | After (CI-2) |
|---|---|---|
| Isolated exact method, 3× (TRX duration) | 3 s, 4 s, 4 s (wall 5.2–5.7 s) | **764 ms, 686 ms, 752 ms** (wall 1.9–2.0 s; final-tree re-run 746/757/773 ms) |
| Full Fast tier, this test's TRX duration | 8.13–10.82 s (one run **tripped the 10 s limit**) | **2.47 s** (PASS) |
| Full Fast tier result | 8.13 s PASS / 10.82 s FAIL | **9,946 tests, 0 failed, PASS** (`fast: 9946 tests, 286.7s`) |
| Work per test method | 1 BFS over the class member graph | 1 table lookup into a per-type closure |
| Member traversals | 4 per member (2 in `Add`, 2 in the method loop) + 1 per `Direct` call site `ToString()` | 1 cached traversal per member node; no `ToString()` |

Targets from the brief: **≤ 1 s isolated → 0.69–0.76 s ✓**, **≤ 3 s inside the full Fast tier →
2.47 s ✓**. The 10 s limit, the calibration and the tier runner are untouched.

## 2. Profile (measured, not assumed)

A temporary `Stopwatch` breakdown was added to the *unmodified* algorithm and the exact method was
run 3× (`CI2_PROFILE` dump; raw files archived in
`docs/reports/CI2_assets/equivalence.txt`). Times are milliseconds of wall time on the single test
thread, median of the three runs first.

Old (sequential) algorithm, 1,114 files / 233,095 lines / 11.5 MB / 1,174 top-level types /
13,212 member records / 6,309 `[Fact]`/`[Theory]`/`[Skippable*]` methods:

| Phase | run 1 (dump on) | run 2 | run 3 | share of run 2 |
|---|---|---|---|---|
| enumerate + filters (incl. all of the below) | 5048.9 | 4086.5 | 4662.0 | — |
| `File.ReadAllText` | 52.2 | 57.4 | 62.4 | 1.4 % |
| `CSharpSyntaxTree.ParseText` + root | 1555.1 | 1803.1 | 1494.5 | **44 %** |
| `Check` (all types) | 3434.8 | 2219.0 | 3098.3 | **54 %** |
| ↳ `Add` walks (`Direct` + `Refs` per field/prop/method/nested ctor + outer ctors) | 1340.3 | 918.3 | 1181.2 | 41 % of `Check` |
| ↳ test-method walks (`Direct` + `Refs` again, same nodes) | 689.2 | 423.9 | 619.4 | 19 % of `Check` |
| ↳ per-test `Reach` BFS | 92.8 | 80.0 | 146.2 | 3.6 % of `Check` |
| ↳ `Traits` / attribute-name scans | 51.0 | 85.6 | 98.5 | 3.9 % of `Check` |
| ↳ remainder (dictionary/HashSet churn, `OfType`/`Where` enumerators, iterator machinery) | — | ~711 | — | 32 % of `Check` |

All four candidate costs were real, in this measured order:

1. **(a) sequential parse — ~44 %.** 11.5 MB of C# parsed on one thread; grows with the suite.
2. **(b) double walks — ~60 % of `Check` (~32 % of total).** `Add` walked every member for
   `Direct` *and* `Refs`, then the test loop walked the same method nodes again with `Direct` +
   `Refs` (four traversals per test method, two per other member).
3. **(d) `ToString()` in `Direct` — inside (b).** Every `MemberAccessExpressionSyntax` invocation
   allocated `ma.Expression.ToString()`; also `ma2.Expression.ToString()`, `oc.Type.ToString()` and
   `a.Name.ToString()` in the hot loops.
4. **(c) per-test `Reach` — only ~3.6 % of `Check` (~2 % of total).** The hypothesis that the
   repeated BFS was a large quadratic cost is **not** what the profile shows: 6,309 BFS runs cost
   ~80 ms total. It was removed anyway (a per-type closure replaces it), but it was never the
   gate problem — (a) and (b) were.

## 3. The change (one file)

1. **Files are parsed and scanned in parallel** (`Parallel.ForEach` over the filtered file list;
   ~4 s of independent CPU work). Violations go into a `ConcurrentBag`, then to a `List` and
   `Sort(StringComparer.Ordinal)`; the failure message text and its `"\n  "` join are unchanged and
   now deterministic (the old order was filesystem enumeration order).
2. **One cached traversal per member node.** `Direct` + `Refs` became a single `Walk(node)` that
   yields `(Facts, HashSet<string> refs)` in one `DescendantNodes()` pass; results are memoised by
   node in `measured`, so the member graph (`Add`) and the test-method loop reuse the same walk.
   `Add` copies a cached ref set before merging duplicate names/overloads, so cached sets are never
   mutated.
3. **Per-type transitive closure instead of per-test BFS.** One least-fixed-point pass over
   `facts[n] = direct[n] | facts[refs[n]]` (≤ 5 iterations, facts grow monotonically), then
   `Reach(start, direct) = direct | ⋃ closure[start]`. Same reachable-fact set as the BFS (union
   over the transitive closure), 19–25 ms per whole run instead of 80–146 ms of BFS.
4. **Structural name comparison instead of `ToString()`.** `EndsWithName(expr, "Require")` replaces
   `target == "Require" || target.EndsWith(".Require")` (matches `Require`, `X.Require`,
   `A.B.Require`), and `IsDottedName(expr, [...])` replaces the exact-match
   `ma2.Expression.ToString() is "Require" or "Allegiance.TestInfra.Require"` for
   `ArtworkAvailable`. `ObjectCreationExpressionSyntax.Type.ToString() is "GraphicsDevice" or
   "GraphicsDeviceManager"` became an `IdentifierNameSyntax` identifier check with the same accepted
   forms. Rules, `Allowed` list, message strings, per-type scoping, trait filtering, `SDL_VIDEODRIVER`
   literal check and `TestArtRoot` identifier check are byte-for-byte unchanged.

New (parallel) profile, same instrumentation on the rewritten code. Per-phase ms are **summed over
the 24 worker threads**, so they are CPU-time, not wall time (they inflate under memory/CPU
contention); the TRX/wall number is the gate-relevant one:

| run | wall (TRX) | read Σ | parse Σ | check Σ | ↳ member walks Σ | ↳ closure Σ | ↳ test loop Σ |
|---|---|---|---|---|---|---|---|
| 1 (dump on) | 805 ms | 254.5 | 6275.4 | 8647.2 | 5731.0 | 19.3 | 284.2 |
| 2 | 756 ms | 180.1 | 5640.5 | 8902.5 | 6346.5 | 25.4 | 215.8 |
| 3 | 765 ms | 223.1 | 5548.7 | 8868.2 | 6152.4 | 18.7 | 244.1 |

## 4. Equivalence proof (old vs new)

A temporary `CI2_DUMP` hook (removed before the final build) wrote a sorted line per
**top-level type**, per **member** (name, packed `Facts`, sorted refs), per **ctor aggregate** and
per **`[Fact]`/`[Theory]`/`[Skippable*]` method** (file:type.method@span, plain/skippable,
`[Fact(Skip=…)]`, raw + trait-masked `Facts`, requires/lane traits, violation text), with the
`Allowed` filter **off**. Old and new runs produced 20,695 lines / 5.19 MB each:

```
sha256 old.txt        343ab8ca31cd0066236cac0d0fdac7fc158b64272ec3b647fa722425365845df
sha256 new.txt        343ab8ca31cd0066236cac0d0fdac7fc158b64272ec3b647fa722425365845df
diff old.txt new.txt  -> empty (byte-identical, already sorted)
```

So on the real tree the per-test `Facts`, the member graph, the refs of every member, the ctor
aggregate and the full violation list are identical, not just the final pass/fail. Note this is
stronger than "same verdict set": the `Allowed`-suppressed `UpdateCadenceTests.Floor_…` violation is
still produced with the same text when the filter is off (1 violation tree-wide), and every
`Artwork`/`Gpu`/`RequiresArtwork`/`RequiresGpu`/`Skips` bit agrees for all 6,309 test methods.

**Seeded violations.** A scratch file `tests/Allegiance.Core.Tests/CI2SeedTests.cs` (deleted before
the final build) seeded one violation per rule, and both algorithms were run on it:

```csharp
private static string Helper() => TestArtRoot.Value;
private static readonly string HelperField = Helper();

[Fact]                    public void Seed1_plain_fact_calls_require_artwork()      { Require.Artwork(); }
[SkippableFact]           public void Seed2_skippable_reaches_artroot_through_field_helper() { Assert.NotNull(HelperField); }
[Fact]                    public void Seed3_plain_fact_sets_sdl_videodriver()        { Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "offscreen"); }
```

Old and new dumps with the seeds (`20,704` lines) are also byte-identical
(`a3a17abb…`). Both report all three, with the same text (full evidence in
`docs/reports/CI2_assets/equivalence.txt`):

```
CI2SeedTests.Seed1_… calls Require.*()/Skip.If*() from a plain [Fact]/[Theory] — use [SkippableFact]/[SkippableTheory]
CI2SeedTests.Seed2_… reads the artwork corpus (TestArtRoot) but never calls Require.Artwork()
CI2SeedTests.Seed3_… creates a real GL device but is a plain [Fact]/[Theory]
```

The shipped (hook-free) code with the seeds present printed exactly those three and filtered the
`Allowed` entry — identical to the old violation set minus the one `Allowed`-listed test.

## 5. Verification (light gate)

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx -m:1 -p:AutoRestoreMGCBTool=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** |
| Exact method isolated 3× (`--filter "FullyQualifiedName~CleanCloneSkipGuardTests"`) | Pass, **764 / 686 / 752 ms** (before: 3 / 4 / 4 s) |
| `tools/run-tests.sh --tier fast` | **`fast: 9946 tests, 286.7s, PASS`**, 0 failed; this test **2.472 s** in `fast/Allegiance.Core.Tests-main.trx` (before: 8.13–10.82 s), `P744 factor=0.972; Fast limit=10.000s` unchanged |
| Clean-clone guard run | **Passed, 746 ms**, 0 failed, 0 skipped |
| Showcase never-parks 5× | **Not needed** — test-only change, no product/timing code touched |

The literal command from the brief,
`tools/run-ci-gate.sh --clean-clone --no-build --filter "FullyQualifiedName~CleanCloneSkipGuardTests"`,
cannot work as written: `run-ci-gate.sh` appends the release-workflow filter itself, so the extra
`--filter` makes `dotnet test` fail with *"Option '--filter' expects a single argument but 2 were
provided"*. The guard was therefore run with the **same clean-clone environment** the script
exports (`ALLEGIANCE_*_PATH` → `/nonexistent/allegiance-ci-clean-clone`,
`__EGL_VENDOR_LIBRARY_FILENAMES` → same, `DISPLAY`/`WAYLAND_DISPLAY` unset) and the required filter
against `AllegianceCore.slnx --no-build`; result above. (`--clean-clone` alone runs the whole gate,
which is unaffected by this test-only change and was already covered by the Fast tier run.)

## 6. Follow-ups (not changed, out of scope)

* `EndsWith(".Require")` accepts any receiver whose last identifier is `Require` (`this.Require`,
  `someField.Require`) — kept as-is. `ArtworkAvailable` matching is exact-name, so
  `X.Require.ArtworkAvailable` is (still) not recognised.
* `TestArtRoot` is matched as a bare identifier anywhere, including `Something.TestArtRoot.Value`
  (the identifier node inside a member access) — kept as-is.
* `Require.GraphicsDeviceManager()` / `new GraphicsDeviceManager` are *not* treated as
  `Require.GraphicsDevice`; only the exact member names in `Direct`/`Walk` count. Some GPU-adjacent
  helpers (`Require.NativeSourceAvailable`) are not part of rule 3. All unchanged.
* Only plain-syntax positions are matched: `global::Require.Artwork()`, `Require?.Artwork()` or a
  call through a `using static` alias are invisible to the scan (the clean-clone gate script stays
  authoritative). Unchanged; the old string comparison had the same blind spots (and, unlike the
  structural comparison, treated `Require . Artwork()` with internal whitespace differently — no
  such call site exists in the tree, which the byte-identical dump proves).
* The scan is now parallel; a future `tests/` file that fails to parse would surface as an
  `AggregateException` from `Parallel.ForEach` instead of the first parse exception. No test file
  fails to parse today (the full Fast tier and the guard pass).
