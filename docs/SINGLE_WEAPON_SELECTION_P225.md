# SINGLE-WEAPON SELECTION — P225 (TK_ToggleGroupFire / TK_NextWeapon / TK_PrevWeapon)

## Relationship to P221

`docs/WEAPON_FIRE_P221.md` wired `TK_FireWeapon` (Space) in **group-fire mode only**
(`ShipControlState.AllWeapons`) and explicitly deferred single-weapon selection as
the natural next task. P225 delivers that "selection half": the pilot can toggle
between firing **all** mounted fixed weapons and firing **exactly one** selected
fixed mount, cycling the selection with two new keys, with a HUD readout.

Critically, **zero sim code changed**. `Weapon.IsWeaponSelected`
(`src/Allegiance.Core/Sim/Weapon.cs:363-375`) already implements both modes:
`AllWeapons` fires every fixed weapon; `OneWeapon` + `(StateM & SelectedWeaponMask)
== this weapon's MountId` fires only the matching mount. P225 is 100% client
input + local UI state that chooses which mode/mount and sets the right bits.
The task brief explicitly forbade touching `Weapon.IsWeaponSelected` or any other
sim-side code — none was touched (git diff confirms only the MonoGame client and
its tests changed).

## Scope delivered

1. **Client-local selection state** (not server state — exactly like the existing
   `_cameraMode` / `_mouseFlightEnabled` fields in `Game1.cs`):
   - `_groupFire` bool (default `true`, preserving P221's all-weapons behaviour
     so nothing regresses by default).
   - `_selectedMount` int (default `0`), the fixed-mount id used in OneWeapon mode.
   - Clamped/cycled via `FixedMountCount()` — see the honesty note below.

