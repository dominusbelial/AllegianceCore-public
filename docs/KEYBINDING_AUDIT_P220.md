# KEYBINDING_AUDIT_P220.md

**Task:** P220 — a full native keybinding audit of the MonoGame client
(`src/Allegiance.ClientCore.MonoGame/`) against native FreeAllegiance's complete
`TrekKey` (`TK_`) command set. The one scoped implementation gap this task arc
found — afterburner input (`TK_FireBooster` → Shift) + sound — already landed on
`master` as `3896551` ("P220: wire afterburner (Shift → TK_FireBooster) — input +
sound"). **This document is the audit itself and nothing else** — no source changes.
**Branch:** `master` (no push — Domingo pushes).

---

## 0. Headline

Every `TK_` constant in `igc.h:226-451` is classified into exactly one bucket:

- **A. Wired** — the MonoGame client already has an equivalent key.
- **B. Gap** — server/sim support already exists in this port; only client input is missing (the actionable bucket).
- **C. Out of scope** — no backing feature exists in this port yet.

Source of truth: `src/Igc/igc.h:226-451` defines **213 real `TK_` commands** plus the
two sentinels `TK_NoKeyMapping=-1` (top of range) and `TK_Max=222` (last) — **215**
enumerated values total. (Note: `TK_Obsolete1` and `TK_ViewRearRight` share value 99;
they are two distinct names over one slot.) Native's own *default key→VK binding table*
is **not** in the reference checkout — grepped `WinTrek/`, `Igc/` and every `.idl`/`.cfg`;
it shipped in a data/registry resource outside this source tree. No default VK is therefore
guessed for anything that can't be confirmed from code; the client's key choices below are
the port's own recollections and are labelled as such.

**Counting the whole range:** A=17, B=9, C=187, plus `TK_NoKeyMapping` + `TK_Max` =
**215 total**. Arithmetic: 17+9+187 = 213 real commands; +2 sentinels = 215.

---

## 1. Correction note (previous research error — correct, don't quietly overwrite)

An earlier pass of this task mis-read `shipIGC.cpp:2987`'s
`if ((m_stateM & coastButtonIGC) && !afterF)` as evidence that the afterburner button
was "dual-purpose" with coast. Re-reading `src/Pigs/PigSrv/PigShip.h:195-209`'s
`InterpretThrustBit` table (`ThrustBooster` → `afterburnerButtonIGC`, a distinct entry
from any coast-related bit) plus this port's own sequential bit layout
(`ShipControlState.CoastButton = 8` → `ShipControlState.AfterburnerButton = 1024`, each a
power-of-two matching a *separate* native flag, `ShipControlState.cs:13-20` vs
`igc.h:810,817`) confirms these are **two independent buttons**. `afterF` in that snippet
is a hull-capability flag (does this ship have an afterburner part mounted — the port's
`Ship.cs:2955` `afterF` reads the same `AfterburnerButton` bit), unrelated to which button
is held. They are not the same input; the vector-lock gap (§3) is distinct from the
now-wired afterburner.

---

## 2. Bucket A — Wired (17)

| TK command | Key | Wiring (file:line) | Notes |
|---|---|---|---|

| `TK_ThrustForward` | W | continuous, FlightInput.cs:79 | forward thrust (CruiseCommand forward bit) |
| `TK_ThrustBackward` | S | continuous, FlightInput.cs:80 | reverse thrust |
| `TK_ThrustLeft` | Q | continuous, FlightInput.cs:81 | left strafe |
| `TK_ThrustRight` | E | continuous, FlightInput.cs:82 | right strafe |
| `TK_PitchUp` | Up | continuous, Game1.cs:464 → FlightInput.cs:69 | pitch axis +1 |
| `TK_PitchDown` | Down | continuous, Game1.cs:465 → FlightInput.cs:69 | pitch axis -1 |
| `TK_YawLeft` | Left | continuous, Game1.cs:466 → FlightInput.cs:68 | yaw axis -1 |
| `TK_YawRight` | Right | continuous, Game1.cs:467 → FlightInput.cs:68 | yaw axis +1 |
| `TK_RollLeft` | A | continuous, Game1.cs:468 → FlightInput.cs:76 | roll axis (sign flipped live) |
| `TK_RollRight` | D | continuous, Game1.cs:469 → FlightInput.cs:76 | roll axis |
| `TK_FireBooster` | Shift | held continuous, Game1.cs:475 → FlightInput.cs:88 | afterburner; commit 3896551 (P220) |
| `TK_ToggleAutoPilot` | G | edge, Game1.cs:525-546 | engage/disengage autopilot (P212) |
| `TK_QuickChatMenu` | R | edge, Game1.cs:415 | opens nested quick-chat menu (P217) |
| `TK_ToggleMouse` | M | edge, Game1.cs:430 | virtual-joystick mouse flight on/off (P208+) |
| `TK_StartChat` | Enter | edge, Game1.cs:397-403 | opens/composes chat line (P214) |
| `TK_Target` | T | edge, Game1.cs:511-523 | cycle nearest-to-crosshair target (P212); approximate mapping |
| `TK_ViewChase` | V | edge, Game1.cs:447-453 | toggles chase ↔ cockpit camera (P211); approximate mapping |

All ten movement keys funnel through `FlightInput.Translate` (`FlightInput.cs:65-100`),
which packs the `ShipControlState` movement bits and the four joystick axes before
`Game1.UpdateFlying` sends them via `CommandControls` (`Game1.cs:576`). `M`, `V`, `R`, `T`,
`G`, `Enter`, `Escape` are edge-triggered toggles handled directly in `UpdateFlying` /
`Update` (`Game1.cs:430/447/415/511/525/397/348`). Escape doubles as chat-cancel and
quit — it opens no in-game menu, so `TK_MainMenu` stays in bucket C.

---
## 3. Bucket B — Gap (9): server/sim support exists, client input does not

These are the actionable items: each already has working C# support server/sim-side, so
each is a **pure input-wiring (or input+small-UI) gap**, not a new feature. None is
implemented by this task — this section documents them and names the natural next task.

- **`TK_LockVector`** — *Lock the current heading and speed.* This port already consumes the
  coast/vector-lock bit: `Ship.cs:2971` `else if ((StateM & ShipControlState.CoastButton) != 0 &&
  !afterF)` is the exact port of native `shipIGC.cpp:2987`'s coast branch, and
  `ShipControlState.CoastButton = 8` (`ShipControlState.cs:13`, native `coastButtonIGC` `igc.h:810`)
  is declared. **No key sets it** — `FlightInput.Translate` only ever sets the six movement bits +
  afterburner (`FlightInput.cs:78-88`). Pure input-wiring gap: bind a key to OR `CoastButton` into
  `stateM`.
- **`TK_FireWeapon`** — *Fire the selected weapon.* The single biggest real gap — **nothing can
  shoot from this client today**, yet the whole server-side fire mechanic is complete and proven.
  Selection bits exist: `ShipControlState.OneWeapon`/`AllWeapons`/`WeaponsMask`
  (`ShipControlState.cs:40-44`, native `oneWeaponIGC`/`allWeaponsIGC`/`weaponsMaskIGC` `igc.h:825-827`),
  and `Weapon.IsWeaponSelected` (`Weapon.cs:363-375`) fires a mounted fixed weapon when the ship's
  `StateM` carries `AllWeapons` (or `OneWeapon` + the `SelectedWeaponMask` selection, `Weapon.cs:372-374`).
  Proof the mechanic works end-to-end: the wingman NPC sets/clears the very same `AllWeapons` bit itself
  (`Ship.cs:4558,4603-4605`) and its guns fire. Confirmed via grep that **no key or mouse button in
  `Game1.cs` sets any weapon-selection or fire bit** (the client input block `Game1.cs:457-475` sets
  movement + afterburner only; there is no `Mouse.ButtonState` fire anywhere). **Bigger than an
  input-wiring fix:** wiring `TK_FireWeapon` alone is useless without a weapon-select/HUD state (which
  gun is selected, group-fire toggle, the `SelectedWeaponMask` bits `ShipControlState.cs:32-36`) — the
  29 selection/inventory commands in bucket C (`TK_PrevWeapon`, `TK_ToggleWeapon1-4`, …) are that UI
  side. Recommend this as the **next P-numbered task** (see §6).
- **`TK_ToggleBooster`** — *Toggle booster rocket on/off.* The afterburner sim is live (hold wired
  in bucket A); `Ship.cs:2955` reads `ShipControlState.AfterburnerButton` as `afterF`. `TK_ToggleBooster`
  is only the latched-on/off variant of the same bit — document-only here, deliberately not implemented
  (per brief). Small input-only gap once a toggle latch is desired.
- **`TK_FireMissile`** — *Fire the selected missile.* `Ship.FireMissile` exists and is directly
  callable (`Ship.cs:2330`), raising `MissileCreatedEvent` so the server broadcasts the real
  `FM_CS_FIRE_MISSILE(70)` (`Ship.cs:2297-2348`; wire type `Wire/FireMissile.cs:62`). Missile lock
  buildup is ported (`Ship.UpdateMissileLock`, `Ship.cs:2405`). The client→server dispatch path also
  exists: `FM_C_FIRE_EXPENDABLE` is handled server-side (`Server.cs:3771,6658`,
  `Wire/FireExpendableRequest.cs:35`). Gap: no client key sends any fire-missile/expendable input, and
  there is no mounted-launcher/ammo UI to choose *which* missile. Needs the same weapon/expendable UI
  as `TK_FireWeapon`.
- **`TK_DropMine`** — *Drop a minefield.* `Ship.DeployMine` exists and is directly callable
  (`Ship.cs:2497`), raising `MineCreatedEvent` for the server's `FM_S_EXPORT(26)/OT_mine` broadcast
  (`Ship.cs:2509-2513`). Deployment can route through the same `FIRE_EXPENDABLE` handler as missiles.
  Gap: no client key triggers it; no dispenser/mine selection UI.
- **`TK_DropChaff`** — *Drop chaff.* `Ship.DeployChaff` (P182) exists and is directly callable
  (`Ship.cs:2617`), returning the spoofed missile list; `FIRE_EXPENDABLE` is the intended dispatch
  (`Server.cs:6658`). Gap: no client key triggers it.
- **`TK_Suicide`** — *Kill yourself.* Fully wired server-side: `Wire/Suicide.cs` is the
  `FM_C_SUICIDE(80)` codec (`Fmid=80`) and `Server.HandleSuicide` (`Server.cs:3869`) re-runs the exact
  combat-kill path (launcher NULL, `FedSrv.CPP:6335-6350`). Client simply lacks a key to *send* it —
  the smallest, cleanest input-wiring gap in this bucket (no new sim work, no UI).
- **`TK_Reload`** — *Reload (ammo from cargo).* `FM_CS_RELOAD(129)` is handled server-side
  (`Server.cs:3588`, `HandleReload` at 6397; `Wire/Reload.cs`), a P192 port (`FedSrv.CPP:6504-6653`).
  Needs a live cargo hold to pull from and a mounted weapon to fill — no client reload key exists.
- **`TK_Ripcord`** — *Ripcord (jump to a sector).* Full sim/teleport support exists (P112/P113):
  sector ripcord countdown/revalidation (`Model/Sector.cs:543-548`) and `Server.PerformRipcordTeleport`.
  Client initiation wire is coded: `Wire/RipcordRequest.cs` (`RIPCORD_REQUEST(184)`,
  `FedMessageDumper.cs:45`). Gap: no client key plus no target-sector picker UI (a ripcord must name a
  destination sector).

---
## 4. Bucket C — Out of scope (187): no backing feature in this port yet

One line per family — the point is a complete accounting, not investigation of things that
cannot be wired yet. Every one of the 187 is named below (counts in parentheses).

### Targeting — the full target-selection matrix (64)
`TK_TargetNearest`, `TK_TargetPrev`, `TK_TargetFriendly`, `TK_TargetFriendlyNearest`, `TK_TargetFriendlyPrev`, `TK_TargetEnemy`, `TK_TargetEnemyNearest`, `TK_TargetEnemyPrev`, `TK_TargetEnemyBase`, `TK_TargetEnemyBaseNearest`, `TK_TargetEnemyBasePrev`, `TK_TargetFriendlyBase`, `TK_TargetFriendlyBaseNearest`, `TK_TargetFriendlyBasePrev`, `TK_TargetCommand`, `TK_TargetCenter`, `TK_TargetHostile`, `TK_TargetSelf`, `TK_TargetNothing`, `TK_TargetWarp`, `TK_TargetWarpNearest`, `TK_TargetWarpPrev`, `TK_TargetEnemyBomber`, `TK_TargetEnemyBomberNearest`, `TK_TargetEnemyBomberPrev`, `TK_TargetEnemyFighter`, `TK_TargetEnemyFighterNearest`, `TK_TargetEnemyFighterPrev`, `TK_TargetEnemyConstructor`, `TK_TargetEnemyConstructorNearest`, `TK_TargetEnemyConstructorPrev`, `TK_TargetEnemyMiner`, `TK_TargetEnemyMinerNearest`, `TK_TargetEnemyMinerPrev`, `TK_TargetEnemyMajorBase`, `TK_TargetEnemyMajorBaseNearest`, `TK_TargetEnemyMajorBasePrev`, `TK_TargetFriendlyBomber`, `TK_TargetFriendlyBomberNearest`, `TK_TargetFriendlyBomberPrev`, `TK_TargetFriendlyFighter`, `TK_TargetFriendlyFighterNearest`, `TK_TargetFriendlyFighterPrev`, `TK_TargetFriendlyConstructor`, `TK_TargetFriendlyConstructorNearest`, `TK_TargetFriendlyConstructorPrev`, `TK_TargetFriendlyMiner`, `TK_TargetFriendlyMinerNearest`, `TK_TargetFriendlyMinerPrev`, `TK_TargetFriendlyLifepod`, `TK_TargetFriendlyLifepodNearest`, `TK_TargetFriendlyLifepodPrev`, `TK_TargetFriendlyMajorBase`, `TK_TargetFriendlyMajorBaseNearest`, `TK_TargetFriendlyMajorBasePrev`, `TK_TargetFlag`, `TK_TargetFlagNearest`, `TK_TargetFlagPrev`, `TK_TargetArtifact`, `TK_TargetArtifactNearest`, `TK_TargetArtifactPrev`, `TK_TargetAlliedBase`, `TK_TargetAlliedBaseNearest`, `TK_TargetAlliedBasePrev`  
— No class/specific-target cycling exists; the POC wires only the single `TK_Target` cycle (bucket A, T).

### Throttle presets (6)
`TK_ThrottleUp`, `TK_ThrottleDown`, `TK_ThrottleZero`, `TK_Throttle33`, `TK_Throttle66`, `TK_ThrottleFull`  
— Throttle is an analog −1..+1 axis with no preset quantisation.

### Views — only `TK_ViewChase` wired (17)
`TK_ViewFrontLeft`, `TK_ViewFrontRight`, `TK_ViewLeft`, `TK_ViewRight`, `TK_ViewExternal`, `TK_ViewFlyby`, `TK_ViewMissile`, `TK_ViewTarget`, `TK_ViewTurret`, `TK_ViewBase`, `TK_ViewSector`, `TK_ViewCommand`, `TK_ViewRear`, `TK_ViewRearLeft`, `TK_ViewRearRight`, `TK_ViewExternalOrbit`, `TK_ViewExternalStation`  
— No such camera family in the POC (V only toggles chase↔cockpit).

### Weapon selection / inventory / cargo — UI side of the bucket-B weapon gap (29)
`TK_PrevWeapon`, `TK_NextWeapon`, `TK_ToggleGroupFire`, `TK_PrevPart`, `TK_NextPart`, `TK_MountPart`, `TK_ReplacePart`, `TK_UnmountPart`, `TK_DropPart`, `TK_NextCargo`, `TK_DropCargo`, `TK_SwapWeapon1`, `TK_SwapWeapon2`, `TK_SwapWeapon3`, `TK_SwapWeapon4`, `TK_SwapTurret1`, `TK_SwapTurret2`, `TK_SwapTurret3`, `TK_SwapTurret4`, `TK_SwapMissile`, `TK_SwapChaff`, `TK_SwapShield`, `TK_SwapCloak`, `TK_SwapAfterburner`, `TK_SwapMine`, `TK_ToggleWeapon1`, `TK_ToggleWeapon2`, `TK_ToggleWeapon3`, `TK_ToggleWeapon4`  
— No cargo/inventory/weapon-select UI exists; these are the selection half of the `TK_FireWeapon` recommendation (§6).

### Chat / command-accept subsystem (10)
`TK_NextCommMsg`, `TK_PrevCommMsg`, `TK_CycleChatMsgs`, `TK_ChatPageUp`, `TK_ChatPageDown`, `TK_AcceptCommand`, `TK_ClearCommand`, `TK_RejectCommand`, `TK_ExecuteCommand`, `TK_MatchSpeed`  
— No chat scroll or command-accept system.

### Console modes (9)
`TK_ConModeGameState`, `TK_ConModeCombat`, `TK_ConModeCommand`, `TK_ConModeNav`, `TK_ConModeInventory`, `TK_ConModeInvest`, `TK_ConModeTeam`, `TK_ConModeMiniTeam`, `TK_ConModeTeleport`  
— No console-mode system.

### Zoom / LOD / radar / grid / misc view toggles (13)
`TK_ZoomOut`, `TK_ZoomIn`, `TK_FOVOut`, `TK_FOVIn`, `TK_ToggleLODSlider`, `TK_LODUp`, `TK_LODDown`, `TK_CycleRadar`, `TK_ToggleGrid`, `TK_ToggleCommand`, `TK_ToggleCollisions`, `TK_TrackCommandView`, `TK_RollModifier`  
— No such render toggles.

### Vertical thrusters (2)
`TK_ThrustUp`, `TK_ThrustDown`  
— No vertical-strafe key wired.

### Misc gameplay / meta (5)
`TK_MainMenu`, `TK_Help`, `TK_ScrnShot`, `TK_Obsolete1`, `TK_ToggleCloak`  
— Escape only quits the POC (no menu screen, so `TK_MainMenu` is here); `TK_ToggleCloak` is inert bookkeeping only (`MountedPart.cs:19`, `Ship.cs:1690`); `TK_Obsolete1` is the unused sentinel slot shared with `TK_ViewRearRight`.

### Training missions (6)
`TK_PauseTM`, `TK_CommandFinishTM`, `TK_SkipTMGoal`, `TK_IncreaseTMClockSpeed`, `TK_DecreaseTMClockSpeed`, `TK_ResetTMClockSpeed`  
— No training-mission system.

### Turrets beyond the wired gunner basis (5)
`TK_OccupyNextTurret`, `TK_PromoteTurret1`, `TK_PromoteTurret2`, `TK_PromoteTurret3`, `TK_PromoteTurret4`  
— No turret-occupancy/selection UI.

### Voting (2)
`TK_VoteYes`, `TK_VoteNo`  
— P187 server balloting exists, but the POC has no voting UI.

### Debug test hooks (9)
`TK_DebugTest1`, `TK_DebugTest2`, `TK_DebugTest3`, `TK_DebugTest4`, `TK_DebugTest5`, `TK_DebugTest6`, `TK_DebugTest7`, `TK_DebugTest8`, `TK_DebugTest9`  
— Test-only, never shipped to players.

### Quick-chat 1-0 as distinct commands (10)
`TK_QuickChat1`, `TK_QuickChat2`, `TK_QuickChat3`, `TK_QuickChat4`, `TK_QuickChat5`, `TK_QuickChat6`, `TK_QuickChat7`, `TK_QuickChat8`, `TK_QuickChat9`, `TK_QuickChat0`  
— Superseded: the P217 nested single-key quick-chat menu (R + descendants, `docs/CLIENTCORE_MONOGAME_QUICKCHAT_P217.md`) already covers every send, so distinct number-shortcuts are unnecessary — classified C (covered), not A.

---
## 5. Accounting (the arithmetic)

| Bucket | Count |
|---|---|
| A — Wired | 17 |
| B — Gap | 9 |
| C — Out of scope | 187 |
| **Real commands** | **213** (17+9+187) |
| + `TK_NoKeyMapping` (−1) + `TK_Max` (222) sentinels | +2 |
| **Total enumerated in `igc.h:226-451`** | **215** |

---
## 6. Recommended next P-numbered task

**The weapon-fire gap (`TK_FireWeapon`).** It is the only bucket-B item that is both (a)
fully supported server-side (the `StateM` `AllWeapons`/`OneWeapon` bits drive a working
`Weapon.IsWeaponSelected` fire mechanic — proven by the wingman NPC) and (b) the difference
between a *flyable* client and one that can *fight*. It is deliberately bigger than a pure
input-wiring fix because firing is meaningless without a weapon-select/HUD state (which gun
is selected, single vs group fire, the `SelectedWeaponMask` bits) — so the natural scope is:
client weapon-selection state + HUD + a fire key that ORs the fire bit into `stateM`, plus
(optionally) the selection keys from bucket C. Next runner-up for a *small* win: `TK_Suicide`,
a one-key client send of the already-complete `FM_C_SUICIDE(80)` path.
