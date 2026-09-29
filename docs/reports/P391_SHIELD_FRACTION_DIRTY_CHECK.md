# P391 — a player's own shield-fraction regen doesn't refresh their HUD while stationary

## Symptom (live-observed)

After P389 landed server-side shield regeneration, a ship that regenerates its
shield **while sitting completely still** (recovering from a fight, or just
idle/docked) never updates its owner's own HUD shield reading. The server value
is correct — moving the ship (any thrust/turn) instantly snaps the HUD to the
real, fully-regenerated value — but with no motion the HUD quietly freezes at
whatever fraction was last broadcast, for as long as the ship doesn't move.

## Root cause (confirmed)

`Server.Broadcast.cs:397-402` — the per-tick gate that decides whether a peer's
OWN ship gets a fresh `SINGLE_SHIP_UPDATE` (the 47-byte message whose byte 41,
`ServerShipUpdate.cs:128`, carries the shield fraction via
`ServerShipUpdate.cs:75`) has **four** dirty terms:

```csharp
if (!live.PositionEverBroadcast
    || (ship.Position - live.LastBroadcastPos).Length() >= DirtyEpsilon
    || (fwd - live.LastBroadcastForward).Length() >= DirtyEpsilon
    || (up - live.LastBroadcastUp).Length() >= DirtyEpsilon
    || miningNow != live.LastBroadcastMining)
```

Position, forward, up, and mining-state. **Shield fraction is not among them.**
A stationary ship never re-trips any of the four, so its owner's HUD shield
reading goes stale for exactly as long as the ship stays put — precisely the
class of bug the skill's P-shield note flagged earlier for the HULL fraction
(the own-ship update is only sent when the ship is DIRTY).

## The fix (3 coupled edits, all in `src/Allegiance.Server/`)

1. **`Server.cs:301-315`** — a dedicated `ShieldFractionEpsilon = 0.005f`
   constant (rationale below).
2. **`Server.Lifecycle.cs:44`** — `LiveShip` gains a
   `float LastBroadcastShieldFraction` component (inserted before
   `bool LastBroadcastMining`, keeping the lone defaulted
   `PositionEverBroadcast` parameter last — C# forbids a non-defaulted
   parameter after a defaulted one).
3. **`Server.Broadcast.cs:401-403`** — the dirty gate gains a fifth term
   `MathF.Abs(ship.ShieldFraction - live.LastBroadcastShieldFraction) >=
   ShieldFractionEpsilon`, and the `live with { ... }` state update at
   `Server.Broadcast.cs:421` stamps the new `LastBroadcastShieldFraction`
   alongside the other `LastBroadcast*` fields.
4. **`Server.Lobby.cs:1857`** — the single `new LiveShip(...)` construction
   site seeds the new component with the ship's current `ShieldFraction`.

The new term follows the exact shape of the existing boolean `miningNow !=
live.LastBroadcastMining` comparison, upgraded to a float epsilon comparison
(an exact-equality `!=` would re-broadcast on every tick's float noise even
when nothing visible changed).

### Why 0.005 (and why not `DirtyEpsilon`)

`DirtyEpsilon` (`Server.cs:299`) is **1e-3 world units (metres)** — its own doc
says "position-change threshold (world units)". Reusing it blindly for a
dimensionless 0..1 fraction would conflate two unrelated scales; per the brief
I checked its calibration before deciding against reusing it.

The shield slot on the wire is a div-240 BytePercentage (`Quant.cs:20-29`,
divisor 240, not 255), so the **smallest representable shield-fraction change a
client can display is 1/240 ≈ 0.004167**. `ShieldFractionEpsilon = 0.005` sits
just above that step, which buys two things:

- **No sub-quantization spam** — the term never fires unless the change is
  visible to the HUD (a fraction step the byte can actually express).
