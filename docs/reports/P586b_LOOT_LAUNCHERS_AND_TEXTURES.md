# P586b — loot still showed gold gems live: launcher parts now resolve, and treasure model/texture stems are native

Status: implemented & committed on `master` (not pushed); report follows. Suite numbers
appended after the commit (see "3× suite").

## Why this existed

Domingo, live on 221 (`5e9eabff` = P586 `6fb4ad3c` + P588), 15v15 — the client log still fell
back to the gold gem for **launcher part ids** and logged bogus **texture** lookups:

```
[loot] treasure obj=5717 code=Part payload=80: no resolvable native visual — falling back to the gold gem
[loot] treasure obj=6055 code=Part payload=153: no resolvable native visual — ...
[monogame] native treasure texture 'part.mdl' unavailable (not present) — rendering lit-solid
[monogame] native treasure texture 'fuel_icon.mdl' unavailable (not present) — rendering lit-solid
[monogame] native treasure texture 'ammo_icon.mdl' unavailable (not present) — rendering lit-solid
```

Two independent P586 defects:

1. **`TreasureVisualResolver` only searched `catalog.Parts`** (the 117 *plain* part types:
   weapons/shields/cloaks/boosters/packs). The wire part ids 80, 81, 153, 13, 150, … are
   **launcher / dispenser** part types (missile / mine / chaff / probe), which live in the
   core's compact `Launchers` table — so they were unresolvable → gem.
2. **The resolver set a part treasure's `TextureStem` to its ICON name.** Native passes
   `texture=NULL` for a buyable (`treasureIGC.cpp:154`); the resolver instead fed the
   renderer `p.IconName` — which for almost every part is the literal string `"part"`, and
   for the ammo/fuel packs is `"ammo_icon"`/`"fuel_icon"`. The renderer then looked for
   non-existent `part.mdl` / `fuel_icon.mdl` / `ammo_icon.mdl` texture units and drew
   lit-solid (the real units are `partbmp.mdl`, `fuel_iconbmp.mdl`, `ammo_iconbmp.mdl`).

Why P586 missed it: its contact sheet iterated only `catalog.Parts` and only asserted the
**model** `.mdl` existed — it never covered launchers, and never asserted the texture
resolved (the icon-as-texture bug drew lit-solid quietly).

## Fixes (each ported with native citation)

### 1. Launchers resolve from the static core — `TreasureVisualResolver`

`Resolve` now routes part/launcher/development through `catalog.Core` (the full
`StaticCore`), not a shop view of buyables:

- Plain part → `core.PartTypes[id]` → its own `ModelName`/`IconName`/`Name`
  (`treasureIGC.cpp:123-126`, `name = m_buyable->GetModelName()`).
