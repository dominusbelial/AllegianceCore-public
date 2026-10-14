# P586 — loot/treasure looks like native: each part drops as its OWN spinning model (ammo, fuel, missiles, shields…), cash/power-ups as `acs14`, artifacts and flags native, instead of one shared gold-gem placeholder

## Why this task exists

Domingo (2026-10-08): "we need to work on the loot; in the original game the
prize/loot was a large icon rotating that would let you know if it was rocket
fuel, ammo or something else."

**Native** (`Igc/treasureIGC.cpp:116-190`, `CtreasureIGC::Initialize`):
- **Part / development treasure** (`m_buyable`): `name = m_buyable->GetModelName()`,
  `icon = m_buyable->GetIconName()` (`:123-126`), so the pickup is drawn with
  **that part's own model** (ammo pack, fuel pack, missile pack, shield, gun…).
- **Cash / power-up** (`c_tcCash`/`c_tcPowerup`): model **`acs14`**, icon
  `cashicon` / `iconpu` (`:116, 151-155`).
- **Flag**: decal texture `biosflag`, icon `flag`, side-coloured, signature
  1.5 (`:128-139, 157-170`). **Artifact** (flag on `SIDE_TEAMLOBBY`): model
  `artifact`, icon `articon` (`:140-145`).
- Every model treasure: **random orientation** and a **random rotation**
  (axis = random direction, angle rate `random(-6, 6)` rad/s) (`:179-190`), so
  it visibly tumbles/spins. `SetRadius(5.0f)` (`:192`), drift-to-stop
  deceleration (`:~100-115`), lifespan expiry.
- The icon is what the radar/HUD shows (`WinTrek/radarimage.cpp:636, 716, 877`
  handle `OT_treasure`); check how native sizes the model on screen (5 u
  radius vs the model's own radius: native `Load(...)` scales to the thing's
  radius) so pickups are readable from a distance.

**Port today:** `Rendering/TreasureMeshFactory.cs` + `TreasureRenderer.cs`
draw **one shared gold gem** for every treasure. Its own doc
(`TreasureMeshFactory.cs:38-53`) says native's art "is not present in this
checkout's artwork tree". **That's no longer true:** `~/Development/Artwork`
has `acs14.mdl`/`acs14bmp.mdl`, `artifact.mdl`, `biosflagbmp.mdl`,
`cashiconbmp.mdl`, `iconpubmp.mdl`, `articonbmp.mdl`, and every part's model
(P566's native MDL decoder/`NativeShipRenderer` path already loads native
meshes; P567 did the same for asteroids). The wire `DataTreasureIGC`
(`treasureCode` + `treasureID`) is enough to resolve the buyable from the core
(part types / developments → `ModelName`, `IconName`).

## Scope

1. Resolve each treasure's native visual from `treasureCode`/`treasureID`
   via the static core: the part/development's `ModelName`/`IconName`, `acs14`
   for cash/power-up, `artifact` for the artifact, the `biosflag` decal
   (side-tinted) for flags. Log once per unresolved case, falling back to the
   gem.
2. Render with the native mesh path (decoded `.mdl` + texture, native winding
   handling from P566/P567/P570, sector-lit with lighting ON (P564)), scaled
   per native rules, with the **native random orientation + spin**
   (deterministic per treasure id so all clients agree visually).
3. Radar/HUD: show the native icon (`GetIconName`, `cashicon`, `iconpu`,
   `flag`, `articon`) for treasures on the radar/target display where native
   does. Use P584's sprite-orientation decode for 2D icons.
4. Pickup identity in the target box/tooltip (name: e.g. "Fuel Pack", "Ammo
   Pack", "Flag", "Artifact"), as native `SetName` does.
5. Keep native drift/lifespan/pickup rules (gameplay unchanged).

6. **Too many loot pickups left after combat.** Domingo: "at the end of
   combat it ends with hundreds of tiny floating cubes representing the
   loot." Besides the look (the gold-gem placeholder reads as small cubes),
   check **count and lifetime against native**: what native drops on a ship
   death (which parts, with what chance: find `CreateTreasure`/the
   kill-ship drop path in `FedSrv` + `Igc`), the per-treasure `lifespan`
   (`m_expire = time0 + lifespan`, `treasureIGC.cpp:~195`) and where native
   removes expired/picked treasures, any cap per sector, and whether bots'
   (MCP) loadouts inflate drops. Measure the port in a 15v15 showcase:
   treasures created, alive over time, expired, picked up, vs native's
   rules. Fix any divergence (server side if the drop/expiry logic differs,
   client side if the client fails to remove expired/picked treasures,
   e.g. a missing TREASURE destroy message). Report the before/after counts.

## Proof (real window, P572 tooling + MCP)

- A contact sheet of every treasure kind's model from the static core (all
  part types + acs14 + artifact + flag), via the P565 inspector or a tool.
- Live: kill a bot carrying parts (or spawn treasures via a debug/MCP path)
  → real-window screenshots of an ammo pack, fuel pack, missile pack, a gun
  part, cash and a flag spinning in space at 50/200/600 u, plus the radar
  icons; a short frame strip showing the spin.
- Tests: treasure → visual resolution per code, deterministic spin seed,
  fallback.
- Report `docs/reports/P586_NATIVE_TREASURE_VISUALS.md`.

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
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
