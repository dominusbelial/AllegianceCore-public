# P1035 — Jet HUD skin, story 1/9: N-skin registry, `hud.jetSkin` flag, Jet palette + vector primitives scaffold

Implementation complete in the `hermes/p1035` worktree, base commit `30bd89aa`.
No commit, no push, no stash, no remote host, no Docker (not installed here). Raw captures (460 MB each) and the fast-tier logs live in
`~/allegiance-evidence/P1035/` (outside the repo); the report's assets are under the 2 MB task cap.

P972 §4 (Jet design) and §9 Phase 3A define the target; this story is only the foundation: the ordered
N-skin registry, the `hud.jetSkin` gate, Jet's validated palette, the vector-primitive layer every later
Jet story uses, and the three story-1 readouts.

## 1. Zero-delta baseline first, then the comparison

The **unmodified** tree was built (`tools/hud2-proof`, Release) and captured **before any edit**:

```
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet hud2-proof.dll --capture ~/allegiance-evidence/P1035/baseline
HUD2_CAPTURE_PASS 1848 combinations in 5.8 min -> ~/allegiance-evidence/P1035/baseline
HUD2_MANIFEST 1848 combinations, commit 30bd89aacd19+dirty, driver llvmpipe (LLVM 19.1.7, 256 bits)
```

The existing P980/P988 matrix is unchanged: **22 scenarios × 7 resolutions (1920×1080, 2560×1440,
1280×720, 1600×900, 1366×768, 800×480, 3840×2160) × 3 sizes (0.75/1/1.5) × 2 backdrops (dark/bright) ×
2 skins = 1,848 combinations** (924 Classic + 924 Modern), decoded-RGBA goldens plus a semantic
element/visibility snapshot per combination. After the change the same tree was captured again
(`~/allegiance-evidence/P1035/after`, same 1,848 combinations in **6.1 min**) and compared:

```
HUD2_CAPTURE_PASS 1848 combinations in 6.1 min -> ~/allegiance-evidence/P1035/after
HUD2_COMPARE baseline 30bd89aacd19+dirty at ~/allegiance-evidence/P1035/baseline; 1848 combination(s)
HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)
```

The only differences are the pixels inside the fixture's recorded animation mask (the wall-clock
low-resource `!` badge at ModernHudRenderer.cs:166, documented in P980 §3, 84 of the 1,848 entries), and
every entry reports `outside: 0`. **Classic and Modern are pixel-identical (decoded RGBA) and
semantically identical: 1,848/1,848 combinations, 0 pixel diffs, 0 semantic diffs.**

## 2. Changes

