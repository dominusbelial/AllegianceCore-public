# P997 — Construction effect and station hand-off

> **Claude review (2026-11-11):** the brief asked for the shell to vanish at the hand-off "whatever native's trigger is";
> Codex correctly found native keeps it (station created at shrink start, shell shrinks/fades around it until
> `m_timeComplete`, `Igc/buildingeffectigc.h:160-183,310-358`, `clintlib/appmsg.cpp:1692-1718`) but shipped the immediate
> hand-off as the default. Behaviour parity rules: the **default now draws `ClientWorld.BuildingEffects`** (native
> lifetime: shrink tail to 40 s, 181, explosive rock death, reset), and the immediate hand-off is the opt-in
> `visuals.constructionHandoff` flag (default **off**, Visuals page hotkey `B`), drawing `ConstructionVisuals`. The
> sighting's defect was the non-native placeholder mesh (an opaque hub+torus), not the tail itself: native shows a
> fading textured shell there for up to 10 s. `EFX-0004` is `implemented` again. Pinned by
> `P997ConstructionRenderTests.Instant_handoff_is_an_opt_in_visual_flag_and_native_tail_is_the_default`.

Status: complete. Required implementation, captures and checks finished; the full Fast sweep remains qualified by the existing failures documented below. Base: `d0734dfa`, branch `codex/p997`. No commits, pushes or rig runs.

Domingo's 2026-11-10 client video showed a dark violet torus surrounding an orange asteroid with a completed native outpost and green-framed bay. The construction renderer reused P289's hub-and-ring station placeholder; its timer-only visual lifetime allowed the placeholder to overlap a completed station.

## Native study

`effect/efgeo.cpp:99-136,191-227` loads `build.mdl` and renders two rotating copies: a source-alpha inner sphere at radius / 1.1 in the outer colour's HSB complement, and an additive outer sphere. It uses the model's texture and global colour, without station lighting. The outer colour is hardcoded `(50,28,146)/255` by `Igc/buildingeffectigc.h:93`. The native artwork's `buildtext.mdl` describes a textured sphere using `build1bmp`.

`Igc/buildingeffectigc.h:19-22,50-54,96-98,160-225` defines four ten-second phases: envelope from builder surface to asteroid (radius 1 to asteroid radius), growth to `1.1 * max(asteroid radius, station radius)`, increasing inner opacity, then shrinking and fading toward station radius. `clintlib/clintlib.h:1013-1029` constructs the local effect; `clintlib/appmsg.cpp:80-105` imports other players' exported effects. `WinTrek/trekigc.cpp:1762-1768` creates the render geometry.

**Important correction to the brief:** native normally *retains* its final shrinking shell during conversion. `Igc/buildingeffectigc.h:310-358` creates the station at the start of shrink after explicitly detaching the asteroid link; `clintlib/appmsg.cpp:1692-1718` does the same for a nonexplosive asteroid retirement, specifically so the effect survives. Explosive termination removes the linked effect (`Igc/asteroidigc.h:33-44`); `BUILDINGEFFECT_DESTROYED` terminates it (`clintlib/appmsg.cpp:1773-1790`). The requested immediate visual hand-off is therefore an explicit P997 policy difference, not a newly discovered native rule. The four-phase sampler still implements native's full timeline.

## Root causes and changes

References to the original implementation below are at dispatch base `d0734dfa`.

