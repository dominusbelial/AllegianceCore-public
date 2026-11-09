# P980 — Skins phase 1A: HUD golden baseline and coverage inventory

**Status:** tools/tests/docs only. **No file under `src/` changed** — Classic and Modern render exactly what they
rendered before this task; this slice only makes that output *provable*.
**Base:** worktree of `master` at `66d72e6f` (flight far-clip), tree dirty with this task's files.
**Native reference:** read-only `~/Development/Allegiance/src/` (every native line below re-grepped for this report).
**Proposal:** `docs/reports/UI_SKINS_P972.md` §1, §2.3–2.7, §9 phase 1.

---

## 1. What this slice delivers

| Deliverable | Where |
|---|---|
| Bigger, richer render matrix (adds 800×480, 3840×2160, bright/dark backdrops, 6 coverage scenarios) | `tools/hud2-proof/Program.cs` |
| `--capture <dir>`: decoded RGBA + SHA-256 of the **decoded** pixels, semantic snapshot, deterministic `manifest.json` | `tools/hud2-proof/Program.cs` |
| `--compare <baseline>`: re-render vs baseline, changed-pixel counts, diff images, non-zero exit on any difference, golden-file integrity check | `tools/hud2-proof/Program.cs` |
| `--diff-captures <a> <b>`: offline capture-vs-capture comparison (the determinism proof) | `tools/hud2-proof/Program.cs` |
| `--self-test <baseline>`: proves the compare mode is honest (edited golden, 1 changed pixel, semantic change) | `tools/hud2-proof/Program.cs` |
| One-command self-test against the committed sample baseline (`P980_GOLDEN_SELFTEST_PASS`) | `tools/hud2-proof/golden-selftest.sh` |
| GPU-free, exact RGBA8 PNG decode/encode for the golden path | `tools/hud2-proof/PngCodec.cs` |
| Coverage inventory generator (every `hud` row + every `screens` row) | `tools/hud2-proof/coverage.py` |
| Inventory snapshot (regenerable) | `docs/reports/P980_assets/coverage.json`, `docs/reports/P980_assets/COVERAGE.md` |
| Full manifest (review copy of the outside-repo baseline) | `docs/reports/P980_assets/manifest.json.gz` |
| Runnable mini-baseline: Classic + Modern at 800×480 **and** 4K | `docs/reports/P980_assets/sample-baseline/` |
| Live-tier proof harness timeout raised for the bigger matrix | `tests/Allegiance.ClientCore.MonoGame.Tests/ModernHudTests.cs` |

The original HUD-2 harness rendered 16 scenarios × 5 resolutions × 3 sizes × 2 skins (480 combinations; P972 §2.7
quotes 234 for the same matrix — that arithmetic was wrong, the code always produced 480). Every one of its assertions
is kept: real measured font bounds, no text overlap, inventory name/stats column fit, the HUD-9b/HUD-11/FONT-1c pinned
console anchors, the SYSTEMS-list-versus-inventory-pane rule, and Modern's "every visible element was drawn" coverage.

## 2. The matrix

`22 scenarios × 7 resolutions × 3 sizes × 2 backdrops × 2 skins = 1848 combinations`

* **Resolutions** — 1920×1080, 2560×1440, 1280×720, 1600×900, 1366×768 (the previous five) **+ 800×480 + 3840×2160**.
  264 combinations are rendered at 800×480 and 264 at 4K.
* **Sizes** — 0.75 / 1.0 / 1.5 (the persisted Modern `Size`; 1.5 also turns `ColorBlindSafe` on, as before).
* **Backdrops** — `dark` `Color(40,53,76)` and `bright` `Color(225,215,205)`, now for **every** scenario (the old
  harness only gave `critical` a bright backdrop, so translucent/blended HUD pixels were never compared against a light
  background).
* **Skins** — Classic (shared `HudRenderer` + the production `RadarBracketRenderer` pass and measured Classic notices)
  and Modern (`ModernHudRenderer`), both through the production renderers with real fonts and native artwork.

Scenario set: the 16 HUD-2 scenarios (`flight`, `chat-stress`, `combat`, `critical`, `chase`, `docked`, `lifepod`,
`dead`, `command`, `hidden`, `inventory`, `systems`, `radar-All`, `radar-Default`, `radar-Target`, `radar-Minimal`)
**+ 6 added for parity rows no scenario showed** (§7):

