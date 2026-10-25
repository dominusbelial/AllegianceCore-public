# P825 — random civilizations at mission creation

Working-tree implementation on `codex/p825`, based on public master
`b782c8cd8ebfa4bc0743c49eb43f02caffac8e2a`. No commits or pushes. All execution
was local; no rig, Mac or other remote host was contacted.

## Behaviour and implementation

`games[].civIds` accepts JSON `null` and integer `-1` for random sides. The
existing `int[]` configuration API remains intact through a source-generated-JSON
compatible property converter. `--civs -1,-1` uses the same sentinel;
`--civs -1,25,-1` supports a configured three-team game. Invalid IDs, array
contents/counts and an exhausted legal pool after fixed-side exclusions produce
explicit configuration errors. Fixed IDs, including duplicates, remain fixed.
No fixed production config or bot balance setting was changed.

Every `AssembleGameMission`, including P439 rotation, resolves a new tuple before
map generation and side construction. Configured IDs are never overwritten.
The resolver uses the existing `Server.LegalLobbyCivs` pool, excludes fixed
sides from every refill, and draws without replacement by swap-remove. It
refills after exhausting the available pool, permitting repeated civs only when
there are more random sides than available civs. If fixed sides occupy every
legal civ, a remaining random slot is a clear configuration error rather than
an implicit exception to fixed-side exclusion.

`games[].civSeed` is the first match seed. Later match seeds come from that
game's own `Random(civSeed)` stream; an omitted seed is drawn cryptographically.
Each assembly logs the actual per-match seed. To replay any logged matchup,
set `civSeed` to that match's logged seed with the same core and fixed/random
slots. This stream is separate from simulation RNG and map generation. Pin
`mapGen.seed`/`--map-seed` separately when also replaying generated geometry.
There is no extra environment variable or service, so Compose wiring is unchanged.

Research plans are precomputed before ticking, keyed by game configuration,
side and actual civ. Rotation selects from those cached entries using the rolled
tuple and does no plan-file I/O. Random civs without authored plans receive the
P702 generated plan, including when the previous fixed matchup explicitly
requested `plan_a`; civs with authored plans still honor that name. Fixed
configurations retain explicit-plan validation. Mission plans feed the existing
research commander and briefing path. Lobby civ selection retains its existing
P802 behaviour. Civ/plan validation now runs before host-reading profiler callbacks are
registered, including non-research configurations. Broadcast configuration
validation retains its earlier position so it can reject invalid values without
loading the private artwork corpus. Unknown fixed civs preserve
the legacy non-research `InvalidOperationException`; research errors retain
`InvalidDataException` with game/side/civ and “no valid research plan” context.
A regression test forces a profiler report after both failed-boot paths.

Assembly emits `[matchup] mission #I match=N seed=S side0=ID:Name(plan) ...`.
The existing `[gameover] ... iSideWinner=...` line now includes the same match
identity/seed and the **actual final** side civs/plans, including any lobby
changes. Old mission metadata is discarded during rotation. All loops and
logging enumerate sides.

`tools/p819-side-bias-proof/matchups.py` is a standalone sibling to the old
two-side combat audit. It needs only the server log and tallies completed
matches, matchup tuples, and appearances/wins/losses/draws by civ and side
position, including N sides and draws. It does not infer civs from old log
formats or require bot logs. Its tests cover interleaved missions, lobby civ
changes, negative seeds and a three-side draw.

## Native citations and deliberate composition

Line numbers were rechecked in the local native source:

| Rule | Native source | Treatment |
|---|---|---|
| Default mission civ IDs are NA | `Igc/igc.h:1276-1282` | JSON null and CLI -1 represent NA. |
| Initial civ catalogue | `FedSrv/fsmission.cpp:64-77` | Native enumerates every core civ; its `<300` check is commented out. |
| Creation-time fill and refill | `FedSrv/fsmission.cpp:175-198` | Port swap-remove random draw; with one remaining civ, consume it without another RNG draw and refill. |
| Legal player civ pool | `WinTrek/teamscreen.cpp:897-916,2663-2672` | Reuse P802's nonnegative IDs below 300; bonus/special civs are never rolled. |
| Lobby random pick | `WinTrek/teamscreen.cpp:1062-1075,2680-2686` | Existing P802 pool is shared, not duplicated. |

