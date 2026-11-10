# P988 — Skins 1C: renderer adapter and runtime switching

Implementation and qualification complete. Dispatch base `6659cb91765b`.
**Overall gate remains FAIL:** the full Fast and training invocations retain
the failures documented below; the stored P980 baseline also has pre-existing
drift on this driver. The final adapter's dispatch comparison is zero-delta.
All changes remain in this working tree. No commits, pushes, remote-host contact,
golden updates, token extraction, screen chrome, Modern 2.0 or Jet work.
The report was created before implementation and gate execution.

## Changes

| File:line | Change |
|---|---|
| `src/Allegiance.ClientCore.Game/Rendering/Skins/IHudSkinRenderer.cs:9` | Prebound HUD/command/post-starmap composition contract, coverage attachment, radar icons, overlay text and disposal. `HudDrawFrame` at :22 borrows the viewport/target preview and passes the resolved selection gate. |
| `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinResources.cs:6` | Immutable load-time callbacks for external radar, ship tags, pod/death, command, chat, cargo and parts. No per-frame closures or resource construction. |
| `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinRenderer.cs:9` | Adapts the existing, unchanged `HudRenderer` and `ModernHudRenderer`. Main composition at :30; command :40; console :42. Modern retains its integrated console/radar/feedback. Disposal :59 releases Modern's private SDF resources once; shared textures and preview remain borrowed. |
| `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinSwitcher.cs:5` | Immutable resolved definition/renderer pair; two preloaded adapters. Request :35 validates availability without replacing the prior skin on failure; cycle :45; held-edge consumption :54; atomic reference publication :61. Selection/dispatch does no file IO or definition lookup. |
| `src/Allegiance.ClientCore.Game/Game1.Lifecycle.cs:480` | Bind services after both renderers and radar resources are loaded. |
| `src/Allegiance.ClientCore.Game/Game1.GpuBudget.cs:38` | Construct the resource owner; :50 uses the live rebindable map and compose/modal/focus/training-filter gates. The single-chord shortcut avoids the general input helper's temporary alias list; :67 saves and announces only after successful activation. Existing graphics row 6 at :199 queues catalog selection; its Classic/Modern appearance at :292 is unchanged. |
| `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:254` | Capture existing keyboard input at :392; observe the chord after Update (including early returns), then commit before Draw at :259-260. Observing blocked presses prevents a held chord from firing on modal dismissal or focus regain. No extra proof-keyboard read is added. |
| `src/Allegiance.ClientCore.Game/Game1.Rendering.cs:3541` | Main adapter composition. Command at :1914 and console at :1935 retain their original positions around touch/starmap. Ship-tag colour mapping is passed explicitly at :3552. Shared notices use the active adapter; a switch-only two-second toast is at :3543. |
| `src/Allegiance.ClientCore.Game/Game1.Commanding.cs:441` | Shared command geometry remains common; adapter supplies whether to draw the Classic external text summary. |
| `src/Allegiance.ClientCore.Game/Game1.TrainingIntegration.cs:60` | Restore saved pre-lesson active renderer and persisted skin together on every existing cleanup path. Existing pre-load/headless cleanup still restores settings without constructing graphics resources. |
| `src/Allegiance.ClientCore.Game/Game1.cs:2214` | Dispose the skin owner once, with startup-failure fallback to the existing Modern renderer cleanup. |
| `src/Allegiance.ClientCore.Game/Rendering/HudSettings.cs:17` | A bound preference setter queues requests through its single renderer owner; activation bypasses that hook. Legacy proof/profile `Style` writes remain supported without per-frame preference polling. Unbound settings retain P981 migration semantics. Training settles a queued choice before taking its restore point. |
| `src/Allegiance.ClientCore.Game/Rendering/InputActionCatalog.cs:341` | Rebindable `CycleHudSkin`; Shift+F12 inherited by both presets. No native TK is invented. |
| `tools/hud2-proof/Program.cs:343` | Exercise the production adapter for every existing combination. P980's fixture-specific external radar/notices order is deliberately unchanged; it differs from Game1's production ordering. |
| `tools/p988-skin-switch-proof/Program.cs:1` | Actual Game1 + loopback server + production input/options + real-backbuffer captures; validates held chord, compose gate, atomic queued options, target/draft/panes and restart. |
| `tools/p912-training-proof/Program.cs:221` | Verify the active renderer id after the seed frame, so Classic/Modern lesson labels prove the corresponding renderer is actually bound. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/P804ClientLeadTests.cs:450` | Update the existing radar wiring guard to follow Game1 → bound resources → adapter → production radar; no drawing assertion is removed. |
| `tools/font1c-proof/Program.cs:233` | Inspect the shared native status art through the existing Classic renderer field after ActiveHud becomes the interface. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/HudSkinSwitchingTests.cs:25` | Presets/native audit, runtime/restart round trips, 13 compose/modal gates, focus/training filter, options with state open, training restore, allocation, invalid selection/lifetime and composition order. Existing `ModernHudSettingsInputTests.cs:30` now supplies adapters and commits at the frame boundary. |