* `training` — native `RollTrainingOverlay` callout (indicator.cpp:634-641) + the P949 one-time control hint.
* `ghost` — ghost/death banner, lives badge below 10, deathmatch kills badge.
* `commandee` — native command-recipient verb line (HUD-10 `DrawCommandee`).
* `target-full` — every target-pane field populated (ore, kill bonus, TTI), the opt-in selection bracket, the granted
  ballistic lead marker, own detection eye and carried artifact.
* `slide` — inventory + target panes mid-slide (`InventorySlide`/`TargetPaneSlide` 0.5).
* `radar-overlays` — every radar overlay flag at once (queued order + command icon, threat/threat-flash, subject,
  carried flag/artifact, ripcord, detection eye, ore/structure fill bars).

The 16 original scenarios are unchanged, so their assertions still guard exactly what they guarded before.

## 3. Determinism: what was nondeterministic and how the fixture fixed it

Requirement: capture twice, prove 0 differences, and fix any nondeterminism **in the fixture, never in the renderer**.

A grep of both HUD renderers for clocks/random finds exactly **one** wall-clock site that reaches pixels:

```csharp
// src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.cs:166-167
float pulse = .55f + .45f * MathF.Sin((float)Stopwatch.GetTimestamp() / Stopwatch.Frequency * MathF.Tau * 2);
DrawMeasuredText(_small, "!", new(center.X + radius, center.Y - radius), color * pulse, true);
```

That is Modern's low-resource "!" badge: its alpha is a sine of the **absolute machine time**, so every run renders it
differently. It fires whenever a gauge is at/under its alert threshold (`Hull/Shield/Fuel/Ammo ≤ .15`, `Energy ≤ .05`),
i.e. in `critical` and `lifepod` — 84 of 1848 combinations.

Two more renderer-instance state fields are frame-timing dependent (`_dt` from `Stopwatch.GetElapsedTime`,
`_levelToastRemain`, `ModernHudRenderer.cs:114-116`, `:128-129`); they drive gauge easing and the HUD-level toast.

Fixture fixes (renderer untouched):

1. **Per-combination frame state.** `ModernHudRenderer`'s private frame cache (`_animated`, `_previous`, `_dt`,
   `_levelToastRemain`, `_shownLevel`) is reset to its constructor values before every draw
   (`Proof.ResetModernFrameState`). With `_previous = 0` the renderer itself uses `_dt = 1` on that draw, the eased
   gauge value starts at its model value (so easing is an exact no-op), and no level toast can leak in. A renamed field
   fails loudly instead of silently reintroducing wall-clock drift. (One renderer per user `Size` is reused: its
   constructor builds two SDF glyph atlases, ~9 s each on this host — see §8.)
2. **An explicit, recorded animation mask.** The animated badge is placed through `DrawMeasuredText`, so the renderer's
   own measured text box for `"!"` (plus its translucent background fill, inflated by 4/2 px) is *exactly* the animated
   region. Each combination records that mask in the manifest and snapshot, stores both the full-pixel hash **and** the
   hash of the pixels outside the mask, and the comparison counts pixels inside versus outside the mask separately.
   The mask is 22 px (800×480, Size .75) to 2 960 px (4K, Size 1.5) — 0.006 % of a 4K frame at worst.
3. **No fixture clock, no randomness.** Radar pulse phase (`HudContext.RadarPulse`), contact set, chat, notices and
   every other frame-varying input are constants of the fixture.

Result — two consecutive full captures of the same tree compared offline:

```
HUD2_DETERMINISM_PASS 1848 combinations: 0 pixel diff(s), 0 semantic diff(s), 83 animation-only diff(s), manifest equal=True
manifest.json identical apart from the masked animation hashes: True
```

The 83 (of 84 possible) animation-only entries are the badge pixels; **every** differing pixel is inside the recorded
mask (`outside: 0` on each entry), which is what makes the mask honest rather than a blanket exemption. This is also why
the brief's "fix through the fixture" is achievable without touching `src/`: the wall-clock sine itself is a finding for
the slice that owns the renderer (§8, item 4).

## 4. Baseline location, size, commit

Baseline (outside the repo, per the evidence rule):

