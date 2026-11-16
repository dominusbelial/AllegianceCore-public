# P1044 — A real 3-D cockpit for AllegianceCore

Investigation and disposable local spike, 2026-11-16. No game feature ships in this change.

## Recommendation

A cockpit is feasible as client presentation. The recommended route is a hybrid: a separately rendered interior with live console displays, while world-registered targeting and urgent information remain sharp, unobstructed overlays. Hiding Classic alone would remove information without supplying its replacement. Classic and Modern must remain available.

This report was assembled from the checked-out implementation and installed native source/artwork. All source paths below are repository-relative, or native-relative under `Allegiance/src`, or artwork-relative under `Artwork`. Unqualified `Game1.*`, `Rendering/*` and skin filenames in the current-state discussion are under `src/Allegiance.ClientCore.Game/`; named skin files are in `Rendering/Skins/`. No remote machines or public websites are part of the investigation.

## Current state

The historical P211 and P241 documents actually live at `docs/CLIENTCORE_MONOGAME_COCKPIT_HUD_P211.md` and `docs/COCKPIT_HUD_REDESIGN_P241.md`, rather than the brief’s `docs/reports/` paths. Their historical projection values are superseded by current code. The skin design reference is `docs/reports/UI_SKINS_P972.md`.

The parity inventory also changed: current `docs/parity/parity-matrix.csv` has **112** rows with `area=hud`, not 111. The mapping in this report’s assets covers all 112, including the class contract marked n/a, the game-list boundary, and command-view summary. No parity status is changed by this study.


The current first-person eye is `shipPosition + forward * 14`, with ship roll and held head yaw, vertical FOV 50°, and a base 1–10,000 m projection (`src/Allegiance.ClientCore.Game/Rendering/CockpitCamera.cs:43`, `:83`). Camera-space scene rendering keeps the eye at zero. The enabled radial-visibility path can extend the projection far plane past the configured spherical range (`Game1.Rendering.cs:498`); the cockpit must preserve that existing policy too. `Game1.Director.cs:109` gates head yaw to cockpit/chase; `:139` uses the gunner orientation when boarding a turret. Own hull and turret carrier are excluded from the cockpit colour/depth gather (`Game1.Rendering.cs:2583`; colour path at `Game1.Rendering.cs:762`). Chase, missile, turret, drone and command cameras already exist; a cockpit interior is not among their assets.

`Rendering/HudRenderer.cs:186` consumes a shared `HudModel`; `Rendering/ModernHudRenderer.cs:111` extends it. Telemetry, context, gauges, warnings and the semantic element inventory live at `Rendering/HudModel.cs:278`. Crucially, visibility policies already live in that model (`:296` flight/instruments; `:298` allowed indicator cameras; `:318` cockpit lead capability; `:338` target pane; `:346` centre-HUD preference). A cockpit renderer must consume those decisions rather than derive new ones. `Rendering/HudModelAdapter.cs:11` is the projection-to-model boundary; `Game1.Rendering.cs:3546` builds the shared snapshot before renderer dispatch.

`Rendering/Skins/IHudSkinRenderer.cs:9` supplies HUD, command and console phases. `HudSkinRenderer.cs:32` still owns external Classic radar, ship tags and pod/death passes, while `:45` owns Classic chat/cargo/parts. Simply skipping Classic’s main `Draw` will neither hide every Classic surface nor preserve every gameplay surface. `Game1.Rendering.cs:3578` dispatches the built model, `:1922` calls the command phase and `:1943` calls the console phase after the starmap. The abstraction has no interior prepass or arbitrary MFD widget renderer yet.

`HudSkinSwitcher.cs:18` accepts exactly two adapters; `:27` constructs exactly two installed entries; `:48` assumes a two-way current index. This must become a validated installed-renderer list before adding `cockpit`. JSON catalog entries alone cannot install rendering code (`Rendering/Skins/HudSkinCatalog.cs:35`). `Game1.GpuBudget.cs:44` constructs the adapters, `:56` handles the rebindable cycle chord, and `:67` commits/persists at the frame boundary. Shift+F12 is the existing default action, with compose/modal/focus gates retained.

`Rendering/VisualFlags.cs:24` documents default-off **new** visual departures, with approved defaults now on (`:125`, `:142`, `:154`). The brief and some glossary prose predate that approval. Proposed cockpit flags start off. Existing approved HDR/space defaults are not reverted.

The scene’s optional HDR path resolves before the HUD (`Game1.Rendering.cs:1867`–`:1888`). The storage contract is display-encoded RGB with headroom, not an all-linear legacy scene: see `Rendering/HdrOutput.cs` and `Rendering/VfxPostProcessor.cs:140`. Scene colour is RGBA16F when supported, and scene depth is Depth24 (`VfxPostProcessor.cs:129`, `:152`); the output transform runs once. The target-preview renderer already owns a lazy 256² colour+Depth24 target and restores the caller’s target (`Rendering/TargetModelPreviewRenderer.cs:32`, `:71`, `:90`). This proves target management infrastructure, not that the whole HUD can be drawn unmodified onto a cockpit display.

Native meshes load at runtime via `Rendering/NativeShipRenderer.cs:117` → `Allegiance.Core/Data/MdlFile.cs:363`; named frames include position, forward and up (`MdlFile.cs:63`, `:388`). Native lit materials already share sector lighting (`NativeShipRenderer.cs:349`); the optional HD/PBR material path is separate. An interior can reuse these loaders, but needs its own eye-local mesh, materials and display surfaces.

## Native findings

Native has a **per-hull camera attachment point**, not a ready-made interior system. `Igc/hullTypeIGC.cpp:80` looks up the hit-test frame named `cockpt` and scales its position, otherwise using zero. `WinTrek/WinTrek.cpp:7057`, `:7064`, `:7072` use that point transformed by ship orientation; a turret gunner instead uses the weapon mount position (`:7051`–`:7052`). It hides the source ship in settled internal view (`:7297`–`:7300`), and applies the native world clip (`:8077`). The current port’s 14 m forward offset is not a faithful use of native per-hull eyes; changing that offset is a separate camera parity task and must not be smuggled into a cosmetic cockpit toggle.

Native does render small 3-D indicator objects: `WinTrek/indicator.cpp:167` starts a 3-D layer for the joystick pointer. It also draws lead/turn/training/velocity indicators in allowed camera modes (`:764`–`:796`). That is not a canopy, seat or dashboard. The optional turret image/camera code around `WinTrek.cpp:7028`–`:7069` is commented out, not evidence of a shipping interior renderer.

