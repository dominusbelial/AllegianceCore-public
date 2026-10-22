# P782 (PAR-1) — Parity re-audit: HUD and screens rows under "behaviour parity, visuals may differ"

Date: 2026-10-22 · Branch: `hermes/p782` · Scope: `scripts/parity-scorecard.py`, `docs/parity/parity-matrix.csv`, `P674ParityScorecardTests.cs`, report.

## Summary

Introduced a new `done-visual` status to the parity scorecard: a row whose behaviour is fully native but whose only remaining differences are **visual** (font face/size, pixel-art appearance, position/geometry, colour shade, blend look) counts as full credit and is reported separately from plain `done`. The validator accepts `done-visual` only when the row's reason starts with the literal `VISUAL:`. A `visual-weight` column was added to the per-area and TOTAL scorecard lines.

Re-audited all 78 non-done, non-n/a rows in `hud`/`screens` plus the two HUD/chat-related `commander` (609) and `lobby` (649) rows. **15 rows** were reclassified `partial → done-visual` (behaviour verified complete in code, each pinned by an existing test); the remaining 63 stay `partial`/`missing` with reasons rewritten to lead with `BEHAVIOUR GAP: <exact missing behaviour with native cite>`. No weights, areas, or native cites changed; no row was promoted `partial → done` (pixel parity is not claimed).

Index moved **87.17 % → 89.64 %** (weighted, hud 43.12 % → 56.88 %), with **visual-weight 45** (= 15 flipped rows × 3) attributed to `done-visual`.

## 1. Scorecard before / after

### `docs/parity/parity-matrix.csv`

Before (baseline `master`):
```
area | weighted (credit/weight) | unweighted (complete/eligible)
commander | 92.80% (75.164/81.000) | 93.48% (43/46)
commands  | 97.80% (578/591)       | 96.32% (183/190)
fedmsg    | 98.42% (499/507)       | 98.37% (121/123)
hud       | 43.12% (141/327)       | 43.12% (47/109)
lobby     | 88.40% (41.548/47.000) | 81.82% (27/33)
parts     | 100.00% (161/161)      | 100.00% (42/42)
screens   | 85.81% (97.829/114.000)| 84.62% (77/91)
TOTAL     | 87.17% (1593.541/1828.000) | 85.17% (540/634)
```

After (`hermes/p782`):
```
area | weighted (credit/weight) | unweighted (complete/eligible) | visual-weight
commander | 92.80% (75.164/81.000) | 93.48% (43/46) | visual 0
commands  | 97.80% (578/591)       | 96.32% (183/190) | visual 0
fedmsg    | 98.42% (499/507)       | 98.37% (121/123) | visual 0
hud       | 56.88% (186/327)       | 56.88% (62/109) | visual 45
lobby     | 88.40% (41.548/47.000) | 81.82% (27/33) | visual 0
parts     | 100.00% (161/161)      | 100.00% (42/42) | visual 0
screens   | 85.81% (97.829/114.000)| 84.62% (77/91) | visual 0
TOTAL     | 89.64% (1638.541/1828.000) | 87.54% (555/634) | visual 45
```

### `docs/parity/sim-rules-matrix.csv`

Unchanged (validates under the same `done-visual`-aware validator; no `done-visual` rows present):
```
TOTAL | 94.39% (1212/1284) | 93.32% (391/419) | visual 0
```

## 2. Scorecard change (`scripts/parity-scorecard.py`)

- Added `done-visual` to `STATUSES` and to `COMPLETE` (so it earns **full** credit, weighted and unweighted).
- Validator: a `done-visual` row whose `port_cite_or_reason` does not **start** with `VISUAL:` is rejected (`invalid ... must start its reason with 'VISUAL:'`).
- `score()` now also returns `visual_weight` (the credited weight from `done-visual` rows); the per-area and TOTAL lines gained a `visual <weight>` column.

## 3. Tests

