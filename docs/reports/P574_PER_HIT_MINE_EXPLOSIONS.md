# P574 — per-hit mine explosions (at P2) + native explosion sounds for every world explosion

## Summary

Mines now blow up WHERE a ship touches them — one violet pop per hit, at the
ship's nose along its path — instead of a single blast at the field centre when
the field dies. The field visibly thins as it wears (native `SetMineStrength`),
and every world explosion (mine pop, ship death, station blast) now plays its
native tiered positional sound.

Two misattributions in the old port are corrected against native:
- The old centre `ExplosionType.Mine` blast sent on `MINE_DESTROYED`/expiry cited
  `shipIGC.h:2024` — but that line is the **per-hit** pop inside
  `ApplyMineDamage`, not a field-death effect. Native draws **nothing** when a
  mine is used up or expires.
- The explosion-sound tier classes (`ExplosionSoundTier.cs`, `SoundManager.cs`)
  existed but were only wired to the missile path; `ExplosionEvent`s were silent.

## Why (Domingo)

> "if you touched one mine that one in particular would explode, because that's
> how they worked on the original client." ... "It goes off one by one."
> "mines had a sound associated with their explosion right?"

## Native behaviour ported

### Per-hit mine pops (`shipIGC.h:2021-2024`, `mineigc.h:129-132`)
`ApplyMineDamage` keeps each ship's worst hit (`m_maxMineP1/P2`), then:
```cpp
if (m_maxMineTime > m_timeLastMineExplosion + 0.5f) {
    m_timeLastMineExplosion = m_maxMineTime;
    GetCluster()->GetClusterSite()->AddExplosion(m_maxMineP2, 1.5f, c_etMine);
}
```
The P2 placement is `position1 + dV·((c_radiusPlacement(10)+rHim)/|dV|) +
Vector::RandomPosition(10)` (`mineigc.h:132`) — just ahead of the ship along its
path. Ported: `Sector.ResolveMineDetonations` now computes the native P2
(with a sim-deterministic `RandomPosition` replacement) and raises a new
`IIgcSite.MineHitEvent(mine, p1, p2)` when the per-ship `0.5 s`
`TimeLastMineExplosion` gate passes; the server broadcasts
`EXPLOSION_EVENT(207)` (`c_etMine`, scale 1.5) at P2 to the same-sector audience.

### No field-death / expiry blast (corrected misattribution)
Native `KillMineEvent` just terminates the mine (`clintlib.cpp:2954-2958`; the
game-site default at `igc.h:4845` is empty), and expiry
(`CmineIGC::Update` → `Terminate`, `mineigc.cpp:150-156`) draws nothing. The
old centre blast in `Server.BroadcastMineDestroyed` was **removed**.

### Field thinning (`mineigc.h:142,180` → `efgeo.cpp:21-24,43-51`)
Every hit calls `SetMineStrength(m_fraction)`; `MineFieldGeoImpl::SetStrength`
truncates the decal vector to `1 + int(strength·radius²/100)` by index. The
server now sends a port-internal `MINE_STRENGTH(853)` update (with the mine's
current fraction) alongside each pop, and the client thins its decal cloud to
the same count, keeping the first N decals (native tail-truncation).

### Explosion sounds for every world explosion (`trekigc.cpp:86-101`)
Every `AddExplosion` in the player's current cluster plays a positional
`GenericSoundSource`: `scale > 100` `largeExplosionSound`, `> 10`
`mediumExplosionSound`, else `smallExplosionSound`. The client now drains every
`EXPLOSION_EVENT(207)` and voices the tier by its `scale` at a distance-attenuated
volume — skipping `c_etMissile` (already voiced by `MissileSoundPlanner`).

## Files changed

Server / core:
- `src/Allegiance.Core/Sim/Ship.cs` — `TimeLastMineExplosion` (port of
  `m_timeLastMineExplosion`).
- `src/Allegiance.Core/Model/Sector.cs` — native P1/P2 + throttle + raises
  `MineHitEvent`; `MineHit` carries the `Mine`; deterministic `RandomPosition`.
- `src/Allegiance.Core/Sim/IIgcSite.cs` — `MineHitEvent` (default no-op).
- `src/Allegiance.Server/ServerHost.cs` — `MineHit` hook + forward.
- `src/Allegiance.Server/Server.Lifecycle.cs` — wire `MineHit = BroadcastMineHit`.
- `src/Allegiance.Server/Server.Combat.cs` — `BroadcastMineHit` (pop at P2 +
  `MINE_STRENGTH`); removed the centre blast on death.
- `src/Allegiance.Core/Wire/MineStrength.cs` — new port-internal message (853).

Client:
- `src/Allegiance.ClientCore/State/ClientWorld.cs` + `ExplosionSoundEntity.cs` —
  buffer every explosion for the sound pass; apply `MINE_STRENGTH` thinning.
