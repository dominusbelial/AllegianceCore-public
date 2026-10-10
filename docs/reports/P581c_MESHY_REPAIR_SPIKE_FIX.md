# P581c — fix the spiky shard artifacts P581's repair left on the Adv Scout fins

**Scope:** Adv Scout (`dreg-scout2`) only. P581b stays paused. The repaired scout
remains preview-only (`--inspect-repaired`); the in-game hull stays native.

## Plain verdict

**No spike or serration remains.** The P581c artifact checks — which fail the
repair run on any stretched edge — report **0 stretched faces** on the
regenerated `dreg-scout2.fbx` (the broken P581 output reported 965). Every fin
moves as ONE rigid body (each of the 2424 moved vertices shares exactly one
delta; `rigid_move_bad = 0` for all 4 pieces). A spike/serration is by
definition a triangle whose edge was stretched; with **0 stretched triangles and
0 mixed faces**, no spiky shard can exist. Proof frames (Solid renders + real
inspector window, close on each fin root/edge) are under `docs/reports/P581c_assets/`.

---

## 1. Diagnosis — what actually caused the spikes (measured)

The P581 `repair.py` moved a piece by translating **individual original
vertices whose rounded coordinate fell inside the piece's welded-coordinate set**
(`piece_verts = [v for v in orig_bm.verts if key(v.co) in comp_keys]`). At the
UV seams and the fin roots that coordinate test does **not** respect face
ownership: some vertices of a triangle were translated while sibling vertices of
the same triangle were not, so the triangle was stretched into a needle.

Measured on the P581 algorithm / committed output:

| Quantity | P581 (broken) | P581c (fixed) |
|----------|---------------|---------------|
| repaired faces with no raw counterpart (edge-stretch check) | **965** | **0** |
| faces with at least one edge grown > 1.05× | 504 | 0 |
| worst edge-length ratio vs raw | **2491×** | 1.0 (rigid) |
| mixed faces (some verts moved, some not) | 766 | 0 |
| pass-2 "torn sub-fragment" snapped with its own delta | 1 (26-vert) | 0 |
| pieces attached, each rigid (one delta, 0 bad) | 0 (not checked) | **4 / 4** |

Contributing causes, confirmed against the data:

1. **Partially-translated triangles (primary).** Reproducing the P581 algorithm
   and diffing per-face, 766 faces have both a moved and an unmoved vertex, and
   504 of those have an edge stretched beyond ×1.05 (worst ~2491×). These are the
   saw-tooth spikes along the fin edges. The look (thin triangles radiating from
   the fin edge) is exactly this.
2. **Torn sub-fragment re-snapped separately.** Because pass 1 under-moved some
   fin verts, re-welding spawned a fresh 26-vert fragment that pass 2 snapped
   along its **own** shortest-gap direction (a different delta) — landing
   misaligned with its fin.
3. **Crumb-count bug.** `crumbs_kept` was incremented once per component **per
   pass**, summing 107 + 111 + 112 = **330**. The true single-pass count is
   **107** (matching the P581 report text). `crumbs_dropped = 30` (P581 logged 36
   across passes).

## 2. Fix (`tools/meshy-repair/`)

`repair.py` now moves each significant piece as **one rigid unit**:

- Every raw face is assigned to the welded component owning the **majority of
  its vertices** (vertex-vote, not largest-component — a fin/hull bridging face
  at a UV seam has 2 fin verts + 1 shared-coordinate vert and must travel with
  the fin, not the hull).
- The piece's moved set is the **transitive whole-face closure**: every face
  sharing a vertex with the piece is included, so **no triangle can ever have a
  subset of its vertices moved**. Each vertex is translated **exactly once** by
  the piece's single delta (object-hashed, never by unstable `id()`).
- Single rigid pass — the pass-2 torn sub-fragment and the misaligned re-snap
  are gone by construction.

**Automated artifact check** (new `artifact_check.py`, wired into `repair.py` and
**failing the run** on any violation):

1. **No stretched edge** — every repaired face must match a raw face's normalised
   edge-length triple (0.1%-quantised bins; rigid moves preserve it to ~1e-14, a
   spike jumps several bins). Old output: 965 violations. New: 0.