An inventory of the installed artwork found 3,620 `.mdl` files, 646 containing `MeshGeo` and 438 containing `FrameData`. No filename contains cockpit, canopy or interior. No `.mdl` contains the ASCII `cockpt` token. The only `cockpit` text is a chat preference in `Artwork/dialog.mdl:19`; `interior` matches are sound definitions (`Artwork/sounddef.mdl`, `Artwork/sounddefvalor.mdl`). These checks do not prove that every vertex of every exterior model lacks a cabin-shaped detail; they do establish that this corpus supplies no identified standalone cockpit interior or reusable interior asset manifest. Native’s `cockpt` is hit-test metadata and need not appear as a text token in artwork MDLs.

`Artwork/cortui_huds.mdl:1` enumerates Classic, Raveen, lessHUD, GlassHUD, Parci and Jello HUD slots. `Artwork/CortUI/` supplies PNG gauges, masks, panels and status graphics; the referring `.mdl` files call `ImportImageFromFile` (for example `Artwork/gh_fuel_back_bmp.mdl:4`). The word “GlassHUD” describes a flat HUD skin, not a physical collimator. Reuse licensed existing indicators if useful; create the interior geometry. No evidence supports promising a native cockpit mesh that can simply be enabled.

## Complete HUD destination map

[Machine-readable mapping](COCKPIT_3D_P1044_assets/hud-mapping.csv) preserves every current native-item name, source matrix line, inherited native citation, current status, primary surface, fallback and parity guard. It maps all **112/112** rows, without upgrading any status. [Citation locations](COCKPIT_3D_P1044_assets/matrix-citation-locations.csv) were re-read with `rg -n` from the installed corpus. These are inherited matrix reference locations, not a new certification that each historical line is the ideal semantic citation.

“HUD glass” below means a collimated, final-resolution projection overlay: it may appear to float above a small physical glass combiner, but is never clipped to that glass or perspective-shrunk into its texture. “MFD” is an actual render-target-backed cockpit display. Every MFD entry has a full 2-D fallback when small, off-view, disabled, lost or unreadable; urgent values additionally stay on glass. World effects and screens outside flight explicitly remain in their existing layer. This map describes the **proposed production hybrid**, not the incomplete spike.

| # | Existing HUD row | Proposed primary destination |
|---|---|---|
| 1 | Hull integrity | MFD: systems |
| 2 | Shield charge | MFD: systems |
| 3 | Energy reserve | MFD: systems |
| 4 | Fuel reserve | MFD: systems |
| 5 | Ammunition reserve | MFD: systems |
| 6 | Thrust setting | MFD: systems |
| 7 | Speed | HUD glass: speed |
| 8 | Kill bonus | MFD: systems |
| 9 | Roll tutorial cue | 2-D overlay: training |
| 10 | Signature percentage | MFD: systems |
| 11 | Detection eye | HUD glass: own status |
| 12 | Cloak active | HUD glass: own status |
| 13 | Cloak strength and drained state | HUD glass: own status |
| 14 | Autopilot status | HUD glass: own status |
| 15 | Vector lock status | HUD glass: own status |
| 16 | Carried flag | HUD glass: own status |
| 17 | Carried artifact | HUD glass: own status |
| 18 | Target name | MFD: target |
| 19 | Target type description | MFD: target |
| 20 | Target side identity | MFD: target |
| 21 | Target hull | MFD: target |
| 22 | Target shield | MFD: target |
| 23 | Target range or sector | MFD: target |
| 24 | Target speed | MFD: target |
| 25 | Target ore | MFD: target |
| 26 | Target kill bonus | MFD: target |
| 27 | Target in weapon range | MFD: target |
| 28 | Target model preview | MFD: target |
| 29 | Target time to intercept | MFD: target |
| 30 | Cockpit centre reticle | HUD glass: projected indicators |
| 31 | Velocity direction marker | HUD glass: projected indicators |
| 32 | Joystick turning indicator | HUD glass: projected indicators |
| 33 | Ballistic lead marker | HUD glass: projected indicators |
| 34 | Aim hit-probability predictor | HUD glass: projected indicators |
| 35 | Chase weapon aim without target | HUD glass: projected indicators |
| 36 | Accepted and current order turn arrows | HUD glass: projected indicators |
| 37 | Selected target bracket | HUD glass: projected indicators |
| 38 | Missile lock flower | HUD glass: projected indicators |
| 39 | Missile lock console tips (partial / acquired / lost) | 2-D overlay: lock tips |
| 40 | Training HUD callouts | 2-D overlay: training callouts |
| 41 | Radar viewport and round border | HUD glass: radar/contact overlays |
| 42 | Radar ship contacts | HUD glass: radar/contact overlays |
| 43 | Radar station contacts | HUD glass: radar/contact overlays |
| 44 | Radar warp contacts | HUD glass: radar/contact overlays |
| 45 | Radar asteroid contacts | HUD glass: radar/contact overlays |
| 46 | Radar missile contacts | HUD glass: radar/contact overlays |
| 47 | Radar probe and mine contacts | HUD glass: radar/contact overlays |
| 48 | Radar treasure and tech contacts | HUD glass: radar/contact overlays |
| 49 | Radar accepted order brackets | HUD glass: radar/contact overlays |
| 50 | Radar queued order brackets | HUD glass: radar/contact overlays |
| 51 | Radar nearest enemy bracket | HUD glass: radar/contact overlays |
| 52 | Radar threat damage flash | HUD glass: radar/contact overlays |
| 53 | Radar command subjects | HUD glass: radar/contact overlays |
| 54 | Radar carried flag marker | HUD glass: radar/contact overlays |
| 55 | Radar carried artifact marker | HUD glass: radar/contact overlays |
| 56 | Radar ripcord marker | HUD glass: radar/contact overlays |
| 57 | Radar construction highlight | HUD glass: radar/contact overlays |
| 58 | Radar name labels | HUD glass: radar/contact overlays |
| 59 | Radar range labels | HUD glass: radar/contact overlays |
| 60 | Radar hull and shield bars | HUD glass: radar/contact overlays |
| 61 | Radar ore and miner fill bars | HUD glass: radar/contact overlays |
| 62 | Radar friendly detection eyes | HUD glass: radar/contact overlays |
| 63 | Radar friendly ripcord seconds | HUD glass: radar/contact overlays |
| 64 | Teleport lead-up sparks (ripcord + aleph) | World effect: unchanged |
| 65 | Radar command verb labels | HUD glass: radar/contact overlays |
| 66 | Radar LOD modes | HUD glass: radar/contact overlays |
| 67 | Radar range fade | HUD glass: radar/contact overlays |
| 68 | Sector location | 2-D overlay: location |
| 69 | Turret carrier identity | 2-D overlay: crew/observer state |
| 70 | Observer and turret board state | 2-D overlay: crew/observer state |
| 71 | Accepted order text | HUD glass: orders |
| 72 | Queued order text | HUD glass: orders |
| 73 | Chat history | 2-D overlay: console/tips/vote |
| 74 | Chat composition and recipient | 2-D overlay: console/tips/vote |
| 75 | Command recipient verb and wing controls | 2-D overlay: console/tips/vote |
| 76 | Tip text lines | 2-D overlay: console/tips/vote |
| 77 | Critical tip text lines | 2-D overlay: console/tips/vote |
| 78 | Vote banner | 2-D overlay: console/tips/vote |
| 79 | Money credits | HUD glass: counters/countdowns |
| 80 | Lives remaining | HUD glass: counters/countdowns |
| 81 | Mission kills | HUD glass: counters/countdowns |
| 82 | Mission countdown | HUD glass: counters/countdowns |
| 83 | Ripcord countdown | HUD glass: counters/countdowns |
| 84 | Eject pod identity panel | 2-D overlay: pod/death |
| 85 | Lifepod oxygen endurance | 2-D overlay: pod/death |
| 86 | Death banner | 2-D overlay: pod/death |
| 87 | Boundary and crowd warnings | 2-D overlay: urgent warnings/damage |
| 88 | Incoming missile warning | 2-D overlay: urgent warnings/damage |
| 89 | Damage flash timer | 2-D overlay: urgent warnings/damage |
| 90 | HUD style and software layouts | 2-D overlay: settings |
| 91 | Console pane visibility and slide animation | 2-D overlay: console panes |
| 92 | HUD base class contract | Shared contract (n/a visual) |
| 93 | Game list screen boundary | 2-D overlay: existing game-list screen |
| 94 | Flight equipment inventory pane | 2-D overlay: complete inventory pane |
| 95 | Equipment part identity and key | 2-D overlay: complete inventory pane |
| 96 | Equipment selected and active state | 2-D overlay: complete inventory pane |
| 97 | Equipment quantity and empty warning | 2-D overlay: complete inventory pane |
| 98 | Equipment mounting progress | 2-D overlay: complete inventory pane |
| 99 | Launcher arming progress | 2-D overlay: complete inventory pane |
| 100 | Equipment weapon range | 2-D overlay: complete inventory pane |
| 101 | Equipment shot damage | 2-D overlay: complete inventory pane |
| 102 | Equipment burst rate | 2-D overlay: complete inventory pane |
| 103 | Equipment shield strength | 2-D overlay: complete inventory pane |
| 104 | Equipment shield recharge seconds | 2-D overlay: complete inventory pane |
| 105 | Equipment afterburner top speed | 2-D overlay: complete inventory pane |
| 106 | Equipment afterburner endurance | 2-D overlay: complete inventory pane |
| 107 | Equipment cloak endurance | 2-D overlay: complete inventory pane |
| 108 | Inventory turret gunner identity | 2-D overlay: complete inventory pane |
| 109 | Inventory cargo slots | 2-D overlay: complete inventory pane |
| 110 | Inventory own ship name | 2-D overlay: complete inventory pane |
| 111 | Equipment empty slot affordances | 2-D overlay: complete inventory pane |
| 112 | Command view ship summary | 2-D overlay: existing command summary |