`GLOSSARY.md` indexes the new ownership pattern. The existing HUD-style, options,
training, rebinding and input-default parity rows gain evidence; their statuses
are unchanged. No new simulation rule, service, port or environment variable.

The production pass order is preserved: Classic radar → shared ship tags → HUD
→ Classic pod/death → shared no-target feedback → command → touch → starmap →
Classic chat/cargo/parts. Modern continues to draw its radar and model overlays
inside its existing HUD pass. Native coordinate conversion and drawing code in
`HudRenderer.cs`/`ModernHudRenderer.cs` are untouched.

## Key-binding audit

Re-grepped the actual native corpus and both effective preset maps. Native
`Artwork/defaultinputmap.mdl:3-107` contains no Shift+F12 row. Its only VK 123
entry is **`(123, 2, 203)` at :98**, Ctrl+F12 → training-clock reset (`Igc/igc.h:427`). Modifier
values are `WinTrek/trekinput.cpp:26-29`; specific families precede Any at
:543-577. The executable preset audit verifies Shift+F12 is unique in both maps,
plain F12 still selects Credits, Ctrl+F12 still selects clock reset, and the
new action can be rebound. The native-default column remains empty because
this is a port presentation action; both presets inherit its port default.
As with existing Shift chords, the modifier is also Port layout's held booster
key; this slice changes no flight binding or booster rule.

The graphics row queues the same switch even with a draft or panes open; the
shortcut yields to typing, modal UI, focus loss and the training action filter.
Blocked press edges are consumed, so dismissal while still holding the key
cannot create a delayed switch. Only activation changes settings and persists
the skin. Level, nullable player overrides, target, chat, parts and pane state
are retained. Training restores the original saved preference on exit.

## Golden evidence

The complete existing matrix is **22 scenarios × 7 resolutions × 3 sizes × 2
backdrops × 2 skins = 1,848 comparisons (924 per skin)**. No golden was re-blessed.

The untouched dispatch proof binary first compared against the existing P980
baseline (`~/allegiance-evidence/hud-goldens/66d72e6f`): **1,848 compared; 1,764
pixel differences, 42 semantic differences, 0 corrupt goldens**. This is a
pre-existing qualification failure. The P980 driver is Intel Arc B50; this
sandbox exposes **llvmpipe (LLVM 21.1.8, 256 bits)**. The initial semantic failures
also include existing target-stat formatting drift (`SHLD 30%` / `SHLD 30 %`).
The original binary reproduces the 800×480 flight pair **3/3 alone**, with exactly
11,951 Classic and 12,254 Modern changed pixels each time. See `docs/TEST_FLAKES.md`.
The report does not claim that the old Arc goldens pass on this lane.

