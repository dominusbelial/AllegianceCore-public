# P1073 — Browser gaps found on the live perpetual battle (generated maps, research briefing)

Lane: any (browser/visual task; an image-capable model). Base: master at or after `6bc0dade`. P1072 (WebSocket bots,
`tools/allegiance-mcp` + server metrics) may run in parallel, so do not edit `tools/allegiance-mcp`. Five-hour cap from
your start time. Report: `docs/reports/BROWSER_LIVE_GAPS_P1073.md`, written before implementation and kept current.

## Why

On 2026-11-22 the lobby VPS started a public perpetual 5v5 (P1071 preset):
- random civs each match;
- showcase `techMode: "research"`;
- map rotation over **generated** maps (`mapGen` SingleRing / PinWheel / DoubleRing).

Every earlier browser harness used the static `XXV_simple` map with fixed civs, so the gaps below were never reached. The first real
browser joins showed them:

0. **Fixed already (context, don't redo):**
   - Generated sectors carry 250–750 stars (`MissionMapGenerator.cs:383,408`), while XXV_simple sectors author 0.
   - KNI's WebGL `ConcreteGraphicsContext.PrimitiveTypeGL` throws `NotSupportedException` for `PrimitiveType.PointList`, which stopped `Game1` on the first flight frame.
   - Hotfix `6bc0dade`: under `BROWSER`, `SpaceStarRenderer` and `Starfield` expand points into tiny `LineList` dots (`BrowserStarDots`).
   - `NativeSkyRenderer.cs:241` still has a PointList draw. The browser doesn't construct it today, but it is the same trap.
1. **Research briefing dropped on every join.** The console shows
   `[recv] contained malformed message fmid=992 len=3836 error=PlatformNotSupportedException (dropped, session kept)`.
   - fmid 992 is `ResearchBriefing`, a port-only message; native has no counterpart, so cite that.
   - It is Brotli-compressed (`src/Allegiance.Core/Wire/ResearchBriefing.cs:31,44`).
   - Browser-WASM .NET ships no Brotli codec, so browser players never get the research plan the showcase sends.
2. **Generated-map aleph and planet textures fail in the browser:**
   `[warp] texture 'alephbmp' unavailable (InvalidDataException: Invalid asset name.)`, and the same for `plnt19bmp`.
   - The exception comes from `StagedManifest.Canonical` (`src/Allegiance.ClientCore.Game.Browser/Assets/StagedManifest.cs:16-20`).
   - It is caught at `WarpRenderer.cs:417-427`, which then uses the soft fallback.
   - Generated maps use `plnt19`/`plnt42` and the `nebplnt*` list (`MissionMapGenerator.cs:47-48`, `MapGenContext.cs:48-51`).
3. **Hangar Launch pane:** `[hangar] art 'animlaunch' missing (no such frame animation) — text-row hangar for that pane`.
   The art is not in any browser pack.
4. **HUD shows `AFTERBURNER ACTIVE` with `PWR 0%`** (`HudRenderer.cs:1912-1923`; seen in the P1069 review and live today).
5. **Invisible stations (Domingo, live, 2026-11-22).**
   - Domingo flew a Phoenix vs Iron Coalition match, was podded, and pressed "Dock at home station". The autopilot docked at a station he could not see.
   - The deployed generation packs only 695 of 3,582 artwork `.mdl` files. `p1_gar.mdl` (Phoenix garrison) and many other station models are absent.
   - `tools/p1065-battle-packs.py` `core_roots` (`:21-46`) packs every **hull** (core record kind 29) and **missile** (kind 23) but no **station types**.
   - Station art only arrives through the P1051/P1054 sector packs, which were traced on the fixed-civ XXV_simple map (civs 1/3).
6. **White, untextured asteroid (Domingo, live).** One asteroid type on generated maps renders fully white. It is visible
   in a headless guest's capture too (the bright white/blue rock beside the ship). Likely the same coverage gap for asteroid
   models or textures that generated maps emit.

## Items

### 1 — Research briefing reaches browser players
- First prove which codecs work in the browser runtime, in the **real browser** (Brotli vs Deflate vs GZip), with a
  test hook. Don't assume.
- Constraint: released desktop clients (≥ 0.1.7, the VPS minimum) must keep decoding what they get today.
- Options to evaluate, then pick one and say why:
  - (a) The server sends a browser-decodable encoding only to WebSocket peers, chosen by the per-peer transport
    (`IServerTransport.PeerProtocolName`, as P1071's play log does). Use an explicit format marker, never sniffing.
  - (b) A managed Brotli decoder in the browser client. No new package without a justification; licence and size
    noted in the report.
  - (c) Anything better you find.
- Size: the message must still fit one ushort-sized frame for the largest authored plan. Test the largest plan.
- If the choice changes the server, say so plainly in the report. A server change means a VPS server rebuild, which
  Claude does after asking Domingo.

### 2 — Generated-map textures in the browser
- Find the exact relative path that `WarpRenderer` (and any planet/backdrop renderer) passes for `alephbmp` and
  `plnt*bmp`, and why `Canonical` rejects it. Fix the name/path handling at its source; don't weaken
  `Canonical`'s rules (it is a security boundary for the staged pack store).
- Make sure every texture a generated map can reference has a verified pack entry:
  - `plnt19`, `plnt42`, the full `nebplnt*` list, `alephbmp`;
  - anything else `MapGenContext`/`MissionMapGenerator` can emit (enumerate them in a test).
- Extend the scene/sector pack tooling (`tools/p1054-browser-packs.py`, `tools/p1066-scene-packs.py`) instead of a new
  pipeline, and keep the publish script order in `scripts/publish-browser-flight.sh`.

### 2b — Stations and asteroids for every civ and every generated map
- Extend `core_roots` to read the **station type** records (find the core record kind and layout in native `Igc/igc.h` /
  the port's core reader; cite them). Give every station type of every civ a verified on-demand pack, loaded the
  same way hulls are, so that any random-civ matchup shows every station. Include the dependent textures (`ImportImageFromFile`).
- Do the same for every asteroid type / model / texture a generated map can place (`MapGenContext`, `MissionMapGenerator`)
  and for the static maps' asteroids. Find out why one asteroid draws fully white (missing texture, wrong name, or an
  unsupported texture format in the browser) and fix the cause.
- A test enumerates the core's station types × civs and the map generator's asteroid set and asserts that each one
  resolves to a pack entry. "No `.mdl` the client can request for a station/asteroid is missing" must be a test,
  not a manual check.
- Keep the join payload small: on-demand packs, requested when a sector or matchup needs them, as P1065 does for hulls.
  Report the new pack sizes, and the join/first-flight bytes before and after.

### 3 — `animlaunch` in the browser hangar
- Pack the native launch animation that the desktop hangar uses (find where the desktop loads it), so the browser
  Launch pane draws the native art instead of the text fallback.

### 4 — `PWR` while the afterburner is active
- Read native first: what the afterburner/power readout shows and where it comes from (cite file:line in the native
  HUD/`clintlib`).
- Fix the value source so the readout matches native in both HUD styles. Check whether the desktop has the same bug,
  and fix it there too if so (same shared code).

### 5 — Guard this class of bug
- A unit test for `BrowserStarDots` (shape and count of output), and a `BROWSER`-build test or analyzer-style check
  that no browser-reachable renderer calls `DrawUserPrimitives`/`DrawPrimitives` with `PrimitiveType.PointList`. Also
  guard `NativeSkyRenderer.cs:241` the same way as the hotfix.
- A browser harness, `tools/p1073-browser-generated.cjs`, modelled on `tools/p1068-browser-guest.cjs` and
  `tools/p1069-browser-engines.cjs`, plus the existing browser host (`tools/p1051-browser-host`). Boot the host
  with a **generated map** and showcase research mode (the P1071 example config's rotation entry), then join as a guest
  and fly 30 s in Classic and Modern at 1920×1080 and 800×480. It asserts:
  - no page errors;
  - no `[recv] … malformed` lines;
  - no `[warp] texture … unavailable` lines;
  - the research briefing is received;
  - every station and asteroid in the home sector has its model and texture resident (a test hook);
  - the "Dock at home station" autopilot ends docked at a **visible** station;
  - the hangar Launch art is present;
  - captures written.
- If the lane has no Chromium, write the harness, run everything else, and say so. Claude runs it on the
  workstation and opens every capture.

## Gate

Light gate:
- solution build 0/0 and `tools/build-tools.sh`;
- every new test 3× alone;
- `Allegiance.ClientCore.Browser.Tests`, `Allegiance.ClientCore.Game.Browser.Tests` and the Core wire tests;
- one full Fast sweep at the end;
- the existing P1066/P1068/P1069/P1070 harness scripts still build (Claude runs them).

Flakes go in `docs/TEST_FLAKES.md` with 3× isolated reruns. Use `$HOME/.dotnet/dotnet`, `NUGET_HTTP_CACHE_PATH` and
`--disable-build-servers`.

## Not in scope

- Any VPS, rig or lane-bots change, deploy or run.
- Changing what the server sends desktop clients, or the desktop wire format.
- Weakening `StagedManifest.Canonical`, the pack verification, or the CSP.
- New browser features beyond these four gaps. List anything else you notice in the report as a candidate.

## Common rules

- No commits, pushes, package installs or deploys; leave changes in the working tree.
- Never `git stash`; never `pkill -f`; stop only processes you started, by PID.
- Native first: cite native file:line for every native-behaviour claim. `ResearchBriefing` and the browser packs are
  port-only, so say so. Footer: "Native intent / Modern approach", both sections.
- HUD changes must work in Classic **and** Modern. Screenshot both.
- No home paths, hostnames, IPs or secrets in committed files.
- Report: what was done per item, tests with counts, anything unfinished and why.
