# P783 (PAR-2) — radar label and stat tiers per contact kind, as native

Base: **master** `19b32562` (re-checked `git log -1` at start; every native line
number below was re-grepped before use). Worktree `allegiance-pi-p783`, branch
`pi/p783`, model DeepSeek (`deepseek/deepseek-flash`, thinking high). **Client-only**:
no server, wire or core change. No commits, no pushes — everything is in the
working tree.

Native citations are the checkout at `~/Development/Allegiance/src/`.

## 1. The native table (what P783 implements)

Native decides a contact's label block in one place per contact: the per-kind
switch at `WinTrek/radarimage.cpp:703-961` sets `bIcon`/`bLabel`/`bStats` and
rewrites `range`, after which the label pass (`:1199-1462`) prints the name line
(`:1199-1208` measure, `:1394-1398` draw), the range line (`:1210-1216`,
`:1400-1404`), and the shield/hull/fill bars (`:1406-1458`).
Two facts drive the whole table:

- **the important mask short-circuits it.** `bLabel = (maskBrackets & (target |
  accepted | queued | highlight | threat | flag | artifact)) != 0` and
  `bStats = bLabel` (`:704-711`); when that is true the per-kind switch is *not
  entered at all*, so an important contact keeps name + stats + range in **every**
  LOD (and the full `colorOther`, `:1094-1096`).
- **`Minimal` (native "None") is checked before the switch** (`:724-731`): every
  non-self, non-important contact loses icon and range, and *no* non-important
  contact gets a label — including the own ship, which keeps only its icon.

Context used by the switch: `type` = the contact kind, `m_radarLOD`
(`radarimage.h:42-52`: All / Default / Target / Minimal, cycled by
`WinTrek.cpp:9955-9975`, text `c_szRadarLODs` `radarimage.cpp:2-5`), `pside` vs
`psideMine` (exact side and `AlliedSides`, `igc.h:4297`),
`ucRadarState != c_ucRadarOffScreen` (on screen), and `separation`, which is
**the surface separation** — `separation -= radiusSource + pmodel->GetRadius()`
(`:626-636`) runs *before* the switch, so every `rangeClipLabels` (`= 1000.0f`,
`:18`) test compares radii-adjusted distance while the printed range line is the
centre distance `int(separation + 0.5f)` (`:626-628,1210`).

In the table, **clip** = the range line is dropped when the **surface**
separation ≥ 1000 m (`rangeClipLabels`).

