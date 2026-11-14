# P1016 — Loadout V2 hardpoint highlights

Slot hover and existing keyboard focus now mark the selected weapon mount on
Loadout V2's rotating hull. Turrets also show a faint direction guide and the
seated gunner's name. Both HUD styles use their existing palettes. The feature
stays behind `visuals.hangar.loadoutV2`, default off.

The report was drafted before implementation/gate. Base: `30e17489`.
No commits or pushes; the brief is in ignored `.scratch/p1016/`.

## Finding, native evidence and root cause

The original `Game1.LoadoutV2.cs:90-97` drew the preview and the overlay
independently. `DrawDockedHullPreview` (`Game1.Rendering.cs:5399`) kept its
camera matrices local, and `LoadoutV2Renderer.Draw` had no projected mount data.
The real CVH frame positions already existed in `HullWeaponPositions.Compute`.

Re-grepped/read in the mounted native reference before citation:

- `WinTrek/load.cpp:1321-1356,1370,1377`: **both UpdateCone and its hover
  callbacks are commented out**. Its dead code used `acos(hd.minDot)`, position
  and orientation for non-fixed weapon mounts.
- `Igc/igc.h:1696-1708`: **minDot itself is commented out**. PCore001's 30-byte
  hardpoint records have sounds, frame/abbreviation, mask and fixed flag, with
  no serialized firing-angle limit. Padding must not be interpreted as minDot.
- `Igc/hullTypeIGC.cpp:89-136`: named CVH frames provide weapon positions
  and turret orientations. Position is multiplied by hull scale; absent frames
  fall back to the simulation's origin. Highlighting deliberately omits these
  absent frames instead of suggesting an invented mount at the origin.
- `_Utility/HitTest.cpp:797-801`: frame position X is negated; direction Y/Z
  are negated. The shared port positions already apply this bridge.
- `WinTrek/load.cpp:176-205`: turret rows show their gunner. P1016 reuses the
  P1014 occupant lookup, including its relative-seat mapping between hulls.
- `WinTrek/load.cpp:863-887,919-934`: the preview's independent camera,
  framing, Y spin and X tilt remain the existing implementation.

Parity: updated `P1014-002` in `docs/parity/p1014-loadout-renderer.csv` and
added `docs/parity/p1016-hardpoints.csv`. The dead-cone row is `na`; resurrecting
a presentation idea earns no native behavior parity credit.

## Fix and limits

`Rendering/LoadoutV2Hardpoints.cs` contains only CPU geometry and MonoGame math
values, with no GraphicsDevice. It uses `HullWeaponPositions.Compute`, checks
frame presence separately (a real origin frame is valid), and projects through
the preview's actual world/view/projection matrices into its screen rectangle.
Clipped/behind-camera points are rejected. A camera-to-mount ray through the
CVH convex sub-hulls identifies obscured mounts; their rings/cones remain
visible at 40% intensity. This is collision-geometry occlusion, not render-mesh
pixel depth: CVH envelopes can dim a mount on a visible mesh protrusion.

`Game1.Rendering.cs:5399` now returns the camera it used; draw calls and their
order are unchanged. The native-art path works directly in hull units. The
existing Meshy fallback normalizes those hull units to its radius-one preview;
its replacement mesh may differ from native CVH geometry. Missing CVH produces
no marker. `Game1.LoadoutV2.cs:99-123` consumes the existing HitTest hover and
presentation SlotFocus/OpenSlot. Hover takes precedence, including equipment
rows. An open part picker retains its source slot. Geometry is cached per hull
and invalidated when the static core changes. No input handling was added.

The renderer draws a ring, short cone/axis guide and fitted marker label inside
a preview scissor rectangle, then restores the scissor/rasterizer state. The
scissor rasterizer belongs to this renderer/device and is disposed by
Game1.UnloadContent (covered by the existing disposal guard). Gunner
names come from the shared slot model. Non-weapon sockets (shield, afterburner,
cloak, launchers, cargo) show nothing: there is no native frame that would
justify a marker, and a hull-wide outline would suggest a location we do not
know.

**Firing-arc qualification:** a true minDot firing boundary is unavailable in
this native format. PCore001 therefore shows **“Aim guide / arc limit
unavailable”**. The cone's size is decorative; only its position and direction
are authored data. The pure projection API supports a supplied minDot and its
acos-equivalent axial/radial geometry is tested, but production never invents
one. No simulation aiming, buy, mount, price or wire rule changed.

## Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/P1016HardpointTests.cs` adds 12 cases:
real Fighter/Bomber/Cruiser frames versus independently calculated screen
coordinates; no CVH/no frames/non-weapon slots; an actual missing Bomber frame
(hull 124, mount 1); fixed/turret/gunner selection; hover/focus/open-picker
precedence without shop mutation; homogeneous camera clipping; frame direction
and optional minDot math; occlusion across orbit angles; a valid origin frame.
The corpus is loaded only after Require.Artwork, so absent artwork skips cleanly.