The native constructor comment says “minimizing duplication vs. pre-existing
sides,” but its shown loop does **not** remove fixed IDs from the initial list.
P825 explicitly requires fixed-side exclusion and the lobby special-civ cut;
this implementation combines those requested constraints with the native draw
algorithm instead of claiming that the native constructor already implements
them. The parity row records that composition. Three-faction gameplay remains
out of scope; resolver and research-assembly tests exercise three sides.

## Bot audit

No hard-coded rig civ was found in MCP behaviour. Live sources already are:

- `BotManager.TechLoadouts.cs:53-54`: civ from the current `SideRoster`, then
  purchase/role selectors use the live shop's civ and tech masks.
- `BotManager.Support.cs:267`: support civ also comes from the live side roster.
- `BotManager.Research.cs:96-103`: a changed briefing epoch or plan civ replaces
  the research commander with a tree for `briefing.Plan.CivId`.

Consequently no bot implementation changes were needed. The existing P802
random-picker test incorrectly required a pick different from the current
civ, although native allows selecting it again. Its wait now observes the
actual random relay; it still checks that both peers and the authoritative
side agree on one legal resolved ID. The P705 CLI test now accepts `-1` and
continues rejecting values below it.

 The fixed pair in the
old P819 proof is an explicit A/B fixture, not production bot policy; the new
`--random-rotation` proof mode exercises random civs in one server process.

## Local consecutive-match proof

Command, from the repository root:

```sh
~/.dotnet/dotnet run --project tools/p819-side-bias-proof -- \
  --random-rotation --matches 3 --seconds 240 --output .scratch/p825/showcase
python3 tools/p819-side-bias-proof/matchups.py \
  --server .scratch/p825/showcase/server.log --output .scratch/p825/showcase/tally.json
```

The proof loads real JSON `civIds: [null, null]`, seed 825, uses the smallest
existing showcase preset (15v15), ordinary legal purchases/research, a 30-second
match countdown and two-second automatic intermission. `showMap: true` is a
fixture setting so a short proof need not wait for native fog scouting. It
uses the native map and actual socket-connected MCP bots. The normal countdown
ended all three short matches as draws; this is a rotation/identity proof,
not balance evidence. No direct game-over invocation or synthetic winner is
used by this proof. The first attempted launch used an invalid zero kill-floor
fixture option; it was corrected to the existing positive minimum before this
successful run.

| Match | Seed | Side 0 | Side 1 | Result | Sim-pass failures |
|---|---|---|---|---|---|
| 1 | 825 | 35 Bios / generated | 201 Rixian / generated | Draw | 0 |
| 2 | 1561494364 | 5 Omicron / generated | 201 Rixian / generated | Draw | 0 |
| 3 | 1906288745 | 3 Ga'Taraan / plan_a | 1 Dreghklar / generated | Draw | 0 |

The next mission was assembled automatically with another tuple, 201/4.
Evidence: [assembly and game-over lines](P825_assets/matchups.log),
[proof results](P825_assets/result.json), [server-only tally](P825_assets/tally.json).
A civ may recur between matches; the no-repeat rule applies within each match.

## Verification

Build, tool-build and AOT checks pass on final source (zero warnings/errors,
no IL2026/IL3050). A final native executable boot also parsed the null config
and logged seed 825 / civs 35,201 successfully. Tooling uses the installed
`~/.dotnet/dotnet`. One later build attempt encountered a read-only, expired
NuGet vulnerability cache; setting the existing `NUGET_HTTP_CACHE_PATH` to
workspace scratch resolved it, and all 87 tools were rebuilt successfully.

Exactly **one** Fast-tier invocation ran: 10,801 passed, six failed and three
existing optional-Meshy skips; nine infrastructure checks passed. The raw
suite result remains **FAIL**. The failures were:

| Test | Finding and action |
|---|---|
| `ServerTests.Boot_WithUnknownCivilization_ThrowsHelpfulError` | Initial validation changed the legacy exception type; final source preserves it. |
| `P705TechModeTests.Unknown_civ_or_named_plan_fails_with_side_context_before_listening` (unknown civ) | Initial validation omitted the research error context; restored. |
| Same method (bad plan) | A preceding failed non-research boot had left a profiler callback without a host. Civ validation now precedes host-reading callbacks; the new failed-boot regression covers both modes. |
| `P705TechModeTests.Civ_run_parameter_is_ordered_and_validated` (-1 case) | Old assertion rejected the newly required sentinel; updated, with an additional below -1 rejection case. |
| `P802TeamCivLiveTests.Random_change_resolves_to_a_real_legal_civ_and_relays_it` | A valid random roll selected the current civ; removed the incorrect “must change civ” fixture assumption. |
| `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` | Existing allocation flake, 1,568 bytes over 10,000 frames; no HUD production code changed. Exact-method follow-ups are recorded with the final gate receipt. |

No second broad Fast run was used to replace that snapshot. Final-source
new/affected methods and each failing method passed three times in separate
exact-method processes: **48 .NET invocations / 90 case passes**, plus three
Python invocations / six case passes, with no failures or skips. This includes
the new failed-boot/profiler regression, P702 plan assembly, P765 migration,
P802 civ changes, legacy fixed-civ errors and the HUD allocation method.
The existing broadcast-validation rejection test also passed three times.

The P700 showcase never-parks method passed **5/5**. The observation results
were:

| Run | Seconds observed | Reached objectives | Longest stop with target |
|---|---|---|---|
| 1 | 357.5 | 24/30 | 1.720 s |
| 2 | 325.0 | 24/30 | 2.006 s |
| 3 | 352.0 | 24/30 | 2.005 s |
| 4 | 427.7 | 30/30 | 0.000 s |
| 5 | 373.1 | 24/30 | 2.005 s |

The maximum was **2.006 s**, below the five-second bound. For comparison,
P821's saved baseline passed 5/5 (maximum 2.205 s) and its included fixed build
passed 5/5 (maximum 2.021 s). These are historical receipts from
[P821 verification](P821_assets/verification.json), not a new simultaneous
baseline experiment. The current observation durations overlap those saved
baseline runs (roughly 350–450 seconds); there was no parking or arrival failure.

[Final verification receipt](P825_assets/verification.json) includes all
standalone method runs and the original Fast snapshot. [Parking comparison](P825_assets/parking.json)
includes current measurements and the saved baseline records. Raw logs and TRX are
retained in the sandbox-writable `.scratch/p825/` tree (including the original
Fast snapshot); portable evidence in this report is small text/JSON only.

## Claude's 221 recipe (not executed here)

Keep the existing two-team rig map, research and bot settings. In the existing
server config change only:

```json
"civIds": [null, null]
```

Retain `researchPlans` (`plan_a` remains valid where authored and otherwise
falls back to generated). Omit `civSeed` for a fresh logged sequence; set a
specific integer when comparing repeatable sequences. Restart once to load
the changed config; subsequent rotations roll civs without server restarts.
An existing override can use `--civs -1,-1`; remove any fixed `--civs 25,3`
override that would replace the JSON choice. No new environment variable is
required. No deployment or restart was performed by this lane.

For each match retain the assembly and game-over lines: mission slot, match
number, seed, side position, civ ID/name, plan and winner/draw. Run locally on a
copy of the rig server log:

```sh
python3 tools/p819-side-bias-proof/matchups.py \
  --server copied-server.log --output p825-race-matrix.json
```

Compare `byCiv` and `byPosition` wins **and appearances**, draw counts and
`completedMatchups`; compare reversed tuples separately before attributing
position effects to race. Seeds replay civ draws, not a deterministic entire
battle. Keep the old combat audit for ore, losses, role selection and research
progress; its two-side assumptions were not expanded into three-faction play.