```
~/allegiance-evidence/hud-goldens/66d72e6f/
  manifest.json              1.30 MB, 1848 combinations (commit 66d72e6fc847, dirty=true)
  png/<combo>.png            1848 lossless RGBA PNGs (decoded pixels are the golden)
  snapshots/<combo>.json     1848 semantic snapshots
  run.json                   timings/mode (diagnostics only; never compared)
  total 460 MB
```

`commit` records `git rev-parse HEAD` (**66d72e6fc847**) plus a `Dirty` flag; the renderer sources were unmodified, the
dirty files are this slice's tool/test/doc changes. Driver recorded in every manifest:
`MonoGame.Framework.DesktopGL`, adapter `Mesa Intel(R) Arc(tm) Pro B50 Graphics (BMG G21)`, profile `Reach`,
surface `Color/Depth24`, MonoGame 3.8.5.1, `SDL_VIDEODRIVER=offscreen`, Ubuntu 26.04.1 LTS, display scale 1.0.

Repeat capture for the determinism proof: `~/allegiance-evidence/hud-goldens/66d72e6f-repeat/` (same size, same
manifest apart from the masked hashes). Reports: `~/allegiance-evidence/P980/{determinism,compare,selftest}/`.

**Semantic snapshot** (per combination, indented JSON, byte-compared): every `HudElement` with `Available`, `Visible`,
native placement (anchor/X/Y/Y-up), value, text, colour and projected screen position; every measured text box (text +
bounds + `FixedAnchor`); the inventory pane with per-row name/stats widths against their column limits and the fit
verdict; the Modern drawn-element set and the resolved Modern scale; the recorded layout findings.

**Manifest entry**: combination id, scenario, width/height, size, backdrop, skin, full-pixel SHA-256, outside-animation
SHA-256, animation mask + masked pixel count, PNG path, snapshot path + hash. No timestamps, so two captures of the same
tree produce byte-identical manifests (modulo the animation hashes).

## 5. Compare mode and the self-test

```sh
# build once (the harness is outside the solution, as every tools/*/*.csproj is)
~/.dotnet/dotnet build tools/hud2-proof/hud2-proof.csproj

# THE GATE for every later phase-1 slice (1B…1G): re-render and compare every combination
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null ~/.dotnet/dotnet \
  tools/hud2-proof/bin/Debug/net10.0/hud2-proof.dll \
  --compare ~/allegiance-evidence/hud-goldens/66d72e6f

# quick loop while editing one area (substring match on "<scenario>/<W>x<H>/s<size>/<backdrop>/<skin>")
… --compare ~/allegiance-evidence/hud-goldens/66d72e6f --filter "flight/800x480" --max-diffs 5

# the committed mini-baseline (Classic+Modern at 800×480 and 4K) — works straight from a fresh clone
… --compare docs/reports/P980_assets/sample-baseline

# re-capture (only when the FIXTURE legitimately changes; never to "bless" a renderer change)
… --capture <newDir> ; … --diff-captures <oldDir> <newDir> <reportDir>   # 0 differences required

# prove the compare mode itself still detects changes (either form)
tools/hud2-proof/golden-selftest.sh                                   # committed sample, ~40 s incl. build
… --self-test ~/allegiance-evidence/hud-goldens/66d72e6f             # the real baseline
```

CLI note: the old harness wrote a handful of sample PNGs whenever it was given an output directory; that implicit
capture is now the explicit `--samples` flag (the default legacy pass writes only `render-checks.json`, which is what the
Live-tier test runs).

Behaviour:

* exit `0` = no difference; `1` = at least one difference; `2` = harness failure/assertion break.
* per-combination changed-pixel count **outside** the animation mask, plus the count **inside** it (informational);
* diff image for the first N differing combinations (`--max-diffs`, default 10) with changed pixels magenta on a
  25 %-dimmed baseline and masked pixels cyan; `compare-report.json` with the full list;
* semantic differences print the first 8 differing snapshot lines and fail the run;
* **golden integrity**: the stored PNG must decode to the hash its manifest recorded, and the snapshot file must hash to
  its manifest entry — a hand-edited or truncated golden fails instead of silently passing;
* missing/extra combinations and a changed animation mask are differences, so the fixture cannot silently shrink.

