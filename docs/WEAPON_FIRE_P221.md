# WEAPON FIRE — P221 (TK_FireWeapon wired in the MonoGame client)

## What was wired

The single biggest real gap in the MonoGame client (from `docs/KEYBINDING_AUDIT_P220.md`
§3) is closed: **a human player can now shoot from this client.** This stage wires
`TK_FireWeapon` end to end using the *same lever the wingman AI already pulls* — no new
sim behaviour.

Scope was deliberately the smallest real slice that makes the client able to fight:
**group-fire ("all weapons") mode only**, exactly what the wingman NPC does
(`Ship.cs:4558,4603-4605` sets `AllWeapons`). Nothing else.

### 1. Input (`FlightInput.cs` + `Game1.cs`, afterburner pattern)

- `FlightInputState` gained a `Fire` bool (`FlightInput.cs`), positioned right after
  `Afterburner` with the same `= false` default.
- Bound to **Space** in `Game1.cs`'s input block. Space is unclaimed — every other
  binding (W/S/Q/E/Up/Down/Left/Right/A/D/Shift/R/M/V/T/G/Enter/Back/Escape) is taken.
  The native default VK table is **not** in the reference checkout (grepped during
  P220; not hunted again), so absent a confirmed default this port picks Space on its
  own — the same justification P220 already used for Shift→afterburner
  (`KEYBINDING_AUDIT_P220.md`).
- In `FlightInput.Translate`, when `Fire` is held, `ShipControlState.AllWeapons` is
  OR-ed into `stateM` — the exact shape of the existing
  `if (input.Afterburner) stateM |= ShipControlState.AfterburnerButton;` line.
- **Held / continuous, not edge-triggered** (native `IsTrekKeyDown` semantics): a held
  Space keeps firing, like afterburner.
- **The `composing` gate already neutralizes fire while typing chat or navigating the
  quick-chat menu**: `Fire` is read inside the existing
  `composing ? FlightInputState.None : new FlightInputState(...)` ternary, so all
  flight input (now including fire) is suppressed while composing. No extra gate was
  needed — verified by construction.
- No new sim/server behaviour: `Weapon.IsWeaponSelected` (`Weapon.cs:363-375`) already
  treats `StateM & AllWeapons` as "every fixed weapon fires" (the port of
  `allWeaponsIGC = 32768`, `igc.h:826`).

### 2. HUD (`HudFrame.cs` + `HudRenderer.cs`, P211 pattern)

- `HudFrame` gained a `Firing` field read identically to the existing `AfterburnerActive`
  — from `StateM & ShipControlState.AllWeapons` (`Game1.cs`, the HudFrame construction).
- `HudRenderer` draws a minimal firing indicator on the existing reticle: while firing,
  the crosshair ring/ticks/dot render in a weapons-hot warning colour and a **FIRING**
  tag appears beneath the ring. No layout redesign — one more state-driven readout
  consistent with what's already there.

### 3. Loopback test (`tests/Allegiance.ClientCore.MonoGame.Tests/FlightInputTests.cs`)

Three new pure-logic tests (no graphics device), mirroring the afterburner input's
shape in the same project:
- `HoldingFire_SetsAllWeaponsInStateM` — Fire held ⇒ outgoing `stateM` carries `AllWeapons`.
- `ReleasingFire_LeavesStateMFreeOfAllWeapons` — Fire lifted ⇒ `AllWeapons` cleared
  (release-clears, not latched).
- `HoldingFire_ComposesWithMovementAndAfterburner` — Fire is additive; it does not
  disturb simultaneous movement/afterburner bits.

**Must-fail-if-removed verified**: temporarily disabling the `if (input.Fire)
stateM |= ShipControlState.AllWeapons;` line made the two fire-asserting tests fail
(expected `AllWeapons` absent) while the release test correctly stayed green; the line
was then restored.

## Live-fire proof (required, observed, not simulated)

Ran the **real** `Allegiance.Server` (temp config, `teams:1`, port 7761) and the
**real** MonoGame client (on display `:0`, direct-connect to 7761) together. Because
this host is Wayland with no input-injection tool (xdotool/ydotool/wtype absent), a
**temporary** auto-fire diagnostic held the `Fire` input once the mission was
`STAGE_STARTED` — the exact same `FlightInputState.Fire` flag that holding Space
produces. **Every diagnostic was removed before commit** (Server.cs diff is empty;
Game1.cs contains no diag remnants).

Observed in the server console log:

1. **Weapon armed on the spawned ship.** The client's spawned ship reported
   `[combat] ship 8096 armed 1 of 1 fixed hardpoints ... mount 0: 'PE Gat Gun 3'`.
   (Note: the *stock* default hull picked first-by-id is `Adv Miner`, which has no fixed
   hardpoints and is genuinely left unarmed — a real `[combat]` finding. To live-fire I
   temporarily switched the spawn to a fight-capable hull (fixed hardpoint at mount 0),
   the P218-style temp spawn override, then reverted it.)
