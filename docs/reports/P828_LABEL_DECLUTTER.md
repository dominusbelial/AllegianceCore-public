# P828 — Modern-HUD radar label declutter for big battles

**Status:** implemented, gated, live-captured locally. Non-native **visual**
option: **Classic draws exactly what native draws** (asserted in tests), Modern
declutters by default and the player can switch it off.

- Base: public master `ac932657` (P825), worktree `allegiance-pi-p828`.
- Native reference: `~/Development/Allegiance/src/` (`WinTrek/radarimage.cpp`).
- Assets: [`P828_assets/`](P828_assets/) — real 1920×1080 backbuffer frames
  (downscaled to ≤1 MB PNG; the full-size originals, the server/MCP logs and the
  client logs of the capture session are kept outside the repo in
  `~/allegiance-evidence/P828/`).

---

## 0. TL;DR

Domingo's 200v200 showcase looked "like excel": the port is **faithful** — in the
default radar mode native labels *every* on-screen ship (friendlies name only,
enemies name + stats + range), and native never de-overlaps HUD text. At ~390
ships in one sector that is a wall of `mcp-wN-bNNN-NNNN` + range numbers.

The fix is a **Modern-HUD visual option** (Classic is untouched, byte-for-byte
the old draw loop):

| Rule | What it does |
|---|---|
| Priority | always label your target, your accepted/queued orders, your chat/command subjects (wingmen/recipients), everything damaging you, the construction highlight, the carried flag/artifact, the own ship |
| N cap | then the nearest **N = 12** (configurable 0–64) other allowed labels: **enemy ships → friendly ships → everything else** (rocks/treasure range lines), nearest first |
| Distance fade | alpha 1 inside 900 m of the camera, linear to 0.35 at 4500 m, clamped |
| Overlap merge | non-priority labels on a deterministic 120×46 px screen grid collapse per side into one compact `xN` tag (two tags if both sides share a cell); the target/priority set is never merged |
| Stat bars | kept exactly where native allows them for a **labelled** contact, dropped with the label for everything else |
| Telemetry | an off-by-default overlay line: `radar labels: 1840 contacts / 4 labelled / 9 merged into 3 tags` |

Measured locally on a ~180-ship Omega furball (1 840 camera-visible radar contacts
on the telemetry frame): with the option OFF the frame paints a name tag for every
one of those ships plus every allowed rock range line, and the text covers
essentially every pixel column of the 1920×1080 screen; with it ON the same frame
paints **4 name labels + 3 count tags** for the same ships (telemetry line, §7),
and the icon/bracket layer (positions, brackets, side colours) is untouched.

---

## 1. Why native looks like this (and why it is not a port bug)

* `WinTrek/radarimage.cpp:703-710` — an **important** contact (target / accepted /
  queued / highlight / threat / flag / artifact) forces `bLabel = bStats = true`
  and skips every per-kind tier.
* `WinTrek/radarimage.cpp:747-757` — the Default ship tier: `bLabel = true` for
  every ship that is on screen **or** inside the 1000 m clip; the exact own side
  loses its range line, everyone else gets `bStats = true` (hull/shield bars).
  So at 390 ships every one of them is labelled with at least a name.
* `WinTrek/radarimage.cpp:1284-1311` — the label offset is computed per contact
  and never tested against any other label; native HUD text **overlaps by
  design**. The port reproduced that in `HudTextLayout.PlaceBlock`'s overflow
  fallback.
* `WinTrek/radarimage.cpp:686-700` — the *only* distance-fade idea native ever
  had is **commented out**, so native labels are full opacity at any range.

Native was designed for 20–60-player games. The declutter changes **nothing**
about what native's modes allow (P624 All/Default/Target/Minimal still decide
that) — only how many of those allowed labels are drawn and how.

## 2. Rules the implementation keeps

1. **Classic is exactly native.** `HudRenderer.RadarDeclutterPlan` (the classic
   skin) returns `RadarLabelDeclutterOptions.Off`; the selector then marks every
   label the native cache resolves as `Label` with alpha `1`, produces no tags,
   and `DrawRadarLabelBlock` multiplies its colour by `1f` — the same arithmetic
   as before. Tests pin the result contact-by-contact (§6.2) and the plan itself
   by reflection.