Added to `tests/Allegiance.Core.Tests/P674ParityScorecardTests.cs`:
- `DoneVisual_rows_get_full_credit_and_appear_in_the_visual_column` — a `done-visual` row earns full credit and appears in the `visual <weight>` column (asserts `area | 50.00% (5/10) | 66.67% (2/3) | visual 3` and the TOTAL line).
- `DoneVisual_without_VISUAL_prefix_is_rejected` — a `done-visual` row without the `VISUAL:` prefix fails validation.

Existing P674 assertions kept passing. `P674ParityScorecardTests` ran **3× alone** (10/10 each run). Every test fixture cited by a flipped row (Hud14ClassicGaugeTests, Hud10PanelsTests, HudInventoryPaneTests, RadarLabelModelTests, HudModelTests, Hud10ServerClockLiveTests) was run and passes (146 + 1 green). No production code changed in `src/` or `tools/`.

## 4. Re-audited rows (78)

Legend: old status → new status. `= partial` / `= missing` rows stay as-is with rewritten reason. `→ done-visual` = behaviour verified complete in code, only visual differences remain, pinned by the cited test.

| # | area | item | old → new | behaviour gaps left | test cite |
|---|------|------|-----------|--------------------|-----------|
| 380 | hud | Hull integrity | partial → done-visual | none (visual only) | Hud14ClassicGaugeTests |
| 381 | hud | Shield charge | partial → done-visual | none (visual only) | Hud14ClassicGaugeTests |
| 382 | hud | Energy reserve | partial → done-visual | none (visual only) | Hud14ClassicGaugeTests |
| 383 | hud | Fuel reserve | partial → done-visual | none (visual only) | Hud14ClassicGaugeTests |
| 384 | hud | Ammunition reserve | partial → done-visual | none (visual only) | Hud14ClassicGaugeTests |
| 385 | hud | Thrust setting | partial → done-visual | none (visual only) | Hud14ClassicGaugeTests |
| 386 | hud | Speed | partial → done-visual | none (visual only) | HudModelTests (SpeedUnit 'mps', Speed placement) |
| 388 | hud | Roll tutorial cue | partial = partial | native tutorial annotation absent (indicator.cpp:634) | — |
| 389 | hud | Signature percentage | partial = partial | no continuous GetCloaking ramp on the wire → cloak uses engaged-0 end state (shipIGC.h:268-270) | — |
| 392 | hud | Cloak strength and drained state | partial = partial | drained-state is GlassHUD-only art, not drawn; centre readout is a port extension | — |
| 397 | hud | Target name | partial = partial | unseen-target last-seen hull type absent (consoledata.cpp:65) | — |
| 398 | hud | Target type description | partial = partial | native last-seen hull fallback absent (consoledata.cpp:40) | — |
| 400 | hud | Target hull | partial = partial | unseen-state still approximated (no per-target CanSee gate, consoledata.cpp:202-212) | — |
| 401 | hud | Target shield | partial = partial | CanSee still approximated (consoledata.cpp:252-276) | — |
| 404 | hud | Target ore | partial = partial | port shows fraction of remaining ore; native radar fill units differ (consoledata.cpp:449) | — |
| 406 | hud | Target in weapon range | partial = partial | turret gunner weapon unsolved → boarded false (indicator.cpp:196-204); probe/treasure/cluster no pose | — |
| 407 | hud | Target model preview | partial = partial | aleph/probe/mine/treasure-model previews absent (console.cpp:672) | — |
| 410 | hud | Velocity direction marker | partial = partial | velocity is own/sim ship, not native GetSourceShip for boarded gunner (indicator.cpp:130-148) | — |
| 412 | hud | Ballistic lead marker | partial = partial | server-solved lead stands in for client solveForLead (indicator.cpp:179) | — |
| 413 | hud | Aim hit-probability predictor | partial = partial | chase/missile reuse server lead, not native forward-projected aim (indicator.cpp:334) | — |
| 414 | hud | Chase weapon aim without target | partial = partial | no marker when loadout/weapon unresolved (indicator.cpp:367,399-400) | — |
| 415 | hud | Accepted and current order turn arrows | partial = partial | CanSee approximated by object presence (indicator.cpp:411,783) | — |
| 416 | hud | Selected target bracket | partial = partial | port draws an extra 3D selection box native lacks; native targeted blip drawn (HUD-13) | — |
| 418 | hud | Training HUD callouts | partial = partial | training annotations absent (indicator.cpp:461) | — |
| 419 | hud | Radar viewport and round border | partial = partial | eye-space edge clamp approximated by NDC clamp (WinTrek.cpp:7882-7944) | — |
| 420 | hud | Radar ship contacts | partial = partial | per-contact label tiers absent (PAR-2); icon+side colour done (HUD-16) | — |
| 421 | hud | Radar station contacts | partial = partial | label tiers absent (PAR-2) | — |
| 422 | hud | Radar warp contacts | partial = partial | native labels + Target range clip absent (PAR-2) | — |
| 423 | hud | Radar asteroid contacts | partial = partial | ore fills absent (PAR-2) | — |
| 424 | hud | Radar missile contacts | partial = partial | aim tiers absent (PAR-2) | — |
| 425 | hud | Radar probe and mine contacts | partial = partial | per-contact stats absent (PAR-2) | — |
| 426 | hud | Radar treasure and tech contacts | partial = partial | live dev-tech split deferred (PAR-2) | — |
| 429 | hud | Radar nearest enemy bracket | partial = partial | enemybmp.mdl all-black → native draws none; diamond is a port extension | — |
| 431 | hud | Radar command subjects | partial = partial | composer has no individual/group recipient selection (radarimage.cpp:340-342) | — |
| 435 | hud | Radar construction highlight | partial = partial | native c_maskHighlight is flag-treasure highlight, not construction (radarimage.cpp:639,701) | — |
| 436 | hud | Radar name labels | partial → done-visual | none (visual only) | RadarLabelModelTests (LOD label tiers) |
| 437 | hud | Radar range labels | partial → done-visual | none (visual only) | RadarLabelModelTests (range value, per-tier, command-cam suppress) |
| 438 | hud | Radar hull and shield bars | partial → done-visual | none (visual only) | RadarLabelModelTests.Stats_sentinel_above_one_suppresses_the_bar |
| 439 | hud | Radar ore and miner fill bars | partial = partial | miner fill lacks pilot-type gate; asteroid uses remaining-ore (radarimage.cpp:989-997) | — |
| 444 | hud | Radar LOD modes | partial = partial | label/stat tiers remain absent (separate rows) | — |
| 448 | hud | Observer and turret board state | partial = partial | BoardTextImage/InTurretPaneImage absent; BoardState not drawn in flight (dialog.mdl:5431-5433) | — |
| 451 | hud | Chat history | partial = partial | leader-bold not wired (console.cpp:3508); OLDER marker is a port extension | — |
| 452 | hud | Chat composition and recipient | partial = partial | mouse-command/command/shell compose input paths absent (console.cpp:705) | — |
| 453 | hud | Command recipient verb and wing controls | partial = partial | per-recipient command fan-out not ported (console.cpp:746) | — |
| 454 | hud | Tip text lines | partial = partial | side colour falls back to teal accent when SideRoster unknown (console.cpp:723,913) | — |
| 455 | hud | Critical tip text lines | partial = partial | 30s training lifetime not surfaced (console.cpp:726-730,913-931) | — |
| 457 | hud | Money credits | partial = partial | always-visible flight credit counter gone (console.cpp:792) | — |
| 458 | hud | Lives remaining | partial = partial | no lives-limit elimination simulation → badge never appears (console.cpp:812-817) | — |
| 460 | hud | Mission countdown | partial → done-visual | none (visual only) | Hud10PanelsTests.Mission_clock... + Hud10ServerClockLiveTests |
| 462 | hud | Eject pod identity panel | partial → done-visual | none (visual only) | Hud10PanelsTests.Lifepod_panel_and_gauge... |
| 463 | hud | Lifepod oxygen endurance | partial → done-visual | none (visual only) | Hud10PanelsTests.Lifepod_panel_and_gauge... |
| 464 | hud | Death banner | partial = partial | gate uses port Dead flag; native IsGhost (deaths>lives) not on wire (WinTrek.cpp:3653) | — |
| 465 | hud | Boundary and crowd warnings | partial = partial | textual badges are a port extension (WinTrek.cpp:8654) | — |
| 468 | hud | HUD style and software layouts | partial = partial | native skin selector / visibility switches unported (WinTrek.cpp:5104) | — |
| 469 | hud | Console pane visibility and slide animation | partial = partial | full overlay/slide coverage unported (console.cpp:3268-3290,3400-3415) | — |
| 472 | hud | Flight equipment inventory pane | partial → done-visual | none (visual only) | HudInventoryPaneTests |
| 473 | hud | Equipment part identity and key | partial = partial | gunner assigned-weapon semantics absent (no per-turret occupant wire) | — |
| 474 | hud | Equipment selected and active state | partial = partial | turret gunner active semantics absent (consoledata.cpp:1080,1122) | — |
| 477 | hud | Launcher arming progress | partial = partial | GetArmedFraction not on client wire → arming bar absent (consoledata.cpp:1157,1189) | — |
| 486 | hud | Inventory turret gunner identity | partial = partial | per-turret occupant rows absent (no occupant names on wire) | — |
| 487 | hud | Inventory cargo slots | partial → done-visual | none (visual only) | HudInventoryPaneTests (cargo collapse + row order) |
| 490 | hud | Command view ship summary | partial = partial | target speed/distance missing; softgauge scaling approximated; money/AP/RADAR line invented | — |
| 338 | screens | Chat pane / History scrolling and channels | partial = partial | Alt+C filter cycle is a port adaptation; native filters live in lobby (teamscreen.cpp:3085-3101) | — |
| 354 | screens | Scoreboard / career leaderboard query | missing = missing | no persistent career leaderboard query (leaderboardscreen.cpp:435-503) | — |
| 355 | screens | Scoreboard / find-me and page nav | missing = missing | no career rank pagination / FindMe (leaderboardscreen.cpp:512-519,554-630) | — |
| 356 | screens | Scoreboard / column sort and profile nav | missing = missing | native career controls absent (leaderboardscreen.cpp:521-539,633-670) | — |
| 362 | screens | Intro screen / Options menu | partial = partial | Sound/Game one-press toggles, not native submenus (WinTrek.cpp:4429-4442,4568-4586) | — |
| 370 | screens | Training screen / Back to intro | partial = partial | exact native return-to-intro destination not verified (trainingscreen.cpp:160-164) | — |
| 371 | screens | Training screen / Basic Flight launch/runtime | partial = partial | full native lesson completion through real client not proven (mission2.cpp:50-70) | — |
| 372 | screens | Training screen / Pre/post-training slideshow | missing = missing | native TrainSlideshow/PostTrainSlideshow absent (trainingscreen.cpp:166-204) | — |
| 373 | screens | Training screen / Introduction mission | missing = missing | Introduction mission not playable (trainingscreen.cpp:182-185) | — |
| 498 | screens | Training screen | partial = partial | standalone UI + in-process host binding remain (trainingscreen.cpp:175-230) | — |
| 500 | screens | Character-info / career stats and civ | partial = partial | needs Zone Club career DB the port lacks (CharInfoScreen.cpp:242-260,717-774) | — |
| 501 | screens | Character-info / profile editing & apply | partial = partial | applied text persists locally only; no club-server channel (CharInfoScreen.cpp:837-880) | — |
| 502 | screens | Character-info / leaderboard & games nav | partial = partial | native profile-to-leaderboard flow absent (CharInfoScreen.cpp:804-826) | — |
| 537 | screens | Server list / sort and game filters | partial = partial | 11/12 filter flags + sort cols 0/3/7 are placeholders (directory DTO lacks game metadata) | — |
| 609 | commander | Team chat / Compose recipient and team rendering | partial = partial | full compose-focus + native channel filtering not covered (console.cpp:1282-1338) | — |
| 649 | lobby | Lobby chat / Lobby-directory relay feedback | partial = partial | full native social/directory chat channel broader than port (appmsg.cpp:2640-2665) | — |

