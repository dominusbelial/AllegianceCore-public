# P413 — cloak toggle key (the whole mechanic is already real server-side, just unreachable)

## Why this task exists

Surveying native's full key-binding catalog (`~/Development/Allegiance/
src/WinTrek/trekinput.cpp:1480-1650`) against this port turned up two
mechanics that are already fully implemented and working but have no
player-facing key at all. This is the smaller, cleaner one.

## Confirmed real mechanic, already working server-side

`Ship.cs:2099-2101` (P269): a mounted `ET_Cloak@0` part is picked up by
`Ship.UpdateCloak`, which activates it from the `CloakActive` control
bit, drains energy, and ramps `Ship.Cloaking`
(`Ship.cs:358-368`, `IsCloaked` at `:379`). Detection/visibility is
already fully integrated: `Server.Broadcast.cs` gates telemetry on
`ship.IsCloaked && !ship.SeenBySide(sideId)` in at least 4 real call
sites (`:944,1036,1466,2019`). `ShipControlState.CloakActive = 0x800`
(`Sim/ShipControlState.cs:33`) is the exact bit the client would need to
set — the same bitmask mechanism already used for throttle/fire/
afterburner buttons, sent every tick.

**Confirmed gap**: nothing in `Allegiance.ClientCore.MonoGame` or
`Allegiance.ClientCore` ever sets this bit — grepped, zero hits for
`Cloak` anywhere in the client project. Native's key is
`TK_ToggleCloak` ("Toggle Cloak").

## Scope

1. Add a key handler (pick an unbound key — check
   `Rendering/KeyBindingTable.cs` for what's already taken; this port's
   own convention seems to be single letters near related functions,
   e.g. near `C` for chaff) that toggles the `CloakActive` bit in the
   ship's outgoing control state, exactly the same way the existing
   afterburner/fire/throttle buttons are already set from key state.
2. Add the new key to `KeyBindingTable.cs`'s help overlay, matching its
   existing style (`"Toggle Cloak [TK_ToggleCloak]"` or similar).
3. Confirm cloak's energy-drain gating already prevents toggling on with
   insufficient energy (check `Ship.UpdateCloak`'s real gate) — this
   task adds the key, not new gating logic; if the existing gate already
   handles "can't activate" gracefully, just confirm it, don't rebuild
   it.

## Explicitly out of scope

- Any change to `Ship.UpdateCloak`, energy drain, or detection/visibility
  logic — already correct, confirmed working.
- Any visual cloak effect (fade shader, etc.) — not requested; note
  plainly in the report if you notice one is trivially cheap to add, but
  don't build it speculatively.
- P368-P412 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: mount a cloak part on a real ship, press the
  new key, and confirm (a) the ship's `IsCloaked` becomes true
  server-side, (b) an enemy ship without prior detection stops receiving
  telemetry for it, (c) pressing the key again deactivates it and
  telemetry resumes.
- A test confirming the control-bit wiring itself (key press → correct
  bit set in outgoing `ShipControlState`).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P413_CLOAK_TOGGLE_KEY.md` in the repo root: the key
   chosen, the wiring, and the verification performed.
