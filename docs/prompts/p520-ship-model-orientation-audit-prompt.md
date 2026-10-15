# P520 — ship model orientation audit: per-asset yaw override for Meshy models that came out rotated (ic-dev confirmed)

## Why this task exists

**Domingo's live playtest (2026-10-03):** flying the IC Devastator, "model looks
not properly oriented, seems like it needs to spin 90 degrees to the right".

**Confirmed by geometry.** The renderer applies one **global** correction to
every ship mesh: `NoseAlignmentCorrection = CreateRotationY(-90°)`
(`src/Allegiance.ClientCore.Game/Rendering/ShipRenderer.cs:42`, applied at
`:127`). Each Meshy model comes from its own generation job and can come out
yawed differently. Hull 29 'Devastator' (native `tecdev`) resolves for IC to
asset **`ic-dev`**. Its GLB bounding box
(`~/Development/allegiance-tools/reference/p353stage/ic-dev.glb`) has its
**long axis on Z** (extent x 0.371, y 0.663, z 1.888). **Every other IC ship's
long axis is X**: ic-devastator 1.903/0.654/0.613, ic-fighter, ic-frigate and
so on. `ic-ejectpod` and `ic-scout` are also Z-longest, but they're
near-square, so check them visually; the numbers can't settle it.

**Interim:** Claude added an uncommitted LOCAL PLAY PATCH in Domingo's play
worktree (`~/allegiance-play`, `Game1.Lifecycle.cs` model load) that rotates
`ic-dev`'s mesh bones −90° about Y at load. **Domingo confirmed it live (2026-10-03): "devastator looks better"**,
so **−90° about Y is the verified correct override for `ic-dev`**. Use that
value; don't re-derive the sign. This task makes it real and
general.

## Scope

1. **Audit every ship asset** (all civs in `Content/Models`, from the GLB
   sources or the built FBX): bounding-box extents, longest axis, and a
   **visual render** of each from a fixed top view after the global
   correction. Produce a contact sheet so a human can confirm nose
   direction, since bounding boxes can't tell nose from tail or settle
   near-square hulls.
2. **A data-driven per-asset orientation override:** a small table keyed by
   asset name (yaw degrees, plus pitch/roll if ever needed) applied once
   at model load, on top of the global correction. Use the cleanest place
   (load-time bone transform, or a matrix passed to `ShipRenderer`), and
   apply the same table to textures, thumbnails and any other consumer.
   Seed it with `ic-dev` and whatever else the audit finds.
3. **Make it part of the pipeline:** a reusable check (a test or tool)
   that flags any asset whose orientation disagrees with its class
   siblings, so P511-P515's new Meshy models are caught as they land.
   Document it in `meshy-faction-ship-swap.md`.
4. Remove the need for Claude's local play patch.

## Explicitly out of scope

- Re-generating any Meshy model.
- The global `NoseAlignmentCorrection`, which was confirmed live in P250-era
  work.

## Verification

- A failing-first test: the audit check flags `ic-dev` before the override
  and passes after.
- The contact sheet (before/after) in the report.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P520_SHIP_ORIENTATION_AUDIT.md`.