## 5. Flips to `done-visual` (15) — the behaviour each keeps, and what differs visually

All 15 were verified in code: the described behaviour is implemented and pinned by an existing test; the remaining delta is visual only (font face/size, art, position/geometry, colour shade). Reasons in the matrix begin with `VISUAL:`.

- **380–385 Classic gauges** (Hull/Shield/Energy/Fuel/Ammo/Thrust): HUD14/14b layer model, exact source rects + justification, DefaultCenterHudOn console offsets, reveal, flash, additive blend, single-pane scale — pinned by `Hud14ClassicGaugeTests`. Visual: label tint = port accent vs live side colour (WinTrek.cpp:8681), DejaVu Sans ↔ Verdana (smVerdana ~8px); % numeral is an off-by-default port extension.
- **386 Speed**: mps unit + same-frame predicted own velocity in prediction mode (consoledata.cpp:136; dialog.mdl:2546); numeral lgBoldVerdana + unit smVerdana on a second line vs native one-string `NumberString(speed)+' mps'` → position/typography differ. Pinned by `HudModelTests` (SpeedUnit 'mps', placement).
- **436 Radar name labels**: name lines drawn at contacts with per-kind LOD tiers (radarimage.cpp:704-980); placement approximate + font. Pinned by `RadarLabelModelTests`.
- **437 Radar range labels**: `int(separation+0.5)` metres with native suppress rules (radarimage.cpp:1209;626-636); placement approximate + font. Pinned by `RadarLabelModelTests`.
- **438 Radar hull/shield bars**: fractions from ship.Latest/StationLive with >1 sentinel suppression (radarimage.cpp:1408-1441); native 16px bar art + font. Pinned by `RadarLabelModelTests.Stats_sentinel_above_one_suppresses_the_bar`.
- **460 Mission countdown**: native countdown-vs-elapsed rule from GameLengthSeconds (WinTrek.cpp:8128-8143); native game-state screen art not ported → shown in the port text GAME STATE pane. Pinned by `Hud10PanelsTests.Mission_clock_...` + `Hud10ServerClockLiveTests`.
- **462 Eject pod identity panel**: own-lifepod gate + unscaled conejectpoddisplaybmp at SideCenter(-86,-87) (consoledata.cpp:578-590); GlassHUD GHEjectPodDisplayOn variant not drawn (classic art in both skins). Pinned by `Hud10PanelsTests.Lifepod_panel_and_gauge_...`.
- **463 Lifepod oxygen endurance**: own-ship 52×4 softgaugebmp integer fill at (66,10) (consoledata.cpp:534-545); GlassHUD bar at (60,52) not drawn separately. Pinned by `Hud10PanelsTests.Lifepod_panel_and_gauge_...`.
- **472 Flight equipment inventory pane**: model-driven at SideBottomRight(-36,40) with native art/icons (inventoryimage.cpp:299; dialog.mdl:5388); native tab image + selected-row background differ, font. Pinned by `HudInventoryPaneTests`.
- **487 Inventory cargo slots**: selected-cargo-first ordering, S key, duplicate collapse, lifepod exclusion (inventoryimage.cpp:169,341); no cargo icon bitmap, font. Pinned by `HudInventoryPaneTests`.