Self-test (`HUD2_SELF_TEST_PASS`, 7/7 assertions, run against the real baseline):

1. untouched copy passes (0 differences);
2. a **single hand-edited pixel** in the golden PNG is caught by the manifest-hash check (`corruptGoldens = 1`);
3. the same pixel with the manifest re-signed (i.e. what a *renderer* regression looks like) is reported as
   **`1 changed pixel(s)` outside the animation mask** and a diff image is written;
4. restoring the golden passes again;
5. an edited semantic snapshot is caught by its hash, and with the manifest re-signed by the snapshot **content diff**;
6. restoring it passes. The self-test therefore proves pixel detection, count accuracy, diff-image emission, semantic
   detection and both integrity checks — in both directions.

## 6. Sample captures committed (≤ 2 MB task cap)

`docs/reports/P980_assets/` totals **1.74 MB**:

| File | What it is |
|---|---|
| `manifest.json.gz` (188 KB) | the **full** 1848-combination manifest for review (`gunzip -c manifest.json.gz \| head`): every hash, mask and path of the baseline |
| `sample-baseline/` (1.4 MB) | a runnable 4-combination mini-baseline: `flight`, size 1, dark, **Classic+Modern at 800×480** and **Classic+Modern at 3840×2160** (PNGs + snapshots + manifest). `--compare docs/reports/P980_assets/sample-baseline` → `HUD2_COMPARE_PASS 4 combinations, 0 diff(s)` |
| `coverage.json` (172 KB) | the generated inventory (§7) |
| `COVERAGE.md` (40 KB) | the human-readable inventory table |

Read back (every image opened and described, not inferred from pixel counts):

* **800×480 Classic** — the dense native layout at its most cramped: `Current orders:` block top-left, `OUT OF BOUNDS`
  and `Reloading ammo...` banners, twelve radar labels with range text at the left, the segmented gauge arcs and
  `260 mps`/`SIG: 42%`/`FIRING` readouts in the centre, the round ripcord countdown `Ripcord in: 9` **overlapping** the
  radar label block and the centre reticle, `Fig Adv` inventory pane flush against the right and bottom edges with
  ellipsised names, and `COCKPIT` bottom-left. This is exactly the small-window reality the P949 real frames show.
* **800×480 Modern** — the same information with SDF typography and rings: `MOUSE DZ`/chat at the top, six circular
  gauges with labels (`AMMO 80%`, `ARMOR 82%`, `SHIELD 70%`, `THRUST 100%`, `ENERGY 90%`, `FUEL 90%`), `KB 15%`,
  telemetry tags (`AUTOPILOT`, `AFTERBURNER ACTIVE`, `PWR 100%`, `WEAPONS: ALL`, `VECTOR LOCK`, `MINING`, `TIME
  DILATION 85%`), the `HUD Tactical` tag, the `[TEAM] returning` composer, and the inventory pane again flush to the
  right/bottom edge (its last row sits on the bottom pixels).
* **3840×2160 Classic** — the native fixed-pixel HUD, i.e. everything at 1:1 pixel size in a 4K frame: legible but tiny,
  with the radar cluster and the ripcord box crowding the centre and the inventory pane hugging the bottom-right corner
  (+024deg roll dial top-right). 4K therefore does **not** scale the HUD today; the baseline records that truth.
* **3840×2160 Modern** — the same fixed-size ring cluster and SDF text centred in a very large empty frame; the
  density-driven Modern scale (`ModernHudScale.Core(displayScale 1.0, size 1)`) is resolution-independent by design.
  Phase 2/3 legibility work must not move these pixels.

## 7. Coverage inventory

Generated by `tools/hud2-proof/coverage.py` from the baseline snapshots + `docs/parity/parity-matrix.csv`
(full table: `docs/reports/P980_assets/COVERAGE.md`, data: `coverage.json`).

**hud rows: 111/111 inventoried, 0 unmapped**

