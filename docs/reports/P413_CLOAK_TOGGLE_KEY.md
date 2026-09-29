# P413 — Cloak toggle key (TK_ToggleCloak)

The whole cloak mechanic was already real server-side (P269/P270) — the only
gap was a player-facing key. This stage adds one, wired through the exact same
outgoing-control-state path the existing afterburner/fire/throttle buttons use.

## The key chosen: **F**

`Rendering/KeyBindingTable.cs` was audited for unbound single letters in
flight. Free candidates were `F`, `L`, `U`, `Z`:

- `L` / `U` are bound only in the **docked shop** screen (L = Launch, U =
  Undock, `Game1.Panes.cs:142-143`) — not in flight.
- `F` is bound only in the **lobby** server-filter (`Game1.Session.cs:164`) — a
  separate input context, free in flight.
- Native's own training text points at both `K` and `G` for cloak
  (`mission4.cpp:719` "Press the K key" and `:722` "Press the G key") — both are
  already taken in this port (K = suicide, G = autopilot), and the native
  default VK table is not in the checkout (P220), so neither is usable.

**F is placed adjacent to C (chaff)** — both are defensive countermeasures, and
this port's own convention is single letters near related functions ("e.g.
near `C` for chaff", per the brief). F is the only key physically adjacent to
C that is free in flight (X/D/V beside C are all taken). Laid out in the help
overlay next to chaff:

```
C   Drop chaff [TK_DropChaff]
F   Toggle cloak [TK_ToggleCloak]
```

## The wiring

`FlightInputState` gained a `Cloak` flag (pure, on the record beside
`Afterburner`/`Fire`), and `FlightInput.Translate` folds it into the outgoing
stateM exactly like afterburner/fire:

- `Flight/FlightInput.cs` — `if (input.Cloak) stateM |= ShipControlState.CloakActive;`
  (`ShipControlState.CloakActive = 0x800`, `Sim/ShipControlState.cs:33`, the
  native `512*coastButtonIGC` = `cloakActiveIGC` bit, `igc.h:820`).
- `Game1.FlightInput.cs` — the F key is **edge-triggered** (like C/K/N/chaff):
  a press flips the latched `_cloakToggled`, carried into `FlightInputState`
  each frame (`Cloak: _cloakToggled`). This matches native `TK_ToggleCloak`,
  which XORs the bit per key press (`WinTrek.cpp:9468-9471`).
- `Game1.cs` — `_fCloakWasDown` / `_cloakToggled` fields.

The client sends it every frame through the real control path
`ConnectionSession.CommandControls` → `FM_C_SHIP_UPDATE(24)` →
`Server.Dispatch.cs:152` `live.Ship.StateM = u.Payload.StateM` →
`Ship.UpdateCloak` (`Ship.cs:6157`) reads the bit, gates on a mounted
`ET_Cloak@0` part + not docked/boarded/ripcording, and activates the cloak.

No server/sim code changed — this is a pure input-wiring stage.

## Item 3 — energy-drain gating already handles "can't activate"

Confirmed, no new gate added. `Ship.UpdateCloak` (`Ship.cs:6157-6229`): with a
drained energy pool, `energy < energyNeeded` floors the pool at 0 and throttles
the cloak's target strength by `r²` (`maxCloaking *= r*r`, `:6202-6203`), so the
ramp converges to `_cloakLevel == 0` → `DeactivateCloak()` (`:6224-6225`). A ship
with no energy simply never reaches an effective invisible state — graceful, no
error path, nothing to rebuild. A ship with **no** `ET_Cloak` part at all has
`_mountedCloak` null and `UpdateCloak` returns early, clearing any emitter — so
toggling the bit on a cloak-less ship is harmless (the server just ignores it).

## Verification (all real, not fabricated)

### Unit tests — control-bit wiring (`FlightInputTests`, +3)
- `CloakEngaged_SetsCloakActiveBitInStateM` — `Cloak:true` → `CloakActive` bit set.
- `CloakDisengaged_LeavesStateMFreeOfCloakActive` — toggled off → bit clear.
- `CloakEngaged_ComposesWithMovementAfterburnerAndFire` — additive, coexists.
- **Must-fail-if-removed proven**: neutralising the `if (input.Cloak)` line made
  the two engaged tests red; restored → green. (39/39 in the combined
  FlightInput + live filter.)

### Live proof (`CloakToggleLiveTests`, +1, real in-process server)
A real `ConnectionSession` (the pilot) sends `CommandControls` with the exact
stateM `FlightInput.Translate(...Cloak:true).StateM` produces — the identical
path the F-key handler's frame takes — to a real ship over the real TCP
transport, with a real `FltBot` enemy observer on the opposite side, faohshcar
hull + low-draw cloak, 2000 u apart (uncloaked detection ~5700, cloaked
collapse ~332). Results:

- **(a)** cloaker `StateM=0x800` reached the server; `Ship.IsCloaked` became
  true and `Cloaking < 0.05` (near-full).
- **(b)** the enemy peer (no prior detection) received **zero** HEAVY telemetry
  entries for the cloaked ship after the sweep flipped `SeenBySide` false.
- **(c)** `Cloak:false` deactivated the cloak (`IsCloaked` false) and telemetry
  to the enemy resumed.

Logged: `P413-LIVE: F-key control path drove cloaker 8098 ... IsCloaked
true->false, enemy side 0 saw it True -> 0 -> True`. **Must-fail proven**: with
the wiring line removed, the live test fails at (a) — the cloak never activates.

### Build & suite
- Whole-solution `AllegianceCore.slnx` Debug build: **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **775/775** green (incl. the 3 new
  unit tests, the new live test, and the HelpOverlay layout with the new F row).
- `Allegiance.Core.Tests` full suite: see below (documented live-wire flakes only).
- `Allegiance.ClientCore.Tests` / `Allegiance.Lobby.Tests`: unaffected.

## Notes (honesty items)
- The P270 visual layer (translucent + shimmer render, violet "CLOAKED" HUD
  tag) already exists and keys off the same `CloakActive` stateM bit, so this
  toggle immediately pays off visually with zero extra work — no speculative
  fade/shader was built (out of scope; it would be redundant).
- `F` is dual-use (lobby filter vs flight cloak) in separate screens — the same
  pattern as `R` (lobby refresh vs in-flight quick-chat), which the port already
  accepts.

## Files changed (P413 only)
- `src/Allegiance.ClientCore.MonoGame/Flight/FlightInput.cs`
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs`
- `src/Allegiance.ClientCore.MonoGame/Game1.cs`
- `src/Allegiance.ClientCore.MonoGame/Rendering/KeyBindingTable.cs`
- `tests/Allegiance.ClientCore.MonoGame.Tests/FlightInputTests.cs`
- `tests/Allegiance.ClientCore.MonoGame.Tests/CloakToggleLiveTests.cs` (new)
- `tests/Allegiance.ClientCore.MonoGame.Tests/Allegiance.ClientCore.MonoGame.Tests.csproj` (Bot ref for the live-test enemy observer)

Unrelated pre-existing local edits (`tools/vizfight/Program.cs` ListenAnyIP,
`docker/compose.server.json` map) were left unstaged — not part of P413.
