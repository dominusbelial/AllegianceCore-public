# P859 — bisect and fix two Live-tier MonoGame regressions (P699 pod autopilot, Modern HUD overlap)

Base: public master at dispatch, `f4d9263d` (`Broadcast snapshot + encode-once on by default …`), in the
`allegiance-pi-p859` worktree. Nothing was committed or pushed; every change is in the working tree.

Both failures reproduced **exactly as reported** on the dispatch base before any change:

| Failure | Reproduced at `f4d9263d` | Bisect result (first bad commit) |
|---|---|---|
| `P699HumanPodHomeLiveTests.Human_eject_autopilot_flies_two_jumps_and_docks_without_oxygen_respawn` (both `timeScale` rows) | 2/2 fail in ~1 s at `P699HumanPodHomeLiveTests.cs:41` (`AutopilotControls`), called from `:142` | **`7734a197` — P797 "native map discovery" (2026-10-23)**; parent `fdd70154` (P795) passes |
| `ModernHudTests.Both_skins_render_Hud1_scenarios_without_overlapping_text_and_modern_draws_all_visible_elements` | 1/1 fail in ~10 s: `flight 1280 1.5 overlap: radar:Enemy fighter with a long pilot name {X:4 Y:344 Width:582 Height:83} / Ripcord in: 9 {X:551 Y:338 Width:179 Height:33}` | **`aec8de11` — P833 "Modern HUD scale from display density" (2026-10-26)**; parent `62a5b50f` (P831) passes |

Both bisects were single-test-per-step with a **full solution build at every step** (driver scripts and raw step
logs: `~/allegiance-evidence/P859/step.sh`, `bisect.sh`, `bisect-p699.log`, `bisect-hud.log`, verdicts
`bisect-p699.txt`, `bisect-hud.txt`).

```
# P699 (good = 1a8be792, the commit that added the test)
PASS 7017b7a6  FAIL ba6f7a66  PASS bbf78fb5  PASS 1a189217  PASS fdd70154  FAIL 887ab4c3  FAIL 0eca1fa2  FAIL 7734a197
# HUD (good = 151dc2d0, the commit that added the test)
PASS 238cc887  PASS 72322030  PASS 102f8940  FAIL 31bff892  PASS f3e5148f  FAIL aec8de11  PASS 62a5b50f
```

Nightly history: `docs/TEST_FLAKES.md:58-70` (P828, 2026-10-25) already records the P699 case as a deterministic
base failure "for whoever owns pod autopilot", and `docs/reports/P804_CLIENT_LEAD_SOLVER.md:160-163` lists it as a
pre-existing failure. Nothing in the ledger covered the HUD case: it is Live-tier
(`[Trait("Tier","Live")]`, `ModernHudTests.cs:62`) and the fast tier never runs it — which is exactly why
`aec8de11` shipped (see §4).

---

## 1. P699 human pod home — what actually broke, and why the product was right

### Mechanism (file:line)

1. The test's client joins an **unscouted** mission: the game config (`P699HumanPodHomeLiveTests.cs:71`) never
   sets `ShowMap`, and there are no scouts. Since P797 the join burst exports an aleph only when the joining
   side has seen it — native `fsmission.cpp:4175-4186` (`if (pwarplink->data()->SeenBySide(pside)) ExportObj(…,
   OT_warp …))`), mirrored in `src/Allegiance.Server/Server.Lobby.cs:4252-4258`. The client therefore holds only
   its start sector's aleph pair: `ClientWorld._warps` is filled exclusively from those exports
   (`src/Allegiance.ClientCore/State/ClientWorld.cs:3219`). Measured in the failing run: warp ids 1053
   (cluster 1051 → partner 1054 in 1052) and 1054 (cluster 1052 → partner 1053 in 1051), **nothing else**.
2. `Game1.TryGetAutopilotControls` (`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:3045`) resolves the
   target's sector (`:3072`, home station in 1053 ≠ own 1051), so it needs a route: `AllWarps`
   (`:3616`) turns each known aleph into an edge only when its **partner** record is present
   (`WarpDestinationResolver.DestinationSector`, `src/Allegiance.Core/Model/WarpDestinationResolver.cs:33-40`),
   giving two edges `1051→1052` and `1052→1051`.
