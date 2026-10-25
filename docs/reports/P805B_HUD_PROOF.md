# P805b — real-window proof for P805: aleph / probe / treasure target previews and the per-style selection box

Branch `pi/p805b` (worktree of public master `041e23a0`, the P805 merge). No commits — everything is left in the working tree.
Report assets: `docs/reports/P805B_assets/` (11 files, 572 kB; every file < 100 kB). Full-resolution frames, client/server/MCP logs and the
generated server config: `~/allegiance-evidence/P805B/{classic,modern,classic-bracket}/` (outside the repo).

Native reference: `~/Development/Allegiance/src/` (every cite below was re-grepped in this checkout). P805's report
(`docs/reports/P805_HUD_LEFTOVERS.md`) is the row inventory this proof closes §1.1/§1.2 for.

> **What this task found and fixed.** The proof path itself turned up **five real bugs** that P805's headless
> gate tests could not see (they never ran the art resolve or the draw dispatch):
> 1. **A probe target threw** — the resolve left `PreviewArt.Treasure` null while the draw switch read
>    `art.Treasure!.Value` for `TargetPreviewKind.Probe`.
> 2. **The aleph preview was the wrong art** — P805 drew the aleph *texture as a flat camera-facing quad*; native
>    `LoadAleph` builds an additive textured **vortex mesh** (`effect/alephgeo.cpp`), which is what native's target
>    camera shows.
> 3. **Every PART treasure previewed nothing** — the art was resolved from the treasure's *object id* instead of the
>    `DataTreasureIGC.TreasureId` **payload** native uses (`treasureIGC.cpp:118-171`).
> 4. **The Modern selection box never drew** — P805 passed the `SelectionBracketOn` flag only to the *classic*
>    renderer's constructor, so `ModernHudRenderer`'s base gate stayed `false` (the Modern override was dead code).
>
> A fifth, smaller one found while comparing the two styles' probe frames: the probe preview inherited the *treasure*
> tumble (`NativeTreasureRenderer.SpinTransform(targetId, age)`), so the same probe model showed a different random
> silhouette per run/object id. Native gives the tumble to treasures only (`treasureIGC.cpp:180-190`); the pane now
> draws a probe at its authored orientation.

---

## 1. The proof path (extended the existing harness, no new tool)

### 1.1 `scripts/live-proof.sh`

| New option | What it does |
| --- | --- |
| `--mapgen <type>`, `--map-seed <n>`, `--player-treasures <n>` | Generated arena (`mapGen: {type, seed, playerSectorTreasures}`) instead of a map file: P811's seed makes the layout rebuildable and the per-home-sector count spawns real `Mission.PopulateTreasures` treasures in the pilot's own sector, which is the only way a static-arena map ever has one to select. |
| `--hud-settings <file>` | Exported as `ALLEGIANCE_HUD_SETTINGS`, so the run picks the skin (`Style`) and per-style overrides (`SelectionBracket`) from a file the report can quote. |
| `--bots-after <s>` | Issue the MCP spawn command *after* the client is flying (and start the MCP driver only then). Both matter: a bot scenario staged before the client makes the MCP's startup probe take the pilot's side first, and the client is then refused for **TeamBalance** (reproduced in the first run: `[sideselect] refused peer 2 'proof' ship 8099 side 0: TeamBalance`). |

Also fixed while in here (all harness, no behaviour change for ordinary play):
`--art` now really honours `local.env` (the script's own help text promised it but never sourced the file, so every run
without `--art` died on `artPath … does not exist`); the client's per-user config (`XDG_CONFIG_HOME`) is redirected into
the run directory so a proof cannot clobber the operator's pilot profile, and the directory is *created* first (.NET
returns an **empty** `ApplicationData` for a missing `XDG_CONFIG_HOME`, which silently dropped `AllegianceCore/logs` into
the current directory).

### 1.2 Client automation (`LiveProof/AutoProofPlan.cs`, `LiveProof/AutoProofMachine.cs`, `Game1.AutoProof.cs`)

Every addition is a scripted *player* action; nothing new goes on the wire.

