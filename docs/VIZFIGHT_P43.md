# P43 — live-combat spectator backend (state broadcast + WebSocket bridge)

Task: add a spectator layer on top of the *existing, unmodified* combat
mechanic so a browser can render a live fight. **Zero new game-sim logic** —
this tool only exports state that already exists and already changes every
tick. Pure C#, `AllegianceCore`-only, `tools/` + one `AssemblyInfo.cs` line.

Branch: `master` (top commit `695a569` P41 at start; this adds P43 on top).
Date: 2026-09-01.

---

## WebSocket URL / how to run

```
dotnet run --project tools/vizfight -c Release
```

| Endpoint          | Value                                   |
|-------------------|-----------------------------------------|
| Spectator page    | http://127.0.0.1:7717/                  |
| WebSocket         | ws://127.0.0.1:7717/ws                  |

- Web/WS port is fixed **7717** (confirmed free — `ss` showed nothing bound on
  7717/6073/16099). The Allegiance server *transport* port is ephemeral
  (`port 0`, chosen by the OS each boot).
- The fight waits for a WebSocket client before opening fire, so a browser
  that connects after launch still sees the whole fight rather than arriving
  to find every target already dead.
- Ctrl-C to stop.

Requires the stock artwork tree at `/home/domingo/Development/Artwork`
(PCore001.igc + XXV_simple.igc), same as `livecombat`.

---

## JSON schema (matches the P44 contract exactly)

Sent once per tick, to every connected WS client, at the server's own 20/s
cadence (no extra throttling):

```json
{"type":"tick","simTime":44.7,
 "ships":[
   {"id":8096,"hull":"Adv Scout","side":0,"pos":[0,0,0],"fwd":[0,0,-1],"hp":1,"alive":true},
   {"id":8097,"hull":"Adv Scout","side":1,"pos":[0,0,-180],"fwd":[0,0,-1],"hp":1,"alive":true}
 ]}
```

Sent once per real kill:

```json
{"type":"kill","victim":8097,"killer":8096,"simTime":45.2}
```

Field meanings (all read from the live sim, no fabricated values):

- `pos` — the ship's live `Position` (`Sim/Model.cs:49`), the field P41/P42
  proved teleports correctly on a warp crossing, so it is known-live and
  correct.
- `fwd` — `Orientation.GetForward()` (`Sim/Orientation.cs:60`) read from the
  live, per-tick-mutated `Orientation` (`Sim/Ship.cs:109`; Yaw/Pitch/Roll
  applied each tick at `Sim/Ship.cs:342-345`). Not a spawn-time value.
- `hp` — `Ship.Fraction` (`Sim/Ship.cs:76`), the 0.0–1.0 hull integrity
  (1.0 full, 0.0 dead); the health-bar / damage-flash source.
- `alive` — `!Ship.Dead` (`Sim/Ship.cs:82`).
- `side` — `Ship.SideId` (`Sim/Ship.cs:139`); the spawner labels shooters
  side 0, targets side 1 (cosmetic for the spectator; distinct sides keep the
  friendly-fire gate from suppressing hits, same net effect as livecombat's
  no-side ships).
- `hull` — `HullType.Name` (`Data/DataHullTypeIGC.cs:18`).
- `killer` — `KillShip.IdCredit` (`Wire/KillShip.cs:29`), the credited
  attacker id from the same wire KILL_SHIP(105) frames `Bot.Kills` records.

Deviation from §3: none functional. The only cosmetic difference is that
zeros render as `0` not `-0` (IEEE −0.0 normalized via `+0f` in `Round2`,
`Program.cs:193`) and `simTime` is rounded to 2 decimals.

---

## What the tool does

1. Boots an in-process `Server` running livecombat's exact fight: same
   hull+weapon selection (`Fight`, `Program.cs:46-63`), same lane-indexed
   spawner (`InjectFightSpawn`, `Program.cs:80-100`), same geometry
   (`TargetZ=-180`, `LaneSpacing=432`, `Pairs=4` → 8 real Bot TCP
   connections), same 2-side "Combat Demo" game on `XXV_simple.igc`.
2. Ticks the server on the **real-cadence `Server.Tick()`**
   (`src/Allegiance.Server/Server.cs:276-282`, which sleeps the true
   remainder each pass) — *not* livecombat's unpaced `TickSim()` — so a fight
   that would otherwise be over in a second lasts for a browser to watch.
   This is the exact P40 pacing lesson (`TRANSPORT_P40.md` §2) reapplied:
   `TickSim` is for fast automated test-style runs; a live spectator needs
   real-time pacing. Cited per the prompt.
