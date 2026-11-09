# P972 — Switchable UI skins, Modern 2.0 and the Jet military HUD

**Status:** design proposal only; no production source, test or parity CSV change is included.  
**Scope:** phase 1 must ship the skin system with Classic and Modern visually unchanged before Modern 2.0 or Jet is enabled.  
**Native reference:** read-only `WinTrek/`, `Inc/` and installed artwork MDL definitions. Native artwork paths below are logical `Artwork/...` paths, not machine-specific paths.

## Executive proposal

1. **Phase 1 — skin infrastructure, zero visual delta.** Replace the persisted two-value style choice with a versioned, validated skin id and an immutable, pre-bound skin session. Keep Classic and Modern renderer output pixel-identical while moving palette/font/stroke/layout ownership behind the abstraction. Resolve files, ids, renderer factories, fonts and resources only when content loads or the player changes skin—not in the frame loop.
2. **Phase 2 — Modern 2.0.** Keep every Modern feature, option and key. Establish a type/spacing/grid system, stronger contrast and adaptive density; retain the native-allowed contact set and make declutter a visual presentation policy, never a visibility or targeting rule.
3. **Phase 3 — Jet.** Add a green-phosphor vector skin (amber variant) with flight-path marker, sector-plane attitude ladder, tapes, stores, target designator/lock/closure, caution panel and RWR-style radar. All 111 current HUD parity rows receive an explicit equivalent or an explicit shared-audio/world-effect treatment. Nothing is removed.
4. **One shared model and input contract.** Skin changes presentation only. Targeting, radar LOD eligibility, commands, chat, training, loadout transactions, audio triggers, simulation and wire data remain shared. Every existing both-style test becomes a registry-driven all-installed-skins test, with explicit Classic policy pins where native behaviour intentionally differs.

## 1. Current-state audit

### 1.1 What exists now

`HudStyle` really is only `Classic, Modern` (`src/Allegiance.ClientCore.Game/Rendering/HudSettings.cs:6`). A fresh `HudSettings` selects Modern (`:13`); an unreadable/missing file selects Classic for an existing player and Modern for a fresh profile (`:139`). Two important migration details follow:

* **Current invalid-enum behaviour is not the requested new behaviour.** `Normalize` changes an undefined enum to Classic (`HudSettings.cs:118`); P972 requires an unknown **skin id** to fall back to Modern. This must be a deliberate migration test, not an accidental side effect of replacing the enum.
* **The brief's 75–150% description is stale for persisted settings.** P833 widened `ModernHudScale.MinSize/MaxSize` to **0.5–2.0** (`Rendering/ModernHudScale.cs:26-27`), with density × user size capped by the window's 1.5-units-per-1080px budget (`:54`, `:73-83`). The Ctrl+F6 row still steps/clamps **0.75–1.5** (`Game1.GpuBudget.cs:154`). Phase 1 must preserve both behaviours exactly; unifying that pre-existing discrepancy is a separate decision, not a skin-system drive-by.

Modern is not just a colour swap. `ModernHudRenderer` uses SDF text, geometric gauges and its own composition (`Rendering/ModernHudRenderer.cs:68-80`, `:111-175`), while `HudRenderer` owns the shared draw flow plus Classic art and native font selection. The existing extension points—text scale/font, radar labels, gauges, target pane and inventory—are virtual (`Rendering/HudRenderer.cs:566`, `:793`, `:826`, `:1361`, `:2523`, `:3132`). That is a useful migration base, but not yet a complete skin boundary.

### 1.2 Every production style branch

The production audit found executable `HudStyle` branches in **nine files**, plus comments in two others. Test/tool `InlineData` uses are much more numerous and are addressed by the registry-driven test plan below.

| Location | Current decision | Phase 1 treatment |
|---|---|---|
| `Rendering/HudSettings.cs:6,13,58,71,118,139` | Enum, default, Modern defaults for selection bracket/declutter, invalid/fresh/existing migration | Persist a string skin id; resolve nullable option defaults from the selected skin definition. Preserve known Classic/Modern defaults; unknown id → Modern. |
| `Game1.GpuBudget.cs:30,153-154` | Choose active renderer; two-way options toggle; size step | Replace with resolved active renderer + catalog cycle. Keep the two-skin phase-1 options row visually identical; later skins extend the same row. |
| `Game1.Lifecycle.cs:317-334` | Constructs Classic, Modern and chat with different artwork/fonts | A skin factory owns preloading/caching/disposal. No file, font or texture resolution in `Draw`. |
| `Game1.Rendering.cs:1833,1839` | Classic chat/cargo/parts drawn outside `ActiveHud` | Move ownership behind the skin frame renderer while preserving exact z-order and Classic output. Do not duplicate Modern's overlays. |
| `Game1.Rendering.cs:3439-3452` | Classic radar external; accessible ship tags only Modern; target preview and selection gate on active renderer; Classic pod/death external | Make radar, ship-tag palette and feedback explicit skin services. Keep model gates and target preview identity shared. |
| `Game1.FlightInput.cs:1978-1989` | `0` cycles HUD levels only in Modern | Query a resolved capability, not `Skin == Modern`: Classic false; Modern, Modern 2.0 and Jet true. Radar `.` remains independent and unchanged. |
| `Game1.Commanding.cs:473` | Classic-only external command-summary lines | Route through the skin's command-summary widget; keep text/model and Classic placement unchanged. |
| `Rendering/CareerScreenRenderer.cs:39-46` | The only non-flight renderer with a direct Modern/Classic panel-style branch | Resolve screen palette roles. Current Classic and Modern values remain byte-identical in phase 1. |
| `Game1.TrainingIntegration.cs:9` | Training snapshots/restores `HudStyle` | Snapshot the canonical skin id and Jet variant, restore after every training exit path, and cover fallback behaviour. |

`HangarScreenRenderer` explicitly gives both current styles the same native artwork (`Rendering/HangarScreenRenderer.cs:22-24`). That must remain true in phase 1. Modern 2.0/Jet screen art is a later opt-in renderer, not a recolour of the native hangar.

### 1.3 Hard-coded look outside the enum

Turning the enum into a string without addressing these call sites would produce a fake skin system:

* **HUD tokens:** Classic accent/panel colours are static fields (`HudRenderer.cs:15-16`, with more native/semantic colours through the file); Modern accent/muted/alert/cloak/carry colours are static (`ModernHudRenderer.cs:33-37`), and its accessible relationship/side palettes live in `HudPalette` (`:789-803`). Gauge spread, target pane scale, stroke widths, opacity and dozens of fixed anchors are code constants.
* **Fonts:** Classic chooses native font identities; Modern maps every identity to its two SDF fonts (`ModernHudRenderer.cs:79-80`). This needs a font-role resolver, but Classic's per-role native sizes cannot be flattened into one generic font size.
* **Radar and tags:** `RadarBracketRenderer` is constructed separately in the two paths; the colour-blind mapping is applied to ship tags only under the Modern branch (`Game1.Rendering.cs:3441-3443`). Contact shape and label eligibility are semantic and stay in the shared model.
* **Intro and screens:** intro title/dim colours are local values (`Game1.IntroScreen.cs:753-755`), selection fills are local (for example `:814`), and career has its own boolean style branch. Other overlays (scoreboard, game over, help, ready check, team/side select, quick chat, options, key bindings, docked/loadout) also contain fixed colours and geometry. The new interface therefore needs a **screen-chrome** contract as well as a flight-HUD contract.
* **Shared native content is not automatically skinnable:** training slideshow artwork, target model content, world effects and native hangar art are content surfaces. Classic/Modern keep them unchanged. New skins may frame or replace a screen renderer only after every action and state has an equivalent; they must not tint simulation/world imagery to fake cohesion.

#### Complete UI presentation call-site inventory

A repository-wide direct-draw/colour search gives the following implementation checklist. These are the places that must either consume resolved screen/HUD tokens or be explicitly classified as shared native/world/diagnostic content; a new `HudSkinCatalog` alone cannot make them switch consistently.

| Group | Hard-coded presentation call sites found |
|---|---|
| Flight radar/chat/tags | `Rendering/ChatRenderer.cs:18-20` (panel/border/text), `Rendering/RadarBracketRenderer.cs:127-129` (contact colours and further overlay values), `Rendering/ShipSideTagRenderer.cs:43-49` (tag offset/width/stroke), plus `HudRenderer`/`ModernHudRenderer` above. |
| Docked and modal renderers | `Rendering/DockedScreenRenderer.cs:34-36`, `ConnectionLostScreenRenderer.cs:31-33`, `GameOverScreenRenderer.cs:37-39`, `ScoreboardOverlayRenderer.cs:36-38`, `PilotInfoOverlayRenderer.cs:38-40`. |
| Help/briefing/team renderers | `Rendering/HelpOverlayRenderer.cs:34-36`, `KeyBindingRenderer.cs:26-28`, `MissionBriefingRenderer.cs:34-36`, `ReadyCheckOverlayRenderer.cs:25-27`, `SideSelectOverlayRenderer.cs:25-27`, `TeamPaneRenderer.cs:29-31`, `SectorInfoPaneRenderer.cs:24-26`, `StarmapRenderer.cs:42-44`. |
| Other UI renderers | `Rendering/CareerScreenRenderer.cs:19-29,39-46`, `ChatRenderer.cs:18-20`, `CreditsOverlayRenderer.cs:26-28`, `QuickChatMenuRenderer.cs:18-20`, `HangarScreenRenderer.cs:72-105` (native art + shared geometry), and the native-font/SDF construction in `Game1.Lifecycle.cs:317-334`. |
| Direct intro/host/browser draws | `Game1.IntroScreen.cs:753-755` and its view-specific draw methods, `Game1.Session.cs:867`, `Game1.CreateGame.cs:123`, `Game1.HostLan.cs:230`, `Game1.JoinLink.cs` (draws using the supplied intro palette rather than owning a second palette). |
| Direct options/pane/game draws | `Game1.GpuBudget.cs:271`, `Game1.Panes.cs:2213`, `Game1.Hangar.cs:217`, `Game1.Commanding.cs:358,473-477`, `Game1.FlightParts.cs:324`, `Game1.Rendering.cs:1833-1843,4788-4832`, `Game1.Skirmish.cs:203`, `Game1.Slideshow.cs:43` (native slide content stays white/shared). |
| Benchmark/diagnostic/touch draws | `Game1.Benchmark.cs:87`, `Game1.BenchmarkSharing.cs:322`, `Game1.Inspector.cs:452`, `Game1.InFlightUtility.cs` (direct draw with caller-supplied values), `Game1.Touch.cs:244`. These need focus/layout preservation and semantic colours where skinned; debug/world geometry must not become gameplay-affecting skin data. |
| Geometry consumed by those draws | Fixed layout is also outside the two HUD classes: e.g. `Rendering/InventoryPaneLayout.cs:17`, `MinimapGeometry.cs:19`, `MinimapLayout.cs:14`, `OwnStatusLayout.cs:135`, `RadarBracketLayout.cs:52`, `TargetPaneLayout.cs:16`, and the native console/chat rectangles in `Hud10Layouts.cs`. The complete screen-layout family (`GameOverTablesLayout`, `HelpOverlayLayout`, `InvestmentLayout`, `LobbyStatusLayout`, `MissionBriefingLayout`, `MissionSettingsFormLayout`, `PartInfoLayout`, `PilotInfoLayout`, `RadarLabelLayout`, `ReadyCheckLayout`, `ScoreboardLayout`, `SectorInfoPaneLayout`, `ShopLayout`, `SideSelectLayout`, `StarmapLayout`, `TeamCivPickerLayout`, `TeamNameEditLayout`, `TeamPaneLayout`, `TeleportLayout`) must be classified as **semantic native geometry** (shared) or **skin geometry** (resolved), not mechanically made configurable. |