- **A smooth climb during regen.** Measured regen rates in the representative
  core (`PCore001.igc`) range ~0.85%–2.5% of the pool per second (e.g. "Sm
  Shield 3" 1.67%/s, "Med Aslt Shd 1" 2.5%/s, "Lrg Shield 3" 0.85%/s), i.e.
  0.0005–0.00125 per 0.05 s tick — **below** the wire step, so rebroadcasting
  every tick would be pure waste. At 0.005 the HUD updates every ~0.2–0.6 s in
  0.5%-steps: continuous, never chunky, and never redundant.

For an unshielded ship `ShieldFraction ≡ 0` (confirmed: `ClearLoadout` /
`Ship.cs:3244` resets to 0, and `MountPart` is the only setter to a non-zero
value, `Ship.cs:2045-2046`), so `|0 − 0| = 0 < 0.005` — the new term is a strict
no-op and cannot fabricate per-tick rebroadcasts for every unshielded ship.

## Live proof (real server + real loopback clients, `LiveShieldRegenBroadcastTests`)

Real in-process `Server` on loopback TCP, a shooter dealing real projectile
combat damage to a shielded stationary target, then the target sitting
completely still (commanded `ControlData.Neutral` the entire test) while its
shield regenerates. Evidence from the detailed run:

```
P391: target shield drained to 0.715 over 103 combat ticks; hull=1.000
P391: target's own wire shield climbed 0.658 -> 0.883 while stationary (server reached 0.885) over 550 regen ticks
P391: distinct shield fractions observed on own wire = 51 [0.658, 0.663, ...]
P391: stationary UNshielded ship received 1 own-ship updates over 240 idle ticks (expect exactly 1)
```

- **Drain via real combat**: the target's shield fell 1.0 → 0.715 under fire,
  hull untouched (shield absorbed all of it) — the shield starts genuinely
  below full.
- **Own HUD climbs with zero movement**: the target's own client observed its
  shield climb 0.658 → 0.883 over the stationary regen window — 51 distinct
  values, a smooth monotone stream, tracking the server-side regen curve
  (wire 0.883 vs server 0.885 — within wire quantization). The test also
  asserts **every** received position equals the anchor (Δ ≤ 0.012), so the
  climb is attributable to the shield dirty term alone, not motion.

### "Must fail if removed" (discrimination)

Temporarily disabling the new gate term (`Server.Broadcast.cs`) reproduced the
exact bug — the server regen climbed to 0.885 but the target's own wire froze:

```
own shield must climb on the wire while stationary: minSeen=1.000 lastSeen=1.000 (server 0.885)
```

That is byte-for-byte the reported symptom (stale frozen HUD that only moves
once the ship moves). The test is genuinely discriminating, not an
always-green.

### Unshielded no-op (P391 scope item 3)

`Stationary_unshielded_ship_generates_no_spurious_shield_rebroadcasts`: a
default-spawn (no shield mounted) stationary ship received **exactly 1**
own-ship `SINGLE_SHIP_UPDATE` (the P332 forced first send) over 240 idle ticks
— no per-tick flood, and every received value carried `ShieldFraction == 0`.

## Scope / exclusions

- **No change to regen math (P389) or absorption math (P87)** — both confirmed
  correct; this is purely a broadcast-cadence gap in the own-ship dirty gate.
- **Analogous case deliberately LEFT, flagged not fixed**: the teammate
  position broadcast at `Server.Broadcast.cs:626-630` (P85) shares the same
  dirty-gate shape (position/forward/up only) and so a *teammate's* shield is
  likewise not re-streamed to peers while it sits still. That is cross-side /
  teammate shield visibility — explicitly **out of scope** for P391, which is
  strictly the owning player's own HUD reading their own value. Flagged here
  per the brief ("say so either way"), not silently fixed.
- P368–P390 untouched.

## Build / test status

- Whole-solution build: 0 warnings, 0 errors.
- New suite: `LiveShieldRegenBroadcastTests` — 2 tests, both green (isolated
  and in the focused regression batch).
- Focused regression batch (LiveCombatWire, ShipsUpdateLive, MiningStateWire,
  TeammatePositionBroadcast, CombatShield, RepairAndRefuel, KillBonusLive,
  LiveTurretGunner, OwnShipKillBonusUpdate + new): 41/41 green.
- Full Core.Tests suite: 3 consecutive runs; only the pre-existing documented
  live-wire timing flakes expected.