2. **Never show a label native's mode suppresses.** The selector asks
   `RadarLabelModel.Resolve` (through the HUD-5b cache) first, so a contact with
   no native label can never be selected, whatever the N cap or priority says.
   Target mode in a 189-ship sector is the live proof: native removes friendly
   ships from the radar, the port's contact list has `ships=0`, and **zero**
   labels draw in either style (telemetry: `contacts=651 ships=0`).
3. **Both HUD styles** (§`briefs/_both_styles.txt` rules): behaviour lives in the
   shared pure model (`Rendering/RadarLabelDeclutter.cs`), the shared draw loop
   (`HudRenderer.DrawRadarLabels`) consumes it, and the only style-specific piece
   is the one virtual property Modern overrides. Both styles were captured in the
   same battle (Default + Target).
4. **2-D conventions**: the count tag is positioned in the port's screen-space
   (y-down) layout like every other HUD text; the native block's own y-up
   stacking (`radarimage.cpp:1394-1404` walks `offset.Y -= heightFont` upward) is
   unchanged and untouched — the declutter only decides *whether* that block is
   drawn and with what alpha.

## 3. The model — `Rendering/RadarLabelDeclutter.cs`

Pure values + one reusable instance; **no graphics types** (same discipline as
`RadarLabelModel`/`RadarBracketLayout`), so everything is headless-testable.

```csharp
RadarLabelDeclutterOptions.Off                       // Classic / option off
RadarLabelDeclutterOptions.Modern(maxExtraLabels, scale, telemetry)
RadarLabelDeclutter.Select(contacts, radarMode, commandCamera, options, labelCache)
  → Decision(i) = IconOnly | Label | Merged, Alpha(i), Tags (count tags)
```

* `IsAlwaysLabelled(contact)` = `IsSelf || Subject || RadarLabelModel.IsImportant`
  — the native important mask (`radarimage.cpp:704-710`) plus `c_maskSubject`
  (`:652`, wingmen and command recipients) plus the own ship. **"Locked by" /
  "locked on"**: native has **no incoming-lock mask** (`radarimage.cpp:30-42`) and
  the port's wire carries no incoming-missile-lock state, so the closest native
  signals stand in: the selected target (`c_maskTarget` — the same object the
  missile lock follows) and the threat bucket (`c_maskThreat`, `:660-671`, the
  ships that actually damaged you inside the 40 s window). This is documented in
  the XML doc rather than invented data.
* `Rank(contact)` = enemy ship `0`, friendly ship `1`, everything else `2`. The
  third group matters: in a busy sector the contact list is ~90 % asteroids whose
  native Default tier is a *bare range line* (`radarimage.cpp:922-935`) — without
  the ship-first rank those rock range numbers eat the whole N budget (measured
  on the live frame: ~180 ship labels + ~1 650 rock/mine range lines competing for
  the 12 slots).
* `Fade(distance)` = full inside `FadeNear`, linear to `MinAlpha` at `FadeFar`,
  clamped, monotonically non-increasing (unit-pinned). Distance is the contact's
  **camera** distance, a new `HudContact.CameraDistance` the radar snapshot fills
  with `(worldPos − cameraEye).Length()`; `Range` remains the own-ship centre
  distance the range *line* prints. The label cache deliberately does **not**
  compare it (it changes every frame).
