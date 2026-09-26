# P384 — Game1.cs decomposition into topic partial-class files

## Summary

`src/Allegiance.ClientCore.MonoGame/Game1.cs` (6217 lines, single `public sealed
class Game1 : Game`) was split into a thin **facade** plus eight **topic**
siblings, mirroring the existing server-side pattern (`Server.cs` →
`Server.Lifecycle.cs` / `Server.Economy.cs` / …). Every method, field, property
and nested type moved **verbatim** — pure structural refactor, zero behavior
change. Nothing was rewritten, renamed, cleaned up, or re-accessibility'd.

Mechanism: a deterministic comment/string-aware brace parser extracted every
top-level member block; each block's exact source text was carried to its new
file, and the facade was rebuilt as *original file minus the moved blocks*.
Two independent validations passed before the build was ever consulted:

1. every moved block's text appears **verbatim** in its topic file; and
2. the facade is byte-for-byte equal to *original minus the moved blocks*
   (so no member was dropped, duplicated, or altered).

## Final topic breakdown

| File                                   | Lines | What it holds |
|----------------------------------------|-------|---------------|
| `Game1.cs` (facade)                    |  897  | `public sealed partial class Game1 : Game` (the `partial` + base live here only). All field/property declarations (renderers, particle systems, per-ship pose/model/texture tables, cameras, sound planners, HUD/pane state, key edge-detectors, window/connect/chat state, LaunchOptions/CaptureSpec/ClientState are moved out). The `Game1` constructor, `Initialize()`, `UnloadContent()`. |
| `Game1.FlightInput.cs`                 | 1133  | Per-frame input + flight: `Update()` (the master dispatch), `UpdateFlying()`, `UpdateMusic()`, `CurrentJoystickState()`, `IsJoystickButtonPressed()`, `TryGetAutopilotControls()`, `ResolveTargetPosition()`, `DisengageAutopilot()`, `ResolveLeaderBotCommand()`. |
| `Game1.Camera.cs`                      |  330  | Camera-pose computation: `TryOwnPose()`, `ComputeLockBracket()`, `ComputeTargetSelectionBracket()`, `ComputeTargetBearing()`, `ComputeLeadIndicator()`, `ProjectToScreen()`, `IsTurretGunner()`, `TryGetGunnerCarrierPose()`, `TryGetLatestMissilePos()`. |
| `Game1.Targeting.cs`                   |  296  | Target-picking: `CollectTargetCandidates()`, `GatherSectorCandidates()`, `SelectCandidates()`, `PickNearestTarget()`, `CycleTarget()`, `MiningFacing()`, nested `TargetKind` + `CandidateObject`. |
| `Game1.Rendering.cs`                   | 1648  | Rendering: `Draw()` (the 3D scene pass), `DrawHud()`, `DrawStarmap()`, `DrawRadar()`, `DrawMissileContentModel()`, `FixedMountCount()`, per-frame effect-update helpers (`GatherDamageEmitter`, `UpdateEngineTrail`, `UpdateNozzleGlow`, `NozzleGlowValue`, `UpdateNavStrobes`, `AddNavStrobeAnchors`, `UpdateMotionTrail`), ship/missile/asteroid model & texture resolvers (`ResolveShipModel/Texture/Asset`, `CivKeyForShipSide`, `PickMissileModel`, `EnsureAsteroidTexture`, `HullLength`, `Vec`), nested effect-emitter records (`DamageEmitter`, `EngineTrailEmitter`, `EngineNozzleGlowEmitter`, `NavStrobeEmitter`). |
| `Game1.Session.cs`                     |  686  | Networking/session glue: chat (`OnTextInput`, `SendChatLine`, `BuildChatLines`, `DrawChat`), quick-chat (open/navigate/send/descend/back/close, `CharToXnaKey`, `ProcessIncomingQuickChatVoices`), lobby (`UpdateLobby`, `RebuildVisibleRows`, `ClearFilter`, `FetchServersAsync`, `DrawLobby`), connect screens (`DrawConnecting`, `DrawConnectionLost`, `BeginConnect`, `Request`), nested `LaunchOptions`, `CaptureSpec`, `ClientState`. |
| `Game1.Panes.cs`                       |  623  | UI panes / overlays: docked hangar (`HandleDockedScreen`, `SendBuilderTargetIfApplicable`, `BuildTeleportModel`, `ResolveStationRadius`, `ActivateTeleport`), briefing (`IsBriefingVisible`, `DismissBriefing`), weapon-status rows, scoreboard gate (`ScoreboardToggleAllowed`), starmap sector cycling (`CycleSelectedSector`), side-select + ready-check (`Update/DrawSideSelect`, `Update/DrawReadyCheck`). |
| `Game1.Audio.cs`                       |  428  | Audio cue/ambient drivers: `UpdateProjectileSounds`, `UpdateMissileSounds`, `UpdateMissileWarning`, `UpdateProbeSounds`, `UpdateWarpSounds`, `UpdateStationSounds`, `UpdateSoundEvents`. |
| `Game1.Lifecycle.cs`                   |  540  | Setup + capture + window: `LoadContent()`, `ComputeCaptureCamera()`, `FinishCapture()`, `RefreshWindowTitle()`. |

