# P795 — remove the MCP arena mode; march is the only battle start

Base: `master` at dispatch, `37ea36f2` (re-checked `git log -1` before starting).
Branch/worktree: `pi/p795`. No commit or push; all changes are in the working tree.

## Why

Domingo (2026-10-22): *"we need to work on arena mode, is broken … we probably should
remove it … and focus on scaling the full normal mode."* A 200v200 arena at Alderan
collapsed into a home-sector death loop within 2 minutes (local hero session: 7,880
uncredited deaths in 12 min, ~700/min at the two home stations), relaunched bots flew
home→arena, and the finale/strike/support logic pulled arena bots out. Fixing it (P794)
was cancelled: we will not use the mode.

P795 removes the MCP arena battle start completely. **March is the only mode.** All
server/Core/client code is untouched; the server `relocate` admin cheat stays.

## What was removed (base line numbers from `37ea36f2`)

### `tools/allegiance-mcp/McpTools.cs` (−67/+39)

| Removed | Base lines |
|---|---|
| Tool description "Supports staging/arena"; `mode` enum `["march","arena"]`; the `arena` schema property | 68, 87–88 |
| Homogeneous path arena setup: `IsArenaMode`, `arena` gating of staging/routes, `arenaSpots`/`arenaNote`, mirrored placement, per-slot arena pose, `useStaging: staging, arena: …` spawn args, `mode:"arena"` + `arena`/`arenaNote` output fields | 455–473, 482, 506–507, 517–521, 526, 547–550 |
| Modern/mixed-fleet path arena setup and outputs (same machinery over resolved fleets, bomber-plan skip, `ResolveScenarioTarget(arena, …)`) | 571, 636–651, 674–678, 709, 726–738, 754–755, 761, 778–784 |
| `IsArenaMode(args)` | 1398–1402 |

Replacement: `ArenaRequested(args)` + `ArenaRemovedError`, checked at the top of
`SpawnScenario` before either dispatch path. A request with `mode:"arena"` (any case)
or the legacy `arena:true` now returns:

> `spawn_scenario: arena mode was removed in P795; use mode:"march" (the only battle
> start). Both fleets now launch at home and march through the alephs to target_sector.`

The `mode` schema now advertises `["march"]` only, and both tool descriptions carry the
removal history.

### `tools/allegiance-mcp/BotManager.cs` (−203/+13)

| Removed | Base lines |
|---|---|
| `Relocator` field + doc, default `CreateNetworkedRelocator()` install in the ctor | 165–172, 216–225 |
| `RelocateTimeoutSeconds`, `CreateNetworkedRelocator()` (wire `relocate` sender + `SET_CLUSTER` wait) and its `[p554] arena:` logs | 233–285 |
| `arena`/`arenaPosition`/`arenaFacing` parameters on `SpawnBot` / `SpawnBotCore` and forwarding | 488, 495, 505 |
| `ActiveBot.Arena/ArenaPosition/ArenaFacing` bookkeeping and the post-registration arena relocation block, incl. every `FailArena` error and the `[p539d] arena:` success log | 681–685, 697–729 |
| `FailArena` | 766–774 |
| `ProbeSectorCenter` (only used for arena mirroring) | 874–896 |
| `ArenaOffset = 650f` | 4354–4356 |
| `ComputeArenaPlacements` | 4688–4718 |
| Arena branches/conditions: finale bomber `bot.Arena || …`, bomber candidate gate, `!bot.Arena && RecordCombatSectorTransition`, asset-defence gate, the arena engage-in-place block | 2859, 2995, 4787, 4807, 4821–4828 |
| `ActiveBot` arena fields | 6184–6187 |
| Arena wording in the TTK/beacon comments | 133, 660 |

At `Arena == false` every removed read was already false, and every simplified
condition is equivalent (`!x && A → A`, `x || A → A`, `if (x) {…} → dead`,
`arena ? r : ResolveRoleTarget(...) → ResolveRoleTarget(...)`).

