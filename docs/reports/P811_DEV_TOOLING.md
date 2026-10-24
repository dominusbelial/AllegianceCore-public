# P811 — Developer tooling: GLOSSARY index, one-command local stack, reproducible generated maps

Branch: `hermes/p811` (worktree `~/allegiance-hermes-p811`; base `master` at
dispatch `23d9422d` "P786: HUD wire data…").
Status: **complete** — all three deliverables implemented, verified, and left
in the working tree (no commit, no push).

This task is pure developer tooling, so it has no parity "rows" of its own to
close; the row-shaped reporting format is mapped onto the three deliverables
below, each with its native rule + cites, port before/after, tests, and gate
numbers.

---

## Deliverable 1 — `GLOSSARY.md` (kept-current per-domain index)

**Files added:** `GLOSSARY.md` (root), `tools/check-glossary-links.py`,
plus a `GLOSSARY.md link check` step in `.github/workflows/ci.yml`.

- Per-domain sections: simulation, wire/FEDMSG, server, client & HUD
  (Classic/Modern), bots/MCP, lobby, tools/proofs, parity matrices, rig/runner,
  dev-tooling, and map generation. Every full repo path cited is real — the
  link-check (`python3 tools/check-glossary-links.py`) asserts existence and
  passes (95 distinct paths).
- **Maintenance rule:** the glossary header states *"update this glossary when
  you add a system"* and the CI link-check enforces path validity. The brief
  asked for a short rule in `AGENTS.md`/lane rules; `AGENTS.md` is a protected
  agent-instruction file here (write refused), so the rule lives in the
  `GLOSSARY.md` header (the natural in-repo "lane rules" location) — worth
  porting to the lane-rules doc (`pi-lane-rules.md`) on the dispatcher side.
- **Under line budget:** `wc -l GLOSSARY.md` = 268 lines (limit ~1,500).
- **Hand-check:** every cited path verified by the script; the quick-reference
  table uses full paths.

**Gate:** glossary link-check is now part of `ci.yml` (a hard gate, like the
Fast-tier non-corpus filter).

---

## Deliverable 2 — one-command local stack: `scripts/dev-stack.sh`

**File added:** `scripts/dev-stack.sh`, plus `QUICKSTART.md` (documented, and
pointed to from `README.md`).

- `up|down|status`; server + lobby + optional MCP bots (`--bots N`) + optional
  client (`--client`). Default is **local processes** (real Release binaries,
  real artwork); `--docker` reuses the existing `docker-compose.yml`.
- Also supports `--map-gen <TYPE>` and `--map-seed <N>` so a reproducible
  generated map is one flag away (ties deliverables 2 and 3 together).
- **Process hygiene (lane rules):** no `pkill -f`/`pgrep -f`; state/PIDs are
  recorded in `.scratch/dev-stack/` (git-excluded via `.git/info/exclude`) and
  `down` kills exactly the PIDs `up` started. No `/home/...` paths in the
  script (it sources the gitignored `local.env` like `tools/run-tests.sh`).

**Smoke (verified on Linux):**
```
scripts/dev-stack.sh up --local --map-gen Brawl --map-seed 812377 --bots 1
  → server listening on 127.0.0.1:6073
  → [boot] game 'Dev Stack' generating map: topology=Brawl seed=812377 (1 sectors / 0 warps / 2 stations / 23 asteroids)
  → MCP RESULT spawn_scenario isError=False … "spawned":1 per side (hulls mcp-b001-3654, mcp-b002-2031)
scripts/dev-stack.sh status   → LOBBY/SERVER/MCP RUNNING
scripts/dev-stack.sh down     → clean; no leftover processes
```
Evidence logs: `.scratch/dev-stack/default.logs/{server.out,mcp_out.log}`
(scratch, git-excluded — not committed; summarized above in the report).

**Gate:** dev-stack smoke (up → a bot joins → down) passed on Linux.

---

## Deliverable 3 — reproducible generated maps (`--map-seed N`)

**Native rule + cites.** Native's map maker draws every piece of the map from
a per-run RNG stream — the generator seeds its RNG once and `Generate*`/
`Populate*` methods consume `rand()` (`ImapMakerIGC::Create` /
`CmapMakerIGC::GenerateMission`, cited throughout
`src/Allegiance.Core/MapGen/MissionMapGenerator.cs`, e.g. `mapmakerigc.cpp:168-203,
340-483, 869-893, 884-894, 908-911, 918-923`; cluster-name draw
`mapmakerigc.cpp:119-128`; Brawl scatter count `mapmakerigc.cpp:1790-1793`
`randomInt(3,5)`; asteroid type coin-flip `asteroidigc.cpp:231-255`). Fixed
seed ⇒ identical rand() stream ⇒ identical map.

**Port before.** The generator drew from `Random.Shared` directly
(`MissionMapGenerator.cs`, `MapGenContext.GetNewClusterName`,
`NativeAsteroidTypes.GetRandomType`), so every run produced a different map
and a rig-observed generated map could not be reproduced.

**Port after.**
- `src/Allegiance.Core/MapGen/MapRandom.cs` (new): `IMapRandom`
  (`Next()`, `Next(min,max)`, `NextDouble()`) with `SharedMapRandom`
  (delegates to `Random.Shared` — bit-for-bit the historic behaviour) and
  `SeededMapRandom` (`System.Random(seed)`), plus `MapRandomFactory.FromSeed`.
