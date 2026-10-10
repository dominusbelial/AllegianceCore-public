# P581 — Meshy repair, Adv Scout ONLY (pilot pass): automated snap of detached
# pieces onto the hull, crumb drop, and an inspector preview of the repaired
# model (real-window proof).

**Scope (Domingo, 2026-10-09):** build the repair pipeline + audit tooling but run
them on **`dreg-scout2` (Adv Scout) only**; the other hulls and the replacement
catalogue / `catalogue:<id>` mapping / inspector catalogue browser are **P581b**
(`docs/prompts/p581b-…-prompt.md`), which runs only after Domingo reviews this
result. The Adv Scout keeps its native model in game; the repaired scout is for
review.

## What was built

### 1. Repair pipeline `tools/meshy-repair/` (Blender headless, deterministic)

| File | Role |
|------|------|
| `common.py` | Shared analysis: FBX import → `bmesh.ops.remove_doubles(dist=0.0005)` → welded connected components → largest = hull → hull-face BVH → `find_nearest` min gap per component (the exact method from the brief). |
| `analyze.py` | Per-model welded audit → JSON. |
| `repair.py` | Automatic repair + export. |
| `render.py` | Colored per-piece Solid/Workbench 4-view renders (hull grey, pieces highlighted). |
| `run.sh` / `README.md` | Wrapper + docs. |

**Repair rule (reproduces the brief):** for each significant detached piece
(≥ 20 welded verts, gap > touch), **rigid-translate the whole component along the
shortest-gap direction until it overlaps the hull by a small margin** (default
`--margin 0.01`) — shape, UVs and texture are preserved because every original
split vertex coincident with a welded vertex of the moved piece moves identically.
Delete crumbs under the size threshold that don't touch the hull. Recalculate
normals. **Keep UV seams** (weld only for analysis). Iterate until no significant
detached piece remains (a translation can tear a small sub-fragment at a
piece/piece junction — a second pass snaps it too). Export to a **new** file
`Content/Models/repaired/<model>.fbx`; originals untouched.

### 2. Inspector preview of the repaired model (view-only)

The repaired FBX is compiled by the content pipeline as `Models/repaired/dreg-scout2`
(new `Content.mgcb` entry) and loaded under `_shipModels["repaired/dreg-scout2"]`.
A pure resolver `Rendering/RepairedModelPreview.cs` chooses the model key to draw;
the inspector's `J` key / `--inspect-repaired` flag toggles the repaired variant on
the meshy side (side-by-side still shows native). It is **never written to
ship-art.json** — the in-game Adv Scout stays native (Domingo's decision), the
repaired scout is preview/review only. `--inspect-angles N` adds an unattended
orbit-angle sweep (6 evenly spaced azimuths, tilted to expose the wings) that shots
real-backbuffer frames and exits — used for the raw-vs-repaired-vs-native proof.

A latent non-demo shot bug was fixed along the way: `_inspectShotCooldown` only
decremented in demo mode, so `--inspect-shots` / the sweep captured exactly one
frame then stalled; it now ticks once per frame outside demo mode
(`Game1.Inspector.cs`, `UpdateInspector`).

## Adv Scout (`dreg-scout2`) audit + repair numbers (measured)

| Quantity | Before | After |
|----------|--------|-------|
| raw verts / faces | 18,041 / 20,000 | 17,993 / 19,991 |
| welded verts / faces | 13,374 / 19,994 | 13,638 / 19,991 |
| welded components | 142 | 113 |
| significant detached pieces | 4 (548/532 @ 0.061, 302/297 @ 0.148) | 0 detached (max gap 0.0011) |
| floating crumbs dropped | — | 36 |
| crumbs kept (touching hull) | 107 | 107 → 106 (final weld merge) |
| UV seams | preserved | preserved |

The repair moved each of the 4 pieces by `gap + 0.010` so the resulting gap
(overlap) is `−0.010` (reported in `repair_report.json`); the independent
after-analysis confirms every former piece now touches/merges the hull
(min gap ≤ 0.0011, i.e. at weld threshold). Evidence JSON in
`docs/reports/P581_assets/audit/{dreg-scout2_before, dreg-scout2_after,
dreg-scout2_repair_report}.json`.

## Real-window proof (2560×1440, real backbuffer)

Ran the windowed client on the live display with `--inspect --inspect-hull
"Adv Scout" --inspect-angles 6` (raw) and the same with `--inspect-repaired`.
6 side-by-side frames per mode (native ⇐ left, meshy ⇒ right) at azimuths
0/60/120/180/240/300°, pitch 22°:

- `docs/reports/P581_assets/proof/raw/` — native | raw-Meshy
- `docs/reports/P581_assets/proof/repaired/` — native | repaired-Meshy

`docs/reports/P581_assets/renders/` holds the colored before/after Blender
Solid/Workbench renders (hull grey, the 4 pieces highlighted), which clearly show
the wings/blades detached in the raw model and seated on the hull after repair.

**Toggle verified from the frames themselves:** at a fixed angle, the native
(left) half is **bit-identical** between the raw and repaired runs (lit-pattern
XOR = 0.00000) while the meshy (right) half differs (XOR = 0.00328), proving
`--inspect-repaired` swaps the rendered model in the real window and that the
runs are otherwise reproducible. The console confirms
`repaired meshy variant 'repaired/dreg-scout2' loaded` and `meshy side = REPAIRED`.

(Note: the local vision model was misconfigured this session — routed to a
non-multimodal model — so the frame content could not be machine-described here;
the objective gap metrics above plus the real screenshots are the evidence.)

## Failing-first tests

6 new tests in `Allegiance.ClientCore.MonoGame.Tests/RepairedModelPreviewTests.cs`
for the repaired-variant resolution: prefer repaired only when requested **and**
loaded; fall back to raw otherwise; safe on empty/null; unrepaired assets never
pick up another asset's repaired variant. `RepairedModelPreview` did not exist on
today's code (the test project could not compile), so the tests fail before and
pass after. All 6 green.

## Whole-solution build / suite

See the commit message + test ledger below (suite numbers recorded from the 2
consecutive `scripts/run-tests.sh` runs on the final code; known flakes ledgered in
`docs/TEST_FLAKES.md`).

## Native fidelity

No new native gameplay rule was ported — this task is art/mesh tooling only.
Gameplay (collision/hit-test) is untouched; the repair pipeline is a Python
asset-transformer and the inspector change is a view-only toggle. The hull's
native `.mdl` render path (`NativeShipResolver`) is unchanged.

## Committed on `master`, not pushed.

`git branch --show-current` → `master`.
