# P59 — live event feed in the spectator UI

## Status
Implemented. Build clean (0 warnings / 0 errors) and `dotnet test` 3 consecutive
clean runs, each **2263 passed / 0 failed** — identical to the pre-change
baseline (2263 passed before any edit). Committed on `master`.

## What this adds
The spectator page (`tools/vizfight/wwwroot/index.html`) previously rendered
only ship positions/geometry — the economy arc (dock, undock, mining, selling,
payday, warp crossings) was observable only by reading `Console.WriteLine`
server logs over SSH. This adds a small scrolling panel (bottom-right,
`#events` / `#eventsFeed`) that shows the same notable moments live in the
browser as `{ type = "event", text, simTime }` messages over the existing
WebSocket — no new transport, no new endpoint, same hub the `tick` and `kill`
messages ride.

## How it's wired (mirroring the existing `Payday` / `ShipDocked` hooks)
The seams the codebase already uses (`ServerHost` exposes `Action` hooks that
`Server` subscribes to at boot; the sim reaches the host through `IIgcSite`
default-site methods, cf. `MineAsteroidEvent`/`PaydayEvent`) were extended,
not reinvented:

1. `IIgcSite.ReportNotable(string)` — new C#-only default no-op relay, taking
   the **ready-made** text line so the feed can never drift from the console
   log (the same string handed to `Console.WriteLine` doubles as the feed text).
   Added in the file's existing "C# only — NOT part of the C++ seam"
   instrumentation section.
2. `ServerHost.Notable` (Action<string>?) + `ServerHost.ReportNotable`
   (site override → `Notable?.Invoke`), mirroring the `ShipDocked`/`Payday`
   hook declarations.
3. `Server.Notable` (public event Action<string>?) + `OnNotable` handler,
   wired in `BindHostAndTransport` (`_host.Notable = OnNotable;`), mirroring
   `_host.Payday = OnPayday;`.
4. Raised alongside the existing `Console.WriteLine` at each notable log site
   (same text, `Console.WriteLine` calls preserved verbatim):
   - `ServerHost.cs`: `[mine-sell]` (sell), `[dock]` (docked), `[warp] ... crossed`
   - `Server.cs`: `[undock]` (ship undocked), `[payday] ... -> PAYDAY(114)`
   - `Sector.cs`: `[mine] ... began mining`
   Only the actual-occurrence lines are surfaced; the debug/refusal lines
   (`— ignored`, `— refused`, `0 players — kept in treasury`, `nothing to pay`,
   `pure-sim transit`) are deliberately NOT forwarded — they aren't moments.
5. `tools/vizfight/Program.cs` `ExploreMain` subscribes `server.Notable +=
   text => hub.Broadcast(Serialize({ type="event", text, simTime=Round2(SimNow) }))`,
   mirroring the combat kill-broadcast site's exact structure (type + simTime,
   same JSON opts, same sim thread).
6. `wwwroot/index.html`: `#events` panel (fixed, bottom-right, styled to match
   the other HUD panels) appends each `event` message as one line, capped at
   30 lines (`appendEvent` drops the oldest once `childElementCount > 30`).

## What is proven (no VM)
- All six target sites now fire the event and forward to the spectator hub —
  verified by build + review of each raise site, not by a live demo (see
  below).
- Nothing that already worked is regressed: the full suite (2263 tests) still
  passes identically to baseline; combat-mode wiring, ship rendering/sector
  focus, and the kill feed are untouched (combat mode explicitly out of scope
  for this task, and no kill-path code changed).

## What Claude Code's live-VM pass should check
Because the event feed is emitted inside `vizfight`'s infinite loop and there
is **no existing test in this repo that asserts on the spectator WebSocket
message shape** (`LiveCombatWireTests` covers the wire `KILL_SHIP` frames via
`Bot.Kills`, not the hub's `kill` message) — matching the task's caveat, there
was no test precedent to extend, so build-clean + reasoning is the bar here.
Please verify live on the standing demo:

1. Feed populates with real text. While the running explore demo is up
   (`vizfight --explore`, outside bots from P46 loopback Bot or real Wopr),
   watch the bottom-right panel — it should show `[dock]`, `[undock]`,
   `[mine]` (began mining), `[mine-sell]`, `[payday] ... -> PAYDAY(114)`,
   and `[warp] ... crossed` lines, matching the SSH server log line-for-line.
2. Scrollback cap. Generate (or let run) > 30 events and confirm the feed
   stops growing at 30 lines, oldest dropped first, no layout break.
3. Non-regression in the browser: ship cones still move in the focused sector,
   sector dropdown/focus and geometry still work, and combat mode
   (`vizfight` fight view) still renders its kill feed unaffected.

## Files touched
- `src/Allegiance.Core/Sim/IIgcSite.cs` (new `ReportNotable` default)
- `src/Allegiance.Server/ServerHost.cs` (`Notable` hook + `ReportNotable`
  override + raises at dock/mine-sell/warp-crossed)
- `src/Allegiance.Server/Server.cs` (`Notable` event + `OnNotable` wiring +
  raises at undock/payday)
- `src/Allegiance.Core/Model/Sector.cs` (raise at mine-start)
- `tools/vizfight/Program.cs` (subscribe + forward in `ExploreMain`)
- `tools/vizfight/wwwroot/index.html` (panel + JS handler)