2. **Two new edge-triggered keys** (three actually; the brief blessed a third)
   following the exact `M`/`V`/`K`/`C` edge-trigger pattern (a `_wasDown` latch,
   gated behind `composing`):
   - `` ` `` (backtick/grave, `OemTilde`) → `TK_ToggleGroupFire`: flips `_groupFire`.
   - `]` (`OemCloseBrackets`) → `TK_NextWeapon`: advances `_selectedMount`.
   - `[` (`OemOpenBrackets`) → `TK_PrevWeapon`: steps `_selectedMount` back
     (the brief allowed this as "if time allows" — it is the same three lines as
     NextWeapon, so it landed too).
   - **Key rationale**: every flight key is taken (W/S/Q/E/Up/Down/Left/Right/A/D/
     Shift/R/M/V/T/G/Enter/Back/Escape/Space/K/C). The task brief suggested
     `[`/`]` or `,`/`.` as cycle mnemonics — the bracket pair was chosen for
     `[`/`]` (step selection back/forward) and the backtick for the fire-mode
     toggle (a punctuation key distinct from the brackets, sitting in a natural
     "command" position). The native default VK table is not in this checkout
     (same situation every prior key choice here has faced, per
     `docs/KEYBINDING_AUDIT_P220.md`), so these are this port's own choices,
     documented consistently with the Shift→afterburner / Space→fire precedent.
   - The selection is advanced even while group-fire is ON (it simply has no
     effect until group-fire is toggled off), so it is already in place the
     moment the pilot toggles to single-weapon mode.

3. **`FlightInput.Translate` fire logic**: when Fire is held, OR `AllWeapons` if
   `_groupFire`, else OR `OneWeapon | (_selectedMount & SelectedWeaponMask)` —
   the exact two-mode branch `Weapon.IsWeaponSelected` expects.
   - Clean threading: `FlightInputState` gained two fields — `GroupFire` (default
     `true`) and `SelectedMount` (default `0`) — so the whole selection→stateM
     decision stays pure and testable without MonoGame types (the established
     `FlightInput` discipline). A pure `FlightInput.FireContribution(fire,
     groupFire, selectedMount)` helper computes the `stateM` contribution, and
     pure `NextMount(current, count)` / `PrevMount(current, count)` handle the
     wrap-around cycling.

4. **HUD** (`HudFrame.cs` / `HudRenderer.cs`, the `AfterburnerActive`/`Firing`
   pattern): a single top-center one-line readout — `WEAPONS: ALL` in group-fire
   mode, `WEAPONS: MOUNT n` in single-weapon mode (driven by the client-local
   `GroupFire`/`SelectedMount`). Pure ASCII (the P214 spritefont char-range
   pitfall). Additionally the reticle `Firing` detection was broadened to read
   `StateM & (AllWeapons | OneWeapon)` so the existing FIRING tag also lights in
   single-weapon mode (the sim sets exactly one of those bits when firing).

## Honesty note: FixedMountCount() and the hardpoint count

The brief asked to clamp `_selectedMount` to the local hull's
`HullType.Hardpoints.Length`. **The client cannot do that without a server/wire
change** (out of scope, and inconsistent with "zero server change"): the wire
never transmits the local hull's hardpoint list, and a stock auto-connect flight
never populates the own ship's loadout — the own-ship EXPORT is the NA(-1) dummy
(`ClientWorld.cs:215`) and `BUY_LOADOUT_ACK(5)` only reaches a buyer
(`ClientWorld.cs:271-277`). So `FixedMountCount()` uses the mounted-loadout's
distinct non-negative mount ids **when one is known**, else falls back to the
full 2-bit selection range (`SelectedWeaponMask+1 = 4`) — the largest set of
distinct mount ids the sim's selection mask can express. Selecting an id with no
weapon mounted simply fires nothing (`Weapon.IsWeaponSelected`), which is
harmless; group-fire never consults the selection. This is stated plainly rather
than fabricating a wire/hull dependency the port doesn't have.

## Unit tests (`tests/Allegiance.ClientCore.MonoGame.Tests/FlightInputTests.cs`)

Extended, not replaced (P221's three fire tests intact). 14 new cases
(FlightInputTests 21 → 35; MonoGame.Tests total 124 → 138):
- `GroupFireDefault_SingleMountIgnored_FiresAllWeapons` — default true ⇒ AllWeapons,
  even with a nonzero SelectedMount (no regression).
- `SingleWeapon_GroupFireOff_SetsOneWeaponPlusMount` — Fire + GroupFire=false +
  mount 1 ⇒ `OneWeapon | 1` (16385).
- `SingleWeapon_SelectedMountIsMaskedToSelectionWidth` — mount 5 ⇒ masked to 1
  (16385), high bits not leaked.
- `SingleWeapon_ReleaseClears_NoFireBits` — Fire released ⇒ neither AllWeapons
  nor OneWeapon.
- `NextMount_AdvancesAndWrapsAtCount` (5 cases incl. wrap at 2 and 4) and
  `PrevMount_AdvancesAndWrapsAtCount` (5 cases) — cycling wraps at the count.

**Must-fail-if-removed verified**: temporarily forcing `FireContribution`'s
single-weapon branch to return `AllWeapons` turned
`SingleWeapon_GroupFireOff_SetsOneWeaponPlusMount` (expected 16385, got 32768)
and `SingleWeapon_SelectedMountIsMaskedToSelectionWidth` red, while all 33
others (incl. every group-fire/cycle test) stayed green; the branch was restored.

## Live proof (required — observed, not simulated)

Ran the **real** `Allegiance.Server` (temp config, `teams:1`, port 7783) and the
**real** MonoGame client (display `:0`, direct-connect). Wayland host has no
input-injection tool, so — exactly like P221/P222/P223 — a **temporary** scripted
diagnostic (fully removed before commit; `Server.cs` and `Game1.cs` diffs contain
no diag remnants) drove the selection fields and held the Fire flag through the
real `FlightInput.Translate` → wire → sim path. To get a multi-hardpoint ship I
TEMPORARILY pointed `SpawnDefaultShip` at the **Adv Fighter** (hull id 15), which
`MountCombatWeapon` armed at **all three** fixed mounts (0, 1, 2) with 'PE Gat
Gun 3' — then reverted the override (`Server.cs` diff empty).

Server log (all 217 real `[projectile]` broadcasts) segmented by the incoming
`state` value:

```
state=0x8000  (AllWeapons,  group fire)   → 87 projectiles in a 3 s window
state=0x4001  (OneWeapon|1, single mt 1)  → 29 projectiles in a 3 s window
state=0x8000  (AllWeapons,  group fire)   → 87 projectiles in a 3 s window
```

- `0x8000` = `AllWeapons` (32768): group-fire mode — the exact P221 behaviour,
  reproduced with no regression. Each 3 s group window fired 87 projectiles
  (three weapons).
- `0x4001` = `OneWeapon` (16384) | `1` (SelectedWeaponMask for mount 1):
  single-weapon mode, mount 1 selected. The intervening 3 s window fired **29**
  projectiles — **exactly 87 ÷ 3 = one weapon's worth**. Had the selection been
  ignored and group-fire still applied, it would have fired ~87 again. So only
  the weapon at mount 1 fired; mounts 0 and 2 did **not**.

That is the complete chain: toggle group-fire off → select mount 1 → the outgoing
`stateM` carries `OneWeapon | 1` → set on the server's `Ship.StateM` →
`Weapon.IsWeaponSelected` arms exactly the weapon whose `MountId == (StateM & 3)
== 1` → one real projectile per fire tick, not three.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings / 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **138 pass, 0 fail** (124 baseline +
  14 new).
- `Allegiance.ClientCore.Tests`: **54 pass, 0 fail** (baseline unchanged).
- `Allegiance.Core.Tests` (full suite, baseline **3037**): run 1 =
  **3036 pass / 1 fail**, run 2 = **3036 pass / 1 fail** — the single failure both
  times is the documented pre-existing machine-state flake
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`,
  which the skill records as persisting on the pristine tree and referencing none
  of this stage's symbols (turret gunner binding, unrelated to P225). No new
  failures. (Core.Tests gained no tests this stage — the additions live in the
  MonoGame client test project.)

## Explicitly out of scope (unchanged from the brief)

- Any non-fixed (turret) mount — `Weapon.IsWeaponSelected`'s turret branch needs a
  gunner + turret-boarding UI that doesn't exist in this client (a separate,
  larger gap).
