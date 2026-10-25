# P805 (PAR-10) — HUD leftovers: finish the real gaps, reclassify the supersets with evidence

> **Claude review (2026-10-24).** The four captures were removed at merge: the command-view pair had the main menu
> over the target and the flight pair had no target selected, so neither showed the change. Real-window proof of the
> aleph/probe/treasure target preview and of the selection box in both styles is follow-up **P805b**. The selection
> box default was changed per Domingo: **off in Classic (native), on in Modern**; an explicit setting wins in either
> style (`HudSettings.SelectionBracketOn`).

Branch `pi/p805`, base public master `27d95413` (P803). No commits — all changes left in the working tree.
Report assets: `docs/reports/P805_assets/` (Classic+Modern pairs, ≤ 1 MB); full-size originals and client/server logs:
`~/allegiance-evidence/P805/` (outside the repo).

Native reference: `~/Development/Allegiance/src/` (WinTrek, Igc, engine), artwork `~/Development/Artwork/`
(`dialog.mdl` cited at its real line numbers). Every cite was re-grepped in this checkout at P805 start.

---

## 1. Implemented rows

### 1.1 Target model preview (parity row 409) — `done`

**Native rule.** `SetTargetGeo` (WinTrek/WinTrek.cpp:8783-8818) is NOT kind-specific: a model preview is shown for any
target whose `GetSourceShip()` differs from the player's own source ship, whose `GetHitTest() != NULL`, whose
`GetCluster() == trekClient.GetCluster()`, that `CanSee` passes, and whose `ThingSite` resolves a `ThingGeo`
(`GetTargetGeo()` rendered by the ortho `targetCamera`, dialog.mdl:4224-4231, radius-1 framing thinggeo.cpp:1482-1573).
Every kind the brief lists loads a ThingGeo:

- **Aleph (warp)** — `LoadWarp(textureName, iconName, …)` → `LoadCVH(NULL, iconName, c_htsCone)` +
  `ThingSite::LoadAleph(textureName)` (modelIGC.cpp:127-132; warpIGC.cpp:33-34). Surface ThingGeo.
- **Probe** — a per-type REAL mesh: `HitTest::Load(m_data.modelName)` (probetypeigc.cpp:28-33).
- **Mine** — `LoadMine(textureName, strength, radius)` sprite (modelIGC.cpp:134-139; mineigc.cpp:103).
- **Treasure** — `LoadDecal(texture, icon, …)` billboard (flag: `biosflag`) or the buyable's model
  (`Load(0, name, NULL, icon, …)`; cash/powerup `acs14`; artifact `artifact`; treasureIGC.cpp:118-171,
  modelIGC.cpp:276-291).

**Port before.** HUD-21/21b implemented the preview machinery (camera, light, radius-1 scale, zero-alloc key cache)
but `Eligible` hard-restricted kinds to Ship/Station/Asteroid; warp/probe/mine/treasure targets fell to the empty
viewport.

**Port after.** `TargetPreviewKind` gained `Warp`, `Probe`, `Treasure`, `Mine`; the gate accepts them (same native
conditions). Resolution reuses the existing world art paths — aleph: the `warpDef.TextureName + "bmp"` stem native's
`LoadAleph` loads, drawn as a camera-facing quad through the shared treasure decal path; probe: the core's
`DataProbeTypeIGC.ModelName` through the native model loaders; treasure: `TreasureVisualResolver` (P586) —
model treasures through `NativeTreasureRenderer.DrawModel`, the flag decal through `DrawDecal` at the native radius-1
half-size (new optional `half` parameter, default unchanged 5.0). Everything is drawn in the shared pane path, so
**Classic and Modern both show it** (Modern only overrides its decor). **Mine** is gated/resolved but not reachable:
the port's target cycle has no Mine kind (native targets mines through the default-order path the port has not ported).