- `BuildingEffectRenderer.cs:59-61` generated `StationMeshFactory` geometry. Its draw at `:113-129` applied station lighting to that placeholder. `Game1.Rendering.cs:1058-1092` displayed it as construction. The replacement in `src/Allegiance.ClientCore.Game/Rendering/BuildingEffectRenderer.cs:21-115` loads `build.mdl` and its authored texture, draws the two unlit shells with native axis/rate rotations, source-alpha/additive blending, complementary inner colour and correct radius ratio. Render state is restored after the pass. Missing construction artwork is logged; it never revives the station placeholder.
- The phase mathematics already existed in `src/Allegiance.Core/Sim/ConstructionPhase.cs:8-29`. It now drives all shell colour/intensity/position/radius values instead of only placeholder radius and alpha. New literal reference tests cover negative/start time, each phase boundary, midpoints, both radius orderings and completion. Native `Color * intensity` scales RGB **without changing alpha** (`zlib/color.h:101-110`); the renderer preserves that distinction during fade and compensates for BasicEffect's premultiplied diffuse input before straight-alpha blending (`engine/RasterD3D9.cpp:295-312`).
- Original `ClientWorld.Interface.cs:83-97` removed a linked effect only on explosive asteroid retirement. This matched native's retained conversion tail, but could not meet the requested immediate hand-off. `ClientWorld.cs:3772-3776` pruned only at 40 seconds. P997 adds per-view `ConstructionVisuals` (`ClientWorld.cs:1572-1576`) and makes `Game1.Rendering.cs:1065-1085` use it. Station export at the exact construction position (same sector, 0.01-unit tolerance), either kind of asteroid retirement, interruption, sector exit, complete local observer snapshot, game over, mission rotation and reset retire the visual immediately. An unrelated nearby station or another sector cannot retire it. A station received before a late effect also suppresses that visual; game-over late exports cannot revive it.
- Initial geometry exports precede `SET_CLUSTER` on this server (`Server.Lobby.cs:5250-5345`). Clearing every shell on that barrier would hide legitimate late-join construction. `ClientWorld.cs:2828-2837` restores only destination-sector reservations with a still-present asteroid, an unexpired phase and no replacement station. This also permits leaving and returning during a live build without resurrecting completed construction.
- The construction table is **not rendering-only**: `tools/allegiance-mcp/BotManager.Research.cs:244` uses `BuildingEffects` to reserve build rocks. `ClientWorld.BotSharing.cs:36-46` shares that table across bot views. Those reservation semantics remain unchanged, including the conversion tail, interruption and existing timer pruning. The new presentation table is private to each view; MCP consumes no new state. Tests explicitly pin reservation retention across hand-off and sector changes.
- `Game1.Rendering.cs:1048-1054` now posts one local console/chat warning per station type/model pair before drawing the finished-station placeholder. The notice names both type and model, including empty-model/unknown-type cases. `StationFallbackWarnings.cs:8-12` is shared by Classic and Modern. `NativeStationRenderer.cs:100` caches missing/undecodable model results after the existing one-shot log, avoiding repeated disk reads or decoder exceptions every frame. The hub/ring remains only the finished-station missing-art fallback.
- `tools/p997-construction-proof/` exercises the actual GPU `NativeStationRenderer.MeshFor` path for **all 143 PCore001 station types / 81 distinct model stems**. A headless Fast test exercises the production `NativeStationRenderer.DecodeModel` entry point for every type with artwork alone; a second Live GPU audit exercises `MeshFor`, including native buffer upload. The headless test skips only without artwork; the GPU audit also skips without a GL device. With this worktree's ignored `local.env`, it passes with **0 skipped**. The same proof creates deterministic encoded wire fixtures and renders construction, station hand-off and intentionally missing station art using production renderers and both production HUDs. It does not modify any artwork file.
- `GLOSSARY.md` documents the separate presentation/reservation lifetimes. `docs/parity/effect-matrix.csv`, row `EFX-0004`, now cites the actual build-shell implementation and is marked **partial for exact native parity**, because the requested immediate hand-off intentionally omits native's conversion tail. The requested P997 implementation is complete; that deliberate policy is not credited as full native equivalence. No station model, texture, build duration, constructor AI, economy, server flag or P996 area changed.