## Options and recommendation

**Build a generic hybrid cockpit, using a shared interior renderer plus a cockpit skin.** Stage the interior as an independent overlay compatible with Classic and Modern, then add a curated hybrid skin that moves suitable instruments onto displays. Keep all precise targeting/contact indicators and urgent messages in the final-resolution overlay. Do not introduce a `hideClassicHud` feature switch as the architecture.

| Option | Information and implementation | What the captures support | Relative effort / decision |
|---|---|---|---|
| A. Hide Classic; substitute a model | A model supplies none of Classic’s gauges, console, radar or input contract. Hiding only the main draw leaves other Classic phases. Adding replacement instruments turns this into B. | The cockpit-only captures have the cleanest central aperture, but only thrust/fuel remain: they are visibly incomplete as a playable HUD. | S for a visual demo; L for a complete replacement. Reject as a shipping shortcut. |
| B. Third `IHudSkinRenderer`, MFDs and HUD glass | Reuse the shared model; generalize the two-entry switcher, extract widget draws, add display prepass and native-resolution projected indicators. Keep command/console fallbacks. | The single display proves texture-on-quad readability at 1080p. A complete B layout, glass treatment, populated target panel and full message load have **not** been rendered; its finished appearance is unproven. | M–L. Good final ownership boundary; a fully physical, MFD-only information layout is unsuitable at 800×480. |
| C. Interior overlaid beneath any skin | Small independent interior service; existing skins draw last. It preserves information most easily, provided canopy occlusion is solved. No Classic-specific branching. | Both hybrid pairs show a sense of enclosure, but duplication and the speed readout crossing the MFD look poor. At 480p the overlap is unacceptable. | S–M for the layer; M to meet protection/layout gates. Useful first slice and fallback, not the captured final composition. |
| D. Recommended hybrid: C’s shared interior + B’s curated cockpit skin | Reuse the same frame/display service for all skins. The cockpit skin places systems/target summaries on MFDs, keeps glass/console safety information, and promotes complete panels to 2-D when space or view direction requires it. | The cleaner cockpit-only aperture and legible native-resolution overlays suggest the combination. That is a design inference from the captures, not approval of an unseen final skin. | M–L, staged. One generic interior first; proceed to visual approval before enabling by default. |

The end state can retain some Classic glyphs or a Modern palette without preserving their entire layouts. Separate geometry from HUD skin identity so a user can choose a canopy with either existing style, or choose the curated cockpit layout. There is no reason to touch simulation, server messages or multiplayer settings.

No external-game feature claims are needed to select this architecture. Public website research was omitted to honor the brief’s prohibition on contacting public hosts; comparisons to Elite, Star Citizen, X4 or Everspace would otherwise need primary-source verification. The local design references P972 and P978 are background, not evidence that those products implement the proposed pipeline.

## Gameplay parity and fairness

Keep one `HudModelAdapter` snapshot and the same targeting/radar/input code for every skin. Do not add cockpit-owned sensor queries, contact selection, range tests, lock acquisition, lead solving, visibility or server exports. Preserve unknown/null values; a blank MFD field must not turn an unknown value into zero. The existing model gates remain authoritative even when a widget changes destination. The mapping above and CSV include non-instrument rows deliberately: chat, inventory, command summaries, game-list boundaries and world effects cannot disappear during a “HUD replacement.”