**Data path.** No new wire data — the kinds resolve from the existing exports (`DataWarpIGC` (shipments), probe type
table, `TreasureEntity` code/payload).

**Tests.** `P805HudLeftoversTests.Preview_gate_accepts_warp_probe_treasure_and_mine_targets`,
`.Preview_kind_enum_has_the_four_new_kinds` — fail to compile on master. Edited
`Hud21TargetModelPreviewTests.Eligible_only_knows_the_three_in_scope_kinds` (3 → 7 kinds).

**Screenshots.** Classic + Modern command-view pairs with the Alderan aleph framed as target (the target group's
14347 m / 0 mps rows are §1.8's change; the aleph itself is behind the menu overlay in these captures — the preview
render path is pinned by the headless gate tests + hud2-proof render pass):

- `docs/reports/P805_assets/command-classic-target-group.png`
- `docs/reports/P805_assets/command-modern-target-group.png`

### 1.2 Selected target bracket (parity row 418) — `done` (opt-in superset)

**Native rule.** Native draws NO 3-D-view selection box. The selected target is marked only by the `c_maskTarget`
`targetedbmp` bracket on the radar contact (radarimage.cpp:307-319, 352; DrawBlip's bracket pass), already ported in
HUD-13.

**Port before.** The P345/P376 closed-rectangle selection box (plus name/hull/shield/ore/distance readout) drew around
every selected target in the world view — a superset with no native counterpart.

**Port after.** Kept as an explicit opt-in (`HudSettings.SelectionBracket`, **default OFF = native**): the flight draw
is gated in the shared `HudRenderer.Draw` (both skins — Modern overrides only the bracket's stroke style), and the
"Selected target bracket" element's visibility matches (`HudModel.Context.SelectionBracket`), so the proof tool's
element-coverage check stays honest. Native-intent classification: the box is a **modern readability extension**;
removing it entirely would discard working UX, so it is opt-in (the same treatment as `GaugePercentage`).

**Tests.** `P805HudLeftoversTests.Selection_bracket_defaults_off_native` — fails to compile on master.

**Screenshots.** Classic + Modern flight pairs, no selection box on the HUD:
`docs/reports/P805_assets/flight-classic-no-selection-bracket.png`,
`docs/reports/P805_assets/flight-modern-no-selection-bracket.png`.

### 1.3 Radar treasure and tech contacts (parity row 428) — `done`

**Native rule.**

- Development treasure: `UsefulTreasure` returns `pd->IsObsolete(trekClient.GetSide()->GetDevelopmentTechs())
  ? c_iNotUseful : c_iUsefulPart` (radarimage.cpp:452-463); `IsObsolete = m_techOnly &&
  (m_data.ttbmEffects <= ttbm)` (developmentigc.h:110-113). A not-useful treasure has no icon in any LOD but All
  (radarimage.cpp:877-899).
- Part treasure: `ppt->GetEffectTechs() <= GetDevelopmentTechs()` → `c_iUsefulTech` (icontechbmp override);
  launcher types additionally need `GetCluster() && GetHullType()->CanMount(ppt, 0)` (radarimage.cpp:487-511).

**Port before.** `RadarTreasure.Usefulness` implemented the switch but ran with a hard-coded
`partEffectsSatisfied: true` ("documented deferral — needs the live development-tech mask") and no obsolete test for
developments.

**Port after.** Both tests are live client-side from the **existing** P284 wire
(`CREATE_BUCKETS(191)` → `ClientWorld.DevelopmentTechsBySideId`): the part's `TtbmEffects` subset test and the
development's `DevelopmentLogic.IsObsolete` (already ported for the bucket catalog). No new wire data — the mask is
the same per-side tech state the shop and investment gates already read.

**Tests.** `P805HudLeftoversTests.Development_treasure_is_not_useful_once_obsolete`,
`.Part_treasure_effect_tech_gate_is_still_the_live_input`, `.Flag_treasure_is_a_useful_part` — fail to compile on
master.

### 1.4 Radar construction highlight (parity row 437) — `done`

**Native rule.** `c_maskHighlight` is set ONLY for a flag treasure
(`GetTreasureCode() == c_tcFlag → maskBrackets |= c_maskHighlight`, radarimage.cpp:637-640). No bitmap is ever loaded
for it (the surface loader at radarimage.cpp:214-235 has no highlight entry) and `DrawBlip`'s bracket pass handles
only enemy/me/subject/flag/artifact/rip/targeted/last-fired (:336-357). Its native effects are:
full `colorOther` brightness (no ×0.5 dim, :1094-1096) and a forced label gate (:707).

**Port before.** The port drew an invented procedural white ring for `Highlight` (RadarBracketRenderer
`DrawRing(17px)`) and never set the mask on any contact; the P495 asteroid build-spot pulse was (and stays) a separate
feature.

**Port after.** A flag-treasure contact carries `Highlight: te.Code == TreasureCode.Flag`; the renderer no longer
draws anything for the mask (`HasOverlayBracket` no longer counts it) — the full-brightness effect stays via
`RadarLabelModel.BracketBrightness`. P495 build-spot pulse classified: **design extension** (native has no
asteroid-construction radar surface; kept for build UX, documented in the matrix note).

**Tests.** `P805HudLeftoversTests.Highlight_only_contact_draws_no_overlay_bracket` — **fails at runtime on master**
(verified on a master worktree: master returns `true` because the invented ring counted as a bracket).

### 1.5 Tip text lines (parity row 456) — `done`

**Native rule.** Tip/sector console text colour is `mySideColor = GetSideColor(Me, OnEveryFrame)` (dialog.mdl:26,
used by TipTextImage/TipTextImage1/TipTextImage2 at :2006-2033), whose `ModelData::GetSideColor` fallback is
`Color(1,1,1)` **white** (consoledata.cpp:99-107) — never an accent hue.

**Port before.** Both skins fell back to the port teal accent (Classic `TextMain`, Modern `Accent`) when the own-side
colour was not yet known.

**Port after.** Both skins fall back to `Color.White` (the native fallback), cited at the fallback site. Three
ordinary slots / newest-first / retention / font tiers were already native (HUD9).

**Tests.** `P805HudLeftoversTests.Tip_side_colour_fallback_is_white` — fails to compile on master. Visible in the
real-client pairs (top-centre tips render white in both styles).

### 1.6 Money credits (parity row 459) — `done` (no code change; evidence)

**Native rule.** There is **no always-visible flight credit counter** in native. `console.cpp:792` only feeds the
`Money` data slot (`m_pnumberMoney->SetValue((float)trekClient.GetMoney())`); the only surface that reads it is
`moneyPane` — `creditboxbmp` + `NumberString(Money) + " cr"` + `CURRENT CREDITS` (dialog.mdl:1885-1908) — which lives
exclusively inside `MergedInvestmentsPaneImage` under `ofInvestment` (dialog.mdl:5199-5203, 5368), i.e. the F5 pane.

**Port.** The port already matches: the flight HUD has no money readout (the old P442 line was removed in HUD-10;
`HudRenderer.DrawMoney` is dead code, uncalled), and the investment pane draws the real creditbox art + value + label
at the native anchor (`DockedScreenRenderer` `nativeMoneyPanel`; `MoneyCreditsLayout` pinned by
`Hud10PanelsTests.MoneyCreditsLayout.Label == "CURRENT CREDITS"`). The row's "BEHAVIOUR GAP" reason misread
console.cpp:792 (a data update, not a flight surface). Matrix updated to `done` with the full evidence.

### 1.7 HUD style and software layouts (parity row 470) — `done-visual`

**Native rule.** Independent, persisted visibility switches:
`ShowCenterHUD` (WinTrek.cpp:2898, `LoadPreference("CenterHUD", TRUE)` :3300-3301, `ToggleCenterHUD` :5082-5086),
`ShowTargetHUD` (:2899, :3302-3303, :5090-5094), gating `VisibleImage(CenterHudDisplay, ShowCenterHUD)` /
`VisibleImage(TargetDisplay, ShowTargetHUD)` (dialog.mdl:5195-5198); plus the **menu-only** (NOT persisted)
`ShowChatHistoryHUD` (:2897, `ToggleChatHistoryHUD` :5074-5078, idm :3934). The skin selector and software HUD
layouts (`cortui_huds.mdl`, soft/Parci centre-HUD variants) are CortUI-era art.

**Port after.** `HudSettings.ShowCenterHud` / `ShowTargetHud` (default true, persisted like native) and a session-only
chat-history toggle (default true, not persisted — matching native) gate the **shared model** in both skins:
`HudModel.CentreHudVisible` hides the whole centre group (gauges, speed, afterburner, own-ship status),
`TargetPaneVisible` gains the `TargetHudVisible` leg, and the chat history lines hide while the composer still draws
(classic + modern). All three are rows 13-15 of the port settings menu (native exposes them in its Graphics menu).
The global F1 hide stays on top, unchanged.

**Classification.** The visibility behaviour is native and done; the skin selector / software layouts are art-only →
`done-visual` (`VISUAL:` reason in the matrix).

**Tests.** `P805HudLeftoversTests.Center_and_target_hud_settings_default_on_like_native`,
`.Target_pane_gates_on_the_native_target_hud_switch`, `.Centre_hud_gates_on_the_native_center_hud_switch`,
`.Chat_history_gate_hides_history_but_defaults_on`; edited
`Hud19CentreHudTests.Graphics_overlay_has_the_joystick_row_and_it_toggles` (row count 13 → 16). All fail to compile
on master.

### 1.8 Console pane visibility and slide animation (parity row 471) — `done-visual`

**Native rule.** Every pane entry in the dialog.mdl pane table carries two anchors and a 0.5 s slide
(`AnimatedTranslateTransform2`, trekctrls.cpp:996-1060); `SetOverlayFlags` plays `paneSlideSound` on any mask change
(console.cpp:3268-3290). The target pane: `(TargetDisplayImage, SideBottom, Point(-320,-183), Point(-320,25), .5,
ofInCockpit | ofInChase | ofGloatCam, true)` (dialog.mdl:5404) — 208 native px of travel.

**Port before.** The eight HUD-10 panes (chat composer, command, inventory, investment, teleport, team, game state,
sector) already slid with the sound; the target pane did not.

**Port after.** `SlidingPane.TargetPane` rides the same `PaneSlideTracker` (mask = `InCockpit | InChase`, 0.5 s) and
both `DrawTargetPane` overrides apply `TargetPaneLayout.SlideOffset(progress, scale)` = `(1−p)·208·s` px downward —
**Classic and Modern**. Only art the port does not draw remains un-offset (GlassHUD variants etc.) → `done-visual`.

**Tests.** `P805HudLeftoversTests.Target_pane_slide_tracks_the_cockpit_chase_mask`,
`.Target_pane_slide_offset_matches_the_native_travel` — fail to compile on master.

### 1.9 Command view ship summary (parity row 492) — `done-visual`

**Native rule.** `cortCommandModeInfo` (SideLeft(-200,120)→(36,120), dialog.mdl:5412) contains the `cortTargetGroup`
under `IsNotNull(Target, OnEveryFrame)` (dialog.mdl:4689-4705): `cortTargetShieldsGauge`/`cortTargetHullGauge`
("Target shields:"/"Target hull:", :4658-4688), `cortTargetRange` — `NumberString(GetRange) + "m"` (NO space), or the
sector name when GetRange is −1 (:4606-4628) — and `cortTargetSpeed` — `NumberString(GetSpeed) + " mps"` (:4630-4656).
Money/autopilot/radar-mode feed their own console slots (console.cpp:791-793), NOT this group; the "name" row is the
experience badge (dialog.mdl:4597).

**Port before.** The summary echoed a port-invented `money/AP/RADAR` line (`HudStringCache.SummaryLine`) and a
`Target: <name>` row; the target group's range/speed were missing.

**Port after.** Both inventions removed per the native-intent rule (`SummaryLine` deleted; its tests rewritten); the
native target group is drawn in the shared draw method — "Target shields:"/"Target hull:" softgauges, then the range
(`Nm`/sector fallback) and speed rows — in **both** skins (Modern reuses the classic draw). Softgauge art metrics stay
the port bar inset → the row's remaining difference is visual.

**Tests.** `P805HudLeftoversTests.Command_summary_target_range_has_no_space_before_the_unit`,
`.Command_summary_target_speed_prints_native_mps` (fail to compile on master); `Hud12bReviewFixTests` rewritten for
the removed `SummaryLine` / added helpers.

**Screenshots.** Both command-view pairs show `14347m`/`14210m` + `0 mps` (the framed Alderan aleph) under the own
readouts, with the invented line gone:
`docs/reports/P805_assets/command-classic-target-group.png`,
`docs/reports/P805_assets/command-modern-target-group.png`.

---

## 2. Reclassified rows (no behaviour change; evidence)

### 2.1 Radar nearest enemy bracket (parity row 431) — `done-visual`

Native's `enemybmp.mdl` in the checkout decodes **fully transparent** (real-client log:
`status art 'enemybmp' is fully transparent ('enemybmp.mdl') — procedural fallback`), so native's `c_maskEnemy`
`DrawExpandedBlip` (radarimage.cpp:334-336) paints an invisible surface — native draws no visible nearest-enemy
bracket. The port's procedural diamond at the same native `radiusBracket` (gated on `bRangeLabels` + the native
FindTarget rule, HUD5b/13) is a readability addition **over an invisible native surface** → `VISUAL:` reason recorded.