World/mesh/effect renderers found by the same broad colour search are intentionally excluded from UI skinning. Target preview, slideshow and native hangar content have the explicit shared-content treatment above. Phase 1F's “remaining overlay renderers” means this entire UI inventory, including the unglamorous host, diagnostics and touch paths.

### 1.4 Shared model, native coordinates and draw order

`HudModel`/`HudContext` is the correct boundary: it already carries telemetry, visibility, target, contacts, console, inventory, training and lifecycle state without textures or fonts. `HudElements` is the canonical 111-row coverage inventory used by the Modern renderer's missing-element assertion (`tools/hud2-proof/Program.cs:126`). The new skins must consume this model; they must not read the live world independently.

The coordinate rules are deliberately **not** “everything is y-up”:

* `HudPoint` is centre-relative **y-down**, while native `HudPlacement` defaults to native **y-up** (`Rendering/HudModel.cs:7-10`; `Rendering/HudElements.cs:3-7`). Conversion belongs in one tested layout adapter.
* Native `StringImage` has negative-Y bounds (`engine/image.cpp:354`) and decrements `y` before drawing (`:382-390`): text hangs below its point in native image space. Font substitution must not silently change that anchor.
* Native `GroupImage::Render` walks the MDL value list from first to last (`engine/image.cpp:954-969`), so the first child paints last/on top. Skin render passes must preserve that order, including radar/chat/target-preview compositing.
* Native hangar **screen panes** are top-left, y-down, distinct from HUD images; the current port documents and pins this at `Rendering/HangarScreen.cs:12-23`, with uniform 800×600 letterboxing at `:150-159`.

### 1.5 Proof assets and fixtures that already exist

`tools/hud2-proof` is a deterministic production-renderer fixture using real fonts/artwork and `RenderTarget2D` (`tools/hud2-proof/Program.cs:27-55`). It covers 16 scenarios, five resolutions, three sizes and two renderers (`:46-55`) and checks real text bounds, inventory fit and every visible `HudElement` (`:74-131`). It is the right base for golden equivalence, but currently hard-codes `new HudRenderer[] { classic, modern }` (`:55`) and does not include 800×480 or 4K.

Real-window automation also exists (`scripts/live-proof.sh`, `tools/hud2-proof/capture-loopback.py`, and the P949 proof). This report includes the matched P949 real 800×480 Classic/Modern frames in `UI_SKINS_P972_assets/current-*-real-800x480.png`. They show the current small-window reality: Classic's segmented grey arcs and dense range labels, Modern's cyan rings and Tactical tag, and the one-time key hint in both. They are evidence of the **current** UI, not P972 renderings.

### 1.6 Native HUD intent relevant to this proposal

Native already supplies the flight-path/velocity marker: cockpit-only, rounded non-zero speed, successful projection and an additive centre surface (`WinTrek/indicator.cpp:128-150`). Joystick, lead, accepted/current-order, training and direction indicators share the internal/chase/missile-camera indicator pass (`:762-797`), with accepted=current yellow and distinct accepted/current green/red (`:783-792`). Jet re-skins those existing semantics; it does not invent a second flight director.

Native radar labels and contact eligibility remain the authority. P828 verified the important-contact and Default-ship rules at `WinTrek/radarimage.cpp:703-710` and `:747-757`; Modern declutter selects only from that allowed set. P972 preserves that rule in every skin. The incoming missile warning remains the native/source-ship audio path; neither a green RWR nor a lock symbol may claim an incoming-locker identity that the model does not possess.

## 2. Phase 1 skin-system design

### 2.1 Architecture: resolve once, draw through immutable references

Use two boundaries:

1. **Definition/catalog boundary (load/switch time):** id, version, palette roles, font roles, stroke/layout values, renderer ids, presentation defaults and optional sound-set id. JSON is cheap for these values and makes a new palette/layout possible without another enum. Arbitrary executable behaviour is never loaded from JSON.
2. **Resolved frame boundary (Draw):** an immutable `ResolvedHudSkin` containing arrays indexed by compile-time roles, loaded font/texture handles and direct widget-renderer references. `Game1` holds one active reference. No JSON, file IO, string parsing, id lookup, palette dictionary lookup, reflection or resource construction occurs per frame.

Proposed new files (all under `src/Allegiance.ClientCore.Game/` unless stated):

| New file | Responsibility |
|---|---|
| `Rendering/Skins/HudSkinIds.cs` | Canonical ids (`classic`, `modern`, later `modern2`, `jet`), role enums and id normalization. Ids are data; runtime arrays use enums/integers. |
| `Rendering/Skins/HudSkinDefinition.cs` | Serializable definition records and schema version; palette/font/stroke/anchor/widget/sound records. |
| `Rendering/Skins/HudSkinJson.cs` | Source-generated JSON context, preserving the project's AOT/no-reflection rule. |
| `Rendering/Skins/HudSkinLoader.cs` | Size/depth/string/number/enum validation, whole-definition failure, duplicate-id rejection, unknown renderer/font/sound rejection and diagnostic log. |
| `Rendering/Skins/HudSkinCatalog.cs` | Ordered installed catalog, built-in fallback, definition load, `Cycle`, `Resolve`, and one-time unknown-id warning. Modern is always registered before fallible user skins. |
| `Rendering/Skins/ResolvedHudSkin.cs` | Immutable palette/font/stroke/layout arrays, direct renderer references, capabilities and viewport-size cache. Built only on load, skin/variant/size/density/viewport change. |
| `Rendering/Skins/IHudSkinRenderer.cs` | Flight renderer contract, coverage reporting, optional radar-icon/target-preview attachment and disposal. |
| `Rendering/Skins/IHudWidgetRenderers.cs` | Gauge, indicator, target, radar, console/chat, inventory and feedback widget contracts. Widget instances are created at bind time, not from a per-frame factory. |
| `Rendering/Skins/IScreenSkinRenderer.cs` | Intro, hangar, loadout and shared overlay chrome; consumes existing screen models/layout rectangles, never their commands. |
| `Rendering/Skins/HudSkinSwitcher.cs` | Atomic between-frame switch, options/F-key request queue, persistence after successful activation, variant selection and one-line toast. |
| `Content/Skins/classic.skin.json`, `modern.skin.json` | Phase-1 values extracted verbatim from the current renderers. Packaged content, no user-write requirement. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/` | Catalog/migration/schema/switch/performance tests and the all-skins theory data. |

Phase 2 adds `modern2.skin.json`, `Modern2HudRenderer.cs` and focused widget/layout files. Phase 3 adds `jet.skin.json`, `jet.amber.skin.json` (a palette variant referencing the same renderer ids), and `Rendering/Jet/` widget files. New code renderer ids are registered explicitly in code; a definition cannot load a type by name.

### 2.2 Contracts

Illustrative shape (names are proposal API, not code added by this report):

```csharp
public interface IHudSkinRenderer : IDisposable
{
    string Id { get; }
    HudSkinCapabilities Capabilities { get; }
    void Bind(in HudSkinResources resources);       // load/switch-time only
    void Draw(HudModel model, in HudDrawFrame frame); // resolved refs only
    void SetRadarIcons(RadarIconTextures? icons);
    void SetTargetModelPreview(TargetModelPreviewRenderer? preview);
}

public interface IHudWidgetRenderers
{
    IHudGaugeRenderer Gauges { get; }
    IHudIndicatorRenderer Indicators { get; }
    IHudTargetRenderer Target { get; }
    IHudRadarRenderer Radar { get; }
    IHudConsoleRenderer Console { get; }
    IHudInventoryRenderer Inventory { get; }
    IHudFeedbackRenderer Feedback { get; }
}

public sealed record HudSkinCapabilities(
    bool SupportsDensityLevels,
    bool SupportsAccessibleSidePalette,
    bool UsesCompactScreenChrome,
    bool SupportsPaletteVariants);

public sealed record HudSkinPresentationDefaults(
    bool SelectionBracket,
    bool RadarDeclutter,
    HudLevel DensityLevel);
