# P353 — Wire the 16 Meshy-regenerated Iron Coalition (IC) ship models into the MonoGame port

Pure content replacement: swap in the 16 freshly-regenerated Iron Coalition ship
meshes + textures behind the existing P287 asset names. No code / registration
change — the names and their wiring in `HullModelResolver.ShipAssets` /
`Content.mgcb` already exist (P287/P288); only the file bytes behind each asset
changed. Iron Coalition is the third faction in the P351 (Bios) / P352 (GT)
hull-regeneration arc — this is IC's full 16-class P287 roster.

## Source → asset mapping (all 16 of IC's real P287 roster classes)

Taken from a live `vizfight roster 0 ic` `RosterPlanner.Plan` run (this task's
actual server, roster_dump.json written on boot), not memory. The 9 reused
hulls come from `gt_regen_glbs/` (shared native geometry with another faction —
Bios constructor/ejectpod, GT carrier/cruiser/dev, etc., by the original game's
own design); the 7 IC-specific from `gt_ic_missing_glbs/`.

| Source GLB | Dir | Asset (replaced .fbx/.png) | Native hull | Length |
|---|---|---|---|---|
| wc_icbmb.glb   | gt_ic_missing_glbs | ic-bomber           | Bomber               | 25.92 |
| cap502.glb     | gt_regen_glbs      | ic-carrier          | Attack Carrier [SR]  | 300   |
| utl14a.glb     | gt_regen_glbs      | ic-constructor      | Lg Adv Constructor   | 85    |
| utl18a.glb     | gt_regen_glbs      | ic-corvette         | Corvette             | 75    |
| cap01.glb      | gt_regen_glbs      | ic-cruiser          | Lt Cruiser           | 250   |
| cap100.glb     | gt_regen_glbs      | ic-dev              | Devastator           | 225   |
| utl17a.glb     | gt_regen_glbs      | ic-devastator       | Devastator           | 225   |
| utl100.glb     | gt_regen_glbs      | ic-ejectpod         | Lifepod              | 4     |
| wc_icfig.glb   | gt_ic_missing_glbs | ic-fighter          | Enh Fighter          | 14.94 |
| cap02a.glb     | gt_ic_missing_glbs | ic-frigate          | Frigate              | 200   |
| wc_icint.glb   | gt_ic_missing_glbs | ic-int              | Interceptor          | 14.84 |
| wc_ichvy.glb   | gt_ic_missing_glbs | ic-interceptor      | Hvy Interceptor      | 14.84 |
| utl91.glb      | gt_regen_glbs      | ic-miner            | Adv Miner            | 19.95 |
| fig05ops1.glb  | gt_ic_missing_glbs | ic-scout            | Lxy Scout            | 11.4  |
| wc_icsf.glb    | gt_ic_missing_glbs | ic-stealthfighter   | Stealth Bomber [NG]  | 18.46 |
| utl93b.glb     | gt_regen_glbs      | ic-trooptransport   | Hvy Troop Transport  | 22.73 |

(Length values decoded live from PCore001.igc for the scale check below.)

Note `ic-dev` (cap100) and `ic-devastator` (utl17a) are TWO distinct P287 asset
slots that both natively display "Devastator" — distinct hull IDs (321 vs 222)
and distinct GLBs, wired separately exactly as the table shows. Confirmed two
visually distinct ships in-game (see Verification).

## Conversion

1. **Staging.** All 16 GLBs symlinked into `p353stage/` renamed to their target
   asset names (`wc_icbmb.glb` → `ic-bomber.glb`, etc.) — the 
   `extract_meshy_textures.py` lookup and the FBX output naming both key off
   the asset name, and the same source GLB (e.g. cap502) must yield
   `ic-carrier.fbx` here alongside the `gt-carrier.fbx` P352 already produced.
2. **GLB → FBX**: `glb_to_fbx.py` (headless Blender 5.2.2) per asset — materials
   stripped (geometry + UVs + normals only), triangulated, decimated to the 20k
   cap, FBX binary exported. All 16 converted cleanly, no FBX export warnings,
   no ERROR/Traceback in the log: every one exceeded ~200k source tris and hit
   the 20k cap (14 at exactly 20000; 2 at 19999 — ejectpod, miner, from
   proportional decimation). Final tris 19642–19991, single coherent mesh.
3. **Texture extraction**: `extract_meshy_textures.py p353stage <Content/Textures/ships>`
   — all 16 `ic-*` lines printed `OK` (1024×1024 PNGs, 1.15–1.99 MB each). The
   other 60 MISS lines (non-IC factions) are the expected harmless noise; the
   script's nonzero exit is from those, not anything IC-related.