### 2.2 Boundary and crowd warnings (parity row 467) — `done-visual`

Native's boundary/crowd warning surface is **audio-only**: on the `GetWarningMask()` rising edge it plays
`salBoundryExceededSound` / `salSectorCrowdedSound` and draws nothing (WinTrek.cpp:8650-8666,
`c_wmOutOfBounds` / `c_wmCrowdedSector`). The port plays the same two voices on the same rising edges
(`SoundManager.UpdateShipWarnings` — already native since the warning-mask port). The textual
`OUT OF BOUNDS` / `SECTOR CROWDED` badges are a port readability addition on top of a native behaviour that has no
visual surface at all → `VISUAL:` reason recorded; badges kept (visuals may differ).

### 2.3 Radar command subjects (parity row 433) — still `partial` (analysed, not closed)

Native sets `c_maskSubject` on ships found in the console's subject list — populated by `SetRecipient` for
`CHAT_GROUP` (every ship of the group), `CHAT_INDIVIDUAL` (the one ship) and `CHAT_SHIP` (own source ship + child
ships) — and draws the `subjectbmp` bracket (radarimage.cpp:340-342, 651-652; console.cpp:269-455,
`ToggleSubject` multi-select at :112-135). The port composer still has no individual/group recipient selection
(the P784 cycle deliberately skips Individual and there is no group multi-select), so the two subject lists can never
contribute contacts. Closing this row requires the console.cpp:269-455 recipient machinery (a chat/commanding feature,
not a HUD surface) and did not fit this wave; the row stays open with this analysis recorded.

