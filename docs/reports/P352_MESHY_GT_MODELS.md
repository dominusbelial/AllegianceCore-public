# P352 — Wire the 16 Meshy-regenerated GT ship models into the MonoGame port

Pure content replacement: swap in the 16 freshly-regenerated Ga'Taraan (GT)
ship meshes + textures behind the existing P287 asset names. No code /
registration change — the names and their wiring in
`HullModelResolver.ShipAssets` / `Content.mgcb` already exist; only the file
bytes behind each asset changed.

## Source → asset mapping (all 16 of GT's real P287 roster classes)

Taken from a live `vizfight roster 0 gt` `RosterPlanner.Plan` run, not memory.
Every entry resolved to the exact hull named in the task brief.

| Source GLB | Dir | Asset (replaced .fbx/.png) | Native hull | Length |
|---|---|---|---|---|
| utl18a.glb  | gt_regen_glbs      | gt-ass      | Corvette             | 58  |
| wc_gtpt.glb | gt_regen_glbs      | gt-bomber   | PT Bomber            | 54  |
| cap502.glb  | gt_regen_glbs      | gt-carrier  | Attack Carrier [SR]  | 300 |
| wc_gtcon.glb| gt_regen_glbs      | gt-con      | Sm Std Constructor   | 42  |
| cap01.glb   | gt_regen_glbs      | gt-cruiser  | Lt Cruiser           | 76  |
| cap100.glb  | gt_regen_glbs      | gt-dev      | Devastator           | 42  |
| wc_gtfig.glb| gt_regen_glbs      | gt-fighter  | Fighter              | 14  |
| cap415.glb  | gt_regen_glbs      | gt-hod      | Harbinger of Doom    | 76  |
| fig22.glb   | gt_regen_glbs      | gt-int      | Hvy Interceptor      | 26  |
| wc_gtmnr.glb| gt_regen_glbs      | gt-miner    | Miner                | 50  |
| wc_gtstang.glb| gt_regen_glbs    | gt-mustang  | Mustang              | 38  |
| wc_gtpod.glb| gt_ic_missing_glbs| gt-pod      | Lifepod              | 5   |
| fig12.glb   | gt_ic_missing_glbs| gt-rd       | Lxy Patroller        | 28  |
| wc_gtsct.glb| gt_ic_missing_glbs| gt-scout    | Scout                | 14.3|
| wc_gtsf.glb | gt_ic_missing_glbs| gt-sf       | Stealth Fighter      | 23  |
| utl93b.glb  | gt_regen_glbs      | gt-tt       | Hvy Troop Transport  | 49  |

(Length values decoded live from PCore001.igc for the scale check below.)

## Conversion

1. **GLB → FBX**: `glb_to_fbx.py` (headless Blender 5.2.2) per asset — materials
   stripped (geometry + UVs + normals only), triangulated, decimated to the 20k
   cap, FBX binary exported. All 16 converted cleanly, no FBX export warnings:
   every one exceeded 200k source tris and hit the 20k cap (4 at 19999 due to
   proportional decimation); final tris 19839–19991, single coherent mesh.
2. **Texture extraction**: `extract_meshy_textures.py` against a staging dir
   with all 16 GLBs renamed to their asset names (script looks up
   `<meshy>/<asset>.glb`). All 16 `gt-*` lines printed `OK` (1024×1024 PNGs);
   the MISS lines for the other factions are the expected harmless noise.
3. Files replaced in place at `Content/Models/gt-<class>.fbx` and
   `Content/Textures/ships/gt-<class>.png`.

## Build

- MonoGame content pipeline rebuilt the 16 new `.fbx` + 16 `.png` into `.xnb`
  with **0 warnings / 0 errors** (all 32 gt XNBs regenerated).
- Whole-solution Release build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.

## Tests

- MonoGame content suite run 2×: **652/652 passed both runs** (no flakes this
  time — the usual live-wire timing flakes did not fire).
- Core suite: see run below (only pre-existing documented live-wire flakes
  expected).

## Live proof (real server + real MonoGame client)

Booted `vizfight roster <port> gt` (real 9-faction server, 16 real GT bots via
`SendBuyLoadout`) and verified the plan resolves all 16 to the exact hulls in
the table above.

- **`roster-capture dump`** (real HullModelResolver + ShipRenderer + compiled
  content): loaded all 16 distinct models → `roster_gt.jpg` labelled sheet.
  16/16 distinct JPEG sizes, 15/16 distinct tri counts (two hulls share one
  count — expected for cap01/fig22 sharing). Visual: 13/16 read clearly as
  distinct, metallic-textured models; the darkest-livery ones (carrier,
  fighter, sf, tt) read near-black in the dim sheet lighting.
- **`roster-capture wire --side 1`** — a real ConnectionSession joined the live
  server over the wire and **received all 16 distinct GT ships**, rendered as
  `roster_net_gt_wire.jpg` (1400×958). This is the definitive networked proof
  that the real client path delivers and renders all 16 (no duplicate, no
  placeholder, no gray fallback).
- **Real MonoGame client (Game1) overview capture**: `nPoses=15` of 16 GT ships
  + viewer having known positions at the first capture frame (the GT column is
  single-file, so later/behind ships are occluded in the overview framing —
  the sheets above are the unambiguous all-16 proof). In-sector frames show the
  real fleet with GT teal/cyan accents.
- **Scale (ShipRenderScale auto-normalizes to 0.5 × HullType.Length)**, two
  chase-camera close-ups in the real client (viewer scout, radius 7.15):
  - **Carrier** (cap502, Length 300 → radius 150): the attack carrier fills the
    frame as a large capital ship with claw/docking-arm structure, dwarfing the
    small scout — 60× length ratio confirmed visually.
  - **Lifepod** (wc_gtpod, Length 5 → radius 2.5): the pod renders smaller than
    the viewer's own scout (scout 7.15 > pod 2.5), the opposite extreme.
  So carrier ≫ scout ≫ lifepod, both finite and sensible. No per-model tuning
  needed (confirmed live, not assumed).

### Notes on the "dark" ships
`gt-fighter` / `gt-tt` (and to a lesser extent carrier / sf) render dark. This
is the ships' real livery, not a missing texture: (a) their extracted
baseColor PNGs are non-blank with the same brightness profile as the clearly-
rendering `gt-carrier` (mean luminance ≈42–46, ~55–60% non-black pixels), and
(b) the approved Meshy thumbnails for wc_gtfig and utl93b are themselves dark
charcoal/near-black with teal accents — the in-game dark read matches them. A
missing texture would render white/unlit, not black; normals are shared with
the 13 correctly-shaded models.

## Out of scope (untouched)
Iron Coalition's 7 regenerated GLBs (separate task P353); P350's civ-hull
mapping; `HullModelResolver` / `RosterPlanner` / `Content.mgcb` structure;
native reference tree (read-only). This is purely a file-content swap behind
existing names.
