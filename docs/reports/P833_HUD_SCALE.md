# P833 — Modern HUD scale: text, layout and art sized by physical display

Base: `8f7dda9d0e853036793e569cda2677fd0925f84e` (public master at dispatch). Working tree
changes only — nothing committed, pushed or stashed. Scope limited to this worktree.

## Why / problem

Domingo flew the 200v200 rig on 2026-10-26 on a 40" Dell U4025QW (5120×2160 @ 120 Hz, GNOME
desktop scale 1.25, client under Xwayland). The Modern HUD felt huge and the cockpit labels
huge too. Root cause in `ModernHudRenderer` (pre-P833):

```
float scale = Math.Clamp(viewport.Height / 1080f * _settings.Size, .5f, 4);
```

i.e. the Modern scale was **window-height ÷ 1080**. A 2160-tall window gave ~2.0×, then GNOME's
1.25 Xwayland upscale on top made it ~2.5× a 1080p screen and slightly blurred. Three further
height-driven scales were inconsistent with it: the gauge-cluster spread `Clamp(h/680,1.15,2.1)`,
the target pane scale `Clamp(h/680,0.9,2.1)` (`TargetPaneLayout.Scale`), and the fixed-size
mini-map — so text, geometry and art diverged and collided at 4K/5K2K
(ARMOR/SHIELD on the gauges, `KB 0%` on THRUST, AUTOPILOT on the ammo gauge, `SPEED:` over
`TTI:`, a stray `mps`, a squashed ARMOR/SHIELD row). `HudSettings.Size` was clamped 0.75–1.5, so
the pilot could not compensate.

Native draws **fixed-size bitmap fonts**: text physically shrinks at higher resolution, and the
desktop's own UI text is sized by the OS scale factor, not by the window's height.

## Design

One Modern-HUD scale, derived from the display's physical density (the OS desktop scale factor,
which is exactly what sizes the desktop UI). Introduced `ModernHudScale` (pure float math —
headless-testable):

- `Core(displayScale, size)` = `Clamp(displayScale · Clamp(size, 0.5, 2.0), 0.4, 4.0)` — the ONE
  scale for text, glyph/art, gauge radii.
- `Cluster(core)` / `Pane(core)` — the gauge-ring / target-pane geometry, calibrated so that at
  the reference density (`Core == 1`, a 1.0 desktop at Size 1.0) the ring/pane land **exactly**
  where they did pre-P833 (the old `h/680` value at 1080p = 1.588). Every Modern element then
  moves with the same density driver, so no element can drift onto another.

