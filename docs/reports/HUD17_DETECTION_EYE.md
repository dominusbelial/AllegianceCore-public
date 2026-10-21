# HUD-17 — real detection state for the own-ship eye and the radar friendly detection eyes

Base: master at dispatch `e96b9943` (HUD-13 + HUD-15 present). Worktree
`pi/hud17`, model DeepSeek V4.1 Flash. No commits, no pushes: everything is in
the working tree.

Native citations are the checked-out tree at `~/Development/Allegiance/src/`;
artwork citations are `~/Development/Artwork/`. Every cite below was opened and
read before being used.

## 1. Native rule and the port's implementation

| Native rule | Cite | Port |
|---|---|---|
| `Detected` is a bit in the ship's **own side's** `ShipStatus` copy; true while at least one side that is neither the ship's own side nor allied has a known (spotted) status for it. | `FedSrv/fsship.cpp:263-265` (Spotted sets it), `:306-318` (Hidden recomputes from every remaining side's `!GetUnknown()`) | `Allegiance.Core.Sim.ShipDetection.IsDetectedByEnemy` — one indexed loop over `Mission.Sides`, skipping the own side and same-`AllyGroup` sides, returning true on the first `ship.SeenBySide(side.Id)`. `SeenBySide` is the port's "status not unknown" mapping, the same one P493's cross-side sweep uses. No LINQ, no lambdas, no enumerator boxing. |
| Spotted on an enemy side marks the own-side copy. | `fsship.cpp:237-265` | The same helper, recomputed every tick from the latched P132 `SeenBySide` flags. |
| Hidden recomputes from *all* remaining sides, so losing one eye does not clear the bit while another enemy side still sees the ship. | `fsship.cpp:287-318` | Full re-scan of all non-own, non-allied sides every tick, not a per-side toggle. |
| Leaving a cluster hides the ship from every other side; docking counts as leaving (`SetStation` sets the cluster to NULL). | `FedSrv/FedSrv.CPP:10711-10746`; `Igc/shipIGC.h:421-457` | `stationId is not null` returns false before any side scan. Native hides immediately; the port's `SeenBySide` latch can stay true for up to one 10-pass sweep — recorded as a divergence below. |
| A boarded child follows its carrier's own-side bit. | `fsship.cpp:268-284,321-334` | The helper walks `ParentShip` iteratively (depth-capped for malformed fixtures) and returns the carrier's value. |
| Allied sides do not count as "detected by enemy". | `fsship.cpp:263,306` (`AlliedSides`) | Allied ⇔ same non-null `Side.AllyGroup` (`Model/Side.cs:349-361`); a null group on both sides is not an alliance. |
| **Own-ship eye polarity:** `console.cpp:811` feeds `!ppi->GetDetected()` into a `SwitchPane` whose only visible branch is value 0; `PlayerInfo::GetDetected()` is itself the negation of the raw bit, and `dialog.mdl` says "since 'Detected' = 0 when eyed". Eye visible ⇔ **raw bit true**. | `WinTrek/console.cpp:811`; `clintlib/clintlib.h:438`; `Artwork/dialog.mdl:206-207,2526-2534` | `HudModel.cs`: `bool eye = visible && own.Detected;` (was the inverted `!own.Detected`). `HudFrame`/`HudModel` comments now cite the four native sites correctly. |
| **Radar friendly eye:** another ship of exactly the same side, player-piloted, reads the **raw** bit (`ss.GetDetected()`, no inversion). | `WinTrek/radarimage.cpp:1006-1019` | `RadarLabelModel.ShowDetectionEyeFor(ship, mySide, isSelf)` feeds `ClientWorld`'s `ShipEntity.Detected`; the pure gate is unchanged otherwise. |
| **Radar eye art:** `AWF_FLIGHT_EYE_ICON = "eyebmp"`, loaded once; left column width = max(command icon, eye); eye drawn below the command icon; tinted the full side colour; `BlendModeAdd`. `eyebmp.mdl` → `ImportImageFromFile("eye.png", true)`; `eye.png` is a 16×16 3-component JPEG. | `WinTrek/artwork.h:85`; `radarimage.cpp:226,1006-1019,1257-1266,1281-1282,1333-1352`; `Artwork/eyebmp.mdl` | `HudNativeTextures.RadarEyeIcon` loads `eyebmp`; `RadarLabelLayout.LeftColumn` implements the max-width/below-icon/first-slot geometry; `HudRenderer.DrawRadarLabelBlock` draws it full-colour. The JPEG is converted to a **luminance-alpha** mask at load: black texels become alpha 0 and add nothing under the port's single alpha-blended HUD batch — the visual result of native's `BlendModeAdd` without splitting the batch. |
| Native appends one label line for any left-side art and keeps the `*`/A-Q text only when the art is missing. | `radarimage.cpp:1281-1282,1305-1352` | `RadarLabelCache` caches four name-line variants (command prefix × eye mark); `SelectNameLine` picks the no-`*`/no-prefix line when the art draws. `*` remains only as the missing-art fallback. |
| Status changes are sent, not polled: `SendShipUpdates` does nothing on even frames and on odd frames sends only a side's **changed** `ShipStatus`. | `FedSrv/FedSrv.CPP:3663-3700` | `Step("shipDetection", SweepShipDetection)` runs after `Mission.Update` and before `broadcastShipStates`, recomputes the bit for every peer ship + same-side NPC, and fills a reusable `HashSet<short> _shipDetectedChanged`. The own, teammate and same-side-NPC status paths re-send when that set contains the subject, in both the serial and parallel builders. The parallel build only reads the set. |