| Kind | LOD | name | range | hull / shield | ore / fill | aim-lock | clip |
|---|---|---|---|---|---|---|---|
| own ship (`OT_ship`, self) | All | **yes** (`:742`) | no (range = 0 for self, `:626-628`) | **yes** `:742`, `:981-987` | ore only if a same-side `c_ptMiner` (`:992-997`) | lock bracket only if targeted (`:345-352`, LOD-independent) | — |
| | Default | yes (`:749`, self is always on screen) | no (`:752-753`) | **no** (same side ⇒ no `bStats`, `:752-755`) | no | same | — |
| | Target | **no** (friendly branch, `:760-767`) | no | no | no | same | — |
| | Minimal/None | **no** (`:724-731`) | no | no | no | same | — |
| friendly ship (exact own side) | All | yes | yes only while **on screen**; an off-screen same-side ship drops it (`:743-744`) | yes | ore if `c_ptMiner` | lock bracket if targeted | rangeClipLabels on the off-screen case only |
| | Default | yes if on screen **or** within the clip (`:749-750`) | **no** — the exact own side zeroes it (`:752-753`) | no (`bStats` stays false) | no | same | name clipped |
| | Target | **no** — the whole contact leaves the radar (`:760-767`) | no | no | no | — | — |
| | Minimal | no | no | no | no | — | — |
| allied ship (`AlliedSides`, not exact side) | All | yes | yes (the off-screen rule is exact-side only) | yes | ore only for the exact own side | lock bracket | — |
| | Default | yes if on screen or within the clip | yes (**kept** — not the exact side) | **yes** (`:754-755`) | no | same | name clipped |
| | Target | **no** (ally = friendly branch) | no | no | no | — | — |
| | Minimal | no | no | no | no | — | — |
| enemy ship | All | yes | yes | yes | no (not own side) | lock bracket + `c_maskEnemy` | — |
| | Default | yes if on screen or within the clip | yes | yes (non-own-side) | no | same | name clipped |
| | Target | yes only within the clip (`:773-774`) | **always kept** (`:768-775` never zeroes it) | yes only while **on screen** (`:770-771`) | no | same | name clipped |
| | Minimal | no | no | no | no | — | — |
| station, non-pedestal | All | yes | yes | **yes** (`:798`, `:1039-1044`) | no | lock bracket | — |
| | Default | no | yes only while on screen **and** within the clip (`:803-804`) | no | no | — | rangeClipLabels |
| | Target | no | **no** — native sets range = 0 (`:809`) | no | no | — | all |
| | Minimal | no | no | no | no | — | — |
| station, pedestal (`c_sabmPedestal`) | All/Default/Target | no | yes only within the clip (`:784-791`) | no | no | lock bracket | rangeClipLabels in **every** non-Minimal LOD; Minimal suppressed at `:724-731` |
| | Minimal | no | no | no | no | — | — |
| warp (`OT_warp`) | All | **yes** | yes | no | no | lock bracket | — |
| | Default | **yes** (`:954`) | yes | no | no | — | — |
| | Target | **no** (`bLabel = mode <= Default`) | yes only within the clip (`:955-956`) | no | no | — | rangeClipLabels |
| | Minimal | no | no | no | no | — | — |
| asteroid, generic (unnamed or `'A'…`) | All | no | yes only within the clip (`:907-915`) | no (the whole contact is icon-only) | no | — | rangeClipLabels |
| | Default | no | yes only within the clip (`:924-933`) | no | no | — | rangeClipLabels |
| | Target | no | yes only within the clip (`:939-943`) | no | no | — | rangeClipLabels |
| | Minimal | no | no | no | no | — | — |
| asteroid, named special | All | **yes** (`:917-918`) | yes | **yes** (`:918`, `:1046-1069`: hull + ore fill) | yes (`GetOreSeenBySide/(2·capacity)`, `:1051-1062`) | lock bracket | — |
| | Default | no | yes (the default branch has no named-rock clip) | no | no | — | — |
| | Target | no | yes only within the clip (`:939-943`) | no | no | — | rangeClipLabels |
| | Minimal | no | no | no | no | — | — |
| missile | All | no | **yes** | no | no | lock bracket only if targeted; the *contact* tier keys on the missile's own target | — |
| | Default | no | only if the missile's target exists and is on the **exact** own side (`:821-830`, no ally term) | no | no | same | — |
| | Target | no | same aim gate | no | no | same | — |
| | Minimal | no | no | no | no | — | — |
| probe / mine | All | **only for an on-screen enemy** (`:841-850`) | yes, except a same-side/ally contact that is **both** off screen and beyond the clip (`:843-844`) | **yes** for every probe/mine (`:852`) — probe hull `:1080`, probe/mine fill `:1073,1079` | fill = `GetTimeFraction()` | lock bracket (probes selectable) | rangeClipLabels (off-screen same-side/ally case) |
| | Default | no | yes unless it is a same-side/ally contact beyond the clip (`:857-861`) | no | no | — | rangeClipLabels for same-side/ally |
| | Target | no | only a within-clip **enemy** (`:866-869`) | no | no | — | rangeClipLabels |
| | Minimal | no | no | no | no | — | — |
| treasure / tech, useful | All | **yes** (`:882`) | yes | no (treasure has no stats case) | no | lock bracket / flag highlight | — |
| | Default | no (`bLabel = mode == All`) | yes | no | no | — | — |
| | Target | no | yes only within the clip (`:883-887`) | no | no | — | rangeClipLabels |
| | Minimal | no | no | no | no | — | — |
| treasure, not useful | All | no | yes | no | no | — | — |
| | Default/Target | — contact is not on the radar at all (`:893-897`, `RadarLod.ShowTreasure`) | — | — | — | — | — |
| | Minimal | no | no | no | no | — | — |

Two cross-cutting rules from the same block: `!bLabel ⇒ pszName` is cleared
**but the range line survives on its own** (`:967-974`; that is why a Target-mode
enemy beyond the clip still prints just a number), and `bStats` gates *all* stat
readings (`:977`), with a value `> 1.0f` acting as the "no reading" sentinel
(`shield/hull/fill` start at `2.0f`, `:963-965`; the bars render only for
`<= 1.0f`, `:1406,1430,1445`).

