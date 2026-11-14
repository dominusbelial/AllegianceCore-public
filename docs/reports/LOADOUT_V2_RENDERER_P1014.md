# P1014 — Loadout V2 renderer

The real client now draws `hulls | ship | slots` behind
`visuals.hangar.loadoutV2`, default **off**, in Classic and Modern. Existing
shop keyboard navigation drives the same ShopModel. Buy/mount/carry-over and
input rules are unchanged. The report was drafted before the gate. Final task checks pass; the single
full Fast gate remains **FAIL**, as detailed below. Base: `5c224c40`. No commits or pushes.

## Findings, root causes and fixes

| Finding / root cause | Fix |
| --- | --- |
| `Game1.Rendering.cs:2093` unconditionally drew the old shop panes; the merged V2 model/stats/layout had no production consumer. | Insert the default-off V2 branch at `:2093`; retain the original draw body at `:2097`. `Rendering/VisualFlags.cs:115,181` registers the flag in the existing profile/launch/options table. |
| The renderer needs testable decisions about text, focus, estimates, colours and affordability. | `Rendering/LoadoutV2Presentation.cs:48` builds a graphics-free snapshot from LoadoutV2Model, ShipStatSheet and StatDelta. It reads all prices, refunds and shortfall directly from ShopModel. The empty-slot offer matches the existing cursor's index zero. |
| A current/restored hull can be absent from the station's offers. The loopback server starts in an advanced scout while the fresh station offers the stock scout. `LoadoutV2Model` previously used only `ShopModel.EffectiveHull`, which is null in that state. | `Rendering/LoadoutV2Model.cs:418` falls back to the supplied hull universe for display and slot enumeration. It does **not** add a buyable hull or change ShopModel's mount/offer predicates. A real-core regression test proves that separation. |
| The existing preview fills the viewport. | `Game1.LoadoutV2.cs` temporarily sets the viewport to `LoadoutV2Layout.ShipPreview`, calls the unchanged P519 preview, then restores the viewport in `finally`. Native assets, orbit, framing and fallbacks are reused. |
| Long numerical values plus units could consume the stat delta's width. | `Rendering/LoadoutV2Renderer.cs:112` fits the complete value/delta line as a group. Other long rows use the existing Fit helper and ellipsis. |

Port references above are under `src/Allegiance.ClientCore.Game/`.
`LoadoutV2Renderer` takes the presentation frame, layout, existing HUD fonts and
white texture, style and a layout hit-test result. It performs no input or shop
mutation. Classic uses the existing gold/grey loadout palette; Modern uses the
existing cyan/muted palette. Green/red are the existing fit/no-fit colours.

The screen shows core hull names, armour classes, prices, current/selected
markers and dimmed lock reasons; effective-hull slots and parts; turret occupants
mapped back to the current ship's relative seats; inline offers with prices and
coloured per-part changes; native hull stat rows first; explicitly marked `~`
extras; loadout/refund/net/shortfall; Default, Revert, Buy-or-Need and Launch;
title and money. The stat bar includes shield strength and estimated aggregate
weapon DPS when present. The compact extras are energy recharge, fuel and ammo.

## Native references and parity

Re-read/re-grepped in the mounted native source before citation:

- `WinTrek/load.cpp:112-122`: valid slots per hull. P1010 supplies these rows.
- `WinTrek/load.cpp:176-205`, especially `:183-185`: turret gunner or `<open>`,
  translating chosen-hull turret mounts to the current hull's fixed-mount count.
- `WinTrek/load.cpp:785-790,877-887`: independent geo pane, Y rotation and X tilt.
- `WinTrek/load.cpp:797-820`: loadout button panes and their native actions.
- `WinTrek/load.cpp:986-1054`: hull performance/current-base values; P1011
  supplies native labels and formulas. `:1065-1094` supplies weapon stats.