Native indicator camera gates are at `WinTrek/indicator.cpp:764`; lead projection/capability checks are at `:283`–`:330`, lock drawing at `WinTrek/radarimage.cpp:325`, and per-kind radar label rules at `:590`–`:1021`. Preserve those semantics through existing model decisions. A world-registered lead point stays at its original screen point, not projected a second time onto a nearby glass quad. All contact brackets, edge indicators, native-enabled labels and warnings draw after the interior, with depth testing off and unchanged LOD/fade. Do not draw new through-obstacle labels or contact types merely because a canopy exists.

**Opaque frames are not automatically fair.** In the spike, hulls behind the rails and the console are hidden. Merely retaining targeting logic does not satisfy the brief’s visibility condition, and equal wire knowledge is not proof of equal perceptual difficulty. A production layer needs an explicit visibility protection policy:

1. Build protected screen footprints from the already eligible world draw list and the existing radar/contact overlay list, using the same projection, projected radius and edge placement. Include visible objects without an enabled bracket; do not promote objects that native eligibility excludes.
2. Mask out or strongly fade cockpit pixels in these footprints before drawing the interior, with padding for a moving object and its native label. Draw the original eligible indicators afterward. Keep reticle, lead, locks, warnings and the compact navigation/console region permanently protected. Use a soft transition to avoid a distracting flashing hole.
3. If footprint work becomes excessive, any transform is invalid, or protection cannot cover a view/state, suppress the interior for that frame. Preserve the scene; never drop contacts to meet a cockpit performance budget. A user can also disable the cockpit immediately through the normal presentation setting.
4. Compare identical visibility/target/lock/order traces with interior off/on and every installed skin. Put contacts at each rail, console edge, viewport edge, rear/side look direction and camera cut. Pixel/capture checks must show all permitted information remains readable. Test low contrast, color-blind palette, maximum HUD size, rapid warning changes and busy scenes, not just an empty sky.

A full silhouette-protection mask may erase much of the canopy in a busy battle. That is an honest tradeoff; changing the sensor or targeting rules to keep a pretty canopy would violate the assignment. Prototype this policy before paying for detailed art. The spike has no contact protection, no populated radar model and no all-row parity proof; it is not multiplayer-ready.

Retain the same eye, FOV, zoom and world clip settings when toggling the interior. Do not give the cockpit skin wider vision, a new targeting ray, a zooming MFD camera or privileged spectator data. No animated cockpit button should intercept existing flight input. Server behavior and network traffic remain identical. Rule/knowledge parity can be tested exactly; identical human reaction times across different art/layouts cannot be guaranteed. Cosmetic approval therefore also requires play/readability review, with a complete overlay fallback always available.

## Assets, attachments and budgets

Start with **one generic pilot cockpit**, not one per hull. Use original primitive geometry to tune the silhouette and protected aperture, then commission a restrained low-poly mesh. A scout/fighter variant and a capital/turret variant can follow only if the first cockpit earns approval. Per-hull interiors multiply attachment, camera, silhouette and regression work across all hulls and optional HD replacements, with no native interior library to amortize it.

The installed `.cvh` corpus makes the native attachment explicit: 627 files, **285 containing `cockpt`**. `Artwork/wc_bifig.cvh:72` is an example, with position and forward on the next two lines. Native reads those triples and flips the required axes (`_Utility/HitTest.cpp:774`–`:803`); hull scale is `0.5 * length / meshRadius` (`Igc/hullTypeIGC.cpp:62`). The port already reads CVH frames (`src/Allegiance.Core/Collision/CvhReader.cs:109`; `CvhTypes.cs:38`). Prefer this collision-frame source when discussing native camera attachments; MDL `FrameData` is useful for art sockets but is not evidence that a native eye exists in each MDL.

For the cosmetic feature, define an interior descriptor with model/material ids, seat role, eye-local transform, forward/up basis, metre scale, MFD quad corners/UVs and protected aperture. Parent it to the **seat/ship**, not directly to the final head-look orientation: turning the head must reveal the side of the cabin instead of dragging the front panel around with the gaze. The spike uses the identity forward camera and tests only the forward view; it does not prove this transform. Keep the current actual world eye unchanged initially. Native CVH eye adoption, if desired, is a separately reviewed parity correction applied consistently to all styles and targeting paths.

Proposed starting budgets, to be checked after visual tuning:

| Resource | Generic cockpit target |
|---|---|
| Geometry | 2,000–8,000 triangles, ≤4 material draws, no cockpit shadows or mirror scene rendering |
| LOD | Fixed near-eye mesh initially; one cheap fallback mesh/material tier. External hull LOD is irrelevant because the interior only draws from its seat. |
| Materials | One 1024² albedo atlas, one normal and one packed roughness/metal/AO atlas; optional emissive mask. 3 RGBA8 textures = 12 MiB, about 16 MiB with full mips; an additional RGBA8 emissive atlas adds about 5.3 MiB with mips. |
| Download | Aim ≤5 MiB for the generic mesh and compressed authored textures, excluding existing fonts/art; measure actual encoding. |
| Displays | Initially one or two 512×256 RGBA8 targets, no depth, no MSAA, no mip generation: 0.5 MiB each; three use 1.5 MiB. Pool/preallocate, never recreate per frame. |
| Optional 4K displays | Increase only from projected pixel size, up to 1024×512 each (2 MiB); three would use 6 MiB. Do not scale render targets to the entire 4K viewport. |

Primitive geometry is original code in the saved spike. Hand-made low-poly art is the preferred first production source because the canopy silhouette and UV/text areas need deliberate control. AI-generated mesh can be an optional HD-pack input, but still needs topology cleanup, metre scale, inward-facing surfaces, separate display materials, manual UVs and authored sockets; it does not eliminate that work. Do not import exterior HD-pack art into this report or silently make a new cockpit depend on the optional pack.

`Artwork/LICENSE:1` is MIT, copyright FreeAllegiance; a copy accompanies the screenshots in the assets folder. Preserve its notice when redistributing derived artwork. New commissioned assets need an explicit redistribution grant and provenance manifest; AI outputs need their generation/service-rights record and human media review. Use repository-approved fonts and retain their notices. No generated art or third-party cockpit mesh was downloaded for this study.

## Rendering design

