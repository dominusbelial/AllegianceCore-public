# P376 — Target selection bracket: red for enemy, blue for friendly

## What changed

The player's OWN autopilot-target selection bracket (the closed rectangular
"square thing" from P345, `TargetSelectionStyle` / `TargetSelectionView` /
`HudRenderer.DrawTargetSelectionBracket`) now carries a universal friend/enemy
colour convention, exactly as Domingo asked: an **enemy ship's bracket is RED**,
a **friendly (same-side) ship's bracket is BLUE**, independent of which side the
local player is actually on. Non-ship targets (station / asteroid / warp) have no
side, so they keep the original P345 neutral aqua treatment.

Previously the bracket was two fixed colours regardless of the target's side —
`Selected = (0.28, 0.78, 0.95)` aqua-blue and `Engaged = (0.50, 1.00, 1.00)`
icy-cyan — chosen by `v.Engaged ? TargetSelectionStyle.Engaged :
TargetSelectionStyle.Selected` (`HudRenderer.cs:752`).

## The friend/enemy resolution

The target's real side is resolved at the exact same point (and same lock) that
P359 already resolves the ship for the pilot-name/health readout in
`Game1.ComputeTargetSelectionBracket`: the target `ShipEntity` is already looked
up from `_session.World.Ships`, and its `SideId` is compared against the local
player's own `world.MySideId`. Because this port has no alliance concept (an
established project convention), "friendly" collapses to **same side as me** and
everything else is **enemy** — the identical predicate the contact cue and leader
command already use:

```csharp
isEnemy = !(_session.World.MySideId is { } mySide && targetShip.SideId == mySide);
```

`TargetSelectionView` grew one field — `bool? IsEnemy` — which is `null` for a
non-ship target (keeps the neutral colour) and true/false for an enemy/friendly
ship. The flag is purely derived from server-exported data (the ship's `SideId`,
which P369 made real), so each viewer computes it relative to its OWN side.

## The colour values (and why)

`TargetSelectionStyle.ColorFor(bool engaged, bool? isEnemy)` selects the colour:

| State                              | Value               | Notes |
|------------------------------------|---------------------|-------|
| Neutral selected (non-ship)        | `(0.28, 0.78, 0.95)` | P345 aqua, unchanged |
| Neutral engaged (non-ship)         | `(0.50, 1.00, 1.00)` | P345 icy-cyan, unchanged |
| **Friend selected** (same-side)    | `(0.080, 0.478, 0.936)` | clear blue |
| **Friend engaged** (same-side)     | `(0.494, 0.886, 1.000)` | brighter light blue |
| **Enemy selected** (other-side)    | `(0.922, 0.484, 0.426)` | clear red |
| **Enemy engaged** (other-side)     | `(1.000, 0.594, 0.514)` | brighter light red |

Reasoning:

- **Universal red/blue binary, not P372's per-side palette.** P372 coloured
  radar/station contacts by each side's real roster colour via
  `ClientWorld.TryGetSideColor` (teal-friendly / orange-enemy for stations). This
  brief is explicitly the *simple* binary Domingo asked for — "red colored,
  friendly can be blue … regardless of the team" — NOT a third per-side-colour
  scheme. So I did **not** reuse P372's exact constants; the bracket gets its own
  red/blue pair. This is the deliberate choice per scope item 3: P372's
  teal/orange reads as "station side identity", while red=enemy/blue=friendly is
  the intuitive hostile/friendly code Domingo requested. Keeping them separate
  also avoids coupling the two elements' hues.
- **Enemy-red stays distinct from the missile-lock hot-red.** `LockBracketStyle`
  (out of scope) uses a hot saturated `Full = (1.00, 0.20, 0.10)`. The enemy-red
  here is a softer rose-red and differs from every lock state (grey/amber/red), so
  the two coexisting boxes remain separable by colour as well as by shape (closed
  rectangle vs open corner-L arms) — preserving the P345 anti-aliasing contract.
- **Engaged-vs-selected is preserved within each family.** The engaged variant is
  strictly brighter in every RGB channel than its selected sibling (and
  `ThicknessFor` still thickens the border: 3.2 px engaged vs 2.2 px selected), so
  "flying toward it" reads the same whether the target is a friend or an enemy.