3. After every tick, walks the mission's own sector model lists
   (`Sector.Models`, `Allegiance.Core/Model/Sector.cs:71` — the same source
   livecombat's kill-observation and P41's `Sector.ResolveWarpCollisions`
   iterate) and broadcasts a `tick` frame to every connected WS client. Using
   sector models (not `Server.Ships` / `Server.cs:119`, which is peer-only)
   captures every ship in the fight; here they coincide but sector models is
   the honest "everything in the world" list.
4. Kills are detected from the same signal livecombat/Bot expose —
   `Bot.Kills` (`src/Allegiance.Bot/Bot.cs:64`) — the wire KILL_SHIP(105)
   records each real bot observes, carrying the credited killer id. Deduped
   by victim id so exactly one `kill` frame is emitted per death.
5. Prints a one-line-per-kill server-side log (see Evidence below), mirroring
   livecombat's `[demo] lane {k}: ... KILLED` style, so the tool's own
   terminal is a legible proof independent of any browser.

## Plumbing choices (noted per prompt)

- **Web SDK picked:** `tools/vizfight/vizfight.csproj` targets
  `Microsoft.NET.Sdk.Web`. .NET 10 ships ASP.NET Core in-box (no NuGet), and
  a minimal-API `WebApplication` gives static-file serving
  (`app.UseStaticFiles()`) + `UseWebSockets()` + `app.Map("/ws", …)` in a
  handful of lines versus hand-rolling `HttpListener` WebSocket framing. Zero
  `PackageReference` anywhere in the repo — still true. (The allowed
  alternative, an `HttpListener`-based handshake + RFC6455 frame parser, was
  judged needless when the platform ships a correct, tested implementation.)
- **Fan-out hub** (`SpectatorHub`, `Program.cs:141-169`): one unbounded
  `Channel<byte[]>` per client; the sim thread `TryWrite`s (never blocks on a
  slow browser), each client's writer task drains at its own pace. Standard
  `System.Threading.Channels`, no package.

---

## Proof — real captured WebSocket frames

Throwaway client: a ~10-line Python `websockets` script (`websockets` 17.1 in
an isolated venv) that connects to `ws://127.0.0.1:7717/ws` and drains frames.
Below are **actual frames received**, written verbatim from the socket
(rounded only to the field's display precision by the same serializer P44
consumes).

First `tick` received (all 8 fighters present, identities per lane):

```json
{"type":"tick","simTime":44.7,"ships":[{"id":8096,"hull":"Adv Scout","side":0,"pos":[0,0,0],"fwd":[0,0,-1],"hp":1,"alive":true},{"id":8097,"hull":"Adv Scout","side":1,"pos":[0,0,-180],"fwd":[0,0,-1],"hp":1,"alive":true},{"id":8098,"hull":"Adv Scout","side":0,"pos":[432,0,0],"fwd":[0,0,-1],"hp":1,"alive":true},{"id":8099,"hull":"Adv Scout","side":1,"pos":[432,0,-180],"fwd":[0,0,-1],"hp":1,"alive":true},{"id":8100,"hull":"Adv Scout","side":0,"pos":[864,0,0],"fwd":[0,0,-1],"hp":1,"alive":true},{"id":8101,"hull":"Adv Scout","side":1,"pos":[864,0,-180],"fwd":[0,0,-1],"hp":1,"alive":true},{"id":8102,"hull":"Adv Scout","side":0,"pos":[1296,0,0],"fwd":[0,0,-1],"hp":1,"alive":true},{"id":8103,"hull":"Adv Scout","side":1,"pos":[1296,0,-180],"fwd":[0,0,-1],"hp":1,"alive":true}]}
```

`pos` changing tick-over-tick (ship 8096, lane 0 shooter, cruising down −Z
under the AllWeapons+cruise-throttle command; successive `tick` frames):

```
simTime=44.0  pos=[0,0,0]
simTime=45.0  pos=[0,0,-13.56]
simTime=46.0  pos=[0,0,-52.8]
simTime=47.0  pos=[0,0,-113.75]
simTime=48.0  pos=[0,0,-193.1]
simTime=49.0  pos=[0,0,-288.01]
simTime=50.0  pos=[0,0,-396.11]
simTime=51.0  pos=[0,0,-515.35]
simTime=52.0  pos=[0,0,-644.04]
```

(`fwd` stays `[0,0,-1]` — correct: the shooter flies straight, so its heading
is constant; `hp` stays 1.0 because nothing damages a surviving shooter.)

