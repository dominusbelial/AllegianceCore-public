# P520 — per-asset ship model orientation audit & override

Date: 2026-10-03 · Branch: `master` (committed, not pushed) ·
Task: `docs/prompts/p520-ship-model-orientation-audit-prompt.md`

## Summary

Domingo's live playtest found the IC Devastator flying with its mesh yawed 90°
from its class siblings ("needs to spin 90 degrees to the right"). Root cause:
the renderer applies one **global** correction (`NoseAlignmentCorrection =
CreateRotationY(-90°)`, `ShipRenderer.cs:42`) that assumes every Meshy model's
long axis is on **X** (engine-forward), but each Meshy model comes from its own
generation job and can come out yawed. `ic-dev`'s mesh is **Z**-long, so the
global correction left it pointing sideways.

Claude prototyped a −90°-about-Y bone rotation at load in Domingo's play
worktree; **Domingo confirmed it live** ("devastator looks better"). P520 makes
that real and general:

1. **Measured every shipped ship model** (raw render-frame bounding-box
   extents, long axis) from the compiled FBX content and produced before/after
   top-view **contact sheets** so a human can confirm nose direction.
2. **Added a data-driven per-asset orientation override table**
   (`ModelOrientationOverrides`, seeded `ic-dev` + the 3 other audit-found
   meshes), applied once at model load on top of the global correction and
   shared by the in-game renderer, roster-capture thumbnails, turnarounds and
   wire sheets.
3. **Made it a pipeline gate**: a headless audit check + unit test that flags
   any asset whose orientation disagrees with its class siblings, so P511–P515
   new Meshy models are caught as they land.
4. **Removed the local play patch** (superseded by the real fix).

Out of scope (unchanged): re-generating any Meshy model, and the global
`NoseAlignmentCorrection` (confirmed correct in P250-era work).

---

## 1. The audit measurement

