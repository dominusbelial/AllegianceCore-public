# P418 — vizfight `attach`: a real spectator client on the SAME live server

Date: 2026-09-27
Branch: master (committed, not pushed — Domingo pushes)

## The goal

Domingo's real objective: a credible "proof the port works" showcase — the
browser spectator view and a real MonoGame client both showing the **same
live action on the same real server**, side by side. Every existing
`vizfight` mode (`showcase`/`explore`/`roster`/`proof`/`stress`) boots its
own private, self-contained in-process `Server` with its own private bots —
completely isolated from whatever real server a real MonoGame client connects
to. They could never show the same thing because they were never the same
game.

P418 adds a seventh, fundamentally different mode: `vizfight attach`, which
does **not** boot a server at all. It is a real spectator **client** that
connects to an external, already-running server over the same wire protocol
the MonoGame client uses (`Allegiance.ClientCore.ConnectionSession`), tracks
that server's live `ClientWorld`, and republishes what it actually receives
into the unchanged spectator page. Real `Allegiance.Bot` peers are then
choreographed against that same server through **wire-only** commands — the
only ones a real player's client has.

Because the spectator and the MonoGame client are both ordinary clients of
the same server (same side, same starting cluster), the browser and the
client render the same real ships, positions and (when they happen) kills.

## The `attach` mode

    vizfight attach <host> <port> <webPort> [bots]

* `<host>:<port>` — the server to attach to (e.g. `192.168.0.221 6073`), the
  exact host:port a real MonoGame client connects to.
* `<webPort>` — where Kestrel serves the unchanged `wwwroot/index.html` +
  WebSocket (same `SpectatorHub`/`HandleWs` machinery every other mode uses).
* `[bots]` — number of choreographed real Bot peers (default 8).

### The spectator client

1. `ConnectionSession.Connect(port, "AttachCam", host)` → real TCP.
2. `SendLogonRequest(...)` → wait for the real `LOGONACK`.
3. `SendPositionReq(sideId)` → real side-select, seats a throwaway spectator
   ship (investigated: a dedicated "just observe, don't seat a ship" login
   path does **not** exist; a seated spectator that never flies is the
   honest fallback, and it is what every other client does too).
4. `SendPlayerReady(...)` → the spectator readies like any player so the
   mission's real auto-start can fire.

Once seated, the spectator passively receives the server's live traffic into
its `ClientWorld` and the mode republishes it every ~20Hz through the
existing `SpectatorHub`:

* `tick` frames — ships + non-ship models (projectiles/missiles/chaff/
  mines/treasures) in the exact P44/P204 JSON the page already renders;
* `kill` frames — real `KILL_SHIP(105)` events drained off the wire;
* `sector` frames — lazily-discovered static geometry.

### The ClientWorld → spectator-JSON bridge

New pure, web-free translator
`src/Allegiance.ClientCore/State/SpectatorFrame.cs`. It is additive to
ClientCore and fully unit-tested (`SpectatorFrameLiveTests`) so the mapping
is verifiable without pulling in the web tool:

* `BuildTickFrame(world, hullName, simTime, ref modelCounter)` → the
  `tick` frame. Ships map: `id`=ShipId, `hull`=HullId→name via the caller's
  resolver, `side`=SideId, `pos`/`fwd` from `ShipEntity.Latest`, `hp`=
  `Latest.HullFraction`, `alive`=!IsDead. Models map from the wire entity
  tables (Projectiles/Missiles/Mines/Chaff/Treasures) to the same `kind`-
  discriminated entries `vizfight.SerializeModel` emits.
* `BuildKillFrame(KillShip, simTime)` → the `kill` frame
  (`{type,victim,killer,simTime}`), byte-compatible with `vizfight`'s.
* `BuildSectorFrame(world, clusterId, clusterName)` → the `sector` frame
  (warps/stations/asteroids), matching `vizfight.SectorMessageJson`.

`vizfight`'s `TickFrame`/`SpectatorHub`/`wwwroot/index.html` are otherwise
unchanged and reused as-is; only the ships/models source changes from an
owned `Srv` to a live `ClientWorld`.