**Aim/lock tier.** The missile lock bracket itself is drawn before the label
switch iff the contact is the current target and the magazine reports lock
(`lock != 0.0f`, `:345-352`), so it does not depend on the radar LOD. The
*missile contact* tier is the aim-keyed one (`:819-832`): outside All a missile
keeps its icon/range only when `pmodelTarget->GetSide() == psideMine`.

## 2. What the port did before, and what changed

Before P783 `RadarLabelModel.Resolve` handled ship/station/warp/asteroid only and
everything else (missile, probe, mine, treasure, tech) fell through with
`bLabel = bStats = false, range = raw`; the clip compared the **printed centre
range** (`contact.Range`) instead of the surface separation; the ship ore fill was
gated only on "friendly" (not `c_ptMiner`); the own-ship contact carried no name
or stats; and — the biggest visual bug — `RadarLabelCache` published its range
line from the **raw** contact range, so every native clip (station Target, warp
Target, asteroid, probe/mine) was silently defeated at draw time.

The fix is one pure table plus the context it needs:

| File | Change |
|---|---|
| `Rendering/RadarLabelModel.cs` | new `RadarLabelSpec` + **`RadarLabelModel.Spec(contact, mode, commandCamera)`** = the single pure `kind × LOD × context → label spec` function, a literal port of `radarimage.cpp:703-961` (important mask, Minimal, then one branch per kind incl. **missile, probe, mine, treasure, tech**). `Resolve` is now `Spec` + the raw readings (`:967-981`). New helpers `IsImportant`, `WithinRangeClip` (the `:626-636` surface-separation clip, falling back to the range for headless fixtures), `RangeLineShown` (`:523-529,626-628`). |
| `Rendering/HudModel.cs` | `HudContact` gains the native context the table reads: `IsAllied` (exact side vs `AlliedSides`), `ClipSeparation` (the adjusted separation the clip uses), `MissileTargetsOwnSide`, `TreasureUseful`, `IsPedestalStation`. All optional, appended, so every existing call site still compiles. |
| `Rendering/RadarLabelCache.cs` | (a) the cached `RangeLine` now follows the resolved label instead of the raw contact range — the drawn range obeys the tier/clip; (b) the range-only fast path re-reads `Spec` so a clipped range clears the line; (c) the cache key adds the new context plus the **clip verdict** (not the moving metre value), so a steady contact keeps its cached label and only a 1000 m crossing rebuilds. |
| `Game1.Rendering.cs` | the radar gather fills the new context for **every** kind: `ClipSeparation = LodSeparation(centre, radius)` (native `:626-636`), `IsAllied`, `IsPedestalStation`; missiles get a range line + `MissileTargetsOwnSide`; probes/mines get their type model name, range, side relation, clip and the probe's real hull fraction; treasures get the native name (buyable model name / `Flag` / `Artifact` / `acs14`, `Igc/treasureIGC.cpp:121-167,193`) and `TreasureUseful`; the own ship carries its real name + hull/shield for the All tier; the ship ore fill now needs `PilotType == c_ptMiner` **and** the exact own side (`:992-997`). |

Both HUD skins consume the table through the one path they already shared:
`HudRenderer.DrawRadarLabels` → `RadarLabelCache.Resolve` →
`RadarLabelModel.Resolve/Spec`; `ModernHudRenderer : HudRenderer` inherits it and
does not override the label pass, so there is no second copy of the rules.

## 3. Tests

New file `tests/Allegiance.ClientCore.MonoGame.Tests/P783RadarLabelTiersTests.cs`
— 17 facts, one per kind across **every** LOD (plus the cross-cutting rules):

