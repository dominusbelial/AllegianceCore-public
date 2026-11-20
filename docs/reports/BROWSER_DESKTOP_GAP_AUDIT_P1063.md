# Browser vs desktop in a small battle: gap audit — P1063

Started 2026-11-20 16:05, finished 17:25 (lane local time, UTC−5); six-hour cap 22:05. Audit only: no product
code changed, no commits or pushes, loopback only. Raw captures and logs are in `<lane evidence dir>`
(`.scratch/p1063/`, ignored).

## Read this first: there is no Chromium on this lane

The P1051 harness's modules path (`.scratch/p1051/npm/node_modules`: Playwright + `@sparticuz/chromium`) does not
exist on this lane, and no other browser binary is installed (whole filesystem and npm cache searched; only
shell-completion stubs exist). The brief forbids installing packages, so **the browser client was not run in this
audit.** What that means for each item:

| Item | Status |
|---|---|
| 0 — Battle on loopback | **Half done.** Server + bots + **desktop** client ran at 1, 5 and 10 per side. The browser did not join. The command a browser-joinable battle host needs is worked out from source but **not run**. |
| 1 — Matched captures | **Desktop: fresh from the battle. Browser: the already checked-in captures** of P1051/P1053/P1054 (same map, core and home sector, one or two ships, no battle). Two pairs match closely (same hull, sector, camera); the combat scenes have no browser image at all. No clips: this lane has no video encoder, so motion is shown as frame strips. |
| 2 — Gap table | **Done from source and from a freshly published browser generation of this checkout.** Exact for "not ported / deliberately off" rows; it cannot show browser bugs that only appear at run time. |
| 3 — Performance | **Desktop measured at 1, 5 and 10 per side. Browser not measured**; the only browser numbers are the earlier one/two-ship ones, quoted and labelled. Download sizes are measured from the fresh generation. |
| 4 — Ranked fix plan | **Done.** |

The single most important finding: **in a battle the browser would not draw most of the other ships.** Its download
pack holds the meshes of 5 of the core's 179 hull models (the scout, two lifepods, two miners); a ship whose mesh is
missing is skipped, and skipped before its radar contact is created. In the 5 v 5 battle here the bots flew
`fig21` (Hvy Scout) and `quizfigv2` (Adv Fighter); neither mesh is in the pack. This is established from the pack
manifest and the draw code, not from a browser run.

Path shorthands used below: `G/` = `src/Allegiance.ClientCore.Game/`, `B/` = `src/Allegiance.ClientCore.Game.Browser/`,
`H/` = `src/Allegiance.ClientCore.Browser/`, `D:` = a line of the desktop frame `Draw` in `G/Game1.Rendering.cs`
(the method starts at `:310`).

## Item 0 — battle setup

New tool `tools/p1063-battle.sh` (modelled on `scripts/live-proof.sh`, but it does not build, runs the client
offscreen/software-GL, and caps the size at 10). It boots `Allegiance.Server` on `XXV_simple.igc` / `PCore001.igc`,
starts one desktop client with the existing `--auto-*` proof flags, spawns the bots through `allegiance-mcp`
(`spawn_scenario`), and stops every process by exact PID. The pilot takes one seat on side 0, so `--size N` gives
N v N including the pilot.

Build once (Release, 0 warnings / 0 errors for all three):

```
export NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p1063/nuget-http
for p in src/Allegiance.Server tools/allegiance-mcp src/Allegiance.ClientCore.MonoGame; do
  ~/.dotnet/dotnet build -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false $p; done
```

The battle used for the combat captures and the 10 v 10 measurement (5 v 5 is the same with `--size 5` and counts
4 and 5):

```
SDL_VIDEODRIVER=x11 xvfb-run -a -s '-screen 0 1920x1080x24' tools/p1063-battle.sh \
  --out .scratch/p1063/d10 --size 10 --style Modern \
  --scenario '{"sides":[{"count":9},{"count":10}],"target_sector":"Ixion","staging":false}' \
  --autoshots 29,32,44,47,...,278 --exit-after 285 \
  --client-extra "--auto-launch --auto-launch-after 60 --auto-goto Ixion --auto-stop-after 40 --director --director-seed 1063"
```