- `MapGenParams.Seed` (nullable int) → `MissionMapGenerator.Generate` creates
  the rng and threads it through `MapGenContext` and the generator; every draw
  (cluster decoration, names, asteroid placement/types, Brawl scatter) goes
  through it. `RandomDirection`/`RandomAsteroidPosition` became instance
  methods on the same rng. `NativeAsteroidTypes.GetRandomType`/`ResolveTypeIndex`
  gained an optional `IMapRandom` (null ⇒ `Random.Shared`, unchanged).
- Server: `MapGenConfig.Seed`, `Server.Lifecycle.ToCoreParams` passes it, and
  `ServerConfigLoader.ApplyCli` accepts `--map-seed N` (applies to every
  configured `mapGen` game; rejects a file-only config with a clear message).
- **Logging (scope-3):** every generated map logs **one line** with seed +
  parameters:
  `[boot] game 'Dev Stack' generating map: topology=Brawl seed=812377 (…sectors/…warps/…stations/…asteroids)`.
  Unseeded runs log no `seed=`.

**How the data reaches the client.** A generated map flows through the same
`Mission.Assemble` path as a file-decoded map (existing native-field/mission
records — `MissionMap` → assembled `Mission`); the client receives the normal
`CLUSTERINFO`/`STATIONS_UPDATE`/`ASTEROIDS_UPDATE`/`SHIP_UPDATE` wire data. The
seed affects only generation; there is no new wire message.

**Tests (all new, pass 3× alone).** `tests/Allegiance.Core.Tests/MapGenSeedReproTests.cs`:
- `SameSeed_ProducesIdenticalMap` (canonical structural fingerprint) — proves
  same seed ⇒ same map.
- `DifferentSeed_ProducesDifferentMap` — the seed actually matters.
- `ApplyCli_MapSeed_OverlaysOntoGeneratedMap` / `_RejectsFileOnlyConfig` —
  the server CLI plumbing.
- Corpus-dependent mapgen tests (`[Trait("Requires","Artwork")]`) run here on
  the corpus; on CI (non-corpus gate) they are filtered, matching the existing
  MapGen tests. The two `ApplyCli` tests are corpus-free and run everywhere.

**Regression:** the existing 99 MapGen tests still pass unchanged
(filter `FullyQualifiedName~MapGen` → 99/99).

**Classification.** *Design:* using `Random.Shared` was a port simplification,
not a native rule — native is per-run-seeded, so threading a deterministic
stream is the faithful behaviour and a strict improvement (reproducibility).
*Modern better approach:* `System.Random(seed)` is the minimal deterministic
PRNG; a hash-seeded `xoshiro`-style PRNG would be faster/stronger but is
unnecessary for map generation and would diverge from native's `rand()`-shaped
draw pattern.

**Gate numbers:** same-seed test 3× alone = pass (3/3); mapgen regression
99/99; build 0 warnings/0 errors; `tools/build-tools.sh` and the Server AOT
publish reported below.

---

## Verification (light gate) — summary

| Gate | Result |
|---|---|
| Whole-solution build 0/0 | PASS (0 warnings / 0 errors) |
| `tools/build-tools.sh` (every tools/ project) | PASS — "build-tools: all 73 tool projects built", 0 errors |
| Server AOT publish (`-p:PublishAot=true`, no IL2026/IL3050) | PASS — native `Allegiance.Server` produced; AOT_EXIT=0 |
| New tests (MapGenSeedReproTests) 3× alone | PASS 4/4 ×3 |
| One Fast-tier run (`tools/run-tests.sh --tier fast`) | PASS* — 10169 tests / 299 s; all P811-touched projects green (Core.Tests 5085/5085, ClientCore.Tests 1742/1742). One unrelated MonoGame HUD test (`Hud12bReviewFixTests.Chat_line_truncation…`) failed once under load but passes 3/3 alone → load flake (see `docs/TEST_FLAKES.md`), not a regression |
| dev-stack smoke (up → bot joins → down) | PASS |
| GLOSSARY link-check (in CI) | PASS (95 paths) |

`*` fast tier overall exit was 1 solely due to that one load flake; it is not
related to P811 (no HUD/chat/rendering change here) and passes in isolation.

## Evidence / scratch

- `.scratch/dev-stack/` — dev-stack smoke state + logs (git-excluded).
- `.scratch/aot/` — the AOT publish output (git-excluded, deleted at the end).
- No large media committed; this task adds no new images.

## Unfinished / notes

- `AGENTS.md` is a protected agent-instruction file on this box, so the
  glossary-upkeep rule could not be written there; it lives in the
  `GLOSSARY.md` header instead. Recommend adding one line to the lane-rules
  doc: "update GLOSSARY.md when adding a system".
- Docker `--docker` path is provided and `docker compose config` is sane, but
  the full container smoke (heavy image build + artwork bind-mount) was not
  run in this pass; the process-based path is the verified default and the
  documented recommendation. `docker/README.md` describes the artwork mount.
- Skirmish `Seed` already flows as `ServerConfig.BenchmarkSeed`; a skirmish
  that opts into a generated map gets the same reproducible path through
  `MapGenConfig.Seed`. No skirmish-specific UI change was made.
