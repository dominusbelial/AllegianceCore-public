# Loadout V2 layout — P1013

Status: complete. Build/focused/visual checks pass; full Fast gate remains FAIL
on existing test families, qualified below. No commits or pushes.
Base: `1ac1076f`.

## Finding and root cause

The existing `src/Allegiance.ClientCore.Game/Rendering/ShopLayout.cs:9` describes
the legacy three-page text shop;
`src/Allegiance.ClientCore.Game/Rendering/DockedTouchZones.cs:54` uses independent
legacy touch bands.
Neither supplies shared three-column geometry or an inline expanding picker.
`src/Allegiance.ClientCore.Game/Game1.Rendering.cs:2085` reserves 84 physical
pixels for the docked footer. This is a missing V2 layout contract, rather than
a native simulation or purchase-rule defect.

## Native references

Re-read from the read-only native source for this story:

- `WinTrek/load.cpp:785-790`: the ship GeoPane has its own size and position.
- `WinTrek/load.cpp:797-820`: Default, Next, Previous, Launch, Buy, Confirm,
  Back and hull-list panes/event bindings.
- `WinTrek/load.cpp:112-124,2010-2043`: hull-specific slots, including turret
  mount translation at `:102-106`.
- `WinTrek/load.cpp:2138-2182`: native pricing and button enablement remain
  outside the layout; this story adds no buying or mounting rules.

## Fix

`LoadoutV2Layout` is a pure geometry snapshot using the existing device-free
`TouchRect`. It returns disjoint major regions, padded text bounds and visible
leaf elements carrying original model indices. Both HUD styles consume the
same geometry. The bottom 84 pixels remain reserved regardless of UI scale.

Hull and offer scroll offsets are row based. Opening a slot inserts a bounded
picker below it and moves subsequent slots down. The offers scroll inside that
picker, and outer slot scroll clamps to keep its owner and picker visible.
Closing the picker releases that clamp. No popup covers the stats or buttons.
Only complete rows are exposed for drawing/hit testing.

`DockedTouchZones` adds V2 zones and lookup beside unchanged legacy methods.
Text fitting delegates to a measurement overload of `TeamPaneRenderer.Fit`;
the existing font overload keeps its existing results. The layout wrapper also
handles budgets smaller than the ellipsis. There is no production renderer,
input branch or flag wiring in this story.

## Tests and screenshots

The 48 new cases pass in each of three isolated runs: **48/0/0 × 3**
(Passed/Failed/Skipped; **144/0/0** total). The development run also passed
48/0/0. Each property case uses a deterministic seed and sweeps user Size
0.5/0.75/1/1.5/2, display density 1/2, generated counts and scroll requests.
Tests cover all seven requested viewports and both HUD styles: disjoint major
regions and leaves, viewport/list/text containment, hit-test round trips,
inline row displacement, first/middle/last open slots, scroll endpoints,
invalid expansion, empty lists, half-open edges, scale caps and long names.
Real PCore001 tests derive counts and the final empty-slot offer from ShopModel.
With artwork deliberately absent, those cases skip cleanly: **0/0/2**.

Real-window captures succeeded using Mesa software GL and Xvfb. All **18/0/0**
captures were viewed: seven requested sizes × both styles at scale 1, plus
1280×720 and 3840×2160 at requested scale 2 with final hull/slot/offer scroll.
The open header stays visible and the picker stays above stats/buttons/footer.
The 720p scale caps to 1.06; the footer remains 84 physical pixels.
The captures show debug geometry and real row labels; ship/stat/price rendering
belongs to P1014. [Capture index](P1013_assets/README.md),
[Classic 720p](P1013_assets/classic-1280x720-scale1.png),
[Modern 720p](P1013_assets/modern-1280x720-scale1.png).

Raw logs/TRX remain in `.scratch/p1013/`; screenshots are public-safe debug
rectangles/text, with no private artwork or personal details.

## Gate

- Solution: `dotnet build AllegianceCore.slnx --disable-build-servers
  -nodeReuse:false -p:UseSharedCompilation=false`: **0 warnings / 0 errors**.
