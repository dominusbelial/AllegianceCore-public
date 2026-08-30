# COMBAT_LIVE_WIRING — wire combat into the live server/bot loop (P19)

Date: 2026-08-30 · Branch `master` · Repo `/home/domingo/Development/AllegianceCore`

## Background — what the prompt's recon got right (and one thing it got wrong)

The prompt correctly identified that the *simulation* half of combat was already
complete and unit-tested: weapons fire, projectiles collide, hulls drop, ships
die, and `FM_S_KILL_SHIP(105)` round-trips. What was missing was the **live
server wiring** — the two gaps below.

**One recon claim was wrong.** The prompt asserted "*Server.cs never references
IIgcSite at all ... whatever site the live server's missions use is the default
no-op (NullIgcSite)*". That is not the case at HEAD:

- `Server.cs:242` binds every world mission to the **live `ServerHost`**:
  `Mission.Assemble(core, map, sides, _host)`.
- `ServerHost.cs:42` declares `internal sealed class ServerHost : IIgcSite,
  ITransportSite` — a real, observing site, not a `NullIgcSite`.

So the site seam already existed and was already live. What was missing was that
`ServerHost` implemented **no** `IIgcSite` member override — every callback
(including `KillShipEvent`) fell through to the interface no-op default. Gap 2
below is therefore "make `ServerHost` implement the one callback that matters",
not "construct a new site".

---

## Gap 1 — spawned/assigned ships had zero weapons mounted

`Ship.MountWeapon` (`src/Allegiance.Core/Sim/Ship.cs:127-137`) is public and
explicit; nothing called it automatically. `Server.SpawnDefaultShip`
(`src/Allegiance.Server/Server.cs:503-520`) built and placed the ship and
stopped there, so a live bot's assigned ship had no weapon to fire regardless of
the `StateM` bits it sent.

### Fix — `Server.MountCombatWeapon` (`Server.cs:526-550`)

After `mission.PlaceShip(...)` in `SpawnDefaultShip` (`Server.cs:512`), the
ship is armed by `MountCombatWeapon(ship)`. The selection mirrors the proven
`CombatDeathTests` pattern (`CombatDeathTests.cs:79-84`), strengthened to pick a
weapon that can actually hurt:

- `EquipmentType.Weapon` part types only;
- **dispersion 0** so a straight shot lands (`w.Dispersion == 0f`);
- whose **projectile exists in the core** *and* has **`Power > 0f`** — a real
  damage dealing weapon. This last filter was added after the live test exposed
  it: the naive first-match selection in `PCore001.igc` picks `PW Mini-Dis`
  (`PartTypeIGC.id …`, `projectileTypeId 68`), whose projectile has `Power = 0`
  and thus fires forever but never damages anything. `CombatDeathTests` had
  always enforced `pt.Power > 0f`; the server's first cut did not.
- ordered by `EnergyPerShot` ascending (cheapest to feed).

`DataPartTypeIGC` is the parent record (carrying `PartId`, `Name`); the weapon
fields live in its `Weapon` subtype (`DataPartTypeIGC.cs:80-93`), reached via
`p.Type` cast, so `MountCombatWeapon` selects the parent and extracts `.Type`.
Mount goes through `Ship.MountWeapon` (public, `Ship.cs:127`), so a placed,
armed ship fires from the first tick once selected and off-cooldown.

`AssignShip` (`Server.cs:489-495`) routes through `ShipSpawner` when one is
injected (the test seam), else `SpawnDefaultShip`; the default path is where the
weapon is mounted, so a custom `ShipSpawner` keeps full control of its own
loadout (unchanged — the flight tests inject `ShipSpawner` and are untouched).

**Verification:** the live server log shows the mount on a real spawn
(verbatim below, §Live evidence A).

---

## Gap 2 — nothing broadcast `KILL_SHIP` (or any damage) over the wire

The simulation's death hook `IIgcSite.KillShipEvent` (`IIgcSite.cs:141-143`) was
fired by `Ship.ReceiveDamage` (`Ship.cs:237`) when a hull reached zero, but
`ServerHost` did not override it — so the default no-op swallowed every death.
A ship could die in simulation and no connected peer would ever hear about it.

### Fix — `ServerHost` implements `KillShipEvent` + `Server.BroadcastKillShip`

- `ServerHost.cs:65-73` — a new optional hook
  `Action<double, IShipIgc, IModelIgc?, float, Vector3, Vector3>? ShipKilled`
  (the owning `Server` subscribes; null by default, so a headless host that has
  not installed a broadcaster behaves exactly as before).