---

## 3. Verification

- **Build:** `~/.dotnet/dotnet build AllegianceCore.slnx` — 0 warnings, 0 errors.
- **Tools:** `tools/build-tools.sh` — all 73 tool projects, 0/0.
- **hud2-proof render pass:** `HUD2_RENDER_PASS 288 rendered scenarios` (element coverage, overlap, hidden-text
  checks pass in both skins after the bracket gating).
- **New/affected tests, 3× alone:** `P805HudLeftoversTests` (16) + edited `Hud12bReviewFixTests`,
  `Hud19CentreHudTests`, `Hud21TargetModelPreviewTests` + neighbours `Hud10Panels/Hud12DataAndArt/Hud13RadarBracket`:
  3× pass (136 tests/run incl. ModernHud GPU pair 3×; the headless subset re-run 8 more times, all green).
  One unidentified single failure in one intermediate batch run (1/136, of ~11 runs) did not reproduce in 12
  subsequent runs — treated as a load flake (other agents were running suites concurrently); not ledgered because the
  failing test could not be captured.
- **Fail on master:** verified in a scratch worktree at `27d95413`: 15/16 new tests fail to COMPILE on master
  (new APIs); `Highlight_only_contact_draws_no_overlay_bracket` compiles and FAILS at runtime on master.
