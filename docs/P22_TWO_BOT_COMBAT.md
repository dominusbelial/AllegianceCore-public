# P22 — real two-bot combat on the actual `Allegiance.Bot` console app

Date: 2026-08-30 · Branch `master` · Repo `/home/domingo/Development/AllegianceCore`

## What P22 does

P19 proved live combat through a bespoke test harness (`LiveCombatWireTests` +
its private `CombatClient`). P22 proves the **real, shippable bot** — the
`Bot` class behind the `allegiance.bot` console app — can observe combat. Three
deliverables:

1. `Bot.cs` learns to observe kills (`Kills` + `ResetKills`, mirroring the
   existing `Observations`/`ResetObservations` pattern).
2. A real two-bot combat demo, `tools/livecombat`, driving two real `Bot`
   instances through the actual combat code path.
3. A regression test (`LiveCombatWireTests.Real_Bot_class_observes_its_own_
   kill_over_the_wire`) so the new surface stays covered under the normal
   full-suite run, tagged `[Collection("LiveNetwork")]` per P20.

## 1 — `Bot.cs` observation surface

- `src/Allegiance.Bot/Bot.cs:33` — new `List<KillShip> _kills`.
- `Bot.cs:52-58` — `public IReadOnlyList<KillShip> Kills { get; }` and
  `ResetKills()`, locked the same way as `Observations` (snapshot-on-read,
  cleared on demand).
- `Bot.cs:146-148` — the `OnReceive` switch gained a `KillShip.Fmid` (105) case
  that decodes and records every `KILL_SHIP` the bot receives. Fields decoded
  from `Wire/KillShip.cs:68-87`: `ShipId` (victim), `TypeCredit`, `IdCredit`
  (killer), `SideIdKiller`, `BKillCredit`, `BDeathCredit`.