| File:line | Change |
|---|---|
| `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinIds.cs:13` | `HudSkinIds.Jet = "jet"` (canonical lowercase id, same `Normalize` rule). |
| `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinDefinition.cs:15,40,55` | `HudSkinDefinition.Palette` (:15, optional, null for Classic/Modern so their JSON stays byte-identical) and `HudSkinPalette` (:40) with the five roles Primary/Dim/Hot/Plate/Keyline. `HudSkinColor.TryParseHex` (:55) parses `#RRGGBB` headlessly (strict: exactly 7 chars, leading `#`, hex digits). |
| `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinLoader.cs:48,55` | Palette validation inside `Validate`: all-or-nothing — every present palette must carry all five roles and each must parse, otherwise `HudSkinLoadException` rejects the whole definition (prior skin stays active). |
| `src/Allegiance.ClientCore.Game/Rendering/Skins/jet.skin.json` | The embedded Jet definition: id `jet`, name `Jet`, Modern-equivalent defaults (SelectionBracket/RadarDeclutter on, Level Tactical), palette `#79FFC0` / `#3DA87B` / `#C9FFE3` / plate `#06110C` / keyline `#080B09`. Picked up by the existing `Rendering/Skins/*.skin.json` `EmbeddedResource` glob — same packaging as classic/modern. |
| `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinSwitcher.cs:17` | **N-skin registry.** An ordered `_installed` array (Classic, Modern, then Jet when supplied); `Cycle` (:86) steps to the next **enabled** entry with wrap in both directions; `Request` (:74) refuses a gated/uninstalled id without touching active or pending; `Enabled` (:117) asks the `hud.jetSkin` delegate. `Installed` (:72) exposes the ordered registry for tests/UI. Construction keeps a persisted `jet` while the gate is off **without** rewriting the stored value (only an accepted id activates), and `ObserveJetAvailability` (:104) queues Modern when the flag turns off while Jet is active or pending. Apply/Active remain a single reference swap — no lookup, IO or allocation. The old `(settings, classic, modern, catalog)` constructor is kept (:30) so every existing caller/test compiles unchanged. |
| `src/Allegiance.ClientCore.Game/Rendering/VisualFlags.cs:122,192,248` | The `hud.jetSkin` visual flag: key, label **Jet HUD**, description **"military vector HUD — in development"**, `Toggle`, default **OFF**, exposed as `VisualOptions.JetSkin`. One table row — profile parsing, `--visuals` override, startup log and the Options → Visuals page are all table-driven and need no change. |
| `src/Allegiance.ClientCore.Game/Game1.GpuBudget.cs:38` | `InitializeHudSkins` builds the Classic/Modern adapters, preloads the Jet renderer when the built-in definition exists (:49) and constructs the registry with `jetEnabled: JetSkinEnabled` (:53). `JetSkinEnabled` (:59) is the live flag read; `UpdateHudSkinInput` (:66) publishes it at the update boundary before the held-edge logic. `HudStyleRowLabel` (:92) is the Ctrl+F6 HUD-skin row (row 6) text, used at :316. |
| `src/Allegiance.ClientCore.Game/Rendering/Jet/JetVectorPainter.cs:1` | **`JetVectorPainter`** + `IJetVectorSurface` (+ production `JetSpriteBatchSurface`): line, polyline (closed optional), circle, arc, corner brackets, filled plate, keyline-backed text. Every primitive paints a **near-black outer under-stroke first** then the ink (:137-231); the standard stroke is **1.5 px at 1080p** × the Modern/density scale, floored at **1 physical pixel** (:128-135); the polyline point buffer is pre-sized (:101) — no per-frame allocation. |
| `src/Allegiance.ClientCore.Game/Rendering/Jet/JetHudRenderer.cs:1` | **`JetHudRenderer : IHudSkinRenderer`** (`Id = "jet"`, `Coverage` = Cockpit centre reticle / Speed / Thrust setting, :96). Story-1 symbology: broken gun cross (:178, native indicator camera gate), boxed numeric speed (:191), boxed throttle % (:214, the shared model's thrust-gauge fraction), `JET / GREEN - WIP` tag bottom-centre (:245). Shared passes in the **Classic composition** — `Radar(model)`, `ShipTags(model, modern: false)`, Classic `PodDeath`, Classic external command summary (`DrawCommand → Command(model, true)`), and `Chat/Cargo/FlightParts` on `DrawConsole` (:147-152). Text caches avoid re-formatting per frame; unknown telemetry draws nothing. |
| `tools/hud2-proof/Program.cs:344` | `--jet <dir>`: renders the Jet skin through the production renderer (its own primitives + the production Classic `RadarBracketRenderer`) at 1920×1080 and 800×480 on dark/bright backdrops plus instrument-hidden states, and writes `jet-capture.json` (coverage, scale, stroke px, tag). Separate from the Classic/Modern matrix so the zero-delta capture stays byte-identical. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetSkinScaffoldTests.cs:1` | New tests (§3). |
| `GLOSSARY.md:980` | Jet skin / `hud.jetSkin` / `JetVectorPainter` entry. |
| `docs/reports/JET_SKIN_SCAFFOLD_P1035_assets/` | Seven Jet PNGs + `jet-capture.json`, plus one Classic and one Modern flight PNG (9 images, 868 KB total). |

Existing tests updated only where a pinned literal grew with the new table row / built-in skin (the
behaviour they assert is unchanged): `P979VisualsOptionsTests.cs` (Visuals page 14 → **15** rows and a
Jet-row assertion), `VisualFlagsTests.cs` (`Describe()` gains `hud.jetSkin=off`),
`Skins/HudSkinCatalogTests.cs` (2 → **3** built-ins, Jet present),
`Skins/HudSkinMigrationTests.cs` (the "unknown id" example was `jet`, now `modern2`; `jet` is added to
the normalization theory proving the persisted value is kept).

### Design decisions

* **Gate as a delegate, not a settings read.** `HudSkinSwitcher` never imports the visuals object: Game1
  passes `() => _visuals.JetSkin`. Selection stays a reference swap, Jet's membership is decided at Cycle/
  Request time (no rebuild, no allocation), and tests can flip the gate without a Game1.
* **Installed ≠ selectable.** Jet's definition always loads (so a persisted `"Skin":"jet"` normalizes and
  validates), but with the flag off it is not in the cycle and `Request("jet")` returns false. On start-up
  with the flag off, a persisted `jet` shows Modern (Modern option defaults, `Style` → Modern) and the
  settings keep `jet`; the boot `hud.json` save therefore re-writes `jet`, not `modern`.
* **Runtime flag-off fallback at the frame boundary.** `ObserveJetAvailability(false)` only *queues*
  Modern; `CommitHudSkinChange` applies it after Update, exactly like a key/options switch, and (as for
  any successful activation) persists it — so the saved `Skin` becomes `modern` when the player turns the
  flag off while Jet is active. No half-switched frame and no work inside a sprite pass.
* **Classic composition for every shared pass.** Jet is monochrome and native-adjacent; radar (native
  bracket art), ship tags (native side colours), pod/death, command summary and chat/cargo/flight parts
  use the Classic services, not Modern's composed variants. Later Jet stories can reskin each pass.
* **Instrument gate, not flight gate.** Speed/throttle follow `HudModel.InstrumentsVisible` (flight and
  not docked/dead/command), the same native group the Classic gauges use, so Jet never widens what native
  shows; the gun cross follows `ReticleVisible` (cockpit only, native indicator cameras
  `WinTrek/indicator.cpp:762-797`).
* **ASCII tag.** HUD fonts are ASCII (`HudFormat.Printable`), so the P972 em dash is drawn as a hyphen:
  `JET / GREEN - WIP`.
* **Palette is data, not code.** Jet's colours live in `jet.skin.json` and are parsed once in the renderer
  constructor; Classic/Modern definitions carry no palette block and therefore stay byte-identical.

## 3. Tests

New `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/JetSkinScaffoldTests.cs`:

* built-in Jet definition + P972 palette + Modern-equivalent defaults; Classic/Modern definitions carry no
  palette;
* palette validation: a bad colour in any of the five roles rejects the whole definition
  (`HudSkinLoadException`) and the catalog keeps the prior skin; a palette missing a role is rejected
  all-or-nothing; the hex parser accepts/rejects the documented shapes;
* the flag is a Visuals toggle, default OFF, `hud.jetSkin=on` parses;
* N-skin cycle order and wrap both ways with the flag ON; flag OFF keeps exactly the old two-skin cycle,
  `Request("jet")`/`Request(" JET ")` false; flag ON accepts `Request("JET")` and activates it; the
  Ctrl+F6 row text names Jet;
* runtime flag-off fallback while Jet is active (atomic, then Modern) and while Jet is only queued (never
  activates Jet); idempotence;
* persistence across restart with the flag ON, and the flag-OFF case (shows Modern, `Skin` stays `jet`
  in memory **and** on disk after a save);
* **0 B/frame** for 3-skin input/selection/dispatch (100,000 frames through `ActiveHud`) and for
  `JetHudRenderer.Draw` on a fixed model (100,000 frames);
* Jet draw smoke for 13 `HudModel` states (cockpit, chase, command, docked, lifepod, dead, ghost, turret,
  training, hidden, no-telemetry, empty-telemetry, notices): no throw, shared passes exactly once each in
  the Classic composition, no invented zero, coverage = the three story-1 ids;
* vector primitives: the dark under-stroke is submitted first and is wider than the ink; 16 line
  submissions for corner brackets; 9 text submissions (8 keyline offsets + ink); stroke floors at 1 px
  and scales to 6 px at density 4.

Results (this container, Release), each run 3× alone as the lane rule requires:

```
run 1: Passed 45, Failed 0, Skipped 0 (470 ms)
run 2: Passed 45, Failed 0, Skipped 0 (463 ms)
run 3: Passed 45, Failed 0, Skipped 0 (472 ms)
```

The two 0 B/frame tests print `P1035 3-skin input/selection/dispatch: 0 B / 100000 frames = 0 B/frame`
and `P1035 JetHudRenderer.Draw: 0 B / 100000 frames = 0 B/frame`. Nothing here exceeds 10 s, so no
`[Trait("Tier","Live")]` is used and all 45 run in the fast tier.

## 4. Screenshots (read back with the vision model)

`tools/hud2-proof --jet <dir>` at the two required resolutions (all PNGs ≤ 2 MB total: 9 images,
868 KB). Every image was opened and described; the pixel check below is corroboration, not a
substitute for reading them.

| Image | What is actually visible |
|---|---|
| [`jet-flight-1920x1080-dark.png`](JET_SKIN_SCAFFOLD_P1035_assets/jet-flight-1920x1080-dark.png) | Centre broken green cross; `SPD / MPS` caption over a boxed `260`; `THR` caption over a boxed `100%` (the fixture frame has the afterburner latched, so throttle is +1); the native Classic radar icons in a row across the middle; green `JET / GREEN - WIP` bottom-centre. The readout plates are near-black with a dark border. |
| [`jet-flight-800x480-dark.png`](JET_SKIN_SCAFFOLD_P1035_assets/jet-flight-800x480-dark.png) | Same four Jet elements at the small-window floor: the cross is shorter, the boxes tighter, the tag sits above the bottom edge with room to spare; nothing is clipped. |
| [`jet-flight-800x480-bright.png`](JET_SKIN_SCAFFOLD_P1035_assets/jet-flight-800x480-bright.png) | The same glyphs on the light backdrop; the vision read-back confirms the green strokes **and** text carry a black outline, so the HUD stays legible off a bright scene. |
| [`jet-radar-overlays-1920x1080-dark.png`](JET_SKIN_SCAFFOLD_P1035_assets/jet-radar-overlays-1920x1080-dark.png) | The shared Classic radar pass: faint native round border, a row of native contact icons (ship/station/warp/rock/missile/probe/mine/treasure glyphs), plus Jet's cross/readouts/tag. Confirms radar still works while Jet is active. |
| [`jet-lifepod-800x480-dark.png`](JET_SKIN_SCAFFOLD_P1035_assets/jet-lifepod-800x480-dark.png) | Pod state keeps the flight instruments (cross + speed + throttle) and the tag; identical readouts because the fixture pod carries the same speed/throttle. |
| [`jet-docked-800x480-dark.png`](JET_SKIN_SCAFFOLD_P1035_assets/jet-docked-800x480-dark.png) | Docked: **only** the bottom-centre `JET / GREEN - WIP` tag — no cross, no readouts (native hides the centre instrument group when docked). |
| [`jet-command-1920x1080-dark.png`](JET_SKIN_SCAFFOLD_P1035_assets/jet-command-1920x1080-dark.png) | Command camera: no reticle and no speed/throttle boxes (native hides them in command view), tag only; the Classic radar row across the middle. |

Pixel corroboration (`.scratch/p1035/pngstats.py`, decoded RGBA): at 1920×1080 the flight frame contains
1,247 exact `#79FFC0` ink pixels and 395 of them have a near-black neighbour (the under-stroke), the
plate/keyline band is present around both boxes, and no `#C9FFE3` (hot) pixel is painted — story 1 has no
alert state.

One Classic and one Modern capture proving they are unchanged: [`classic-flight-1920x1080.png`](JET_SKIN_SCAFFOLD_P1035_assets/classic-flight-1920x1080.png) and
[`modern-flight-1920x1080.png`](JET_SKIN_SCAFFOLD_P1035_assets/modern-flight-1920x1080.png) in the asset folder are the same flight frame (1920×1080, size 1, dark)
rendered by the identical code path. Read back: the Classic PNG shows the native grey/teal segmented
gauge cluster, `Ripcord in: 9`, `FIRING`, `THRUST/FUEL/AMMO/ENERGY`, `SIG: 42%`, `260 mps`, the systems
and sector text top-right, the orders/tips block, the weapon list and the dense native radar range labels
(`A2 Enemy fighter with a long pilot name` 1250 m, `A-4 Aleph` 600 m) — i.e. the untouched Classic HUD.
The Modern PNG shows its cyan gauge rings with percentages (`AMMO 80%`, `ARMOR 82%`, `SHIELD 70%`,
`THRUST 100%`, `FUEL 90%`) and the Modern typography — the untouched Modern HUD. Anything else would have
failed the 0-diff comparison above.

The same frame pair also matches the SHA-256 of the two PNGs copied here
(`abef2436042fc8bd0fe065f808791978124cd3f9d047791d64ec2a585860d742` Classic,
`acddf4821fad1711cdd10d0c9de5f43f693e563fc9f83320c04ad19f97d76613` Modern).

## 5. Parity

No `docs/parity/*.csv` row changes status: the Jet skin is presentation-only (P972 §5 keeps every
existing status; a new look cannot promote a `done-visual` row). Elements Jet draws are the existing
`Cockpit centre reticle`, `Speed` and `Thrust setting` ids from the shared model; nothing is invented
(no altitude/G/Mach/lock identity). `tools/check-glossary-links.py` passes.

## 6. Gate log

| Gate | Command | Result |
|---|---|---|
| Solution build | `dotnet build AllegianceCore.slnx -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false` | **Build succeeded, 0 Warning(s), 0 Error(s)** |
| Tools build | `tools/build-tools.sh` | **build-tools: all 140 tool projects built**, each 0 warnings / 0 errors |
| New tests 3× alone | `dotnet test --filter FullyQualifiedName~JetSkinScaffoldTests` (3 separate processes) | **45/45 pass each run** (470 / 463 / 472 ms) |
| Zero-delta (baseline first, then compare) | `hud2-proof --capture baseline` (5.8 min) → `--capture after` (6.1 min) → `--compare baseline` | **`HUD2_COMPARE_PASS 1848 combinations, 0 pixel diff(s), 0 semantic diff(s), 84 animation-only diff(s), 0 corrupt golden(s)`** |
| Jet evidence capture | `hud2-proof --jet <dir>` | `HUD2_JET_CAPTURE_PASS 7 scenes` |
| Glossary links | `python3 tools/check-glossary-links.py` | `GLOSSARY.md link check OK (898 distinct paths checked)` |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | **14,792 tests, 573.6 s, FAIL**: MonoGame 5,517 passed / 4 not executed, Lobby 193 + Update 55 passed, ClientCore.Tests 2,450 passed / 1 failed, Core.Tests 6,565 passed / 4 failed / 3 not executed (both include their quarantine passes, 2 and 17); 5 calibrated-limit offenders. Every failure is a pre-existing, already-ledgered family on a path P1035 does not touch; each was rerun 3× alone (the table below and the P1035 section of `docs/TEST_FLAKES.md`) and recorded in `docs/TEST_FLAKES.md` (P1035 section) |
| AOT publish | not run — **no `src/Allegiance.Server` or `src/Allegiance.Core` change** | n/a |
| `docker compose config` | not run — no service, port, store or env var changed | n/a |
| Training gate | not run — no training-affecting code changed (`Game1.TrainingIntegration` untouched, the switcher's training-restore path is exercised by the existing `HudSkinSwitchingTests`, 3× green here) | n/a |

The five fast-tier failures and their isolated reruns (each with its exact method filter, its own process,
artwork, `--no-build`):

| Test | Sweep | 3× alone |
|---|---|---|
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | fail (4 s) | fail / pass / pass |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | fail (143 ms, one-ULP bound) | 1 failure in runs 1 and 3, clean in run 2 |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_autonomously_mines_full_bag_and_sells_at_friendly_station` | fail (30.4 s, also over the limit) | pass / pass / pass |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | fail (206 ms) | pass / pass / pass |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | fail (1 s) | pass / pass / fail |

No test was deleted, quarantined or had its assertion relaxed.

## 7. Anything unfinished / caveats

* Jet is a **scaffold**: only the three story-1 elements are drawn by Jet itself; the ladder, tapes, FPM,
  target/lock, stores, caution, RWR and screens are later stories (P1036–P1043). The WIP tag says so.
* The fixture's radar contacts sit on one horizontal line (fixture composition), so in the captures a
  contact can overlap the gun cross; the real radar places contacts anywhere on screen.
* The shared-pass composition is Classic for every pass (documented above); a later story may give Jet
  its own radar/tag treatment.
* No new rig, Live/loopback proof or 200v200 capture: story 1 adds no behaviour, and the training/Live
  gates are not touched (no training-affecting code changed); `tools/run-training-tests.sh` was therefore
  not run. The fast tier does exercise the existing loopback suites (results in §6).

## Native intent

Native's HUD already supplies every value Jet shows: the cockpit centre reticle and its indicator family
are gated by the internal/chase/missile camera check (`WinTrek/indicator.cpp:762-797`) and the reticle
surface is `indicator.cpp:149-150`; the throttle reading is the native axis mapping
`jsValues[c_axisThrottle]*0.5+0.5` with the movement/afterburner override (`WinTrek/WinTrek.cpp:8461-8467`),
the same fraction the shared model hands every skin (`HudModel.cs:414`), and the speed value is the
existing `HudFrame.Speed` whose unit is native `dialog.mdl`'s speed text. Native hides the centre
instrument group when docked and in the command view, which is exactly the `InstrumentsVisible` gate Jet
uses. Jet invents no aircraft physics, no horizon/altitude/Mach/G, no lock identity and no sensor data:
it is a new 2-D presentation of the shared model.

## Modern approach

The skin system stays a validated, versioned definition bound once to an immutable renderer. P1035 makes
the switcher an ordered N-skin registry with a delegate gate, so a new skin costs one definition file, one
renderer and one list entry — no per-frame lookup, IO or allocation (proved at 0 B/frame). Jet's colours
are definition data validated all-or-nothing, its primitives are one shared painter with pre-sized buffers
and a physical-pixel stroke floor, and every non-Jet HUD service keeps flowing through the same
`HudSkinResources` passes the Classic adapter uses, so a work-in-progress skin cannot break chat, radar,
command view, pod/death, cargo or flight parts. Classic and Modern remain byte- and pixel-identical, which
the 1,848-combination decoded-RGBA comparison proves.