### Arena branches in the other partials

| File | Base lines | Removed |
|---|---|---|
| `BotManager.Defense.cs` | 45, 55, 194 | `!bot.Arena` / `bot.Arena` in `DefenseAvailable`, `UpdateDefense`, `TryDriveDefense` |
| `BotManager.Finale.cs` | 45, 349, 369, 415 | `FinaleOriginal.Arena`, snapshot/restore, `bot.Arena = false` on finale refit |
| `BotManager.Siege.cs` | 40, 42 | `Arena` in the strike snapshot, reset on strike assignment |
| `BotManager.Lifecycle.cs` | 214, 296–297 | bomber-target `!bot.Arena`; `ResolveScenarioTarget(bool arena, …)` parameter and arena short-circuit |

Also removed the only remaining arena log tags, `[p539d] arena` and `[p554] arena`.

## What was kept, and why

* **The server `relocate` admin cheat** (`src/Allegiance.Server/Server.AdminCheats.cs`
  P554/P602/P655) and all of `src/` — untouched. Live proofs and tests still use it
  (`P554AdminCheatLiveTests`, `LiveRipcordTests`, `P540EjectLiveProofTests`,
  `MineCombatTests`, `P655GameStateIntegrityTests`, …). Only the MCP's private
  networked relocator (a client of that cheat) was removed.
* **March (normal) mode** — `staging` (P539d), multi-route wings (P635), bomber
  enemy-home routing (P637), finale/strike/support logic, defence, travel telemetry,
  TTK telemetry, `spawn_bot` and all non-scenario tools. No march decision was changed.
* **`P402DebugMapGenArenaLiveTests` and `LiveP388DebugNoGameOverTests`** — these test
  the server's *debug-mode generated test arena* (`liveSettings.debugMode` + MapGen),
  a different, still-supported feature. They never used the MCP arena mode; deleting
  them would have dropped unrelated server coverage. Left intact.
* **Native training "Practice Arena"** (README, Mission 6) and the P402 generated-arena
  compose variant (GUIDE) are unrelated native/server features; left intact.
* Historical material under `docs/reports/`, `docs/prompts/` and `docs/reviews/` still
  mentions the old arena mode. It is a record of past tasks, not live documentation;
  not rewritten.

## Tests removed / changed

| Test | Change | Reason |
|---|---|---|
| `P539dFairContactDecisionTests.ArenaPlacements_AreMirroredAndEquidistantFromCentre`, `ArenaPlacements_SameSideBotsSpreadApart` | **removed** (2 tests) | Their only subject, `ComputeArenaPlacements`, was deleted with arena mode. |
| `P539dFairContactLiveTests` arena branch, `WireArenaRelocator`, `ServerSectorById`, `_reloc`, `ALLEGIANCE_P539D_ARENA_*` env, `[p539d] arena` log filter | **removed**; `Record_one_run` now records march only (`travel`/`staging`) | The harness drove MCP arena via the in-process relocator. Travel/staging measurement (arrival tables, routes, collision/death accounting) is kept unchanged. |
| `BotFixRegressionTests.FailedArena_RemovesRegisteredEntry_AndReportsFailure` | **removed** | Tested the deleted `BotManager.FailArena`. Orphan-cleanup on spawn failure is still covered by `PendingOwnership`/spawn-failure tests. |
| `BotFixRegressionTests.Arena_AllRolesUseArenaSector` | **removed** (3 theory cases) | Tested the removed arena short-circuit in `ResolveScenarioTarget`. |
| `BotFixRegressionTests.MissingTarget_EngagesInPlace` | **updated** to the new `ResolveScenarioTarget(role, rendezvous, enemyHome)` signature | Signature change only; same assertions. |
| `P554AdminCheatLiveTests.Mcp_arena_against_a_refusing_server_returns_an_explicit_error` | **removed** | It booted a server solely to prove the MCP arena error path. The server-side refusal is still covered by the 7 remaining P554 tests (non-privileged, disabled option, secret required, ownership rules), all passing. Removed its now-unused `Ticker` helper and `Allegiance.Tools.McpSpawner` using; reworded two comments. |
| **New** `P795ArenaRemovedTests` | **added** (9 cases) | 6 arena requests (`mode:"arena"`, `"ARENA"`, legacy `arena:true`, mixed-fleet, showcase, explicit fleet) must return the removal error naming `march`; 3 counter-cases prove `mode:"march"`, `arena:false` and the default are not swallowed by the guard. |