Leave the existing world near/far projection alone. In a conventional 24-bit perspective depth buffer, a useful far-from-near estimate is `Δz ≈ z²(f−n)/(f·n·(2²⁴−1))`. With world near=1 m and far=10,000 m, the quantization step near the far plane is about **5.96 m**. Reducing world near to 0.05 m to accommodate a cockpit makes that roughly **119 m**, twenty times worse. These are depth-step estimates, not measured visible z-fighting. A separate interior range, 0.05–5 m, gives roughly 4.7 micrometres at z=2 m and does not affect distant geometry.

The spike demonstrates the simple version: preserve scene colour, clear **depth only**, then render the cockpit with its own projection, then draw the 2-D HUD. Outside geometry cannot clip into the cockpit. It intentionally overlays close scene geometry; the contact/scene protection policy is needed to make that cosmetic rather than obstructive. No second world traversal, scene reflection or per-display 3-D camera is required.

Proposed production order:

1. Update dirty MFD targets from the shared model **before** the world scene, and restore explicit target/viewport/state. Do not nest this inside a SpriteBatch or the target-preview pass. This avoids preserving/rebinding a completed full-resolution scene for each display, as the disposable spike does.
2. Render the world and its unchanged depth/effects pipeline. Obtain protection footprints from the same eligible draw data. Do not feed interior depth to soft particles, fog, occlusion, radar eligibility or targeting.
3. For the first polished version, compose a lightly lit interior **after** the existing scene output transform at final resolution. Apply the cockpit’s own controlled sun/ambient/material shading in display-referred output; this intentionally keeps instruments readable and the world’s approved HDR path unchanged. Clear depth once here. This requires no extra full-screen cockpit target if direct blending/masking suffices.
4. Draw display faces emissively with bounded contrast and no bloom; draw glass symbology and all protected UI last. A real transparent glass pane should have negligible tint, no refraction and no unreadability-producing glare.

An interior rendered before the HDR output transform could share scene lighting more physically, but then display colors/text are graded and may bloom or become unreadable. Making its opaque frame participate before the single output transform while drawing displays later is a valid future refinement: clear interior depth before the frame, retain the frame depth separately across the transform, then composite emissive faces. That is more state/target complexity and needs HDR-on/off, bloom, FSR, resize, capture and device-loss proofs. Do not add a second tone map or assume every legacy scene writer is linear. The spike measures only the native display-referred scene path; it does not validate an HDR-integrated cabin.

Use existing sector sun direction transformed to seat space, a soft ambient floor, and a bounded emissive panel glow. Let the existing damage cue also tint the cabin briefly without suppressing the native flash/warnings; do not make systems actually fail, lights reveal unseen enemies, canopy damage obscure vision, or text vary with sun angle. Material polish and reflected ambient light matter more than polygon count for this close view.

At 800×480, the spike’s display occupies about **252×108 screen pixels**, while its source target is 512×256. A 20 px glyph in that texture becomes about 9 px on screen: the capture shows why increasing texture resolution alone cannot fix small text. Set a production minimum rendered text height (start at 14 logical pixels, user-scaled) and promote full panels to 2-D when projected size, obliqueness, head look or HUD scaling violates it. At 1080p the same display is about 568×242 pixels and the labels read clearly. At 4K its projected footprint doubles; use SDF text or appropriately sized targets rather than magnifying a fixed low-resolution raster. 4K was not captured here. Test it in the visual lane before accepting the layout.

Retain current held-key/hat yaw (`Rendering/HeadLook.cs:40`, `Game1.FlightInput.cs:1975`). The port already applies native-style afterburner jiggle to the cockpit view (`Game1.Rendering.cs:508`; native `WinTrek/WinTrek.cpp:374`–`:399`); do not add it twice. Additional head bob or console vibration belongs behind a separate default-off motion flag, bounded so it cannot obscure readings or move the picking ray. Leave this embellishment out of the first release.

Mouse/hat free look is not necessary to ship the first forward cockpit. A later mouse-look mode needs an explicit held modifier, one relative-mouse owner, suppression of flight deflection only while looking, zeroing of stored deltas on entry/exit/focus loss, and unchanged keyboard/gamepad flight input. The visual head transform must not rotate guns or create a new target-picking behavior. No clickable 3-D cockpit controls initially: use the current bindings and 2-D interactive panes. Test side/rear hat look, recenter, zoom, focus loss, turret transitions and mouse-flight ownership together.

## Other views and states

| View/state | Production treatment |
|---|---|
| Ordinary pilot cockpit | Draw the descriptor for the actual seat, only when the normal flight model is visible; protected overlay always last. |
| Turret gunner, internal view | Initially retain current overlay without generic pilot frame. Later use a gunner descriptor at the carrier weapon mount, with gunner orientation and source-ship data; never use the pilot nose socket. |
| Turret external / missile / chase / other external cameras | No interior. Keep the selected skin’s existing external-view indicators and target summaries. |
| Lifepod | Interior off initially; retain pod identity, rescue instructions, oxygen/endurance and existing source hull rules. A dedicated pod shell is optional art later. |
| Transient death / ghost / respawn | Interior off while dead/ghost and through camera override. Preserve the separate ghost-banner gate. Restore only after the new hull/seat is known; never leave the previous hull’s console. |
| Docked / hangar / loadout | Existing full screens and hull previews. No cockpit overlay and no hidden 3-D hit targets. |
| Command / drone follow / drone cockpit | Existing command presentation initially; no generic pilot cabin. Drone cockpit is still a command camera for model visibility, not permission to show pilot-only instruments. |
| Training | Hide the interior and temporarily resolve the cockpit skin to the previously selected Classic/Modern teaching layout for the whole lesson. Current training landmarks are fixed HUD positions (`HudTrainingCallout.cs:38`); preserve script actions and semantics. Save/restore presentation and flags on every exit through `Game1.TrainingIntegration.cs:20`, `:41`. Later a semantic widget-anchor provider could project each callout onto its actual MFD widget; it needs separate proof. |
| Spectator / observer | No interior by default; do not invent a pilot seat or reveal additional data. Existing spectator HUD suppression at `Game1.Rendering.cs:3291` remains authoritative. Explicitly boarding a legal observer seat uses normal board-state policy, not spectator omniscience in a cockpit. |
| HUD hidden / clean recording | Hide instruments under existing preferences, and omit the decorative cockpit for clean director output. A decorative toggle must not resurrect a hidden native panel. |
| Missing art / RT/device failure / unsupported backend | Fall back atomically to a complete installed Classic/Modern presentation, with no blank frame. Keep the requested preference for future retry, while clearly recording the effective fallback. |

Changing layouts must preserve input focus, target, panes, money, radar mode and training state across Shift+F12, graphics options, death, docking and lesson exits. The initial C layer can cover both skins without migrating them; a third skin must implement all three existing phases and the external-view fallbacks.