- `ServerHost.cs:76-77` — `public void KillShipEvent(...)` raises that hook.
  This is the one `IIgcSite` member this host cares about; every other member
  keeps the interface default. Declared public (not explicit-interface) so the
  hook and the override live together — a class member shadows the interface
  default in dispatch (same technique as `RecordingIgcSite.cs:20-23`).
- `Server.cs:176` — `BindHostAndTransport` sets `_host.ShipKilled =
  BroadcastKillShip` once at boot.
- `Server.cs:563-588` — `BroadcastKillShip(now, ship, launcher, …)`:
  - resolves the victim to a `Ship` (a non-ship victim is logged and ignored);
  - resolves the credited attacker id (launcher as a `Ship`, else NA −1);
  - targets **peers whose ship is in the victim's sector** (they can see the
    kill) — matches the broadcast intent without a LOS model;
  - builds `KillShip.FromVictim(victimId, IgcObjectType.Ship, killerId)`
    (`Wire/KillShip.cs:38-47`, fmid 105, fixed 14 bytes) and sends it to each
    target via the central `SendPeer` path (so it passes through `--capture`);
  - runs on the simulation thread (the site raises it inside the mission tick,
    same thread that owns `_peerShips` and broadcasts ship states), so no extra
    locking is needed.

The `KillShip` wire encoding itself is untouched (`KillShip.cs` was already
correct and round-trip tested).

**Verification:** the live two-party test observes a real `KILL_SHIP(105)`
arrive at both bots (verbatim below, §Live evidence B).

---

## Live test — a real kill over the wire

`tests/Allegiance.Core.Tests/LiveCombatWireTests.cs` is a **real two-party test
against the real server** — same recipe as the flight suite: a real `Server`
instance boots in-process, and two real loopback-`TcpTransport` bot clients
connect, log on, and exchange real encoded/CRC'd messages. It does **not** mock
the sim or the transport.

- `InjectFightSpawn` wires a deterministic fightable spawn via the server's own
  `ShipSpawner` seam (the exact seam `BotFlightTests` uses): first-spawned ship
  at origin (the shooter, armed via the same weapon-selection logic the server's
  default path now uses), second at z=−180 (the target). This seam is used
  *because* the stock `server.json` default spawn uses the core's **first hull
  by id — "Adv Miner"** (see finding 1), which cannot fire.
- The shooter sends `FM_C_SHIP_UPDATE(24)` with `AllWeapons` (`StateM=0x8000`,
  `ShipControlState.AllWeapons = 32768`, `ShipControlState.cs:42`) and no
  thrust; the target idles. Roles are resolved by spawn position, not socket
  arrival order (the two client sockets race on which logon the server drains
  first).
- The server ticks real chunks; the weapon fires real projectiles; the collision
  pass resolves real hits; the target's hull reaches zero; the server
  broadcasts `KILL_SHIP(105)`.

### Live evidence A — Gap 1 on a real external server (verbatim)

External `dotnet run` server + two real `allegiance.bot` clients (port 6099).
The server mounted a real weapon on every validated spawn:

```
[boot] transport listening on 127.0.0.1:6099
[conn] peer 1 'SHOOTERBOT' joined
[combat] ship 8096 armed with weapon 'PW Mini-Dis' (projectile 68, 0/shot, mount 0)
[launch] peer 1 'SHOOTERBOT' got ship 8096 in sector 'Arrokoth' (Vector3 { X = 720, Y = 730, Z = 920 })
[conn] peer 2 'TARGETBOT' joined
[combat] ship 8097 armed with weapon 'PW Mini-Dis' (projectile 68, 0/shot, mount 0)
[launch] peer 2 'TARGETBOT' got ship 8097 in sector 'Arrokoth' (Vector3 { X = 1440, Y = 730, Z = 920 })
[shutdown] clean: 36070 ticks, 0 overruns
```

*(That capture was taken with the pre-fix binary, hence the Power-0 `PW Mini-Dis`;
the committed `MountCombatWeapon` additionally filters `Power > 0f`, so a
rebuilt server arms a damaging weapon instead.)*

### Live evidence B — real kill + KILL_SHIP over the wire (verbatim, xunit)

From `LiveCombatWireTests.Real_two_party_combat_produces_a_kill_ship_over_the_wire`
(detailed logger):