No other test referenced the MCP arena mode. All other `arena` hits under `tests/` mean
the server debug arena (sometimes only as a local variable or test name) or native
training Mission 6 (kept).

## Docs / scripts

* `README.md` — bot section no longer says "arenas"; the 200v200 example explains that
  `march` is the only battle start since P795 removed arena.
* `docs/GUIDE.md` — the MCP driver "scenarios and arenas" → "scenarios"; the P402
  generated-arena compose file remains (server feature).
* `scripts/repro_p593b_v{4,6,7,8,9,10,11,12}.sh` — convert the arena reproduction
  invocations to `mode:"march","staging":false` (quick contact, no arena) and update
  their headers/echo lines.
* `scripts/p604b-repro.sh` — drop the `arena` argument and the `ARENA` variable.
* `scripts/proof-p640.sh` — the 15v15 observer proof now spawns `mode:"march"`.

## Verification (light gate)

Environment: `~/.dotnet/dotnet` 10.0.400 explicitly; artwork/native paths from
`local.env`; no remote hosts; no `git stash`.

| Gate | Result |
|---|---|
| Whole-solution build (`AllegianceCore.slnx`) | **PASS — 0 warnings / 0 errors** |
| `tools/build-tools.sh` | **PASS — all 70 tool projects built** |
| AOT publish | **not required** — no `src/Allegiance.Server`, `src/Allegiance.Core` or client file changed |
| `P795ArenaRemovedTests` | **3× alone PASS — 9/9 each** |
| `P539dFairContactDecisionTests` | **3× alone PASS — 10/10 each** |
| `BotFixRegressionTests` | **3× alone PASS — 135/135 each** |
| `P554AdminCheatLiveTests` | **3× alone PASS — 7/7 each** |
| `P539dFairContactLiveTests.Record_one_run` (staging) | **3× alone PASS** (size 3, 60 s; plus one 90 s before/after pair) |
| Showcase never-parks (`Local_showcase_never_parks_with_target_for_five_seconds`) | **base 6/6 PASS; after 5/5 PASS** |
| Fast tier (`tools/run-tests.sh --tier fast`) | **PASS 10,020 tests** (first run) and **PASS 10,023 tests** (final run, 281.8 s) |

Fast-tier note: the middle run had two failures in unrelated areas —
`P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`
and `ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy`. Both pass
**3/3 alone**, so they are the documented shared-machine load flake, not this change.
The final full-tier run was clean.

### Showcase never-parks (base vs after)

Each run passes the test's own bar: `reached ≥ 24/30`, real gunfire/damage,
`idleWithTarget.currentCount == 0`, `botsLastMinute == 0`, no sim-pass failures.

| Run | sec | samples | reached | longest speed<1 with target | shots | hits | kills |
|---|---:|---:|---:|---:|---:|---:|---:|
| base (detailed) | 109.4 | 1093 | 28/30 | 2.006 s | 2770 | 1202 | 10 |
| after 1 | 107.3 | 1072 | 27/30 | 2.107 s | 2479 | 898 | 8 |
| after 2 | 111.3 | 1111 | 28/30 | 1.924 s | 3477 | 1603 | 10 |
| after 3 | 109.3 | 1091 | 28/30 | 2.007 s | 3476 | 1493 | 12 |
| after 4 | 111.7 | 1114 | 28/30 | 1.917 s | 2779 | 1254 | 13 |
| after 5 | 110.5 | 1103 | 27/30 | 2.006 s | 3573 | 1412 | 8 |