## 6. Grouped remaining behaviour gaps (feed PAR-2 … PAR-7)

These rows keep `partial`/`missing`; each reason now leads with `BEHAVIOUR GAP:` and cites the native file.

- **PAR-2 — radar label tiers / per-contact stats**: Radar ship(420), station(421), warp(422), asteroid(423), missile(424), probe/mine(425), treasure/tech(426) contacts — per-contact label/stat/aim tiers & dev-tech split absent; Radar ore/miner fill bars(439) (pilot-type gate, oreSeen not on wire); Radar LOD modes(444) (label/stat tiers absent).
- **PAR-3 — chat / command compose**: Chat composition & recipient(452) (compose input paths), Command recipient verb & wing controls(453) (per-recipient fan-out), Chat history(451) (leader-bold), Chat pane history/channels(338) (Alt+C filter adaptation), Team chat(609) (compose-focus + channel filtering), Lobby chat relay(649).
- **PAR-4 — training**: Training HUD callouts(418) + Roll tutorial cue(388) (annotations absent), Critical tip training lifetime(455); screens Training back-to-intro(370), Basic Flight runtime(371), slideshow(372), Introduction mission(373), Training screen(498).
- **PAR-5 — HUD wire data**: Launcher arming progress(477) (GetArmedFraction not on wire), Inventory turret gunner identity(486) + Equipment part identity(473)/selected-active(474) (no per-turret occupant wire), Target in weapon range(406) (turret gunner weapon), Velocity direction marker(410), Ballistic lead(412), Aim predictor(413), Chase aim(414), Order turn arrows(415) (server-solved lead / GetSourceShip / CanSee), Observer & turret board state(448), Command view ship summary(490).
- **Target pane unseen / CanSee**: Target name(397), Target type description(398), Target hull(400), Target shield(401) — last-seen hull fallback + per-target scanner CanSee gate absent; Target ore(404) (units).
- **Own status / signature / cloak / counters**: Signature percentage(389) (no cloak ramp on wire), Cloak strength & drained state(392), Money credits(457), Lives remaining(458), Death banner(464), Boundary/crowd warnings(465).
- **Radar edges**: Selected target bracket(416), Radar viewport/border(419) (NDC clamp), nearest-enemy bracket(429), command subjects(431), construction highlight(435).
- **HUD layout / style / equipment**: HUD style & software layouts(468), Console pane visibility/slide(469), Tip text lines(454).
- **Screens**: Intro Options menu(362), Scoreboard career query(354)/find-me(355)/sort(356), Character-info career(500)/profile(501)/leaderboard nav(502), Server-list sort & filters(537).