- **Fast tier:** `tools/run-tests.sh --tier fast` — 10213 tests, PASS
  (`TestResults/tiers-20261024T214729.601647Z/summary.json`).
- **Zero-allocation frame path:** `Hud12bReviewFixTests` allocation tests pass unchanged; all new draw-path strings
  go through the value-keyed `HudStringCache` (`TargetRange`/`TargetSpeed` caches), the slide offset is pure float
  math, and the preview key stays a readonly struct.
- **Scorecard:** `scripts/parity-scorecard.py` validates; hud block now **92.66 %** (was 65 % weighted at dispatch),
  total 94.86 %.
- **Line endings:** `docs/parity/parity-matrix.csv` rewritten with `csv.writer(lineterminator='\n')`; `grep -c $'\r'`
  = 0.

## 4. Parity rows updated (`docs/parity/parity-matrix.csv`)

| Row | Before | After |
|---|---|---|
| Target model preview (409) | partial | done |
| Selected target bracket (418) | partial | done |
| Radar treasure and tech contacts (428) | partial | done |
| Radar nearest enemy bracket (431) | partial | done-visual |
| Radar construction highlight (437) | partial | done |
| Tip text lines (456) | partial | done |
| Money credits (459) | partial | done |
| Boundary and crowd warnings (467) | partial | done-visual |
| HUD style and software layouts (470) | partial | done-visual |
| Console pane visibility and slide animation (471) | partial | done-visual |
| Command view ship summary (492) | partial | done-visual |
| Radar command subjects (433) | partial | partial (analysis recorded, §2.3) |