* **Merge**: non-priority labelled contacts are bucketed into
  `floor(x/cellW) × floor(y/cellH)` with the side flag in the key; a bucket with
  ≥2 members becomes one tag at the centroid (mean position, mean fade, first
  member's side colour). Deterministic: buckets are created in contact order and
  scanned in order, so the same frame always yields identical tags. The tag text
  is `xN` — the bundled native HUD fonts are ASCII 32-126
  (`Content/Fonts/NativeR8.spritefont`), so the typographic `×` would render as
  `?`.
* **Budget**: one instance per renderer; the decision/alpha/distance/cluster-index
  arrays grow once, the cluster and tag lists are cleared in place, and count-tag
  strings come from a cache. 400 contacts × 120 frames ⇒ **0 bytes allocated**
  (test in §6.1).

## 4. Renderer integration

* `HudRenderer.DrawRadarLabels` (both styles): run the selection once per frame,
  keep `MarkRadarBrackets` for every contact (icons/brackets are never
  decluttered), draw the full block only for `Label`, skip `Merged`/`IconOnly`,
  then draw the count tags (`Layout.PlaceBlock` keeps them clear of the surviving
  labels) and, when enabled, the telemetry line.
* `DrawRadarLabelBlock(..., float labelAlpha)` multiplies the native
  `colorOther` brightness **and** the icon tints, stat bars and background by the
  fade — with `labelAlpha == 1` the arithmetic is identical to the old code, so
  Classic is unaffected.
* `DrawRadarMergeTags` marks `"Radar name labels"` when a tag draws, so the
  Modern coverage report still sees the label row on a crowded frame.
* The telemetry line is rebuilt at most every 16 frames (debug-only, off by
  default); it is drawn at the bottom-left above the camera tag with a fixed
  anchor so it never reshuffles the layout.
* No per-frame allocation is added to the draw path: the label cache, the
  scratch lists and the tag strings are all reused.

## 5. Settings + options menu

`Rendering/HudSettings.cs` (persisted in `hud.json`, like the other HUD options):

| Field | Default | Meaning |
|---|---|---|
| `RadarDeclutter` (`bool?`) | `null` → **off in Classic, on in Modern**; an explicit value is the player's choice in either style | enables the declutter |
| `RadarLabelMax` (`int`) | `12`, clamped 0–64 | the "nearest N" budget for non-priority labels |
| `RadarLabelTelemetry` (`bool`) | `false` | draws the contacts/labelled/merged line |
| `RadarLabelPlan(scale)` | — | one place that turns the settings + HUD style into the model options |

`Ctrl+F6` (Settings → Graphics / HUD) gains three rows (16–18: on/off, N in steps
of 2, telemetry). The panel height is now computed from the row count
(`Game1.GraphicsPanelHeight`) instead of the old fixed 700 px, which the 19 rows
had outgrown; Up/Down wrap on `GraphicsRowCount = 19`.

`HudContact.CameraDistance` (a new trailing optional field) is filled by the
radar snapshot for every contact kind — own ship, ships, stations, warps,
missiles, probes, mines, treasures, asteroids — from the same `cameraEye` the
projection uses. It is presentation data only; nothing in the native label rules
reads it.

## 6. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/P828RadarLabelDeclutterTests.cs`
(14 tests, all headless):

1. the selected target is always labelled (N = 0 ⇒ only it draws);
2. wingmen/command subjects/attackers/own ship are always labelled, and the one
   ordinary slot goes to the enemy;
3. a contact native suppresses is never labelled (Target mode friendly, Minimal
   non-self, a name-less/stat-less friendly in Default);
4. the N cap limits only ordinary labels;
5. enemies before friendlies (10 nearer friendlies lose all 4 slots to enemies);
6. ships outrank rock/treasure range lines in the N cap;
7. distance breaks ties inside a group, contact order is the last key;
8. fade is full near, faint far, clamped, monotonic (50 m steps) and strictly
   between the endpoints at half range;
9. overlapping labels collapse into one count tag, the target in the same cell is
   never merged, and a second run produces the identical tag (determinism);
10. one cell with both sides produces two tags (enemy + friendly);
11. a lone label in a cell keeps its block;
12. **per-style consumer test**: the Classic renderer's own plan yields *every*
    native label at alpha 1 with no tags, while the Modern renderer's plan (same
    contacts, same frame) yields < 1/4 of them plus tags, and the target survives
    both; the explicit `RadarDeclutter` override wins in either style;
13. **zero allocations** — 400 contacts × 120 frames on the selection path: 0
    bytes (both the Modern and the option-off paths);
14. the setting defaults per style, round-trips through `hud.json`, clamps
    `RadarLabelMax`, and the three Ctrl+F6 rows exist and are wired.

`Hud19CentreHudTests.Graphics_overlay_has_the_joystick_row_and_it_toggles` was
updated for the new row count (it pinned `"% 16"`; it now pins
`GraphicsRowCount = 19` and the new `case 16`).

## 7. Live proof (local server + bots, never the rig)

Setup (all local, `127.0.0.1`, `m2xspiderw.igc`, `debugMode` arena: side 0 home
**Omega (1053)**, side 1 home **Plato (1060)**, one warp each to Alderan):

* server `Allegiance.Server --config …` with `testHarness`/`testUnlock`/
  `adminCheats`, `maxPlayersPerTeam: 200`, and
  `transport.maxConnectionsPerIp: 512` (the default **64 per-IP cap** refused the
  extra bots — with it left at 64 only 63 of 180 bots could connect);