```

`HudDrawFrame` contains the viewport, measured layout, resolved palette/font/stroke arrays and sprite passes. `IHudWidgetRenderers` exposes direct references; a renderer never asks a dictionary for “the target renderer” each frame. Existing `HudRenderer`/`ModernHudRenderer` are adapted behind this interface first. Their virtual calls are not a new hot-path cost; P972 must not add another registry/virtual-factory layer beneath every glyph.

The resolved defaults/capabilities replace every current `Style == Modern` policy branch explicitly:

| Skin | Selection bracket default | Declutter default | Initial density | `0` density key | Accessible side mapping available |
|---|---|---|---|---|---|
| Classic | off (native) | off (native) | Tactical value retained but ignored | no | no automatic remap |
| Modern | on | on | Tactical (current) | yes | current behaviour |
| Modern 2.0 | on | on | Auto (new opt-in visual default) | yes | current behaviour |
| Jet | on | on | Auto | yes | monochrome shape/text/pattern mapping (never hue-only) |

An explicit player `SelectionBracket`, `RadarDeclutter`, `RadarLabelMax`, `Level` or colour-blind preference always wins over these defaults. Switching skins changes no native radar LOD, Centre/Target HUD visibility or chat-history toggle.

A separate `IScreenSkinRenderer` covers intro, hangar/loadout and other menus. Its input is a semantic screen model plus **the same resolved rectangles used by hit testing**. It cannot buy, launch, sort, join, navigate or change focus itself.

### 2.3 Definition data: data where cheap, code where necessary

A definition contains:

* `schema`, `id`, `name`, `version`, optional `variantOf` and `extends` (built-in base only, no recursive user includes);
* semantic palette roles: `textPrimary`, `textMuted`, `panel`, `panelStrong`, `accent`, `warning`, `critical`, `shield`, `friendly`, `hostile`, `neutral`, `cloak`, `carry`, plus native fixed colours that Classic must preserve;
* font roles (`nativeSmall`, `nativeMedium`, `nativeLargeBold`, `body`, `numeric`, `display`) with face, source pixel size, SDF/bitmap mode and fallback chain;
* stroke widths, glow limit, corner radii, spacing grid, opacity and line caps;
* named anchors/scales for gauge ring, own status, speed/throttle, target, minimap, console, inventory and screen safe areas;
* renderer ids for the widget groups and screen chrome;
* presentation defaults (selection bracket, declutter, density level) and capabilities;
* optional sound-set id mapping **semantic existing events** to installed samples. Sound switching never retriggers a cue, changes lock timing or creates another warning controller.

Validation is all-or-nothing for a skin. Unknown schema/renderer/font, invalid colour/NaN, non-positive stroke, unsupported character range, duplicate anchor or failed required resource rejects that definition and leaves the prior skin active. At startup an unknown persisted id selects Modern. A bad Modern built-in is a startup error, not a silent blank HUD. Optional user definitions live under the profile's `skins/` directory with a small file/count budget; packaged Classic/Modern/Modern 2.0/Jet need no user files.

### 2.4 Settings and backward-compatible migration

Replace persisted `Style` with canonical `Skin` (JSON string), while accepting the old field during load:

| Input | Result |
|---|---|
| Fresh profile, no readable HUD file | `modern` |
| Existing player, no readable HUD file / no style field | `classic`, preserving current migration |
| Legacy `"Style":"Classic"` / `"Modern"` (including the existing string-enum representation) | corresponding canonical id |
| Legacy numeric 0 / 1 | Classic / Modern |
| `"Skin":"classic"`, `"modern"`, later `"modern2"`, `"jet"` | that installed skin |
| Unknown id or unknown legacy enum value | `modern`, with one diagnostic |
| Known id with different case/surrounding whitespace | normalized canonical id, with one diagnostic |
| Malformed JSON on an existing/fresh profile | preserve the current existing-player/fresh-profile fallback respectively |

The next ordinary settings save writes `Skin` and drops the legacy field. Loading itself does not rewrite a user's file. `JetPalette` (`green`/`amber`) is a separate persisted variant so the F-key cycles skins, not every colour variant. Existing nullable `SelectionBracket` and `RadarDeclutter` continue to override skin defaults; `Level`, `Size`, colour-blind preference, chat lines and native visibility toggles remain profile options, not copied into each skin file.

`TrainingPreferences` saves/restores both fields. Unknown skin fallback must not erase unrelated settings or reset the key layout, transport, mouse options or the P949 one-time control hint.

### 2.5 Runtime options and F-key

Add rebindable `CycleHudSkin`, default **Shift+F12** in both input presets. Plain F12 is Credits and Ctrl+F10/F11/F12 are training clock controls (`Rendering/InputActionCatalog.cs:590-598`); Shift+F12 is otherwise unassigned in the catalog. It uses the same compose/modal/focus gates as other presentation actions and is consumed while a text field owns typing. Options → Graphics/HUD row 6 becomes the catalog selector; its phase-1 two-entry appearance remains unchanged. When Jet is active a variant row appears; no permanent extra blank row is imposed on Classic/Modern.

A switch request is queued and applied at the update/draw boundary, never during a sprite pass. The active reference, resolved viewport cache and screen chrome change together; resources were preloaded, so no frame hitch or half-switched frame occurs. Save only after activation succeeds, then show a short measured toast (`SKIN: Modern 2.0`, `SKIN: Jet / amber`). Switching does not reset HUD level, radar LOD, chat, target, selected part, pane open state, training state or sound loops. Preload all four built-ins at `LoadContent`; lazy-load optional user skins at selection time outside `Draw`, with a busy-safe prior-skin fallback.

### 2.6 Rendering migration order (pixel gate after every step)

1. **Characterize first:** capture unmodified Classic/Modern RGBA goldens and model/visibility snapshots; add 800×480 and 3840×2160 to the existing matrix.
2. **Catalog + persistence:** introduce ids/loader/migration and an adapter around the existing two renderer instances. No drawing code moves.
3. **Frame ownership:** adapt Classic/Modern to `IHudSkinRenderer`; move the external Classic chat/cargo/radar/command/pod-death branches behind the same frame composition, preserving pass order exactly.
4. **Tokens:** extract colours, fonts, line weights and layout constants one widget family at a time into Classic/Modern definitions. Keep native semantic colours distinct from freely skinnable accent colours.
5. **Screen chrome:** introduce palette/layout interfaces for intro, career and other overlays while preserving every current value and every existing draw/hit rectangle. Keep native hangar/slideshow art unchanged.
6. **Registry-driven tests + performance:** only after all current goldens pass, replace hard-coded two-style theories/tools with installed-skin enumeration. Then enable the phase-2 work branch.

### 2.7 Zero-change proof and all-skins test strategy

**Golden equivalence means decoded RGBA equality, not visually similar PNGs.** Before each extraction, capture the same fixture in the same process/driver with the old path; after extraction capture the new path and compare every pixel and the semantic model/visibility snapshot. PNG metadata/compression is irrelevant. Keep platform-independent semantic assertions in ordinary CI and run exact pixel goldens on the pinned software-GL fixture; do not bless a new hash merely because output changed.

Expand the current 234-combination fixture (`16 scenarios × 5 resolutions × 3 sizes × 2 skins`) with 800×480 and 3840×2160, bright/dark backgrounds, green/amber Jet and 200v200-like contact stress. Preserve the real font overlap/inventory assertions and generalize `Modern.DrawnElements` to `IHudSkinRenderer.Coverage` so every visible `HudElement` is either drawn or accounted for by an explicit shared-audio/world-effect row.

Create `InstalledSkins.All` test data. Existing `[InlineData(HudStyle.Classic)]/[InlineData(HudStyle.Modern)]` tests migrate as follows:

* **Shared behaviour/model tests:** run once where style is provably irrelevant; add one all-skins adapter smoke test proving no skin changes the result.
* **Renderer, visibility, key, settings and both-style integration tests:** run against every installed skin, including existing training and Live theories. Classic-only native policy assertions remain Classic-specific; Modern-specific rendering assertions move to capability-specific data rather than being deleted.
* **Golden/layout/coverage tests:** every skin × required resolutions × colour-blind/palette variants × relevant state. Screenshot every phase in Classic **and** Modern for regression, plus Modern 2.0 and both Jet variants.
* **Loopback/Live:** the same target/radar/pod/turret/command/training/hangar flows run for every skin, serially, with screenshots. No skin is released on headless evidence alone.

Required scenario set: flight/chase/cockpit, target + partial/full lock, bright critical background, four radar LODs, dense contacts, long chat/compose/command recipients, full inventory/stat rows, lifepod/ghost, turret/carrier, command view, training callouts/hint, docked hangar/loadout/in-turret, intro/options/key bindings/help/briefing/team/side select/game over, resize and runtime switch with panes open.

### 2.8 Performance contract and later rig measurement

The selection path must allocate **0 B/frame** and perform no lookup beyond reading the active reference. Viewport/density/size changes rebuild a cached `HudDrawFrame` outside the hot loop; contact selection retains P828's reusable buffers. New glow is bounded/pre-rendered—no full-screen blur and no synchronous luminance readback.

After local proof, Claude should A/B/A/B the migrated Classic and Modern against the captured baseline on the rig, using identical scene/replay, window, graphics settings and warm-up. Record frame CPU/GPU p50/p95/p99, FPS, renderer allocations, switch hitch and 200v200 label cost. Acceptance: 0 B from skin selection/dispatch, no new per-contact allocation, and no >2% p95 frame-time regression beyond run noise; any regression blocks later skins. No rig or performance run was performed for this design report.

## 3. Modern 2.0 skin

### 3.1 Visual direction and non-negotiables

Modern 2.0 is a **readability revision of Modern**, not Modern-minus-features. The flight model, target fields, inventory statistics, console states, native visibility gates, radar LODs, all Modern HUD levels/options and every key remain. Full density can still show every native-allowed label. The proposed default for this opt-in skin is `Auto`: Tactical in an ordinary sector and Combat in a dense one; the player's explicit level/nearest-N choices still win.

The mockup uses a quiet teal/slate base, warm target/caution emphasis, thin keylines and dark glass only where text needs it. The centre is intentionally emptier than current Modern. Target, own ship, weapons and radar are four stable edge zones; transient orders/chat/tips use reserved bands rather than drifting over gauges.

### 3.2 Tokens and typography

| Token | 1920×1080 reference | 800×480 compact | Rule |
|---|---:|---:|---|
| Layout grid | 8 px | 4 px | All pane gaps/insets snap to grid; no arbitrary 1–3 px drift. |
| Body text | 16 px | 11 px minimum | DejaVu Sans/SDF; actual measured ink, not only font em size. |
| Secondary/label | 12–13 px | 9–10 px minimum | Uppercase + modest tracking for labels, never long paragraphs. |
| Primary numeric | 28–34 px | 22–24 px | DejaVu Sans Mono; speed/range/resources align by numeric column. |
| Panel inset | 16 px | 10 px | Consistent title/body/footer rhythm. |
| Standard gap | 8 px | 6 px | Related items 4/8 px; unrelated groups 16/24 px. |
| Hairline | 1 px | 1 px physical | Never scale below one physical pixel; use opacity for de-emphasis. |
| Strong symbol | 2 px | 1.5 px | Target/lock/caution remains visible over stars. |

At 4K, use the existing density × user-size rule and window cap, with cached SDF rasterization at physical size. Do not merely scale a 1080p bitmap to 4K and do not let 4K turn the centre cluster into a second menu. Long names use measured fit/ellipsis in projected labels, while the target pane and inventory retain the full readable identity.

Text/symbols over space receive a dark keyline and, only for measured text zones, a dark plate. Target/body text contrast goal is **4.5:1** on its backing and critical thin symbols **3:1** against the local backing; validate on the existing bright `critical` fixture as well as black space. No GPU readback is needed to choose a backing: layout knows its plates, and projected labels use the established measured-background path.

The colour-blind-safe palette retains shape and text redundancy: friendly = chevron/blue, hostile = diamond/amber, neutral = circle/white; critical resources add `!` and numeric values. Side identity is not collapsed to friend/enemy—the current nearest-native-side accessible mapping remains available.

### 3.3 Information hierarchy

1. **Immediate combat:** centre gun/velocity markers, selected-target designator, lock state, range/closure, in-range and incoming warning.
2. **Survival:** armour/shield/energy/fuel/ammo/thrust, signature, detection/cloak, carried objective and pod oxygen.
3. **Intent:** accepted/queued orders, current command, weapon group/arming/mounting and autopilot/vector lock.
4. **Situation:** native-allowed contacts/labels, minimap/starmap, sector, turret/carrier and command summary.
5. **Meta:** chat/tips/vote/kills/lives, HUD level, camera, diagnostics and control hint.

Higher tiers never erase lower-tier features; compact layouts consolidate them. The full inventory pane remains the authoritative detailed stores view. Expanded starmap, chat compose, inventory and modal screens take their normal focus and reserved space.

### 3.4 Declutter at 200v200

Modern 2.0 builds on P828/P836 rather than introducing another visibility law:

* native `RadarLabelModel.Resolve` runs first; a skin can never add a label that the selected native LOD suppresses;
* target, accepted/queued orders, chat/command subjects, attackers/threats, carried objectives and own ship remain priority;
* Tactical keeps priority + nearest N; Combat uses the existing enemy-first ordinary budget and friendly icons; Full preserves every allowed label; Cinematic and Auto retain their current key/menu semantics;
* overlap groups become deterministic `xN` tags, ordinary off-screen contacts become edge pips under the same level rules, and icons/brackets retain native semantics;
* at a 200v200 clash the proposed Auto→Combat presentation shows the selected target, threats/orders and nearest enemy labels rather than a wall of friendly names. This is visual selection only—no contact, target, command or wire data is removed.

Acceptance includes an enemy-dense 200v200 capture, not just a one-sided crowd, and a label-by-label assertion that Full's allowed set is unchanged.

### 3.5 Compact and large-window composition

At **800×480**, own ship condenses to a two-column status card, weapons to two rows, target to name/range + two bars, and radar to a smaller corner pane. Secondary decorative subtitles may drop; no visible parity element may silently disappear. Chat and notices use measured reserved bands and the existing configured line count. The 800 mockup demonstrates the compact hierarchy; the implementation must run the complete visibility/coverage suite rather than treating the mockup as proof.

At **4K**, panels remain at the same logical screen anchors while fonts/strokes rasterize at physical density. Large extra space increases clear separation, not the number of ordinary labels. Ultrawide safe areas clamp centre-projected labels away from edge panels. At 200% user size, the existing height cap and `HudTextLayout` overflow policy still apply.

## 4. Jet military skin

### 4.1 Look and colour variants

Jet is a collimated-HUD-inspired **spaceflight presentation**, not an aircraft aerodynamics model. Thin vector strokes, round flight-path marker, angular designators, numeric stores and a restrained RWR create the military feel. No horizon, altitude, Mach number, G-force, radar lock identity or weapon capability is invented.

The green variant uses one hue: primary `#79FFC0`, dim `#3DA87B`, hot/alert `#C9FFE3`. The amber alternative swaps the **entire** palette (proposed primary `#FFD280`, dim `#B98A45`, hot `#FFF0C2`), not just alerts. Relationship, lock and warning states use shape, text, line pattern and flash rather than a second hue. This preserves the monochrome requirement and makes the skin intrinsically less hue-dependent.