## 5. Unfinished / follow-ups

> **P805b follow-up (done).** The real-window proof below was delivered by **P805b**
> (`docs/reports/P805B_HUD_PROOF.md`), which also fixed four preview/dispatch bugs this task's headless gate tests
> could not see: the probe preview threw on a null visual; the aleph preview drew the texture as a flat quad instead of
> native `LoadAleph`'s additive vortex (`effect/alephgeo.cpp:80-102`); part/development treasures resolved their art
> from the object id instead of the `DataTreasureIGC.TreasureId` payload (`treasureIGC.cpp:118-171`), so they previewed
> nothing; and the Modern selection box never drew because the `SelectionBracketOn` gate was only wired into the classic
> renderer's constructor. The §1.2 claim "Modern overrides only the bracket's stroke style" therefore needed the P805b
> fix to be true in a real window.

- **Radar command subjects (433)** — needs the console.cpp:269-455 individual/group recipient machinery (see §2.3).
- **Mine target preview** — the gate/art path exists but the port cannot target a mine yet.
- **Aleph/probe/treasure preview screenshots** — **done in P805b** (Classic + Modern pane captures, both styles plus the
  Classic `SelectionBracket: true` override).
- **Modern target-pane slide art** — the offset is applied; a mid-slide capture was not staged (transient state).