- `TK_FireMissile`, `TK_DropMine` — no mountable launcher equipment subtype exists
  in this port yet.
- Weapon swap / mount / replace / drop-part (`TK_SwapWeapon1-4`,
  `TK_MountPart`/`TK_ReplacePart`/`TK_UnmountPart`/`TK_DropPart`) — mid-flight
  loadout editing, a separate and much larger UI.
- Native reference source (`~/Development/Allegiance`) — untouched, read-only.
- `Weapon.IsWeaponSelected` and all other sim-side code — untouched.

## Citations (file:line)

- `src/Allegiance.Core/Sim/ShipControlState.cs:32-44` — `OneWeapon`(16384),
  `AllWeapons`(32768), `SelectedWeaponMask`(3), `SelectedWeaponShift`(0).
- `src/Allegiance.Core/Sim/Weapon.cs:363-375` — `IsWeaponSelected` two-mode
  branch (`Weapon.cs:372` = fixed-branch selection).
- `src/Allegiance.ClientCore.MonoGame/Flight/FlightInput.cs` — `FlightInputState`
  `GroupFire`/`SelectedMount` fields; `FireContribution`, `NextMount`, `PrevMount`;
  `Translate` uses `FireContribution`.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `_groupFire`/`_selectedMount`
  fields + `_gfireKeyWasDown`/`_prevWKeyWasDown`/`_nextWKeyWasDown` edge latches;
  `` ` `` / `[` / `]` handlers; `FixedMountCount()`; FlightInputState construction
  carries the selection; `HudFrame` construction carries `GroupFire`/`SelectedMount`;
  `Firing` reads `AllWeapons | OneWeapon`.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudFrame.cs` — `GroupFire`/
  `SelectedMount` fields.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs` —
  `DrawWeaponMode` ("WEAPONS: ALL" / "WEAPONS: MOUNT n").
- `src/Allegiance.ClientCore/State/ClientWorld.cs:215,271-277` — own-ship loadout
  never populated in a stock auto-connect flight (basis for the `FixedMountCount`
  fallback).