4. Files replaced in place at `Content/Models/ic-<class>.fbx` and
   `Content/Textures/ships/ic-<class>.png`. `git status`: exactly 32 modified
   files (16 fbx + 16 png), all `ic-*`, nothing else touched.

## Build

- MonoGame content pipeline rebuilt the 16 new `.fbx` + 16 `.png` into `.xnb`
  (Debug output, fresh 23:10 timestamps on all 32): **0 warnings / 0 errors**.
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.

## Tests

- See run below (Core suite; expected only pre-existing documented live-wire
  timing flakes whose names vary run to run). MonoGame content suite also run.

## Live proof (real server + real render path)

Booted `vizfight roster 7633 ic` (real 9-faction server; the `ic` civ filter
spawns exactly IC's 16 real bots on side 2 via the real `SendBuyLoadout` path;
transport bound `127.0.0.1:38487`). The live `RosterPlanner.Plan` resolved all
16 to the exact hulls in the table (roster_dump.json: 16 rows, all civ=ic,
sideId=2; ic-dev hull 321 vs ic-devastator hull 222 distinct).

- **`roster-capture dump`** (real HullModelResolver + ShipRenderer + compiled
  content) → `roster_ic.jpg`: 16/16 distinct JPEG sizes, 16/16 distinct tri
  counts (19642–19991) — every IC ship is distinct geometry.
- **`roster-capture wire --port 38487 --side 2`** — the definitive networked
  proof: a real `ConnectionSession` joined the live server over the wire,
  seated on side 2 (IC), and **received all 16 distinct IC ships**, each
  mapped to its correct hull id (ic-carrier→78, …, ic-dev→321,
  ic-devastator→222), rendered as `roster_net_ic_wire.jpg` (1400×958, "WIRE
  RECEIVED (side 2) 16 ships"). 16/16 distinct, textured models, no duplicate,
  no placeholder, no gray fallback.
- **`ic-dev` vs `ic-devastator`** (the two both named "Devastator"): visually
  distinct in both the approved Meshy thumbnails and the in-game renders.
  `ic-dev` (cap100) is a compact angular V-wing fighter with teal cockpit +
  red wing marking (matches cap100_thumb); `ic-devastator` (utl17a) is an
  elongated low-profile hull with a large faceted fan/turbine disc at the rear
  (matches utl17a_thumb). Confirmed not conflated.
- **Scale (ShipRenderScale auto-normalizes to 0.5 × HullType.Length)** —
  confirmed OBJECTIVELY against the new compiled geometry: the raw rendered
  radius of each new model (measured by `ShipRenderScale.RenderRadius` through
  roster-capture) mapped by the runtime `ScaleForLength` yields exactly
  0.5 × Length for all 16:
  - **Carrier** (cap502, Length 300) raw 95.63 → rendered radius **150**
    (0.5×300) — the largest, a capital ship.
  - **Lifepod** (utl100, Length 4) raw 118.6 → rendered radius **2** (0.5×4) —
    the smallest.
  - Scout (Length 11.4) → radius 5.7; carrier/lifepod visual-radius ratio
    **75×**. Proportional, matching gameplay collision. No per-model tuning
    needed (measured live, not assumed).
- **The "dark" ships**: `ic-trooptransport` and `ic-dev` render dark in the
  sheets. This is the real IC livery, not a missing texture: their extracted
  baseColor PNGs are non-blank with the same brightness profile as the
  clearly-rendering ships (mean luminance 43.8 / 41.4, non-black pixels
  87% / 99.8%, max 255) — a missing texture renders white/unlit, not black.
  IC is canonically the near-black/navy faction; the in-game dark read matches
  the approved Meshy thumbnails (utl93b_thumb, cap100_thumb).

## Out of scope (untouched)

- Any other faction's ships (GT's 16 are P352's, already on master; `gt-*`
  assets untouched — git status shows only `ic-*` changed).
- P350's civ-hull mapping (landed, correct); `HullModelResolver` /
  `RosterPlanner` / `Content.mgcb` structure unchanged — pure file-content swap.
- Native reference tree `~/Development/Allegiance` (read-only).

## Files committed

`src/Allegiance.ClientCore.MonoGame/Content/Models/ic-*.fbx` (16) and
`src/Allegiance.ClientCore.MonoGame/Content/Textures/ships/ic-*.png` (16).