Every glyph has a near-black outer keyline; plates are near-black green/amber-neutral and scanlines/glow never cross text ink. Minimum standard stroke is 1.5 px at 1080p and 1 physical pixel at 800×480; glow is a subtle pre-rendered under-stroke, not additive bloom. Hot flash follows existing damage/lock timing. Text stays horizontal even when the ladder rolls.

### 4.2 Flight symbology

| Jet element | Exact data/behaviour source and adaptation |
|---|---|
| Gun cross | Existing cockpit reticle/firing/turret gates; small broken cross, not a second lead point. |
| Flight-path marker (FPM) | Existing native velocity-direction projection and rounded non-zero-speed gate (`WinTrek/indicator.cpp:128-150`). Circle + wings + tail, visually distinct from gun cross and ballistic lead. |
| Pitch/attitude ladder | Ship orientation relative to the **sector reference plane**, the same artificial reference used by command/grid presentation. No planetary horizon/altitude. A new `HudFlightReference` presentation snapshot carries the existing own-ship quaternion + reference plane into `HudContext`; quaternion-based projection rolls the ladder, shows numeric pitch/off-scale chevrons and has a tested vertical singularity fallback. The renderer never reads the live world. |
| Heading tape | Own forward projected onto that sector plane; degrees are presentation navigation only. Near vertical, retain last valid sector heading and mark heading unavailable rather than jumping randomly. No steering change. |
| Speed tape | Existing own-ship speed (`mps`), fixed moving tape and boxed numeric; pod speed uses the same value. |
| Throttle tape | Existing −1..+1 throttle mapped through the current model; afterburner/movement override and turret −1 behaviour remain shared. Numeric percent cannot conceal reverse/idle detent. |
| Ballistic lead / aim predictor | Existing granted lead, green ramp and out-of-range grey semantics; Jet maps ramp to dim→hot monochrome plus `IN RANGE`/shape. Chase/no-target aim remains separate. |
| Order steering | Existing accepted/current/same-target arrows and colours become open/closed/double chevrons plus `ACC`/`CUR`; direction and visibility unchanged. |
| Joystick/mouse/training | Existing joystick mesh/turn magnitude, mouse stick/notices and training callout geometry get thin-vector equivalents; none is removed for looking cleaner. |

### 4.3 Target, lock and stores

The target box uses four corner strokes sized from the existing screen radius. Lock progress is a segmented diamond/arc: partial = open/dashed + `LOCK nn%`, full = closed/hot + `LOCK`; no lock = no cue. Range, sector fallback, closure, hull/shield, TTI (only where the model supplies it), ore, kill bonus and in-range state remain separate fields. Proposed closure convention is **positive = decreasing separation**, computed from the same existing positions/velocities as Modern; unknown values stay blank. Add optional `ClosingSpeed` to `HudTargetPane` from that same shared derivation so an off-screen target does not gain/lose the reading based on projection.

Missile lock tone/flash uses the existing `MissileLockFeedback` and native sound dispatch. Skin changes must not restart a loop or add a second tone. Incoming missile warning remains owner/source-ship based; RWR never identifies “who locked you” without data.

The compact stores block lists gun/ammo, missile count/arming, armour/shield/energy/fuel/throttle and active equipment. F4 opens the full inventory with every current key, selected/active state, empty warning, quantity, mount/arming bar, range/damage/rate, shield strength/recharge, afterburner speed/endurance, cloak endurance, turret occupant, cargo and empty slot. “Stores” is a presentation grouping, not a reduced inventory.

### 4.4 Master caution/warning and RWR

A single caution panel aggregates **existing** warnings: damage flash, low/critical resources, missile-track warning, boundary/crowd, critical tips, death/ghost and pod oxygen. Each has icon + words + current value where available; priority is missile/damage/pod, then critical resource, then boundary/system. Existing native timers and audio rising edges remain authoritative; the panel does not acknowledge, suppress or delay them.

The RWR-style circle is another renderer for the **same native-radar-allowed contact snapshot**. It uses own-relative bearing/range bands, distinct ship/station/warp/rock/missile/probe/mine/treasure/tech shapes, selected/order/subject/threat overlays, labels and LOD rules. To avoid reconstructing bearing from a clamped screen point, the shared snapshot builder adds optional `RelativeBearingRadians` to `HudContact`, derived from the same already-available world pose used for that contact's native projection. This is presentation-only data—no new wire field, target input or contact inclusion rule. Null bearing falls back to the ordinary projected scope rather than inventing a direction. The RWR grants no 360° knowledge beyond contacts already available. Missiles threatening the own side retain their native tier; damage threats flash from the existing threat field. Expanded Tab starmap remains unchanged in capability and navigation.

### 4.5 Jet at 800×480 and 4K

At 800×480 the ladder shows fewer rungs (numeric pitch remains), tapes become short edge strips, stores collapse to three readout rows, caution becomes two hot lines and RWR shrinks without dropping contact classes. The target designator and lock survive at full stroke contrast. Detailed stats remain one F4 pane away. At 4K the same vector paths rasterize sharply at density, with no sub-pixel hairlines and no inflated centre ladder. Every Jet green layout also runs in amber; variant choice is a palette swap with identical measured boxes.