`tools/roster-capture orient` loads every P287 ship asset through the real
`Content.Load<Model>` pipeline, reads the raw vertex positions (transformed by
each mesh's `ParentBone.Transform` — the frame the renderer draws), and computes
axis-aligned bounding-box extents. Full 72-row data:
`P520_SHIP_ORIENTATION_AUDIT_assets/orient_extents.json`.

Measurement validity check against the task's GLB reference: `ic-dev` measures
`x 37.364 / y 66.361 / z 188.862` here vs the source GLB `0.371 / 0.663 / 1.888`
(~100× content-pipeline scale) — same proportions, same **long axis Z**. ✔

Classification rule (`ModelOrientationOverrides`):

- **Long axis X = forward = correct** (the convention the global correction
  assumes).
- **Decisive Z/Y-long, no override → Flagged** (the bug, caught).
- **Near-square** (two biggest extents too close to settle by numbers) →
  **Ambiguous**, judged visually on the contact sheet — bounding boxes can't
  tell nose from tail or settle these.

### Decisively non-forward meshes found (all Z-long)

| asset | x | y | z | long axis | ratio z/x | override |
|---|---|---|---|---|---|---|
| `ic-dev` | 37.4 | 66.4 | 188.9 | Z | 5.06 | −90° |
| `gt-dev` | 37.4 | 66.4 | 188.9 | Z | 5.06 | −90° (same mesh as ic-dev) |
| `rix-con` | 55.2 | 54.5 | 189.7 | Z | 3.44 | −90° |
| `rix-miner` | 45.7 | 46.7 | 189.9 | Z | 4.16 | −90° |

`gt-dev` shares the exact mesh with `ic-dev` (identical extents — both the
`tecdev`/"dev" role). Every other ship is forward-aligned (X). Several hulls
(e.g. `ic-ejectpod`, `ic-scout`, `rix-int`, `rix-pod`) lean Z but are
near-square → **Ambiguous**, verified visually: they read aligned on the
contact sheet, so no override.

## 2. Contact sheet (before / after)

Fixed top view of the whole 72-ship fleet, each ship upright so its raw long
axis is legible (a yawed mesh reads crosswise to its forward-X siblings).

- **`P520_SHIP_ORIENTATION_AUDIT_assets/orient_before.jpg *(screenshot of a Meshy model, kept in the private asset pack)*`** — global
  correction only. `ic-dev`/`gt-dev`/`rix-con`/`rix-miner` sit crosswise.
- **`P520_SHIP_ORIENTATION_AUDIT_assets/orient_after.jpg *(screenshot of a Meshy model, kept in the private asset pack)*`** — overrides
  applied. All four now align with their siblings.
- Close-ups (`orient_flag_ic-dev_before/after.jpg`) and side-by-side proofs
  (`orient_compare_ic-dev_vs_ic-devastator.jpg`,
  `orient_compare_rix-miner_vs_rix-carrier.jpg`, …) — the corrected mesh's long
  axis now matches a known-correct sibling exactly.

## 3. The override table & wiring

`src/Allegiance.Core/Content/ModelOrientationOverrides.cs`:

- `Overrides` — keyed by asset → `(yaw, pitch, roll)` degrees; seeded
  `ic-dev`, `gt-dev`, `rix-con`, `rix-miner` all `(−90, 0, 0)` (the sign Domingo
  verified live; not re-derived).
- `Extents` — the 72 measured extents, embedded so the audit is headless and
  reproducible.
- `Audit()` / `ClassifyRaw()` / `OverridesMustCoverEveryRawFlag()` — the gate.

`src/Allegiance.ClientCore.Game/Rendering/ShipOrientationApply.cs` folds the
override into each unique parent-bone transform **once at load**, in the same
slot as the baked axis-swap (before the global `NoseAlignmentCorrection`), so
the two compose correctly. Applied in:

- `Game1.Lifecycle.cs` `LoadContent` (the in-game fix — replaces the play patch),
- `roster-capture` `LoadModel` (dump / turnaround / wire thumbnails).

## 4. The failing-first test

`tests/Allegiance.ClientCore.MonoGame.Tests/ModelOrientationOverridesTests.cs`:

- **Before override** (`Audit_FlagsIcDev_BeforeOverride`): `ClassifyRaw`
  on `ic-dev`'s measured extents → `Flagged` (axis Z) — the audit catches the
  bug, including `gt-dev` / `rix-con` / `rix-miner`.
- **After override** (`Audit_IsClean_AfterOverride`): every raw-flagged asset
  carries an override ⇒ `OverridesMustCoverEveryRawFlag()` true and the full
  `Audit()` has **no** `Flagged` entries. If a future Meshy model lands yawed
  without an override entry, this fails — that's the pipeline gate.
- Guards: the override set is exactly the audit-found four; `ic-dev` override
  is −90°; known-correct `ic-devastator` is `Ok`; near-square `ic-ejectpod` is
  `Ambiguous`; `Extents` covers all 72 shipped assets.

## 5. Pipeline documentation

`docs/meshy-faction-ship-swap.md` documents the faction-swap import pipeline
and the new orientation gate, including the exact vet command for a new civ
batch.

## 6. Play patch removal

The ad-hoc ic-dev rotation in `~/allegiance-play`
(`Game1.Lifecycle.cs`, LOCAL PLAY PATCH 2026-10-03) is removed — the real
table-driven fix replaces it. (The unrelated `docker/compose.server.json`
play-session civ config remains, out of scope.)

## Verification

- Whole-solution build (`/home/domingo/.dotnet/dotnet build`): **0 warnings,
  0 errors**.
- Full suite (`scripts/run-tests.sh`), 3 consecutive runs — see below.
- Failing-first test passes (above). Contact sheet produced (above).
- Committed on `master`, not pushed; `git log -1` below.

<!-- VERIFICATION-RESULTS -->

### Full suite, 3 consecutive runs (`scripts/run-tests.sh`)

| run | Lobby | MonoGame (incl. P520) | ClientCore | Core | total | failures (all documented live-wire load-flakes) |
|---|---|---|---|---|---|---|
| 1 | 12/12 | 1208/1208 | 438/438 | 3707/3708 | 5365/5366 | `Core…LiveChatSectorWireTests.Individual_…relay…over_the_wire` — TEST_FLAKES.md:216 |
| 2 | 12/12 | 1208/1208 | 436/438 | 3705/3708 | 5361/5366 | `ClientCore…ShipDeleteGoneLiveTests.SameSide_WarperLeavesSector…` (:51/:114); `ClientCore…ClientWarpApproachSlowHullLiveTests.Legacy_200_450…` (:215); `Core…LiveStationRescueTests.Lifepod…` (:156); `Core…LiveRelaunchTests.Carrier_without_launch_slots…` (:40); `Core…ConquestTerritoryLiveTests.Conquest_win_in_one_mission…` (:29) |
| 3 | 12/12 | 1208/1208 | 437/438 | 3706/3708 | 5363/5366 | `ClientCore…FullSessionKillIntegrationTests…RealCrossSideKill` (:33); `Core…LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` (LiveTeamLobby flake family, run-tests.sh header); `Core…LiveTreasurePickupTests.Rescue_ship…moneychange…` (:157) |

Every failure is a **pre-existing live-wire timing flake** in the untouched
`Allegiance.Core` / `Allegiance.ClientCore` assemblies, documented in
`docs/TEST_FLAKES.md`; they rotate run-to-run under full-suite socket load
(confirmed: each passes in isolation — e.g. the run-1 `LiveChatSectorWireTests`
passed alone in 213 ms). None touch ship rendering/orientation. The P520 test
assembly (`Allegiance.ClientCore.MonoGame.Tests`, incl. `ModelOrientationOverridesTests`)
passed **1208/1208 in all 3 runs**.

Note: run-tests.sh exits non-zero on any of these live-wire flakes — consistent
with the ledger's own clean-baseline observations (TEST_FLAKES.md:358-360) where
even reference runs record ~3705/3708 with the same rotating live tests.