Wire writes: `MakeOwnShipStatus` (both branches), `MakeNpcShipStatus`, and the
pooled `PackStatus` all use `ShipDetection.IsDetectedByEnemy`; the pooled path
writes `state | (detected ? 0x80 : 0)`. The join burst, periodic refresh, sector
change and ship-command refresh paths inherit the real bit. Cross-side
(enemy-copy) statuses are untouched and keep `Detected=false`.

## 2. Per-tick cost (400 peers, 2 sides, ~half seen)

From `Hud17DetectionAllocationTests` (replayed through
`TestProjectRecordedTick`, 120 warm-up ticks, 100 measured ticks; console output
of the run):

```
HUD17-COST: 400 peers; shipDetection=0 B/tick; broadcastShipStates edge=80 B/tick
base=80 B/tick; SHIP_STATUS edge=17036.8/tick base=1208.0/tick; extra=15828.8/tick
```

- The new `shipDetection` capture stage is **0 bytes/tick**.
- `step:broadcastShipStates` does not grow against the no-edge baseline
  (80 B/tick both).
- The status-packet numbers are a deliberately worst-case edge pattern: the
  test flips **every** ship's enemy visibility every 10 passes, so all 400
  change at once and each side's ~200 recipients receive their mates' statuses
  (native has the same O(side × ships) burst on a simultaneous edge). The
  steady state is the periodic refresh (1208/tick); the synthetic all-ships
  edge adds ~15.8k/tick on the 10 ticks where it fires, i.e. ~1.6k/tick
  amortised. Per real per-ship edge the resend is one status per same-side
  recipient (the live test measures 0–1 tick latency, not the old 100-tick
  teammate cadence).
- `IsDetectedByEnemy` itself is one `Mission.Sides` loop × one dictionary lookup
  per ship — about 800 lookups/tick at 400 ships and no allocation.

The fast-tier run and the whole-solution build are clean; see §5.

## 3. Screenshots (real client, 1920×1080, both skins)

Captured by `tools/hud17-proof`: a real `Allegiance.ClientCore.MonoGame` Game1
over TCP against a local `Allegiance.Server` (`m2scatter.igc`, 2 sides), with a
side-0 bot teammate and a passive side-1 bot ship as the only enemy scanner.
The enemy is moved into / out of the pilot's sector through the real
`MoveShipToSector`; the server's visibility and `shipDetection` sweeps then
produce the real status edge. Raw PNGs:
`~/allegiance-evidence/HUD17/raw/{classic,modern}/`; hashes and paths are in
`docs/reports/HUD17_assets/screenshots.json`. Each described below from the
opened image.