Things learned getting there (each cost a run):

- `SDL_VIDEODRIVER=offscreen` gives a broken screenshot on this lane (the frame is drawn into an 800×480 corner of
  the requested backbuffer, the rest is white). Under Xvfb with the x11 driver the captures are correct.
- The bots do not fight in a home sector: with `target_sector` set to the pilot's home (Ceto) nothing came within
  view in 335 s. With `target_sector: "Ixion"` (one aleph from Ceto) the first kills are about 95 s after the spawn.
- `--auto-goto` fires once at launch and gives up if the aleph is not yet known, so the pilot must launch late
  (`--auto-launch-after 60`), after a team-mate has discovered the aleph. With only one or five bots that discovery
  can come later, or the ship can be stopped by `--auto-stop-after` before it arrives: the 5 v 5 measurement needed three
  attempts and the 1 v 1 never got there.

**Spectating.** Neither client can watch a server mission without a seat. The desktop's spectator exists only for
its own embedded skirmish host (`G/Game1.SkirmishObserver.cs:14`, `LocalSpectating => _skirmish is not null && …`);
online it has the director and follow cameras (F2/F3, `--director`, `--follow`; `G/Game1.Director.cs:514`), which film
the battle while the pilot's own ship sits parked. That is what the desktop "spectator" captures here are. The
browser page offers only side buttons and "Ready and launch current ship" (`H/Pages/Menu.razor:66-88`), and in flight
only a fly-by view of the nearest ship ("View nearby ships", `B/Game1.Flight.cs:38,314-324`). So the answer to "can the
browser spectate" is **no**, and the desktop can only do it from a parked ship.

**Browser half, not run.** A server the browser can join needs the lobby identity path and the WebSocket listener
(`tools/p1051-browser-host/Program.cs:46-58`). That host sets `Identity.AllowAnonymous = false`, which refuses the
anonymous TCP bots; the server default is `true` (`src/Allegiance.Server/Server.Identity.cs:16,95-99`), which would
admit both. So the next step is a `--bots N` option on `p1051-browser-host` that leaves anonymous logon on and starts
`scripts/mcp_driver.py` against its TCP port. I did not make that change because nothing here could prove the
browser side of it.

## Item 1 — captures

**Method.** Desktop: the real client window (1920×1080, Xvfb, llvmpipe software GL, `ALSOFT_DRIVERS=null`), real
backbuffer screenshots from the client's own `--autoshots`. Browser: unedited Chromium screenshots already in
`docs/reports/BROWSER_*_assets/`. Sheets were composed with `tools/p1063-pair-images.py` (new, stdlib only: box
downscale to a common height, white divider, no other pixel change). **Browser is always the left image.** Every
sheet below was opened and looked at; total 13 PNGs, 2,719,304 bytes.

**How well the cameras match.** Sheets 01–03 are a real match: same hull (the scout), same sector (Ceto), same chase
camera, same HUD style; the own ship lands on the same pixels in both clients. Sheets 04–07 are the same kind of scene
in the same map but not the same moment or framing. Sheets 08–13 have no browser counterpart for the combat content;
the browser half is described from source.

