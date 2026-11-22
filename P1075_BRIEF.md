# P1075 — Browser live follow-ups: late-arriving art, purple frame, first-join bytes, small HUD/hangar fixes

Lane: any (browser/visual task; an image-capable model). Base: master at or after `d1c3a825` (P1073 + P1074 merged and
deployed to the public VPS on 2026-11-22). Five-hour cap from your start time. Report:
`docs/reports/BROWSER_LIVE_FOLLOWUPS_P1075.md`, written before implementation and kept current.

## Why

Claude ran real browser guests against the live perpetual 5v5 (generated maps, random civs, showcase research) after the
`d1c3a825` deploy. The research briefing, rock textures, sky and "Dock at home station" all work. Five problems remain:

1. **Stations that arrive after their first draw stay invisible: confirmed in code, not yet seen on screen.**
   - `NativeStationRenderer.MeshFor` (`src/Allegiance.ClientCore.Game/Rendering/NativeStationRenderer.cs:96-119`)
     records a miss in `_missingModelLogged` and returns null from then on (`:100`).
   - The texture lookup does the same with `_missingTextureLogged` (`:127`).
   - Nothing clears either set when a verified battle pack is promoted. P1073 added that invalidation only for the sky
     and planet renderers (`NativeSkyRenderer.InvalidateMissingArt`, `Game1.Scene.cs` content-revision check).
   - So any station whose pack lands after the first frame that tried to draw it stays untextured or invisible for the
     session. That includes a station a bot builds mid-match in your sector, and every station on a slow first join.
   - Live evidence: in the harness metric (`Game1.SectorArt.cs` `SectorArtMetrics`), the starting garrison resolves
     `texture: "dgarrbmp"`. Every bot-built station (ids 9188+: `dgsup`, `drgop`, `dgexp`, `dtac`, `drgsy`, `dbase`,
     `tectp`, `helm`) reports `texture: null` 30 s and 2 min after launch.
   - Their packs do contain the textures, e.g. `station-4` = `dgsup.mdl`, `dgsupbmp.mdl`, `dregbaybmp.mdl`.
     `TextureStemFor` returns null whenever `MeshFor` is null (`:159-164`).
2. **A single purple frame while packs promote.** In the first ~10 s after launch on the live server, about 77 MB of
   packs promote. In 1 of 3 sampled runs, one sampled frame (sampled every 2 s) was entirely RGB (68–69, 34, 136):
   - the 3-D scene is gone (no stars, ship or rocks);
   - the HUD draws normally on top;
   - the next sample was normal.
   That colour is MonoGame/KNI's fill for a render target whose contents were discarded. Something in that frame
   probably switches render targets after the scene is drawn and discards it. Candidates to check, not conclusions:
   - texture/glyph work that sets a render target mid-frame, e.g. `HudSdfFont.cs:31-35`;
   - texture uploads or tiling on promotion (`exp22bmp` must be tiled before GPU upload, per the P1065 battle receipt);
   - the P1073 sky reload on every content revision (`Game1.Scene.cs` BeginScene: `InvalidateMissingArt`,
     `_skySector = -1`).
   Locally the packs arrive almost at once, so it never reproduced there.
3. **First-join bytes.** A 5v5 join promoted ~77 MB in its first 20 s (18 MB at t+2 s, 47 MB at t+6 s, 77 MB at t+18 s).
   P1073's report estimated 9–17 MB on top of the 13.8 MB join + runtime. Station packs reach 9.8 MB each (median
   1.4 MB), static rocks reach 6.1 MB, and planet posters reach 3.9 MB.
4. **"AFTERBURNER ACTIVE" on a ship with no afterburner.** The default guest loadout (hull 13, one gun, part 7) has no
   afterburner. The server correctly reports power 0 (native `shipIGC.h:1137`), but the port's top-centre readout
   (`HudRenderer.cs:1912-1923` `DrawAfterburner`) prints "AFTERBURNER ACTIVE · PWR 0%" from the button bit alone.
