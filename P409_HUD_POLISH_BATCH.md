# P409 — HUD polish batch: thrust-gauge color, leader-command overlap, silent P333-disable warning

Bundle of three small, independent quality/polish items from the same kimi-k3
3D/flight/controls/navigation/HUD review that produced P404-P408 (report:
`~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`,
findings Q1-Q3). Mirrors the P340 precedent of bundling a review's own small
polish items into one batch. No correctness claim was made for any of them —
all three are presentation/clarity fixes.

Branch: `master`. Committed locally only (Domingo pushes).

---

## Q1 — Throttle gauge must not read "0% WARNING" at idle

**Claim (finding Q1):** `HudRenderer.DrawGauge` painted every gauge's
percentage `frac <= 0.5f ? Warning : Accent`, so an idle throttle (axis −1 →
fraction 0) rendered as "0%" in warning red — the "0% WARNING" look the
2026-09-21/22 review already flagged as wrong. A throttle is an axis position,
not a depletable resource.

**Fix:** `HudRenderer.cs:424-430` — the warning ramp now applies only to
genuinely depletable resources:

```csharp
bool warn = kind != GaugeKind.Thrust && frac <= 0.5f;
_sb.DrawString(_small, pct, ..., warn ? Warning : Accent);
```

`GaugeKind.Thrust` always uses `Accent`. Hull/shield/energy/fuel/ammo keep the
unchanged `frac <= 0.5f` ramp (those are consumables where "low = bad" is
correct). The threshold itself was left untouched, per scope.

**Live proof** (`tools/hud-polish-proof`, offscreen RenderTarget2D, real
production `HudRenderer` + real `GaugeTextures` + real spritefonts):

```
[Q1] Thrust % at idle vs resource % at empty (colour of the '%' text)
    pct region (323,540)-(347,562) red=0 teal=221     <- Thrust, throttle -1 (frac 0)
    pct region (393,473)-(417,495) red=221 teal=0     <- Hull, frac 0
    thrust @ idle 0%  -> TEAL
    hull   @ empty 0% -> RED
  PASS  Q1: Thrust '%' is the ACCENT colour (teal) at idle — NOT warning red
  PASS  Q1: a depletable resource (Hull) at 0% still reads WARNING red (ramp kept)
```

Screenshot of the rendered frame: `tools/hud-polish-proof` output `frame.png`
(the combined Q2 frame); the Q1 per-region pixel classifications above are
sampled from the real rendered buffers.

## Q2 — Leader-command line overlaps the speed readout

**Claim (finding Q2):** `DrawLeaderCommand` drew centred at `(w/2, h−108)`
and `DrawSpeed` drew the big speed numeral at the same `cy = h−108` — the
two anchored to the same point, so a leader's same-side-bot T-selection
prompt overprinted the speed readout.

**Fix:** `HudRenderer.cs:536` — the leader-command line was raised to
`h − 140` (32px above the old anchor), clearing the speed numeral.

**Live proof** (real-font geometry on the actual `_small`/`_big` spritefonts,
plus pixel corroboration on a rendered combined frame with a real leader
command `"DOCK PILOT  [X]"`):

```
    speed numeral: rows [612, 659]
    leader NEW @ h-140: rows [580, 602] clears=True
    leader OLD @ h-108: rows [612, 634] would_overlap=True     <- pre-fix really overlapped
  PASS  Q2: leader-command line (h-140) bottom sits above the speed numeral top (h-108) — no overlap
  PASS  Q2: pre-fix anchor (h-108) really did overlap the speed numeral (contrast holds)
    pixel corrob: leaderBandLit=True speedBandLit=True gapEmpty=True (gap rows 602..611)
  PASS  Q2: on the rendered frame the leader band and speed band are both drawn with an empty gap between them (no overprint)
```

The OLD anchor `h−108` produces rows [612,634] which collide with the speed
numeral's [612,…]; the NEW `h−140` leaves a 19-row empty gap.

## Q3 — Silent P333 ship-scaling disable

**Claim (finding Q3):** `HullLength` returns 0 when the hull catalog is
unavailable, `ScaleForLength` treats that as scale 1 (fallback, unnormalized),
and no log line warns the user.

