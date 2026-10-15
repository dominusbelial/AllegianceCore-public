# P391 — a player's own shield-fraction regen doesn't refresh their HUD while stationary

## Why this task exists

Live-tested this session, right after P389 landed shield regeneration.
The server-side regen is genuinely correct (confirmed by moving the ship,
which instantly snapped the HUD from a stale 25% to the real, fully-
regenerated 100%) — but while sitting still, the player's own shield HUD
reading never updates on its own, silently going stale for as long as the
ship doesn't move.

## Confirmed root cause

`Server.Broadcast.cs:373-412` — the per-tick loop that re-sends a player's
own `SingleShipUpdate` (the message carrying `ServerShipUpdate.FromModel`,
which encodes `ShieldFraction` at byte 41 per P87) is gated on a dirty
check:
```csharp
if (!live.PositionEverBroadcast
    || (ship.Position - live.LastBroadcastPos).Length() >= DirtyEpsilon
    || (fwd - live.LastBroadcastForward).Length() >= DirtyEpsilon
    || (up - live.LastBroadcastUp).Length() >= DirtyEpsilon
    || miningNow != live.LastBroadcastMining)
```
Four terms: position, forward, up, mining-state. **Shield fraction is not
one of them.** A ship that isn't moving (typical while recovering from a
fight, or just sitting docked/idle) never re-triggers this send, so the
owning player's own HUD never learns the shield regenerated (or, for that
matter, depleted from some non-movement-correlated damage) until something
else — any position/orientation change, or mining starting/stopping —
happens to fire the same broadcast for an unrelated reason.

## Scope

1. Add a `ShieldFraction` term to this dirty check, following the exact
   shape of the existing `miningNow != live.LastBroadcastMining` boolean
   comparison (a `float` comparison needs an epsilon, not exact equality —
   reuse `DirtyEpsilon` or a value appropriate for a 0.0-1.0 fraction if
   `DirtyEpsilon` is calibrated for world-position units; check what it's
   actually calibrated for before reusing it blindly).
2. `LiveShip` (wherever `LastBroadcastPos`/`LastBroadcastForward`/
   `LastBroadcastMining` are declared — find the record) needs a matching
   `LastBroadcastShieldFraction` field, updated in the same `with { ... }`
   expression at line 411 alongside the other `LastBroadcast*` fields.
3. Ships with no mounted shield have `ShieldFraction` permanently `0`
   (confirmed: `ClearLoadout` resets it to 0, never set otherwise) — this
   change must be a no-op for them (0 vs 0 never trips the epsilon check),
   not add spurious rebroadcasts for every unshielded ship every tick.

## Explicitly out of scope

- Any change to the actual regen math (P389) or absorption math (P87) —
  both are confirmed correct; this is purely a broadcast-cadence gap.
- Cross-side/teammate shield visibility (a *different* ship's shield state
  reaching other peers) — this task is specifically about a player's own
  ship's own HUD reading its own current value; don't expand scope to
  auditing every other consumer of `ShieldFraction` unless you find a
  clearly analogous bug worth flagging in the report (don't silently fix
  something not asked for, and don't silently skip something clearly
  broken either — say so either way).
- P368-P390 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: reproduce the exact session sequence — get a
  ship's shield below full (combat or collision damage), then sit
  completely still (no thrust, no turning) and confirm the HUD's shield
  reading now visibly climbs on its own over the next several seconds
  without any movement, matching the real server-side regen curve — not a
  frozen number that only jumps once you move.
- Confirm an unshielded ship (no mounted shield part) shows no new/spurious
  per-tick broadcasts from this change.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P391_SHIELD_FRACTION_DIRTY_CHECK.md` in the repo root: the
   exact epsilon chosen and why, and the live stationary-regen proof.