- Focused tests: **48/0/0 × 3**.
- `tools/build-tools.sh`: **all 139 projects build**, each with **0 warnings /
  0 errors**, including the new layout proof.
- Glossary links: **815 paths pass**. All registered parity matrices validate;
  P1013 parity CSV: **3 valid rows**;
  explicitly test-only, earning no production parity credit before P1014.
- One full Fast suite, `tools/run-tests.sh --tier fast --no-build`, with a
  120-minute bound: **FAIL**, **14,298/8/7** in **609.6 seconds**, plus **9/0/0**
  infrastructure checks. All 48 P1013 cases passed; their longest case was
  **3.454 s**, below the calibrated **11.505 s** limit. Twelve unrelated
  executions also exceeded that limit. The full suite was not repeated.

| Fast project | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Lobby | 144 | 0 | 0 |
| Update | 55 | 0 | 0 |
| ClientCore.MonoGame | 5,277 | 0 | 4 |
| ClientCore | 2,441 | 3 | 0 |
| Core | 6,381 | 5 | 3 |
| Total | 14,298 | 8 | 7 |

The eight failures are existing respawn, missile-lock, own-aim, armed-probe,
auto-seat, NPC-defend, warp-bomb floating-boundary and warp-status-receipt
families. P1013 changes no Core, Server, ClientCore or network fixture source.
All 16 distinct failed/over-time methods were run three times alone, including
all theory rows: **48 invocations, 166/8/0** across **174 cases**. The recurring
isolated failures were respawn (1/3), missile-lock (2/3), auto-seat (2/3), and
warp-bomb floating upper bounds (3 cases across the three method runs). All
other methods passed all three repeats, with timing-only methods below the
calibrated limit. None of these results clears the failed sweep. Exact counts,
signatures and durations are ledgered in
[TEST_FLAKES](../TEST_FLAKES.md#p1013-loadout-geometry-qualification--2026-11-14-utc).
Raw full-sweep results: `.scratch/p1013/fast/summary.json` and
`.scratch/p1013/fast/fast/summary.json`; isolated evidence:
`.scratch/p1013/retries/summary.json` and per-run TRX files.

The fresh worktree required restore. Packages came from the preinstalled cache;
NuGet audit stayed enabled, using the lane's cached vulnerability base/update
data dated 2026-11-12 through a temporary loopback-only source. No public hosts
were contacted. Restore config, writable HTTP cache and wrapper live only under
`.scratch/p1013/`. The wrapper supplies the required build-server workaround to
every tools build as well. No infrastructure setting is added to the repository.

Final hygiene checks pass: working-tree whitespace, new source secret/home-path
scan and all 18 screenshot links. Every screenshot was viewed. The brief was
moved into ignored task scratch. All started processes have ended, including
proof windows/Xvfb, test hosts and the temporary loopback audit server. No
commits, pushes or remote-machine operations were performed.

## Hand-offs

- P1014: pass resolved UI scale to `Compute`, use returned `Scale` when drawing
  text, fit through `FitText` with actual font metrics, draw only `Elements`,
  and use the returned preview rectangle for the ship camera. Wire the
  default-off `visuals.hangar.loadoutV2` branch; this story has no shipping call site.
- P1015: keep separate hull/slot/offer row scroll offsets and replace requested
  offsets with the returned clamped values. `HitTest`/`LoadoutV2HitTest` return
  model indices. Close the picker before scrolling away from its owner. Existing
  command/modal input gates still belong in Game1.
- Model rows must include the shared model's final empty-slot offer. Button
  labels, prices, refusals, purchase/launch rules and availability come from
  ShopModel and the other epic stories, never from this layout.
- The stat count defaults to eight and is configurable by the stats renderer.

## Native intent

Keep native loadout choices, preview, pricing and actions; change presentation
without introducing another source of purchase or mount rules.

## Modern approach

Share pure geometry, bounded inline lists, measured text and exact hit targets
between styles so later drawing and input use one layout contract.
