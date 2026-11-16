# P1045 — Study: the MonoGame client in a browser (KNI / WebAssembly), served by the lobby

Base: Claude's master at dispatch. Own worktree and branch. **Investigation + design report with two throwaway
spikes.** The final change set is ONLY the report and its assets: revert the spikes from the tree and save them as
`docs/reports/BROWSER_CLIENT_P1045_assets/spike-*.patch` (≤ 300 KB each). Rules: public-repo hygiene (no personal
paths/IPs/hostnames), **never `git stash`**, `~/.dotnet/dotnet`, never `pkill -f`/`pgrep -f`, no synthetic CPU load,
scratch in `.scratch/p1045/`, do not commit or push, **do not contact the rig, mini, Mac, the VPS, the public lobby or
any game server** — everything runs locally (loopback). Internet only for NuGet/npm/docs. Native reference
`/srv/corpus/Allegiance/src/`, artwork `/srv/corpus/Artwork` (read-only; `local.env` points at them). Read `AGENTS.md`
and `GLOSSARY.md` first; re-grep every line number before citing it.

**Time:** 6 hours max. Write the report as you go (section by section); stop spikes early rather than overrun.

## What Domingo wants (2026-11-16)
A community member said the biggest barrier is install friction: "I'd try a one-click web remake of Alleg but I won't
download anything to install & run." Domingo asked: "could we create a KNI flow using the public lobby? to load the
monogame client on a browser?" — i.e. open a URL on the lobby, the existing C# client (or as much of it as possible)
runs in the browser via WebAssembly, logs in through the lobby, lists games and joins one. The desktop client stays
as it is; the browser build is an additional target.

## Today (verify and extend — cite file:line)
- Client: `src/Allegiance.ClientCore.Game` (shared game code, already split for the Android target
  `src/Allegiance.ClientCore.Android`), `src/Allegiance.ClientCore.MonoGame` (MonoGame 3.8.5.1 DesktopGL, net10.0),
  NVorbis audio. What in the client touches the OS: files/artwork loading, threads/`Task.Run`/timers, sockets, native
  libraries, `Process`, clipboard, config/profile paths, shaders/effects (HDR P984, float render targets).
- Transport: `src/Allegiance.Core/Transport/` (`NetworkLink`, `UdpTransport`, `TcpTransport`,
  `NatTraversalClient`), `Allegiance.ClientCore.Game/Discovery/DiscoveryEngine.cs`. How cleanly could a third
  WebSocket (or WebTransport) transport slot in on both sides?
- Lobby: `src/Allegiance.Lobby` is an ASP.NET Web SDK app (`Program.cs`, `LobbyEndpoints.cs`, identity/chat/career/
  squad/event endpoints, `PunchSignaling.cs`). Which client↔lobby traffic is already plain HTTP (browser-safe) and
  which is not? CORS, auth tokens, TLS assumptions. Hardening from `docs/reports/R1_REPORT.md` and
  `R1B_CARBON_REPORT.md` that a browser entry point must keep.
- Prior art in the repo: `tools/vizfight/` (Kestrel + one-way JSON WebSocket spectator hub, `Program.cs:312-423`,
  Three.js page) — what of its hub design carries over to a two-way game gateway.

## Questions the report must answer
1. **Engine options**, compared with a recommendation (public sources, current versions — verify, don't recall):
   (A) **KNI** (MonoGame-API fork with a Blazor/WebGL backend), (B) MonoGame's own web/WebGL work if any exists and its
   status, (C) FNA + SDL/Emscripten, (D) a thin TypeScript/Three.js client over the same protocol (for comparison
   only). Porting effort for our code, what breaks (effects/shaders, render targets, HDR, input, audio), licence.
2. **Networking**: browsers cannot open raw UDP/TCP. Options: WebSocket gateway on the lobby host, WebSocket/
   WebTransport listener in `Allegiance.Server` as another `NetworkLink` transport, WebRTC data channels. Latency and
   head-of-line blocking vs native UDP for flight/combat; what must change in the server; what cannot work in a
   browser (LAN discovery, client hosting/NAT traversal) and how the UI hides it. Security: a new public entry point
   — auth, rate limits, message size caps, the R1/R1b rules.
3. **Lobby flow**: serving the WASM bundle from the lobby (static files, compression, caching headers, HTTPS/`wss`
   requirement, COOP/COEP headers if WASM threads are used), login → game list → join, and what the lobby needs
   (CORS, token handoff to the game server/gateway). Should the bundle be served by the lobby or a CDN?
4. **Artwork**: the full artwork is ~2.5 GB. Which subset does a first match need (measure: fonts, HUD, sky,
   effects, starter hulls, sounds)? On-demand loading + browser cache (IndexedDB / Cache API), format conversion
   (e.g. pre-packed atlases, compressed textures), first-load size target, licence check for serving the art
   publicly.
5. **Threads/runtime**: .NET WASM threading status in net10, AOT vs interpreter, GC, bundle size (trimmed), what
   async rewrites the client needs.
6. **Performance**: measure with the spike (below): frame time in Chromium (and Firefox if available) for a
   static sector scene at 1080p, bundle size, startup time. Estimate 200v200 feasibility honestly.
7. **Input/UX**: pointer lock for mouse flight, browser-reserved keys (Ctrl+W, F5, F11, Esc …) vs our bindings,
   fullscreen, focus loss, mobile (out of scope, say so).
8. **Plan**: phased slices (S/M/L) a delegate can build, each with acceptance criteria and tests, which lane suits
   each, and the risks. Phase 1 must be shippable behind a flag/separate build without touching the desktop client.

## Spikes (throwaway, local only)
- **Spike 1 — render:** a KNI (or the option you recommend) browser build that loads ONE real hull `.mdl` + texture
  via the existing `Allegiance.Core` parser (`Data/MdlFile.cs`) and draws it with a sky/background in a browser.
  Headless Chromium screenshot(s) at 1920×1080 and 800×480 into the asset folder (PNG, ≤ 2 MB total; read every
  image and describe what is actually visible). Record bundle size and frame time.
- **Spike 2 — network:** a minimal WebSocket path against a **local** `scripts/dev-stack.sh up --local` server: the
  browser (or a headless test harness using the same client code) logs on and receives world updates through a
  gateway or a server-side WebSocket transport, and sends one input. Measure round-trip on loopback. Stop the
  dev-stack when done (`scripts/dev-stack.sh down`).
If a spike cannot be finished in time, report exactly where it stopped and why.

## Report
`docs/reports/BROWSER_CLIENT_P1045.md`: current state with file:line, the options table and recommendation,
networking design, lobby flow, artwork plan, runtime/threads, measured spike numbers, input/UX, the phased plan, risks.
Footer sections: **"Native intent"** and **"Modern approach"**. Gate: tree clean except the report and its assets,
and `~/.dotnet/dotnet build AllegianceCore.slnx` still 0 warnings/0 errors after the spikes are reverted. Every
process you start must end before you finish.