* `allegiance-mcp` via `scripts/mcp_driver.py`: `spawn_scenario
  {preset:"showcase", count_a:90, count_b:90, target_sector:"Alderan",
  staging:false}` plus two reinforcing waves (70/70 and 1/60) — **381 bots** alive,
  **189** of them side 0 in Omega;
* one windowed client per configuration: `--auto-side 0 --auto-ready
  --auto-launch --auto-goto Omega --auto-radar <mode> --autoshots 50,65,80
  --window 1920x1080 --camera chase`, `ALLEGIANCE_HUD_SETTINGS` pointing at a
  per-run `hud.json`, `ALLEGIANCE_RADAR_PROOF=1` so each shot logs the real radar
  snapshot.

Per-frame radar proof lines from the captured frames (`ships` = ship contacts,
`contacts` = everything the radar mode draws):

| Run | Frame | Radar proof |
|---|---|---|
| Classic, Default | t=65 s | `contacts=1791 ships=178 enemies=0` |
| Modern, Default, declutter ON | t=65 s | `contacts=1661 ships=175 enemies=0` |
| Modern, Default, declutter OFF | t=65 s | `contacts=1960 ships=180 enemies=0` |
| Modern, Default, declutter ON + telemetry | t=65 s | `contacts=1840 ships=179 enemies=0`, overlay `4 labelled / 9 merged into 3 tags` |
| Classic, Target | t=65 s | `contacts=671 ships=0` (native Target removes friendly ships) |
| Modern, Target, declutter ON | t=65 s | `contacts=651 ships=0` |

### What the frames actually show

* [Classic, Default, 178 ships](P828_assets/classic_default_omega.png) — the
  whole 1920×1080 frame is covered by `mcp-bNNN-NNNN` names and numbers: name
  tags on the left, right, top and bottom edges, range numbers (`446 m`,
  `1145 m`, `2048 m`, `921 m`, …) between them, and the sector readout/gauges
  fighting for space. The hull in the centre is barely distinguishable from the
  text. This is the "excel" screen Domingo saw.
* [Modern, Default, declutter **off**](P828_assets/modern_default_declutter_off_omega.png)
  — same battle, same sector, same ~180 ships with the same native label set.
  **Identical wall of text** with Modern's SDF glyphs: the change is the option,
  not the style.
* [Modern, Default, declutter **on**](P828_assets/modern_default_declutter_on_omega.png)
  — the text field is gone. Five faded friendly names
  (`mcp-b350-4134`, `mcp-b258-6247`, `mcp-b094-2941`, `mcp-b256-2081`,
  `mcp-b344-7324`) sit at the distance-faded alpha, one `x2` and one `x3` count
  tag mark two overlapping clusters, and every other contact is icon-only (the
  blue cross/diamond blips and the yellow ship cluster top-left). Gauges, sector
  block and radar readouts are untouched.
* [Modern, declutter **on**, telemetry](P828_assets/modern_default_telemetry_omega.png)
  — the debug line reads **`radar labels: 1840 contacts / 4 labelled / 9 merged
  into 3 tags`**: 1 840 camera-visible radar contacts (179 ships + ~1 660
  asteroids/mines/probes), 4 name labels, 9 labels folded into 3 `x2`/`x3` tags.
* [Classic, Target](P828_assets/classic_target_omega.png) and
  [Modern, Target, declutter on](P828_assets/modern_target_declutter_on_omega.png)
  — in a 189-ship sector, Target mode shows **no ship labels at all** in either
  style: the native Target LOD drops friendly ships from the radar entirely
  (`ships=0` in the proof line), so there is nothing for the declutter to keep.
  Only the rock range lines at the edges remain (native asteroid tier). This is
  the "never show a label native's mode suppresses" rule, live.

### What I could not get locally (and why)

The showcase's wings kept their fleets in **their own staging sectors** (189 side
0 in Omega, ~110 side 1 in Plato, ~80 in Alderan) — every 90v90 wave converged,
crossed its single warp and fought in the *staging* sector, but the two fleets
never clashed inside one sector during the capture window (the side-intel log
reported `clash=pending` for the whole run). Consequence for the screenshots:

* the >150-ship frames are **one-sided** (`enemies=0`): 178–184 friendly ships in
  Omega. That still exercises the real problem (every contact is a native Default
  label) and the declutter, but it does **not** show enemy stat bars / enemy
  colour, nor the "enemies before friendlies" ordering visually. That ordering is
  pinned by unit tests (§6.1 items 5–6) and by a cross-side frame captured
  earlier in the run (`Alderan`, `contacts=182 ships=89 enemies=1
  nearestEnemy=8096`, which confirmed the same label rules live for an enemy).