`vizfight` gained a `ProjectReference` to `Allegiance.ClientCore` (the
previous claim that it already referenced `ClientCore.MonoGame` was **false**
in this tree — the csproj only referenced Core/Server/Bot — so the
reference is a real, needed addition).

### The wire-only bot choreography

Real `Allegiance.Bot` peers on the same external server, driven entirely
through real client-visible messages (no `Server.TryJoinSide`,
`SpawnNpcShip`, `ship.MountWeapon`, or treasure injection):

1. login + `SendPositionReq(side)` (side-select) — all peers on the
   spectator's side (see "why one side" below);
2. `SendPlayerReady` (ready-up) → the real auto-start countdown;
3. `SendBuyLoadout(fLaunch:true)` (launch/undock), armed with a real weapon
   part for the gunners;
4. `CommandControls(...)` weaving cruise with `AllWeapons` held by gunners →
   real continuous motion + real projectile stream visible in `models`.

## Which showcase beats needed re-derivation / were dropped

The self-hosted `showcase` arc has several beats that manipulate the
in-process `Server` directly. Against an external server **none of that
exists**, so each was traced and resolved honestly:

| showcase beat | how showcase does it | on an external server |
|---|---|---|
| side membership + leadership | `server.TryJoinSide(live, side)` | **Re-derived** — real `SendPositionReq(sideId)` (the P281 wire side-select) |
| ready-up / countdown | `SendPlayerReady` + real auto-start | Retained — same real wire messages |
| launch / loadout | `SendBuyLoadout(fLaunch:true)` | Retained — same real wire message |
| movement / fire | `FlyTo` + `CommandControls` + AllWeapons | Retained — same real wire messages |
| combat kill (Gamma miner drone) | `server.SpawnNpcShip(...)` (a real player cannot spawn NPCs) | **Dropped** — replaced by real projectile-spray + eject pods (see below) |
| flag pickup + secure | `PlaceShowcaseFlag(...)` (injects a flag Treasure into the sector) | **Dropped** — a real client cannot place a flag treasure wire-only |
| alliance / vote / donate | `SendChangeAlliance` / `#mutiny`+`SendVote` / `SendPlayerDonate` | Possible wire-only, but **not driven** — they need a reacting party and add nothing to the side-by-side "same game" proof |

### Why the whole fleet is on one side (honest architectural finding)

An opposing-side ship undocks into **its own side's home sector**, which is a
*different cluster* than the spectator's. A client only ever sees
same-side ships (anywhere) plus its own cluster (cross-side HEAVY_SHIPS_
UPDATE). So to make every choreographed ship appear in **both** the web view
and the MonoGame client's view at the same time, the fleet must share one
cluster — seating everyone on the spectator's side puts them all in that
side's home sector with the spectator.

The trade-off is that the friendly-fire gate then suppresses same-side hits,
so "combat" is real projectile-spray, not kills. That is the honest,
documented result: on an external production server where every side is in
the mission, a real cross-side kill is **not wire-only achievable** (player
ships eject to lifepods instead of dying). The KILL_SHIP→kill-frame path is
still fully wired through `SpectatorFrame` and is proven live by the
`SpectatorFrameLiveTests` unit test, where a real gunner killed a real
null-sided victim on a controlled in-process server and the wire's real
`KILL_SHIP` produced a real `kill` frame.

## Verification performed

### 1. Unit test — the ClientWorld→JSON bridge (real wire, real kill)

`tests/Allegiance.ClientCore.Tests/SpectatorFrameLiveTests.cs` boots a real
in-process server, two real `ConnectionSession`s (a weapon-mounted gunner on
side 0 and a null-sided victim), readies both to the real auto-start, and the
gunner shoots the victim to a real death. It then asserts:

* `BuildTickFrame` on the gunner's real world maps `id`/`hull`/`side`/`pos`/
  `fwd`/`hp`/`alive` correctly (side 0, real HullId→name, real rounded
  position).
* A real `KILL_SHIP` in the victim's world produces a real `kill` frame with
  `victim` and `killer` = the real ship ids.
* `BuildSectorFrame` emits a `sector` frame from the real EXPORT tables.

Passed.

### 2. Whole-solution build