| # | Sheet | What it shows |
|---|---|---|
| 01 | [chase, Modern](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/01-chase-modern.png) | Browser / desktop with `--sky native` / desktop default. Same scout, gauges and moon in the same places. Browser: dark navy background, brightly lit rocks, no engine glow. Desktop: black background with small dust specks, dim blue-lit rocks, a SYSTEMS panel (gun, ammo), reticle, SIG readout, weapons line, sector minimap, "Current location". Third frame (afterburner on, 161 mps): orange glow around the hull and a long yellow exhaust column. |
| 02 | [own ship, 1:1 crop](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/02-own-ship-detail.png) | Same three frames uncropped-scale. Browser hull is lit warm/brown from a fixed direction; desktop hull is lit by the sector light; the afterburner frame adds the glow and exhaust. The browser frame reads SHIELD 0 % with a red mark; the desktop reads 100 %. |
| 03 | [chase, Classic](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/03-chase-classic.png) | Browser: ship, three bright rocks, Classic gauge ring, no labels. Desktop (flying to the aleph on autopilot): the aleph swirl ahead, range labels on rocks all round the frame edge, a red team message, target pane, weapons line, minimap, exhaust column. |
| 04 | [hangar and loadout](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/04-hangar-loadout.png) | Browser: a four-line text box ("HANGAR", callsign, "Station 1051", "Use Ready and launch to fly") and MONEY $0. Desktop: the hangar art screen with its consoles, then the loadout screen — hull list (198 offered), rotating hull preview, equipment slots, stats, Launch. |
| 05 | [asteroid](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/05-asteroid.png) | Browser: one rock filling the view, strongly lit with a bright highlight. Desktop: a rock selected as target (hull 100 %, range, ORE 0 %), dark, lit softly from one side; other rocks carry range labels. |
| 06 | [sky](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/06-sky.png) | Browser (a nebula sector): a full-screen yellow/violet nebula behind a rock. Desktop (Ceto, HUD hidden, default sky): black, a few dust specks, dim rocks, a grey moon; no nebula. Different sectors, so this pair shows the two sky styles, not one scene. |
| 07 | [sector map](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/07-starmap.png) | Same 13-sector graph in both. Browser (800×480): sector names overlap each other; a "Sector" drop-down below does the selecting. Desktop: names clear, a sector info box (asteroids, He3, probes), "COMMANDER MARK", plus the always-on minimap bottom right. |
| 08 | [ships near and far](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/08-ships-near-far.png) | Browser: two scouts as small bare meshes with name labels. Desktop 10 v 10, director camera: two fighters close up, sector-lit blue; then two distant ships each drawing a long yellow exhaust line with smoke puffs, and a third with a blue ribbon and a glow. |
| 09 | [HUD in the battle, Modern / Classic](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/09-hud-in-battle.png) | Desktop only. Enemy and friendly brackets with names and ranges, the selected target's name/range/closing speed/hull/shield, a 3-D preview of the target ship in the target pane, "… ejected" messages, minimap with the battle sector marked. |
| 10 | [lifepod HUD, Modern / Classic](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/10-lifepod.png) | Desktop only. "You ejected", "YOU ARE IN AN EJECT POD", oxygen countdown, autopilot-to-base prompt, the home station selected and previewed. |
| 11 | [aleph, station](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/11-aleph-station.png) | Desktop only. The aleph as a blue-white swirl with range and closing speed; the home station from 583 m with lit windows and coloured edge lights. |
| 12 | [strip: hit → eject → pod](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/12-strip-hit-eject.png) | Desktop only, 1–2 s apart. Enemy at 353 m; the whole frame washes red as the hull is hit; "You were forced to eject by …"; pod HUD with oxygen. |
| 13 | [strip: self-destruct](BROWSER_DESKTOP_GAP_AUDIT_P1063_assets/13-strip-selfdestruct.png) | Desktop only, 0.4 s apart. Scout; then the pod beside the station with a bright flash and rays at the wreck position, fading over the next two frames. |

Not captured on the desktop either: a clean frame of gun bolts, a missile in flight, chaff or a fireball. The logs
prove they happened (4,143 projectiles, 39 explosions, 325 hits in the 10 v 10 run) but none of the 3-second
screenshots caught one in frame. The respawn path was captured (strip 12 continues into the hangar screen and
"docked in a lifepod: restored last loadout" in the log) but not put in a sheet.

### Sound