To isolate P988, the **original pre-adapter binary** is retained at
`.scratch/p988/base-proof-bin/` (assembly hashes in
`.scratch/p988/baseline-binaries.sha256`). It captures the full dispatch matrix
at `.scratch/p988/dispatch-baseline/` on this same driver: **1,848/1,848 captured**.
This separate diagnostic capture does not replace any P980 golden. The after
capture and decoded-RGBA/semantic comparison passed for the full matrix. The
final-build direct renderer comparison independently passed all 1,848 cells
with **0 pixel differences, 0 semantic differences and 0 corrupt goldens**.

| Comparison | Compared | Pixel differences | Semantic differences | Animation-only |
|---|---:|---:|---:|---:|
| Untouched dispatch → stored P980 | 1,848 | 1,764 | 42 | 0 |
| P988 final adapter → untouched dispatch capture (direct render compare) | 1,848 | **0** | **0** | 84 |
| Dispatch capture → P988 capture (decoded PNG + manifest comparison) | 1,848 | **0** | **0** | 83 |
| P988 capture → stored P980 | 1,848 | 1,764 (same pre-existing drift) | 42 | 0 |

The existing P980 mask for Modern's wall-clock warning badge is unchanged.
Every unmasked pixel must match; animated pixels remain explicitly counted.
The fixture's external Classic radar ordering is retained, so the separate
adapter-order tests and actual Game1 proof cover the production composition.
The committed `golden-selftest.sh` fails on its untouched old image on this
lane; the retained original binary repeats the same failure **3/3 alone**.
Against the separate dispatch capture, all **7/7** corruption/determinism
self-test steps pass: clean baseline, edited PNG integrity, exactly one changed
pixel, restored pixels, edited snapshot integrity, changed semantics, restoration.
No committed sample or full golden was changed.

## Runtime captures

The loopback proof passed `P988_SWITCH_PASS` and a fresh process passed
`P988_RESTART_PASS`. All five actual 800×480 screenshots were opened and visually
reviewed. Total PNG size is **608,814 bytes**, below 1.5 MB.

| Capture | Visible result |
|---|---|
| [Classic before](P988_assets/01-classic-before.png) | Native segmented grey gauge arcs, cyan labels, speed/signature and the compact Monmar minimap over the asteroid scene. |
| [Modern after](P988_assets/02-modern-after.png) | Cyan circular 100% gauges, larger speed numeral, Tactical/Radar tags and `SKIN: Modern`; the same client remains in flight. |
| [Modern with panes before](P988_assets/03-modern-panes-before.png) | Expanded starmap over the Modern gauges; inventory cargo slots at right; selected-station readouts below and the unsent yellow team draft. |
| [Classic with panes after](P988_assets/04-classic-panes-after.png) | `SKIN: Classic`, segmented arcs behind the same starmap and inventory, with the draft still visible above the Classic bottom console. |
| [Classic after restart](P988_assets/restart-classic.png) | Classic segmented gauges and compact Monmar minimap loaded in a fresh Game1 process from the saved skin. |

The 800×480 floor retains existing dense-map/console crowding and clipped draft
text. No readability/layout correction is claimed. The first capture was taken
after ship telemetry and the world scene settled; an earlier startup-only image
was discarded. These are actual Game1 backbuffers, with no image editing.

## Allocation and tests

The warmed loop exercises actual **Game1.UpdateHudSkinInput**, `ApplyPending`,
the actual **Game1.ActiveHud getter** (through delegates bound once outside the
measurement), and all three interface phases, including both preloaded
selections: **0 B / 100,000 frames = 0 B/frame** in **both Port and Native**.
This measures input polling and selection/dispatch
overhead with no-op drawing sinks, not allocations already inside the existing
drawing implementations. Saving and toast-string construction happen only
on a successful switch, outside Draw. The final actual-Game1 selection test
passed **2/0/0 ×3**, with zero bytes in all six preset repetitions. The
single-chord assumption is guarded by both-preset tests; rebinding and the
most-specific-chord/training filter still apply.