| Flag | Action |
| --- | --- |
| `--auto-target-seq "kind@seconds,…"` | Selects a **sequence** of target kinds at fixed after-launch times (`aleph@4,probe@14,treasure@22,enemy@32`). Selections in the sequence **do not engage the autopilot** (an approach to an aleph transits it, and the pane preview needs only same-cluster, not proximity). `--auto-target` keeps its old "select and fly at it" behaviour. |
| `--auto-carry-probe` | Fills the hull's **Dispenser** socket with the cheapest probe launcher the station offers, through the production `ShopModel` navigation (PARTS tab → Dispenser slot → part; native `BuyPartMenu`'s type+`CanMount` list, `load.cpp:1680-1751`). Needed because the debug arena's spawned hull defaults to a **chaff** dispenser, so the N key would drop chaff. |
| `--auto-drop-probe-at <s>` | The N key: `FIRE_EXPENDABLE` with `EquipmentType.Dispenser` (`TK_DropMine`, `trekinput.cpp:1424`) — deploys a real probe (`[monogame] audio: probe deploy` + `world.Probes` export). |
| `--auto-summon-enemy-at <s>`, `--auto-summon-enemy-dist <m>` | Places an **MCP-harness** enemy ahead of the pilot with the port's privileged `relocate` chat cheat (`Server/Server.AdminCheats.cs` — a **port extension**, native has no relocate token). It asks about *every* known enemy, so the server refuses the ownerless NPC miner and accepts the harness bot; the client learns the new sector/pose from the per-cluster `HEAVY_SHIPS_UPDATE` (ClientWorld P375), which is what makes the enemy a normal `--auto-target enemy` candidate. Retried every 4 s until an enemy is observed in-sector, then left alone. |
| `--auto-stop-after <s>` | The Backspace `TK_ThrottleZero` preset (`-1`, `WinTrek.cpp:10939-10960`): the pilot holds station instead of drifting into an aleph during a 38 s scripted run. |
| `--auto-treasure part\|cash\|powerup\|development\|flag` | Restricts a `treasure` selection to one `TreasureCode`. For `part` it prefers the spawned **plain part** treasure whose native model is the most compact, because native normalises every preview by `1/m_radiusMesh` (`thinggeo.cpp:367-381,1185-1193`) and a slim launcher/gun model is technically correct but reads as an empty pane. |
| `--auto-hide-chat-history` | The session-only native `ShowChatHistoryHUD` toggle (`WinTrek.cpp:2897,5074-5078`), so the scripted `relocate` replies do not sit over the capture. The composer still draws. |

Also: a scripted run centres the pointer once after launch (the command-camera path already did), so the cockpit
head-look starts at zero and the preview's view direction is reproducible — native's preview ignores head-look anyway
(`WinTrek.cpp:426` vs `:438`).

