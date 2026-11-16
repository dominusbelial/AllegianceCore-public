# P1044 — Study: a ship with a real 3-D cockpit (what would it take?)

Base: Claude's master at dispatch. Own worktree and branch. **Investigation + design report.** You MAY build a throwaway
spike in the worktree to measure and screenshot, but the final change set is ONLY the report and its assets: revert the
spike from the tree and save it as `docs/reports/COCKPIT_3D_P1044_assets/spike.patch` (≤ 300 KB). Rules: public-repo
hygiene (no personal paths/IPs/hostnames), **never `git stash`**, `~/.dotnet/dotnet`, never `pkill -f`/`pgrep -f`, no
synthetic CPU load, scratch in `.scratch/p1044/`, do not commit or push, do not contact the rig, mini, Mac or any public
host. Native reference: `/srv/corpus/Allegiance/src/` (read-only; `local.env` points at it and at the artwork). Re-grep
every line number before citing it. Read `AGENTS.md` and `GLOSSARY.md` first.

**Time:** 5 hours max. Write the report as you go (section by section), then the plan.

## What Domingo wants (his words, 2026-11-16)
"Do you think we could have a ship with a real cockpit? … I wonder what it would take, probably putting the whole
classic HUD implementation behind a feature flag to hide it?"

And, added the same day: "a hybrid would be acceptable if it looks good" — e.g. a modelled cockpit frame/console with
some instruments on 3-D displays while other information stays a 2-D overlay. Treat a hybrid as a first-class option and
judge every option by how it LOOKS in the screenshots, not only by architecture.

So: a first-person view from **inside** a modelled cockpit (canopy frame, console, instruments), instead of (or in
addition to) the flat 2-D HUD over the scene. Answer whether it is feasible, what it costs, and the best way to build it.

## Today (verify and extend — cite file:line)
- First-person cockpit camera + the 2-D HUDs: `docs/reports/CLIENTCORE_MONOGAME_COCKPIT_HUD_P211.md`,
  `docs/reports/COCKPIT_HUD_REDESIGN_P241.md`, `HudRenderer.cs` (Classic), `ModernHudRenderer.cs`, the camera modes
  (cockpit / chase / missile / turret / command view) and how the own ship is or is not drawn in cockpit view.
- The HUD skin system (P980/P981/P988): `Rendering/Skins/` — `IHudSkinRenderer`, `HudSkinSwitcher` (currently
  hard-wired to Classic + Modern), `HudModel`/`HudModelAdapter`, Shift+F12. Also the plan in `docs/reports/UI_SKINS_P972.md`.
- The visual-flag system (`Rendering/VisualFlags.cs`, `--visuals key=on`): every visual departure from native ships
  behind a flag, default OFF until Domingo approves it.
- Rendering pipeline: HDR scene path (P984), near/far clip (far 10 000), depth precision, render targets already used
  (target preview), mesh loading (`.mdl` runtime parser), lighting.
- Native: does native Allegiance have ANY cockpit interior (meshes, textures, `CortUI` art, cockpit `.mdl`, per-hull
  cockpit data)? Search the artwork and `WinTrek/` (`indicator.cpp`, camera code). State clearly what exists.

## Questions the report must answer
1. **Approach options**, compared with a recommendation:
   - (A) Domingo's idea: hide the Classic HUD behind a flag and draw a cockpit model in its place.
   - (B) A **cockpit skin**: a third `IHudSkinRenderer` whose instruments are drawn into render targets shown on
     in-cockpit displays (MFDs) plus a collimated HUD glass, using the same `HudModel` — Classic/Modern untouched.
   - (C) A cockpit-only overlay layer (canopy frame + console mesh) that works with ANY skin's 2-D HUD.
   - Anything better you find (e.g. how other space sims do it: Elite Dangerous, Star Citizen, Freespace 2 Open,
     X4, Everspace — public sources only).
2. **Gameplay parity and fairness.** Native behaviour must not change: what the player can see/target, radar contacts,
   lock, lead, warnings. A canopy frame must not hide contacts native shows (brackets/radar must stay usable); the HUD
   information must stay complete (map each of the 111 `hud` parity rows to where it lives in the cockpit — MFD,
   HUD glass, or still a 2-D overlay). Multiplayer: purely cosmetic, no advantage or disadvantage — say how to keep it so.
3. **Assets.** Per hull, per hull class, or one generic cockpit? Sources: procedural/primitive geometry first, hand-made
   low-poly, or AI-generated (Meshy is used for the separate optional HD pack — new assets are fine there). Size, LOD,
   texture budget, licensing, how a cockpit is attached to a hull (eye point, canopy orientation, scale).
4. **Rendering.** Near-plane/depth precision with a cockpit 0.5–2 m from the eye vs a 10 000 m far clip (separate
   depth range / two-pass / depth clear?), lighting the interior (sun, damage flash), HDR interaction, render-target
   cost of live MFDs, readability of small text on 3-D displays at 800×480 and 4K, motion (head bob/shake from
   damage/afterburner — optional), free look (mouse/hat) and how it interacts with mouse flight.
5. **Other views and states:** turret seats, lifepod, death/ghost, docked/hangar, command view, chase camera (no
   cockpit), training lessons (callouts point at HUD elements — what happens), spectator.
6. **Performance.** Measure with the spike: frame CPU/GPU cost of a cockpit mesh + N MFD render targets at 1080p
   in a busy scene (use an existing local scenario/test fixture; no rig). Budget target: < 0.5 ms CPU, < 1 ms GPU.
7. **Effort and plan.** Phased slices (S/M/L) a delegate can build, each with acceptance criteria and tests, the flag
   name(s), and which lane suits each (some need real-window visual judgement). Name the risks.

## Spike (optional but recommended)
Prove the hardest unknowns in a throwaway branch: a primitive cockpit frame (boxes/quads) parented to the camera in
cockpit view, depth handled so it never clips into the scene, and ONE instrument rendered from `HudModel` into a render
target and shown on a cockpit quad. Real-window screenshots (Classic HUD off, cockpit on) at 1920×1080 and 800×480 into
`docs/reports/COCKPIT_3D_P1044_assets/` (PNG, ≤ 2 MB total, read every image and describe what is actually visible).
Then revert the code (keep `spike.patch`).

## Report
`docs/reports/COCKPIT_3D_P1044.md`: current state with file:line, native findings, the options table and
recommendation, parity/fairness mapping, assets, rendering, other views, measured performance, the phased plan, risks.
Footer sections: **"Native intent"** and **"Modern approach"**. No gate beyond: the tree is clean except the report and
its assets, and `~/.dotnet/dotnet build AllegianceCore.slnx` still passes 0/0 after you revert the spike.
