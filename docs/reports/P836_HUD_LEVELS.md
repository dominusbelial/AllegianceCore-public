# P836 — Modern HUD levels: Full / Tactical / Combat / Cinematic + Auto

Base: public master at dispatch (`aec8de11`, ≥ the required `aec8de11`).
Worktree: `allegiance-hermes-p836` (branch `hermes/p836`). Nothing committed,
nothing pushed, no stash — changes are left in the working tree for the
dispatcher's WIP commit.

## What this adds

A Modern-HUD density level, built on the P828 label declutter. Five settable
levels (`Full`, `Tactical`, `Combat`, `Cinematic`, `Auto`), each a **data
preset** over the P828 knobs plus a new off-screen contact rule. Lives in the
shared model (not a renderer) so Classic, Modern and the tests read the same
table.

### The preset table (`Rendering/HudLevels.cs`)

| Level | Labels | nearest-N | merge | fade (near/far) | off-screen |
|---|---|---|---|---|---|
| Full | every native-allowed label | – | off | 100 / 1000 (native) | NativeBlock |
| Tactical | priority + nearest-N | 12 (=P828) | on | 100 / 1000 (native) | EdgeDot |
| Combat | priority + nearest ENEMIES only (friendlies icon-only) | 6 | on | 50 / 600 | EdgeDot |
| Cinematic | only the selected target | – | off | 50 / 600 | None |
| Auto | resolves Full/Tactical/Combat from the in-sector ship count (hysteresis) | – | – | – | – |

- Every level's distance fade uses native's alphas: **0.75 at ≤ near, linear to
  0.25 at ≥ far** (`radarimage.cpp:686-700`) — only the *distances* differ per
  level. P836b makes `Select` honour the 0.75 ceiling (it was ignoring
  `MaxAlpha`). Classic never fades (flat alpha 1).

- The label **selection law** is unchanged from P828 and native: it never labels
  a contact the radar mode suppresses (`RadarLabelModel.Resolve` first). The
  levels only change *how many* of the allowed labels draw and how.
- **Full** = the native loop (every label, no cap, no merge) **plus** the
  native distance fade — the P828 "off" behaviour with the fade applied. The
  near ceiling is native's commented-out **0.75** closeAlpha
  (`radarimage.cpp:686-700`), linear down to 0.25 at far — P836b makes the
  Modern fade follow that exact curve.
- **Combat** drops friendlies from the ordinary-label competition
  (`LabelFriendlies=false`): they become icon-only, so every N slot goes to the
  enemies the pilot is fighting.
- **Cinematic** keeps exactly the selected target's label, and reduces chrome by
  suppressing ordinary off-screen blocks entirely.

