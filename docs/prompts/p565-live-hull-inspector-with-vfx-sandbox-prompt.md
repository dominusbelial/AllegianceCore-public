# P565 — live hull inspector: an offline client mode to review every hull one by one with a mouse-controlled orbit/zoom/pan camera, screenshots, lighting/cloak toggles, and a VFX sandbox (smoke, trails, hits, explosions, mines) spawned around the hull, plus one-key "mark bad" notes

## Why this task exists

**Domingo (2026-10-06):** after live testing, ships (and stations) have
readability problems and **some hulls have transparent/missing parts**
("depends on the ship"). P564 fixes the lighting, stations and known mesh
issues and produces per-hull contact sheets, but: "we will have to go hull by
hull … the live hull inspector would be good, and maybe a mouse controlled
camera to check different points of view so we can take screenshots and zoom
in to details? **some of the issues are only present when combined with other
vfx like smoke and particles**".

**Reuse (don't rebuild):**
- Hull model/texture resolution: `ResolveShipModel`/`ResolveShipTexture`/
  `ResolveShipAsset` (`Game1.Rendering.cs:~2342-2380`, `HullModelResolver`), the
  in-flight `ShipRenderer`, hull scale (`ShipRenderScale`).
- The hangar preview orbit (`Rendering/DockedHullOrbit.cs`,
  `DockedHullPreviewFraming.cs`, P525/P526, native `load.cpp` spin).
- Cameras: `Rendering/CameraMode.cs`, `CameraCycler.cs`, `ChaseCamera.cs`.
- Screenshots: F11 `Capture.ScreenShot`, real backbuffer (P417; P559
  `ALLEGIANCE_AUTOSHOT`).
- Sector lighting: `SectorLighting`/`SectorLightingEffect` (P530/P564); VFX
  renderers and particle pools (P545/P546/P547: engine/missile trails, damage
  smoke `DamageDebrisTracker`, hull hits, shield flares, explosions
  `ExplosionPreset`, mines `MineRenderer`); the `--fx`, `--soft`, `--bloom`
  switches.

## Scope (client only; works OFFLINE, no server)

1. **`--inspect` launch mode:** loads the static core and art, opens an empty
   sector scene (choose a sector's lighting: the brightest, the darkest, the
   221 Alderan; and a starfield/backdrop), and shows one hull at a time.
   Optional `--inspect-civ <id|name>` and `--inspect-hull <id|name>`.
2. **Mouse camera:**
   - left-drag orbits around the hull;
   - right-drag or middle-drag pans;
   - wheel zooms (from far to very close, **near clip low enough for close-ups**);
   - double-click focuses a point;
   - a key resets to native preview framing;
   - optional slow auto-orbit (native preview spin);
   - **fly-by camera** (Domingo: "also include a fly by camera option"): the
     camera sweeps past the hull along a path, the classic cinematic pass. It
     approaches from the distance, passes close beside/over/under the hull, then
     recedes, looking at the hull throughout (or at the path ahead, toggle). Options:
     speed, closest-pass distance, side (left/right/over/under), random path,
     loop; optionally the **hull itself moves** (flying at its native speed with
     its engine trail) while the camera holds a fixed point it flies past, the
     "ship passes the camera" shot. Works with every VFX sandbox combination
     and with F11 screenshots; a key starts/stops it and another re-runs it.
3. **Hull navigation:** next/previous hull (e.g. PgUp/PgDn), next/previous civ;
   **all player-flyable hulls** of the core (and drone hulls behind a toggle).
   An on-screen label: civ, hull id/name, model asset, native length, model
   radius/scale, texture, and whether the fallback model was used.
4. **Look toggles:** **`native | meshy` ship models (P566)**, side by side or toggled; cloak/translucent on/off (and a cloak-fraction slider);
   `Native | Enhanced` VFX; soft particles on/off; bloom on/off; lighting variants
   (pre-P530 stock rig, P530, P564 final); sector light presets; wireframe and
   normals debug views; show back faces; a side tint.
5. **VFX sandbox around the hull** (the real renderers, real assets, not
   mock-ups):
   - engine trail + nozzle glow at a chosen throttle;
   - **damage smoke** at a chosen hull fraction (e.g. 75/50/25%);
   - shield hits at random points;
   - hull hits (surface flash + `utl10` bits);
   - missiles launched from/past the hull (trail smoke);
   - an explosion nearby and on the hull (fighter/station presets);
   - a minefield around the hull;
   - **projectiles of any weapon** fired past/at the hull: pick any
     projectile type from the core (P544 visuals: billboards incl. the nanite
     "donuts", directional Pulse Laser, model projectiles like the laser cannon
     / MassDriver), at its native speed/lifespan/rate; single shot or stream;
   - **missiles of any missile type** (model + trail + hit/explosion), fired
     from the hull, at the hull, or crossing in front of the camera;
   - **mines of any mine type** (dogfight packs and layer fields);
   - pause/step time, and a slow-motion slider.
   Each on its own key and **freely combinable** (Domingo: "it should allow us to
   see projectiles, missiles, all the combinations"), e.g. a damaged smoking
   hull under a gun stream with missiles inbound and an explosion behind it.
   Add a **"chaos" preset** that turns everything on at once, and a small
   on-screen panel listing the active effects and their parameters (type,
   rate, distance), editable with keys or mouse.
   **Weapon/missile pickers enumerate the real core** (all `ProjectileTypes`,
   missile types, mine types), not a hard-coded list.
6. **Capture and notes:** F11 screenshots (real backbuffer, timestamped, with
   hull id in the name); **"mark bad" key** → prompt for a short note (or pick a
   tag: holes / transparent / flat / scale / texture / vfx-overlap) → append a
   line to `inspector-notes.jsonl` (hull, civ, camera pose, toggles, active VFX,
   screenshot file). A summary export Claude can turn into a fix list.
7. A short `docs/HULL_INSPECTOR.md`: keys, flags, workflow.

8. **In-flight cinematic camera (live play, for B-roll):** Domingo liked using the
   fly-by for footage of long autopilot trips ("traveling to far sectors is really
   easy"). Reuse the inspector's camera code as an extra **camera mode during
   normal play** (alongside cockpit/chase, e.g. via the camera-cycle key or its own
   key):
   - **orbit** the own ship (mouse orbit/zoom while the autopilot or the player
     flies; no control input lost);
   - **fly-by** passes the own ship repeatedly as it travels (camera placed ahead,
     ship passes, re-place), surviving **warp transits** (re-anchor in the new
     sector after the aleph flash);
   - an optional **trailing "drone cam"** with smooth lag;
   - **hide-HUD toggle** for clean footage (one key; HUD returns on any flight
     input or the same key);
   - cockpit/chase/flight controls unaffected; F11 screenshots work; nothing
     sent on the wire.
   Proof: a real-window frame sequence of an autopilot trip Ganymede → Rigel →
   Alderan in fly-by mode with the HUD hidden, including the aleph transits.

## Explicitly out of scope

- Fixing the hull/mesh/lighting issues themselves (P564 and follow-ups); this
  is the review tool.
- Any wire/server change (the cinematic camera is client-side only).

## Proof

- Unit tests for the pure parts (camera orbit/zoom/pan math, hull list
  enumeration incl. the fallback flag, notes serialisation).
- **Real client run** (window 2560×1440): screenshots of an Adv Fighter close-up
  (zoomed on a detail), the same hull cloaked, the hull with damage smoke +
  engine trail + a nearby explosion (Enhanced, soft ON), a gun stream of three
  different projectile types + a missile volley at the hull, the "chaos" preset, a fly-by frame sequence (approach / closest pass / recede), and the hull list
  stepping through at least 10 hulls across both civs. Plus a sample
  `inspector-notes.jsonl`.
- Report `docs/reports/P565_HULL_INSPECTOR.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

- **Visual proof rule:** screenshots must come from the **real client window** (backbuffer,
  e.g. `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture`/offscreen renders alone don't
  count; that gap hid the P547 (invisible geometry) and P562 (near-invisible aleph) regressions.

- **Real-window proof tooling:** use P572's `scripts/live-proof.sh` and the client
  `--auto-side/--auto-ready/--auto-launch/--auto-goto/--autoshots` flags
  (`docs/LIVE_PROOF.md`) to take unattended real-window screenshots (with MCP bots
  where needed). "Couldn't automate the window" is no longer an acceptable gap.

## ➕ Per-hull art choice: keep the good Meshy ships (Domingo, 2026-10-07)

"Some of the meshy ships do actually look good, we may want to save a few of
those and move away from the most odd ones eventually."

- Add a committed per-hull override file (e.g. `assets/ships/ship-art.json`:
  `{ "<hull id or model name>": "meshy" | "native" }`). The default for unlisted
  hulls is **native** (P566). The global `--ships native|meshy` flag still forces
  one source for comparisons.
- The inspector's native/meshy toggle gets a **"keep this choice for this hull"**
  action that writes the override file, plus a **side-by-side view** (native
  left, Meshy right, same camera/lighting) to judge.
- The game (flight, hangar preview, remote ships) resolves each hull through
  the override. If a chosen Meshy asset is missing, fall back to native, logged
  once.
- **Don't delete any Meshy assets.** Curation happens later through this file.
- Proof: a contact sheet of every hull that has both, native vs Meshy, saved in
  the report assets as Domingo's review sheet; tests for the resolution order
  (flag > override > native default) and the missing-asset fallback.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
