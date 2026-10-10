"""Shared Blender-headless helpers for the Meshy repair pipeline (P581).

Runs inside `blender --background --python`. Provides FBX import + welded
connected-component analysis (the exact method from the P581 brief:
bmesh.ops.remove_doubles -> connected components -> largest = hull -> BVH of
hull faces -> find_nearest min gap per component).
"""
from __future__ import annotations
import bpy, bmesh, math
from mathutils.bvhtree import BVHTree
import os, sys, json, re

WELD_DIST = 0.0005          # brief: remove_doubles(dist=0.0005)
PIECE_MIN_VERTS = 20        # significant piece threshold (brief: >= 20 verts)


def import_fbx(fp: str):
    """Import the FBX as the sole mesh object (dedupe any prior meshes). Return
    the single mesh object."""
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(confirm=False)
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)
    bpy.ops.import_scene.fbx(filepath=fp)
    meshes = [o for o in bpy.data.objects if o.type == 'MESH']
    assert len(meshes) == 1, f"expected 1 mesh object, got {len(meshes)}"
    return meshes[0]


def welded_components(obj):
    """Return (bm, components) where components is a list of lists of welded
    vertex indices forming connected components (largest first) after
    remove_doubles(WELD_DIST). Coordinates rounded so callers can map welded
    verts back to the original split mesh."""
    mesh = obj.data
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=WELD_DIST)
    bm.verts.ensure_lookup_table()
    bm.edges.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    # connected components via union-find over edges
    vidx = list(range(len(bm.verts)))
    parent = list(range(len(bm.verts)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[rb] = ra

    for e in bm.edges:
        union(e.verts[0].index, e.verts[1].index)
    comps: dict[int, list[int]] = {}
    for v in bm.verts:
        comps.setdefault(find(v.index), []).append(v.index)
    comp_list = sorted(comps.values(), key=len, reverse=True)
    return bm, comp_list


def component_stats(obj, bm, comp):
    """size (extent) + min gap to hull for a welded component."""
    hull_sel = [v.index for v in bm.verts]
    # hull = largest component; recompute faces of hull
    # build BVH from hull faces
    return None


def hull_bvh(bm, hull_comp_set: set[int]):
    """BVHTree over the faces whose verts are all in the hull component
    (Blender >=4.1 API: BVHTree.FromPolygons(verts, polygons))."""
    coords = []
    poly = []
    idx = {}
    def vi(co):
        key = (round(co[0], 6), round(co[1], 6), round(co[2], 6))
        if key not in idx:
            idx[key] = len(coords)
            coords.append(co.copy())
        return idx[key]
    for f in bm.faces:
        if all(v.index in hull_comp_set for v in f.verts):
            fv = [vi(v.co) for v in f.verts]
            for i in range(1, len(fv) - 1):
                poly.append((fv[0], fv[i], fv[i + 1]))
    tree = BVHTree.FromPolygons(coords, poly)
    return tree, poly


def analyze_model(fp: str) -> dict:
    """Full analysis of one FBX. Returns a dict with welded vert/face counts,
    hull/component breakdown, per-significant-piece size+gaps, crumb count,
    non-manifold edges, and gap of every piece to hull."""
    obj = import_fbx(fp)
    bm, comps = welded_components(obj)

    total_welded_verts = len(bm.verts)
    total_welded_faces = len(bm.faces)

    hull = set(comps[0])
    hull_tree, _ = hull_bvh(bm, hull)

    pieces = []
    crumbs = 0
    gaps = []
    for comp in comps[1:]:
        verts = [bm.verts[i] for i in comp]
        # min gap: nearest distance from each vert to hull BVH
        best = None
        best_pair = None
        for v in verts:
            loc, normal, idx, dist = hull_tree.find_nearest(v.co)
            if loc is not None:
                d = (loc - v.co).length
                if best is None or d < best:
                    best = d
                    best_pair = (tuple(loc), tuple(v.co), tuple(normal))
        # extent = bounding box diagonal of the component
        xs = [v.co[0] for v in verts]; ys = [v.co[1] for v in verts]; zs = [v.co[2] for v in verts]
        extent = math.sqrt((max(xs)-min(xs))**2 + (max(ys)-min(ys))**2 + (max(zs)-min(zs))**2)
        n = len(comp)
        rec = {
            "nverts": n, "extent": extent,
            "gap": best, "nearest_hull_point": best_pair[0] if best_pair else None,
            "vert": best_pair[1] if best_pair else None,
            "hull_normal": best_pair[2] if best_pair else None,
            "bbox": [[min(xs), min(ys), min(zs)], [max(xs), max(ys), max(zs)]],
        }
        gaps.append(best)
        if n >= PIECE_MIN_VERTS:
            pieces.append(rec)
        else:
            crumbs += 1

    # non-manifold edges (count on welded bm)
    nonmanifold = sum(1 for e in bm.edges if not e.is_manifold)

    # flipped normals estimate: share of faces whose winding flips vs neighbors
    flipped = estimate_flipped(bm)

    ship_len = ship_length(bm)

    return {
        "source_fbx": os.path.basename(fp),
        "raw_verts": len(obj.data.vertices), "raw_faces": len(obj.data.polygons),
        "welded_verts": total_welded_verts, "welded_faces": total_welded_faces,
        "components_after_weld": len(comps),
        "hull_nverts": len(comps[0]),
        "pieces_ge20": pieces, "piece_count_ge20": len(pieces),
        "crumb_count": crumbs,
        "gap_min_of_all_pieces": min(gaps) if gaps else None,
        "nonmanifold_edges": nonmanifold,
        "flipped_normals": flipped,
        "ship_length": ship_len,
    }


def ship_length(bm):
    xs=[v.co[0] for v in bm.verts]; ys=[v.co[1] for v in bm.verts]; zs=[v.co[2] for v in bm.verts]
    return max(max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs))


def estimate_flipped(bm):
    """Crude flipped-normal indicator: count faces whose area-weighted normal
    disagrees with the majority lobe of their connected neighbour normals.
    Cheap proxy: fraction of faces with negative dot to the mesh's overall
    average normal direction region. We instead count zero-area/degenerate and
    report a heuristic divergence score."""
    if len(bm.faces) == 0:
        return 0.0
    # build a quick histogram of face-normal directions
    import numpy as np
    ns = np.array([[f.normal[0], f.normal[1], f.normal[2]] for f in bm.faces])
    ns /= np.clip(np.linalg.norm(ns, axis=1)[:, None], 1e-9, None)
    # mean direction (dominant lobe)
    mean = ns.mean(axis=0)
    mean /= np.linalg.norm(mean)
    dots = ns @ mean
    # faces strongly opposing the dominant direction (>90 deg) are candidates
    bad = int((dots < 0).sum())
    return {"divergent_frac": float(bad / len(bm.faces)), "divergent_faces": bad}