```
[logon] 1 'SHOOTER' validated=True -> 'Welcome SHOOTER' (server time 908, ship 8096)
[logon] 2 'TARGET' validated=True -> 'Welcome TARGET' (server time 908, ship 8097)
[ctrl] 2 ship 8097 controls=(yaw 0.000 pitch 0.000 roll 0.000 thr -1.000) state=0x0
[ctrl] 1 ship 8096 controls=(yaw 0.000 pitch 0.000 roll 0.000 thr -1.000) state=0x8000
[combat] ship 8097 killed by ship 8096 at sim-t 2.1500000000000004 — KILL_SHIP(105) -> 2 peer(s)
LIVE-COMBAT: target 8097 killed over the wire after 62 ticks
LIVE-COMBAT: KILL_SHIP(105) on target client: shipID=8097 typeCredit=0 idCredit=8096 sideKiller=-1 bKill=False bDeath=True
LIVE-COMBAT: KILL_SHIP(105) bytes = 0E-00-69-00-A1-1F-00-00-A0-1F-FF-FF-00-01
LIVE-COMBAT: target wire hull fraction fell to 1 before death
```

*(The exact kill time/ticks vary run to run — 62-63 ticks ≈ 2.0-2.3 s sim —
because the cooldown gate and cadence scheduling are timing-dependent; the
assertions and the wire bytes are stable.)*

Breakdown of the wire bytes: `cbmsg=0x0E(14)` `fmid=0x69(105)` `shipID=0x1FA1(8097)`
`typeCredit=0(OT_ship)` `idCredit=0x1FA0(8096)` `sideKiller=0xFFFF(−1)` `bKill=0`
`bDeath=1` — exactly the `KillShip` layout (`KillShip.cs:8-15`).

The second test, `Moving_target_streams_its_falling_hull_over_the_wire`, drives
a moving target and asserts the falling hull is streamed via `SINGLE_SHIP_UPDATE`
`HullFraction` (`ServerShipUpdate.FromModel:118` fills it from `Ship.Fraction`).

### Findings flagged for follow-up

1. **The stock `server.json` default spawn hull cannot fight.** The default
   spawner picks the core's first hull by id — **"Adv Miner"** (id 1): `MaxEnergy
   = 0`, `MaxAmmo = 0`, `Hardpoints.Length = 0`. Even with a damaging weapon
   mounted, such a ship has no energy and no ammo, so `FireWeapon`'s energy and
   ammo gates never pass and it never fires. The wiring (mount + broadcast) is
   correct and proven via the `ShipSpawner` seam, but **the stock server cannot
   demonstrate a live kill without also selecting a combat-capable default hull**
   (e.g. a hull with `MaxEnergy > 0`, `MaxAmmo > 0`, and a fixed hardpoint, like
   the tests' `Dreadnought 4`/`Adv Scout` pairs). This is a genuine finding for a
   follow-up, not a defect in this task's wiring.

2. **An idle victim's falling hull is not streamed tick-by-tick.** The
   `SINGLE_SHIP_UPDATE` broadcast is dirty-gated on position change
   (`BroadcastShipStates`, `Server.cs:304-322` skips ships within
   `DirtyEpsilon`). The hull slot *exists* in the payload
   (`ServerShipUpdate.FromModel:118` fills `Ship.Fraction`), but an immobile ship
   is never marked dirty, so its hull does not visibly tick down. The kill itself
   (`KILL_SHIP`) is broadcast regardless of motion, so the death always crosses
   the wire. Flagged — the fix would be a "hull changed ⇒ dirty" condition, out
   of this task's scope (it does not change sim or wire format, and an idle bot
   with zero damage intake never needs it).

3. **`PW Mini-Dis` (Power-0 weapon) is a selection trap.** Several weapons in
   `PCore001.igc` have `Projectile.Power = 0` (they fire but cannot damage). A
   weapon-selection heuristic must filter `Power > 0f` or it silently arms a
   harmless weapon. `MountCombatWeapon` now does this; worth remembering when
   loadout/buy paths are added.

---

## Verification

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build     # 0 warnings, 0 errors
dotnet test      # Passed: 1517 (1515 pre-existing + 2 new live-combat tests), 0 failed
```

All existing tests stay green — the task only connects existing, tested pieces
(mount + broadcast); it changes neither the weapon/projectile/damage/death
simulation nor the `KillShip` wire encoding.

## Files changed

- `src/Allegiance.Server/Server.cs` — `SpawnDefaultShip` arms the placed ship;
  new `MountCombatWeapon`; `BindHostAndTransport` wires `ShipKilled`; new
  `BroadcastKillShip` handler.
- `src/Allegiance.Server/ServerHost.cs` — new `ShipKilled` hook + `KillShipEvent`
  override (the one live `IIgcSite` callback).
- `tests/Allegiance.Core.Tests/LiveCombatWireTests.cs` — the new two-party
  live-wire kill + hull-streaming proof.