| Fact | What it pins (native cite) |
|---|---|
| `Own_ship_tiers` | All name+stats, Default name only, Target/Minimal nothing (`:740-745,724-731`) |
| `Friendly_ship_tiers` | on-screen All range kept / off-screen All range dropped, Default name-without-range and no stats, Target hidden (`:742-757,760-767`) |
| `Allied_ship_tiers` | ally keeps range in All **and** Default and *does* get Default stats; Target hides it (`:743,749-755,760`) |
| `Enemy_ship_tiers` | All/Default name+range, Default stats even off screen/beyond clip, Target name within clip / stats on screen / range always (`:749,754,770-774`) |
| `Station_tiers` | All name+stats+range, Default range only when on screen and within clip, Target no label, pedestal on the fixed clip in All/Default/Target and suppressed in Minimal (`:784-813,724-731`) |
| `Warp_tiers` | name in All+Default, Target range only, Target clip on the surface separation (`:952-957,626-636`) |
| `Asteroid_tiers` | generic rocks label-less with a clip-limited range in all three LODs, named rocks name+hull+ore in All only, Default named rock **not** clipped, Target clipped (`:901-950`) |
| `Missile_tiers` | no name/stats ever, All range, outside All only the exact-own-side aim keeps the range (`:819-832`) |
| `Probe_tiers` | All stats + on-screen-enemy name + the three range cases, Default range clip, Target enemy-within-clip, probe hull feeds the bar (`:834-875,1077-1082`) |
| `Mine_tiers` | the shared probe/mine path for a mine, and no fill bar (`:834-875,1071-1075`) |
| `Treasure_tiers` | useful part **and** tech: name in All only, range everywhere but the Target clip, never stats; not-useful treasure has only the All range (`:877-899`) |
| `Important_mask_forces_every_tier_in_every_lod` | the mask short-circuit (`:704-711`) and the command-camera range zero (`:523-529`) |
| `Minimal_suppresses_every_non_important_label` | one sweep over every kind (`:724-731`) |
| `Clip_uses_the_adjusted_separation_not_the_printed_range` | `WithinRangeClip` on the surface separation vs the printed range (`:626-636`) |
| `Command_camera_removes_every_range_line_but_keeps_name_and_stats` | `bRangeLabels` only kills the range line (`:523-529`) |
| `Clipped_range_never_reaches_the_drawn_range_line` | the regression this task fixes in `RadarLabelCache` |
| `Label_cache_rebuilds_when_the_contact_crosses_the_clip` | the cache invalidation |

**Which of these fail on the unfixed code.** The new tests use `Spec`/
`WithinRangeClip`, which do not exist pre-P783, so to get a real red run I
temporarily restored `HEAD`'s `RadarLabelModel.cs` + `RadarLabelCache.cs` and ran
the same assertions through `Resolve` only (`P783PreFixProbeTests`, deleted after
the run; the raw output is in
`~/allegiance-evidence/P783/pre-fix-probe.log`):

```
Failed!  - Failed: 5, Passed: 1, Total: 6
  Probe_all_stats_and_name_tier                Expected: "Probe" / got null      (no probe tier)
  Treasure_name_only_in_all                    Expected: "Powerup" / got null    (no treasure tier)
  Missile_aim_tier_outside_all                 Expected: null / had a value      (enemy-aimed missile labelled)
  Warp_target_clip_uses_the_surface_separation Expected: not null / got null     (printed range used as the clip)
  Clipped_range_never_reaches_the_drawn_range_line Expected: null / drew "900 m" (cache ignored the clip)
```

The one that passed pre-fix is the "mine Default has no hull" half, because the
old model never produced stats for anything but ships/stations/asteroids.

Three-times-alone runs of the new and affected tests, and the one Fast-tier run,
are in §4.

## 4. Verification (light gate)

| Gate | Command | Result |
|---|---|---|
| Whole-solution build | `~/.dotnet/dotnet build AllegianceCore.slnx -c Debug` | **0 warnings, 0 errors** |
| Tools build | `tools/build-tools.sh` | `build-tools: all 70 tool projects built` (0/0) |
| New tests ×3 alone | `dotnet test ... --filter FullyQualifiedName~P783RadarLabelTiers` | **17/17 passed** three times (25–32 ms) |
| Affected tests ×3 alone | `RadarLabelModelTests`, `Hud5bReviewFixTests`, `Hud4RadarTests`, `Hud17DetectionEyeTests`, `Hud11LayoutPerfTests` | **50/50 passed** three times |
| Whole client test project (one-off) | `dotnet test tests/Allegiance.ClientCore.MonoGame.Tests` | **3251 passed, 0 failed**, 3 skipped (skipped are the Meshy pack-asset checks) |
| Fast tier (once) | `tools/run-tests.sh --tier fast --no-build` | **PASS — 9983 tests, 288.7 s** (`fast: 9983 tests, 288.7s, PASS`); MonoGame 3231 passed / 3 not-executed, ClientCore 1650+2, Core 5063+10, Lobby 24 |
| Parity CSVs | `python3 scripts/parity-scorecard.py` | `parity-matrix.csv: validated 668 rows`, `sim-rules-matrix.csv: validated 473 rows`; `hud 43.12% (141/327)` unchanged (see §6) |