| Kind | Rows | Meaning |
|---|---|---|
| visible `HudElement` in ≥1 scenario | **96** | the row is in the semantic snapshot as `Visible`, with its placement, value and text |
| matched by measured renderer text / structure | **5** | `Roll tutorial cue`, `Training HUD callouts`, `Command recipient verb and wing controls`, `Console pane visibility and slide animation`, `HUD style and software layouts` |
| **covered by the fixture** | **101** | 96 + 5, across the 22 scenarios |
| model-only (available, never visible) | **3** | `Observer and turret board state` (native loadout-only image, dialog.mdl:2106), `Money credits` (native moneyPane is investment-pane-only, dialog.mdl:5199-5203), `Inventory turret gunner identity` (`HudElements` hard-codes the row unavailable — a model gap for 1D/2D) |
| owned elsewhere (no HUD pixels) | **7** | see the list below |

New coverage added by the P980 scenarios: target ore, target kill bonus, target time-to-intercept, selected target
bracket, launcher arming progress, own detection eye, carried artifact, ballistic lead marker, radar queued-order
bracket + command icon, radar threat flash, radar command subjects, radar carried flag/artifact markers, radar ripcord
marker, radar ore/miner fill bars, radar friendly detection eye, roll tutorial cue, training callouts, death banner,
lives remaining, mission kills, command recipient, inventory/target pane slide, plus the three radar-label rows the
overlay scenario stresses.

**Rows no hud2-proof scenario shows today** (not fixable by this fixture — they need a different surface before 1D
claims them):

| Row | Why | Needs |
|---|---|---|
| `Target model preview` | P805 3-D target-preview renderer (world/model pass in `Game1.Rendering`), not a HUD widget | real-window scenario (or a preview-renderer fixture) |
| `Teleport lead-up sparks (ripcord + aleph)` | `WarpTransitParticles.EmitTeleport` world effect | real-window capture |
| `Mission countdown` | native game-state screen clock (`Game1.Rendering`, `MissionClock`); neither HUD renderer draws it | game-state screen scenario |
| `Incoming missile warning` | source-ship **audio** loop (`Audio/MissileWarningController`), no HUD pixels by design | shared-audio treatment (P972's explicit "shared-audio/world-effect" class) |
| `Radar range fade` | the native 100/1000 m alpha block is commented out (radarimage.cpp:689-700) — nothing draws | shared-audio/world-effect class (no visual equivalent) |
| `HUD base class contract` | `n/a` parity row (abstract C++ wrapper) | none |
| `Game list screen boundary` | `implemented`, merged with the server-browser **screens** row | screen scenario (see below) |

**screens rows: 103, and 0 of them are shown by a hud2-proof scenario** — the fixture renders the flight HUD only. Each
row is inventoried with the screen family its port cite names and the test files that mention that family
(`coverage.json` → `screens`). **29 rows have no test that even mentions their renderer family**, including the whole
hangar-screen visual family (button face strips, launch-pane animation, hover text, in-turret badge, hangar-on-dock),
the intro credits/help/replay-intro buttons, the splash/timeout screen, the training screen family (mission list,
back-to-intro, basic flight, slideshow flow), the squads screen, the teleport-pane back/recovery path, the sector-map
target-to-order interaction, the F-key help overlay, the mission-briefing body and the key-rebinding popup. Those are
**the screen scenarios 1F needs**; 1A records them rather than inventing them.

## 8. Findings for the later slices (blocking notes, none of them a renderer change here)

1. **Small-window layout collisions at 800×480 (real, recorded, not fixed).** 164 of 1848 combinations carry layout
   findings (2 180 recorded overlaps in total): 162 are the 800×480 band (≈16 recorded overlaps per Classic frame,
   ≈3 per Modern frame — 2 088 of the 2 180 lines are 800×480 Classic) and 2 are
   `radar-overlays/1366×768/s1.5/Modern`. Most common pairs: `Current location:` vs `TIME DILATION 85%` (156),
   `+024deg` vs `Alystra` (138), `Ripcord in: 9` vs a radar label (114), `NO ENEMY FIGHTER IN SECTOR` vs
   `Reloading ammo...` (108). The fixture's 18-contact + long-name radar density is higher than a real 800×480 frame, so
   this is *fixture-crowded* rather than proof of a production bug — but the P949 800×480 reality is close enough that
   1D should treat the 800×480 band as the first place a layout change can regress. They are stored in the snapshot
   (`Findings`) and summarised on stdout (`HUD2_FINDING`), not thrown, because the golden must hold today's output.
2. **The inventory pane runs to the last pixel of the right/bottom edge at 800×480** (visible in both committed samples).
   The column-fit assertion passes (names are ellipsised, stats scaled into the native slot), so this is a pane-placement
   question, not a fit bug — worth a look when 1D touches pane ownership.
3. **Two gate duplications that a skin migration can silently split.** (a) `HudElements` decides "Selected target
   bracket" visibility from `HudContext.SelectionBracket` (`HudElements.cs:414-418`) while `HudRenderer` decides to draw
   it from its own `SelectionBracket` property; the fixture must set both. (b) `HudElements` ignores two native option
   gates: gauges stay `Visible` when `CentreHudVisible=false`, and `Chat history` stays `Visible` when
   `ChatHistoryVisible=false`. The Modern "every visible element drawn" assertion caught (b) immediately — a `toggles`
   scenario had to be dropped. Both belong to 1D/1E: **one resolved gate, not two copies.**
4. **Modern has a wall-clock animation** (`ModernHudRenderer.cs:166`, the low-resource "!" badge alpha) and a
   wall-clock frame delta (`:114-116`). Until 1C/1D gives the renderer a caller-supplied frame clock, 84 of 1848
   combinations can only be compared with the recorded mask. That is a deliberate, tiny (≤ 2 960 px) and documented
   exemption — not a blessed hash.
5. **`ModernHudRenderer`'s constructor costs ~9 s** (two SDF glyph atlases built by brute force over a 13×13 distance
   window, `HudSdfFont.cs:19-60`). A renderer per combination is therefore unaffordable; the fixture reuses one per user
   `Size` and resets the frame state instead. If 1C/1D changes when SDF atlases are built, revisit that reset.