- `src/Allegiance.ClientCore/State/MineEntity.cs` — `Strength` (default 1.0).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — decode `MINE_STRENGTH`.
- `src/Allegiance.Core/Wire/FedMessageDumper.cs` — name 853.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` + `FlightInput.cs` —
  `UpdateExplosionSounds()` drain; `ExplosionSoundRouter.cs`, `ExplosionSoundCurve.cs`.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — `PlayWorldExplosion`.
- `src/Allegiance.ClientCore.Game/Rendering/MineFieldGeometry.cs`, `MineRenderer.cs`,
  `Game1.Rendering.cs`, `Game1.cs` — strength-aware decal count (thin by index).
- `src/Allegiance.Bot/Bot.cs` — capture `EXPLOSION_EVENT(207)` for the live proof.

## Tests (all fail on the pre-P574 code, pass after)

`tests/Allegiance.Core.Tests/MineCombatTests.cs`:
- Crossing ship raises one mine pop at the native P2 (ahead, within (10+rHim)±10).
- Two consecutive in-field ticks produce only the first pop (0.5 s throttle).
- Throttle releases once >0.5 s elapses.
- Friendly + stationary crossings raise no pop.
- Live: crossing broadcasts a Mine pop ahead of the ship, not the field centre.

`tests/Allegiance.Core.Tests/MineStrengthWireTests.cs`: round-trip + 12-byte layout.

`tests/Allegiance.ClientCore.MonoGame.Tests/P574ExplosionSoundAndMineThinningTests.cs`:
- Tier per scale (1.5→small, 60→medium, 350→large; boundaries at 10/100).
- Positional volume curve; router same-sector only, missile skip, tier+volume,
  no-listener → none; mine decal count thins natively and keeps the first decals.

## Verification

- Whole-solution build: 0 warnings, 0 errors.
- Full suite via `scripts/run-tests.sh` (P471) — 3 consecutive runs on the final
  code, each reported as it happened (every failure is a documented
  load/live-wire flake in `docs/TEST_FLAKES.md`, none related to P574):

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1 | 12/12 | 1398/1398 | 544/545 | 3770/3773 |
| 2 | 12/12 | 1398/1398 | 545/545 | 3772/3773 |
| 3 | 12/12 | 1398/1398 | 544/545 | 3770/3773 |

  Run-1 flakes (all cited): `ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears`
  (TEST_FLAKES row "fixed (P482) — residual ~1/15 timing flake"),
  `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`
  + `GameOverPlayersWireTests.Conquest_win_…leak…another` (rows 29-30, observed
  across prior suites), `CargoBayReloadTests.Reload_transfers_ammo_…` (rotating
  CargoBayReload member, rows 53-54).
  Run-2 flake: `WarpBombTests.Live_whole_mission_warp_bomb_…delayed_blast_lands`
  (row 183, "live-wire delayed-blast timing").
  Run-3 flakes: `FullSessionKillIntegrationTests.FullSession_…RealCrossSideKill`
  (row 33, "suspect (P472) live-wire timing"), `LiveWinConditionTests.Board_refused_once_mission_over`
  (row 34), `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_…` (row 157),
  and `NpcShipFoundationLiveTests.Ownerless_npc_ship_appears_in_peer_heavy_updates_with_position`
  (not yet in the ledger; **passes in isolation** — a full-suite-load flake, not a
  regression). Every P574 test (mine-domain, live pop-at-P2, MineStrength wire,
  explosion-sound/thinning) passed in all three runs.

- Branch: `master`, committed (not pushed).

## Live proof

Real-server, real-wire evidence (a genuine connecting client over loopback,
from the live test `Live_crossing_broadcasts_a_mine_pop_ahead_of_the_ship_not_at_the_field_centre`);
the server's own log lines, captured verbatim:

```
[mine] ship 8098 deployed -> EXPORT(26)/OT_mine 11 pos (0,0,0) life 15 -> 2 same-side peer(s)
[mine] ship 8098 hit by mine 5464: c_etMine pop at (X=-3.09, Y=-5.59, Z=-82.24) sector 1051 -> 3 peer(s)
[mine] mine 5464 strength -> 1.000 (MINE_STRENGTH) -> 3 peer(s)
[P574-LIVE] mine pop at (X=-3.09, Y=-5.59, Z=-82.24) (82.5 from centre, radius 100), scale 1.5
```

The pop is 82.5 units from the mine centre, ahead of the crossing ship along
its velocity — **not** the field centre — exactly the native P2 placement, and
it is broadcast while the field is still alive (per-hit, not on death).

A real windowed client was also launched (P572 `live-proof.sh`, 2560×1440
backbuffer) and produced valid real-window PNGs
(`proof_t010.1_sOmega_cchase.png`, `t016`, `t022`), but the autoshot windows
did not catch a ship mid-crossing a minefield, so no clean "trail of violet
pops" frame was captured this session. The minefield mechanics (P2 placement,
0.5 s throttle, per-hit broadcast, strength thinning) are instead proven by the
wire-level live test + server logs above and the domain unit tests — no
screenshot has been fabricated.