3. `TryResolveAutopilotGoal` (`src/Allegiance.ClientCore.Game/Game1.Targeting.cs:772-795`) → BFS over those two
   edges → no path to 1053 → `false` at `Game1.FlightInput.cs:3088-3091`, i.e. the autopilot **refuses to
   engage on the first frame** (~0.7 s, exactly as reported), long before any steering or timeout.

### Native says the same thing

* `GotoPlan::SetControls` routes a cross-cluster goal through
  `FindPath(m_pship, pclusterTarget, bCoward)` (`Igc/common.cpp:1905-1906`). When it returns NULL the autopilot
  keeps the target but clears the warp waypoint and returns "not flying"
  (`Igc/common.cpp:1918-1929`, comment: *"Do not know of a warp to the intended target"*).
* `FindPath` (`Igc/common.cpp:894-1010`) seeds from the current cluster's warps and expands each new cluster's
  warps **only through alephs the ship can see**: `if (pship->CanSee(pwarp))` at `:920` (seed) and `:975`
  (expansion). `CanSee` = same side ‖ `SeenBySide(side)` ‖ in scanner range (`Igc/shipIGC.h:380-390`) — for a
  side-less warp that is *discovered*.
* The eject rescue that this test models is the same shape: `FindTarget(pShip, c_ttStation | c_ttFriendly |
  c_ttAnyCluster | …, c_sabmRescue…)` then `SetCommand(…, c_cidGoto); SetAutoPilot(true)`
  (`clintlib/appmsg.cpp:231-238`; port row SIM-0414), and `FindTarget`'s adjacent-cluster walk is likewise
  `CanSee`-gated (`Igc/common.cpp:682-690`).
* The server-side discovery rule is native, not a port invention: `FedSrv/fsmission.cpp:4181`.

**So the product behaviour is native-correct; the fixture was wrong.** P797 (the first bad commit) correctly
introduced native per-side discovery and left this fixture unscouted. The sibling cross-sector pod fixture hit
the identical wall and was already corrected the same way on 2026-10-25 (`7cf119e1` — *"P540b test: seed the
known map (ShowMap) … this fixture has no scouts"*); P699's human fixture was simply missed.

### Fix (test-only, justified against native)

`tests/Allegiance.ClientCore.MonoGame.Tests/P699HumanPodHomeLiveTests.cs:71-83` — the game config now sets
`ShowMap = true` (native `MissionParams::bShowMap`, `Igc/igc.h:1123`) with a comment naming the native
`FindPath`/`CanSee` rule, so the two-jump route is genuinely discovered and the test measures what it claims to
measure: the flight, the docking and the oxygen budget — not map discovery.

### Fail-first proof

* Before the fix (dispatch base `f4d9263d`): 2/2 rows fail at `AutopilotControls` — same stack, same 0.7 s.
* Bisect "good" endpoint `1a8be792` passes; the first bad commit `7734a197` and every later commit fail.
* After the fix: 2/2 rows pass **3× alone** (34 s each run, below).

### Guard added in the fast tier

New `tests/Allegiance.ClientCore.MonoGame.Tests/P859RegressionProofTests.cs` pins the rule itself (fast, no
device, no artwork) by driving the production `Game1.AllWarps` + `Game1.TryResolveAutopilotGoal`:

* `Autopilot_refuses_a_route_through_undiscovered_alephs` — only the start sector's pair is known (the P797
  symptom) → the route home is unreachable, `TryResolveAutopilotGoal` returns false.
* `Autopilot_routes_the_two_jump_route_when_the_alephs_are_discovered` — the discovered table routes and the goal
  is the **start sector's** mouth, never the far sector's coordinates (the P540 "fly at a foreign sector" bug).
* `Warp_without_its_partner_is_not_a_route_edge` — a dangling export is skipped, never guessed (P540c).

`Game1.AllWarps` was widened from `private` to `internal static` (same file, doc comment says why) so the fast
tier can call it; no behaviour change. Its doc comment now states the `CanSee` equivalence with
`Igc/common.cpp:920/975`.

---

## 2. Modern HUD overlap — what actually broke and how it is fixed

### Mechanism (file:line)