| Asset (in `docs/reports/HUD17_assets/`) | What is visible |
|---|---|
| `own-eyed-classic.webp` | Classic cockpit. The native `radonbmp` eye is drawn in the centre-HUD status row (yellow eye with red pupil, right of the SIG: 50% readout). Radar label `hud17_teammate 2353 m` has the native eyebmp icon to its left. |
| `own-not-eyed-classic.webp` | Same Classic frame/geometry: **no** `radonbmp` at the status row. Radar no longer shows the teammate label in this frame; the own-HUD eye is the asserted change. |
| `radar-teammate-eye-classic.webp` | Classic cockpit: `hud17_teammate` contact on the radar with the native eyebmp eye to the left of its label; the name line has no `*` (art replaced the fallback). |
| `radar-teammate-no-eye-classic.webp` | Classic cockpit: `hud17_teammate 421 m` at the bottom of the radar with **no** eye icon. |
| `own-eyed-modern.webp` | Modern skin: the Modern status presentation renders the same state as the text `EYE` near the top of the centre HUD; the radar's `2357 m` contact carries the native eyebmp eye. (Modern deliberately renders status icons as text; the Classic skin shows the native `radonbmp` art.) |
| `own-not-eyed-modern.webp` | Modern skin: no `EYE` text in the status row, and the radar label `hud17_teammate 420 m` is present with **no** eye icon. |
| `radar-teammate-eye-modern.webp` | Modern skin: the right-edge radar label (`2350 m`) carries the native eyebmp eye, and the own status shows `EYE`. |
| `radar-teammate-no-eye-modern.webp` | Modern skin, the immediate post-edge frame: no eyebmp anywhere on the radar and no `EYE` text. In this one frame the moving pilot's client pose has let the teammate label drift out of its block position; the `own-not-eyed-modern.webp` frame shot a moment later shows the same teammate label (`420 m`) still present with no eye. |

`Hud17DetectionEyeTests.Eyebmp_resolves_to_the_native_16x16_image` also pins the
artwork itself: `eyebmp` resolves to `eye.png`, 16×16.

## 4. Tests and how their failure was checked

Runtime-failure check: the production behavior was temporarily reverted
(`SweepShipDetection` step made a no-op, the builders hard-coded
`Detected=true`/`0x80`, and `HudModel` restored to `!own.Detected`), then:

- `Hud17DetectionLiveTests` failed at the first "before any enemy sees A:
  Detected=false" assertion (`Assert.All` 2/2 true).
- `Hud17PooledStatusTests` failed on the false case (`Assert.All` 2/2 true).
- `HudModelTests.Own_status_shows_the_detection_eye_on_the_raw_detected_bit`
  failed (`Assert.True` expected true).

The production files were restored from backups and the suite re-run.

New/updated tests:

- `tests/Allegiance.Core.Tests/Hud17DetectionLiveTests.cs` — two side-0 bot
  peers + a side-1 bot-ship scanner: before the spot both own and teammate
  statuses are false; the enemy moving onto A flips both within 20 ticks; moving
  it away clears both within 20; docking A through a real landing bay clears
  both; the docked status carries `StateDocked`.
- `Hud17ShipDetectionTests.cs` — native rule unit tests: enemy spot true,
  own-side/ally spot false (and breaking the alliance true), docked/sideless/
  unplaced false, boarded child follows the carrier, malformed/self/cyclic
  parent chains do not recurse forever.
- `Hud17PooledStatusTests.cs` — pooled `PackStatus` and non-pooled
  `MakeOwnShipStatus` bytes are identical in serial and parallel builds, with
  `Detected=true` for the enemy-seen ship and `Detected=false` for the unseen
  one.