The server already sends the required native observation: `src/Allegiance.Server/Server.Research.cs:73-89` packs silent `ASTEROID_DESTROYED` **before** `EXPORT/OT_station` for the owning side. Native `FedSrv/FedSrv.CPP:11429-11462` sends side-scoped rock retirement, creates the station, then terminates the rock. No server change was needed. The new TCP loopback test spawns an actual constructor through `Server.SpawnNpcShip` with a PCore001 builder drone type (asserting NPC pilot type and hull), starts its build effect, and advances ordinary simulation ticks through actual station creation at 30 seconds; without calling visual prune it observes the consumed rock gone, the station present and no shell, while the reservation remains before 40-second expiry.

## Screenshots

Captured with `tools/p997-construction-proof` from production ClientWorld, model/texture decoders, effect/station renderers, HUDs and chat renderer. These are deterministic render-target screenshots, not a rig run or a full Game1 play session. Grow is sampled at 15 seconds. Completion consumes the rock by encoded retirement followed by station export; the final native outpost model has no construction shell. The proof camera faces the native +X garage, exposing the green-framed bay rather than the rocky back. Fallback deliberately hides only `ss92.mdl` through an asset-source wrapper, triggers the renderer's real missing-model log and shows its one-shot notice. All six PNGs were visually reviewed; no private paths, hostnames or external artwork files are embedded as standalone assets.

| File | Bytes |
|---|---:|
| [grow-classic.png](P997_assets/grow-classic.png) | 242,838 |
| [grow-modern.png](P997_assets/grow-modern.png) | 266,393 |
| [complete-classic.png](P997_assets/complete-classic.png) | 108,512 |
| [complete-modern.png](P997_assets/complete-modern.png) | 126,222 |
| [fallback-classic.png](P997_assets/fallback-classic.png) | 65,618 |
| [fallback-modern.png](P997_assets/fallback-modern.png) | 80,340 |
| **Total** | **889,923** |

## Validation

All runs are local and bounded with `timeout`. The ignored `local.env` supplies artwork/native references. The preliminary ordinary restore attempted NuGet's default audit-cache refresh before offline restoration was pinned; it failed on the read-only cache. All subsequent restoration and tool builds explicitly use the local package cache with package audit disabled. No network package source is needed for the completed builds. An initial constructor test fixture placed its builder outside the sector bounds, so normal out-of-bounds damage interrupted it; moving that fixture inside the sector corrected the test without changing production simulation.

| Check | Passed / Failed / Skipped or result |
|---|---|
| Solution build | **0 warnings / 0 errors** |
| New ClientCore tests, three isolated runs | **30 / 0 / 0 each** |
| New renderer/decoder/GPU tests, three isolated runs | **7 / 0 / 0 each** |
| All new tests combined | **111 / 0 / 0** over three repetitions |
| Headless Fast CI test with no display/dummy SDL | **1 / 0 / 0**, 86 ms; no graphics device |
| Existing construction/clock/station regressions | **44 / 0 / 0** (ClientCore 30; MonoGame 14) |
| All tools build | **132 projects**, all successful, no warnings/errors |
| One complete Fast sweep (runner lock; 180-minute timeout) | **14,035 / 6 / 3**, 14,044 total; **qualified FAIL**, details below |
| Showcase never-parks: five dispatch-base / five changed | **5 / 0 / 0 in each arm**; no parking failures |
| Glossary links / parity scorecard | Pass; glossary checks 759 paths |
| Working-tree hygiene / whitespace | Pass; no added secret, personal-email or absolute-home-path patterns; six PNGs reviewed |
| Screenshots | Six reviewed PNGs; **889,923 bytes**, under 1 MB |

The Fast runner completed full coverage, including its 19 quarantined timing cases (all passed), in **576.7 seconds**. The three skipped cases require the optional private Meshy pack, not station artwork. Its six failures are existing profile permissions, chat seating, missile partial-tip receipt, NPC docking deviation, bot flight rollout and mission-rotation receipt signatures. All were repeated three times alone: chat, NPC docking, flight and rotation passed all repeats; the profile case failed all three under the original read-only config and passed all three with writable scratch configuration. The known missile partial-tip failure persists in all three repeats despite full lock and is left open. Two additional methods passed the sweep but exceeded its 11.021-second limit; both pass three isolated repeats below it. Exact counts, durations and reasons are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). No unrelated product/test assertions were changed, and the full suite is not presented as green.