## 5. Element-by-element coverage — Modern 2.0 and Jet

The row names below are verbatim from `docs/parity/parity-matrix.csv:389-499`. **Every one of the 111 HUD rows is listed.** “Shared” means the existing model/data/timing remains authoritative; it does not mean the row may be skipped by a renderer. Visual changes must not silently promote a `done-visual` row to full native parity or demote a `done` row.

### 5.1 Own ship and flight status (17 rows)

| HUD parity row | Modern 2.0 equivalent | Jet equivalent |
|---|---|---|
| Hull integrity | `ARMOR` bar + exact % in own-ship card; `!` critical state | Segmented `ARMOR/HULL` store + numeric; damage flash and caution |
| Shield charge | Blue/shape-distinct bar + exact % | `SHIELD/CHARGE` segmented store + numeric |
| Energy reserve | Compact energy bar + %; equipment detail unchanged | `ENG` store + numeric, low-energy caution |
| Fuel reserve | Fuel bar + %, lifepod becomes oxygen treatment | `FUEL` store; pod `OXY` with same fraction |
| Ammunition reserve | Ammo bar/% plus gun readout | `GUN` store + ammo %/count and empty `A` cue |
| Thrust setting | `THR nn%` beside speed; reverse/idle detents retained | Throttle tape + numeric and detents |
| Speed | Large numeric `mps` aligned bottom-centre | Boxed speed tape + `mps` numeric |
| Kill bonus | `KB nn%` own-status line | `KB nn` stores/status line |
| Roll tutorial cue | Compact roll strip/callout using shared training gate | Ladder roll pointer + unchanged training callout |
| Signature percentage | `SIG nn%` with measured label | `SIG nn` status readout |
| Detection eye | Eye icon + `EYE`, not colour alone | Eye outline + `EYE` in status/caution |
| Cloak active | Cloak icon + `CLOAK` text | `CLK` line + outlined cloak glyph |
| Cloak strength and drained state | `CLOAK nn%` + `LOW` state | `CLK nn` + flashing `DRAINED` text |
| Autopilot status | `AP` chip with existing binding/help | `AUTO` annunciator with same gate |
| Vector lock status | `VECTOR` chip | `VLOCK` annunciator |
| Carried flag | Flag outline + `FLAG` objective chip | Flag pictogram + `FLAG` line; shape redundant |
| Carried artifact | Artifact diamond + `ARTIFACT` | Diamond + `ART` objective line |

### 5.2 Selected target (12 rows)

| HUD parity row | Modern 2.0 equivalent | Jet equivalent |
|---|---|---|
| Target name | Full name in target card; projected label may measured-fit | Target designator readout, full name in target detail |
| Target type description | Type under name | `TYPE` stores/target-detail line |
| Target side identity | Side name + relationship shape; accessible side palette retained | Side name + `HOSTILE`/`FRIEND`/neutral shape and text, not hue inference |
| Target hull | Numeric % + bar | `HULL nn` segmented bar |
| Target shield | Numeric % + distinct bar | `SHLD nn` segmented bar |
| Target range or sector | Range in metres or native `LOCATION` fallback | Range/closure readout or sector fallback, never fabricated zero |
| Target speed | Target `mps` in expanded target details | Target `SPD` readout |
| Target ore | `ORE nn%` on relevant target/label | `ORE nn` on relevant designator/detail |
| Target kill bonus | Target `KB nn%` | Target `KB nn` |
| Target in weapon range | `IN RANGE` text + closed diamond | `IN RANGE` + closed designator; out-of-range open/dim |
| Target model preview | Existing live 3-D preview in a framed target-pane bay; no fake substitute | Same live preview in a vector-framed bay; optional monochrome material treatment only if asset-independent |
| Target time to intercept | `TTI n.n s` only when model supplies it | `TTI n.n` only for the same non-null model value |

### 5.3 Centre indicators (10 rows)

| HUD parity row | Modern 2.0 equivalent | Jet equivalent |
|---|---|---|
| Cockpit centre reticle | Minimal centre dot/cross with firing/turret tag | Broken gun cross with firing/turret tag |
| Velocity direction marker | Ring-with-wings `VELOCITY`, distinct from aim | FPM circle/wings/tail, same native projection/gate |
| Joystick turning indicator | Small direction chevron + magnitude | Open directional pointer + magnitude, native preference gate |
| Ballistic lead marker | Existing lead ring/ramp with accessible shape | Open lead circle + monochrome ramp and in-range shape |
| Aim hit-probability predictor | Predictor ring/cross using existing green ramp | Predictor brackets using dim→hot monochrome ramp |
| Chase weapon aim without target | Grey/neutral chase aim marker | Open chase aim cross, unchanged fallback gate |
| Accepted and current order turn arrows | Two arrow shapes + `ACC`/`CUR`; combined arrow when same | Open/closed/double chevrons + text, same directions/gates |
| Selected target bracket | Four corner strokes and range/name | Target designator box, option default from skin with player override |
| Missile lock flower | Segmented lock ring, partial % and full `LOCK` | Segmented diamond lock, partial/open vs full/closed + existing tone |
| Training HUD callouts | Same callout geometry/text in Modern 2.0 chrome | Thin-vector callout in Jet chrome; training control flow unchanged |

### 5.4 Radar, contacts and labels (27 rows)

| HUD parity row | Modern 2.0 equivalent | Jet equivalent |
|---|---|---|
| Radar viewport and round border | Compact square scope or native round option | Square tactical scope when off; circular RWR when round option is on |
| Radar ship contacts | Native icon set / accessible chevrons by relationship | Ship triangles; own/friendly/hostile distinguished by orientation/fill + text |
| Radar station contacts | Station square/octagon | Station square with centre tick |
| Radar warp contacts | Aleph ring | Aleph open circle + radial ticks |
| Radar asteroid contacts | Rock dot/appropriate native icon | Small open rock polygon; special-rock label preserved |
| Radar missile contacts | Missile dart with native tier | Missile dart + `M`, no inferred locker identity |
| Radar probe and mine contacts | Probe cross / mine diamond, native tiers | `P` cross / mine diamond + native tiers |
| Radar treasure and tech contacts | Treasure/tech distinct icons | `T` diamond / tech double diamond |
| Radar accepted order brackets | Existing accepted bracket art translated to accent corners | `ACC` open corner bracket at same contact/radius |
| Radar queued order brackets | Existing queued bracket treatment | `QUE` dashed bracket at same contact/radius |
| Radar nearest enemy bracket | Hostile nearest-enemy diamond | Double hostile triangle/bracket, same nearest-enemy field |
| Radar threat damage flash | Threat flash + `!`, shared timing | Hot/dashed threat flash + `!`, shared timing |
| Radar command subjects | Subject bracket + chat/command priority | Subject square + `SUB`, same recipient set |
| Radar carried flag marker | Native flag marker / vector flag at bracket | Flag glyph at same expanded-blip radius |
| Radar carried artifact marker | Artifact marker | Artifact diamond at same expanded-blip radius |
| Radar ripcord marker | Ripcord icon + countdown | `RIP` radial marker + countdown |
| Radar construction highlight | Existing full-brightness highlight, **no invented bracket** | Hot label brightness only, **no invented bracket** |
| Radar name labels | Priority/nearest-N measured labels; Full keeps all native-allowed | Monochrome labels selected by same level policy; native allow-set first |
| Radar range labels | Metre values attached to surviving labels | Range attached to surviving labels; no range when native clips it |
| Radar hull and shield bars | Two accessible microbars with numbers in expanded label | Two monochrome line stacks/patterns, same native stats tier |
| Radar ore and miner fill bars | Fill microbar from same oreSeen/miner data | Hatched fill bar, same oreSeen/miner data |
| Radar friendly detection eyes | Eye glyph in label icon column | `EYE` glyph in same label column/gate |
| Radar friendly ripcord seconds | `RIP n s` | `RIP n` with same rounded seconds/cap |
| Teleport lead-up sparks (ripcord + aleph) | Shared world-space particles, not flattened into a 2-D icon | Identical shared world-space particles/timing; Jet adds no duplicate effect |
| Radar command verb labels | Existing native command icon / fallback and accepted/queued text | Vector command glyph + `ACC`/`QUE`, same command id and fallback |
| Radar LOD modes | All/Default/Target/Minimal and mode label unchanged | Same four modes and label; RWR never bypasses them |
| Radar range fade | Level-driven existing fade; Classic remains flat native alpha | Same level-driven fade mapped to monochrome opacity; no extra sensor rule |

### 5.5 Console, alerts and lifecycle (26 rows)

