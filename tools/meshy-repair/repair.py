"""Automatic repair of a Meshy FBX (P581, fixed in P581c): snap significant
detached pieces onto the hull as ONE rigid unit, drop floating crumbs,
recalculate normals, export to a NEW file. Never modifies the original.

P581c fix: the original moved a piece by matching *individual original vertices*
whose coordinates lay in the piece's welded-coordinate set. At UV seams / the
fin root that coordinate test bleeds across face boundaries, so some faces had
only a subset of their vertices translated and were stretched into spiky shards.
Now each raw FACE is owned by the single welded component its vertices weld into
(provably one per face), and a moved piece translates every vertex of every one
of its faces by the SAME delta. No triangle ever connects a moved and an unmoved
vertex, so rigid moves cannot stretch geometry. Crumb counts are reported once
(the old code summed them across passes -> 330 instead of 106).

Deterministic, driven by the same welded-component analysis as `analyze.py`.
Every run ends with an automated artifact check (no stretched edge, face count
= raw - dropped, per-piece rigid move) that FAILS the run on any violation.

Usage (Blender headless):
  blender --background --python tools/meshy-repair/repair.py -- \
      <src.fbx> <out.fbx> [--margin 0.01] [--touch 0.002] [--piece 20]
"""
from __future__ import annotations
import bpy, bmesh, sys, os, json
from mathutils import Vector
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import import_fbx, hull_bvh, ship_length, WELD_DIST  # noqa: E402
import artifact_check  # noqa: E402


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


def _components_and_roots(bm):
    """Union-find connected components of the WELDED bmesh (largest first) and a
    per-welded-vertex component id."""
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
    for v in bm.verts:
        find(v.index)
    comps = {}
    root_of = [0] * len(bm.verts)
    for v in bm.verts:
        r = find(v.index)
        root_of[v.index] = r
        comps.setdefault(r, []).append(v.index)
    ordered = sorted(comps.values(), key=len, reverse=True)
    return ordered, root_of


def _mesh_arrays(bm):
    pos = [[v.co[0], v.co[1], v.co[2]] for v in bm.verts]
    faces = [[v.index for v in f.verts] for f in bm.faces]
    return pos, faces