Live proof itself rebuilt Release (`Allegiance.Server`, `Allegiance.ClientCore.MonoGame`,
`allegiance-mcp`) with 0 warnings / 0 errors. The AOT publish gate does not apply
(no `src/Allegiance.Server` or `src/Allegiance.Core` change).

## 5. Screenshots — real window, Default vs Target (and All / None)

Captured with `scripts/live-proof.sh` (real windowed `Allegiance.ClientCore.MonoGame`,
2560×1440 presented backbuffer, never `--capture`), a real local
`Allegiance.Server` (`m2xspiderw.igc`; its debug topology puts side 0's home in
`Omega` 1053 and side 1's in `Plato` 1060), 1 v 1 MCP arena bots in the client's
sector (`--bots 'spawn_scenario {"size":1,"target_sector":"Plato","mode":"arena"}'`), and
the existing `ALLEGIANCE_FLIGHTKEYS_PROOF=1` radar-mode sequence (P624) which
takes one real frame per LOD. Command run:

```
ALLEGIANCE_FLIGHTKEYS_PROOF=1 scripts/live-proof.sh --art /home/domingo/Development/Artwork \
  --auto-side 1 --client-extra '--auto-launch' \
  --bots 'spawn_scenario {"size":1,"target_sector":"Plato","mode":"arena"}' \
  --exit-after 34 --out .scratch/p783/run6
```

The client's real radar label blocks in each frame (every image below was opened
with the read tool and described from what is visible):