`Game1.GatherSectorCandidates` gained optional `probes`/`treasures` sources (in-sector only), so `--auto-target
probe|treasure` uses the same candidate collector as the T key; every existing caller's pool is unchanged (`legacy
pool has no probe/treasure` is pinned by a test).

One `[preview]` log line per target/model change (`Game1.Rendering.ResolvePreviewArtWithLog`) records the kind, the
resolved stem, the chosen draw path, the radius-1 scale and the treasure visual — the evidence trail quoted below.

---

## 2. Bugs found by the proof, and the fixes

### 2.1 Probe preview threw (null visual)

`ResolvePreviewArt(Probe)` returned geometry + texture but no `PreviewArt.Treasure`; the draw switch had
`case TargetPreviewKind.Probe: case TargetPreviewKind.Treasure:` → `RenderTreasureModel(art.Treasure!.Value, …)`.
Selecting any probe threw `InvalidOperationException` inside the frame. **Fix:** the probe resolve now also carries the
`TreasureVisual` the model path consumes (`Part`/model stem/texture stem — the same mesh the probe draws in the world,
`probetypeigc.cpp:28-33`), and the dispatch is total.

### 2.2 Aleph preview was the wrong art (flat quad instead of the vortex)

Native `SetTargetGeo` hands the pane the target's **own `ThingGeo`** (`WinTrek.cpp:8783-8818`); for an aleph that is
`ThingSite::LoadAleph` (`WinTrek/trekigc.cpp:1900-1924`), which creates `ThingGeo::Create(…)` + `CreateAlephGeo(…)` —
the additive textured funnel/vortex of `effect/alephgeo.cpp:80-102,205-232` — textured with `textureName + "bmp"`.
P805 rendered that texture as a **camera-facing quad** through the treasure decal path, which reads as a flat grey disc.
**Fix:** a new `TargetModelPreview.PreviewDrawPath.WarpFunnel`; `TargetModelPreviewRenderer.RenderWarp` draws the port's
native-fidelity funnel mesh (`Rendering/WarpMeshFactory.cs`, the same art the world aleph uses) through the shared
`WarpRenderer`, radius-1 (`effect/thinggeo.cpp:367-381`; `AlephGeo::GetRadius` = 1, `alephgeo.cpp:151-154`).
`WarpRenderer.Draw` gained `advanceClock: false` so the pane reuses the scene's animation clock instead of advancing it
twice per frame.

### 2.3 Part/development treasures previewed nothing (wrong id)

`TreasureVisualResolver.Resolve(code, treasureId, …)` is native `CtreasureIGC::Initialize`'s
`m_buyable = GetPartType(treasureID)` (`treasureIGC.cpp:118-171`) — it needs `DataTreasureIGC.TreasureId` (the *payload*:
part id, cash amount, flag side, team-lobby sentinel). P805 passed the treasure's **object id**, so a `Part` treasure
resolved to a non-existent part → `TreasureVisual.Gem` → no mesh → the native empty viewport. `Cash`/`Powerup` worked
only because their art ignores the id. **Fix:** `TargetPreviewKey` carries `PayloadId` (part of `Matches`) and the
resolve passes `key.PayloadId`; the flag side / artifact sentinel branch is fixed by the same change.

### 2.4 The Modern selection box never drew (dead override)

`ModernHudRenderer` was built without the `selectionBracket` argument, so its base constructor default (`false`) gated
`HudRenderer.Draw` for that skin: the Modern `DrawTargetSelectionBracket` override (corner brackets + HULL/SHLD readout)
existed but was unreachable, and Modern — the style whose default is supposed to *show* the box — showed nothing.
**Fix:** the gate is a live property (`HudRenderer.SelectionBracket`), set from `HudSettings.SelectionBracketOn` before
every draw for both skins, so a settings-panel change also takes effect live. (This is the parity row 418 behaviour
Domingo asked for: off in Classic = native, on in Modern, explicit setting wins.)

### 2.5 A probe previewed at a random tumble

Comparing the two styles' probe frames showed the same probe model with a different silhouette per run. The cause:
P805 routed probes through `NativeTreasureRenderer.DrawModel`, which applies `SpinTransform(objectId, age)` — native
treasures get a random orientation and rotation at creation (`treasureIGC.cpp:180-190`), but a probe does not, so the
probe was tumbled by a hash of its object id. **Fix:** `DrawModel` gained `spin` (default true, so treasures are
unchanged); the preview passes `spin: false` for `TargetPreviewKind.Probe`, so a probe draws at its authored
orientation — deterministic and identical in both styles (see the two `probe` captures).

---

## 3. Captures (real window, real backbuffer, opened and described)

All captures come from the three runs below, 1920×1080, one local `Allegiance.Server`, `PCore001.igc`, map
`SingleRing` seed `805`, 40 home-sector treasures, MCP bot `side 1` `Adv Fighter` behaviour `idle`, pilot `proof`
(privileged for `relocate`). Client plan (identical for all three runs):

```
--auto-launch Scout --auto-carry-probe --auto-treasure part
--auto-target-seq aleph@4,probe@14,treasure@22,enemy@32
--auto-drop-probe-at 10 --auto-summon-enemy-at 28 --auto-stop-after 2
--auto-summon-enemy-dist 600 --auto-hide-chat-history
--autoshots 6,16,24,34 --exit-after 38
```

| Run | `ALLEGIANCE_HUD_SETTINGS` |
| --- | --- |
| classic | `{"Style":"Classic"}` |
| modern | `{"Style":"Modern"}` |
| classic-bracket | `{"Style":"Classic","SelectionBracket":true}` |

| Capture | What is visible |
| --- | --- |
| `classic_full_aleph.jpg` | The whole real frame at t=6 s: the classic bottom-centre target pane reads `Leighton: aleph` and shows the **blue/white vortex** inside the oval art, no menu or overlay anywhere on the pane, chat history hidden. |
| `modern_full_aleph.jpg` | The same frame in Modern (arc gauges, modern typography): the re-composed target pane reads `Leighton: aleph` and shows the same vortex preview. |
| `classic_pane_aleph_4x.png` | 4× pane crop: the aleph's additive funnel (bright blue/white core, dark spiral arms) fills the pane over the classic oval/crosshair art, under the `Leighton: aleph` name row. |
| `modern_pane_aleph_4x.png` | 4× pane crop, Modern: the same vortex at the same place under the modern pane text (`Leighton: aleph`, the modern kill-bonus row). |
| `classic_pane_probe_4x.png` | 4× pane crop: `probe 5543: unknown` and the probe's real mesh (`utl23`, the `EWS Probe 3` type) — a wide, low lattice with a blue/teal sensor under it — drawn over the oval's grey inner disc. |
| `modern_pane_probe_4x.png` | 4× pane crop, Modern: the same probe mesh (`probe 5540`), identical framing — the probe models are sparse, so native's radius-1 normalisation leaves it small in both styles. |
| `classic_pane_treasure_4x.png` | 4× pane crop: `treasure 5500: unknown` and the **part's own model** — `acs30`, the `Sm Shield 1` buyable — a large green/grey plated body with red and blue details, filling the pane. |
| `modern_pane_treasure_4x.png` | 4× pane crop, Modern: `treasure 5480` with its part model (`acs48`, `Booster 1`) — a large dark cylinder with one brightly lit face — at radius-1 framing. |
| `classic_enemy_no_selection_box_4x.png` | 4× flight-view crop, Classic **defaults**: the enemy `mcp-b001-3150` (`Adv Fighter`, 600 m) dead ahead with the native radar contact label, the native targeted-contact corner marks and the red lead-indicator diamond — and **no closed rectangle, no centred name/health readout**. |
| `modern_enemy_selection_box_4x.png` | 4× flight-view crop, Modern **defaults**: the same situation in Modern, with the modern selection bracket present — four corner brackets around the hull plus `mcp-b001-2902 / 600 m 0 mps / HULL 100% / SHLD 100%`. |
| `classic_enemy_selection_box_override_4x.png` | 4× flight-view crop, Classic with `"SelectionBracket": true`: the player override draws the port's closed blue rectangle, the centred `mcp-b001-6607 / 600 m` readout and the hull/shield bars. |

Pane crops are `(830,800)-(1090,1050)` of the 1920×1080 frame (the native pane is 96 native px at pane-scale 1.588 →
152 px, centred at (957,950)); flight crops are `(800,410)-(1150,700)` around the crosshair. Full-resolution PNGs, logs
and the generated `server.json` are in `~/allegiance-evidence/P805B/`.

Honest notes: the probe's mesh is genuinely sparse, so native's radius-1 normalisation (by the mesh's max vertex
radius) leaves it small in the pane — it is the `EWS Probe 3` mesh in both styles, at the same framing; and the
mission's treasure set is random, so *which* part model appears differs per run (this table names the model each run
actually resolved; the `[preview] … treasure=Part/<model>` line in the client log is the machine-checkable record).

---

## 4. Verification

- `~/.dotnet/dotnet build AllegianceCore.slnx` — **0 warnings, 0 errors**.
- `tools/build-tools.sh` — **all 76 tool projects**, 0/0.
- New/changed tests, 3× alone (`P805bHudProofHarnessTests` (9) + `P572AutoProofTests` + `Hud21TargetModelPreviewTests`
  + `P805HudLeftoversTests`): **84/84 PASS ×3**.
- One `tools/run-tests.sh --tier fast` — **10,360 tests, 291.2 s, PASS**
  (`TestResults/tiers-20261025T004124.329860Z/summary.json`; 3 `NotExecuted` are the live-tier skips). The run before it
  (same code, `…T003129…`) hit one `P739TimerLiveTests` float-boundary failure in untouched `Allegiance.Core` (7.0000000000000009
  vs an upper bound of 7); it passes 3/3 alone and is ledgered in `docs/TEST_FLAKES.md` as a load flake.
- **Fail-first:** the five bugs are not expressible on master — the probe draw path threw at runtime, the aleph draw
  documented a decal, `TargetPreviewKey` had no payload, and `HudRenderer` had no settable selection gate; the new tests
  target the corrected pure seams (`DrawPathFor` incl. `WarpFunnel`, the payload-keyed cache identity, the
  probe/treasure candidate sources, the new flag grammar and machine timing).
- Real-window provenance: each run's `client.out` contains one `[preview]` line per target change — the classic run's
  four are:
  ```
  [preview] Warp target=7 stem='plnt19' tex='-': WarpFunnel scale=1 treasure=- warpTex='plnt19'
  [preview] Probe target=5543 stem='utl23' tex='-': TreasureModel scale=0.267 treasure=Part/utl01bmp warpTex='-'
  [preview] Treasure target=5500 stem='Part' tex='-': TreasureModel scale=6.901 treasure=Part/acs30 warpTex='-'
  [preview] Ship target=8100 stem='-' tex='-': Ship scale=0.174 treasure=- warpTex='-'
  ```
  The same logs contain the
  `[autoproof] auto-target …` selections, the `drop probe/mine (Dispenser)` line, the `relocate` chat lines and one
  `summon settled`; each `server.out` shows the accepted cheats (`[warp] peer N ship 8100 -> SET_CLUSTER sector 0`) and
  the four `screenshot saved:` lines per run.
- No `src/Allegiance.Server` / `src/Allegiance.Core` change, so the AOT publish gate does not apply. No service, port,
  store or env var was added, so `docker-compose.yml` is untouched.
- `tools/check-glossary-links.py` — OK (123 paths); the glossary gained the target-preview and live-proof entries.

---

## 5. Files

- `scripts/live-proof.sh` (+`--mapgen`, `--map-seed`, `--player-treasures`, `--hud-settings`, `--bots-after`, `local.env`
  sourcing, run-local `XDG_CONFIG_HOME`).
- `src/Allegiance.ClientCore.Game/LiveProof/AutoProofPlan.cs`, `AutoProofMachine.cs` (+ sequence/drop/summon/stop/
  treasure-code/chat-history flags and actions).
- `src/Allegiance.ClientCore.Game/Game1.AutoProof.cs` (actions incl. `TrySummonEnemy`, the probe-dispenser shop
  navigation, probe/treasure candidate kinds and the treasure-code/compact-model filter).
- `src/Allegiance.ClientCore.Game/Game1.Targeting.cs` (`GatherSectorCandidates` optional probe/treasure sources).
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` (probe visual, aleph vortex dispatch, payload-keyed treasure
  resolve, `[preview]` log, live selection gate, treasure tumble age).