6. **PNG decoding must stay off the GPU.** The first determinism attempt decoded two 4K goldens per combination through
   `Texture2D.FromStream` and aborted with `intel: the execbuf ioctl keeps returning ENOMEM` after ~800 combinations.
   The golden path now decodes/encodes RGBA8 in managed code (`PngCodec.cs`), which also makes the hashes
   host/driver-independent. Decoded PNGs round-trip exactly (0 corrupt goldens over 1848 × 2 files).

## 9. Verification

| Gate item | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (0 warnings) | _see §9.1_ |
| `tools/build-tools.sh` (every proof project, incl. this one) | _see §9.1_ |
| legacy render pass (all 1848 combinations, every HUD-2 assertion) | `HUD2_RENDER_PASS 1848`, exit 0, 100 s |
| double capture → offline compare (determinism) | `HUD2_DETERMINISM_PASS`: 1848 compared, **0 pixel diffs**, **0 semantic diffs**, 83 animation-only, manifest equal modulo the masked hashes |
| `--compare` against the baseline (the later-slice gate) | `HUD2_COMPARE_PASS`: 1848 compared, 0 pixel diffs, 0 semantic diffs, 84 animation-only, **0 corrupt goldens** |
| `--compare docs/reports/P980_assets/sample-baseline` (committed mini-baseline) | `HUD2_COMPARE_PASS 4 combinations` |
| compare self-test | `HUD2_SELF_TEST_PASS`, 7/7 steps (§5) |
| affected Live-tier test 3× alone | _see §9.1_ |
| one fast-tier suite | _see §9.1_ |
| coverage inventory | `P980_COVERAGE hud=111 … unmapped=0 screens=103` |

Timings (this host, real GPU, offscreen SDL): capture 6.9 / 7.0 min per full pass (460 MB), render-based compare
3.5 min, capture-vs-capture compare 3.5 min, self-test ~1 min, legacy assertion pass 100 s.

### 9.1 Gate log