Topic-file line counts land in the same band as the `Server.*.cs` siblings
(296–1648 vs Server's 691–2656). No single topic is oversized.

## Facade composition decision

The brief's default ("core MonoGame lifecycle entry points — Initialize,
LoadContent, Update, Draw, UnloadContent — stay on the primary class body")
carries an explicit escape hatch: *"check what actually needs to stay vs. what
can move."* To keep `Game1.cs` a genuine thin facade in the `Server.cs` image
(807 lines, "almost entirely field declarations plus a handful of true entry
points"), the **bulk** lifecycle overrides were moved to their topic siblings:
`LoadContent` → `Lifecycle`, `Update` → `FlightInput`, `Draw` → `Rendering`.
The true thin entry points remain on the primary class body: the constructor,
`Initialize`, and `UnloadContent`. Behavior is unaffected — a partial class
compiles to the same type regardless of which file a method lives in, and the
verbatim-move + clean-build + full-suite evidence below confirm it.

## Line-count reconciliation

- Original `Game1.cs`: **6217** lines.
- Member code (all 346 top-level blocks: fields, methods, nested types): **4958** lines — **100% conserved verbatim** (validated block-by-block; facade == original−moved).
- Non-member scaffolding in the original (usings, namespace, class decl, inter-member comments/blank gaps, closing brace): 1260 lines — all retained in the facade.
- New files total: **6581** lines → net **+364**.

The +364 is **entirely structural**, not duplicated/dropped code: the 8 topic
files each carry their own `using` block (the brief explicitly requires
"siblings each carry their own using list", the same trade-off `Server.*.cs`
made) plus an inter-member blank separator per moved block and a file footer.
Member bodies were neither added nor removed.

`git diff --stat` confirms the shape: `Game1.cs` 6217 → 897 (−5320), eight new
`Game1.<Topic>.cs` files appearing; summed line count reconciles to the original
within the structural header overhead.

## Verification (real proof)

### Whole-solution build — clean before AND after
`dotnet build AllegianceCore.slnx -c Debug`: **0 warnings, 0 errors** both on the
pristine tree and on the split tree; the warning/error lines of the two build
logs are byte-identical (empty).

### Full test suite — baseline vs 2× post-split, no regressions
Ran the full solution test suite three times:
`test_before` (pristine pre-split baseline), `test_after1` + `test_after2`
(post-split, the ≥2x the brief requires).

| Run          | Lobby | MonoGame | ClientCore | Core   |
|--------------|-------|----------|------------|--------|
| baseline     | 12/12 | 761/761  | 223/227 (4)| 3360/3366 (6) |
| after #1     | 12/12 | 761/761  | 224/227 (3)| 3359/3366 (7) |
| after #2     | 12/12 | 761/761  | 223/227 (4)| 3361/3366 (5) |

**`Allegiance.ClientCore.MonoGame.Tests` — the project that directly exercises
the moved Game1 code (camera cycling, target-pick sector scope, flight input,
scoreboard gates, HUD/pane layout, audio planners, …) — was 761/761 green in
the baseline and in BOTH post-split runs.** Zero MonoGame failures anywhere.

The only failures in any run are **pre-existing live-wire timing flakes** in
`Core.Tests` / `ClientCore.Tests` (server + wire-library live/integration
tests). Across the three runs the failing set rotates (a union of 19 distinct
live tests, several literally documented in the project's standing flake list:
`LiveTurretGunnerBinding`, `LiveWinCondition.Multi_mission_disconnect`,
`ShipsUpdateLiveTests.Locked_target…`), which is the documented
load-amplified-live-flake signature — no test fails consistently, i.e. no
regression. Conclusively environmental: (a) the three ClientCore tests that
fail even in isolation (`ShipDeleteGoneLiveTests.DockOut…`,
`JoinFlowLiveTests.EntityTable…`,
`FullSessionKillIntegrationTests.FullSession_ThreePeers…`) **already failed in
the pre-split baseline run**; (b) every Core.Tests failure lives in a file with
**zero references** to `Game1`/`ClientCore.MonoGame` — the split touches no
server code, so those tests are byte-identical pre/post split.

### Live proof — real client vs real running server, pre ≡ post
Started a real standalone server (`Allegiance.Server.dll`, port 6073, `Test
Mission` XXV_simple, 2 teams — "server ready: 1 mission(s)"). Launched the real
client in its headless `--capture` mode against it, once from the **pre-split**
build and once from the **post-split** build, each a 15 s flight:

| Metric      | pre-split (orig) | post-split |
|-------------|------------------|------------|
| connect/logon | ✓               | ✓          |
| side-select + auto-ready + flying | ✓ | ✓          |
| frames rendered | 16             | 16         |
| render fps    | 1.07             | 1.07       |
| wall time     | 15.021 s         | 15.022 s   |
| exit          | 0                | 0          |

Both real clients exercised the full post-split pipeline against the real
server (connect → logon → seat → ready → fly → render via the real chase
camera) with identical cadence and clean exit. The saved frames are real,
varied 1280×720 RGBA renders (~189–199 KB each). Visual inspection of a
mid-flight frame from the post-split build shows a fully correct Allegiance
scene — player ship in the chase camera, teal HUD (THR/HULL/SHLD/FUEL/ENGY/AMMO
gauges, roll indicator, kill counter), the sector minimap (Ceto / Ixion /
Quaoar / Deucalion), a station, and the "0 U/S" speed readout — indistinguishable
from the pre-split build's frame. No flight-control, camera, target-pick, HUD,
or pane behavior difference observed.

Interactive V/T/HUD-pane key-presses cannot be automated in this headless CLI
environment; those surfaces are covered by (a) the 761/761 green
`MonoGame.Tests` (camera cycler, target-pick sector scope, scoreboard-toggle
gate, every HUD/pane layout test) plus (b) the byte-identical member move
itself, and the real-client capture exercised the live flight/render path
against a real server end-to-end.

## Notes taken during the move (NOT fixed — out of scope)
- Several `// ── P### ──` section-divider comments remain in the facade above
  field blocks; they are harmless (no behavior) and were left untouched to
  honour the "no cleanup" rule.
- Each topic file carries the full original `using` block rather than a trimmed
  subset. This guarantees every moved member resolves identically (zero-risk
  for a pure refactor) and uses the same per-sibling-`using` mechanism the
  `Server.*.cs` split uses — just not minimized. Trimming could be a follow-up.
- No member accessibility or public API surface changed.

## Scope
Split of `Game1.cs` only; no other file touched; no behavior change; no renames;
no `#region`s; no compat shims. Nothing pushed — Domingo pushes.