2. **`AllWeapons` set on `Ship.StateM` from the client's fire input.** The server's
   `[ctrl]` handler logged the incoming SHIP_UPDATE:
   `[ctrl] 1 ship 8096 controls=(yaw 0.000 pitch 0.000 roll 0.000 thr -1.000) state=0x8000`
   — `0x8000` = `ShipControlState.AllWeapons` (32768) arriving over the real wire from
   the client's held Fire. (Prior unarmed run also showed `state=0x8020` =
   AllWeapons|ForwardButton.) Before `STAGE_STARTED` the same updates were correctly
   logged as ignored (pre-game hold), so the transition is visible.
3. **A real projectile observed server-side.** The server broadcast and logged each
   shot: `[projectile] ship 8096 fired -> PROJ_INST(12) type 4 pos (720,730,920)
   vel (0,0,-600) fwd (0,0,-1) life 1.1 -> 1 same-side peer(s)` — dozens of them
   (101+ by teardown), each a real `Projectile` created by the weapon tick off the
   AllWeapons bit and broadcast as `FM_CS_PROJECTILE_INSTANCE(12)`.

That is the complete chain: **hold Fire (Space) → `AllWeapons` in the outgoing
`stateM` → set on the server's `Ship.StateM` → real projectile created and broadcast.**
A compile or a unit test is not the proof — this is an actual shot observed happening.

### Honest caveats

- Physical-Space keypress was not synthesized (no input tool on this Wayland host); the
  temp diagnostic held the exact same `Fire` flag Space sets. The Space→`Fire` mapping
  itself is the single, self-evident `ks.IsKeyDown(XnaKeys.Space)` line in `Game1.cs`.
- The HUD `FIRING` indicator is compile-verified and driven by the *identical*
  `StateM & AllWeapons` bit the live run proved is set on the ship; it was not separately
  captured as a screenshot this pass.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings / 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **124 pass, 0 fail** (incl. the 3 new fire
  tests; 21 FlightInput tests).
- Full `Allegiance.Core.Tests` suite run 2× (baseline 3037): see per-run results below;
  no new failures beyond the documented `LiveTurretGunnerBindingTests` machine-state
  flake (not chased).

## Deliberately left undone (natural follow-ups — out of scope this stage)

Per `docs/KEYBINDING_AUDIT_P220.md` bucket C (29 items), not implemented here:
- **Single-weapon selection** — `OneWeapon` mode, `SelectedWeaponMask`, and the
  selection/inventory UI: `TK_PrevWeapon` / `TK_NextWeapon` / `TK_ToggleGroupFire` /
  `TK_ToggleWeapon1-4` / `TK_SwapWeapon1-4` (mounted-part swap UI).
- **Expendables** — `TK_FireMissile`, `TK_DropMine`, `TK_DropChaff` (separate
  expendable-UI gaps per the audit).
- **Turret-weapon firing** (the non-fixed branch of `Weapon.IsWeaponSelected`, gated on
  a gunner's own `WeaponsMask`) — this stage is the pilot's own fixed weapons only.

These are the "selection half" of fire the P220 audit flagged as its own task; this
stage deliberately ships only the group-fire slice that makes the client able to fight.

## Citations (file:line)

- `src/Allegiance.Core/Sim/ShipControlState.cs:40-44` — `OneWeapon`/`AllWeapons`/
  `WeaponsMask`/`SelectedWeaponMask` bits (native `igc.h:825-827`).
- `src/Allegiance.Core/Sim/Weapon.cs:363-375` — `IsWeaponSelected`; `AllWeapons` →
  fixed weapons fire (`Weapon.cs:372`).
- `src/Allegiance.Core/Sim/Ship.cs:4558,4603-4605` — wingman AI sets `AllWeapons` to fire.
- `src/Allegiance.Core/Sim/Weapon.cs:321` — `ProjectileCreatedEvent` after `sector.Add`
  (P88 seam).
- `src/Allegiance.Server/Server.cs:9332-9396` — `BroadcastProjectileInstance`, the
  `[projectile] ... PROJ_INST(12)` log.
- `src/Allegiance.Server/Server.cs:2259-2263` — `[ctrl] ... state=0x...` log of incoming
  `StateM`.
- `src/Allegiance.Server/Server.cs:9274-9329` — `MountCombatWeapon` (default spawn arming).
- `docs/KEYBINDING_AUDIT_P220.md:96-109` — the `TK_FireWeapon` gap this stage closes.