Desktop, from the `[monogame] audio:` log lines of nine runs (event, occurrences): projectile fly-by start/stop
(9,440), shield hit on another ship (664), hull hit on another ship (526), asteroid ambience (617), missile ambience
(127), missile explosion (64), rock hit (58), probe ambience (65), aleph ambience (44), ship/station explosion (28),
sector jump (18), station ambience (16), collision (15), own hull hit (13), countdown ticks (90), payday (48),
"enemy joined" (12), "recruit joined your team" (9), "welcome home" (10), docked (10), "you became team leader" (9),
own weapon fire (6), shield down / energy low / ammo low (6 each), hull low (4), incoming-missile warning armed (2)
and cleared (1). Engine and afterburner loops run through `_sound.Update` (`G/Game1.FlightInput.cs:2821`) and do not log.

Browser: **one sound, the menu click.** `B/Platform/MenuAudio.cs:11-14` plays only `mouseclickSound`; the sound
engine, music and voice are excluded from the build (`B/Allegiance.ClientCore.Game.Browser.csproj`, the `Audio/*`
exclusions), and the generation's join manifest contains no sound file (the artwork corpus has 1,142). In flight the
browser is silent.

## Item 2 — gap table

Cause classes: **off** = deliberately disabled, **limit** = WebGL/WASM limit, **not ported**, **bug**. Size is the
fix's rough size (S ≈ 1–2 days, M ≈ 3–5, L ≈ 1–2 weeks). "Cost" is the likely browser frame cost.

Most rows share one cause. The browser's whole flight frame is `DrawOnlineFlight`, 120 lines (`B/Game1.Flight.cs:296-417`);
the desktop's is `Draw` in `G/Game1.Rendering.cs` (5,500+ lines of a 42,000-line `Game1`). The effect renderers
themselves are already compiled into the browser assembly (the csproj includes `G/Rendering/**`) and the plain
"native" tier of each one draws with `BasicEffect` billboards (`G/Rendering/ParticleBillboardBatcher.cs:35`), which the
browser already uses. They are simply never called. Their textures (`exp20/23/24/25`, `f101–f106`, `e_tele`,
`debris1`) are already in the pack.

### Looks

