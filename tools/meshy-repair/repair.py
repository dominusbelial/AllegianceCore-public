"""Automatic repair of a Meshy FBX (P581): snap significant detached pieces onto
the hull (rigid translation along the shortest-gap direction, preserving shape +
UVs/texture), drop floating crumbs, recalculate normals, export to a NEW file.
Never modifies the original.

Deterministic and driven by the same welded-component analysis as `analyze.py`.
ITERATIVE: after translating pieces onto the hull, re-welding can occasionally
tear a small sub-fragment off at a piece/piece junction; the loop re-runs the
snap until no significant detached piece remains (bounded), so no floating
fragment survives.

Usage (Blender headless):
  blender --background --python tools/meshy-repair/repair.py -- \
      <src.fbx> <out.fbx> [--margin 0.01] [--touch 0.002] [--piece 20]

Behaviour per detached component:
  - significant piece (verts >= PIECE and gap > TOUCH): rigid-translate the whole
    component toward the hull so it overlaps by MARGIN (gap_before -> -MARGIN).
  - tiny crumb (verts < PIECE): if it touches the hull (gap <= TOUCH) keep it
    (surface detail), else delete it (floating junk).
UV seams are preserved: every original split vertex coincident with a welded
vertex of a moved piece is translated identically, so UVs/texture are untouched.
"""
from __future__ import annotations
import bpy, bmesh, sys, os, json
from mathutils import Vector
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import import_fbx, hull_bvh, ship_length, WELD_DIST  # noqa: E402


def _args():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, out = argv[0], argv[1]
    kw = {}
    for i in range(2, len(argv)):
        if argv[i] == "--margin":
            kw["margin"] = float(argv[i + 1])
        elif argv[i] == "--touch":
            kw["touch"] = float(argv[i + 1])
        elif argv[i] == "--piece":
            kw["piece"] = int(argv[i + 1])
    return src, out, kw


def key(c):
    return (round(c[0], 5), round(c[1], 5), round(c[2], 5))


def _components(bm):
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
    return sorted(comps.values(), key=len, reverse=True)


def repair(src: str, out: str, margin: float = 0.01, touch: float = 0.002,
           piece_min: int = 20) -> dict:
    obj = import_fbx(src)
    orig_bm = bmesh.new()
    orig_bm.from_mesh(obj.data)

    report = {"src": os.path.basename(src), "margin": margin, "touch": touch,
              "piece_min": piece_min, "ship_length": 0.0, "passes": 0,
              "attached": [], "crumbs_dropped": 0, "crumbs_kept": 0}

    max_pass = 8
    for pass_i in range(max_pass):
        bm = orig_bm.copy()
        bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=WELD_DIST)
        bm.verts.ensure_lookup_table(); bm.edges.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        comps = _components(bm)
        hull = set(comps[0])
        tree, _ = hull_bvh(bm, hull)
        if report["ship_length"] == 0.0:
            report["ship_length"] = ship_length(bm)

        to_move = []    # (snapshotted orig vert objects, delta, gap, n)
        to_drop = []    # crumb key-sets to delete
        any_detached = False

        for comp in comps[1:]:
            n = len(comp)
            best = None
            for vi in comp:
                loc, normal, idx, dist = tree.find_nearest(bm.verts[vi].co)
                if loc is not None and (best is None or dist < best[0]):
                    best = (dist, loc, bm.verts[vi].co)
            gap = best[0] if best else 0.0
            comp_keys = {key(bm.verts[vi].co) for vi in comp}

            if n >= piece_min and gap > touch:
                # significant detached piece -> snap onto hull
                hp, pv = Vector(best[1]), Vector(best[2])
                if gap < 1e-9:
                    dirv = Vector((0, 0, 1))
                else:
                    dirv = (pv - hp).normalized()
                delta = -dirv * (gap + margin)
                # SNAPSHOT exact orig verts NOW — never re-match by live coord,
                # or a moved piece landing on another piece's keys double-moves.
                piece_verts = [v for v in orig_bm.verts if key(v.co) in comp_keys]
                to_move.append((piece_verts, delta, gap, n))
                any_detached = True
            elif n < piece_min and gap > touch:
                to_drop.append(comp_keys)
            else:
                report["crumbs_kept"] += 1

        # delete floating crumbs (only their own faces+verts; keep UV seams)
        for ks in to_drop:
            rem_faces = [f for f in orig_bm.faces
                         if any(key(v.co) in ks for v in f.verts)]
            if rem_faces:
                bmesh.ops.delete(orig_bm, geom=rem_faces, context='FACES')
            rem_verts = [v for v in orig_bm.verts if key(v.co) in ks]
            if rem_verts:
                bmesh.ops.delete(orig_bm, geom=rem_verts, context='VERTS')
        if to_drop:
            report["crumbs_dropped"] += len(to_drop)
            orig_bm.verts.ensure_lookup_table(); orig_bm.edges.ensure_lookup_table()
            orig_bm.faces.ensure_lookup_table()

        # apply translations (each piece only its own snapshotted verts)
        for piece_verts, delta, gap, n in to_move:
            for v in piece_verts:
                if v.is_valid:
                    v.co += delta
            report["attached"].append(
                {"nverts": n, "gap_before": gap, "delta": list(delta),
                 "gap_after": -margin})

        report["passes"] = pass_i + 1
        if not any_detached:
            break

    # recalc normals consistently
    bmesh.ops.recalc_face_normals(orig_bm)
    bmesh.ops.recalc_face_normals(orig_bm)

    orig_bm.to_mesh(obj.data)
    orig_bm.free()
    os.makedirs(os.path.dirname(os.path.abspath(out)) or ".", exist_ok=True)
    bpy.ops.export_scene.fbx(filepath=out, use_selection=False,
                             apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
                             object_types={'MESH'})
    report["out"] = out
    return report


if __name__ == "__main__":
    src, out, kw = _args()
    rep = repair(src, out, **kw)
    print(json.dumps(rep, indent=2))
