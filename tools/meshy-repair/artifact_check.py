"""Automated artifact checks for the Meshy repair pipeline (P581c).

Pure-Python (numpy only) so it can run under plain `pytest` as well as inside
`blender --background`. Verifies that a repair was RIGID: since a rigid move
never stretches, no face edge may be longer than its raw counterpart (guarded by
matching each repaired face's normalised edge-length triple to the raw
multiset), the face count must equal raw-minus-dropped, and every moved piece's
vertices must share exactly one displacement vector.

These functions take plain numpy arrays / python lists so Blender's bmesh objects
are converted at the call site in repair.py and the unit tests need no Blender.
"""
from __future__ import annotations
import numpy as np
from collections import Counter


def face_edge_lengths(pos, face):
    """Edge lengths (cyclic) of one polygon. pos: (N,3) array; face: vertex idx list."""
    n = len(face)
    out = []
    for i in range(n):
        a = pos[face[i]]
        b = pos[face[(i + 1) % n]]
        out.append(float(np.linalg.norm(np.asarray(a) - np.asarray(b))))
    return out


def normalize_triple(lengths):
    """Normalised edge-length triple of a TRIANGLE (scale-invariant, sorted desc).
    Rigid moves preserve it exactly; a stretched edge changes it. None if degenerate."""
    if len(lengths) != 3:
        return None
    s = sorted(lengths, reverse=True)
    if s[0] < 1e-9:
        return None
    return tuple(round(x / s[0], 6) for x in s)


def build_triple_multiset(pos, faces):
    ms = Counter()
    for f in faces:
        t = normalize_triple(face_edge_lengths(pos, f))
        if t is not None:
            ms[t] += 1
    return ms


def _qtriple(t, prec=3):
    """Coarse-quantised normalised triple (default 0.1%). Rigid moves perturb a
    face's edge lengths by ~1e-14, far below one quantisation bin, so a rigid
    face always matches its raw twin; a real spike (edge stretched >>1%) jumps
    several bins and matches nothing."""
    if t is None:
        return None
    return tuple(round(x, prec) for x in t)


def check_edge_stretch(rep_pos, rep_faces, raw_pos, raw_faces, prec=3):
    """Return (stretched, deformed_faces, dropped_count).

    Noise-robust spike detector. A rigid move preserves every face's normalised
    edge-length triple (to ~1e-14); a spike/serration changes it by far more.
    Match each repaired face to a raw face whose coarsely-quantised triple
    (default 0.1% bins) is identical. A repaired face with no raw partner is a
    stretched/serrated face -> violation. ``deformed_faces`` is that count;
    ``dropped_count`` is how many raw faces had no repaired partner (= dropped)."""
    raw_buckets = {}
    for f in raw_faces:
        t = normalize_triple(face_edge_lengths(raw_pos, f))
        q = _qtriple(t, prec)
        if q is not None:
            raw_buckets[q] = raw_buckets.get(q, 0) + 1
    stretched = []
    deformed_faces = 0
    for f in rep_faces:
        t = normalize_triple(face_edge_lengths(rep_pos, f))
        q = _qtriple(t, prec)
        if q is None:
            continue
        if raw_buckets.get(q, 0) > 0:
            raw_buckets[q] -= 1
        else:
            deformed_faces += 1
            stretched.append(q)
    dropped_count = sum(c for c in raw_buckets.values() if c > 0)
    return stretched, deformed_faces, dropped_count


def check_face_count(raw_n, rep_n, expected_dropped):
    """Same face count as raw minus the dropped crumbs. Returns (ok, observed, expected)."""
    observed = raw_n - rep_n
    return observed == expected_dropped, observed, expected_dropped


def check_rigid_move(before, after, delta, tol=1e-5):
    """Verify every vertex in a moved piece displaced by the SAME delta.
    before/after: (N,3) arrays. Returns (bad_count, total)."""
    before = np.asarray(before, dtype=float)
    after = np.asarray(after, dtype=float)
    delta = np.asarray(delta, dtype=float)
    bad = 0
    for b, a in zip(before, after):
        if float(np.linalg.norm(a - b - delta)) > tol:
            bad += 1
    return bad, len(before)