| # | Difference (sheet) | Class | Desktop | Browser | Size | Cost |
|---|---|---|---|---|---|---|
| L1 | **Other ships missing.** Any hull except scout, lifepods, miners is not drawn and has no radar contact. (source; not run) | not ported (pack scope), with a **bug** in the fallback | All hull meshes on disk; 289 models preloaded at start | Pack keeps only hulls the recorded single-ship traces opened, plus two miners (`tools/p1051-browser-packs.py:62-88`); fresh generation: 5 of 179 hull meshes. `B/Game1.Flight.cs:347-348` skips the ship before the contact at `:360` is built | S for the contact fix; M to stream hull meshes on demand through the existing sector streamer | Download: 0.07–1.3 MB gzip per hull type (all 179 ≈ 45 MB gzip, so not in the base pack) |
| L2 | No engine glow, afterburner exhaust or motion ribbons (01, 02, 08) | not ported | `D:1660-1672` trails, `D:1684-1706` nozzle glow, `D:1733-1737` ribbons; drawn at every distance (`ships.fullDetail`, `G/Rendering/VisualFlags.cs:130,207`) | no emitters | M | Grows with ship count: billboards per boosting ship |
| L3 | Flat fixed lighting; rocks and hulls much brighter than desktop (01, 02, 05) | not ported | Sector light each frame (`D:439-442`, `G/Rendering/SectorLightingEffect.cs:73`) | passes `null` lighting to every draw (`B/Game1.Flight.cs:355-356,374-375,387-388`) → default light rig | S | none |
| L4 | Background is navy, not black; no dust, no simulated stars, no lens flare (01, 06) | not ported | Default sky tier "black" (`G/Game1.Session.cs:230`, `D:2419`) with moons, distant objects, flare; dust `D:720-766` | clears to `Color(5,12,20)` (`B/Game1.Browser.cs:219`) and draws the native sky tier (`B/Game1.Flight.cs:339`), which the desktop only uses with `--sky native` | S–M | small |
| L5 | Weapon fire is a plain coloured box per round | not ported | Sprite or model per projectile type (`D:1207-1272`); own shots drawn the frame they fire (`D:1280-1315`) | fallback `ProjectileRenderer` only (`B/Game1.Flight.cs:398-408`) | S–M | grows with rounds in flight (4,143 in one 10 v 10 run) |
| L6 | No hit feedback: no hull sparks, shield flares, damage smoke, red damage wash (12) | not ported | `D:1796-1875`, `D:1634-1649`; HUD flash `G/Game1.Rendering.cs:3405-3411` | `_flashTracker` is declared (`B/Game1.Flight.cs:27`) and never used | M | small |
| L7 | No explosions: a killed ship just disappears (13) | not ported + **limit** | `D:1739-1785`; native tier is `ExplosionRenderer` (BasicEffect) | not called. `exp22bmp.mdl` (15,360 px wide) and `fx18bmp.mdl` are not in the pack; 15,360 px is above the 8,192 px some WebGL devices allow (`MAX_TEXTURE_SIZE`), so that film must be cut into tiles | M | short bursts |
| L8 | Missiles, missile exhaust, chaff, mines, probes, treasure are not drawn | not ported | `D:1336-1396`, `:1589`, `:1412-1435`, `:1454-1475`, `:1486-1510`, `:1522-1567` | not called; missile meshes not in pack (only their collision hulls) | M | small |
| L9 | Aleph: swirl only; no transit sparks, flash or glimpse (11) | not ported | `D:1598-1624`, `D:1927-1931` | `WarpRenderer` only (`B/Game1.Flight.cs:390-396`) | S | none |
| L10 | Station: mesh only; no lights, no build effect (11) | not ported | `D:1063-1066`, `D:1094-1114` | `B/Game1.Flight.cs:366-376` | S | none |
| L11 | No nav strobes; cloaked ships not faded | not ported | `D:1718-1723`; `D:794-796` | opacity fixed at 1 (`B/Game1.Flight.cs:356`) | S | none |
| L12 | Entry-sector rocks start at 256² and sharpen after launch | off (P1054 ruling, to fit the download budget) | originals from disk | `docs/BROWSER_HOSTING.md` | — | — |
| L13 | No HDR scene/tonemap, soft particles, volumetric or enhanced explosions | off + **limit** | on by default: `visuals.space.hdrPipeline` (`VisualFlags.cs:164-170`), soft particles (`G/Game1.GpuBudget.cs:24`), volumetric effects (`src/Allegiance.ClientCore/State/EffectsQuality.cs:45`) | disabled and logged at `B/Game1.Browser.cs:88`: the desktop `.mgfxo` shader bytecode is not KNI/WebGL content; a float scene target also needs `EXT_color_buffer_float` | L | real: extra full-screen passes |
| L14 | Bloom, FSR upscaling, MSAA, HD/PBR materials | off | **also off by default on the desktop** (`G/Game1.GpuBudget.cs:25-26`, `G/Rendering/UpscalingSettings.cs:13,22`, `G/Rendering/Antialiasing.cs:6`; no HD pack on this lane) | off | — | not a default-look gap |

### HUD

Both HUD renderers are shared and take one model. The browser fills very little of it:
`HudModelAdapter.Create(frame, _cameraMode, contacts: contacts)` (`B/Game1.Flight.cs:414`) against the desktop call at
`G/Game1.Rendering.cs:3605-3650`. The gaps are therefore the same in Classic and Modern.