The final affected set passed **212 / 0 / 0** (Passed/Failed/Skipped) on
**each of three isolated runs**. This includes migration/catalog, switching,
presets, options, training restoration, HUD levels and all P804 lead/radar
checks, all 30 new switching cases, and the complete existing training
integration class. The three Live renderer-matrix tests passed **1/0/0 each**, 1m51s,
1m42s and 1m31s. `local.env` is present
and artwork is loaded. Affected, renderer, isolated and never-parks runs skip
zero cases. Fast skips three optional private Meshy-pack cases. Training skips
two opt-in M4 hunt branches per pass, six across three passes; no mandatory
artwork or Nanite completion case is skipped.

The exact Classic/reminders window case reproduced **0/1/0 ×3 alone**, each
at the existing `M2 9.2: native prompts missing or out of order` guard after
the lesson docked/completed. A dispatch control using the original proof source
and retained pre-adapter assemblies reproduced the **same guard failure**.
The baseline Game assembly SHA matches the original retained DLL. Modern's
exact reminder case also reproduced **0/1/0 ×3 alone** at the same prompt-order
guard. The three complete valid training groups are counted below. One broad
Classic/reminders run instead hit its earlier `P940 6.13 all-axes/mouse-turn`
control assertion. That earlier assertion passed in all three isolated Classic
replays before the later prompt guard failed; no control/physics code changed.
Its occurrence remains recorded rather than being counted as a passing window.

Modern Command View timed out at goal `4.3` in Serial B and in the final-binary
replacement course. Both had three probes and dead layer ships. The earlier
isolated full-course replays passed **1/0/0 ×3** (3m44s, 3m50s, 3m48s); the
exact final-binary full-course replays also passed **3/0/0**. The mission-5 /
lesson-4 prefix timed out in Serial B. Its earlier isolated outcomes were
**1/0/0, 1/0/0, 0/1/0** (3m13s, 3m07s, 20m00s). It later passed **3/0/0** on
the final binary and passed in the replacement qualification. A lesson-5
prefix stall observed in the excluded interrupted run passed **1/0/0 ×3**
in isolated follow-ups.

Original-dispatch controls with complete matching Content passed lesson 4
**3/0/0** and lesson 5 once. **The Command View stall is not confirmed on the
original baseline.** The final passing replays qualify as load-sensitive
window results under the lane's rule; no causal fix is claimed. No probe, AI,
lesson guard or timeout was changed to obtain a pass. The first optional
baseline control lacked Content and failed drawing smoke; that incomplete
fixture is excluded. The corrected baseline reminder control reproduced the
same `M2 9.2` prompt-order failure after mission completion.

## Gate

Solution build: **0 warnings / 0 errors**, including the final preference-hook
and training change. All **129 tools** also passed the final refresh, with zero warnings/errors. The only full Fast invocation completed **13,886 executions in 987.93 s**:
**13,877 passed / 6 failed / 3 skipped** (the optional private Meshy pack).
Nine infrastructure checks passed. The failures comprise two existing UDP bind
collisions (both protocols pass 3/3 alone), two existing floating upper-bound
bomb-timer cases, the existing idle-NPC movement signature, and the radar
source guard now corrected (all P804 tests pass in the final 212/0/0 ×3 set).
Two assertions passed above the calibrated Fast duration limit. All non-task
failure/timing families receive three isolated follow-ups; results are in
`docs/TEST_FLAKES.md`. No second full Fast suite was run.

Never-parks: **dispatch base 5/5; changed tree 5/5**, zero skips in either
set. These are unseeded functional trials on a shared host; no performance
improvement is inferred. Glossary links (730 paths), parity validation and
working-tree whitespace checks pass. The parity CSV remains LF-only; statuses
and weights are unchanged. Added code contains no personal email or absolute
home path. The public assets remain below 2 MB.