| Gate | Command | Result |
|---|---|---|
| Solution build | `~/.dotnet/dotnet build AllegianceCore.slnx` | **Build succeeded, 0 Warning(s), 0 Error(s)** (13.8 s) |
| Tools build | `DOTNET=~/.dotnet/dotnet tools/build-tools.sh` | **build-tools: all 123 tool projects built** (3 m 12 s), every project 0 warnings |
| Glossary links | `python3 tools/check-glossary-links.py` | OK (680 paths) |
| Legacy assertion pass | `hud2-proof .scratch/p980/render --test` | `HUD2_RENDER_PASS 1848 …`, exit 0, 1 m 40 s |
| Determinism (double capture) | `--capture A`, `--capture B`, `--diff-captures A B` | `HUD2_DETERMINISM_PASS`: 1848, 0 pixel / 0 semantic / 83 animation-only, manifest equal modulo mask |
| Compare gate | `--compare ~/allegiance-evidence/hud-goldens/66d72e6f` | `HUD2_COMPARE_PASS`: 1848, 0 pixel / 0 semantic / 84 animation-only / 0 corrupt goldens (3 m 31 s) |
| Committed sample gate | `--compare docs/reports/P980_assets/sample-baseline` | `HUD2_COMPARE_PASS 4 combinations` |
| Compare self-test | `tools/hud2-proof/golden-selftest.sh` | `HUD2_SELF_TEST_PASS`, 7/7 steps, `P980_GOLDEN_SELFTEST_PASS` |
| Affected Live-tier test 3× alone | `dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --filter Both_skins_render_Hud1_scenarios` | **3 pass / 3**, 1 m 40 s each |
| Fast tier (one run) | `tools/run-tests.sh --tier fast` | 13 628 cases / 553.3 s: **2 unrelated fail** + 3 `SLOW FAST` under load avg 3.7; both failed tests **3 pass / 3 alone** → ledgered in `docs/TEST_FLAKES.md` (P980 section): `BallotVotingLiveTests.Resign_passing_…` and `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer`. No `src/` change in this slice can affect either path |
| AOT publish | not run — **no `src/Allegiance.Server` or `src/Allegiance.Core` change** | n/a |
| `docker compose config` | not run — no service, port, store or env var changed | n/a |

## 10. How later slices use this

1. **Before touching code:** `--compare ~/allegiance-evidence/hud-goldens/66d72e6f` on the clean tree — must be
   `HUD2_COMPARE_PASS` (it is today).
2. **After each extraction step (1B…1E):** the same command. Any `HUD2_DIFF` line is a regression; a semantic diff is a
   changed visible element/text/bound; an animation-only line is the documented badge.
3. **Per-family work can loop small:** `--filter "flight/800x480"` or `--filter "/4K"`… (`--filter` matches the
   combination id, e.g. `3840x2160`).
4. **If the FIXTURE legitimately changes** (new scenario, new semantic field): capture into a *new* directory, run
   `--diff-captures <old> <new>` and explain every reported difference in the slice report; only then replace the
   baseline path the gate points at. Never re-capture to hide a renderer change.
5. **Screens (1F):** the inventory in §7 is the checklist; the 29 rows without test evidence need scenarios first.

## Native intent

Nothing in the HUD's native behaviour changes here, and the baseline pins the native-derived presentation both styles
must keep. The rules the fixture is built around are the ones P972 §1.4/§1.6 name: native image space is y-up and
`StringImage` bounds hang below their point (`engine/image.cpp:354`, `:382-390`), `GroupImage::Render` walks the MDL
value list first→last so the first child paints on top (`engine/image.cpp:954-969`), the cockpit velocity marker is
gated on the cockpit camera plus a non-zero rounded source speed (`WinTrek/indicator.cpp:128-150`), the training
overlay text is the native `RenderTrainingOverlay` table (indicator.cpp:634-641 for the roll cue), the target pane's
ore/kill-bonus/TTI slots are the native dialog images (dialog.mdl:4208-4214, 1280-1355), and the board-state image is
loadout-only (`dialog.mdl:2106`). Classic remains the native-layout skin: its captured pixels are the reference every
later slice must reproduce exactly.

## Modern approach

The skin work is gated on *decoded RGBA equality plus semantic equality*, not on "looks the same": 1848 production
renderings (800×480 … 4K, two backdrops, three sizes, both skins), hashed from the pixels, with a semantic snapshot of
every visible element, text bound and inventory fit, a golden manifest that is byte-identical across runs, and a
compare mode that counts and highlights changed pixels and refuses to trust a mutated golden. Determinism is owned by
the fixture (frame-state reset, recorded animation mask, constant inputs) so the renderers stay untouched; the one
wall-clock animation it must mask is written down as a finding for the slice that replaces the HUD clock. The
inventory turns "111 HUD rows are done" into "101 rows have a fixture frame today, 3 are model-only, 7 are owned by
world/audio/screen surfaces, and 103 screen rows still need scenarios (29 with no test evidence at all)".