## 7. Honesty note / rows left partial intentionally

Rows where the residual gap was genuinely uncertain (tip-text side-colour fallback, target-bracket superset, nearest-enemy-bracket superset, BoardState in-flight draw) were **left partial** rather than risk over-crediting: per the standing rule, when unsure whether a gap is behaviour or visual, it is classified as **behaviour**. Several `partial` rows are *supersets* (the port draws an extra element native lacks) — they stay partial because the delta is not purely a missing-behaviour-visual pair and deserves a dedicated PAR classification. Pixel-parity (`partial → done`) was never applied.

## 8. Verification

- `python3 scripts/parity-scorecard.py` validates both matrices (0 errors) and prints the new visual column.
- `P674ParityScorecardTests` (incl. the 2 new tests) passed **3× alone** (10/10).
- Cited flip-pinning fixtures pass: Hud14ClassicGaugeTests, Hud10PanelsTests, HudInventoryPaneTests, RadarLabelModelTests, HudModelTests (146 tests, ClientCore.MonoGame), Hud10ServerClockLiveTests (1, ClientCore).
- `tools/build-tools.sh`: all tool projects build (run in this session).
- Solution build 0 errors / 0 warnings; one Fast-tier suite run per the light gate.
- No production code changed in `src/` or `tools/`; no `git stash`; worktree `~/allegiance-hermes-p782` on branch `hermes/p782`.