- `WinTrek/load.cpp:1497-1531`: launch affordability and buy shortfall.
- `WinTrek/load.cpp:1568-1636,1714-1743`: station-filtered hull/part offers,
  already implemented by the shared models.

Four renderer rows are tracked in `docs/parity/p1014-loadout-renderer.csv`.
`P1011-001` in `p1011-loadout-stats.csv` now records the production consumer and
proof. Signed deltas, inline presentation, estimates and colour choices are
presentation additions. No new server/wire/simulation rule is introduced.

## Real-window proof and media review

`tools/p1014-ui-proof/` runs production Game1 with PCore001, native artwork,
software GL, X11/Xvfb and an in-process loopback server. It checks native F4,
visible locked hulls, chosen-hull display, existing Tab/Space opening a weapon
picker, Down moving offer focus, visible inline offers and the shared shortfall.
Native Enter opens chat; the proof uses the existing Space select binding.
The pointer is parked outside the legacy hover bands during keyboard checks.

The base station's parts and two hulls are free. The unaffordable scene uses a
later reachable-tech fixture, the same real catalogue/station filter and a zero
wallet, producing a real **$4,000** Devastator fit. No price or image is invented.
The proof holds only the preview's angle fixed. It waits for the new hull's first
draw before capture, cancels/joins its ticker on exit and has a watchdog.

Coverage: Classic and Modern × 1920×1080 and 1280×720 × default (locked hulls),
weapon offers and unaffordable turret hull; plus flag-off in all four combinations.
The eight final client scenarios pass **56/0/0 assertions**. All **16 final
captures** and **four baseline captures** were viewed. Twelve original PNGs
(**3,621,290 bytes**, 3.45 MiB) are retained in `P1014_assets/`: two 1080p default
views with locked hulls, four offer views, four unaffordable views, and the
Classic 1080p flag-off before/after pair. The 720p defaults and other off pairs
remain in scratch, with their dimensions, CRC checks, hashes and review status
in [qualification.json](P1014_assets/qualification.json).

Examples: [Classic default](P1014_assets/classic-1920x1080-default.png),
[Modern offers](P1014_assets/modern-1280x720-offers.png),
[Classic shortfall](P1014_assets/classic-1280x720-need.png).
Media contains only native artwork and generic local fixtures; no Meshy models,
private identity, secrets or personal content. No screenshot was composed or
replaced with a synthetic UI.

Flag-off baseline binaries were built from the unchanged game at `5c224c40`,
before adding the renderer. The same local fixture, profile isolation, viewport
and held orbit angle were used before/after. The four final
comparisons have **zero differing pixel bytes**, and identical PNG hashes.
`tools/p1014-ui-proof/compare.py` independently checks PNG CRCs, dimensions,
decompression and pixel hashes using only Python's standard library.

A preliminary image preview appeared to omit repeated glyphs/regions. Opening
the original-resolution PNG individually showed complete text. The temporary
Immediate SpriteBatch experiment was reverted. No graphics workaround or claim
about P1019's separate captures is included. Ellipses in long lock/offer text
are deliberate fitting, not missing glyphs.

## Tests and gate

Counts are Passed / Failed / Skipped. The full sweep was run once and was not
repeated after correcting its three task-owned stale expectations.

| Check | Result |
| --- | --- |
| Final solution build | **0 warnings / 0 errors**, 8.12 s |
| All tools build | **140/140**, 0 warnings / 0 errors |
| New/changed tests alone ×3 | **17/0/0** each, **51/0/0** total |
| Related model/layout/stats/flag/options tests | **106/0/0** |
| New tests without artwork | **3/0/10**, clean skips before loading any core |
| One full Fast suite | **14,341/8/7**, 14,356 cases, **665.0 s: FAIL** |
| Fast infrastructure | **9/0/0: PASS** |
| Non-task failed/slow methods alone ×3 | **90/3/0**, 11 methods across 33 processes |
| Real-window scenarios | **56/0/0**, eight client processes; 16 final PNGs reviewed |
| Flag-off before/after | **4/4 pixel-identical**, both styles and both sizes |
| PNG integrity | **16/16** final CRC/decompression checks pass; 12 retained |
| Glossary, parity and diff checks | **PASS**, 833 glossary paths; 19 parity rows validated |