## Verification

### Whole-solution build
`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx` → **0 warnings, 0 errors**.

### Unit tests
`TargetSelectionStyleTests` extended with 8 new P376 assertions (14 total, all
pass): enemy-red dominates in both states, friendly-blue dominates in both states,
non-ship keeps neutral aqua, engaged is strictly brighter within each side family,
and the new side colours are distinct from every missile-lock state.

### Live proof (real 2-side multiplayer session, both viewers)
Extended the P359 live harness (`tools/targetinfo-proof`) to boot a real in-process
server (XXV_simple, 2 teams) with FOUR real `ConnectionSession` peers — two per
side — and render the REAL `HudRenderer` bracket offscreen for each of the four
friend/enemy cases, deriving `isEnemy` exactly as production does
(`viewer.MySideId` vs the target ship's `SideId`). Because P368 spawns each side
in its own home sector, all ships are forced into `mission.Sectors[0]` via the
ShipSpawner injection seam (same reflection the P375 cross-side live test uses) so
opposing ships are mutually visible. Output PNGs in `.verify_p376/`.

Console-confirmed derivation (the flag that picks the colour):

```
sides: alpha viewer=0 target(side1)SideId=1 friendA.SideId=0  |  bravo viewer=1 me(side0)SideId=0 friendB.SideId=1
color-case 'alpha_sees_enemy_red':      viewerSide=0 targetSide=1 => isEnemy=True  (expect RED)
color-case 'alpha_sees_friendly_blue':  viewerSide=0 targetSide=0 => isEnemy=False (expect BLUE)
color-case 'bravo_sees_enemy_red':      viewerSide=1 targetSide=0 => isEnemy=True  (expect RED)
color-case 'bravo_sees_friendly_blue':  viewerSide=1 targetSide=1 => isEnemy=False (expect BLUE)
```

Pixel-sampled bracket-border colours (most-colourful pixel on the box edge):

| Case                                        | Bracket RGB          | Reads as |
|---------------------------------------------|----------------------|----------|
| alpha (side 0) -> enemy (side 1) engaged    | `(255, 151, 131)`    | RED  ✓ |
| alpha (side 0) -> friendly (side 0) engaged | `(125, 225, 255)`    | BLUE ✓ |
| bravo (side 1) -> enemy (side 0) engaged    | `(255, 151, 131)`    | RED  ✓ |
| bravo (side 1) -> friendly (side 1) engaged | `(125, 225, 255)`    | BLUE ✓ |
| enemy selected (for contrast)               | `(235, 123, 108)`    | RED, dimmer ✓ |
| friendly selected (for contrast)            | `( 20, 121, 238)`    | BLUE, dimmer ✓ |

This is the required mirror symmetry: **side A sees side B's ship red and its own
teammate blue; side B sees the exact mirror.** Visual inspection (vision tool) of
the enemy and friendly engaged renders likewise reads the box as "red" and
"cyan/light-blue" respectively. The engaged vs selected pair within each colour
also confirms the brightness/thickness distinction survives the recolouring.

Non-ship regression: the `station_no_readout` render still emits a neutral
(non-red/non-blue) bracket with no name/bar, confirming stations keep the neutral
treatment.

### Full test suite (3 runs)
Ran the full suite three times. Failures in every run are exclusively the
pre-existing documented live-wire/timing flake family (LiveTurretGunnerBindingTests,
Multi_mission_disconnect, ShipsUpdateLiveTests, LiveMoneyTests, LiveCombatWireTests,
LiveRelaunchTests, CargoBayReload/AddPart seats, SeedThreePeers, etc.), with a
DIFFERENT rotating subset each run — the textbook environment-flake signature. Two
of the recurring ones (`SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`
and `LiveTurretGunnerBindingTests.Board_binds_gunner...`) were reproduced failing on
the PRISTINE tree (git stash my P376 files + rebuild) to prove they are not
regressions. The P376 change is client-render-only and touches no network/timing
path; no P376-targeted test fails.