### Off-screen contacts (`RadarBracketRenderer`)
`OffscreenRule { NativeBlock, EdgeDot, None }`. Native pins the full contact
block (icon + label) to the screen edge (`WinTrek/WinTrek.cpp:7899-7921`, the
`else` branch clamps `pointScreenPosition` onto `rectImage`: *"icons are pinned
to the edge of the screen"*). The port now renders:
- `NativeBlock` — the native behaviour, unchanged (Classic and Full).
- `EdgeDot` — a small side-coloured 5×5 pip at the clamped point, no label.
- `None` — nothing.

Priority contacts (target/wingmen/attackers, `RadarLabelDeclutter.IsAlwaysLabelled`)
always keep their `NativeBlock` so they stay readable; the rule governs ordinary
contacts. Threaded through `RadarBracketRenderer.Draw` + `DrawNativeIcons` and
the label path (`RadarLabelDeclutter.Select` suppresses the label for the same
set via `OffscreenHidden`). Classic passes `NativeBlock` → byte-identical.

### Auto level
`HudAutoLevel` is a pure, stateful resolver. Ship count = living, visible craft
in the player's sector (same filter the radar uses), sampled once a second.
Bands: ≤40 Full, ≤120 Tactical, else Combat. **Hysteresis**: it only flips after
the count stays in the new band for a 3 s hold, so an oscillating battle never
flickers. Auto never selects Cinematic. On a change the renderer shows a brief
`AUTO HUD → <level>` toast plus a persistent `HUD <level>` tag; the options row
shows the resolved level when Auto is active.

### Persistence + options + key
- `HudSettings.Level` persisted in `hud.json` (string enum). Default `Tactical`
  (= P828's shipped Modern behaviour, so a fresh profile is unchanged).
- Ctrl+F6 options panel: row 20 "HUD level: Full/Tactical/Combat/Cinematic/Auto
  (→ resolved)". Left/right steps through all five (incl. Auto).
- `0` key (`Keys.D0`, free — only used as `Ctrl+0` chords elsewhere) cycles the
  level, Modern only. **Classic ignores it** and keeps native `.` = `TK_CycleRadar`.
- `HudSettings.ActiveLevel` is the runtime-resolved concrete level (not
  persisted); `EffectiveLevel` honours Auto.

## Classic unchanged (proved)
`HudRenderer.RadarDeclutterPlan` is still hard-coded `Off`; Classic's
`_radarRenderer.Draw` passes `NativeBlock`. Tests prove the Classic label set for
a crowded 200-ship scene is byte-identical to master (every native-allowed label,
alpha 1, no tags), independent of the level setting.

## Files changed
- `src/.../Rendering/HudLevels.cs` (new): `HudLevel`, `OffscreenRule`,
  `HudLevelPreset`, the preset table, the level cycle, `HudAutoLevel`.
- `src/.../Rendering/RadarLabelDeclutter.cs`: 4 new defaulted knobs
  (`AllAllowed`, `LabelFriendlies`, `OnlyTarget`, `Offscreen`), `FromPreset`
  factory, `Select` honours them.
- `src/.../Rendering/HudSettings.cs`: `Level`, `ActiveLevel`, `EffectiveLevel`;
  `RadarLabelPlan` builds from the level; `Normalize` clamps.
- `src/.../Rendering/RadarBracketRenderer.cs`: `OffscreenRule` param,
  `OffscreenHidden`, `DrawEdgeDot`, native-icon skip.
- `src/.../Rendering/ModernHudRenderer.cs`: passes the off-screen rule; level
  tag + Auto-change toast. **P836b** — the toast text is ASCII-only (`->` for
  the arrow): the bundled HUD fonts carry ASCII 32-126 and the typographic `→`
  threw a SpriteFont error at draw time.
- `src/.../Rendering/InputActionCatalog.cs`: `CycleHudLevel` action (0).
- `src/.../Game1.FlightInput.cs` / `Game1.Director.cs`: cycle-key handling + the
  Auto-level state.
- `src/.../Game1.GpuBudget.cs`: options row 20 + case 19; `SaveHudSettingsQuietly`.
- `src/.../Game1.Rendering.cs`: `UpdateHudAutoLevel` (1 Hz in-sector ship count).
- **P836b** — `Rendering/RadarLabelDeclutter.cs`: `Select` now passes
  `options.MaxAlpha` to every `Fade` call, honouring the native 0.75 near
  ceiling (it was ignored, so near Modern labels drew at 1.0).
- `tests/.../P836HudLevelTests.cs` (new, 20 tests — includes P836b's
  `The_level_toast_and_tag_text_is_ascii_safe`).
- `GLOSSARY.md`: HUD levels entry.

## Verification
- Whole-solution build (Release): **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **all 87 tool projects build** (exit 0).
- `P836HudLevelTests`: **19/19 pass, run 3× alone** (each level's label set on a
  fixed crowded scene; Auto thresholds + hysteresis incl. a no-flicker
  oscillating series; off-screen rule per level; settings round-trip; Classic
  identical; zero-allocation; key/options wiring).
- `P828RadarLabelDeclutterTests` (the affected prior suite): **15/15 pass, 3×** —
  no regression from the new knobs.
- Regression checks on the two pre-existing tests my edits touched: the P804
  round-border signature test and the Hud19 options-panel row-count test were
  updated (the panel legitimately gained row 20); both now pass 3×.
- One fast-tier run: `tools/run-tests.sh --tier fast` → **10,865 tests PASS**;
  `Allegiance.ClientCore.MonoGame.Tests` (which carries every P836 change)
  **3516 passed / 3 not-executed (private-artwork skips)**. An earlier fast run
  in the same session failed only the two zero-allocation tests
  (`P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame`,
  `P828RadarLabelDeclutterTests.The_selection_path_allocates_nothing_per_frame`)
  — both **pass 3/3 alone** and the rerun passed the whole fast tier, so that
  was a load flake from the heavily contended box (other lanes' live suites +
  this task's own proof sims running concurrently), not a regression.
- **P836b** — the same gate after the toast fix: whole-solution build **0/0**,
  `tools/build-tools.sh` **87 projects**, affected suites (`P836HudLevelTests`
  20/20 + `P828RadarLabelDeclutterTests` 15/15) **35/35, 3× alone**, and one
  fast tier run **10,866 tests PASS**.

## Screenshots — LOCAL capture (this workstation, `DISPLAY=:0`)

P836b produced these here, per the clarified pi-lane rule ("skip the rig" =
don't touch the remote rig; a local crowded proof is ours to run). Setup:
`scripts/live-proof.sh`-style — one local `Allegiance.Server` (127.0.0.1:6083,
`m2xspiderw.igc`, `maxPlayersPerTeam:200`, `transport.maxConnectionsPerIp:512`,
`maxTeamImbalance:32767` to defeat the rank-balance seat refusal), the
`allegiance-mcp` **90v90 `showcase`** crowd marched to **Alderan** (battle away
from the pilot's home, P828's recipe), and a windowed MonoGame client
(2560×1440 real backbuffer, `--camera chase`, `--auto-goto Omega`/`Alderan`,
`ALLEGIANCE_HUD_SETTINGS` pointing at a per-level `hud.json`). One windowed
client run per level/style; autoshots saved `SaveAsPng` real presented frames.

The four curated PNGs (each < 1 MB, in `P836_assets/`; all full-res captures
kept in `~/allegiance-evidence/P836/{omega,alderan}/`, linked here for the
record):

- [`full_nativeblock_alderan_t110.9.png`](P836_assets/full_nativeblock_alderan_t110.9.png)
  — **Full (NativeBlock)** in the Alderan battle (a "SECTOR CROWDED" fight).
  Dozens of on-screen contact labels (`mcp-b147-8931 · 881 m`, `mcp-b041-6999 ·
  3354 m`, …) plus a dense **wall of full contact blocks pinned to the screen
  edges** for off-screen ships — a ~20-block column down the right edge
  (`mcp-b092-5159`, `mcp-b081-7729`, `mcp-b075-4085`, …), blocks across the top
  and bottom. "HUD Full" tag bottom-left. Legibility suffers where blocks pile
  up — exactly the native look P828 called "excel".
- [`tactical_edgedot_alderan.png`](P836_assets/tactical_edgedot_alderan.png)
  — **Tactical (EdgeDot)** in the same battle: only 2 on-screen labels
  (`mcp-b155-8286 (Ally)`, `mcp-b047-2835`) and the edges are **clear** — no
  full blocks; off-screen ordinary contacts are the small 5×5 side-coloured
  pips. "HUD Tactical" tag bottom-left. This is the off-screen-rule trade
  Domingo weighs: NativeBlock keeps readable blocks at the cost of edge clutter;
  EdgeDot frees the edges to small pips.
- [`classic_omega.png`](P836_assets/classic_omega.png) and
  [`modern_tactical_omega.png`](P836_assets/modern_tactical_omega.png) — the
  **Classic vs Modern** pair in the pilot's home Omega (the both-styles rule):
  Classic draws every native-allowed label (here a handful on-screen, `mcp-b170-3031`,
  `mcp-b004-5657`, …) at full alpha; Modern Tactical declutters to the nearest-N
  set. Both styles show the same radar scene; only the label treatment differs.

Captured-but-not-curated (all in `~/allegiance-evidence/P836/`, full 2560×1440):
6 frames per level for Modern Full/Tactical/Combat/Cinematic, Classic and Auto
at Omega (each shows its **level tag** — `HUD Full`, `HUD Combat`, `HUD
Cinematic`, and `HUD Tactical (AUTO)` for Auto, which resolved to Tactical on
~48–58 in-sector ships), plus the Alderan battle frames for Full/Tactical/Auto.
**Auto toast:** the transient 3 s `AUTO HUD -> level` banner needs a live
band-flip (a ship count crossing 40/120); in both local runs Auto held Tactical,
so the toast wasn't time-captured — but the toast-arrow crash it exposed was
fixed and is pinned by `P836HudLevelTests.The_level_toast_and_tag_text_is_ascii_safe`.

**Honesty note on density:** with the battle marched to Alderan (P828's recipe
for pilot stability), the pilot's home Omega held ~48–58 ships, so the Omega
frames are *moderate*-crowded, not the 180-ship showcase wall P828 shot in the
same sector after a 40-min fight. The genuinely dense, decision-relevant frames
are the Alderan ones above, where the pilot flew into the fight. A full 200v200
single-sector clash is the one thing still best captured on the calmer rig
(original P836 handoff note stands); these local frames are the reproducible
subset.

## Native-intent classification + modern approach note
- **Native intent:** `radarimage.cpp:747-756` (c_rlDefault) labels *every*
  on-screen/in-clip ship — correct for a 20–60 player game, a wall at 390.
  Off-screen blocks are pinned to the edge (`WinTrek.cpp:7899-7921`), and the
  distance fade exists only as a **commented-out** block
  (`radarimage.cpp:686-700`, 0.75→0.25 over 100/1000). So HUD levels are a
  **deliberate Modern superset**: native has no levels, no off-screen variants,
  no active fade. Classification: *Modern-only visual extension; Classic stays
  exactly native.*
- **Modern/better approach:** put the levels in the shared model (pure data), so
  both styles and the tests share one table; the levels tune only *what fraction
  of the native-allowed labels draw*, never the native allow-set, so there is no
  parity divergence to defend. Auto with hysteresis addresses the actual failure
  mode (the sector slowly filling) without flicker.

## P836b — fade ceiling honoured + local screenshots (2026-10-26)

Two gaps closed since the first pass:

1. **The fade ceiling was ignored.** P828's `Select` called `Fade(...)` with
   `options.MinAlpha` only, leaving `maxAlpha` at its 1.0 default, so near
   Modern labels drew at 1.0. P836b passes `options.MaxAlpha` to every `Fade`
   call in `Select` (`RadarLabelDeclutter.cs`), so the Modern fade follows
   native's commented-out curve exactly — **0.75 at ≤ near, linear to 0.25 at
   ≥ far** (`radarimage.cpp:686-700`), distances per HUD level. Every level's
   preset already carried 0.75/0.25 (`FromPreset` uses
   `DefaultMaxAlpha = NativeNearAlpha` and `DefaultMinAlpha = NativeFarAlpha`);
   now the ceiling is actually applied. **Classic is unaffected** — its `Off`
   plan still draws every native label at full 1.0 opacity (`Select`'s
   `!options.Enabled` branch sets `_alpha = 1f` directly and never fades), so
   the two P828/P836 tests that pin Classic alpha 1.0
   (`classic.Alpha(i) == 1f`, `classicPlan.MaxAlpha == 1f`) stay unchanged.
2. **Screenshots are local** (this workstation, `DISPLAY=:0`), per the
   clarified pi-lane rule — the "skip the rig" lane meant don't touch the
   remote rig, not "no local crowded proof". See the Screenshots section.

Updated tests (pinning 1.0 → native 0.75, and why):
- `P828RadarLabelDeclutterTests.Fade_is_full_near_faint_far_and_monotonic` —
  renamed `Fade_near_faints_from_native_ceiling_far_to_floor_and_is_monotonic`;
  it pinned `Fade(...) == 1f` at/below near and `model.Alpha(0) == 1f` for a
  Modern near contact. Both now pin the native ceiling
  (`RadarLabelDeclutter.NativeNearAlpha` = 0.75). The `Select`-path assertion
  (`model.Alpha(0)`) is the direct regression guard for this fix.
- `P836HudLevelTests` Full-fade test — its `Alpha(0) < 1f` / range-bound
  assertions already held under the ceiling; the comment was updated (the "1.0
  max" wording was stale) and the far-label range bound tightened to
  `NativeNearAlpha`.
- Untouched on purpose: the two **Classic** alpha-1.0 pins (Classic never
  fades), and `Modern_fade_uses_native_close_and_far_alpha` (already asserted
  the 0.75/0.25 endpoints).