The eight sweep failures comprise **three stale Visuals-page assertions**
(the new flag increases the count from 13 to 14) and **five non-task failures**.
Both options-page methods now assert 14 rows and the default-off Loadout V2
label, and pass in the final three-run selection. All thirteen new P1014 cases
also passed in the full sweep; none exceeded its **13.827 s** calibrated limit.

The five non-task failures were missile-lock partial-tip delivery, remote death
latching, seeded peer seating, miner docking and Classic training targeting.
Together with eight over-time case executions, they cover eleven methods. Every
method was repeated three times alone, including all theory rows: **90/3/0**.
Only missile-lock tip delivery failed in all three repeats (lock reached 1.000
but no partial tip was observed, matching the existing P1019 signature). All
other isolated cases passed, with a maximum of **7.431 s**, below the limit.
No test ranges, tiers, timing limits or engine behavior were changed.

The full-sweep failure remains recorded; the isolated results do not clear it.
Exact method/case results and signatures are in
[reproductions.json](P1014_assets/reproductions.json), with the qualification
entry in [TEST_FLAKES.md](../TEST_FLAKES.md#p1014-loadout-renderer-qualification--2026-11-14-utc).
The sweep command was:

```sh
timeout --signal=TERM --kill-after=30s 7200s tools/run-tests.sh \
  --tier fast --no-build --results-dir .scratch/p1014/fast
```

The new pure tests use real PCore001 data after `Require.Artwork()` and skip
cleanly without the corpus. They cover row text/ink, hull locks/current markers,
gunner seats and relative remapping, unavailable-current-hull display, inline
keyboard focus, native-first labels/estimates, price/shortfall, signed deltas,
both styles/sizes, and default/off/on/profile/launch routing. Missing-hull lookup in the wiring
also uses an explicit nullable result, so an unresolved id cannot become a
default-initialized hull stat sheet. The existing flag
startup-log and shared Visuals-page row-count expectations were updated for the
new table row. The final selection contains thirteen new renderer cases, the
changed startup-log case, and three options-page theory/fact cases.

Builds use `--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`. NuGet audit remains enabled, using the supplied
cached official vulnerability data dated 2026-11-12 through a temporary loopback
feed and a writable HTTP cache. Final verification uses only the local package cache and loopback audit feed; no other machine or deployment is used.
Docker is absent. No service, store, port or production environment variable was
added, so no Compose change is required.

All proof clients, loopback tickers, Xvfb instances, build/test processes and
temporary audit feeds have ended. The brief is only in ignored scratch. All
changes, the report and reviewed media remain in the working tree; no commits
or pushes were made.

## Hand-offs

- **P1012:** ShopModel and its carry-over logic are untouched. A current hull
  outside the station catalogue can now be displayed by V2, but the existing
  ShopModel still supplies no compatible-slot picker until an offered hull is
  selected. This story does not alter that eligibility or restore policy.
- **P1015:** wire V2 clicks, scrolling, action buttons and rebindable hints to
  the visible `LoadoutV2Layout` indices. The renderer already accepts its hit-test
  result for hover. Until then, legacy hover/touch bands remain the existing
  input path; Q/E, Tab, arrows, Space and B still operate ShopModel. No new input
  handling was added here.
- **P1017:** Buy/Launch refusal messages and live purchase flow remain shared
  paths; this story draws the current BlockReason and shortfall only.

Native intent: the loadout screen shows the chosen hull, mount-specific parts,
performance and the real purchase/refund calculation.

Modern approach: present that shared state in three columns with inline offers,
signed stat deltas and explicit affordability, behind a default-off visual flag.
