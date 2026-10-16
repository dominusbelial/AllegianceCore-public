# P715 — minimap footer spacing and fit

Branch `codex/p715`; base `477513e201a46ac3c7d01da6c83f1d2cf85c075f`. SDK 10.0.400. Build and screenshot metadata: [validation ledger](P715_assets/validation.json).

The compact minimap now reserves a measured caption band before fitting the sector graph. Current-sector names stay above their dots and neighbour names below, within the map region. The caption drops `MINIMAP` when necessary, then uses two lines if the current sector name needs more room; overlong names are measured and shortened with ASCII `...`. The full `[TAB] EXPAND` hint fits at all three requested resolutions. Minimap click handling uses the same measured layout as rendering.

## Native review and port boundary

All native references below are under `~/Development/Allegiance/src/` and were checked directly.

| Native evidence | Application to this change |
| --- | --- |
| `WinTrek/sectormap.cpp:156-161` loads separate closed/open backgrounds and an expand image. `:166-169` derives the pane bounds from the background. | Keep the port's existing compact corner panel; no native bitmap dimensions are transplanted into the responsive HUD. |
| `WinTrek/sectormap.cpp:404-407` places the expand image separately at the background's size minus the button size and `(16, 4)`. `:411-422` places the selected sector caption separately at `(xBorder + 20, yTop - 13)` and appends ` Sector`. | Native's caption and expand control have independent placements. Its sector-info render method (`:392-537`) has no equivalent to the port's combined `MINIMAP YOU ... [TAB] EXPAND` footer. Measured footer reservation, sparse dot labels and text shortening are explicitly **port UI fixes**, not a claim of pixel-for-pixel native placement. |
| `WinTrek/sectormap.cpp:753-754,777-783,810-825` uses authored coordinates, a 10% border fudge and aspect preservation; `:1310-1317` maps them into the map rectangle. | Reuse the existing `StarmapLayout.Fit` coordinate mapping inside the reduced map region, with measured name headroom above and below the dot field. |
| `WinTrek/sectormap.cpp:1324,1333-1343` tests the same drawn cluster points with a nearest-cluster radius of 15 pixels. | Keep the existing `StarmapHitTest` rule and supply the new compact layout to click handling, so moving the graph does not leave its click targets behind. |

## Changes

- `src/Allegiance.ClientCore.Game/Rendering/MinimapLayout.cs`: shared, headless-testable measured geometry for the panel, map, footer and text rectangles. Measures with the caller's font; reserves the footer before graph placement; bounds and shortens names; selects a full, shortened or two-line caption.
- `src/Allegiance.ClientCore.Game/Rendering/StarmapRenderer.cs`: draws the shared layout's points and measured text placements.
- `src/Allegiance.ClientCore.Game/Game1.StarmapClick.cs`: uses that same layout and HUD font for compact-map clicks.
- `tests/Allegiance.ClientCore.MonoGame.Tests/MinimapLayoutTests.cs`: fourteen cases across four methods. Real MonoGame `SpriteFont.MeasureString` runs headlessly with generous glyph metrics. Covers all requested viewports, bottom-row Bug Nebula/Tellus neighbours, current sector at the centre or bottom of the graph, long names, caption fallbacks, missing current-sector information, click positions and a non-clickable footer.
- `tools/minimap-proof/Program.cs`: adds `--footer-regression --window WxH --tag after`. Uses the compiled game HUD font and real `DrawMini` on a window backbuffer; emits PNGs, lossless pane close-ups and measured JSON, and fails if text exceeds its reserved region. Waits five frames for SDL's initial resize before readback. Existing live-graph proof mode remains available.

## Screenshots and actual HUD-font measurements

The deterministic fixture places Omega above bottom-row Bug Nebula and Tellus. Before images use the original `HEAD` renderer; after images use the fixed renderer. Both use the same graph and compiled `HUD.xnb`. These are window-backbuffer captures of the production renderer, with lossless crops for comparison, rather than illustrative mock-ups.

| Viewport | Before | After | Available caption width | Measured caption width | Lowest sector-label bottom to footer band |
| --- | --- | --- | ---: | ---: | ---: |
| 1280×720 | [pane](P715_assets/before_1280x720_pane.png), [full frame](P715_assets/before_1280x720.png) | [pane](P715_assets/after_1280x720_pane.png), [full frame](P715_assets/after_1280x720.png), [measurements](P715_assets/after_1280x720.json) | 316.8 px | 275 px | 18.6 px |
| 1920×1080 | [pane](P715_assets/before_1920x1080_pane.png), [full frame](P715_assets/before_1920x1080.png) | [pane](P715_assets/after_1920x1080_pane.png), [full frame](P715_assets/after_1920x1080.png), [measurements](P715_assets/after_1920x1080.json) | 344 px | 275 px | 21 px |
| 2560×1440 | [pane](P715_assets/before_2560x1440_pane.png), [full frame](P715_assets/before_2560x1440.png) | [pane](P715_assets/after_2560x1440_pane.png), [full frame](P715_assets/after_2560x1440.png), [measurements](P715_assets/after_2560x1440.json) | 344 px | 275 px | 21 px |

Before, 1280×720:

![Original minimap: footer intersects bottom names and is cut off](P715_assets/before_1280x720_pane.png)

After, 1280×720:

![Fixed minimap: bottom names stay above the separate, complete caption](P715_assets/after_1280x720_pane.png)