- `Hud17DetectionAllocationTests.cs` (`Tier=Perf`) — 400 peers, 120 warm-up +
  100 measured ticks, 0 B/tick detection stage, broadcast allocation not above
  the no-edge baseline, and the extra edge packets recorded above.
- `Hud17DetectionEyeTests.cs` — left-column layout (eye below command icon /
  first slot without one), the four cached name lines (`*` absent once art
  draws), and the artwork-gated `eyebmp` 16×16 resolution.
- `HudModelTests` — own eye on the raw `Detected` bit (native polarity).
- `Hud5bReviewFixTests` — a teammate `ShipEntity` whose applied SHIP_STATUS has
  `Detected=true` produces an eye contact/label; `false` produces none; the
  production call site is the extracted `ShowDetectionEyeFor`.
- `ShipStatusServerTests` — the lone probe ship's status is `Detected=false`.
- `Hud15CommandIconTests` — its wiring guard now follows the shared
  `RadarLabelCache.SelectNameLine` call.

Honest note: the radar eye test can only fail on `master` by **not compiling**
(`ShowDetectionEyeFor` and the non-nullable `detected` parameter are new). The
runtime-failing proof for that row is `Hud17DetectionLiveTests` (the server
status edge) plus the real-client screenshots. Each new/affected test was run
3× alone; the live test passed 3/3, the unit/pooled/HUD set passed 3/3, and the
Perf test passed 3/3.

## 5. Verification

- Whole-solution build: 0 warnings, 0 errors.
- AOT publish: `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c
  Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot-publish`
  succeeded with no IL2026/IL3050.
- `tools/run-tests.sh --tier fast`: **PASS**, 9588 tests, 274.6 s (log
  `~/allegiance-evidence/HUD17/fast-tier.log`; the first run exposed an
  infinite `ParentShip` recursion in a hand-built fixture, fixed with the
  depth-capped iterative walk and re-run green).
- Showcase never-parks 5×: 5/5 passed (1 m 50 s–1 m 52 s each).

## 6. Remaining divergences (recorded in the rows)

1. **Slice-change hiding is latch-delayed.** A sector change does not clear the
   port's `SideVisibility` immediately; the latched `SeenBySide` may stay true
   for up to one 10-pass sweep, so `Detected` can lag native's immediate
   `SetCluster` hide (`FedSrv.CPP:10711-10746`). Out of scope per the brief.
2. **Send-on-change granularity.** The port re-sends a subject's own/teammate
   status on its detected-bit change in the same tick (plus the existing periodic
   refresh). Native sends every side's changed status on odd frames
   (`FedSrv.CPP:3663-3700`); the port does not have the per-side status copies,
   so the wire traffic shape differs on simultaneous multi-ship edges.
3. `SIM-0470` stays `partial`: the turret/cadence half is untouched; its notes
   now record the real own-side bit and the change-driven resend.

## Claude review fixups (at merge)
- **Eye blend:** eyebmp is now stored with alpha 0 (RGB kept), which under the premultiplied AlphaBlend batch is exactly
  `src·tint + dst`, i.e. native BlendModeAdd (radarimage.cpp:1316). The earlier luminance-alpha conversion darkened
  what was behind the eye and dropped the art's hue, so the "exactly BlendModeAdd" wording above was wrong.
- **Eye stack gap:** the eye now sits directly under the command icon with no gap (radarimage.cpp:1322-1326, 1342-1344).
- **`ShipStatus.Detected` default** is now `false`; every own-side builder sets it explicitly.
- **NPC detection entries** are cleared with the old world on mission rotation (ship ids only grow).
- **Screenshot wording:** the teammate radar shots show the eye and "2353 m"; the name line is not drawn at that range
  (LOD name gate), contrary to the text above.
- **Traffic numbers:** the measured all-ships-flip figure (~158k messages per edge) is about 2× the 400 + 400×199 ≈ 80k
  expected for same-side delivery; either the publication capture double-counts or the recipient grouping is wider.
  Treat the numbers above as an upper bound until batch 16 measures them live.
