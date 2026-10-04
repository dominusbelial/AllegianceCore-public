# Meshy faction ship swap — importing, resolving, and now orienting

How a new faction's Meshy-generated ship models get into the client and render
correctly, and the **P520 orientation gate** that stops a yawed model from
landing unnoticed.

## 1. What "faction ship swap" is

Each Meshy civ batch (P511 dreghklar, P512 gigacorp, P513 technoflux, P514
omicron, P515 phoenix, …) lands as a set of `.glb` files — one per native
role — that are decimated and re-exported as `.fbx` into
`src/Allegiance.ClientCore.MonoGame/Content/Models/` and registered in
`MODELS_MANIFEST_P287.json`. The client loads every
`Models/<asset>.xnb` (compiled by the MonoGame content pipeline) and selects
geometry per ship via `HullModelResolver` (native `ModelName` → role →
`<civ>-<class>` asset).

## 2. The orientation assumption each swapped model must satisfy

`ShipRenderer` applies one **global** static correction to every mesh —

```csharp
NoseAlignmentCorrection = CreateRotationY(-90°)
```

(FBX baked axis-swap + this -90° yaw) — that turns a mesh whose **long axis is
on X** into one whose nose points forward (engine −Z). **Every swapped model
must therefore be authored with its long axis on X.**

Meshy generation jobs don't guarantee that: a model can come out with its long
axis on Z (or Y). Domingo hit exactly this on the IC Devastator (`ic-dev`):
"needs to spin 90 degrees to the right." Because the global correction can't
know about per-model quirks, one yawed model slips through until a human flies
it.

## 3. The P520 per-asset override

`src/Allegiance.Core/Content/ModelOrientationOverrides.cs` carries a
data-driven table keyed by asset name (yaw, plus optional pitch/roll):

- **`ic-dev`** — −90° about Y (verified live by Domingo 2026-10-03).
- **`gt-dev`** — the same mesh as `ic-dev` (same role/source), identical
  measured extents → −90°.
- **`rix-con`**, **`rix-miner`** — decisively Z-long (ratio ≥3.4) → −90°.

`ShipOrientationApply.Apply(model, asset)` folds the override into each unique
parent-bone transform **once at load**, in the same slot as the baked axis-swap,
*before* the global `NoseAlignmentCorrection`, so the two compose correctly and
every consumer (in-game `Game1.Lifecycle.cs`, `roster-capture` thumbnails /
turnarounds / wire sheets) sees the corrected orientation automatically.

## 4. The orientation gate (how new Meshy models get caught)

The audit is a headless, reproducible check driven by the **measured raw
render-frame extents** embedded in `ModelOrientationOverrides.Extents`
(captured from the shipped FBX by `roster-capture orient`, written to
`orient_extents.json`):

- `Audit()` / `ClassifyRaw()`: an asset's long axis (max extent axis) must be
  **X (forward)**. A decisive Z/Y-long asset with no corrective override is
  **Flagged**; a near-square asset (too close to settle by numbers) is
  **Ambiguous** (needs a visual check on the contact sheet).
- Invariant enforced by the unit test
  `ModelOrientationOverridesTests.Audit_IsClean_AfterOverride`:
  **every raw-flagged asset must carry an override** — so when a new Meshy
  batch lands with a rotated mesh, the test fails until the model is either
  corrected upstream or granted an override entry.

### To vet a new civ batch

```bash
# 1. rebuild content, then measure + render the contact sheet:
/home/domingo/.dotnet/dotnet run --project tools/roster-capture -- orient --out docs/P305_roster
# 2. inspect orient_before.jpg (all 72 top-down) and the *flag_* close-ups:
#    every ship's long axis should read the same way; a crosswise one is yawed.
# 3. if a mesh is genuinely yawed (decisively Z/Y-long on orient_extents.json),
#    add a ModelOrientationOverrides entry (use -90°, Domingo's verified sign)
#    and re-run; the audit must stay clean.
# 4. run the unit tests: dotnet test tests/Allegiance.ClientCore.MonoGame.Tests \
#      --filter FullyQualifiedName~ModelOrientationOverridesTests
```

Near-square hulls (e.g. `ic-ejectpod`, `ic-scout`) can't be settled by bounding
boxes — judge them by eye on the contact sheet, not by the numbers.