A separate [real flight-client screenshot](P715_assets/live/proof_t003.0_sCeto_ccockpit.png) at 1920×1080 shows the change in the complete HUD after an actual TCP connection, seat/ready countdown and `BUY_LOADOUT` launch on the **local `XXV_simple.igc` map**. It uses the existing `--auto-side 0 --auto-ready --auto-launch 13 --autoshots 3 --exit-after 6 --window 1920x1080` automation. No rig/221/200v200 proof was run.

To reproduce the deterministic after captures after building the client content:

```bash
dotnet build tools/minimap-proof/minimap-proof.csproj
for size in 1280x720 1920x1080 2560x1440; do
  dotnet run --no-build --project tools/minimap-proof -- \
    --footer-regression --window "$size" --tag after \
    --content "$PWD/src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content" \
    --out "$PWD/docs/reports/P715_assets"
done
```

## Verification

- Whole solution: `dotnet build AllegianceCore.slnx -m:8 -v:minimal` — **0 warnings / 0 errors**.
- Every `tools/*/*.csproj`: built through an MSBuild project list — **0 warnings / 0 errors**.
- The initial restore could not write NuGet's shared HTTP cache under the sandbox. Re-running with `NUGET_HTTP_CACHE_PATH="$PWD/.verify_p715/nuget-cache"` completed normally; no audit or warning checks were disabled.
- Failing-first check with the final test fixtures against the [isolated original-layout adapter](P715_assets/LegacyMinimapLayout.cs.txt): **13 failed / 1 passed** ([recorded errors](P715_assets/regression-before.json)). The original-renderer before captures also show the footer crossing bottom labels and exceeding the panel.
- Final layout regressions: each of the four methods **3× individually**, **42/42 case executions passed** after the click-layout correction and build. [Final focused ledger](P715_assets/final-focused.json).
- Input compatibility checks: six existing hit-test methods and the action-map source guard each passed **3× individually**, **21/21 executions passed**. [Input ledger](P715_assets/input-focused.json).
- Verification process handling: the first tool shell returned exit 143 while its child verifier continued; the later terminal interrupt also left verifier descendants running. One completed sequential sweep was collected from each verifier, and both second loops were stopped at their next scratch-log open **before another test process can launch**. This keeps the total at exactly two runs per assembly. The overlapping runs and all observed failures are recorded in the [session observations](P715_assets/preliminary-verification.json); no assertion, deadline or test binary was changed for process control.
- Full suites: **exactly two complete unfiltered runs per assembly**, collected as one sequential sweep from each verifier. Both local P542 projectile proof and P660 15v15 showcase opt-ins are enabled. [Invocation ledger](P715_assets/verification.json).

| Sweep | Assembly | Passed | Failed | Skipped |
| --- | --- | ---: | ---: | ---: |
| 1 | Allegiance.Core.Tests | 4,724 | 1 | 0 |
| 1 | Allegiance.ClientCore.Tests | 1,117 | 0 | 0 |
| 1 | Allegiance.ClientCore.MonoGame.Tests | 2,256 | 0 | 3 |
| 1 | Allegiance.Lobby.Tests | 19 | 0 | 0 |
| 2 | Allegiance.Core.Tests | 4,723 | 2 | 0 |
| 2 | Allegiance.ClientCore.Tests | 1,117 | 0 | 0 |
| 2 | Allegiance.ClientCore.MonoGame.Tests | 2,256 | 0 | 3 |
| 2 | Allegiance.Lobby.Tests | 19 | 0 | 0 |

All **three Core failures passed 3/3 fresh exact-method isolated runs after both sweeps finished**; [nine-run retry ledger](P715_assets/diagnostic-isolation.json). These satisfy the brief's load-flake rule and match existing entries in [TEST_FLAKES.md](../TEST_FLAKES.md):

| Sweep | Method | Full-run symptom | Isolated result |
| --- | --- | --- | --- |
| 1 | `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` | `ThreadStateException: Thread has not been started` during shutdown. | 3/3 pass |
| 2 | `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Raw peer never delivered `OnPeerJoined` within the existing deadline. | 3/3 pass |
| 2 | `LiveRipcordTests.Re_target_activates_to_requester_only` | Empty requester receipt collection at `Assert.Single`. | 3/3 pass |

No assertions, deadlines, quarantine traits, skip conditions or transport/ripcord implementation were relaxed. The two sweeps contain **16,231 passes / 3 load-flake failures / 6 existing optional Meshy-pack skips**. Both ClientCore runs passed all **1,117** cases; both MonoGame runs passed **2,256** cases plus the same three unavailable-pack skips. The 14 minimap cases passed in both full MonoGame runs.

[Portable-path raw TRX archive](P715_assets/verification-trx.tar.gz), [archive manifest](P715_assets/trx-manifest.json), [first sweep](P715_assets/accepted-sweep-1.json), [second sweep](P715_assets/accepted-sweep-2.json). Seven of the eight accepted suite TRXs were preserved. The first ClientCore TRX was overwritten by the other verifier's shared output name; its counters and errors had already been parsed and remain in the first-sweep ledger. Initial development results are retained separately in [initial before-test errors](P715_assets/regression-before-initial.json). The final before-test record uses the corrected final fixtures and reproduces all 13 expected baseline failures.

The Core binaries date from before the first sweep and stayed unchanged through the UI build. The final Game and MonoGame test binaries were built before either complete MonoGame run; final source hashes and the preserved startup fingerprints are included in the verification ledgers. `git diff --check` passed.

Changes are left in the working tree. No commit or push was made.