| HUD parity row | Modern 2.0 equivalent | Jet equivalent |
|---|---|---|
| Sector location | Top-right sector label/value in reserved zone | `SECTOR` readout in same information tier |
| Turret carrier identity | `TURRET OF` + carrier in top-right | `TURRET / carrier` readout, same source-ship identity |
| Observer and turret board state | Loadout observer/turret text + native in-turret badge equivalent | Loadout `OBSERVER`/`TURRET` badge only for the same board states |
| Accepted order text | `+` icon and accepted order line | `ACC` + order verb/target |
| Queued order text | Queued icon and separate line | `QUE` dashed icon + order verb/target |
| Chat history | Configured-height chat block, sender side semantics, leader bold, scroll markers | Monochrome channel/sender text with bold leader and same history/scroll/channels |
| Chat composition and recipient | All five compose states with recipient and caret | Same five states in command-line console; typed input/focus unchanged |
| Command recipient verb and wing controls | Recipient/verb/wing bar with same hit rectangles and fan-out | `CMD` console with same actions/hit model and fan-out |
| Tip text lines | Three ordinary slots in reserved activity stack | Same slots/side-colour fallback represented by text/brightness |
| Critical tip text lines | Four critical slots with shared lifetimes | Hot critical slots with same 30 s/7.5 s lifetimes |
| Vote banner | Measured centred vote banner at native configured-chat offset | `VOTE` banner with same text/offset and Y/N input |
| Money credits | **No flight counter**; existing investment-pane credit box | **No flight counter**; investment `CREDITS` readout only |
| Lives remaining | Native conditional lives badge | `LIVES n` badge under same <10/ghost rules |
| Mission kills | Deathmatch-only kills badge | `KILLS n` under same deathmatch gate |
| Mission countdown | Existing game-state countdown/elapsed surface | `MISSION T−/T+` in game-state pane, same countdown rule |
| Ripcord countdown | Centred `Ripcord in: n`, measured 150-wide native reserve | Centred `RIP n`, same rounding/null gate and reserved area |
| Eject pod identity panel | Pod card + return-to-base binding | `LIFEPOD` panel + return-to-base binding |
| Lifepod oxygen endurance | `OXY` time + bar from same endurance | `OXY` segmented endurance + numeric, same fraction |
| Death banner | Ghost/death banner with same IsGhost gate | `PILOT DOWN / GHOST` hot banner, same IsGhost gate |
| Boundary and crowd warnings | Existing audio plus retained port text badges | Same shared audio plus `BOUNDARY`/`CROWD` caution lines; no new visual-native claim |
| Incoming missile warning | Existing owner/source-ship loop plus visual lock warning | Same loop/pitch/disarm plus `MSL TRACK` caution; no duplicate controller |
| Damage flash timer | Existing gauge flash; optional wash remains a profile option | Hot gauge/caution flash with same 0.25/0.25/2.0 s timing; wash option respected |
| HUD style and software layouts | Skin selector + centre/target/chat visibility controls unchanged | Same selector/controls plus green/amber variant; native Classic layouts untouched |
| Console pane visibility and slide animation | Existing 0.5 s slide progress applied to new geometry | Same progress/sound/mask edges applied to vector panes |
| HUD base class contract | `IHudSkinRenderer` adapter retains model/camera/rect responsibilities; no native behaviour claim | Same shared adapter; Jet is another concrete presentation, not another world model |
| Game list screen boundary | Server browser gets Modern 2.0 screen chrome around the same model | Jet browser chrome around the same model; subscription/disposal unchanged |

### 5.6 Equipment, inventory and command summary (19 rows)

| HUD parity row | Modern 2.0 equivalent | Jet equivalent |
|---|---|---|
| Flight equipment inventory pane | Full measured stores pane, same slide/visibility | Full vector stores pane, same slide/visibility |
| Equipment part identity and key | Part name + live binding column | `SLOT/KEY/PART` readout with same bindings |
| Equipment selected and active state | Selected fill + active marker independent of colour | `SEL` box + active solid/dashed marker independent of colour |
| Equipment quantity and empty warning | Quantity + `A` empty warning with reserved column | `QTY` + hot `A`, same cargo aggregation/normalization |
| Equipment mounting progress | `MOUNT` progress bar from same fraction | Hatched `MOUNT` bar, same fraction |
| Launcher arming progress | `ARM` progress + `NOT READY`/`READY` | Segmented `ARM` bar + text, same clocks |
| Equipment weapon range | Full range stat in details | `RNG` stat, same speed × lifespan calculation |
| Equipment shot damage | Damage stat | `DMG` stat, same power+blast calculation |
| Equipment burst rate | Rate stat | `ROF` stat, same 1/dtimeBurst calculation |
| Equipment shield strength | Strength stat | `SHD STR` stat, same fraction × max |
| Equipment shield recharge seconds | Recharge seconds stat | `RECH n s` stat, same formula |
| Equipment afterburner top speed | Top-speed stat | `AB SPD` stat, same GA-scaled formula |
| Equipment afterburner endurance | Endurance/Inf. stat | `AB END` / `INF`, same fuel formula |
| Equipment cloak endurance | Cloak strength + endurance/Inf. | `CLK STR/END` / `INF`, same formula |
| Inventory turret gunner identity | Occupant rows and turret icons | `TURRET n / occupant` rows, same passenger/loadout data |
| Inventory cargo slots | Selected-first cargo rows, pack amounts and S key | `CARGO` rows with same order, amounts and S key |
| Inventory own ship name | Full source-ship/hull title | `SOURCE / hull` title; gunner still shows carrier loadout |
| Equipment empty slot affordances | Dim empty mount row + eligible key | Open empty-mount outline + `EMPTY`, same eligibility |
| Command view ship summary | Own/target bars, range/location/speed in command chrome | Tactical command readout with identical fields and no port-invented extras |

The two rows whose treatment is intentionally not a new 2-D widget—**Teleport lead-up sparks** and **Incoming missile warning**—remain real shared world/audio behaviour. Listing them as “done” still requires their existing evidence to pass under every skin; a decorative substitution would be a feature regression.

## 6. Menus, hangar and loadout

### 6.1 One screen model, one draw/hit geometry source

A skin is not complete if only the flight HUD changes. `IScreenSkinRenderer` covers intro, server browser/connect, options/privacy, single-player/training chooser, career/squads/events, create game, hangar, loadout/shop, investment, team/side select/ready check, briefing, starmap/sector info, scoreboard/game over, help/credits, key bindings, quick chat and in-flight panes. Native slideshow pictures remain content; only their surrounding navigation/chrome may change.

Each screen keeps its existing model and command handlers. Where a new skin changes geometry, the active skin supplies an immutable layout consumed by **both** draw and hit test. No duplicated “visual rectangle” and “click rectangle.” Keyboard focus, text input, Enter/Escape, arrows, Tab and all rebindable actions are identical. Classic and Modern use their existing geometry/art in phase 1.

### 6.2 Intro menu

The current 13-command list is one shared `IntroScreenModel.MainLabels` (`src/Allegiance.ClientCore.Game/IntroScreenModel.cs:44-48`). Modern 2.0 and Jet show all 13 in the same order, including Direct Connect, Showcase, Career and both hosting commands. The highlighted row, pilot field and hints remain in stable zones. The mockups use a wide two-zone composition; compact 800×480 uses the existing compressed-column concept, whose current 34 px design pitch / 16 px minimum lives at `IntroLayout.cs:30-34,44-65`.

The P946 focus rules are not reskinned into mouse-only buttons: Up/Down always move selection, Enter activates, Tab toggles allowed text focus and typed characters never activate menu commands (`IntroScreenModel.cs:89-103`). Modern 2.0 uses calm typography and a ship/mission card; Jet uses a scope-like line motif and monochrome panel, but neither changes activation.

### 6.3 Docked hangar

Classic/Modern retain the native 800×600 hangar artwork and uniform letterbox. Modern 2.0/Jet may use the proposed station/loadout composition only through a skin-specific layout shared with `HangarControls`. All eight native actions remain—Launch, Command, Loadout, Invest (subject to developments), Help, Teleport/Board, Team and Exit—plus port Buy/Default. The hover text, missing-art fallback, in-turret state-2 badge and 0.7 s launch animation retain their current semantics. Jet can animate vector bay doors over the same 0.7 s progress; it cannot turn launch into a different transaction.

At exactly 4:3 or 800×480, no action may be clipped into an unreachable letterbox. The current port-only Buy/Default placement rule and all hit rectangles need compact-layout tests. The mockup's `LAUNCH` and `SHIP LOADOUT` are visual states, not a claim that the current native hangar has already changed.

### 6.4 Loadout/shop

The proposed right-hand stores table exposes hull/parts, mount status and transaction availability using `ShopModel`/`ShopLayout`; Buy, Default and Launch keep their existing money-shortfall, saved-loadout, ACK and optional-launch sequencing. No client-side instant purchase is introduced for multiplayer. Full part descriptions and investment/credit surfaces remain reachable. The loadout observer/turret badge stays loadout-only while the native hangar badge remains hangar-only.

### 6.5 Other screens and native content

Modern 2.0 uses the same grid/type/palette roles as flight. Jet uses squared vector frames, tabular numbers and monochrome focus markers. Selection always has a box/caret, disabled rows have text/reason, and errors are icon + words. Tables preserve native sort orders and roster fields rather than dropping columns to fit the aesthetic; compact screens scroll or paginate through the existing model.

Native Classic/Modern hangar, target models and training slide art are not globally hue-tinted. Jet may frame a real target preview and apply an optional asset-independent monochrome material, but missing art follows the existing fallback instead of an invented ship silhouette. Screen captures for release include every screen family, not only the two requested mockups.

## 7. Mockup and real-capture index

All SVGs are self-contained (no scripts, remote images or external fonts required), have explicit pixel dimensions/viewBox and carry `DESIGN MOCKUP - NOT A CAPTURE` (compact `P972 DESIGN MOCKUP` at 800×480). They were rendered locally and visually read back. Classic/Modern SVGs are faithful **design approximations** of their current layout, not golden images.

| Skin / screen | 1920×1080 | 800×480 | What the read-back shows |
|---|---|---|---|
| Classic as-is flight | [SVG](UI_SKINS_P972_assets/classic-flight-1920x1080.svg) | [SVG](UI_SKINS_P972_assets/classic-flight-800x480.svg) | Grey segmented six-gauge ring, native-like range text, right systems/sector blocks, bottom weapon/speed/camera labels |
| Modern as-is flight | [SVG](UI_SKINS_P972_assets/modern-flight-1920x1080.svg) | [SVG](UI_SKINS_P972_assets/modern-flight-800x480.svg) | Cyan arcs/percentages, Tactical/Default/COCKPIT labels, current systems/sector blocks and centre speed |
| Modern 2.0 flight | [SVG](UI_SKINS_P972_assets/modern2-flight-1920x1080.svg) | [SVG](UI_SKINS_P972_assets/modern2-flight-800x480.svg) | Clear centre, priority target, velocity marker, consolidated own-ship/weapons/sector cards, `xN` contact groups; compact cards at 800 |
| Jet green flight | [SVG](UI_SKINS_P972_assets/jet-flight-1920x1080.svg) | [SVG](UI_SKINS_P972_assets/jet-flight-800x480.svg) | Single-hue heading/pitch/FPM/tapes, target lock/closure, stores, caution and RWR; condensed compact symbology |
| Modern 2.0 hangar/loadout | [SVG](UI_SKINS_P972_assets/modern2-hangar-loadout-1920x1080.svg) | — | Eight hangar actions + Buy/Default, central ship/launch, full part/status table and purchase acknowledgement text |
| Jet hangar/loadout | [SVG](UI_SKINS_P972_assets/jet-hangar-loadout-1920x1080.svg) | — | Same complete action/loadout inventory in green vector treatment |
| Modern 2.0 intro | [SVG](UI_SKINS_P972_assets/modern2-intro-1920x1080.svg) | — | All 13 commands, pilot/profile, keyboard hints and mission card |
| Jet intro | [SVG](UI_SKINS_P972_assets/jet-intro-1920x1080.svg) | — | Same 13 commands/focus hints with monochrome scope motif |