| # | Difference (sheet) | Class | Desktop | Size |
|---|---|---|---|---|
| H1 | Radar shows ships only: no stations, alephs, asteroids, missiles, probes, mines, treasure (03, 09) | not ported | `DrawRadar`, `G/Game1.Rendering.cs:4262-4922` | M |
| H2 | No targeting at all: no target keys, selection bracket, lock, lead indicator, target pane or 3-D preview (09) | not ported | `G/Game1.Targeting.cs`, `G/Game1.TargetCommands.cs`; brackets `G/Game1.Rendering.cs:541-567`; preview `D:655` | L |
| H3 | No always-on minimap, no "Current location"; the map is a button and a drop-down (07) | not ported | `G/Game1.Rendering.cs:4203` | S |
| H4 | No chat, quick chat, kill/eject messages, tips (09, 12) | not ported | `G/Game1.Session.cs:1921`; `D:1972-1974` | M |
| H5 | No weapons/systems panel, ammo count, signature, mission clock (01) | not ported | model inputs at `G/Game1.Rendering.cs:3605-3650` | S–M |
| H6 | No pod or death overlays: nothing says you ejected, no oxygen countdown (10, 12) | not ported | `DrawEjectDeathOverlays`, `G/Game1.Rendering.cs:5012` | S |
| H7 | Shield gauge reads 0 % with a warning on the browser's scout, 100 % on the desktop's (02) | **unknown** — a different loadout (the browser never buys) or a bug; needs a browser run | — | ? |
| H8 | Sector names overlap on the map at 800×480 (07) | bug (layout at small size) | `G/Rendering/StarmapRenderer.cs` | S |

### Things a player cannot do in the browser

| # | Gap | Class | Desktop | Size |
|---|---|---|---|---|
| F1 | **Watch without flying.** | not ported (and the desktop has it only offline or from a parked ship) | `G/Game1.Director.cs`, `G/Game1.SkirmishObserver.cs:14` | S for a parked director camera; L for a real observer seat (server + both clients) |
| F2 | **Choose a ship or buy anything.** One button launches the current hull with its current parts (04) | not ported | `G/Game1.Hangar.cs:278`, `G/Game1.LoadoutV2.cs:27` | L |
| F3 | **Aim.** No target, no lead marker, no missile lock (H2) | not ported | as H2 | L |
| F4 | **Hear anything in flight** | off (audio engine excluded) | `G/Game1.Audio.cs:582-880`, `G/Audio/SoundManager.cs` | L (KNI audio voices + a streamed sound pack) |
| F5 | **Cockpit view and the other cameras.** Chase plus a fly-by of the nearest ship only | not ported | `G/Rendering/CameraMode.cs:35`, default is cockpit (`G/Game1.cs:682`) | S–M |
| F6 | **Talk to the team; see the score, team or game-over screens** | not ported / excluded (`GameOverScreenRenderer` is in the csproj exclusions) | `D:1993-2295` | M |
| F7 | **Command, invest, order, teleport, ripcord** | not ported | `G/Game1.Commanding.cs`, `G/Game1.Orders.cs` | L, low priority for test battles |
| F8 | **See incoming missiles or minefields** (L8, H1) — this one changes how a fight goes, not only how it looks | not ported | as L8 | M |

What does work, from source: joining, picking a side, launching, flying with keys/mouse/pad, firing the gun,
changing sector through an aleph, autopilot home, the server-driven eject into a pod with autopilot to base
(`G/Game1.Prediction.cs:142` → `G/Game1.Eject.cs:152`, both shared), and returning to the hangar state on docking
(`H/Pages/Menu.Online.cs:329`).

## Item 3 — performance

**Browser: not measured here.** The only browser numbers are from earlier stories with one browser ship and one or
two other ships, headless Chromium with software rendering, so **not GPU-representative**: frame gap p50 16.7 ms /
p95 33.4 ms at both 1920×1080 and 800×480 (`BROWSER_FLIGHT_P1051.md`), managed heap 54–140 MB across the P1054
sector tours (`managedHeapBytes` in `BROWSER_SECTOR_PACKS_P1054_assets/*.json`). There is no browser figure for 5 or
10 per side.

**Download sizes**, measured from a browser generation published from this checkout during the audit
(`scripts/publish-browser-flight.sh` with the nine checked-in traces):

| Part | Files | Raw bytes | Compressed |
|---|---:|---:|---:|
| Runtime and game code (`_framework`) | 77 | 17,282,369 | 6,384,919 gzip / 4,936,346 brotli |
| Page scripts, styles, notices | 48 | 1,064,384 | 163,612 gzip / 75,734 brotli |
| Join pack (needed before flight) | 601 | 32,536,380 | 7,564,835 gzip (P1054 audit figure) |
| Every pack file, all 13 sectors' originals | 604 | 250,774,472 | 55,423,543 gzip |