## Local spike, measurements and limits

The disposable tool uses a **real DesktopGL SDL window on a local Xvfb display**, not an image mockup or a composited scene illustration. Its screenshots read the actual window backbuffer before present. It instantiates production native sky/hull renderers and production Classic/Modern renderers, reusing the deterministic P982 busy-scene pattern (`tools/p982-space-proof/Program.cs:214`, `:278`). It draws 400 native `wc_icfig` hulls, normalized to radius 15 m for a visible busy scene, plus native sky. This is a renderer fixture, **not** a 400-player live match, simulation benchmark or actual visibility/contact snapshot. HUD values are the authored P982-style `HudFrame` passed through `HudModel.Create`, prominently labeled in every capture; they are not claimed to come from a networked ship.

The interior is 28 triangles of shaded strips and console faces; each MFD adds two triangles. One 512×256 RGBA8 display is shown in stills; performance sweeps use zero, one or three targets. The displayed rows are **THRUST 80% and FUEL 90%**, read from the model’s first two gauges, rather than a hard-coded fake screen image. Full production meshes, dynamic head transforms, contact protection, actual fog/lock state, input, training and HDR cabin integration are outside the spike. Its primitive strips are visibly flat, not a finished volumetric seat/cabin asset.

There is no exposed `/dev/dri` GPU device. Renderer: **llvmpipe, LLVM 19.1.7, 256 bits; Mesa 25.0.7**, desktop GL 4.5. Measurements therefore report **CPU submission** and the GL elapsed query on a software renderer. The latter is **not a measurement of physical GPU cost** and cannot certify the requested hardware GPU budget. No rig or other machine was contacted to fill this gap.

At 1920×1080, vsync and frame cap off, each configuration has 30 warmup frames and 70 measured frames, repeated three times in a mixed forward/reverse order (840 measured frames total). `GlFrameTimer` issues delayed availability-polled `GL_TIME_ELAPSED` queries; CPU uses `Stopwatch` around only the additional cockpit region. Polling, screenshot readback, logging, initialization and the 400-ship scene submission are outside the measured CPU region. The baseline measures an empty timed region with the same instrumentation. All display targets redraw every measured frame. The CPU metric can include driver backpressure; it is not a promise about pure managed work. There is no synthetic CPU load.

[Raw measurements](COCKPIT_3D_P1044_assets/performance.csv) retain all twelve groups, individual group p95s, the separate scene submission/query means and added draw counts. Group means averaged across the three repeats:

| Added work | CPU submit mean | Mean after empty-region subtraction | Software GL mean | Largest group p95 CPU / GL | Extra draws |
|---|---:|---:|---:|---:|---:|
| Empty region | 0.0055 ms | — | 0.0321 ms | 0.008 / 0.040 ms | 0 |
| 28-triangle interior, no MFD | 0.0588 ms | 0.0533 ms | 0.0504 ms | 0.075 / 0.062 ms | 1 |
| Interior + 1 live MFD | 0.2005 ms | 0.1950 ms | 0.3788 ms | 0.222 / 0.416 ms | 8 |
| Interior + 3 live MFDs | 3.2469 ms | 3.2414 ms | 0.8261 ms | 3.487 / 0.956 ms | 22 |

**Budget verdict:** the primitive interior plus one MFD meets the <0.5 ms CPU target on this setup. Three MFDs decisively miss it. The <1 ms **hardware GPU** target is unverified for every configuration; the software query’s three-MFD mean below 1 ms is not a hardware pass, even when its p95 also falls below that number. The scene itself takes roughly 8–10 ms CPU submission per group; its query number is not a hardware utilization measure.

The three-display result argues for one shared atlas/batch, dirty-region/cadence updates and MFD generation before the world pass. It does not prove how much those changes will save. Critical counters, contacts, lock and warnings must still update with the presented frame, even if slow engineering/statistics pages refresh at 15–30 Hz. Reduce display detail or use a full overlay fallback if the budget cannot be met. The 28-triangle result also cannot certify an 8,000-triangle textured/PBR interior, silhouette protection or 4K; measure the actual chosen assets and full pipeline in the later visual lane.

Reproduction after applying the saved patch, from the repository root (use the installed SDK; restore only from available local packages):

```sh
~/.dotnet/dotnet build tools/p1044-cockpit-proof/p1044-cockpit-proof.csproj --no-restore
# Under a local X display; choose its DISPLAY value in the environment.
LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null ~/.dotnet/dotnet \
  tools/p1044-cockpit-proof/bin/Debug/net10.0/p1044-cockpit-proof.dll \
  --width 1920 --height 1080 --mode cockpit --out .scratch/p1044/window.png
LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null ~/.dotnet/dotnet \
  tools/p1044-cockpit-proof/bin/Debug/net10.0/p1044-cockpit-proof.dll \
  --cost --out .scratch/p1044/cost.csv
```

Modes `hybrid-classic` and `hybrid-modern` draw those production HUDs over the same interior. `--width 800 --height 480` exercises the small window. Apply `spike.patch` with `git apply`; restore using its reverse, `git apply -R`, after collecting results. The patch contains the standalone tool only; it does not edit production code or register a shipping application. A fresh reproduction needs an offline restore of that new project before `--no-restore` can work, and the normal client content build. Do not run the patch as a production feature.

## Captures and visual judgement

All six final PNGs were opened and inspected. They retain the original window dimensions; rounding RGB channels to 8-step values (at most 4/255 channel error), without cropping/compositing, keeps the combined PNG payload below 2 MB. This introduces mild color quantization; it is report compression, not a proposed renderer grade. Original unquantized backbuffers remain scratch-only. These are virtual-display window captures; no physical-screen readability or interactive play judgement is claimed.