**Did I broaden `SingleShipUpdate` tracking to non-self ships? No.** Rationale:
the demo's proof needs only a real kill message, which `KillShip` alone
provides — that's the decisive event. Broadening the position feed to track an
*opponent's* hull would not, by itself, make the demo clearer, because P19
already established (finding 2 in `COMBAT_LIVE_WIRING.md`) that an **idle**
victim's falling hull is not streamed tick-by-tick anyway (the `SINGLE_SHIP_
UPDATE` broadcast is dirty-gated on position change). So tracking non-self
ships would only pay off for a *moving* target, which this demo deliberately
does not require. Keep `Bot`'s observation surface minimal; the kill is the
proof.

## 2 — the demo and the two big design decisions

**In-process server vs. an externally-run server.** The existing `allegiance.bot`
movement demo (README "Bots: How to run") assumes the server already runs
externally. That pattern **cannot** demonstrate a kill: the stock `server.json`
spawn hull ("Adv Miner", id 1) has zero energy, zero ammo, and no hardpoints, so
even a mounted weapon never fires (P19 finding 1). Demonstrating a real kill
therefore requires a fightable spawn, which the server only offers through its
internal `ShipSpawner` seam — and that seam is same-process-only (it's `internal`
`Server.cs:80`, friend-visible). So the demo boots the server in-process.

Consequence: two real `Bot` **instances** in the same process, not two OS
processes. The prompt explicitly sanctions this: driving the real `Bot` class
exactly as the console app does — connect, logon, wait for `SET_CLUSTER`,
`CommandControls` — is the "real bot framework" proof; `LiveCombatWireTests`
already proved two-socket real TCP works fine in one process. Running two
separate `allegiance.bot` OS processes would additionally require an external
fightable server (config changes to make the default hull combat-capable),
which is out of scope. The task's own guidance says don't over-engineer.

- `tools/livecombat/livecombat.csproj` — a `net10.0` exe referencing Core,
  Server, and Bot. Assembly name `livecombat`.
- `src/Allegiance.Server/Properties/AssemblyInfo.cs:4` — granted
  `InternalsVisibleTo("livecombat")` so the tool can reach the server's
  `ShipSpawner` seam (the only internal it touches; no server logic changed).
- `tools/livecombat/Program.cs`:
  - optional `[port]` arg, default `0` = ephemeral; `Config()` mirrors the
    tests' `Teams: 2` (Alpha/Bravo) setup.
  - `InjectFightSpawn` seeds a combat-capable hull: first-spawned ship at
    origin (the shooter, given the same `Power>0`, dispersion-0 weapon the
    server's own `MountCombatWeapon` picks), second at `z=-180` (the target).
    Roles are resolved by spawn position, not connection order — the two
    sockets race on which logon the server drains first.
  - attacker `CommandControls(ControlData.Neutral, ShipControlState.AllWeapons)`,
    target idles; server `TickSim()` until the target `Bot` observes its own
    death, with a `MaxTicks` timeout.

**Which killed whom is pinned down** by the same side-aware friendly-fire gate
that P21 added: the shooter and target sit on different sides (round-robined by
`SpawnDefaultShip` across `mission.Sides` — `Server.cs:523-529`), so the kill
is not miscredited as friendly fire.

## Verbatim demo output (run 1, port ephemeral → 36141)

```
[boot] static core 'PCore001.igc' loaded: version=990051329, 10 civilizations, 447 hulls, 143 station types
[boot]   civ    1  'Dreghklar'
[boot]   civ    2  'Technoflux'
[boot]   civ    3  'Ga'Taraan'
[boot]   civ    4  'Phoenix'
[boot]   civ    5  'Omicron'
[boot]   civ   18  'Gigacorp'
[boot]   civ   25  'Iron Coalition'
[boot]   civ   35  'Bios'
[boot]   civ  101  'Belters'
[boot]   civ  201  'Rixian'
[boot] (stub) cores.txt / ranks.txt / maps.txt NOT loaded — download & rank lists are out of scope
[boot] transport listening on 127.0.0.1:36141
[boot] game 'Combat Demo' ready: map=XXV_simple.igc, 13 sectors, 2 sides [Alpha, Bravo], 38 warps, 2 stations, 0 asteroids
[boot] server ready: 1 mission(s), 20 upd/s (period 50 ms). Ctrl-C to stop.
[demo] server listening on 127.0.0.1:36141
[conn] peer 1 'ATTACKER' joined
[conn] peer 2 'TARGET' joined
[launch] peer 2 'TARGET' got ship 8096 in sector 'Arrokoth' (Vector3 { X = 0, Y = 0, Z = 0 })
[logon] 2 'TARGET' validated=True -> 'Welcome TARGET' (server time 911, ship 8096)
[launch] peer 1 'ATTACKER' got ship 8097 in sector 'Arrokoth' (Vector3 { X = 0, Y = 0, Z = -180 })
[logon] 1 'ATTACKER' validated=True -> 'Welcome ATTACKER' (server time 911, ship 8097)
[demo] shooter bot ship 8096 at Vector3 { X = 0, Y = 0, Z = 0 }, target bot ship 8097 at Vector3 { X = 0, Y = 0, Z = -180 }
[demo] shooter commands AllWeapons (state=0x8000), holds nose-to-nose; target idles
[ctrl] 1 ship 8097 controls=(yaw 0.000 pitch 0.000 roll 0.000 thr -1.000) state=0x0
[ctrl] 2 ship 8096 controls=(yaw 0.000 pitch 0.000 roll 0.000 thr -1.000) state=0x8000
[combat] ship 8097 killed by ship 8096 at sim-t 3.099999999999997 — KILL_SHIP(105) -> 2 peer(s)
[demo] === KILL OBSERVED BY REAL BOT/console-app path ===
[demo] target bot 8097 observed its kill over the wire after 97 ticks (0.1s sim-paced)
[demo] KILL_SHIP(105) on target bot: shipID=8097 typeCredit=0 idCredit=8096 sideKiller=-1 bKill=False bDeath=True
[demo] KILL_SHIP(105) bytes = 0E-00-69-00-A1-1F-00-00-A0-1F-FF-FF-00-01
[demo] shooter bot observed the same kill credited to idCredit=8096
[shutdown] clean: 98 ticks, 0 overruns
```

(The target happened to draw ship 8096 at origin this run and the attacker
8097 at z=-180; role resolution put the shooter at origin, matching `[combat]
ship 8097 killed by ship 8096`. Bytes match P19's breakdown: `cbmsg=0x0E`,
`fmid=0x69(105)`, `shipID=0x1FA1(8097)`, `typeCredit=0`, `idCredit=0x1FA0(8096)`,
`sideKiller=0xFFFF(-1)`, `bKill=0`, `bDeath=1`.)

Run 2 (also clean, exit 0): kill after 94 ticks, ship 8097 by 8096, identical
wire bytes. Reliable across runs.

## Regression test

`LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`
(`tests/Allegiance.Core.Tests/LiveCombatWireTests.cs:341-406`) reuses the
existing class helpers and drives **two real `Bot` instances** (aliased
`FltBot`, `LiveCombatWireTests.cs:10`) through the console-app flow, asserting
`Kills` contains the target's `bDeathCredit` kill and the attacker's credited
kill. It is inside the class already tagged `[Collection("LiveNetwork")]`, so
it serializes against the other live classes per P20 — no new flakiness
introduced.

## Files changed

- `src/Allegiance.Bot/Bot.cs` — kill observation surface (`Kills`,
  `ResetKills`, `KillShip.Fmid` case), doc-comment update.
- `src/Allegiance.Server/Properties/AssemblyInfo.cs` — `InternalsVisibleTo("livecombat")`.
- `tools/livecombat/` — new combat demo (`livecombat.csproj`, `Program.cs`).
- `tests/Allegiance.Core.Tests/LiveCombatWireTests.cs` — new regression test +
  `using FltBot` alias.
- `AllegianceCore.slnx` — added the tool to `/tools/`.
- `README.md` — Bots section: combat-demo note + run instructions.

No changes to `Server.cs` / `Sector.cs` / `Ship.cs` / `Weapon.cs` /
`KillShip.cs` logic or encoding, and no existing test altered (the fight/spawn
helpers are shared, unchanged).

## Verification

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build      # 0 warnings, 0 errors
dotnet test       # 3 consecutive full-suite runs, 1520/1520 each time
dotnet run --project tools/livecombat   # verbatim output above (2 clean runs)
```

Full-suite (post-change) runs, all green:

```
RUN 1: Passed!  - Failed: 0, Passed: 1520, Skipped: 0, Total: 1520, Duration: 16 s
RUN 2: Passed!  - Failed: 0, Passed: 1520, Skipped: 0, Total: 1520, Duration: 17 s
RUN 3: Passed!  - Failed: 0, Passed: 1520, Skipped: 0, Total: 1520, Duration: 15 s
```

**Definition of green met**: build clean, tests green across three runs, and
the demo's captured output shows a real kill observed through the real `Bot`
code path — verbatim evidence, not a summary.