The four biggest code files: game 3.3 MB, runtime 3.0 MB, core 2.6 MB, CoreLib 1.8 MB (0.9 / 1.0 / 0.6 / 0.6 MB
brotli). The build is interpreted, not ahead-of-time compiled (no `wasm-tools` on the lanes;
`BROWSER_CLIENT_P1045.md:188`), which matters for the last paragraph below.

**Desktop**, same lane, llvmpipe software GL at 1920×1080, vsync off (`ALLEGIANCE_NOVSYNC=1`), from the client's
own `[frameprof]` lines (interval between frames, 5 s windows). Compare shape only, never the absolute numbers with a
browser or a GPU.

| Battle | Windows | Mean frame gap | Median of window p95 | Worst window p95 | Combat seen by the client |
|---|---:|---:|---:|---:|---|
| Docked, any size | 6 | 1.1–2.5 ms | — | — | none |
| 1 v 1, pilot in its home sector (it never reached the bots in three attempts) | 48 | 3.8 ms | 6.8 ms | 30.8 ms | none |
| 5 v 5, from arrival in the battle sector to the end (includes the return home in a pod) | 41 | 4.0 ms | 9.3 ms | 33.8 ms | 988 projectiles, 326 hits, 8 explosions |
| 10 v 10, in the battle sector | 40 | 7.4 ms | 16.3 ms | 33.3 ms | 4,143 projectiles, 325 hits, 39 explosions |

Shape: the mean roughly doubles from 5 to 10 per side and the typical p95 rises from about 9 to 16 ms, while the
worst window is about 33 ms at every size (single slow frames, not a trend). The 1 v 1 row is not a battle
measurement: with one bot per side the pilot's one-shot goto never got it to the fight, so it is an empty-sector
baseline.

The server overran its 50 ms tick three times per run (start-up only).

**What will grow badly with ship count in the browser** (from the code, not measured):

- `B/Game1.Flight.cs:346` looks up each ship's hull with a linear scan of the 447-entry hull list, per ship, per frame,
  and `:343` walks every ship in the mission, not only the sector. Under the interpreter this is the first thing to fix.
- `:261` and `:378` walk all 321 asteroids of the map every frame; there is no distance or frustum culling and no
  level of detail (the desktop has both), and each rock is its own draw call.
- Per-frame allocations: the contact and name lists (`:341-342`), a `HudContact` per ship, and a full warp array
  rebuilt every frame while the autopilot is on (`:246`).
- Once effects are added: projectiles (thousands of short-lived rounds in a 10 v 10) and exhaust particles per
  boosting ship are the two counts that scale with the fight.

## Item 4 — ranked fix plan

Ranked by "closer to the desktop look and feel per unit of work":

1. **Ships that are there must be visible** (L1). Build the radar contact before the mesh check and draw a stand-in
   until the mesh arrives; stream hull meshes on demand. Without this nothing else in a battle matters.
2. **Light and background** (L3, L4). Pass the sector light; clear to black; draw the default black sky and dust.
   Two days of work for the biggest change in overall tone.
3. **Engine glow, exhaust, ribbons** (L2). This is what makes distant ships readable and is the thing Domingo asked
   about by name.
4. **The fight itself** (L5–L8). Projectile sprites, hit sparks and shield flares, native explosions, missiles and mines.
5. **A usable combat HUD** (H1–H6, F3). Full radar, targeting and target pane, minimap, messages, pod overlay.
6. **Sound** (F4).
7. **Hangar and loadout** (F2).
8. **Watching** (F1).
9. **HDR, soft particles, volumetric effects** (L13).

Proposed tasks:

| Task | Scope | Main files | Size | Risks | Proof |
|---|---|---|---|---|---|
| **A — Battle host and visible ships** (items 1 and the missing browser half of this audit) | `--bots N` on `p1051-browser-host`; contact-before-mesh fix; on-demand hull, missile and effect-texture staging; the per-frame hull lookup and sector filter; then re-run this audit's captures and the 1/5/10 pacing in Chromium | `tools/p1051-browser-host/Program.cs`, `tools/p1051-browser-packs.py`, `tools/p1054-browser-packs.py`, `B/Assets/SectorPackStreamer.cs`, `B/Game1.Flight.cs:343-364` | M | Download budget (hulls must stream, not join the base pack); cache limit of 64 MiB / 1,024 entries | Chromium on a lane that has it; no GPU needed |
| **B — Shared scene pass** (items 2–4) | Move the desktop's native-tier scene drawing (sector light, black sky and dust, trails/glow/ribbons, strobes, projectile sprites, missiles/mines/probes, hit effects, native explosions, aleph and station extras) out of `Game1.Rendering.cs` into a source-shared partial that both clients call; tile `exp22` | `G/Game1.Rendering.cs` (`D:439-1931`), `G/Game1.FlightInput.cs:2598-2940` (emitters), `B/Game1.Flight.cs`, `B/…csproj` | L | The desktop must stay pixel-identical (the 1,848-case HUD comparison and filename manifest are the guard); interpreter frame cost | **Real GPU** for the browser frame cost and look; Chromium headless for correctness |
| **C — Combat HUD and messages** (item 5) | Feed the shared HUD model what the desktop feeds it: full radar, targeting and keys, target pane, minimap, chat/kill feed, systems panel, pod/death overlays; cockpit view | `G/Game1.Rendering.cs:3360-3650,4262-4922,5012`, `G/Game1.Targeting.cs`, `G/Game1.TargetCommands.cs`, `G/Game1.Session.cs:1921`, `H/wwwroot/flight-input.js` (key routing) | L | Browser key conflicts (Tab, F-keys; see `BROWSER_CLIENT_P1045.md` input table); both HUD styles must be tested and captured | Chromium, both styles, 1920×1080 and 800×480 |
| **D — Sound, hangar, watching** (items 6–8) | Flight sound through KNI audio with a streamed sound pack; the loadout screen; a parked director camera first, a real observer seat later | `G/Audio/*`, `G/Game1.Audio.cs`, `G/Game1.Hangar.cs`, `G/Game1.LoadoutV2.cs`, `G/Game1.Director.cs`, server for the observer seat | L (three M pieces) | Audio needs a user gesture and voice limits; the observer seat is a server rule with no native counterpart | Chromium; a person listening for the sound |

Order and parallelism: **A first** (it also restores the ability to measure). **B and D can run in parallel** after
A. **C should start after B has extracted the shared partial**, because both cut into `Game1.Rendering.cs`; C's
targeting and chat pieces that live in other files can start alongside B.

Flags. Nothing in A–D is a visual departure: it is the desktop's existing default look, so no `visuals.*` flag is
needed on the desktop and the desktop must not change at all. The browser does need **its own switch for effect
load** (not for look): a quality step that the existing frame-pacing fallback (`H/wwwroot/frame-pacing.js`) can lower
before it halves the resolution — exhaust particles and dust off first. Item 9 (L13) is the only piece that needs a
default-off switch and a real-GPU proof before it is shown to anyone; it should wait until A–C are in.

## Tools added, gate

- `tools/p1063-battle.sh` — the battle runner described under item 0.
- `tools/p1063-pair-images.py` — evidence sheets; stdlib only.

No product code changed, so no test run. The three Release builds the battle needs (server, bot driver, desktop
client) and the Debug client build the browser publish needs were 0 warnings / 0 errors. The two new tools are a
shell script and a Python script, so `tools/build-tools.sh` (C# tool projects) has nothing new to build and was not
run. Every process started here was stopped by exact PID by the runner's exit trap.

## Unfinished

- **The browser was never run** (no Chromium on this lane): no browser join, no same-moment pairs, no browser pacing
  or heap at 1/5/10 per side, and L1 and H7 are unconfirmed at run time. Task A covers all of it.
- No video clips (no encoder on this lane); frame strips instead.
- No desktop frame that clearly shows bolts, a missile or a fireball.
- The desktop 1 v 1 row is an empty-sector baseline, not a fight (see item 3).