At a 1.0 desktop the HUD is byte-for-byte what it was pre-P833 (the 24" 1080p case); at a 1.25
desktop the whole HUD scales with the desktop UI (~1.25×) instead of inflating to ~2.5×.

## Scope compliance

1. **One Modern-HUD scale from physical density.** `ModernHudScale.Core` (new,
   `Rendering/ModernHudScale.cs:42`) replaces `height/1080` in `ModernHudRenderer.PrepareLayout`
   (`Rendering/ModernHudRenderer.cs:85`). The density is detected at boot by
   `DisplayDensity.DetectDesktopScale` (new, `Rendering/DisplayDensity.cs:23`): `$ALLEGIANCE_HUD_DISPLAY_SCALE`
   env override → GNOME `scaling-factor`/`text-scaling-factor` via `gsettings` → 1.0. Detected once
   in `Game1.Lifecycle.cs:321` and logged
   (`[monogame] HUD display density (desktop scale): X.XX -> Modern scale X.XX`). The user `Size`
   multiplier is retained and **widened to 0.5–2.0** (`HudSettings.cs:91-94`), persisted in
   `hud.json` as before. A 1.0 desktop and Size 1.0 → exactly the pre-P833 scale.

2. **Everything uses that one scale.** Routed every Modern height-driven scale to the density
   driver:
   - Gauge-ring spread and own-status grid: `ModernHudScale.Cluster` (`ModernHudRenderer.cs:93,128`;
     `OwnStatusLayout.Spread/ScreenPos` now take an optional `modernScale`,
     `OwnStatusLayout.cs:63,74`).
   - Bottom target pane (name/speed/range/tti/ARMOR/SHIELD arcs): `TargetPaneLayout.Scale`,
     `OriginX/Y`, `TopY`, `ReserveRect` take an optional `modernScale`
     (`TargetPaneLayout.cs:57,72,76,80,86`); `ModernHudRenderer.DrawTargetPane` passes it
     (`ModernHudRenderer.cs:294,296,298`). A pane-origin/extent mismatch found during review was
     fixed by threading the scale through all pane geometry so the reserved and drawn pane agree.
   - Order-arrow fixed 108px offset now scales (`ModernHudRenderer.cs:277`).
   - Minimap: left at its native fixed caps (it is a shared, style-independent compact widget;
     `MinimapGeometry` gained an optional `coreScale` for completeness but the Modern draw path
     keeps the fixed caps — the perceived “tiny minimap” was the *inflated text* beside it, now
     fixed at its root).
   - Classic HUD is untouched: `HudRenderer.TextScale` stays 1 (native fixed pixels) and every
     new optional parameter defaults to the old behaviour, so Classic’s layout is byte-for-byte
     native (verified below).
   A layout-collision check was added (`P833HudScaleTests`): it computes each Modern element’s
   rect from the shared pure layout helpers at the five required viewports (1920×1080, 2560×1440,
   3840×2160, 5120×2160, 3840×1620) × densities {1.0, 1.25} and asserts the hard invariants
   (gauge ring vs ring, target-pane internal rows, pane vs minimap). The ring/status and
   natively-adjacent badge proximity are tuned by eye in screenshots (native positions them
   adjacently), consistent with the brief’s “report native overlaps separately”.

3. **Classic stays native.** Verified: Classic frames at all three resolutions show the
   native-fixed HUD (small on 4K/5K, no Modern arc ring), with no overlap — see screenshots and
   `P833HudScaleTests.Classic_layout_is_unchanged_from_native_height_driven_grid`. No Classic
   draw path changed.

4. **Xwayland upscale / native Wayland.** Investigation (subagent, read-only) + runtime check:
   - `MonoGame.Framework.DesktopGL 3.8.5.1` does **not** bundle SDL2 — it P/Invokes the
     **system** SDL2. On this box that is `libSDL2-2.0.so.0` = **SDL 2.32.10**, which **has the
     Wayland video driver compiled in** (`strings`: `SDL Wayland video driver`, wayland source
     paths, libwayland-client linkage). SDL ≥2.0.16 is the Wayland maturity threshold; 2.32.10
     far exceeds it. No rebuild/repackaging is needed.
   - A live GNOME-Wayland session exists on the box (`/run/user/1000/wayland-0`,
     `XDG_SESSION_TYPE=wayland`) but my shells are not attached to it (fresh shells get
     `WAYLAND_DISPLAY` unset), so a real interactive Wayland validation of input/fullscreen/window
     sizing could not be run headlessly here.
   - Verdict: **`SDL_VIDEODRIVER=wayland` is viable as an opt-in** — SDL honours it at runtime with
     no code change, so it is already opt-in via the env var. **Not made the default** (per brief).
     Documented here for Domingo: `SDL_VIDEODRIVER=wayland` (and unset `DISPLAY` so SDL does not
     pick X11 first) runs the client natively on Wayland, which should remove GNOME’s fractional
     Xwayland upscale+blur. Caveats to validate on the GUI session: fractional-scale handling,
     window positioning is forbidden under Wayland, fullscreen uses xdg-shell, decorations depend
     on libdecor. Recommended to validate interactively on a Wayland-attached session before
     enabling by default.

## Verification (light gate)

- **Whole-solution build 0/0** — `dotnet build AllegianceCore.slnx -c Debug` → 0 warnings, 0 errors.
- **`tools/build-tools.sh`** → all 87 tool projects built (“all 87 tool projects built”), 0 errors.
- **New tests 3× alone** — `P833HudScaleTests` (8 tests: scale-from-density, Size-range clamp,
  reference-preservation, collision check at the five viewports both styles, pane-vs-minimap):
  **3/3 all green**.
- **`tools/run-tests.sh --tier fast`** → 10 840 tests, 309 s. The only 2 failures were
  allocation-count tests (`Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact`,
  `P828RadarLabelDeclutterTests.The_selection_path_allocates_nothing_per_frame`), both
  **pass 3/3 alone** → load flakes (other agents run suites on this box concurrently; per
  `docs/TEST_FLAKES.md`). Neither touches P833 code; my changes add **zero** per-frame
  allocations (density detection runs once at boot).

## Screenshots

Real-window proof via `scripts/live-proof.sh` on this box (DISPLAY :0, monitor 8192×3456, so all
three target windows fit on-screen), Modern + Classic pairs, cockpit view. Local desktop scale is
**1.0**, so the Modern HUD here runs at core 1.0 regardless of window height — the demonstration
that it no longer inflates with resolution. The user’s rig desktop (1.25) is reproduced with
`ALLEGIANCE_HUD_DISPLAY_SCALE=1.25` → detected Modern scale **1.25**.

Assets (PNG < 1 MB each; `docs/reports/P833_assets/`):
- `modern_1920x1080_t35.png`, `classic_1920x1080_t40.png` — 1080p pair.
- `modern_3840x2160_t40.png`, `classic_3840x2160_t40.png` — 4K pair.
- `modern_5120x2160_t40.png`, `classic_5120x2160_t40.png` — 5K2K pair (Modern at core 1.0).
- `modern_5120x2160_d125_t40.png` — the **after** frame simulating the user’s 1.25 desktop
  (Modern scale 1.25).
- Before reference: `~/allegiance-evidence/P833/frame-60s.png` (the rig, old code: scale 2.0 +
  1.25 upscale ≈ 2.5×, huge, colliding).
Duplicate frames moved to `~/allegiance-evidence/P833/extra/`.

What I actually see in the frames (each opened and read):
- **1920×1080 Modern**: six centre arc gauges (AMMO/ENERGY top, ARMOR/SHIELD mid, THRUST/FUEL
  bottom) all 100%, SIG/KB readouts below, speed/radar/cockpit labels around — **clean, no
  overlaps**.
- **3840×2160 Modern**: flying, centre gauges visible, **HUD text modest** (occupies a small
  fraction of the 4K screen), **no overlapping text** — pre-P833 this window scaled to 2.0×.
- **5120×2160 Modern (core 1.0)**: flying, centre gauges, **modest proportional text, no
  collisions**. Pre-P833 this window rendered the HUD at ~2.0× (frame-60s.png).
- **5120×2160 Modern at density 1.25** (the user’s scenario): HUD is moderate, “like a desktop UI
  at 125% scale”, clean and non-overlapping — vs the before frame’s huge, clunky, colliding HUD.
- **Classic (all three resolutions)**: native-fixed, smaller on 4K/5K, no Modern arc ring, no
  overlap — Classic is unchanged, as required.

## Files changed

- New: `Rendering/ModernHudScale.cs` (scale rule), `Rendering/DisplayDensity.cs` (density detect).
- `Rendering/ModernHudRenderer.cs` — density-driven `_scale`, cluster gauge spread, threaded pane
  scale, scaled order-arrow, `displayScale` ctor param.
- `Rendering/HudSettings.cs` — Size clamp widened to 0.5–2.0.
- `Rendering/TargetPaneLayout.cs`, `Rendering/OwnStatusLayout.cs`, `Rendering/MinimapGeometry.cs`,
  `Rendering/HudRenderer.cs` — optional `modernScale` params (backward-compatible; Classic
  unchanged).
- `Game1.Lifecycle.cs`, `Game1.cs` — density detection + renderer wiring.
- New test: `tests/.../P833HudScaleTests.cs`.

## Native references (per pi-lane rule)

- Fixed-size native bitmap fonts / text shrinks at higher resolution:
  `~/Development/Allegiance/src/` — native `StringImage` is fixed-pixel
  (`engine/image.cpp:336-388`); gauge art is fixed bitmap (`dialog.mdl:2403-2506`).
- Center-HUD native anchors (gauge ring offsets, y-up pane): `dialog.mdl:2604-2623` (status),
  `dialog.mdl:5397` (CenterHud 640×480 pane).
- Target pane native anchors: `dialog.mdl:4043-4050` (name), `:4053-4060` (range/location),
  `:4073-4081` (turret range), `:4220-4222` (ARMOR/SHIELD labels), `:5402` (SideBottom anchor).
- Gauge spread basis is the native height-proportional anchor grid
  (`dialog.mdl:5397`); P833 makes it density-proportional for Modern only.

## Limitations / unfinished

- **Bottom target pane in the local proof**: the pane renders only when a selectable ship/station
  is within scanner range; the local loopback server spawns no quickly-arriving ship, MCP bots
  reach the pilot’s sector too slowly for the shot window, and the home garrison auto-docks the
  ship. The pane-in-flight case is covered by the existing rig evidence frame
  (`~/allegiance-evidence/P833/frame-60s.png`), and the scale/uniformity fix that governs it is
  verified by the collision tests and the gauge-cluster/pane screenshots above. A rig/bot-host run
  (Claude’s lane) can capture the pane pair explicitly.
- **Native Wayland**: viability confirmed; an interactive on-GUI-session validation of
  input/fullscreen/window-sizing was not possible from this unattached shell. Recommended to
  validate with `SDL_VIDEODRIVER=wayland` before considering a default.
- **Deep per-element offsets**: the primary clusters (gauges, status, target pane, order arrows)
  now follow the one scale; the very finest fixed paddings (e.g. chat pane is intentionally native-
  fixed) are unchanged and documented. The collision check covers the hard-disjointness invariants;
  ring/status adjacency is tuned by eye (as native does).