### Real current-state captures

A headless fixture **does exist**, so real current-skin captures are included rather than substituted with mockups:

| Capture | File | Visible evidence |
|---|---|---|
| Classic, real 800×480 window | [PNG](UI_SKINS_P972_assets/current-classic-real-800x480.png) | Current segmented arcs, dense native range labels, `WEAPONS: GROUP`, `BOOST: OFF`, systems/target/starmap text and the P949 fresh-profile key hint |
| Modern, real 800×480 window | [PNG](UI_SKINS_P972_assets/current-modern-real-800x480.png) | Current cyan gauge rings, `HUD Tactical`, `RADAR Default`, signature/speed, systems/starmap and the same shared key hint |

Source: `docs/reports/P949_assets/p949_hint_{classic,modern}.png`, real presented-window captures from the P949 loopback proof. The copies are byte-identical (SHA-256 `ecfe5538ae8762daf66168ac5f54c3d4d80fe2c5aa1b31ea7c4127638545f3b0` and `ff02cfa7160518138c728187285918c3710e5980a237681b3f5f94c4958362f3`). No new live capture was needed for this design-only task; no 200v200, rig or runtime skin-switch result is claimed.

## 8. Risks and parity hazards

| Risk / affected rows | Why it can break a feature | Required guard / stop condition |
|---|---|---|
| Draw-pass reorder — chat, radar, cargo, command, target preview, pod/death (`hud:Console pane visibility and slide animation`, chat/radar/target rows) | Moving external Classic calls into a skin shell can change z-order or double-draw Modern | Pixel goldens + explicit pass-order test; native GroupImage first-child-on-top convention; stop on any Classic/Modern delta |
| Anchor inversion (`hud` text/pane/gauge rows) | Applying y-up to projected coordinates or hangar panes displaces/clips text | Shared conversion tests at corners and centre; StringImage negative-Y and hangar y-down fixtures; 800/4K captures |
| Declutter becomes sensor filtering (`hud:Radar LOD modes`, all contact/label rows; commander rows) | Hiding contacts from the model would affect target/orders/native LOD, not just readability | Resolve native allow-set first; assert identical model contacts/target/commands across skins; Full allowed-set comparison; enemy-dense capture |
| Jet invents aircraft/lock data (FPM, heading, RWR, incoming warning, TTI) | Sector plane is not a planet horizon; wire lacks incoming-locker identity; unknown values cannot become zero | Quaternion singularity tests, unknown-null tests, source-ship warning tests; no altitude/Mach/G or locker label |
| Palette erases side/alert semantics (side colours, threat, orders, cloak, selected/active states) | Green-only or accessible mapping can make friend/foe/selected indistinguishable | Shape + text + line-pattern redundancy; colour-blind captures; nearest-native-side tests; green/amber same geometry |
| Classic native colours/fonts change (`done` console/inventory/gauge rows) | “Unifying” tokens can alter native yellow/red/white, native font roles or string baselines | Native roles remain separate from accent roles; per-widget zero-RGBA gate; no Classic palette simplification |
| Runtime switch leaks state (`hud` chat/inventory/panes; `screens` training/options) | Switch could close panes, reset LOD/level/chat/target, retrigger lock audio or leak training preference | Atomic switch with open panes/compose/lock; all training exits; sound-loop identity test; save-after-success test |
| Key collision/focus theft (`screens:Intro screen`, key rebinding, training) | New F-key could fire while typing or conflict with Credits/training | Shift+F12 binding in both presets, compose/modal gates, rebind/reset persistence, P946/P949/training suites |
| Settings migration (`hud:HUD style and software layouts`) | Invalid enum currently becomes Classic; new requirement is unknown skin → Modern; legacy profiles must not change | Table-driven legacy string/numeric/missing/malformed/unknown tests; AOT JSON round-trip; no load-time rewrite |
| Screen feature loss (`screens` hangar/loadout/browser/team/help) | A visual redesign can remove Buy/Default, a host command, table column, focus path or hit area | Semantic action checklist per screen, shared draw/hit rectangles, all 13 intro labels, full purchase ACK and keyboard proofs |
| Resource hitch/disposal (all render rows) | Loading SDF/fonts/textures on first switch stalls; disposing shared native art kills the other skin | Preload/refcount ownership, between-frame switch, allocation/switch-hitch measurements and disposal guard |
| Small/4K readability (every visual row) | Hairlines vanish, SDF blurs, fixed panels overlap, 200% size exhausts free space | Physical-pixel stroke floor, measured bounds, 800/4K/size/density matrix, bright/dark shots; no overflow fallback that hides a required element |
| Performance regression (P828/P836 zero-allocation paths) | JSON/dictionaries/factories or glow in Draw adds GC/GPU cost exactly in crowded battles | Resolved references, cached arrays/paths, 0 B selection/dispatch, p95 A/B gate and no synchronous readback |
| Mockup mistaken for implementation | SVGs can omit transient/native states and are not evidence of working controls | Explicit mockup watermark (present); require production renderer + real-window all-skins evidence before release |
| Parity scorecard overclaim (all `done`/`done-visual` rows) | A new look does not prove a previously visual-only row is native-complete | Keep statuses; append implementation evidence/reasons only after both old skins pass and new-skin coverage is separately recorded |

The highest-risk slices are **phase-1 pass ownership**, **screen draw/hit geometry**, **training preference restoration**, and **Jet RWR/heading semantics**. They should not be delegated as one large “skin refactor” patch.

## 9. Phased implementation plan for delegation

Each row is a separately reviewable vertical slice, in order. **Do not start phase 2 until phase 1's zero-delta gate is green; do not expose Jet until its complete screen/feature gate is green.** Update `GLOSSARY.md` when the skin system lands and append parity evidence/reasons to the named rows without upgrading their status by assertion alone.

### Phase 1 — skin system, Classic + Modern unchanged

| Slice | Files / work | Acceptance and tests | Parity surface |
|---|---|---|---|
| **1A. Baseline and inventory** | Extend `tools/hud2-proof` only with baseline capture/manifest, 800×480 + 4K, semantic visibility dump and current real-window shot matrix. No renderer edits. | Existing 234 combinations pass; decoded RGBA baseline stored outside repo; all 111 row names map to coverage ids; current Classic+Modern captures read back. | All `hud` rows; baseline for `screens` rows. No status change. |
| **1B. Id, schema and migration** | Add `Rendering/Skins/{HudSkinIds,HudSkinDefinition,HudSkinJson,HudSkinLoader,HudSkinCatalog}.cs`; adapt `HudSettings.cs`; built-in `classic.skin.json`/`modern.skin.json` initially carry ids/defaults only. | Every migration row in §2.4 passes; unknown → Modern, missing existing → Classic, fresh → Modern; source-generated JSON/AOT-safe; bad definitions fail closed with prior skin active. | `hud:HUD style and software layouts`; no visual delta. |
| **1C. Renderer adapter + switching** | Add `IHudSkinRenderer`, resources/switcher; adapt `Game1.Lifecycle.cs`, `Game1.GpuBudget.cs`, `Game1.TrainingIntegration.cs`; add `CycleHudSkin` to `InputActionCatalog.cs` and both input presets. | Shift+F12/options round-trip Classic↔Modern at runtime and restart; held key fires once; compose/modal gates work; training restores; switch is atomic with target/chat/panes open. Pixel goldens unchanged. | HUD style row; intro options/key binding/training screen rows; input-defaults rows in the matrix. |
| **1D. Unify frame ownership, not appearance** | Adapt `HudRenderer.cs`, `ModernHudRenderer.cs`, `Game1.Rendering.cs`, `Game1.Commanding.cs`; route Classic external radar/chat/cargo/parts/command/pod-death and Modern palette ship tags through explicit services. | Exact pass order and zero RGBA delta for every baseline; no double chat/radar/feedback; target preview survives switches; all visible elements covered. **Highest regression risk.** | All centre/radar/chat/console/pod/death/target rows. |
| **1E. Extract HUD tokens/widgets** | Add resolved palette/font/stroke/anchor arrays and `IHudWidgetRenderers`; adapt gauge, target, radar, console, inventory and feedback families one at a time; fill Classic/Modern JSON verbatim. | Zero RGBA delta after **each family**; native font roles/baselines and fixed colours pinned; no per-frame JSON/dictionary/resource work; selection/dispatch allocation test 0 B. | Own ship 17, target 12, indicators 10, radar 27, console/inventory visual rows. |
| **1F. Screen-chrome boundary** | Add `IScreenSkinRenderer`; adapt `Game1.IntroScreen.cs`, `CareerScreenRenderer.cs`, `HangarScreenRenderer.cs`, `DockedScreenRenderer.cs` and remaining overlay renderers without changing values/geometry. | All current screen goldens identical; every draw rectangle equals hit rectangle; all 13 intro actions and native hangar actions unchanged; native art/slides unchanged. | All `screens` rows, especially intro, hangar/loadout, career and pane navigation. |
| **1G. Registry tests, performance and release gate** | Add `InstalledSkins.All`; migrate both-style theories and proof tools; measure steady frames/switch; document rig handoff. | All existing both-style tests run both skins; solution 0 warnings, tools build, affected/new tests 3×, one fast tier, training gate 3× where touched; local A/B meets §2.8; rig baseline delegated to Claude. | Entire HUD/screens matrix. **Phase 2 is blocked until zero-change evidence is reviewed.** |