| Asset (in `docs/reports/P783_assets/`) | Radar LOD | What is visible |
|---|---|---|
| [`radar-default-cockpit-2560x1440.webp`](P783_assets/radar-default-cockpit-2560x1440.webp) | **Default** | Cockpit, `RADAR Default` bottom-left. Top centre: friendly bot `mcp-b001-7102` with **name only — no range line and no bars** (Default: same side ⇒ range zeroed, no stats). Below it the warp `Alderan 1047 m` (Default keeps a warp's name *and* range). Left edge: enemy `mcp-b002-9221  519 m` — name, range and a red hull bar (Default gives non-own-side ships stats). The Plato garrison is absent: beyond 1000 m an off-screen station loses both name and range. |
| [`radar-target-cockpit-2560x1440.webp`](P783_assets/radar-target-cockpit-2560x1440.webp) | **Target** | Same frame, `RADAR Target`. The friendly `mcp-b001-7102` label is **gone** (friendly/allied ships leave the Target radar) and the warp label is now a **bare `1009 m`** with no name — and it still prints even though 1009 > 1000, because Target's clip runs on the *surface* separation (the warp radius is subtracted), which is the native rule this task ported. The enemy `mcp-b002-9221  578 m` keeps name+range but **no bars** (it is off screen, and Target only gives stats on screen). |
| [`radar-all-cockpit-2560x1440.webp`](P783_assets/radar-all-cockpit-2560x1440.webp) | All | `RADAR All`: friendly `mcp-b001-7102` with **two bars** (blue shield + red hull) and **no range** (it is off screen and same-side), warp `Alderan 1114 m`, enemy `mcp-b002-9221  472 m` with bars, and the own-side station `Plato Debug Garrison 1497 m` with bars — the All tier's "name + stats + range for everything". |
| [`radar-minimal-cockpit-2560x1440.webp`](P783_assets/radar-minimal-cockpit-2560x1440.webp) | None (Minimal) | `RADAR None`: **no radar label block anywhere** — the Minimal tier native applies before the per-kind switch. |

The Default↔Target pair is the required evidence: the friendly ship loses its
label, the warp loses its name and keeps (only) its range, and the enemy keeps
its range but loses the off-screen stats. The All and None frames bound the
table at both ends.

These four frames are the **Classic** skin (the launcher default). The Modern skin
has no separate radar label pass — `ModernHudRenderer : HudRenderer` renders
labels through the same inherited `DrawRadarLabels` → `RadarLabelCache.Resolve` →
`RadarLabelModel.Spec` path (it only swaps the text renderer), so the table has a
single implementation for both skins; no extra Modern frame was captured.

Raw frames, the client/server logs and the MCP transcript are outside the repo in
`~/allegiance-evidence/P783/` (`raw/`, `client.out`, `server.out`,
`mcp_out.log`, `server.json`); the four cited images are ≤ 250 KB total in
`docs/reports/P783_assets/`.

## 6. Parity rows touched

All eleven rows below stay `partial` — `scripts/parity-scorecard.py` has no
`done-visual` status yet (`STATUSES` = done/implemented/partial/missing/na), so
per the brief a behaviour-complete row whose visuals still differ cannot be
flipped; each reason now states exactly what is native and what is left.

- **Radar ship contacts** — label tiers now native (All/Default/Target/Minimal,
  own ship included), miner-gated ore fill. `VISUAL:` the port's horizontal
  24 px fixed-colour bar row vs native's 16×2 px stack in the label colour
  (`radarimage.cpp:1406-1458`), and the substituted label font.
- **Radar station contacts** — native All/Default/Target tiers, pedestal clip,
  Target range drop. Same `VISUAL:` bar/font note.
- **Radar warp contacts** — native labels **and** the Target range clip on the
  surface separation. `VISUAL:` font.
- **Radar missile contacts** — aim tiers implemented (range-only contact; the
  exact-own-side aim gate outside All). `VISUAL:` font.
- **Radar probe and mine contacts** — full tier table + probe hull bar.
  `BEHAVIOUR GAP:` the lifetime fill bar (`GetTimeFraction`, `probeigc.h:271-279`,
  `mineigc.h:183-191`) needs the object's `m_time0`, which the probe/mine export
  does not carry, so no probe/mine fill is drawn.
- **Radar treasure and tech contacts** — useful/not-useful tiers, native names,
  Target clip, never stats. `BEHAVIOUR GAP:` the live dev-tech split
  (`UsefulTreasure`'s `c_iUsefulTech`) and a flag-treasure highlight contact are
  unchanged from before this wave.
- **Radar LOD modes** — the per-kind label/stat tiers are now implemented in
  `RadarLabelModel.Spec` and consumed by both skins; the row stays partial for
  the per-kind gaps above.
- **Radar name labels** — per-kind name tiers native; `VISUAL:` the port block's
  placement vs native's contact-anchored text.
- **Radar range labels** — the clip now uses the surface separation and the
  cache no longer draws a clipped range.
- **Radar hull and shield bars** — *which* contacts get bars is now the native
  `bStats` tier per kind/LOD; `VISUAL:` the bar art/layout differs.
- **Radar ore and miner fill bars** — the `c_ptMiner` + exact-own-side gate is
  in; `BEHAVIOUR GAP:` the asteroid fill still uses the remaining-ore fraction
  because `GetOreSeenBySide/(2·capacity)` (`radarimage.cpp:1051-1062`) is not on
  the wire (PAR-5, out of scope).

## 7. Divergences and open items (deliberate)

1. **Probe/mine lifetime fill** — native `GetTimeFraction()` needs `m_time0`;
   the export carries no creation time, so a locally-stamped value would be
   wrong for every object already in a sector on join. Left absent rather than
   faked (the row records it).
2. **Asteroid ore bar** — still the port's remaining-ore fraction (PAR-5).
3. **Bar art/layout** — kept the current block layout per the brief's
   "keep the current layout"; only *which* contacts get bars changed.
4. **Missile radius** — the client wire has no missile model radius, so the
   missile clip separation subtracts 0 instead of `GetRadius()` (`:626-636`).
5. **Own-ship blip presence** — the port still only adds the own-ship radar
   contact in external cameras (existing divergence, out of scope here); its
   *label tier* now follows native wherever the contact exists.
6. **Treasure usefulness** — the live dev-tech split is still deferred (as in
   HUD-4b), so a tech treasure is classified by the pre-existing rule.
7. **`Minimal` naming** — the port labels the fourth mode "None" exactly like
   `c_szRadarLODs`; the enum stays `RadarMode.Minimal` (`radarimage.h:46`).

Scratch: only `.scratch/p783/` was used (now deleted, except nothing is linked
from the report); build/test artefacts under `TestResults/` are the runner's
normal output.