5. **Small hangar/help items:**
   - **Buy/Def placement (Domingo's screenshot, live):** the port-only Buy/Def cells
     (`HangarScreenRenderer.cs:158-170`) are drawn below the hangar art at the bottom right, partly under the page's
     identity strip at 1280×800.
   - **Help footer text:** the in-game key reference header says "LEFT/RIGHT PAGES" (P1074 browser lifecycle), but the
     shared footer still says "UP/DOWN topics" (`HelpOverlayRenderer.cs:188`). Desktop's manual uses UP/DOWN
     (`Game1.IntroScreen.cs:1176`).
   - **No keyboard launch from the hangar screen:** desktop `Game1.Hangar.cs` has none either. `L` is the Loadout
     screen's `ShopLaunch` (`InputActionCatalog.cs:540`).

## Items

### 1 — Late-arriving art becomes visible (do this first)
- Reproduce it first with a harness or test: a station (and asteroid, ship, missile, probe, treasure) whose pack is
  promoted after the first draw attempt must draw its native model **and** texture on a later frame.
- Fix the class of bug, not just stations. Audit every renderer with a negative or missing cache that the browser
  reaches, including the files that define `TextureStemFor`:
  - `NativeStationRenderer`, `NativeAsteroidRenderer`, `NativeShipRenderer`;
  - `NativeMissileRenderer`, `ProbeRenderer`, `NativeTreasureRenderer`, `ProjectileModelRenderer`;
  - hangar art, and anything else that records a miss permanently.
- Give them one invalidation path tied to the staged store's content revision (`StagedAssetSource.ContentRevision`).
  Log a miss once per revision, not once per frame.
- Desktop behaviour must not change: desktop art is local, so revisions never change there.
- Extend `tools/p1073-browser-generated.cjs`, or add a P1075 harness, for a generated map where a station is **built
  during the session**. Assert that its model and texture become resident and that `TextureStemFor` resolves.
  Capture it in view in Classic and Modern.
  - Use a showcase/bot path that builds a station quickly.
  - Or add a proof-host hook that creates one; a proof-tool hook only, no server rule change.

### 2 — No purple frame
- Find the exact cause and cite file:line. Reproduce it locally by **throttling pack delivery** (a host/proxy option or
  a test hook that delays promotion), so promotion happens mid-flight as it does live.
- Sample every frame, or detect the discard colour in a test hook. Don't rely on 2 s screenshots.
- Fix it so a frame that promotes or processes a texture never presents a discarded scene. For example, do GPU texture
  work outside the scene pass, or restore and redraw the target.
- The harness asserts zero discard-colour frames across a throttled first join, in Classic and Modern.

### 3 — First-join bytes
- Measure what a live-like 5v5 first join downloads in the first 30 s, by pack family (stations, hulls, rocks,
  planets, globes, scene), against P1073's estimate. Write the table in the report.
- Reduce it without changing what the player eventually sees:
  - prioritise by visibility/distance (current sector first; stations and rocks in view before far ones);
  - defer the prefetch sector;
  - avoid duplicate bytes between packs (a texture shared by several station packs must download once);
  - check whether the biggest station and rock packs carry unused LODs or textures.
- Measure before and after. Behaviour parity rules: no visual downgrade without a flag; on-demand promotion stays
  verified.
- Out of scope: any change to the pack verification, `StagedManifest.Canonical`, or the CSP.

### 4 — Afterburner readout when none is mounted
- Read native first: what native shows when the afterburner key is held with no afterburner mounted
  (`WinTrek`/HUD sources; cite file:line).
- Make the port's readout match. If native shows nothing, show the inactive state or no PWR line, in **both** HUD
  styles. Fix the shared code, so desktop and browser agree. Screenshot both styles.

### 5 — Hangar and help polish
- Buy/Def cells: place them inside the hangar art area at every supported browser size (1920×1080, 1280×800, 800×480)
  and on desktop, without covering native hotspots. Read the P945/P1070 layout reasoning first. Screenshots at each size.
- Help footer: the browser footer names the keys that actually page and scroll in the browser. The desktop text stays
  unless desktop behaviour is the same.
- Keyboard launch from the hangar screen: check native's hangar screen for a launch key or accelerator (cite it).
  Add one only if native has one; otherwise document that it doesn't, and leave it.

## Gate

Light gate:
- solution build 0/0 and `tools/build-tools.sh`;
- every new test 3× alone;
- `Allegiance.ClientCore.Browser.Tests`, `Allegiance.ClientCore.Game.Browser.Tests` and the `ClientCore.MonoGame` tests
  for the renderers you touch;
- one full Fast sweep at the end;
- `tools/p1073-browser-generated.cjs` and `tools/p1074-browser-keyboard.cjs` still pass. Claude runs them on the
  workstation if the lane has no Chromium.

Flakes go in `docs/TEST_FLAKES.md` with 3× isolated reruns. Use `$HOME/.dotnet/dotnet`, `NUGET_HTTP_CACHE_PATH` and
`--disable-build-servers`.

## Not in scope

- Any VPS, rig or lane-bots change, deploy or run; any server rule change. Proof-host hooks are fine.
- Weakening pack verification, `StagedManifest.Canonical` or the CSP.
- Desktop defaults or desktop input.

## Common rules

- No commits, pushes, package installs or deploys; leave changes in the working tree.
- Never `git stash`; never `pkill -f`; stop only processes you started, by PID.
- Native first: cite native file:line for every native-behaviour claim. Browser packs and promotion are port-only;
  say so. Footer: "Native intent / Modern approach", both sections.
- HUD/hangar changes must work in Classic **and** Modern; screenshot both.
- Native 2-D conventions: y-up, a StringImage hangs below its point, a GroupImage paints its mdl list in reverse
  (first child on top).
- No home paths, hostnames, IPs or secrets in committed files.
- Report: what was done per item, tests with counts, anything unfinished and why.