### Phase 2 — Modern 2.0

| Slice | Files / work | Acceptance and tests | Parity surface |
|---|---|---|---|
| **2A. Tokens + own ship** | Add `modern2.skin.json`, `Modern2HudRenderer.cs`, `Modern2Layout.cs`, resolved type/grid tokens; implement six gauges, speed, status and warnings. | All 17 own-ship rows visible with exact values/gates; 800/1080/4K and colour-blind captures; bright critical contrast; Classic/Modern goldens still identical. | `hud` own-ship rows 389–405; damage flash/style rows. |
| **2B. Target + indicators** | Add Modern 2.0 target card/preview bay, reticle/velocity/lead/aim/order/lock widgets and measured anchors. | All 22 target/indicator rows covered incl. null TTI/range and chase/no-target; partial/full lock tone not duplicated; screenshot each camera and lock state. | Target rows 406–417; indicator rows 418–427; incoming warning. |
| **2C. Radar + 200v200 declutter** | Add compact radar renderer and Auto default wiring over `RadarLabelDeclutter.cs`, `HudLevels.cs`, `RadarBracketRenderer.cs` without changing native allow-set. | Four LODs, Full allowed-set identical, priority survives, deterministic xN, enemy-dense 200v200 capture and zero-allocation selection; ordinary/offscreen/priority cases pass. | Radar rows 428–454; command subject/order rows; no simulation/wire change. |
| **2D. Console + inventory + lifecycle** | Add Modern 2.0 console/chat/command/inventory/pod/death/turret/command-view widgets. | All 26 console + 19 inventory/command rows, long text and every stat column fit; compose/focus/fan-out and 0.5 s slides unchanged; real pod/turret/training shots. | HUD rows 455–499; screens chat/team/teleport/investment rows. |
| **2E. Intro + hangar/loadout** | Add Modern 2.0 screen layout/chrome for intro, hangar and shop using shared draw/hit geometry. | Supplied mockups reviewed; all 13 intro commands and all hangar/loadout actions work at 800 and 1080; Buy/Default/ACK/launch/hover/badge/animation unchanged. | Intro rows 368–375; hangar rows 343–357; loadout rows 526–532. |
| **2F. Remaining screens + ship gate** | Skin browser/connect/options/career/training/briefing/team/ready/starmap/score/game-over/help/key bindings/quick chat; full all-skins proof. | No unskinned action-only screen or feature dead end; runtime switch across every view; all-skins tests 3×, one fast tier, training/Live proofs, 200v200 and performance gate. Modern 2.0 becomes selectable only now. | All `screens`; full `hud` regression matrix. |

### Phase 3 — Jet green + amber

| Slice | Files / work | Acceptance and tests | Parity surface |
|---|---|---|---|
| **3A. Pure symbology math + core flight** | Add `Rendering/Jet/{JetHudRenderer,JetFlightGeometry,JetIndicatorRenderer}.cs` and shared `Rendering/HudFlightReference.cs`, plus green definition; derive heading/pitch/FPM/tapes from existing own-ship/camera data in the model adapter. | Quaternion/singularity/off-scale/null tests; FPM gate matches native; reverse/idle throttle and pod cases; green contrast at 800/4K; no flight-control delta. | Speed/thrust/velocity/roll/reticle/joystick/lead/aim rows. |
| **3B. Target + stores + caution** | Add Jet target designator/lock/closure, stores/inventory and caution widgets. | All target/own/inventory fields present; closure sign fixed positive-closing; lock tone single-source; damage/caution timers exact; no invented locker; detailed F4 inventory complete. | Own 17, target 12, lock/incoming/damage, inventory 19. |
| **3C. RWR/radar** | Add optional presentation-only contact bearing in the existing snapshot builder, then Jet scope/contact glyphs/labels/overlays using the same contacts and LOD policy; square/round option. | Every contact kind/overlay/label/fill/fade covered; null-bearing fallback and no extra 360° data; native allowed-set and target/commands unchanged; dense enemy capture and allocation gate. | Radar rows 428–454; teleport sparks remain shared. |
| **3D. Console/lifecycle/training** | Add Jet console/chat/command/pod/death/turret/command summary/training callouts. | All compose states, recipients, tips/vote/orders, oxygen/ghost/carrier/callouts/hint work; training preference restore and Live lessons both palettes; screenshots every state. | HUD rows 455–499; training screens and training matrix evidence. |
| **3E. Amber variant** | Add `jet.amber.skin.json` sharing renderer/layout ids; options variant selector and persistence. | Identical measured boxes/contact visibility in green/amber; entire palette swaps (no green leftovers); unknown variant falls back green, unknown skin Modern; restart/training restore. | All Jet visual rows; HUD style/settings rows. No native behavioural difference. |
| **3F. Intro + hangar/loadout + remaining screens** | Add Jet screen chrome/layout for every screen family; optional semantic sound set only after native cues pass. | Same acceptance as 2E/2F for both palettes; all actions/hit targets/focus/ACK/animation; native warning/lock sounds retain timing; optional skin sounds never retrigger on switch. | All `screens`; HUD style/software layouts; native sound-related HUD rows. |
| **3G. Full ship gate** | Complete real-window 800/1080/4K + bright/dark + dense battle evidence, runtime switching and performance A/B; media review. | All installed-skin tests/Live proofs 3× as required, one fast tier, training gate 3×, 0 warnings/tools build, no phase-1 golden change, no parity regression, assets ≤2 MB per task and reviewed. Jet becomes selectable only now. | Entire HUD/screens matrix; explicit rig handoff for 200v200/frame baseline. |

### Required change hygiene during implementation

* Use `~/.dotnet/dotnet`; never stash, commit or push in the delegated worktree unless Domingo explicitly changes that instruction. Keep scratch under `.scratch/<task>/` and evidence ≤2 MB/task.
* Any training-affecting slice runs `tools/run-training-tests.sh --no-build --repeat 3` with artwork. Every new/affected test runs 3× alone and each phase runs one `tools/run-tests.sh --tier fast`.
* Capture Classic **and** Modern for every HUD/interface change, then Modern 2.0 and Jet green/amber for their additions. Read every cited image and describe what is actually visible.
* No new service, port or environment variable is needed. Packaged skin JSON is client content; no Docker/server/wire/simulation change is expected. A server/Core change would re-open scope and trigger the AOT gate.

## 10. Verification of this proposal

* **Scope:** `git status --short` reports only `docs/reports/UI_SKINS_P972.md` and `docs/reports/UI_SKINS_P972_assets/` as untracked. No source, test, glossary, parity CSV or other document changed; no commit or push was made.
* **Coverage:** an automated CSV/Markdown comparison passed **111/111** `hud` parity row names in exact matrix order, with no missing, extra or duplicate rows.
* **Assets:** all **12 SVGs** parsed as XML; dimensions/viewBoxes are exactly 1920×1080 or 800×480; no scripts, external images or remote resources. All **14/14** report asset links resolve. Total assets: **520,146 bytes**, below the 2 MB task cap.
* **Visual read-back:** every SVG was rendered through local headless Chromium and opened/read. The review caught and corrected the Jet throttle/caution collision, compact footer/radar collision, closure-sign inconsistency and green-variant hue inconsistency. Both real P949 PNGs were also opened/read; their copies are byte-identical to the sources (hashes in §7).
* **Citations:** every existing current-code file cited was re-grepped after report writing (20,399 lines checked), as were native `WinTrek/indicator.cpp`, `WinTrek/radarimage.cpp` and `engine/image.cpp` (4,138 lines). The report contains no personal/local absolute path, private IP or temporary-directory reference.
* **Not run, intentionally:** solution/tools builds, tests, live captures, runtime switching, golden equivalence, performance and 200v200 runs. This task allowed only a design report and mockup assets, so there is no implementation to execute or claim as verified. Those gates are specified per implementation slice in §9. New/copied media remains subject to the normal review before any future push.

## Native intent

Native's flight HUD already supplies the semantics worth preserving: real source-ship velocity drives the cockpit direction marker (`WinTrek/indicator.cpp:128-150`); indicators share defined camera/visibility gates and accepted/current-order meanings (`:762-797`); radar LOD and important/per-kind label tiers decide what information is available (`WinTrek/radarimage.cpp:703-710`, `:747-757`). Native image layout uses negative-Y `StringImage` bounds/drawing (`engine/image.cpp:354`, `:382-390`) and first-child-on-top MDL group order (`:954-969`). Native hangar screens instead use top-left screen-pane coordinates and fixed 800×600 content, a distinction the current port pins in `Rendering/HangarScreen.cs:12-23`.

Classic must therefore remain the native-layout skin, including its fixed font/art roles, native visibility toggles and unfiltered native label policy. Modern's existing declutter/levels are presentation supersets, not permission to change contact eligibility. Jet's ladder/RWR are likewise new visual projections of existing data, not new aircraft physics or sensors. Audio/world effects and unavailable values remain honest: no duplicate missile controller, no fabricated incoming-lock identity, TTI, range, ore or sector reading.

## Modern approach

A skin is a validated, versioned presentation definition bound once to immutable resources and direct widget renderers. The shared `HudModel` and screen models remain the only gameplay-facing state; native data/visibility rules remain shared and tested. String skin ids make unknown-skin fallback and future definitions straightforward, while compile-time renderer ids and source-generated JSON keep loading AOT-safe and bounded.

Modern 2.0 improves hierarchy, typography, spacing and dense-battle readability without removing Modern's options or keys. Jet adds the requested military visual language—FPM, sector-plane ladder, tapes, stores, target lock/closure, caution and RWR—in green or amber monochrome, with shape/text redundancy and the complete native HUD inventory. Runtime switching is atomic and persistent, Classic/Modern migration is proven by decoded-pixel equivalence, and every later skin ships only after all-skins behavioural, layout, Live, screenshot and performance gates pass.