| Capture | What is actually visible / decision |
|---|---|
| [Cockpit only, 1920×1080](COCKPIT_3D_P1044_assets/cockpit-1920x1080.png) | Dark polygonal rails and wide lower console, warm native sky, repeated blue native hulls, center console screen with thrust/fuel bars. The open central view reads as a cockpit, but most native HUD information is absent and the bottom console consumes about 31% of the viewport. A visual feasibility sketch, not playable option A. |
| [Cockpit only, 800×480](COCKPIT_3D_P1044_assets/cockpit-800x480.png) | The same silhouette and two gauge values survive. Large blank console cheeks waste scarce space; the instrument lettering is about 9 px and too small as the sole flight readout. Native-resolution fallback is necessary. |
| [Hybrid Classic, 1920×1080](COCKPIT_3D_P1044_assets/hybrid-classic-1920x1080.png) | Classic arcs/labels float in the aperture; roll and weapons/power text remain visible. The speed readout lands on the MFD’s fuel bar. The duplicate gauges and overlapping speed look unfinished; do not approve this unchanged composition. |
| [Hybrid Classic, 800×480](COCKPIT_3D_P1044_assets/hybrid-classic-800x480.png) | The gauge cluster is large, ammo/energy labels sit on the console edge, speed crosses the display, and the steering indicator overlaps it. This fails the small-window layout criterion. |
| [Hybrid Modern, 1920×1080](COCKPIT_3D_P1044_assets/hybrid-modern-1920x1080.png) | Percentage gauges are clearer than Classic against the busy sky. Kill bonus sits on the console rim, and speed again crosses the MFD. Better numeric clarity, still an unapproved duplicate layout. |
| [Hybrid Modern, 800×480](COCKPIT_3D_P1044_assets/hybrid-modern-800x480.png) | Native-resolution percentage gauges remain readable; thrust/fuel and kill-bonus labels collide with the console/display heading, and speed overlays the screen. Modern also needs deliberate reflow, not just a frame switch. |

The strongest visual finding is the enclosure silhouette with an otherwise open aperture. The weakest is the oversized blank dashboard and duplicate status widgets. For the next iteration, lower the console, narrow the rails, add restrained bevel/material shading, and use one MFD for nonurgent detail while reserving fixed safe regions for speed, status and targeting. A convincing hybrid is plausible, but **none of these primitive captures is a finished look to enable by default**. A full third-skin layout and contact/warning load must be seen before declaring D successful.
## Phased implementation plan

These are planning estimates from this investigation, not measured delivery commitments. **S = 1–2 engineering days; M = 3–5; L = 6–10.** A delegate can take each slice independently once its dependencies land. “Codex lane” means local implementation/model/contract validation; “visual lane” means a developer with an actual client window and representative GPU who can judge and capture the result. Hardware work is a future handoff, not authorization to contact another machine in this task.

Use the existing visual-flag table, not new environment variables or server settings:

- `visuals.cockpit.interior`: default OFF; draws only the permitted interior layer beneath whichever skin is active.
- `visuals.cockpit.displays`: default OFF; allocates/updates MFDs only when the interior is effective. Declare dependency on `cockpit.interior` and show the inactive reason in Options.
- Skin id `cockpit`: an explicit opt-in presentation, exposed only when the feature and required device resources are available. Preserve Classic/Modern installations and the existing atomic cycle/focus gates. Model requested skin separately from effective fallback so turning a flag off does not persist a blank/unavailable renderer.
- Optional later `visuals.cockpit.motion`: default OFF; cosmetic extra motion only. It is unnecessary for the initial feature and must not replace existing native jiggle.

Example launch override for a future implementation: `--visuals cockpit.interior=on,cockpit.displays=on`. These names are **proposed**, not recognized by the final tree. No new service, store, port or environment variable is needed.

| Slice | Size / lane | Concrete scope and dependency | Acceptance criteria and verification |
|---|---|---|---|
| 1. Safe interior lifecycle and occlusion experiment | M / Codex + visual review | Independent seat-scoped interior renderer, explicit near projection/depth-only clear, default-off flag; primitive mesh, no MFD. Implement protected footprints/fallback before final art. | Flag off produces identical Classic/Modern captures and contact/target traces. Own camera eye, FOV and world clip are unchanged. Contacts and visible object silhouettes crossing every rail/console remain visible; overload suppresses interior. Test command/chase/pod/ghost/docked/spectator gates and camera cuts. Capture both styles at 800×480 and 1080p. Stop/reconsider opaque geometry if protection makes the cabin visually unusable. |
| 2. Installed renderer registry | S / Codex | Replace two-entry construction/index assumptions with validated preloaded renderer registrations; keep existing ids/defaults, input chord and persistence. Depends on no art. | Existing Classic/Modern output stays identical; a small test third adapter cycles forward/reverse, holds through compose/focus/modal gates, requests/saves/restores atomically and disposes exactly once. Missing third implementation yields complete Modern fallback. Update registry-driven HUD contract tests; no renderer lookup/allocation in steady Draw. |
| 3. MFD service and one systems display | M / Codex + real-window visual lane | Shared model widget draws, pooled one-display target/atlas, correct target/viewport/state restore before world rendering, emissive face after output transform. Depends on slice 1. | Authored gauge sweeps and live ship damage/fuel/throttle appear on the display from the same model as both HUDs. No stale frame after pause, warp, hull change, resize/device recreation. Text falls back at the projected-size threshold; no target read/write binding hazard. Measure isolated and full-frame incremental CPU/GPU cost at 1080p, HDR on/off and FSR on/off. |
| 4. Generic interior art and readable hybrid layout | M engineering + 3–5 art days / visual lane | Lower/thin the dashboard, author shaded inward faces and sockets, remove duplicate gauges in the cockpit skin while preserving the complete destination map. Depends on 1–3. | Review actual Classic+interior, Modern+interior and cockpit-skin captures at 800×480, 1080p and 4K; dark/bright sun orientations, busy contacts, all warning/target/inventory states. No speed/MFD collision; minimum readable text size, full fallback and protected screen regions. Domingo’s visual approval is a release criterion, not an architectural assumption. |
| 5. Full skin/state/parity certification | M–L / Codex + visual lane | Implement all three renderer phases and all 112 mapped rows, external-view fallback, safe input focus, full inventory and chat, unavailable data and display fallback. Depends on 2–4. | Every mapped native-enabled row has a measured/captured equivalent or its explicitly retained world/screen treatment. Compare same target/contact/lock/order/warning traces across installed skins. Test HUD hide, centre/target/history preferences, modal/pane interaction, wallet/countdowns, pod rescue, death/ghost and docking transitions. Both existing skins remain regression-pinned. Do not upgrade existing parity remainders without evidence. |
| 6. Training integration and restore | M / Codex + visual lane | Snapshot effective/requested presentation and cockpit flags; temporarily use complete teaching layout; restore on every lesson exit. A future semantic callout provider is a separate L slice. | Training callouts point to real visible widgets in both teaching styles. Restore after normal completion, quit, failed join, death/restart, disconnect and window close. Run `tools/run-training-tests.sh --no-build --repeat 3` with artwork, including Live/Nanite, and real-window callout captures. |
| 7. Hardware performance, release and fallback | M / visual lane | Measure the final approved mesh, contact protection, target preview and one/two displays; compare flag off/on and both skins in an existing busy local scenario. No remote access implied. | At 1080p, repeated incremental frame CPU <0.5 ms and physical GPU <1 ms, with p95 reported and no recurring spikes hidden in the mean. Verify 800×480/4K, resize, alt-tab, capture and lower-tier backend. If it misses, reduce display cost or retain the full overlay. All new flags remain OFF until explicit visual approval; review new media/provenance before any later push. |
| 8. Optional class/seat variants and free look | L per variant family / visual + Codex | Fighter/capital/gunner descriptors, later mouse-look/motion, optional HD-pack upgrades. Depends on acceptance of generic cockpit. | Mount/orientation/scale proofs per seat; no new input capture or target-ray differences, same protected aperture and all-row fallback. Hardware/art review repeated for each variant. No per-hull rollout without actual cost/benefit evidence. |

