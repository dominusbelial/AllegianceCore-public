# P804 — client-side lead and aim: native `solveForLead` instead of the server lead point

Base: public master `c190523f` (P811 tip; the dispatch cite `6ef6d9bf` is older), worktree
`allegiance-pi-p804`, branch `pi/p804`, changes left **uncommitted**.
Model: DeepSeek V4.1 Flash (vision on; every screenshot below was opened and read).

Native citations are the checkout at `~/Development/Allegiance/src/`; artwork citations are
`~/Development/Artwork/`. Raw PNGs, full run logs and the fast-tier TRX/logs live in
`~/allegiance-evidence/P804/` (outside the repo, per the evidence-size rule). This report's
JPEGs are in `docs/reports/P804_assets/` (844 KB total; **new media — Domingo's review is
required before the push**, per the public-repo rule).

## Summary

The HUD no longer reads a server-solved lead point. Native solves the lead **on the client**
from IGC state it already holds (`RenderLeadIndicator`, `WinTrek/indicator.cpp:183-405` →
`solveForLead`, `Igc/common.cpp:121-149`); the port now does the same:

* the arithmetic moved to a shared, pure Core helper, **`src/Allegiance.Core/Sim/LeadSolver.cs`**
  (the same code the server's wingman AI runs through `Ship.SolveForLead`), and
* **`src/Allegiance.ClientCore.Game/Rendering/ClientLead.cs`** runs native's gate + solve from
  the client's own ship/weapon/target state and derives the native chase/missile quantities.

The port-only **`LEAD_INDICATOR_UPDATE(210)` server stream (P266) is deleted** — native has no
such message, so nothing was kept "for compatibility". The HUD's four open rows
(ballistic lead marker, aim hit-probability predictor, chase aim without target, radar
viewport/round border) are now native behaviour in **both HUD styles** and are marked `done`.

### What changed, file by file

| Area | Change |
| --- | --- |
| Core | new `Sim/LeadSolver.cs` (pure `solveForLead` over `ShipDodgeLaw.SolveForImpact`, muzzle offset, `AbsoluteF`); `Ship.SolveForLead` delegates to it; removed the P266-only `Ship.SolveLead` / `Ship.LeadHitProbability` |
| Client solve | new `Rendering/ClientLead.cs` — native gate (`:188-218`), in-range (`t ≤ lifespan`), chase tracking point, `bWillHit`/green ramp, cockpit screen-space `g` ramp; `TargetWeaponRange.IsInRange` delegates to it (one implementation) |
| Client gather | `Game1.Camera.cs` — `TryGatherLeadScene` (command target `c_cmdCurrent`, selected-mount weapon, muzzle, target pose/velocity/radius, real `CanSee` for ships), `LeadGranted` (hull bit or friendly station/ship relay + `InScannerRange`), `ComputeLeadIndicators` (projection + ramp), `ComputeChaseAim` (selected weapon, native lifespan) |
| HUD model | `HudLead` gains `Granted`/`Visible`/`TargetVisible`; `LeadMarkerVisible` now needs the native capability gate; `AimPredictorVisible` needs an in-front lead (and in-range in the cockpit); `ChaseAimVisible` is native's no-target/`else` branch |
| Renderers | Classic (`HudRenderer`) and Modern (`ModernHudRenderer`) both consume the same descriptor; the Modern target tether honours `TargetVisible`; Classic's target-now ring is gated too |
| Radar | `RadarBracketLayout.Resolve` + `RadarProjection.Project` — native **eye-space** clamp (`tMin` on camera-space right/up, y-up, `c_flBorderSide = 10`), native round-radar circle test/pin, `RoundRadarRadius = 0.48·(h−20)`; `Game1.Rendering` inset 14 → native 10 |
| Removed | `Core/Wire/LeadIndicatorUpdate.cs`, `ClientCore/State/LeadIndicatorState.cs`, `Server.Broadcast.BroadcastLeadIndicator`/`CanShowLeadIndicator`/`PackLead`/coalesce key 11/egress name/test seam, `ClientWorld` apply/reset/property, `ConnectionSession` dispatch case, `Bot.LeadIndicatorBroadcasts`, and the three P266 test files |
| Tests | new `tests/Allegiance.ClientCore.MonoGame.Tests/P804ClientLeadTests.cs` (37 tests); updated `HudModelTests` lead-gate rows, `RadarBracketLayoutTests` callers, `P722` (relay-lead test deleted), `FedMsgDispatchCoverageTests` counts, `P708ShipUpdateTests` (reflection on the private solve), `hud18-proof` (dropped the removed state), plus the new `tools/p804-proof` real-client harness |

`git diff src/Allegiance.Core src/Allegiance.Server` is no longer empty (both changed), so the
AOT publish was run (below).

## 1. Native → port rule table

| Rule | Native | Port before | Port now |
| --- | --- | --- | --- |
| Who solves the lead | the **client**: `RenderLeadIndicator` reads `GetCommandTarget(c_cmdCurrent)`, the mounted/selected weapon and the local sim's velocities, then `solveForLead` (`indicator.cpp:186-224`, `common.cpp:121-149`) | server solved it per tick and streamed `LEAD_INDICATOR_UPDATE(210)` (P266) because the client was assumed to have no core; the HUD read that message | client solves it with the shared Core `LeadSolver`; the server stream is gone. **Data reaches the client through existing native fields/state, not a new message**: target pose/velocity (`SHIP_STATUS`/`HEAVY_LIGHTS`/`EXPORT`/P797 static exports), the hull+part catalogue, the local command target and the own-shot pose/velocity |
| Shooter / weapon | `pship` = source ship (parent when boarded), `w` = `trekClient.GetWeapon()` = selected mount (`indicator.cpp:194-212`, `clintlib/clintlib.cpp:1585-1588`) | server used its own `Ship.Weapons.First()` | `TryGatherLeadScene` uses `ResolveSelectedWeapon` (selected mount; cached) and the hardpoint muzzle (`MuzzleOffsetsFor`). A boarded gunner still returns no lead — P803 is not merged (TODO cite `indicator.cpp:196-204`) |
| Solve formula | `solveForLead` → `solveForImpact`, muzzle offset, `AbsoluteF`, target radius (`common.cpp:121-149`) | server `Ship.SolveForLead` (P169) | `LeadSolver` — the same function, called by both the server AI and the client HUD (one implementation) |
| Speed / lifespan GAs | `speed · c_gaSpeedAmmo` (ammo weapons, `common.cpp:141`); `GetLifespan` folds `c_gaLifespanEnergy` (`weaponIGC.h:165-171`); the chase `fA` uses the RAW speed | server had no HUD path; the port's chase range used raw speed×lifespan with no GA | `ClientLead` applies exactly those factors; `ComputeChaseAim` uses `pt->GetSpeed() · GetLifespan()` |
| Lead reticle surface + range | if the lead direction transforms, draw `leadinbmp`/`leadoutbmp` by `t ≤ lifespan` (`indicator.cpp:283-330`) | server lead dot, projected as a world point | the solved **direction** is projected (`TryProjectDirection`, native `TransformDirectionToImage`) and the native surface swap is used; the marker is gated by the client-computed capability/relay rule |
| Capability / relay gate | hull `c_habmLeadIndicator = 0x800`, else a same-side relay station (`c_sabmRemoteLeadIndicator`) or unboarded ship (`c_habmRemoteLeadIndicator`) with the target in `InScannerRange` (`indicator.cpp:286-320`) | server `CanShowLeadIndicator` | `LeadGranted` on the client: hull catalogue bits, station/ship relay bits, `TargetVisibility.InScannerRange` (the same native scanner rules, incl. LOS over the sector's asteroids); same-side only, parented ships skipped |
| Cockpit hit-probability `g` | screen-space: `l` = px from the view centre to the projected lead, `r` = `GetScreenRadius()`, `f = l²/r²`, `g = f<1 ? 1−0.75f : f<2 ? 0.5−f/4 : 0` (`indicator.cpp:334-357`) | server's **angular** approximation (`Ship.LeadHitProbability`) | `ClientLead.CockpitGreen` with the real screen distance and `RadarScreenRadius.Project` (native 7 px floor); angular approximation deleted |
| Chase/missile aim point | `trackingPosition = shipPosition + forward · |target − ship|`, projected from the camera (`indicator.cpp:246-265`) | the server lead point reused | `ClientLead.TrackingPoint` + `TryProjectDirection` |
| Chase/missile hit ramp | `fA = t·speed`, `fH = fA / (leadDir · forward)`, `bWillHit = fH²−fA² ≤ r²`, `green = bWillHit ? 1−(fH²−fA²)/r² : 0` (`indicator.cpp:256-276`) | server green | `ClientLead.Solve` reproduces the same float sequence |
| No-target / gate-failed chase marker | `if (pWeapon)` + `!GetParentShip()`: grey `centeroutbmp` at `shipPos + forward · speed·lifespan` (`indicator.cpp:367-405`) | needed the first weapon of the group-fire selection, no GA lifespan | `ComputeChaseAim` uses the selected mount (`GetWeapon`) and native `GetLifespan`; the same grey marker fires when a target exists but the weapon/side gate fails (`else if`, native semantics) |
| Radar off-screen clamp | `TransformLocalToEye(positionModel)` then clamp x/y to the image rect by `tMin = min(XMax/x, YMax/y)`; y-up; `rectImage.Expand(-c_flBorderSide = 10)` (`WinTrek.cpp:7700-7726,7882-7921`); round radar pins to the inscribed circle | NDC-direction normalisation (tan-space, dominant-axis) with a 14 px inset | native eye-space `tMin` clamp, y-up, inset 10; round-radar circle test/pin |
| Round radar border | 60 segments, colour `(0.5,0.5,0.5,0.25)`, radius `(YMax−YMin)·0.48` = `0.48·(h−20)` (`radarimage.cpp:155-199,1130-1177`) | already drawn in both skins | unchanged art, now sharing `RadarBracketLayout.NativeBorderSide`/`RoundRadarRadius` with the clamp (tested) |

### Classification

* **Native client-side solve — design.** The server never solved lead for players; only the AI
  used `solveForLead` (`Igc/shipIGC.cpp:2464,2617`). Sharing one implementation is also design.
* **P266 server stream — era constraint (retired).** The client was assumed to lack the static
  core/target wire data; P786 gave it the hull/part catalogue and the target pose/velocity, so
  the workaround is gone. This is the classification the port matrix rows called a
  "BEHAVIOUR GAP".
* **Angular `g` approximation and the NDC radar clamp — port bugs/deviations, fixed.** Both were
  documented approximations; native's screen-space ramp and eye-space clamp are cheap and now
  exact.
* **Round radar — design (optional preference, default off, `WinTrek.cpp:2715`).** No modern
  replacement proposed; the native circle is the feature.
* **Modern-better-approach note.** The ramp ignores weapon dispersion (native never folds the
  part's `Dispersion` into the hit probability); a modern HUD could add a dispersion-aware hit
  cone. Out of scope here (weapon stats are explicitly out of scope).

## 2. Both HUD styles

Behaviour lives in the shared data path (`Game1` computes one `LeadIndicatorView` → `HudLead` →
`HudModel` flags). Per row:

| Row | Classic draw | Modern draw |
| --- | --- | --- |
| Ballistic lead marker | `HudRenderer.DrawLeadIndicator` (leadinbmp/leadoutbmp at `LeadCenter`; procedural diamond only without art), gated by `HudModel.LeadMarkerVisible` | `ModernHudRenderer.DrawLeadIndicator` (ring + target tether; `TargetVisible` gates the tether), same gate |
| Aim hit-probability predictor | `HudRenderer.DrawCentreIndicatorsAdditive` (cockpit `centeroutbmp` at the view centre with `(1−g,g,0)`, or the chase aim point; grey when out of range) + `DrawAimPredictor` fallback | `ModernHudRenderer.DrawAimPredictor` (ring + ticks at the centre/aim point, colour from `Green`/`InRange`) |
| Chase aim without target | `HudRenderer.DrawCentreIndicatorsAdditive` `hasChase` + `DrawChaseAim` fallback | `ModernHudRenderer.DrawChaseAim` |
| Radar viewport + round border | `RadarBracketRenderer.Draw(HudModel…)` → `DrawRoundBorder` then contacts | same `RadarBracketRenderer` called from `ModernHudRenderer.Draw` |

A HUD row is only `done` because the shared model gates are identical for both renderers and
the screenshots below show both skins; Modern's glyphs differ (allowed: visuals may differ).

## 3. Frame cost / allocation

* Pure solve, **Release** (`tools/p804-proof`, per-call loop, no camera work):
  **190–219 ns** per `ClientLead.Solve` across the four capture runs
  (`~/allegiance-evidence/P804/raw/final-*/…log`: `[p804] frame cost: 199 ns …`; machine shared
  with other agents, earlier runs measured 25–35 ns). The unit test (Debug) prints ~0.38 µs.
* **Zero allocation** on the frame path: `P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame`
  asserts `GC.GetAllocatedBytesForCurrentThread()` returns **0** over 1000 warm frames of solve +
  ramp. The gather path is value types + cached weapon spec + struct dictionary enumerators; no
  LINQ/reflection on the frame path (`HudSnapshotBuffers` discipline untouched).
* Per frame the client does **one** solve (one `sqrt` inside `solveForImpact`), one projection and
  a couple of dictionary lookups — a rounding error against a frame. The per-tick owner-only
  server broadcast it replaces (40-byte message per peer per tick) is gone.

## 4. Tests

New file `tests/Allegiance.ClientCore.MonoGame.Tests/P804ClientLeadTests.cs`, **37 tests**:

* client lead == the Core solve on fixed scenes (stationary, laterally moving, receding,
  behind/left); the lead genuinely intercepts (|ΔP + t·ΔV| = t·speed + radius);
* stationary target → zero lead, `t = 1 s`, `WillHit`/green 1; out-of-range boundary (`t ==
  lifespan` in range, `t > lifespan` not); no solution (speed 0 → native sentinel, never in
  range/hit); `AbsoluteF` ignores the shooter's motion; the **muzzle offset is rotated** by the
  ship orientation (and the solve changes without it);
* the native gates (cluster, CanSee, weapon, healing-vs-damaging vs side, chase bypass) and the
  GA factors (`c_gaSpeedAmmo`, `c_gaLifespanEnergy`);
* chase aim = forward projection at target range; the `fHA²` hit ramp (miss/side, big-radius hit
  in `(0,1)`, dead-ahead pure green);
* cockpit `g` ramp golden values (f = 0, 0.25, 1, 1.5, 2, 4) and the zero-radius red end;
* both `HudStyle` values consume the same descriptor (granted/ungranted split, cockpit/chase,
  grey fallback, `Visible=false` suppression) + per-style source contracts (Classic gates in
  `HudRenderer.Draw`, Modern overrides all three indicator draws and the tether);
* radar: native `tMin` clamp, scale invariance, edge-not-corner landing, behind-eye signs, the
  zero-lateral midpoint, round radius `0.48·(h−20)`, the round circle for on-screen/off-screen/
  behind-eye, round-border colour/segments and both-skin routing;
* zero-allocation + frame-cost measurement.

**Which fail on master.** The new file cannot even compile on master (the `LeadSolver`,
`ClientLead`, `Granted`/`Visible` fields do not exist) — master's behaviour is exactly what the
tests reject. A minimal executable red probe was run against a pristine `git archive HEAD` copy:
the same native-clamp call `Resolve(true, 3, 1.5, 2, 1, 900, 640, 10)` lands at **y = 165** on
master (NDC normalisation) vs the native **y = 100** asserted here — see
`~/allegiance-evidence/P804/redcheck.log` and `P804RedRadarTests.cs` (1 failed / 1 passed; the
zero-lateral case happens to coincide on master and is noted there). Every positive assertion in
the new file pins behaviour master lacks: the server lead point instead of the client solve, no
capability gate on the marker, the angular `g`, and the NDC clamp.

Updated tests: `HudModelTests` lead-gate theory (now `Granted`-aware, plus a behind-camera row);
`RadarBracketLayoutTests` callers; `P722InterfaceLoopbackTests` relay-lead test deleted with the
message; `FedMsgDispatchCoverageTests` counts (112 consumed, 13 port-added); `P708ShipUpdateTests`
uses the private solve via reflection; `hud18-proof` no longer prints the removed state.

## 5. Verification (light gate)

| Check | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | **74/74 tool projects built** (exit 0) |
| AOT publish (`Server`, linux-x64, self-contained, `PublishAot`) | **exit 0**, no `IL2026`/`IL3050` |
| New + affected MonoGame tests alone **3×** | 127 passed each run (37 new + HudModel/Radar/Hud18) |
| Affected Core tests alone **3×** | 48 passed each run (P708, NpcWingmanLead, BroadcastSnapshotLoopback) |
| Affected ClientCore tests alone **3×** | 16 passed each run (P722 + FedMsgDispatchCoverage) |
| `tools/run-tests.sh --tier fast` (one) | **PASS — 10 208 tests, 288 s** (`~/allegiance-evidence/P804/fast-tier.log`, TRX under `tier-logs/`) |
| `scripts/parity-scorecard.py` | validated 670 + 473 rows; hud **80.73% (88/109 complete)**, TOTAL **94.05%** |
| Glossary link check | 96 paths OK |

Pre-existing environment failures, **not** P804: `P699HumanPodHomeLiveTests` (2) and
`P540bPodAutopilotCrossSectorLiveTests` (1) fail 3/3 alone in this worktree **and identically on
a pristine `git archive HEAD` build** in this sandbox (checked before deleting `.scratch/base`);
`P800HullProofGpuTests` initially failed only because the proof-tool binaries were not built —
green 3/3 after `tools/build-tools.sh`.

## 6. Real-client screenshots (1920×1080, both skins)

Captured with the new `tools/p804-proof` (real window, real local `Allegiance.Server`,
`m2scatter.igc`, the production autoshot backbuffer path, one run per skin): the own hull is
forced to **Adv Stl Fighter (hull 17, `c_habmLeadIndicator` set)** with **EW Pulse Laser**
(projectile 66: 1250 m/s, 0.5 s), and a real engine-ticked enemy ship 450 m ahead drifts laterally
at 140 m/s. The run logs print the resolved descriptors
(`~/allegiance-evidence/P804/raw/…log`), e.g. the clean-geometry capture:
`leadPx=(769,538) targetPx=(888,540) granted=True inRange=True green=0.00` (Classic) and
`leadPx=(785,538) targetPx=(906,540)` (Modern) — a real **~120 px lateral lead**, not the target.

| Shot | Asset | What is actually visible (read with the image reader) |
| --- | --- | --- |
| (a) cockpit lead — Classic | `p804-a-cockpit-lead-classic.jpg`, `zoom-a-lead-and-ramp-classic.jpg` | Cockpit HUD, target pane `Ship 8097: Adv Scout … RANGE: 451`. The client-solved lead marker (native `leadinbmp`, green four-lobed surface) sits at the projected lead point, ~120 px LEFT of the target's own marker/bracket; the view-centre `centeroutbmp` crosshair is **red** (aim ~2× target screen radius off the lead → native `g = 0`). The zoom shows lead marker, target-now ring, and the red centre crosshair together. |
| (a) cockpit lead — Modern | `p804-a-cockpit-lead-modern.jpg`, `zoom-a-lead-and-ramp-modern.jpg` | Modern skin: the lead glyph is the Modern orange ring at the same lead point, with its **tether line** to the target ring ~120 px right; the Modern aim glyph (orange ring + four ticks) at the view centre. `451 m`, target pane and radar are Modern-composed. |
| (b) chase lead aim — Classic | `p804-b-chase-lead-aim-classic.jpg` | Chase camera over the own hull; the target bracket at the enemy and the **red `centeroutbmp` ring at the forward-projected aim point**, to the right of the target (native `shipPos + forward·targetRange`), in range but `bWillHit` false → native red end. |
| (b) chase lead aim — Modern | `p804-b-chase-lead-aim-modern.jpg` | Same geometry in Modern: the target bracket at 451 m and the Modern aim ring + ticks at the projected aim point right of it. |
| (c) chase, no target — Classic | `p804-c-chase-no-target-classic.jpg` | The command target was cleared; native's no-target branch draws the **grey** `centeroutbmp` ring at the weapon's forward range point (view-centre-ish), no lead marker, no target pane text. |
| (c) chase, no target — Modern | `p804-c-chase-no-target-modern.jpg` | The Modern grey ring + ticks at the same forward-range point. |
| (d) round-radar clamp — Classic | `p804-d-round-radar-clamp-classic.jpg` | Round radar ON: the native border circle (`0.48·(1080−20) ≈ 509 px`) is drawn; after turning the ship 180° the selected enemy is **behind the camera**, no lead marker is drawn (native `TransformDirectionToImage` fails), and its radar blip `Ship 8097 402 m` is **pinned on the circle's bottom edge** — the native eye-space/circle clamp. |
| (d) round-radar clamp — Modern | `p804-d-round-radar-clamp-modern.jpg` | Same circle + blip pinned to the circle's lower edge in the Modern skin. |

## 7. Parity rows updated (`docs/parity/parity-matrix.csv`, LF kept, scorecard validates)

* **Ballistic lead marker** — `partial` → **`done`** (client solve, direction projection,
  leadin/leadout swap, client-side capability/relay gate, server stream retired; both skins;
  `P804ClientLeadTests` + screenshots).
* **Aim hit-probability predictor** — `partial` → **`done`** (native screen-space `g` in the
  cockpit, native forward-projected aim + `fHA²` ramp in chase/missile, in-range/grey rules; both
  skins).
* **Chase weapon aim without target** — `partial` → **`done`** (selected-mount weapon,
  `speed·GetLifespan`, grey marker, native no-weapon/gunner cases; both skins).
* **Radar viewport and round border** — `partial` → **`done`** (eye-space `tMin` clamp with the
  native 10 px inset, round-radar circle/pin, border radius/colour in both skins; tests +
  screenshots).

## 8. Left unfinished / documented limits

* **Turret gunner (P803, out of scope).** Native solves with the parent ship + the turret mount
  (`indicator.cpp:196-204`); the client keeps only `ParentId`, so a boarded player gets **no**
  lead/aim or in-range flag (the pre-existing HUD-18b behaviour, TODO left in
  `TryGatherLeadScene`). When P803 lands, pass the parent pose/orientation and the turret mount's
  weapon/muzzle into the same `ClientLead` input.
* **Static-target `CanSee`.** Ship targets use the real client scanner gate
  (`TargetVisibility.CanSeeShip`, P786); station/asteroid/warp targets keep the same-cluster
  exported-model stand-in (P786/HUD-18 documented approximation) for the lead gate.
* **Round-radar `distanceToEdge`.** Native computes the distance to the circle for on-screen
  round-radar blips (`WinTrek.cpp:7930-7940`); the port still uses the rectangular distance
  (`RadarScreenRadius.ForContact`), which only affects bracket clipping, not placement. The
  round *clamp* itself is native.
* **Screenshot geometry varies with the cockpit camera attitude.** The proof harness pins the
  scene and logs the projected points; the saved pair is from a run where the lead offset was
  clearly visible (~120 px). A future automated pass could freeze the camera pose through the
  debug capture seam for fully deterministic framing.
* The parity row for **Target in weapon range** still lists the gunner gap (P803); it was not in
  this wave's open rows and is unchanged.