Counts below are Passed / Failed / Skipped:

| Check | Result |
| --- | --- |
| New P1016 cases, each isolated repetition | **12 / 0 / 0**, three times |
| Existing P1014 cases, same repetitions (includes both styles' default-off routing) | **13 / 0 / 0**, three times |
| Existing disposal guard (updated for the owned preview scissor), each repetition | **1 / 0 / 0**, three times |
| Combined final isolated total | **78 / 0 / 0** |
| P1016 without artwork | **1 / 0 / 11** |
| Real-window matrix, seven V2 fixtures and two flag-off fixtures | **66 / 0 / 0** |
| Final Bomber reruns / clean unload, both styles | **16 / 0 / 0** |

## Real-window proof and reviewed media

Extended `tools/p1014-ui-proof` with `--hardpoints=fighter|bomber|capital` and
`--extras`; the README documents reproduction. It runs production Game1 against
a local loopback server. Display fixtures use authentic PCore001 hulls/default
parts in ShopModel; they do not buy an otherwise locked hull. A passenger enters
the client through production EXPORT/LOADOUT_CHANGE handlers. Fighter checks
existing slot focus; Bomber/Cruiser hover actual layout rows. Extra checks turn
the orbit and hover a shield, which has no marker.

All seven requested V2 scenes passed and were individually viewed. After the
resource-ownership change, Classic and Modern Bomber were rerun successfully,
including clean Game1 shutdown; both final images were viewed and are
byte-identical to the retained corresponding captures. Retained
PNGs (eight total, 2,730,629 bytes) are in `P1016_assets/`:

| Scene | Classic | Modern |
| --- | --- | --- |
| Fighter fixed weapon, 1920×1080 | [capture](P1016_assets/classic-1920x1080-hardpoint-fighter.png) | [capture](P1016_assets/modern-1920x1080-hardpoint-fighter.png) |
| Bomber turret / seated name / guide, 1920×1080 | [capture](P1016_assets/classic-1920x1080-hardpoint-bomber.png) | [capture](P1016_assets/modern-1920x1080-hardpoint-bomber.png) |
| Cruiser / four turret rows / dim mount, 1920×1080 | [capture](P1016_assets/classic-1920x1080-hardpoint-capital.png) | [capture](P1016_assets/modern-1920x1080-hardpoint-capital.png) |
| Bomber turret, 1280×720 | — | [capture](P1016_assets/modern-1280x720-hardpoint-bomber.png) |
| Legacy flag-off, 1920×1080 | [capture](P1016_assets/classic-1920x1080-off.png) | identical pixels; extra in scratch |

The seven selected scenes show the real mount ring, contrasting palettes,
readable seated name/guide, and no marker spill outside the preview. Existing
stats truncation at 1080p and the legacy covering stats pane remain P1014/legacy
layout behavior. Some image-viewer presentations omitted glyphs or overlay
runs, as recorded previously in P1019; no broad renderer/encoder change is made
here. The retained PNG bytes pass CRC/decompression checks, and the flag-off
comparison below verifies full decoded RGBA equality rather than relying on
visual similarity. Extra opposite-orbit/shield images and both baseline pairs
were also viewed and remain in scratch.

Baseline Classic/Modern captures were made before production edits using the
original P1014 proof. Final captures use the identical --off fixture and fixed
orbit. Both style comparisons changed **0 / 2,073,600 pixels**; all four PNGs
are byte-identical, SHA-256:

`0552fa08e25d84cdb7c3d731d4d16492e30421023ce62228614380bb6486a64a`.

Local artifacts: `.scratch/p1016/off-comparison.json`, `baseline/`, `captures/`,
`tests/`, and build/test logs. No images were composed or retouched. Reviewed
media contains native corpus ships, generic fixture names and no private data.

## Missing-frame audit

PCore001 has **447 hull records, 762 weapon mounts, 83 missing frames across 49
hulls**. All mounts below intentionally get no marker. IDs/mounts in this audit
are diagnostics only; player labels remain names. Mount indices are zero based.
The pictured Fighter/Bomber/Cruiser variants have complete frames.

| Model | Hull ID / name | Missing mount: frame |
| --- | --- | --- |
| `bom05b` | 290 / Gunship | 0: wepemt |
| `utl18a` | 34 / Corvette | 2: turwepemt1; 3: turwepemt2 |
| `cap502` | 78 / Attack Carrier [SR] | 0: wepemt; 1: turwepemt1; 2: turwepemt2 |
| `cap20` | 291 / Destroyer | 0: lwepemt1 |
| `bom20` | 390 / Gunship | 0: wepemt |
| `cap103` | 35 / Corvette | 2: turwepemt1; 3: turwepemt2 |
| `cap103` | 387 / Assault Ship | 0: wepemt |
| `cap503` | 79 / Attack Carrier [SR] | 0: wepemt; 1: turwepemt1; 2: turwepemt2 |
| `cap101` | 391 / Destroyer | 0: wepemt |
| `wc_icbmb` | 490 / Gunship | 0: wepemt |
| `cap500` | 80 / Attack Carrier [SR] | 0: wepemt; 1: turwepemt1; 2: turwepemt2 |
| `cap500` | 827 / Attack Carrier [SR] | 0: wepemt; 1: turwepemt1; 2: turwepemt2 |
| `cap04` | 487 / Assault Ship | 0: wepemt |
| `bom30` | 590 / Gunship | 0: wepemt |
| `cap303` | 37 / Corvette | 2: turwepemt1; 3: turwepemt2 |
| `cap303` | 587 / Assault Ship | 0: wepemt |
| `cap501` | 81 / Attack Carrier [SR] | 0: wepemt; 1: turwepemt1; 2: turwepemt2 |
| `fig25` | 686 / Adv Stl Fighter | 0: lwepemt1; 1: rwepemt1 |
| `fig25` | 611 / Stealth Fighter | 0: lwepemt1; 1: rwepemt1 |
| `fig03` | 681 / Hvy Interceptor | 0: wepemt |
| `bom04` | 690 / Gunship | 0: wepemt |
| `cap504` | 82 / Attack Carrier [SR] | 0: wepemt; 1: turwepemt1; 2: turwepemt2 |
| `drgas` | 50 / Corvette | 0: lwepemt2; 1: rwepemt2 |
| `drgas` | 32 / Assault Ship | 0: wepemt |
| `dcrus` | 28 / Destroyer | 0: lwepemt1 |
| `dn_fateshbmb` | 66 / Gunship | 0: wepemt |
| `tfsb2` | 68 / Stealth Bomber | 0: wepemt |
| `raincarr` | 84 / Attack Carrier [SR] | 0: wepemt; 1: turwepemt1; 2: turwepemt2 |
| `tfflagship` | 76 / Flagship.1 [AtFr] | 4: turwepemt3 |
| `tfflagship` | 75 / Flagship.2 [AtDv] | 4: turwepemt3; 5: turwepemt4 |
| `tfflagship` | 74 / Flagship.3 [AtCr] | 4: turwepemt3; 5: turwepemt4 |
| `tfflagship` | 73 / Flagship.4 [AsCr] | 6: turwepemt3; 7: turwepemt4 |
| `wc_gtsf` | 132 / Adv Stl Fighter | 1: rwepemt |
| `wc_gtsf` | 131 / Stealth Fighter | 1: rwepemt |
| `wc_gtsf` | 133 / Stealth Bomber | 1: rwepemt |
| `wc_gtsf` | 155 / Stealth Bomber [NG] | 1: rwepemt |
| `wc_gtpt` | 128 / PT Bomber | 1: turwepemt |
| `wc_gtstang` | 129 / Mustang | 1: (empty) |
| `bom400` | 125 / Hvy Bomber | 1: rwepemt |
| `bom400` | 124 / Bomber | 1: rwepemt |
| `cap401` | 147 / Assault Carrier | 1: turwepemt1; 2: turwepemt2 |
| `cap401` | 118 / Attack Carrier [SR] | 1: turwepemt1; 2: turwepemt2 |
| `cap400` | 141 / Assault Ship [N] | 1: lwepemt; 2: rwepemt |
| `cap400` | 140 / Assault Ship | 1: lwepemt; 2: rwepemt |
| `cap406` | 146 / Frigate | 1: turwepemt1; 2: turwepemt2; 3: turwepemt3 |
| `cap405` | 144 / Devastator | 1: turwepemt1; 2: turwepemt2; 3: turwepemt3; 4: turwepemt4 |
| `dn_faphshcar` | 213 / Attack Carrier [SR] | 0: wepemt |
| `faohshcar` | 727 / Attack Carrier [SR] | 0: wepemt; 1: turwepemt1; 2: turwepemt2 |
| `bom07b` | 890 / Gunship | 0: wepemt |


## Gate

| Check | Result |
| --- | --- |
| Final solution build | **0 warnings / 0 errors**, 17.98 s (initial build: 28.08 s) |
| Tools build | **140/140 projects, 0 warnings / 0 errors**, final recheck passed |
| Final new/related tests, isolated ×3 | **26/0/0 each**, **78/0/0 total** |
| Single full Fast sweep, 7,200 s bound | **14,370/8/7**, 14,385 cases, 560.1 s; **FAIL** |
| New/related tests within that sweep | **25/0/0**, P1016 maximum case 0.395 s |
| Fast infrastructure | **9/0/0**, 0.034 s |
| Failure/slow-method isolated reproductions | **10 methods ×3**, **36/3/0** |
| Glossary / affected parity CSVs / whitespace | Pass |

The full sweep's calibrated case limit was 11.497 s. Seven assertion failures
were a verification setup error: the public-network blocking proxy had no
localhost exemption, so six updater loopback methods and one benchmark HTTP
method hit connection-refused at the proxy instead of their own local server.
This was my runner environment error, not an application regression or flake.
With NO_PROXY/no_proxy=`127.0.0.1,localhost,::1`, all seven passed three times
alone. The external proxy stayed blocked.

The other failure is the already-ledgered LiveSideSelect auto-seat receipt/clock
boundary (31.95 simulated seconds versus [28,31]); it also failed alone at
32.40, 31.70 and 32.45. SC2NaniteSupport and P796VariedLoadout each had one
sweep timing violation; their entire theory methods passed ×3 in isolation,
maximum case 5.601 and 6.566 s respectively. No unrelated source, test threshold,
quarantine or timing limit was changed. Full outcomes and each case's isolated
results are in [qualification.json](P1016_assets/qualification.json) and the
P1016 entry of [TEST_FLAKES.md](../TEST_FLAKES.md). The one full sweep remains
red; it was not repeated. It preceded a final native-comment citation correction,
reuse of the existing ASCII-safe helper before measuring marker labels,
and ownership/disposal of the preview scissor state;
the solution, tools and isolated loadout checks were rechecked on that final
source. ASCII fixture screenshots are unaffected.

| Method | Sweep trigger | Each isolated run (P/F/S) | Maximum isolated case |
| --- | --- | --- | --- |
| `BS1BenchmarkSharingTests.Real_loopback_HTTP_transport_uploads_exact_preview` | assertion | **1/0/0 ×3** | 0.069 s |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | timing | **3/0/0 ×3** | 6.566 s |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | timing | **2/0/0 ×3** | 5.601 s |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | assertion | **0/1/0 ×3** | 1.015 s |
| `UpdateClientLoopbackTests.A_stale_pending_update_survives_a_failed_later_check` | assertion | **1/0/0 ×3** | 0.266 s |
| `UpdateClientLoopbackTests.A_tampered_manifest_is_refused` | assertion | **1/0/0 ×3** | 0.094 s |
| `UpdateClientLoopbackTests.A_tampered_package_is_refused_and_nothing_is_staged` | assertion | **1/0/0 ×3** | 0.135 s |
| `UpdateClientLoopbackTests.A_version_that_fails_the_apply_cap_is_not_staged_again_until_a_newer_one` | assertion | **1/0/0 ×3** | 0.610 s |
| `UpdateClientLoopbackTests.Same_version_is_up_to_date_and_touches_nothing` | assertion | **1/0/0 ×3** | 0.046 s |
| `UpdateClientLoopbackTests.V1_to_v2_detects_downloads_deltas_verifies_and_applies_on_restart` | assertion | **1/0/0 ×3** | 0.427 s |

Builds use --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false.
The default NuGet HTTP cache is read-only in this lane. A writable copy in task
scratch supplies the existing cached vulnerability metadata, with audit still
enabled; outbound build requests are blocked with a loopback-only proxy. No
remote machine/public host was used for task research or proof. No service,
store, port or application env-var was added, so no compose change is needed.

## Hand-offs

- P1015: keep populating LoadoutV2Presentation.SlotFocus/OpenSlot; the marker
  consumes those and layout hover without any new input handler. No P1015 key,
  controller or command-screen routing was added.
- A future core format would need an actual per-turret limit before presenting
  a firing boundary. PCore001 cannot supply minDot; the aim-guide qualification
  is intentional, not a zero-angle or hemisphere gameplay assumption.
- Missing authored frames above require artwork/core work if markers are wanted
  at those mounts. CVH collision envelopes may conservatively dim visible mesh
  details. Native art was the reviewed screenshot path; replacement art has its
  existing geometric differences.

## Native intent

Native has no working hardpoint highlight: UpdateCone and its hover callbacks
are commented out, and HardpointData no longer contains minDot. This is a
presentation-only addition on the V2 path, default off. Buy/mount rules remain
in ShopModel. Flag-off captures are pixel-identical in Classic and Modern.

## Modern approach

Project authentic hardpoint frames through the exact preview camera and orbit;
connect existing slot hover/focus to a ring, dim obscured mounts without losing
their location, and show the seated gunner in both HUD palettes. Label the
unavailable firing boundary honestly instead of inventing a core rule.