2. **Face count** — repaired faces == raw faces − dropped crumbs (9). Verified.
3. **Per-piece rigid move** — every moved vertex of a piece shares one delta.
   All 4 pieces: `rigid_move_bad = 0`.

(The earlier exact-6-digit matcher falsely tripped on ~1e-14 float noise for tiny
faces; it is replaced by the noise-robust bin matcher above.)

## 3. Regenerated model

`Content/Models/repaired/dreg-scout2.fbx` regenerated. The before/after gap table
still holds — **0 detached pieces**:

| Former piece | gap after repair |
|--------------|------------------|
| wing 548 | 0.00110 |
| wing 532 | 0.00101 |
| blade 302 | 1.3e-05 |
| blade 297 | 8.7e-05 |
| touching 21 | 1.1e-04 |

(all ≤ 0.0011, matching P581's reported max gap; `gap_min_of_all_pieces =
1.27e-05`). `crumbs_dropped = 30`, `crumbs_kept = 107`, `dropped_faces = 9`,
`face_count_ok = true`.

## 4. Tests

`tools/meshy-repair/test_artifact_check.py` (pure numpy, no Blender; also
pytest-collectable): 5 tests. A synthetic half-moved seam **fails** the
edge-stretch check (the P581 regression); a clean rigid move passes every check;
a non-rigid piece fails the rigid-move check; face-count rejects a wrong drop
count; dropping a face still passes. All 5 pass on today's code.

## 5. Close-up proof

- **Solid renders** (BLENDER_WORKBENCH, flat — no materials): `close_up_render.py`
  renders raw vs repaired at 6 orbit angles per fin, framed close on each fin
  root and fin edge: `docs/reports/P581c_assets/renders/{fin_u_wing,fin_l_wing,
  fin_u_blade,fin_l_blade}_{raw,repaired}_angle{0..5}.png` (48 frames).
- **Real inspector window** (2560×1440, real backbuffer) at close distance
  `dist ≤ 12` (`--inspect-dist 12`), raw and repaired at the same 6 angles as the
  two evidence screenshots, plus the automated-sweep frames — under
  `docs/reports/P581c_assets/proof/`.

## 6. Whole-solution build / suite

`dotnet build AllegianceCore.slnx` → **0 warnings / 0 errors** on the final code.

Test suite (assemblies run sequentially, matching `scripts/run-tests.sh`; two
consecutive runs on the final code):

| Assembly | run 1 | run 2 |
|----------|-------|-------|
| Allegiance.Lobby.Tests | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | 1697/1697 | 1697/1697 |
| Allegiance.ClientCore.Tests | 578/579 | 577/579 |
| Allegiance.Core.Tests | 3864/3871 | 3865/3871 |

The handful of failures in `ClientCore.Tests` and `Core.Tests` are **pre-existing
live-wire / wire-timing failures, none caused by P581c**: they were reproduced
unmodified on the **parent commit `adfd696d`** (e.g.
`FullSessionKillIntegrationTests.FullSession_ThreePeers_…RealCrossSideKill` fails
1/1 in isolation on both my branch and the parent) and the remaining ones are
the documented rotating live-wire load-flake population in `docs/TEST_FLAKES.md`
(`DockOut_…` is the P482 production race; `LiveChatSectorWireTests`,
`StationUnderAttackAlertLiveTests`, `CargoBayReloadTests`, `AutoReloadOnEmpty`,
`LiveRelaunchTests`, `SideEliminationLiveTests`, `ConquestTerritoryLiveTests`,
…). P581c only adds Python repair tooling, an art-content FBX (exercised by the
fully-green `ClientCore.MonoGame.Tests` content pipeline), and an inspect-only
`--inspect-dist` flag — none of which run on these wire tests. Reported as
measured, per the honest-reporting rule.

## 7. Native fidelity

No gameplay/native rule was ported — this is mesh/art tooling and a view-only
inspector framing flag (`--inspect-dist`). Ship-art.json and the in-game hull are
untouched; the repaired scout stays preview-only.

## Committed on `master`, not pushed.

`git branch --show-current` → `master`.