The other five base runs passed without `-v detailed`, so only one base run has stat
lines. Raw logs: `~/allegiance-evidence/P795/base/showcase/`,
`~/allegiance-evidence/P795/after/showcase/`.

### March decision/route equivalence

`P539dFairContactLiveTests.Record_one_run` (221 config, staging, 3 bots/side) was
captured before and after. The route **decisions** are identical:

* rendezvous `1052 Alderan`, sides and sector table identical;
* side 0 home 1050 → `1059 Rigel` → 1052 (4 hops in the base run; the after runs show
  the same 2-hop topology plus extra pod-relaunch transits);
* side 1 home 1051 → `1061 Juniper` → 1052 (2 hops), same both sides;
* staging/release and travel behaviour unchanged (`[p539d] side … released from
  staging` still emitted; no arena logs).

Exact replay equality is not possible because `ActiveBot.NextSeed()` draws from
`Random.Shared`, so bot phase offsets (and therefore shot/kill timing) differ every run
even on the base assembly. The semantic proof is stronger: every removed read of
`bot.Arena` was only ever true for an arena spawn, arena spawns are now rejected, so at
`Arena == false` all removed branches were dead and every simplified condition is
algebraically equivalent (see above). As a mechanical cross-check I decompiled the base
and after `allegiance-mcp.dll` with ILSpy and diffed them: 66 hunks, all semantic
changes are arena symbols/branches, the dropped `arena`/`arenaPosition`/`arenaFacing`
parameters, and the `ResolveScenarioTarget` simplification; the rest is goto/IL-offset
renumbering from the shorter method bodies. Raw decompiled pair and diff:
`~/allegiance-evidence/P795/{base,after}.decompiled.cs` and
`~/allegiance-evidence/P795/decompiled-base-vs-after.diff.txt`.

## Remaining `arena` hits in `tools/allegiance-mcp`

`grep -rniE "arena" tools/allegiance-mcp` over source (bin/obj are build outputs) now
returns 18 hits, all of them the removal error, its tool-description/history note, or
removal comments — **no live arena code path**:

| File:line | Nature |
|---|---|
| `BotManager.cs:134` | TTK comment: "the arena battle-start mode was removed" |
| `BotManager.cs:622–623` | Comment: the old arena relocation was removed; spawn always marches |
| `McpTools.cs:68` | Tool description history note (arena removed in P795) |
| `McpTools.cs:87` | `mode` enum is `["march"]`; description history note |
| `McpTools.cs:409, 411` | Guard comment and `ArenaRequested(args)` check |
| `McpTools.cs:459, 532, 621` | Comments: P795 removed the arena alternative |
| `McpTools.cs:1365–1368` | `ArenaRemovedError` definition (the removal error text) |
| `McpTools.cs:1371–1374` | `ArenaRequested` doc + implementation (detects `mode:"arena"`/`arena:true`) |

Full output: `docs/reports/P795_assets/grep-arena.txt`.

## Evidence

Small, committed evidence (≤ 2 MB): `docs/reports/P795_assets/`
`verification.json`, `showcase-never-parks.json`, `grep-arena.txt`.

Raw logs/traces/archives (outside the repo): `~/allegiance-evidence/P795/`
(base/after showcase logs, P539d traces, fast-tier logs, build-tools log, ILSpy
decompiled pair + diff, and the verification JSON copy).

## Open items / notes

* No rig/config change was made or attempted. Per the brief, Claude removes the rig's
  arena profile outside this worktree.
* AOT publish was skipped because no server/Core/client source changed (light-gate rule).
* The `spawn_scenario` summary no longer contains the `arena` and `arenaNote` fields;
  `mode` still reports `"march"` for a staging scenario and `"travel"` for a
  non-staging march. `battleMode` in the mixed-fleet path follows the same rule.
* P791 support, P778 finale/push and P796 loadout variety were not touched.