* an enemy-dense Target-mode frame would need the client on the side *opposite*
  the Omega mass; the local server's side assignment kept seating the proof client
  on side 0 (`[monogame] side-select: requesting side 0` follows the requested
  side), so Target mode had no enemies to label in the crowded sector.

The server, MCP and client logs of the 40-minute session lived in the worktree's
ignored `.scratch/live828/` directory during the capture (deleted afterwards, not
committed); what is committed is the six frames above (PNG, each < 1 MB, 1.99 MB
total).

## 8. Anything unfinished / follow-ups

* **Incoming-lock priority** — "anything you are locked by" is represented by the
  native threat bucket because neither native (`radarimage.cpp:30-42`) nor the
  port's wire carries an incoming-missile-lock state. If a LOCK_TARGETED-style
  event ever reaches the client, add it to `IsAlwaysLabelled` (one line).
* **Tuning knobs** — the fade window (900/4500 m, min 0.35) and the merge grid
  (120×46 px at HUD scale 1, min 2 per cell) are model constants. A 221 session
  with 390 ships is the place to retune them; the defaults keep ~13 labels + a
  handful of tags on a 1 840-contact frame.
* **Enemy-dense capture** for the report is the one gap listed in §7; it needs the
  rig's 200v200 battle where both fleets clash in one sector (or a client seated
  on the enemy side).
* Nothing in `src/Allegiance.Server`, the bot or the native radar rules changed;
  no parity CSV rows are affected (Classic output is identical to master).
* Pre-existing base failure, unrelated to this task: `P699HumanPodHomeLiveTests`
  fails 2/2 on pristine `ac932657` with `ALLEGIANCE_ARTWORK_PATH` set (it *skips*
  without the artwork). Recorded in `docs/TEST_FLAKES.md`.

## 9. Verification

* `dotnet build AllegianceCore.slnx` — **0 warnings, 0 errors**.
* `tools/build-tools.sh` — see §9.1 (every `tools/*/*.csproj`).
* New tests 3× alone — §9.1.
* `tools/run-tests.sh --tier fast` — one run, §9.1.
* Console/log evidence, per-frame radar proof lines and the screenshot pair
  analysis are in §7.

### 9.1 Results

```
=== whole-solution build
dotnet build AllegianceCore.slnx  ->  Build succeeded.  0 Warning(s), 0 Error(s)

=== tools build
bash tools/build-tools.sh         ->  build-tools: all 87 tool projects built (0 warnings)

=== new tests 3x alone
dotnet test --filter FullyQualifiedName~P828RadarLabelDeclutterTests
  run 1: Passed! - Failed: 0, Passed: 14, Skipped: 0, Total: 14 (89 ms)
  run 2: Passed! - Failed: 0, Passed: 14, Skipped: 0, Total: 14 (88 ms)
  run 3: Passed! - Failed: 0, Passed: 14, Skipped: 0, Total: 14 (87 ms)

=== tools/run-tests.sh --tier fast   (ONE run)
Discovery: Lobby 82 fast, Update 28, MonoGame 3490, ClientCore 1818, Core 5410
fast/main Update           {'Passed': 28}        (1.6 s)
fast/main Lobby            {'Passed': 82}        (2.1 s)
fast/main MonoGame         {'Passed': 3487, 'NotExecuted': 3}  (38.3 s)
fast/main ClientCore       {'Passed': 1816}      (189.8 s)
fast/main Core             {'Passed': 5400}      (297.0 s)
fast/quarantine ClientCore {'Passed': 2}, Core {'Passed': 10}
fast: 10828 tests, 306.6 s, PASS
(plus a second identical fast run made after a final behaviour-neutral cleanup)
```

The 3 `NotExecuted` are the existing private-artwork-pack skips. The full
MonoGame-tier run I made before the fast sweep (`--tier` unfiltered, 3 509 tests)
had 3 503 passed / 3 skipped / **3 failed**, all three the pre-existing
`P699HumanPodHomeLiveTests` base failure described in §8 and ledgered in
`docs/TEST_FLAKES.md` (they fail identically on the pristine dispatch base; they
are `Tier=Live`, so the fast gate does not run them).