After the sweep, the screenshot-only camera was turned toward the real bay, the Live loopback fixture was strengthened to use the production NPC spawn factory and actual constructor hull/pilot, and one additional Fast artwork decoder test was added so model decoding can run without a GPU. That last test extracts the existing production parse expression into `NativeStationRenderer.DecodeModel`; regenerated screenshots are byte-for-byte identical. Final solution and all 37 focused cases were checked again, with three isolated repetitions (111 passes). The Fast sweep was run once, as requested; the extra headless case is covered by those final isolated runs. No gameplay code used by P700 changed during comparison; Core, Server, ClientCore and MCP production assembly identities remain stable within each arm.

Detailed local logs/TRX files are under `.scratch/p997/` (ignored). All task build, test, proof and showcase commands have finished; no task-started service remains running. No commits or pushes have been made.

## Showcase comparison

Five sequential dispatch-base/changed pairs use the unchanged P700 method. Times below are its observation seconds; idle is the longest interval below 1 m/s while a target exists (limit: 5 seconds). Timing is descriptive, not a performance comparison.

- Base: **5 passed / 0 failed / 0 skipped**, 5/5 completed.
- Changed: **5 passed / 0 failed / 0 skipped**, 5/5 completed.

| Trial | Base observation (s) | Base longest idle (s) | Changed observation (s) | Changed longest idle (s) |
|---|---:|---:|---:|---:|
| 1 | 204.2 | 2.008 | 386.1 | 0.601 |
| 2 | 200.6 | 2.019 | 289.1 | 1.614 |
| 3 | 276.4 | 1.905 | 405.8 | 1.211 |
| 4 | 149.0 | 2.005 | 267.2 | 0.106 |
| 5 | 185.1 | 1.818 | 228.4 | 1.905 |

All ten trials passed: **0/5 failure rate on base and 0/5 on the working tree**, with no skipped trials. The largest observed idle interval was **2.019 s on base** and **1.905 s changed**, both below the unchanged five-second bound.

Core, Server, ClientCore and MCP gameplay assembly hashes and source-controlled P700 behavior are held constant within each arm. The final P997 decoder extraction changes only unused rendering code in the Game assembly; the stronger P997-only Live fixture changes no P700 method. Final assembly identity checks are retained with the local receipts.

## Reproduction

Run from the repository root with the ignored `local.env` loaded. This lane's
local-only build configuration was:

```sh
set -a
source ./local.env
set +a
export DOTNET="$HOME/.dotnet/dotnet"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false
export RestoreSources="$HOME/.nuget/packages" NuGetAudit=false
timeout 1200 "$DOTNET" build AllegianceCore.slnx --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
timeout 3600 tools/build-tools.sh
for repetition in 1 2 3; do
  timeout 300 "$DOTNET" test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~P997'
  timeout 300 "$DOTNET" test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --filter 'FullyQualifiedName~P997'
done
timeout 10800 tools/run-tests.sh --tier fast --no-build
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null timeout 120 "$DOTNET"   tools/p997-construction-proof/bin/Debug/net10.0/p997-construction-proof.dll .scratch/p997/captures
```

The showcase comparison uses independently built dispatch-base sources from
`git archive d0734dfa` and the working tree, alternating base/changed five times,
with the exact method
`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`.
Per-arm assembly hashes, per-trial return codes, host-load samples, logs and TRX are
retained in `.scratch/p997/showcase/`; each invocation has a 600-second timeout.
These are behavior checks, not a throughput benchmark.

## Native intent

A moving, rotating textured shell transforms a construction asteroid into a station, with deterministic timing, radii and colours.

## Modern approach

Use the native effect art through the existing runtime asset decoder and keep presentation retirement separate from construction reservations. Show an explicit local warning when finished-station art falls back to the procedural placeholder.
