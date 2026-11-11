# P999 offline civilization strategy research

Nothing in `src/` or the live MCP driver references this tool. It reads a core,
generates three deterministic policy variants, validates them with
`ResearchPlanCatalog.Validate`, and exports specifications and plan comparisons.
The scorer is a pure function; file I/O belongs to `Program.cs`.

```sh
mkdir -p .scratch/p999
timeout 300 "$HOME/.dotnet/dotnet" build tools/p999-civ-strategy/p999-civ-strategy.csproj \
  --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
timeout 600 "$HOME/.dotnet/dotnet" tools/p999-civ-strategy/bin/Debug/net10.0/p999-civ-strategy.dll \
  .scratch/p999 '<core.igc>' 999 18,4,5
timeout 180 python3 tools/p999-civ-strategy/audit.py '<saved-batch-directory>' \
  .scratch/p999/core.json .scratch/p999/audit
timeout 60 python3 tools/p999-civ-strategy/render.py .scratch/p999/core.json \
  .scratch/p999/audit docs/reports/P999_assets
```

Without the optional core argument the executable resolves PCore001 through
`ExternalPaths` / `local.env`. The opponent list is optional; omitted means all
other civs in the loaded core. Core names and IDs are never special-cased in the
scorer. The three profiles are skirmish, siege and economy; seed changes only a
bounded preference among utility scores. A live caller would choose a profile
from the match seed; no such caller is added here.

The scorer considers each target's prerequisite route, starting from a known
valid generated prefix. A backward bit-provider slice is replayed exactly;
invalid slices retain the full prefix. It selects the highest utility opening,
then finishes the reachable coverage tail using the precomputed opportunity
scores. This is an offline experiment, not a shortest-path proof or a live
commander. Target costs include the constructor bucket, but omit travel,
construction effects, funding delays and loss/rebuild risk. Power uses a
thirty-second ammo/energy window, fixed guns, fitting shields/missiles,
civilization GA, mobility, opponent defense classes, mining factors and income
indices. It omits hitboxes, turret crews, real delivery/attrition, booster flight,
and portfolio adaptation. See the report before proposing integration.

`audit.py` counts only finalized matches, preserving all side counts and missing
scores. It maps worker bot names using explicit side receipts and unique
starting-hull identity, rejects conflicting mappings, and filters stale generated
plan totals at rotations. Worker clock bounds use nearby timed diagnostics in
the same generation, at most 60 seconds apart. Five-minute hull-selection windows
start at that generation's first diagnostic; boundary/unknown rows stay marked.
Selections are not confirmation of purchases or time spent flying a hull.
`observations.csv` separately preserves rotation launch acknowledgments and
sampled actual hull observations. Neither source provides a complete per-hull
kill/death or income census. Raw worker lines, paths and bot names stay in scratch;
public outputs contain batch/worker identifiers and hashes only.

Tests (with artwork configured, no skipped scorer cases):

```sh
timeout 1200 "$HOME/.dotnet/dotnet" test tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj \
  --no-build --no-restore --filter FullyQualifiedName~P999CivStrategyTests
timeout 30 python3 -m unittest discover -s tools/p999-civ-strategy -p 'test_*.py' -v
```

The C# tests cover all ten core civs and all three profiles against both the
catalog and actual mission-side purchase gates, deterministic seed behavior,
input immutability, renamed/renumbered core records, disabled paths/research,
development-time perturbation and opponent-defense sensitivity. The Python
fixtures cover censored matches, draws, four-side rosters, duplicated results,
unknown scores, normalization denominators and clock/rotation boundaries.