**Finding on the live tree:** this claim is **already satisfied on `master`** —
the one-line edge-gated warning exists in the catalog-load failure path at
`Game1.Lifecycle.cs:208-209` (immediately after `ShopCatalog.TryLoad`, guarded
by `if (_shopCatalog is null)`; committed, blame `47d9d85b`). It is a single
boot-time statement, so by construction it logs exactly once and never per
frame/lookup, and it names plainly that P333 fell back to unnormalized
rendering. **Therefore Q3 required no production code change** — the requested
fix already landed; the work here was to verify it behaves as specified and
document it (the brief was written against an earlier snapshot).

**Live proof** (drives the real `Rendering.ShopCatalog.TryLoad` through the
exact production guard):

```
[Q3] P333 ship-scaling-DISABLED log gate (real ShopCatalog loader)
[monogame] P333 ship scaling DISABLED — artwork core unavailable, every ship renders at scale 1
  PASS  Q3: unavailable hull-length catalog -> P333 DISABLED warning prints exactly once
  PASS  Q3: catalog loads normally -> no P333 warning printed
```

An intentionally-missing artwork dir makes `ShopCatalog.TryLoad` return null
(reproducing the failure path) and the guard emits the P333 line exactly once;
the real artwork path loads the catalog and emits nothing.

---

## Scope adherence

- Changed only the two named HUD behaviours (Q1 colour rule, Q2 y-anchor).
- Q3 confirmed already-correct in place; no code moved, duplicated, or
  relocated (avoids churn beyond the review's ask).
- The warning-ramp threshold `frac <= 0.5f` for the resource gauges is
  untouched; no other gauge/HUD element was modified.
- P368-P408 untouched.

## Verification summary

- **Whole-solution build** (`dotnet build AllegianceCore.slnx -c Debug`):
  `Build succeeded. 0 Warning(s), 0 Error(s).`
- **Affected-project tests** (`Allegiance.ClientCore.MonoGame.Tests`):
  `Passed: 771, Failed: 0` — the only test project that references the changed
  MonoGame assembly is fully green.
- **Full suite**, run ≥2× (Core ×3, ClientCore ×3, MonoGame ×1, Lobby ×1,
  ClientCore ×1 clean-run attempt): the only failures are the documented /
  recognized **pre-existing live-wire timing flakes** — e.g.
  `LiveTurretGunnerBindingTests.Board_binds_...` (the skill-documented
  persistent machine-state flake), plus rotating live-network tests
  (`LiveMoneyTests`, `LiveRipcordTests`, `AddPartTests`, `JoinFlowLiveTests`,
  `ShipDeleteGoneLiveTests`, `ThreePeerSeededSessionTests`,
  `NpcShipFoundationLiveTests`, `LiveWinConditionTests`, …) that pass when
  re-run in isolation (spot-verified 3/3 green for the sampled ones) and that
  grow/rotate under repeated back-to-back live-server load — the documented
  ephemeral-port/socket-exhaustion behaviour, not a regression.

  **Independence guarantee:** both `Allegiance.Core.Tests` and
  `Allegiance.ClientCore.Tests` reference NO MonoGame project (checked their
  `.csproj` `ProjectReference`s). The sole tracked change here is
  `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs`, which neither
  of those test assemblies depends on — their binaries are byte-identical with
  or without this change, so every failure in them is definitionally unrelated
  to P409.

- **Live proof harness** (`tools/hud-polish-proof`): all three Q-proofs PASS
  (see excerpts above); artifact frame saved under the tool's output dir.

## Out of scope / not touched

- All other gauges/HUD elements; resource-gauge warning threshold; P368-P408.

## Deliverables

- Fixes committed on `master` (not pushed): the two HUD changes (Q1, Q2);
  Q3 confirmed already-present and verified (no delta).
- This report: `P409_HUD_POLISH_BATCH.md` (repo root).
- Proof harness: `tools/hud-polish-proof/` (untracked tool, matching the
  `tools/*-proof` convention of prior stages).