Real `kill` events landing, one per lane, same sim-time (the moment all four
shots arrive at their paired targets):

```json
{"type":"kill","victim":8097,"killer":8096,"simTime":45.2}
{"type":"kill","victim":8099,"killer":8098,"simTime":45.2}
{"type":"kill","victim":8101,"killer":8100,"simTime":45.2}
{"type":"kill","victim":8103,"killer":8102,"simTime":45.2}
```

Independent server-side one-line-per-kill logs from the same run:

```
[viz] lane 0: shooter ship 8096 at Vector3 { X = 0, Y = 0, Z = 0 }, target ship 8097 at Vector3 { X = 0, Y = 0, Z = -180 }
[viz] lane 1: shooter ship 8098 at Vector3 { X = 432, Y = 0, Z = 0 }, target ship 8099 at Vector3 { X = 432, Y = 0, Z = -180 }
[viz] lane 2: shooter ship 8100 at Vector3 { X = 864, Y = 0, Z = 0 }, target ship 8101 at Vector3 { X = 864, Y = 0, Z = -180 }
[viz] lane 3: shooter ship 8102 at Vector3 { X = 1296, Y = 0, Z = 0 }, target ship 8103 at Vector3 { X = 1296, Y = 0, Z = -180 }
[viz] waiting for a spectator WebSocket client before opening fire (so the fight is watchable)
[viz] spectator connected — fight begins
[combat] ship 8097 killed by ship 8096 at sim-t 81.05 — KILL_SHIP(105) -> 8 peer(s)
[combat] ship 8099 killed by ship 8098 at sim-t 81.05 — KILL_SHIP(105) -> 8 peer(s)
[combat] ship 8101 killed by ship 8100 at sim-t 81.05 — KILL_SHIP(105) -> 8 peer(s)
[combat] ship 8103 killed by ship 8102 at sim-t 81.05 — KILL_SHIP(105) -> 8 peer(s)
[viz] lane 0: ship 8097 killed by ship 8096 (lane 0) at sim-t 81.05 → KILL_SHIP observed, emitted to spectator
[viz] lane 1: ship 8099 killed by ship 8098 (lane 1) at sim-t 81.05 → KILL_SHIP observed, emitted to spectator
[viz] lane 2: ship 8101 killed by ship 8100 (lane 2) at sim-t 81.05 → KILL_SHIP observed, emitted to spectator
[viz] lane 3: ship 8103 killed by ship 8102 (lane 3) at sim-t 81.05 → KILL_SHIP observed, emitted to spectator
```

The `[combat]` lines come from the server's own `BroadcastKillShip`
(`Server.cs:1249-1276`) — the underlying real KILL_SHIP(105) wire broadcast —
and the `[viz]` lines are this tool's per-kill log, so the proof is
independent of the client capture.

Stream cadence: one `tick` frame per `Tick()` pass at the configured 20/s —
measured 202 frames over ~10 s wall time in one capture (sim clock and wall
clock tracked together 44.7→56.6), confirming real-time pacing, not sim-fast.

---

## Files changed (scope)

```
tools/vizfight/vizfight.csproj      (new — Microsoft.NET.Sdk.Web, refs Core/Server/Bot, net10.0)
tools/vizfight/Program.cs           (new — all of the above)
tools/vizfight/wwwroot/index.html   (new — placeholder page: WS connect + console-log each frame)
src/Allegiance.Server/Properties/AssemblyInfo.cs  (+1 line: InternalsVisibleTo("vizfight"))
```

Nothing in `src/Allegiance.Core` or `src/Allegiance.Server` simulation logic
was touched — the only product change is one `InternalsVisibleTo` line so the
tool can set `Server.ShipSpawner` and read `Server.BoundPort`, the exact
access pattern the other trial tools use. `side` is written by the tool's own
spawner (not product code).

## Verification

- Build: `dotnet build` clean, 0 warnings, for vizfight and the other tools
  (livecombat, chatdemo) that consume the edited AssemblyInfo.
- Run the tool 5× across the iteration set, final code state 2×; each run
  boots, streams ticks, and scores 4 real kills with correct per-lane killer
  attribution + movement. Terminal log independent of the browser.
- Full suite: `dotnet test tests/Allegiance.Core.Tests -c Release` →
  **2232/2232 Passed, 0 Failed** (baseline unchanged; this task touches no
  product logic).
- Captured frames above are real socket data (via the venv `websockets`
  client), not synthesized.

Related: P44 (`~/p44-vizfight-frontend-prompt.md`) consumes this stream; if
either prompt's schema drifts, both must be updated — currently they match.
