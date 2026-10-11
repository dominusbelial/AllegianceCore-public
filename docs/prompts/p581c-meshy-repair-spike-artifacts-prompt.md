# P581c — fix the spiky shard artifacts P581's repair leaves on the Adv Scout fins (still Adv Scout only)

## Why this task exists

P581 (`ee97108c`, `docs/reports/P581_MESHY_REPAIR_AND_CATALOGUE.md`,
`tools/meshy-repair/`) snapped the Adv Scout's (`dreg-scout2`) 4 detached
fin pieces onto the hull. Domingo reviewed it in the live inspector
(`--inspect --inspect-repaired --inspect-hull "Adv Scout"`) on 2026-10-10:
**the fins are attached, but the repaired model has new spiky, serrated
shards along the fin edges and around the fin roots** ("fins are attached
but now it has extra geometry"). Evidence:

- `(screenshots of Meshy models, kept in the private asset pack)`: raw Meshy, fins float
  (upper-left fin and lower fin pair visibly detached). Smooth fin edges.
- `docs/prompts/p581c-repaired-scout-spikes.png`: repaired, fins attached,
  but saw-tooth spikes along both yellow fins and green spiky fringe at
  the fin roots, none of which exist in the raw model.

P581's 6-angle proof frames were at a distance where this didn't show;
the close-up did.

**Suspects to verify, not assume** (find the real cause first):
1. **UV-seam split vertices partially translated:** `repair.py` moves
   "every original split vertex coincident with a welded vertex of a moved
   piece". If some split copies (or verts of triangles that span a moved
   and an unmoved component) are not moved, triangles get stretched into
   spikes. The look (thin triangles radiating from fin edges) fits this.
2. **Torn sub-fragments snapped separately:** the report shows 3 passes and
   5 attachments for 4 pieces. A sub-fragment snapped along its OWN
   shortest-gap direction lands misaligned with its fin.
3. **Crumb handling:** the JSON says `crumbs_kept 330` while the report
   text says 107 → 106. Find out which is true and whether kept crumbs that
   belonged to a moved fin were left behind or moved wrongly.

## Scope

1. **Diagnose:** diff raw vs repaired meshes per vertex/face. Find the
   faces whose edge lengths or area grew a lot (stretched triangles), the
   faces/components that exist only in the repaired mesh, and map each
   artifact to the step that created it. Report numbers.
2. **Fix `tools/meshy-repair/`** so a moved piece moves as ONE rigid unit:
   every vertex of every face of the piece, all its split/seam copies, and
   any crumbs or sub-fragments attached to that piece rather than to the
   hull. No triangle may connect a moved and an unmoved vertex. Fix the
   crumb count reporting.
3. **An automated artifact check** added to the tool, failing the run if
   any is found:
   - no face edge longer than its raw counterpart × 1.05, since rigid moves
     never stretch
   - same face count as raw minus dropped crumbs
   - per-piece rigid-move check (all verts share one delta)
4. Regenerate `Content/Models/repaired/dreg-scout2.fbx` with the fixed tool.
   **Adv Scout only.** P581b stays paused.

## Out of scope

Other hulls; the catalogue; changing which model the game uses (the
repaired scout stays preview-only); inspector features other than what's
needed for proof.

## Verification

- Tests for the artifact checks (a synthetic mesh with a deliberately
  half-moved seam must fail; a clean rigid move must pass).
- **Close-up proof:** Blender renders in **Solid/matcap shading** (not
  materials) of raw vs repaired, 6 angles **close up on each fin root and
  fin edge**, plus the real inspector window at close distance (dist ≤ 12,
  the distance Domingo used) with `--inspect-repaired`, raw and repaired at
  the same angles as the two evidence screenshots. Look at your own frames
  and state plainly whether any spike or serration remains.
- The before/after gap table from P581 still holds: 0 detached pieces.
- Use your **own temporary worktree** for live runs (see the footer rules).
- Whole-solution build 0 warnings/0 errors; suite 2 consecutive runs on the
  final code via `scripts/run-tests.sh`.

## Deliverables

Commit on `master` (not pushed): the tool fix, the regenerated FBX, tests,
and `docs/reports/P581c_MESHY_REPAIR_SPIKE_FIX.md` with the diagnosis,
numbers, close-up proof and a plain verdict.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 2 consecutive runs **on the final code** (reduced from 3 per Domingo, 2026-10-09: the flakes are known/ledgered). Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 2× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 2× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

## ⛔ Shared-environment rules (Domingo, 2026-10-10)

- **Never modify, build in, check out or run from `~/allegiance-play2` or
  `~/allegiance-deploy`.** Those are Domingo's live play client and the 221
  deploy source. For live proofs, create your **own temporary worktree**
  (`git worktree add /tmp/<task>-proof HEAD`), use it, and remove it when
  done.
- **Never connect to, deploy to, or restart the 221 server (`192.168.0.221`).**
  Use a local server on this workstation (as the P572 tooling does).
- Stop every local server, client and bot process you started (by exact PID)
  before ending your session.