1. `aec8de11` (P833) replaced the Modern skin's text scale
   `Math.Clamp(viewport.Height / 1080f * Size, .5f, 4)` with the display-density rule
   `ModernHudScale.Core(displayScale, Size)` = *desktop scale × Size* (`ModernHudScale.cs:42-47`), and widened
   the user Size from 0.75–1.5 to **0.5–2.0** (`HudSettings.cs:114`).
2. The HUD-2 proof sweeps `1280×720 × Size 1.5`. Pre-P833 that produced scale **1.0** (720/1080 × 1.5); after
   P833 it produces **1.5** on any window. At 1.5 the radar label block for "Enemy fighter with a long pilot
   name" measures **582×83 px** (`HudRenderer.DrawRadarLabelBlock`, block width/height at
   `HudRenderer.cs:916-931`), while the Modern reservations — six 112×scale gauge zones
   (`ModernHudRenderer.cs:100-105`), the target pane (`:106`), the minimap (`:94`), the console anchors and the
   centred ripcord box `(527,337,225,71)` (`HudRenderer.cs:711-732`, pre-fix `:712-721`) — cover the whole 1280×720 viewport.
3. `HudTextLayout.PlaceBlock` (`HudTextLayout.cs:58-96`) tries 7 columns × 7 rows, finds nothing free, and falls
   through to `Overflow` (`HudTextLayout.cs:106-112`), which re-uses the desired anchor — at `(4,344)`, i.e.
   **on top of** the drawn `Ripcord in: 9` box `(551,338,179,33)`. The proof's no-overlap `Require`
   (`tools/hud2-proof/Program.cs:73-81`) then aborts with the reported message. A throw-away instrumented build of
   `HudTextLayout.PlaceBlock` (every tried column/row printed with the reserve it hit; reverted, not shipped)
   showed **every** candidate hit a reserved instrument zone, so this is a genuine "the HUD does not fit" case,
   not a search-order quirk.

### Native intent

Native draws **fixed-pixel bitmap fonts** (`Artwork/dialog.mdl` `smVerdana`/`lgBoldVerdana`; the ripcord
countdown is a `StringImage(JustifyCenter, 150, lgBoldVerdana, softText, …)` in a `SideCenter` pane at
`Point(-75,15)`, `dialog.mdl:5145-5151`, `:5429`). 720p text is exactly as large as 1080p text and never grows
with a user text-size setting — so P833's density model (scale with the *desktop* scale, not the window) is a
port-only quality rule, and the missing constraint is that a *small* window cannot hold the scale the density
asks for. Colliding text is not native-fatal (native never de-overlaps, `radarimage.cpp:1284-1311`), but the
port's HUD-2 contract deliberately de-overlaps, so it must not be handed an impossible layout.

### Fix (product)

`src/Allegiance.ClientCore.Game/Rendering/ModernHudScale.cs:48-83`:

* `MaxAuthoredScale = 1.5` — the ratio every HUD proof was authored against (1.5 scale units per 1080 px of
  window height).
* `WindowFactor(height) = clamp(height/1080 × 1.5, MinScale, MaxScale)` — the largest scale that window can hold.
* `Core(displayScale, size, viewportHeight) = clamp(min(Core(density, size), WindowFactor(height)))` — the density
  rule of P833, one-sidedly **capped** by what the window can hold.

`ModernHudRenderer.cs:92` passes the viewport height, so every Modern element (text, gauge ring, target pane,
own-status grid) keeps deriving from the one scale. Effects:

* 1280×720 / Size 1.5 → **1.0** (the pre-P833 value the proof passed with) — the reported failure is gone.
* 1080p / 1.0 desktop → exactly **1.0**; 1.25 desktop → **1.25** (P833 preserved); 2160-tall window → **1.0**,
  not the old 2.0 (P833's whole point preserved).
* Size 2.0 on a 1080p window → capped at 1.5, i.e. the pre-P833 ceiling: the enlarged range still works where
  the window has room (Size 2.0 at 2160 is honoured).
* Classic is untouched (it draws native fixed pixels, `HudRenderer.TextScale == 1`).

Also fixed (same defect class, found by the wider sweep, Classic **and** Modern): the centred ripcord countdown's
reserve covered only the native 150-px box, while the port's `lgBoldVerdana` glyph run is wider and is centred on
the same point — so a free-zone placement could land on the glyphs. At 1600×900 the ordinary tip
`Welcome to the sector` was placed at `(923,422,231,22)` onto the Classic glyph box `(644,435,312,47)`. The
reserve is now the **union** of the native box and the measured glyph run (`HudRenderer.cs:711-733`). No drawn
position changed, so the HUD-11 anchor assertions still hold.

The proof harness also measures the inventory columns at the renderer's real scale now
(`tools/hud2-proof/Program.cs:113-118`, `ModernHudScale.Core(1f, Size, height)` instead of the pre-P833
`height/1080 × Size` heuristic), and the sweep keeps **1600×900** and **1366×768** in addition to
1920×1080 / 2560×1440 / 1280×720 — the untested band that hid the ripcord defect.

### Fail-first proof

The existing HUD-2 proof *is* the fail-first artefact: it fails on the dispatch base with the exact reported
message and geometry, and passes after the fix (3× alone, 480 rendered frames per run). The new fast facts
`P859RegressionProofTests` pin the rule so the cap cannot be silently removed or raised:

* `Modern_scale_is_capped_to_the_window_height_budget` — 1280×720: Size 1.5 → 1.0, Size 2.0 → 1.0, Size 1.0 → 1.0.
* `Modern_scale_keeps_P833_behaviour_where_the_window_has_room` — 1080/1440/2160 keep 1.0, 1.25, 1.5; 2160 ×
  density 1.25 × Size 2 = 2.5.
* `Both_skins_stay_within_the_authored_text_budget` — over 4 heights × 5 sizes × 3 densities: the cap only ever
  lowers, never raises, the density scale; 1080p/1.0/Size 1 is exactly 1.0 (Classic's fixed 1.0 is the other
  bound).

---

## 3. Screenshots (Classic **and** Modern, real GL render, offscreen 1280×720, Size 1.5)

Captured with `tools/hud2-proof` (the same production `HudRenderer`/`ModernHudRenderer` the client uses, real
fonts, real artwork). I opened every image with the read tool before describing it.

| File | What is visible |
|---|---|
| `P859_assets/P859_1280x720_size1.5_flight_Modern_before.png` | P833's density-only scale: the 1.5× Modern HUD overflows a 720p window. "Ripcord in: 9" sits **on top of** the radar label "A2 Enemy fighter with a long pilot nam…"; the inventory rows overflow their pane ("Fig Adv / Gatli… / Plas… / 5SRM Du…" jammed on top of each other); "SPEED/ms" is drawn over the velocity needle, "VECTOR LOCK" over the top banner, "MOUSE DZ 10% QUAD" over the weapons line, "Current …/Alystra/Turret of: Pilot Alpha" piled up in the top right. |
| `P859_assets/P859_1280x720_size1.5_flight_Modern_after.png` | Same window/size after the fix: the whole Modern HUD is back at the 1.0 layout — readable left-aligned `Current orders:`, the centred `Ripcord in: 9` **clear of** the dimmed `A2 Enemy fighter with a long pilot name / 1250 m` radar label, the inventory pane legible inside its own panel, gauges labelled AMMO/ENERGY/ARMOR/SHIELD/THRUST/FUEL, and — as the harness asserts pair by pair — no measured-text collision anywhere in the frame. |
| `P859_assets/P859_1280x720_size1.5_radarAll_Modern_before.png` | The `radar-All` scenario, before: the same crowding with the radar labels and the ripcord countdown overlapping. |
| `P859_assets/P859_1280x720_size1.5_radarAll_Modern_after.png` | The `radar-All` scenario, after: separated text blocks, the radar contacts' labels on the left, the ripcord countdown free at the centre. |
| `P859_assets/P859_1280x720_size1.5_radarAll_Classic.png` | Classic at the same window/size: native fixed-pixel text, round radar ring, the large light-blue `Ripcord in: 9` at the centre over the gauge ring artwork (native geometry) with the contact labels around it — and, as required, **byte-identical before and after the fix** (`md5 30bbca2b…` for `flight`, `a33ee682…` for `radar-All`), i.e. Classic is untouched. |

Media note (lane rule): these five PNGs are **new media** and need Domingo's review before the push
(1.3 MB total, all under `docs/reports/P859_assets/`).

---

## 4. Native-intent classification

| Change | Classification | Native anchor |
|---|---|---|
| P699 fixture now sets `ShowMap` | **Test was wrong, product is native** | `Igc/common.cpp:894-1010` (`FindPath` + `CanSee` at `:920`, `:975`), `Igc/shipIGC.h:380-390`, `Igc/common.cpp:1905-1929` (refuse), `FedSrv/fsmission.cpp:4181` (export only seen alephs), `clintlib/appmsg.cpp:231-238` (eject rescue) |
| `AllWarps` doc + `internal` | **No behaviour change**, documentation of the `CanSee` equivalence | `Igc/common.cpp:920/975` |
| Modern scale capped by the window budget | **Port-only visual rule** (native has no HUD scaling; native text is fixed-pixel) — the cap only bounds a port invention so the port's own no-overlap contract holds; Classic stays native | `Artwork/dialog.mdl:5145-5151`, `:5429`; `WinTrek/radarimage.cpp:1284-1311` (native never de-overlaps) |
| Ripcord reserve = union(native box, glyph run) | **Port-only layout bookkeeping** (native `StringImage` box is 150 px; the port's substitute font is wider) — no drawn position changes | `Artwork/dialog.mdl:5145-5151`, `:5429` |
| Proof sweeps 1600×900 and 1366×768 | **Proof strengthening** (no native rule involved) | — |

No `docs/parity/*.csv` row was added or edited: the HUD changes are port-only visual/layout rules with no native
rule to score, and the P699 change is a test fixture (P797's discovery rows already exist). Other lanes' rows
were not touched (`git diff --stat` shows no CSV).

## 5. Modern / better approach note

* **Routing over a discovery-gapped warp table.** The port's cross-cluster router is fed *positional* aleph
  records, so an undiscovered aleph simply is not there — which is a faithful stand-in for native's
  `CanSee` filter, and it worked without touching the router. The one structural gap left is that the port
  cannot *plan* beyond the discovered frontier the way native's CLUSTERINFO graph allows for map preview; bots and
  the starmap pane already read `ClientWorld.Graph` (the full `CLUSTERINFO` edges, `ClusterGraph.cs:19-24`,
  used at `Game1.Panes.cs:1193`), so a future improvement is to BFS the full graph for the *hop sequence* and
  then require the first hop's aleph to be discovered — same native outcome, better UX ("route known, aleph not
  yet found"). That is a behaviour change and was deliberately **not** made here.
* **HUD scaling.** A single density-derived scale (P833) is the right direction; the durable lesson is that any
  scale rule must be expressed as a *ratio to the window* (as `WindowFactor` now does) so proof sweeps stay
  meaningful across resolutions, instead of being recomputed per call site. The next robustness step would be a
  scale-relative layout unit for the reserved zones (they are still authored in absolute pixels per
  `*AtReference` constants), which would let Size 2.0 work on any window. Out of scope here.

## 6. Latent findings (not fixed, out of scope — for the backlog)

Sweeping extra viewports while designing the fix surfaced two **pre-existing** collisions that are unrelated to
the two regressions and are not covered by the shipped sweep:

1. **1024×600 / any Size** — `Current location:` (fixed anchor, top right) overlaps `AFTERBURNER ACTIVE`
   (`fixed 1024 0.5 overlap: Current location: {X:830 Y:37 W:187 H:22} / AFTERBURNER ACTIVE {X:734 Y:20 W:198 H:22}`).
   The top-right anchor reserve at that narrow width lands under the warning band. Evidence:
   `~/allegiance-evidence/P859/hud-stress-1024x600.txt`.
2. **Size 2.0 at 1080p** has no sweep coverage: the shipped sweep tests sizes 0.75/1/1.5, so the cap now
   *bounds* 2.0 (to 1.5 at 1080p) without proving that bound collision-free. Adding 1.25/2.0 to the sweep is the
   cheap follow-up.

Neither is reachable from the two P859 regressions: the proof's own viewports/sizes pass in both skins, and the
1024×600 case needs the console-anchor reservation follow-up above.

---

## 7. Verification (light gate)

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | **all 91 tool projects built**, exit 0 |
| New fast tests `P859RegressionProofTests` **3× alone** | 6/6 passed, 6/6, 6/6 |
| `P699HumanPodHomeLiveTests` **3× alone** | 2/2 passed, 2/2, 2/2 (34 s each) |
| `ModernHudTests.Both_skins…` **3× alone** (final tree) | 1/1 passed each (11 s / 9 s / 9 s), 480 frames per run |
| `P833HudScaleTests` + all `ModernHudTests` (regression check for P833) | 13/13 passed |
| `tools/run-tests.sh --tier fast` (ONE) | **11 043 tests, 309.7 s**, one failure: `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` (expected 20 sockets/threads, read 19 at `RetryCleanupTests.cs:263`) — **3/3 pass alone** (53/40/43 ms), the documented reconnect load-flake family, ledgered in `docs/TEST_FLAKES.md`; no socket/thread path is touched by P859. 3 `NotExecuted` are the existing private-pack skips. Log: `~/allegiance-evidence/P859/fast-tier.log` |
| MonoGame **Live** tier (once) | **11/11 passed, 15 m 46 s** (`~/allegiance-evidence/P859/monogame-live.log`, `trx/monogame-live.trx`) — includes both target tests with the fix |
| AOT publish (`Allegiance.Server`, linux-x64) | not run — **no `src/Allegiance.Server` or `src/Allegiance.Core` change** (only `ClientCore.Game`, tests, `tools/hud2-proof`) |
| `scripts/parity-scorecard.py` | not required (no CSV touched); validators unaffected |

Raw logs / TRX / bisect traces: `~/allegiance-evidence/P859/` (`step.sh`, `bisect.sh`, `bisect-p699.log`,
`bisect-hud.log`, `bisect-p699.txt`, `bisect-hud.txt`, `monogame-live.log`, `trx/monogame-live.trx`,
`trx/fast/`, `fast-tier.log`, `hud-stress-1024x600.txt`). `docs/reports/P859_assets/` holds the five PNGs
(1.3 MB) plus `validation-summary.json` (the machine-readable form of this table).

### Flakes / red seen during the gate

* `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` — one fast-tier failure
  (socket/thread count 19 vs 20), 3/3 alone → load flake, now ledgered in `docs/TEST_FLAKES.md`
  (2026-10-27 row). Three lanes were running tiers on the host at the time.
* Nothing else red: the MonoGame Live tier (11 tests, including both targets) passed 11/11, and the 480-frame
  HUD proof passed 3/3 alone.

## 8. Files changed

| File | Change |
|---|---|
| `tests/Allegiance.ClientCore.MonoGame.Tests/P699HumanPodHomeLiveTests.cs` | fixture: `ShowMap = true` + the native-rule comment (P699 regression) |
| `tests/Allegiance.ClientCore.MonoGame.Tests/P859RegressionProofTests.cs` | **new** fast-tier proofs: discovery-gated autopilot routing (3 facts) + the Modern text budget (3 facts) |
| `src/Allegiance.ClientCore.Game/Rendering/ModernHudScale.cs` | `MaxAuthoredScale`, `WindowFactor(height)`, `Core(density, size, height)` |
| `src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.cs` | `PrepareLayout` uses the capped scale |
| `src/Allegiance.ClientCore.Game/Game1.Lifecycle.cs` | boot log line describes the density scale + the per-frame cap |
| `src/Allegiance.ClientCore.Game/Rendering/HudRenderer.cs` | ripcord reserve = union(native box, measured glyph run) |
| `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` | `AllWarps` `internal` + the `CanSee`/discovery doc |
| `tools/hud2-proof/Program.cs` | inventory measurement at the renderer's real scale; sweep +1600×900, +1366×768 |
| `GLOSSARY.md` | new entries: Modern HUD scale/text budget, HUD text layout, autopilot routing, lifepod home |
| `docs/TEST_FLAKES.md` | P699 entry marked resolved (P859); 2026-10-27 reconnect load-flake row |
| `docs/reports/P859_LIVE_MONOGAME_REGRESSIONS.md` + `docs/reports/P859_assets/` | this report, five PNGs (need Domingo's review), `validation-summary.json` |