`dotnet build AllegianceCore.slnx -c Release` → **0 warnings, 0 errors**.

### 3. Full test suite (2+ runs)

Ran `Allegiance.Core.Tests` and `Allegiance.ClientCore.Tests` multiple times.
Failures observed each run are exclusively the **pre-existing documented
live-wire timing flakes** that rotate every run (LiveMoneyTests,
Multi_mission_disconnect, LiveTurretGunnerBindingTests, LiveTreasurePickup,
LiveRipcord, LiveRelaunch, LiveLifepodEnduranceDisplay, ShipDeleteGone,
etc.) plus the **known P382 pristine-arena regression class**
(`JoinFlowLiveTests.EntityTable_TracksARealTickingServer` now sees the P382
persistent boot miner as a second entity). The pristine A/B (moving
`SpectatorFrame.cs` aside and rebuilding) reproduces the same two
ClientCore failures, confirming they are **not** introduced by P418. The new
`SpectatorFrameLiveTests` passed every run.

### 4. Live, side by side — same game, two views

**Local (fully controlled):** booted the real standalone
`Allegiance.Server` (m2xspiderw, non-debug, 2 teams) on :6093; ran
`vizfight attach 127.0.0.1 6093 7799 8`. The spectator seated (ship 8098,
side 0), 8 bots seated on side 0, real ready-up → auto-start, all launched,
fleet flew for minutes. The web WS stream carried **10–11 real ships** by
id with real hull names (Adv Scout/Adv Miner), sides and positions. Then a
real MonoGame client ran `--capture 127.0.0.1 6093 mgcap ...` against the
**same** server; it auto-selected side 0 and rendered its own ship plus
three live sensor contacts — the same fleet the web view showed. Both views
are clients of the same server; the attach spectator's 11-ship count jumped
to include the MonoGame client's ship the moment it joined.

**Production host:name from the brief (`192.168.0.221:6073`):** ran
`vizfight attach 192.168.0.221 6073 7799 6` from this box. The spectator
logged on and seated on the real production server; the 6 bots joined; the
web WS stream showed **9 real ships** — the spectator + 6 choreographed bots
**plus two pre-existing real ships already on that production server** (an
Adv Miner and an Adv Scout from prior activity). Confirmed the mode works
against the exact host:port a real MonoGame client connects to, against a
server that already has real players/ships on it. After the run my injected
clients were disconnected (attach killed → clean link-dead).

## Honest limitations / notes

* The web view is a real peer, so it sees only what any peer in the same
  cluster/side sees — not the entire multi-sector universe. That is the
  correct and honest semantic for a spectator.
* Cross-side combat (real kills) is not wire-only producible on an external
  server (players eject). The visible action is real motion + projectile
  spray; the kill pipeline is proven by the unit test.
* Hull-name resolution in the web view comes from decoding the standard
  `PCore001.igc` (HullId→name), the same core all modes and the demo server
  use. An unknown HullId falls back to `hull<id>` rather than crashing.
* The pre-existing uncommitted working-tree tweaks already present before
  P418 (four `ListenLocalhost`→`ListenAnyIP` edits enabling remote viewing,
  and showcase's loop broadcasting `TickFrame` instead of `ExploreTickFrame`)
  ride along in `Program.cs`; `docker/compose.server.json` (map→m2xspiderw)
  is left uncommitted.

## Files changed

* `tools/vizfight/vizfight.csproj` — add `Allegiance.ClientCore` reference.
* `tools/vizfight/Program.cs` — new `attach` dispatch + `AttachMain` +
  wire-only bot choreography (see the P418 region).
* `src/Allegiance.ClientCore/State/SpectatorFrame.cs` — new: ClientWorld→
  spectator-JSON translator (tick/kill/sector), pure and testable.
* `tests/Allegiance.ClientCore.Tests/SpectatorFrameLiveTests.cs` — new:
  live proof of the translation incl. a real `KILL_SHIP` → `kill` frame.

## How to run

    # against a real server a MonoGame client also connects to
    vizfight attach 192.168.0.221 6073 7799 8
    # open http://127.0.0.1:7799/  (or http://<this-host>:7799/) in a browser