Training qualification completed **1,260 executions: 1,245 passed / 9 failed /
6 skipped**. Core passes **126/0/0** in every group. Counts below are
Passed/Failed/Skipped:

| Valid group | Core | MonoGame | Combined |
|---|---:|---:|---:|
| Serial A (`training-pass-2/1`) | 126/0/0 | 290/2/2 | 416/2/2 |
| Serial B (`training-pass-3/1`) | 126/0/0 | 288/4/2 | 414/4/2 |
| Audited replacement (`training-replacement/1`) | 126/0/0 | 289/3/2 | 415/3/2 |
| Total | 378/0/0 | 867/9/6 | **1,245/9/6** |

The nine failures are six reminder-policy executions (five at the reproduced
prompt-order guard, one at the earlier mouse-turn assertion), two full Modern
Command View probe stalls and one mission-5/lesson-4 prefix probe stall.
All mandatory Nanite default/loss/sacrifice/early windows and assigned Nanite
prefixes complete in all three valid groups. All failure families have three
isolated follow-ups in the ledger; the final Modern full-course and lesson-4
replays each pass **3/0/0**, without changing their conditions or timeout.

Builds/tests use the required build-server-disabling flags and the installed
.NET host. Restore uses cached packages with sources cleared and NuGet audit
disabled, avoiding the read-only user cache/network. Scratch and XDG profiles
are inside `.scratch/p988/`; a directory-FD temp alias avoids checkout
`local.env` leaking into temporary bundle-path test fixtures. No AOT server
publish is required: Server/Core are unchanged.

The first provisional training invocation was stopped after finding that old
proof tools assign `Style` after LoadContent. Its incomplete evidence is
excluded. The setter hook queues those writes; training entry settles the
pending choice before saving preferences, and the proof asserts the actual
bound renderer id.

A later qualification run (`training/1`) was unintentionally interrupted while
attempting to replace its nearly expired outer timeout. Ending that wrapper
also ended its test workers; its MonoGame TRX was not written. This controller
error is not counted as a lesson failure or a completed pass. Its entire run,
including its Core results, is excluded from the three valid pass totals.

The valid groups are `training-pass-2/1`, `training-pass-3/1`, and
`training-replacement/1`. The first two run the helper normally, with the two
client projects serially across Fast and Live. The replacement also runs Core
first, then runs MonoGame in **13 disjoint partitions with three workers**.
Discovery audits **294/294 cases**, with no missing or extra cases; each
partition uses the existing sequential runsettings. No case, guard, timeout,
mandatory Nanite branch or artwork requirement is removed. This executes the
complete third pass within the eight-hour task limit. The final executed
case-name multiset also matches the serial 294-case TRX exactly, with no
missing or duplicate cases. Raw per-partition TRXs and both audits are retained.

The two uninterrupted runs began before the final allocation-only shortcut
poll optimization. Their adapter, drawing and training/restoration code is
identical to final source. Replacement P912 windows use the **final Game
binary** with the unchanged renderer-id-asserting proof entrypoint. The final
source also passed solution/tools builds, **212/0/0 ×3**, the complete
1,848-cell comparison and real Game1 switch/restart proof. The exact lesson-4
prefix passed **3/0/0** in separate actual-window replays on the final binary;
this does not establish a causal fix for the earlier mixed result. Assembly
hashes are retained in `.scratch/p988/qualified-game-binaries.sha256`.

Existing proof scratch prefixes were symlinked into `p988/training-window-evidence`
and `p988/legacy-test-evidence`, keeping physical test data in task scratch.
Temporary aliases and controller helpers were removed after their owned
processes ended. Old `.scratch/p931`/`p940` paths in TRX output map to the
corresponding subdirectories of `p988/training-window-evidence`.