Allow roughly **4–7 engineering weeks plus 1–2 artist weeks** for a polished, parity-complete generic cockpit with fallback, including investigation-driven revisions and the above gates. A minimal interior-only opt-in preview is roughly **3–5 days**, not the complete feature. Per-class cabins add about 3–5 art days plus 2–4 integration/verification days per family; bespoke per-hull cockpits can add 1–3 art days **each** before full seat/layout verification. Shared transforms/sockets and material variants should be exhausted before multiplying meshes.

For implementation slices, use the repository’s normal 0-warning solution build, tools build, new behavior tests three times alone and fast-tier gate; training changes also require the Live training gate described above. Add native citations and parity tracking for any newly ported rule. This **report-only** task uses the brief’s narrower final gate: remove the spike and build the whole solution successfully.

## Risks and decision gates

| Risk | Consequence / mitigation |
|---|---|
| Opaque structure hides native-visible objects | Highest gameplay risk. Prove footprint protection before art; omit interior when protection is uncertain. Overlay markers alone are insufficient evidence of equal visibility. |
| 480p and large HUD settings | A physical console cannot hold every native pane legibly. Full native-resolution overlay fallback is required, including while looking away. |
| Three-target performance | Already misses CPU budget in the software spike. Use one/two displays, batch an atlas and schedule before scene; remeasure, never extrapolate a hardware pass. |
| Third-skin infrastructure is incomplete | A JSON definition is not a renderer. Generalize installation, cycle and fallback before enabling a selectable cockpit id. Preserve all three draw phases. |
| Native camera attachment discrepancy | Existing port uses a forward offset; native CVH uses per-hull sockets. Keep cosmetic toggles from changing the actual eye/target ray. Review a parity correction separately. |
| HDR/FSR/state leaks | Wrong target restore, double grading, depth feeding particles or overbright text. Explicit pass ownership and device/resize/capture/HDR-off proofs. |
| Art exceeds primitive cost | Close-up normals, large textures and silhouette protection can cost more than the 28-triangle sketch. Enforce actual asset budgets and profile the final cabin. |
| Training or alternate states lose information | Existing callout anchors/command and pod models do not imply pilot-cockpit instruments. Use explicit state fallbacks and all-exit restoration tests. |
| Looking good is mistaken for feature completion | The current pictures prove enclosure and a live render-target path only. Approve representative busy, damaged, targeting and small-window captures before release. |

## Validation and retained artifacts

The starting revision was `30bd89aa`. The user-provided untracked `P1044_BRIEF.md` is retained untouched. No commits, pushes, stashes, remote-machine sessions or new runtime assets are part of this task. Production source and parity CSVs are unchanged; this is a proposal rather than a new implemented system, so the glossary is unchanged under the report-only scope.

The saved [spike patch](COCKPIT_3D_P1044_assets/spike.patch) recreates the temporary standalone tool. Its source was removed from `tools/` after collecting captures and measurements. Scratch scripts, build logs, unquantized captures and local X-display logs are ignored under `.scratch/p1044/`. The final retained artifacts are this report, the patch, six PNGs, the HUD mapping, report/native matrix citation-location evidence, raw performance CSV and the screenshot artwork notice.

Final validation after removing the spike:

- `~/.dotnet/dotnet build AllegianceCore.slnx`: **succeeded, 0 warnings / 0 errors**. This used `RestoreSources` pointing to the empty local scratch feed and `NuGetAudit=false`, with all dependencies already cached. The initial default restore failed because its advisory-cache write was outside the writable sandbox; the final build used offline restore settings rather than changing repository configuration. This validates compilation, not a refreshed vulnerability audit.
- `git apply --check` accepts the retained patch; `git diff --check` passes. There are **no tracked-file differences** and the temporary tool source is absent. The only new task files are this report and its assets; the original untracked brief remains.
- Mapping validation matches all **112 unique HUD rows** in source order, with original names, native references and statuses preserved; every row has a destination and parity guard. Native/reference anchors were re-grepped; [report source locations](COCKPIT_3D_P1044_assets/source-citation-locations.csv) supplement the inherited matrix-location evidence.
- Six inspected PNGs total **1,584,321 bytes** (under 2 MB); `spike.patch` is **16,528 bytes** (under 300 KB). No unrelated runtime media or generated assets are retained.
- Performance CSV has 12 groups × 70 measured samples = **840 CPU and 840 GL-query samples**, plus corresponding scene queries; all groups contain their full sample count. End-of-run synchronization occurs outside the timed regions.

No gameplay, training or renderer implementation remains in the working tree, so this report does not claim new passing runtime/parity/training tests. Its explicit unresolved acceptance gate is physical-GPU timing and visual/playability review of a completed hybrid, as listed in the plan.

## Native intent

Allegiance’s internal view is a ship/seat camera with complete, shared combat information. Its native `cockpt`/weapon attachments choose the eye; settled internal view hides the exterior source hull (`Igc/hullTypeIGC.cpp:80`; `WinTrek/WinTrek.cpp:7051`–`:7076`, `:7297`–`:7300`). Its HUD indicators and per-kind radar rules convey the information the player is allowed to use (`WinTrek/indicator.cpp:764`; `WinTrek/radarimage.cpp:590`). A modelled cabin must preserve that contract. Native artwork offers flat HUD skins and eye metadata, not an identified interior ready to install.

## Modern approach

Treat the cabin as optional presentation with a protected view, one reusable interior service and a third hybrid skin consuming the existing model. Keep aiming, contacts, warnings, chat and full interactive panes sharp and accessible. Use MFDs for suitable detail, with projected-size fallback, rather than forcing every row onto perspective-shrunk screens. Begin with a generic authored low-poly cockpit and one display, prove visibility and hardware cost, then refine its actual appearance. The study supports doing that work; it does not support enabling the primitive captures or hiding Classic wholesale.