def repair(src: str, out: str, margin: float = 0.01, touch: float = 0.002,
           piece_min: int = 20) -> dict:
    obj = import_fbx(src)
    orig_bm = bmesh.new()
    orig_bm.from_mesh(obj.data)
    orig_bm.faces.ensure_lookup_table()

    raw_pos, raw_faces = _mesh_arrays(orig_bm)   # immutable reference (pre-edit)

    report = {"src": os.path.basename(src), "margin": margin, "touch": touch,
              "piece_min": piece_min, "ship_length": 0.0,
              "attached": [], "crumbs_dropped": 0, "crumbs_kept": 0}

    # ---- welded analysis ONCE (single rigid pass, no re-snapping) ----
    wb = orig_bm.copy()
    bmesh.ops.remove_doubles(wb, verts=list(wb.verts), dist=WELD_DIST)
    wb.verts.ensure_lookup_table(); wb.edges.ensure_lookup_table()
    wb.faces.ensure_lookup_table()
    comps, root_of = _components_and_roots(wb)
    comp_size = {}
    for c in comps:
        comp_size[root_of[c[0]]] = len(c)
    hull_root = root_of[comps[0][0]]
    tree, _ = hull_bvh(wb, set(comps[0]))
    report["ship_length"] = ship_length(wb)

    # coord -> set of welded component ids at that rounded coordinate
    coord_root = {}
    for v in wb.verts:
        coord_root.setdefault(key(v.co), set()).add(root_of[v.index])

    # assign each raw face to the welded component owning the MAJORITY of its
    # vertices (vertex-vote majority, NOT largest-component: a fin/hull bridging
    # face at a UV seam usually has 2 fin verts + 1 shared-coordinate vert and
    # must travel with the fin, not the hull)
    from collections import Counter as _Counter
    faces_by_root = {}
    for f in orig_bm.faces:
        votes = _Counter()
        for v in f.verts:
            r = coord_root.get(key(v.co))
            if r is not None:
                for rr in r:
                    votes[rr] += 1
        if not votes:
            continue
        owner = votes.most_common(1)[0][0]
        faces_by_root.setdefault(owner, []).append(f)

    # ---- classify components; a significant piece = its welded faces + every
    # face transitively sharing a vertex (whole-face closure), so no triangle is
    # ever left with only some vertices moved (the P581 spike cause) ----
    to_move = []    # (root, n, gap, delta, moved_verts)
    to_drop_faces = []

    # face -> verts adjacency using BMFace / BMVert OBJECTS as hash keys
    # (stable underlying-element hashes; NOT id(), whose wrappers are unstable)
    face_verts = {}   # BMFace -> list[BMVert]
    vert_faces = {}   # BMVert -> set[BMFace]
    for f in orig_bm.faces:
        face_verts[f] = list(f.verts)
        for v in f.verts:
            vert_faces.setdefault(v, set()).add(f)

    def _closure(seed_faces):
        """Transitively add every face sharing a BMVert with the set, so the
        result is a union of WHOLE faces (no face can have a subset moved)."""
        done = set()
        stack = list(seed_faces)
        moved_verts = {}
        while stack:
            f = stack.pop()
            if f in done:
                continue
            done.add(f)
            for v in face_verts[f]:
                if v not in moved_verts:
                    moved_verts[v] = v.co.copy()
                for nf in vert_faces.get(v, ()):
                    if nf not in done:
                        stack.append(nf)
        return moved_verts  # dict BMVert -> before-coord

    for comp in comps[1:]:
        n = len(comp)
        rid = root_of[comp[0]]
        best = None
        for vi in comp:
            loc, normal, idx, dist = tree.find_nearest(wb.verts[vi].co)
            if loc is not None and (best is None or dist < best[0]):
                best = (dist, loc, wb.verts[vi].co)
        gap = best[0] if best else 0.0
        if n >= piece_min and gap > touch:
            hp, pv = Vector(best[1]), Vector(best[2])
            dirv = Vector((0, 0, 1)) if gap < 1e-9 else (pv - hp).normalized()
            delta = -dirv * (gap + margin)
            seed_faces = faces_by_root.get(rid, [])
            moved_verts = _closure(seed_faces) if seed_faces else {}
            to_move.append((rid, n, gap, delta, moved_verts))
        elif n < piece_min and gap > touch:
            report["crumbs_dropped"] += 1
            if rid in faces_by_root:
                to_drop_faces.extend(faces_by_root[rid])
        else:
            report["crumbs_kept"] += 1

    # ---- drop floating crumbs ----
    if to_drop_faces:
        bmesh.ops.delete(orig_bm, geom=list(to_drop_faces), context='FACES')

    # ---- apply rigid translations (whole-face closure, each vert once) ----
    for rid, n, gap, delta, moved_verts in to_move:
        for v, before in moved_verts.items():
            if v.is_valid:
                v.co += delta
        report["attached"].append(
            {"nverts": n, "gap_before": gap, "delta": list(delta),
             "gap_after": -margin})

    # ---- rigid-move artifact check (per piece, before -> after) ----
    for i, (rid, n, gap, delta, moved_verts) in enumerate(to_move):
        att = report["attached"][i] if i < len(report["attached"]) else {}
        bad, tot = artifact_check.check_rigid_move(
            list(moved_verts.values()),
            [v.co for v in moved_verts if v.is_valid], list(delta))
        att["rigid_move_bad"] = bad
        att["rigid_move_total"] = tot
        if bad:
            raise RuntimeError(
                f"ARTIFACT: piece {n}-verts moved non-rigidly ({bad}/{tot} verts)")

    # recalc normals consistently
    bmesh.ops.recalc_face_normals(orig_bm)
    bmesh.ops.recalc_face_normals(orig_bm)

    # ---- edge-stretch + face-count artifact checks ----
    orig_bm.verts.ensure_lookup_table(); orig_bm.faces.ensure_lookup_table()
    rep_pos, rep_faces = _mesh_arrays(orig_bm)
    stretched, deformed_faces, dropped_count = artifact_check.check_edge_stretch(
        rep_pos, rep_faces, raw_pos, raw_faces)
    report["dropped_faces"] = len(raw_faces) - len(rep_faces)
    ok_fc, obs_fc, exp_fc = artifact_check.check_face_count(
        len(raw_faces), len(rep_faces), report["dropped_faces"])
    report["edge_stretch_violations"] = deformed_faces
    report["face_count_ok"] = ok_fc
    if deformed_faces:
        raise RuntimeError(
            f"ARTIFACT: {deformed_faces} repaired faces have no raw counterpart "
            f"(stretched edges), first: {stretched[:8]}")
    if dropped_count != report["dropped_faces"]:
        raise RuntimeError(
            f"ARTIFACT: dropped-face mismatch: check observed {dropped_count} "
            f"raw faces unmatched vs repair dropped {report['dropped_faces']}")

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