Raw evidence remains under ignored `.scratch/p988/` in this worktree; the
sandbox cannot write to the external evidence directory. The [retained raw
index](../../.scratch/p988/) includes reproduction wrappers, logs and assembly
hashes. Disposable build caches and controller helpers are removed.

| Evidence | Retained locations |
|---|---|
| Final build and tools | [Solution log](../../.scratch/p988/final-input-build.log), [129-tool log](../../.scratch/p988/gate-tools-build-ultimate.log), [final proof/test binaries](../../.scratch/p988/final-input-build/bin/) |
| Tests and allocation | [Final 212-case repetitions](../../.scratch/p988/final-affected/), [Live renderer repetitions](../../.scratch/p988/render-tests/) |
| Pixel qualification | [Original binaries](../../.scratch/p988/base-proof-bin/), [dispatch matrix](../../.scratch/p988/dispatch-baseline/), [after matrix](../../.scratch/p988/after/), [capture comparison](../../.scratch/p988/before-after/), [final direct comparison](../../.scratch/p988/ultimate-golden-compare/), [old-P980 qualification](../../.scratch/p988/before/), [dispatch self-test](../../.scratch/p988/dispatch-selftest.log) |
| Fast and follow-ups | [Full Fast results](../../.scratch/p988/fast/), [isolated failure/timing controls](../../.scratch/p988/retries/), [flake ledger](../TEST_FLAKES.md) |
| Never-parks | [Base five](../../.scratch/p988/base-parks/), [changed five](../../.scratch/p988/changed-parks/) |
| Three valid training groups | [Serial A](../../.scratch/p988/training-pass-2/1/), [Serial B](../../.scratch/p988/training-pass-3/1/), [audited replacement](../../.scratch/p988/training-replacement/1/) |
| Training diagnostics | [Window receipts](../../.scratch/p988/training-window-evidence/), [other test receipts](../../.scratch/p988/legacy-test-evidence/), [reminder repetitions](../../.scratch/p988/reminder-retries/), [baseline reminder log](../../.scratch/p988/base-training-control.log), [lesson-4 repetitions](../../.scratch/p988/m5-lesson4-retries/), [baseline controls](../../.scratch/p988/base-lesson4-controls/), [final-binary controls](../../.scratch/p988/final-lesson4-controls/) |
| Final runtime | [Switch captures/settings](../../.scratch/p988/ultimate-switch-proof/), [switch log](../../.scratch/p988/ultimate-switch-proof.log), [restart log](../../.scratch/p988/ultimate-restart-proof.log), [compact qualification](P988_assets/qualification.json) |

The shared window-receipt directories also retain excluded-run diagnostics;
valid TRX receipts identify the cases counted in each pass. Original interrupted
runs are retained for diagnosis and excluded from valid qualification totals.
All spawned runs and their workers have ended. The final task-owned diagnostic
socket audit reports **zero live managed processes**. HEAD and the index remain
at dispatch state; all requested code, tests, documentation and reviewed
captures are left in the working tree, without commits or pushes.

## Native intent

Native `WinTrek/WinTrek.cpp:9163-9166` sets the existing HUD-style value;
`training/trainingmission.cpp:74-78,119-121` saves/restores it around lessons.
This slice preserves that restoration intent across named skins. It retains
native StringImage's negative-Y bounds and decrement-before-draw
(`engine/image.cpp:354,382-390`), native placement conversion and GroupImage
paint ordering (`engine/image.cpp:954-970`). These are existing rules, not new
simulation behaviour. The new cycling shortcut and toast are presentation
extensions; the native key table and all gameplay semantics remain intact.

## Modern approach

Preload both adapters once, bind explicit services once, and publish one
immutable resolved-skin reference after Update. Keep the existing drawing and
shared semantic state; resolve and save only when selection changes. Validate
the boundary with decoded RGBA, real-window captures, held/modal input tests,
training restoration and a zero-allocation selection/dispatch measurement.
