# P527 — radar contacts + side tags anchor to the render pose (finish P523's "no third path"), fixing HUD icons trailing moving hulls

## Why this task exists

**Domingo's live playtest (2026-10-04):** "why the miner and the hud icon
position would not match?" The HUD icon is visibly offset from the miner hull.

**Root cause (traced by Claude, verified in source):** the hull and its HUD
icon read **two different positions**.

- **Hull:** drawn at the per-frame **render pose**, `Game1._poses[id]`. For
  remote ships that's the P448b dead-reckoned pose, extrapolated every frame
  (`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:1661-1668`). For the
  own ship it's the local-sim pose (`:1576`, P508).
- **Radar contact bracket and in-world side tag (P372):** projected from the
  **raw last `ServerShipUpdate`**, `ship.Latest.Position`
  (`src/Allegiance.ClientCore.Game/Game1.Rendering.cs:1898-1935`):
  `RadarProjection.Project(PoseConverter.ToXna(up.Position), ownPos, …)`. The
  side tag reuses that projected `site.Point` (`:1931-1934`). The distance
  opacity (`:1908`) also uses raw positions. The loop's own-ship eye `ownPos`
  is the raw own-ship update (`:1890-1891`), not `_poses[me]`.
- The gap ≈ velocity × time since the last update, so the icon **trails** a
  moving hull. It's very visible on big, steadily moving miners.

**This is P523's exact bug, left half-fixed.** P523 (`a56d925f`) introduced
`ResolveShipAnchor` (`Game1.Targeting.cs:484-516`). Its doc comment says target
brackets, labels, the bearing arrow and the missile-lock bracket "must anchor
to this SAME pose or they jitter", and "This is the one shared render pose
accessor the HUD ship anchors all use; no third path." But only two callers
use it: `Game1.FlightInput.cs:1998` (target bracket/label) and
`Game1.Camera.cs:93` (lock bracket). The radar contact loop is the third path.

**Native has a single position per object:** the radar reads
`pmodel->GetPosition()` (`~/Development/Allegiance/src/WinTrek/radarimage.cpp:625`,
own ship at `:532`), the same model position the renderer draws, so native
icons can't drift from hulls.

**Already ruled out:** an off-centre mesh. Claude computed the GLB bounding-box
centres for `bios/gt/ic/rix-miner` (and fighters for comparison). All are
centred on the origin (offset ≈ 0), so P520's per-asset rotation can't swing a
hull away from its icon.

## Scope

1. In the radar-contact loop (`Game1.Rendering.cs` ~:1898-1935), resolve each
   other ship's position through `ResolveShipAnchor(_poses, world.Ships, id,
   out var pos)` instead of `up.Position`. Use it for the projection, the
   opacity distance and therefore the side tag. Keep every existing gate
   (`IsNotVisible`, same-sector, `Latest` present) exactly as is.
2. Resolve the loop's own-ship eye (`ownPos` and the `ownUp.Position` used for
   opacity) from the own-ship render pose (`_poses[me]` via the same
   accessor), falling back to the raw update only when absent. Match how the
   camera and P523's anchors already do it.
3. Keep the per-frame allocation discipline (P326 inlined this loop on
   purpose): no closures and no new per-frame allocations.
4. Update `ResolveShipAnchor`'s doc comment to list the radar contact and side
   tag as callers.

## Explicitly out of scope

- **The shield-hit flare centre** (`Game1.Rendering.cs:1260-1266`) also reads
  `tgt.Latest.Position`. Same pattern, but it's a 3D effect, not a HUD
  anchor. Name it in the report as a follow-up; don't change it here.
- Target-candidate gathering (`Game1.Targeting.cs:173-181`) uses raw poses for
  *selection*, not drawing, and server-authoritative positions are arguably
  right there. Leave it.
- The dead-reckoning (P448b) itself, server update rates, and any HUD visual
  restyling.

## Verification

- A **failing-first** headless test. Pull the per-contact position choice into
  a small static/pure helper if needed, the same way P523 made
  `ResolveShipAnchor` testable. Take a remote ship whose render pose has
  advanced past its last `ServerShipUpdate` (moving, between updates), and
  assert that the radar contact and side-tag anchor equal the **render pose**,
  not the raw update. It must fail against the current code. Also cover the
  fallback (no render pose yet → raw update) and the own-ship eye.
- Keep `P523SelfTargetAndBracketJitterTests` green.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs. Cite `docs/TEST_FLAKES.md` for any remaining flake. **Report only
  numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P527_RADAR_TAG_RENDER_POSE_ANCHOR.md`: root
   cause with file:line, native citation, the fix, test results and the
   shield-flare follow-up note.
