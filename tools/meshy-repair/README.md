# P581 — Meshy ship repair pipeline (Adv Scout pilot pass)

Repair floating/detached pieces in the imported Meshy ship models (Domingo,
2026-10-07: the Adv Scout's protruding "wings" are separated from the hull, so
the gap renders as black space). This pass builds and runs the pipeline on
**`dreg-scout2` only** (P581 pilot); the other hulls and the replacement
catalogue are **P581b**.

## Files

| File | Purpose |
|------|---------|
| `common.py`    | Shared Blender-headless analysis: FBX import, welded connected-component decomposition, hull-BVH min-gap. Exact method from the P581 brief: `bmesh.ops.remove_doubles(dist=0.0005)` → components → largest = hull → BVH → `find_nearest`. |
| `analyze.py`   | Audit one/many FBX → JSON (welded counts, each significant piece's size + gap to hull, crumbs, non-manifold edges, flipped-normals estimate). |
| `repair.py`    | Automatic repair: rigid-translate each significant detached piece along the shortest-gap direction until it overlaps the hull by a small margin; delete floating crumbs; recalc normals; export to `Content/Models/repaired/<model>.fbx`. **Originals never overwritten.** Iterates until no significant detached piece remains (translation can tear a sub-fragment at a piece/piece junction). |
| `render.py`    | Colored per-piece Solid/Workbench 4-view renders (hull grey, pieces highlighted) for before/after review. |
| `run.sh`       | Convenience wrapper: `./run.sh analyze|repair|render [model]`. |

## Why welded components

Meshy splits vertices per UV island, so the raw mesh has 18,041 verts / 20,000
faces that weld to 13,374 / 19,994. Connected-component analysis is done on the
**welded** mesh (UV seams kept; welding is analysis-only), and the repair maps
welded-vertex coordinates back onto the original split mesh so **UVs/texture
are untouched**.

## Adv Scout (`dreg-scout2`) result (P581 pilot)

| Quantity | Before | After |
|----------|--------|-------|
| welded verts / faces | 13,374 / 19,994 | 13,638 / 19,991 |
| welded components | 142 | 113 |
| significant detached pieces (≥20 verts) | 4 (+1 touching 21-vert) | 0 detached (all ≤0.0011 gap, i.e. touching/merged) |
| wing gaps | 0.061, 0.061 | overlap (−0.010 margin) |
| blade gaps | 0.148, 0.148 | overlap (−0.010 margin) |
| floating crumbs dropped | — | 36 |
| UV seams | preserved | preserved |

Evidence: `docs/reports/P581_assets/audit/{before,after,repair_report}.json`,
colored before/after renders in `docs/reports/P581_assets/renders/`, and
real-window 6-angle screenshots in `docs/reports/P581_assets/proof/{raw,repaired}/`.

## Re-running

```bash
cd <repo root>
tools/meshy-repair/run.sh repair dreg-scout2   # analyze + repair + re-audit
tools/meshy-repair/run.sh render dreg-scout2    # colored 4-view renders
```

Blender headless (`blender --background --factory-startup --python …`) — no GUI
needed. Outputs land under `docs/reports/P581_assets/`.