- `src/Allegiance.ClientCore.Game/Rendering/TargetModelPreview.cs` (`PreviewDrawPath` + `DrawPathFor`, `PayloadId`).
- `src/Allegiance.ClientCore.Game/Rendering/TargetModelPreviewRenderer.cs` (shared `WarpRenderer`, `RenderWarp`).
- `src/Allegiance.ClientCore.Game/Rendering/WarpRenderer.cs` (`advanceClock`).
- `src/Allegiance.ClientCore.Game/Rendering/NativeTreasureRenderer.cs` (`spin` switch: treasures tumble, probes do not).
- `src/Allegiance.ClientCore.Game/Rendering/HudRenderer.cs` (live `SelectionBracket` gate).
- `src/Allegiance.ClientCore.Game/Game1.cs`, `Game1.Lifecycle.cs` (chat-history toggle, renderer wiring order).
- `tests/Allegiance.ClientCore.MonoGame.Tests/P805bHudProofHarnessTests.cs` (new, 9 tests).
- `GLOSSARY.md` (target-preview + live-proof entries), `docs/reports/P805B_HUD_PROOF.md`,
  `docs/reports/P805B_assets/` (11 images, 572 kB), `docs/reports/P805_HUD_LEFTOVERS.md` (§5 follow-up marked done and
  the P805-introduced defects noted; the probe-spin fix is P805b-only), `docs/TEST_FLAKES.md` (one ledgered load flake).

New media: `docs/reports/P805B_assets/` is all new screenshots and needs Domingo's review before the push (public-repo
rule).

## 6. Unfinished / follow-ups

- **Mine target preview** stays unreachable (the port has no Mine target kind) — unchanged from P805.
- The preview camera follows the port's head-look yaw (a documented HUD-21b deviation: native's `TurnTo` uses the
  un-headed orientation, `WinTrek.cpp:426` vs `:438`). The proof now centres the pointer once after launch (like the
  command-camera path already did), so a scripted run starts from the same neutral head-look; a human session still
  frames the preview with their head-look.
- The mission's treasure set is still drawn from `Random.Shared` (`Mission.PopulateTreasures`), so *which* part model a
  capture shows is not reproducible from `--map-seed` alone; the proof pins the kind/selection, not the art instance.
