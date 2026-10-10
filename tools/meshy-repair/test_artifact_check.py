"""Failing-first unit tests for the P581c artifact checks (tools/meshy-repair/
artifact_check.py). Pure numpy — no Blender needed. Run with plain python:

    python3 tools/meshy-repair/test_artifact_check.py

(or `pytest` if installed). Constructs a synthetic two-piece mesh and checks
that a CLEAN rigid move passes every artifact check while a deliberately
half-moved seam (one triangle with only some of its vertices translated)
FAILS the edge-stretch check — the exact failure P581 produced on the Adv Scout.

Build a mesh from two triangles sharing an edge:
    anchor triangle  A  (the "hull", never moved)
    piece triangles  P0 P1  (the "fin", moved rigidly by `delta`)
plus an OPTIONAL stray seam triangle S that shares one vertex class with P0.
"""
from __future__ import annotations
import numpy as np
import os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import artifact_check as ac


def build_mesh():
    """Returns (pos, faces, piece_verts, anchor_verts). Unit quad + fin."""
    #  anchor quad (hull)
    a0 = np.array([0.0, 0.0, 0.0])
    a1 = np.array([1.0, 0.0, 0.0])
    a2 = np.array([0.0, 1.0, 0.0])
    a3 = np.array([1.0, 1.0, 0.0])
    #  piece quad (fin) sitting adjacent, to be translated
    p0 = np.array([2.0, 0.0, 0.0])
    p1 = np.array([3.0, 0.0, 0.0])
    p2 = np.array([2.0, 1.0, 0.0])
    p3 = np.array([3.0, 1.0, 0.0])
    pos = [a0, a1, a2, a3, p0, p1, p2, p3]
    # anchor faces (2 tris), piece faces (2 tris)
    faces = [
        [0, 1, 2], [1, 3, 2],      # anchor
        [4, 5, 6], [5, 7, 6],      # piece
    ]
    piece_verts = [4, 5, 6, 7]
    anchor_verts = [0, 1, 2, 3]
    return np.array(pos), faces, piece_verts, anchor_verts


DELTA = np.array([0.0, 0.0, 0.5])


def _apply_translation(pos, verts, delta):
    out = pos.copy()
    out[verts] = out[verts] + delta
    return out


# --------------------------------------------------------------------------
def test_clean_rigid_move_passes_every_check():
    raw_pos, faces, piece_verts, anchor_verts = build_mesh()
    rep_pos = _apply_translation(raw_pos, piece_verts, DELTA)

    stretched, deformed, dropped = ac.check_edge_stretch(
        rep_pos, faces, raw_pos, faces)
    assert stretched == [], f"clean rigid move must not stretch: {stretched}"
    assert deformed == 0
    assert dropped == 0

    ok, obs, exp = ac.check_face_count(len(faces), len(faces), 0)
    assert ok and obs == 0 and exp == 0

    bad, tot = ac.check_rigid_move(
        raw_pos[piece_verts], rep_pos[piece_verts], DELTA)
    assert bad == 0 and tot == 4


def test_clean_rigid_move_with_dropped_face_passes():
    raw_pos, faces, piece_verts, anchor_verts = build_mesh()
    rep_pos = _apply_translation(raw_pos, piece_verts, DELTA)
    # simulate dropping one anchor triangle (face index 0)
    rep_faces = faces[1:]
    raw_n = len(faces)
    rep_n = len(rep_faces)
    ok, obs, exp = ac.check_face_count(raw_n, rep_n, raw_n - rep_n)
    assert ok, f"{obs} != {exp}"
    stretched, deformed, dropped = ac.check_edge_stretch(
        rep_pos, rep_faces, raw_pos, faces)
    assert deformed == 0
    assert dropped == 1, "exactly the one dropped face should be unmatched"


def test_half_moved_seam_fails_edge_stretch():
    """The P581 regression: only ONE vertex of a triangle is translated while
    its two siblings stay put -> the triangle's shape changes -> must be caught."""
    raw_pos, faces, piece_verts, anchor_verts = build_mesh()
    rep_pos = raw_pos.copy()
    # move only vertex 4 (piece corner) by DELTA; leave 5,6,7 unmoved
    rep_pos[4] = rep_pos[4] + DELTA

    stretched, deformed, dropped = ac.check_edge_stretch(
        rep_pos, faces, raw_pos, faces)
    assert deformed >= 1, "half-moved seam must be flagged as stretched"
    assert len(stretched) >= 1


def test_non_rigid_piece_fails_rigid_check():
    raw_pos, faces, piece_verts, anchor_verts = build_mesh()
    rep_pos = _apply_translation(raw_pos, piece_verts, DELTA)
    rep_pos[6] = rep_pos[6] + np.array([0.01, 0.0, 0.0])  # one vert displaced differently
    bad, tot = ac.check_rigid_move(raw_pos[piece_verts], rep_pos[piece_verts], DELTA)
    assert bad == 1 and tot == 4


def test_face_count_rejects_wrong_drop_count():
    raw_pos, faces, piece_verts, anchor_verts = build_mesh()
    rep_pos = _apply_translation(raw_pos, piece_verts, DELTA)
    ok, obs, exp = ac.check_face_count(len(faces), len(faces), expected_dropped=2)
    assert not ok, "claiming 2 dropped when 0 were dropped must fail"


def _run_all():
    import traceback
    fns = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    failed = 0
    for fn in fns:
        try:
            fn()
            print(f"PASS  {fn.__name__}")
        except Exception as e:
            failed += 1
            print(f"FAIL  {fn.__name__}: {e}")
            traceback.print_exc()
    print(f"\n{len(fns) - failed}/{len(fns)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(_run_all())