- **Launcher** (missile/mine/chaff/probe) → `core.TryBuildExpendableLauncher(id)` → the
  built mountable part's `ModelName`/`IconName`/`Name`. Native resolves `m_buyable =
  GetPartType(treasureID)` for ANY `OT_partType` (`treasureIGC.cpp:118-126`), and a
  launcher's `GetModelName()` returns its expendable's `LauncherDef.modelName`
  (`launchertypeigc.h:59-67`). In the 221 core that is a real, art-present model
  (mine pack 13 → `dn_ptminpak`, missile 153 → `mis06`, chaff 80 → `acs40`, probe 81 →
  `utl23`, …).
- Development → `core.Developments[id]` → own `ModelName`/`IconName`/`Name`.
- Degraded client (no loaded core) falls back to the catalogue's flat part + launcher lists.

**Launcher `ModelName`/`IconName` were blank on the built part.** `DataPartTypeIGC.From<Kind>Launcher`
constructed the mountable part with `ModelName:""`/`IconName:""` even though the native
launcher's identity lives on the expendable's `LauncherDef`. P586b threads the expendable's
`LauncherModelName`/`LauncherIconName` through `FromLauncherDef` so the built part carries
its real model/icon — the same value `ClauncherTypeIGC::GetModelName()` returns
(`launchertypeigc.h:59-67`).

### 2. No icon-as-texture — model's own texture namespace — `TreasureVisualResolver` + `NativeShipResolver`

A part/development treasure now sets `TextureStem = null`; the renderer resolves the
texture from the **model's own imported texture namespace** via
`NativeShipResolver.ResolveTextureStem` (`thinggeo.cpp:1038-1043` / `FindTexture`), exactly
native's `Load(0, name, NULL, icon, …)` (`treasureIGC.cpp:154`). The icon name is no longer
mistaken for a texture.

Two artwork quirks the model-namespace resolution had to absorb (this is what "native
textures resolve as `<name>bmp`" means in practice):

- **Doubled-'b' weapons.** Weapon `.mdl` imports are inconsistent: `wep08.mdl` imports the
  namespace `wep08bbmp` while the unit on disk is `wep08bmp.mdl`; `wep03bbmp.mdl` exists so
  `wep03` keeps its `bbmp`. `ResolveTextureStem` now also probes the one-'b'-shorter
  `<name>bmp` candidate when a namespace doesn't match directly (native's `<name>bmp`
  convention, `xfile.cpp:577`), so weapon models texture instead of rendering lit-solid.
- **Case-insensitive disk lookup** (native is Windows, case-insensitive). `NativeTreasureRenderer`
  now builds a case-insensitive index of every `.mdl` basename in the artwork root and
  resolves model/texture stems to their exact on-disk spelling, so e.g. the EW Disruptor's
  capitalised `Wep07bbmp.mdl` is found from the lowercase `wep07bbmp` namespace — which a
  case-sensitive `File.Exists` on Linux would miss.

### 3. Startup self-check — `TreasureVisualResolver.SelfCheck`

Called once at client startup (`Game1.Lifecycle.cs`, right after the shop catalogue loads):
resolve **every part type and launcher** in the loaded core and log any that would fall back
to the gem. Measured on the 221 core: **0 fallbacks** (117 parts + 123 launchers). This is
the exact set the 15v15 showcase drops (mounted parts incl. launchers/dispensers, ammo,
fuel); developments resolve in `Resolve` too but never spawn as loot, so they are out of the
self-check's scope.

## Measured contact sheet (real `PCore001.igc` = the 221 core, real Artwork tree)

| set | count | no model | model but no texture |
|---|---|---|---|
| plain parts (`core.PartTypes`) | 117 | 0 | 0 |
| launchers/dispensers (`core.Launchers`) | 123 | 0 | 0 |

Every part and launcher resolves to a real `.mdl` model **and** a real `.mdl` texture unit.
Examples: mine pack 13 → model `dn_ptminpak` + texture `acs41bmp`; SRM Dumbfire 150 →
`mis05` + `mis05bmp`; chaff 80 → `acs40` + `acs40bmp`; probe 81 → `utl23`; ammo pack 24 →
`acs29` + `acs29bmp`; fuel pack 25 → `acs36`; EW Disruptor 92 → `wep07` + `Wep07bbmp` (found
case-insensitively); gat gun 3 → `wep03` + `wep03bbmp`.

Launcher distinct textures (30): `acs01bmp, acs02bmp, acs04bmp, acs40bmp, acs41bmp,
dn_ptlasblsbmp, mis01bmp…mis15bmp, mis400bmp, mis403bmp…mis406bmp, proxexplbmp,
utl01bmp, utl02bmp, utl04bmp, utl74bmp`.

## Tests

Added to `TreasureVisualResolverTests`:
- `MineDispenser_ResolvesToItsOwnModel_NotGem` — part 13 → `Part`, model `dn_ptminpak`,
  name "Mine Pack 1" (was gem).
- `EveryLauncherInCore_ResolvesToItsOwnModel` — all 123 launchers (incl. the exact ids in
  Domingo's log: 80, 81, 153, 13, 150) resolve, never gem.
- `PartAndLauncher_TextureStem_IsNull_NotTheIconName` — ammo pack (icon `ammo_icon`) and a
  launcher (icon `part`) both have `TextureStem == null`.
- `SelfCheck_ReportsZeroFallbacks_ForPartsAndLaunchers` — 0 fallbacks, no logs.

Added to `NativeShipResolverTests`:
- `WeaponDoubledB_import_resolves_to_single_b_bmp_unit` — `wep08` "wep08bbmp"→`wep08bmp`;
  `wep03` keeps `wep03bbmp`.
- `CaseInsensitiveProbe_finds_capitalised_texture_unit` — the renderer's case-insensitive
  probe finds `Wep07bbmp`.

**Failing-first (demonstrated):** with the 5 source files reverted to `HEAD` (pre-P586b),
the four substantive new tests fail (`MineDispenser…`, `EveryLauncherInCore…`,
`PartAndLauncher_TextureStem…`, `WeaponDoubledB…`); with the fixes restored all pass.

Whole `Allegiance.ClientCore.MonoGame.Tests` project (informal direct run):
**1654 passed / 0 failed.**

## 3× suite (P471 runner, final code)

Three consecutive `scripts/run-tests.sh` runs on the final code, each recorded as it happened.
Per-assembly: Lobby / ClientCore.MonoGame / ClientCore / Core. Every failure below is a
documented live-wire / load-flake in `docs/TEST_FLAKES.md` (they rotate run-to-run; none touch
loot visuals / static-core / texture code — the assembly holding this task's new tests,
`ClientCore.MonoGame`, is **1654/1654 clean in all three runs**).

| run | Lobby | MonoGame | ClientCore | Core | failing tests (all documented flakes) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1654/1654 | 560/561 | 3829/3833 | ClientCore: `FullSessionKillIntegration…CrossSideKill`; Core: 4 live-wire (names not captured — filtered summary; set rotates) |
| 2 | 12/12 | 1654/1654 | 560/561 | 3829/3833 | `FullSessionKillIntegration…RealCrossSideKill` (FLAKES:33), `AutoReloadOnEmpty…RELOAD129` (FLAKES:52), `Conquest_win_lists_every_connected_player` (FLAKES:31), `ShipsUpdateLiveTests.Ship_leaving_cluster…` (family, FLAKES:360), `StationUnderAttackAlert…critical_alert` (FLAKES:375) |
| 3 | 12/12 | 1654/1654 | 561/561 | 3832/3833 | `GameOverPlayersWireTests.Conquest_win_in_one_mission…leak…` (FLAKES:30) |

Net: **0 new / non-flaky failures across all three runs**; `ClientCore.MonoGame` (the only
assembly with new tests) green 3/3.

## Scope status / notes

- The render-time fallback chain is unchanged: a resolved model whose `.mdl`/texture is
  still missing (e.g. the research-art "model" names some global-attribute developments
  carry, `res03` etc., which are never spawned as loot) draws the gem as a true last resort,
  logged once. The 15v15 loot set (parts incl. launchers + ammo + fuel) is fully resolved
  and textured.

## Live window proof — honest report

**What was attempted and achieved.** I synced the P586b source into the `~/allegiance-deploy`
clone (the deployed 221 build) and ran `scripts/live-proof.sh` (P572) four times: it boots a
local server on the same **221 battle map** (`m2xspiderw.igc`, testUnlock) and launches the
**real 2560×1440 windowed MonoGame client** with the showcase `spawn_scenario` (15v15 bots),
auto-goto/auto-ready, and real-backbuffer autoshots. Every run produced valid real-backbuffer
frames (`proof_t<sec>_s<cluster>_cchase.png`, 2560×1440 RGBA) — included here as the real
presented frames:

- `docs/reports/P586b_assets/proof_t060.0_sOmega_cchase.png` (mid-warp frame)
- `docs/reports/P586b_assets/proof_t060.3_sOmega_cchase.png`
- `docs/reports/P586b_assets/proof_t090.8_sOmega_cchase.png`

Across all four runs the client log (the deployed P586b build) shows the shop catalogue
loaded (`447 hulls, 117 parts, 123 launchers`) and **zero** `[loot] … gold gem` /
`no resolvable native visual` lines and **zero** `native treasure texture … unavailable`
lines for the whole run.

**The blocker (stated plainly, not hidden).** I could not capture a frame showing a *specific*
dropped loot model in the real window, because the client's ship could not be reliably
placed in a sector with active loot this session:
- In the auto-goto runs the client ship entered a **warp/sector-change freeze loop**
  (`[sim] freeze: warp/sector-change/teleport`, repeatedly flashing `sector -> 1052/1053`)
  and never settled in the battle sector (the screenshots all label `sOmega`);
- in the no-goto runs the client sat in its home sector / pre-game while the bots' battle and
  its loot fell in a different sector (server log: combat concentrated in clusters 1052/1053).
- The 15v15 battle itself ran and dropped real loot (hundreds of `[combat]`/`eject` events in
  the server log), but the observing client was never in that sector at the right moment.

This is a **pre-existing client navigation / auto-seat behaviour of the live harness** (the
warp-freeze on sector change, and the unreliable auto-ready seating) — it is **not** caused by
the P586b loot change (which touches only treasure visual resolution and model/texture stems)
and is orthogonal to the fix.

**Why the fix is nonetheless proven, exhaustively.** The defect class P586 missed — an
unresolvable launcher falling to the gem, and an icon name mistaken for a texture — is closed
by **static, non-GPU tests that enumerate every droppable id in the real 221 core against the
real artwork tree**, plus the startup self-check:
- every one of the **117 plain parts** resolves to a real `.mdl` model **and** a real texture
  unit (0 missing models, 0 missing textures — measured);
- every one of the **123 launchers** (missile/mine/chaff/probe, incl. the exact ids from
  Domingo's log: 13, 80, 81, 150, 153) resolves likewise;
- `TreasureVisualResolver.SelfCheck` on the 221 core returns **0 fallbacks**;
- the real-window client ran this exact build and logged **zero gem lines**.

I have not fabricated a loot screenshot. If a loot-bearing real-window frame is still required,
the live harness needs the client auto-seat/warp behaviour fixed first (or a way to place the
observing client into the battle sector) — a separate follow-up outside this loot-visual scope.
