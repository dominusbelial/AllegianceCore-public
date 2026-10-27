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

- The label **selection law** is unchanged from P828 and native: it never labels
  a contact the radar mode suppresses (`RadarLabelModel.Resolve` first). The
  levels only change *how many* of the allowed labels draw and how.
- **Full** = the native loop (every label, no cap, no merge) **plus** the
  native distance fade — the P828 "off" behaviour with the fade applied. The
  near ceiling follows P828's shipped `Select` (fades from a 1.0 max, not the
  commented-out 0.75 — see "classification" note).
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
  tag + Auto-change toast.
- `src/.../Rendering/InputActionCatalog.cs`: `CycleHudLevel` action (0).
- `src/.../Game1.FlightInput.cs` / `Game1.Director.cs`: cycle-key handling + the
  Auto-level state.
- `src/.../Game1.GpuBudget.cs`: options row 20 + case 19; `SaveHudSettingsQuietly`.
- `src/.../Game1.Rendering.cs`: `UpdateHudAutoLevel` (1 Hz in-sector ship count).
- `tests/.../P836HudLevelTests.cs` (new, 19 tests).
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
- One fast-tier run: `tools/run-tests.sh --tier fast` → **10,865 tests**; the
  `Allegiance.ClientCore.MonoGame.Tests` suite (which carries every P836 change)
  **3515 passed / 0 failed**. The run's only failure was the pre-existing,
  already-documented load flake
  `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`
  (server command-relay timing, untouched by P836; **passes 3/3 alone**; see
  `docs/TEST_FLAKES.md`).

## Screenshots — PENDING (Claude runs the live 200v200)
The brief asks for 2560×1440 proof frames of each Modern level in a crowded
(~390-ship) sector, plus Classic and a `NativeBlock` vs `EdgeDot` pair, each
< 1 MB, in `docs/reports/P836_assets/`. Producing those needs the full client
against a live **200v200** showcase sector — per the pi-lane rule *"Skip anything
that says live on the rig / 221 / 200v200: Claude runs those"*, these are handed
to Claude. Recipe:
`scripts/live-proof.sh` with the existing crowded/showcase proof setup (P828
used the same `omega` crowd), `--size 2560x1440`, per level: set `HudSettings.Level`
via the `0` cycle key or the F6 row 20, capture Full/Tactical/Combat/Cinematic
(Modern), one Classic frame, and one frame with the Tactical off-screen rule
flipping between NativeBlock (force via `RadarDeclutter=false` to emulate the
P828 look, or set Full) and EdgeDot. Look at each PNG and describe the density.

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
  mode (the sector slowly filling) without flicker. One honest deviation to
  flag: the level fade **near ceiling** stays at P828's shipped effective 1.0
  (the commented-out native `closeAlpha` 0.75 was never actually honoured by the
  shipped `Select` — P828's own fast-tier test pins alpha 1.0 near), so the
  levels preserve P828's shipped look rather than reviving the dormant 0.75.
  If Domingo wants the true native 0.75 ceiling, that is a one-line change
  (`Select` passing `options.MaxAlpha`) but it would alter the P828 fast-tier
  test that pins 1.0 — flagged rather than silently changed.
