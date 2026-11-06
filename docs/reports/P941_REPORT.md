# P941 — Native default controls for new players; training prompts that match the keys

**Base:** Claude's local master at dispatch; work in `hermes/p941` (own worktree), no push.
**Native reference:** `~/Development/Allegiance/src/` (training sources read as ISO-8859-1) and
`~/Development/Artwork/defaultinputmap.mdl`.

## Why

A Windows player on v0.1.3 reported that Q/E did not roll (A/D did), that SPACE fired the guns although the
tutorial says SPACE is the booster, that the booster "worked once", and that the mouse would not fire guns or
missiles — only the wheel throttle worked. The cause is that the port shipped its own default binding table
(`InputActionCatalog.Default`), not native's `defaultinputmap.mdl`:

| symptom | port default (pre-P941) | native default |
|---|---|---|
| roll | `A` / `D` (port) | `Q` / `E` — `defaultinputmap.mdl:9-10` (TK_RollLeft 21, TK_RollRight 22) |
| lateral thrust | `Q` / `E` (port) | `A` / `D` — `defaultinputmap.mdl:17-18` (TK_ThrustLeft 44, TK_ThrustRight 45) |
| booster | `LeftShift` | `Space` — `defaultinputmap.mdl:23` (TK_FireBooster 50) |
| fire weapon | `Space` | **mouse left** — `mouseButtonCommandMap` (0,55), `defaultinputmap.mdl:111` |
| fire missile | `LeftControl` | **mouse middle** — `(2,56)`, `defaultinputmap.mdl:112` |
| target centre | `'` | **mouse right** — `(1,90)`, `defaultinputmap.mdl:115` |

The FreeAllegiance training text was rewritten for its keymap while the 1999 voice-overs that name the old keys
were muted (`mission3.cpp:643` "voiceover does not match keymap", `:942`, `mission2.cpp:746`, `mission3.cpp:451`).
**Native bug:** those muted prompts still pass the wrong recording to `CreateTooLongCondition(30.0f, <sound>)`, so
a hesitating player hears the old wrong key 30 s later.

## 1. Native defaults for a fresh profile; the port layout is a named preset

### Files

- `src/Allegiance.ClientCore.Game/Rendering/InputLayouts.cs` (**new**) — the two selectable presets
  (`InputLayout.Native`, `InputLayout.Port`), the native mouse/wheel map (`MouseButtonBinding`,
  `mouseButtonCommandMap`, `defaultinputmap.mdl:109-116`) and the relocation table with a native citation per row.
- `Rendering/InputBindings.cs` — `ActionMap` owns the selected `InputLayout`: the per-layout default table,
  `SetLayout`, `MouseButtonOf`, `LabelOf`, the native alternates (`[`/INSERT, `]`/DELETE) and the P941 keymap
  document (`{"layout":...,"bindings":{...}}`, legacy flat files accepted).
- `Rendering/KeyBindingStore.cs` — a fresh profile (no keymap file) starts on the native layout; a pre-P941 file
  has no layout marker; it holds only the player's own rebinds, so it migrates to the **native layout** with those rebinds kept on top (Claude's review change: the old port defaults broke the training prompts, and a player who only pressed OK in F6 wrote `{}`); `Reload` without a readable file falls back to the native
  map (native `LoadMap(DEFAULTINPUTMAP_FILE)`, `trekinput.cpp:2436-2444`); `CycleLayout` persists the preset.
- `Rendering/KeyBindingController.cs` / `KeyBindingRenderer.cs` — `P` cycles the preset in F6; the header and F6
  rows name the preset and show the mouse/wheel binding.
- `Game1.KeyBind.cs` — the popup's P handler, the preset/mouse-flight sync, and the `ALLEGIANCE_P941_PROOF`
  capture sequence.
- `Game1.Rendering.cs` — the Controls popup now draws after the docked/hangar/menu panes, so F6 (and its preset
  key) is reachable from the hangar as well as in flight.
- `Rendering/HelpManualModel.cs` — the F1 pages that name keys are generated from the LIVE map.

### Rules

1. Every action with a native keyboard default takes it (`InputActionDef.NativeDefault`, decoded by
   `DefaultInputMap` from `keymapCommandMap`, `defaultinputmap.mdl:3-107`). 99 catalogue entries carry one:
   96 are applied verbatim, the two cruise-throttle entries also keep the native wheel, and `ToggleCommand`'s
   relocation is native's own `Shift+F3` (`mdl:63`).
2. The five commands native wires to the mouse/wheel lose their keyboard chord and ride the mouse:
   left = TK_FireWeapon (55), middle = TK_FireMissile (56), wheel up = TK_ThrottleUp (58), wheel down =
   TK_ThrottleDown (59), right = TK_TargetCenter (90) — `mouseButtonCommandMap`, `defaultinputmap.mdl:109-116`.
   The throttle keeps `=`/`-` as well (`mdl:29-30`).
3. **No action loses its only binding.** Every action the port bound before P941 is still bound: 118 actions that
   native leaves unbound keep their port chord (the table below marks them), 44 were unbound in both, and the
   three mouse-only commands keep a binding (a mouse button).
4. Where a port-only command held a chord a native default claims, native wins the chord (the P623/P624/P626
   rule) and the port command moves to a free chord in the same family — the relocation table below.
5. The F6 screen offers the pre-P941 key set as the named preset **"Port layout"**. A legacy keymap file moves to the native layout with the player's own rebinds kept (`Legacy_flat_keymap_file_migrates_to_native_keeping_the_players_rebinds`); `P` in F6 restores the old key set.

### Mouse-left fires in flight; target picking stays reachable the native way

- With the native layout a fresh profile starts with the **virtual joystick ON** (native's TK_ToggleMouse mode,
  `WinTrek.cpp:9289-9298` / `:9419-9431`), so the left button is TK_FireWeapon, the middle TK_FireMissile, the
  right TK_TargetCenter and the wheel TK_ThrottleUp/Down — exactly `mouseButtonCommandMap`. That is the mode
  native's training text describes ("Left click to fire your main weapon", `mission3.cpp:646`; the on-screen
  training callouts "CLICK RIGHT MOUSE BUTTON TO TARGET CENTER", `indicator.cpp:651-657`).
- **Target picking, the native way:** press **V** (TK_ToggleMouse, `mdl:104`) to turn the virtual joystick off —
  native's own toggle — and the pointer picks again through the port's port of `ResolvePick`
  (`console.cpp:1857-1900`) / `PickObject` (`console.cpp:1912-1990`): left selects a same-side ship (command
  recipient), right takes the legal command target under the cursor, a drag beyond 100 px² cancels
  (`console.cpp:1915-1925`). In the **command camera** the pick path is pointer-based regardless of the mode
  (the port's `HandleFlightMouseButtons` skips the virtual joystick when the command camera is active), so the
  mission-5 "Left click on a Tower Layer / Build / Miner" prompts work in the command view.
- The port preset keeps the old default (pointer mode, mouse flight opt-in on its `ToggleMouse` key), so an
  existing player's mouse behaviour is unchanged.

### Relocations (28)

Each row is a port-only command (native binds no key for it) whose old chord a native default claims.

| Action | Native-layout binding | Why the port key moved |
|---|---|---|
| ToggleStarmap | Insert | Tab is TK_ThrustForward (defaultinputmap.mdl:21); Insert is free because native TK_ThrustUp takes W (mdl:19). |
| PromoteTurret1 | Ctrl + Shift + 1 | Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65). |
| PromoteTurret2 | Ctrl + Shift + 2 | Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65). |
| PromoteTurret3 | Ctrl + Shift + 3 | Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65). |
| PromoteTurret4 | Ctrl + Shift + 4 | Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65). |
| MouseDeadzone | Ctrl + Shift + 5 | Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69). |
| MouseResponseCurve | Ctrl + Shift + 6 | Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69). |
| MouseSensitivityDown | Ctrl + Shift + 7 | Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69). |
| MouseSensitivityUp | Ctrl + Shift + 8 | Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69). |
| PilotInfo | Ctrl + Shift + P | P is native TK_ToggleAutoPilot (defaultinputmap.mdl:13); Ctrl+P stays the profile editor. |
| ReplacePart | Ctrl + Shift + U | Shift+U is native TK_TargetEnemyBaseNearest (defaultinputmap.mdl:48). |
| UnmountPart | Alt + U | Ctrl+U is native TK_TargetEnemyBasePrev (defaultinputmap.mdl:49). |
| ToggleBooster | Ctrl + Shift + B | Shift+B is native TK_TargetFriendlyBaseNearest (defaultinputmap.mdl:51). |
| Scoreboard | Alt + B | B is native TK_TargetFriendlyBase (defaultinputmap.mdl:50); Ctrl+B its previous target (mdl:52). |
| PrevWeapon | Home | [ and ] are native TK_AcceptCommand/TK_RejectCommand (defaultinputmap.mdl:12,14). |
| NextWeapon | End | [ and ] are native TK_AcceptCommand/TK_RejectCommand (defaultinputmap.mdl:12,14). |
| ClearCommand | Shift + Delete | Delete is native TK_RejectCommand (defaultinputmap.mdl:15). |
| Target | Alt + T | T is native TK_TargetFriendly (defaultinputmap.mdl:41). |
| MatchSpeed | Alt + / | '/' is native TK_ConModeGameState (defaultinputmap.mdl:16). |
| Director | Ctrl + F2 | F2 is native TK_ConModeCombat (defaultinputmap.mdl:35). |
| ViewExternalOrbit | Shift + F4 | F4 is native TK_ConModeInventory (defaultinputmap.mdl:38). |
| ViewFlyby | Shift + F5 | F5 is native TK_ConModeInvest (defaultinputmap.mdl:39). |
| ViewTarget | Shift + F7 | F7 is native TK_ConModeNav (defaultinputmap.mdl:37). |
| NextPart | Alt + . | '.' is native TK_CycleRadar (defaultinputmap.mdl:79). |
| ToggleMusic | Ctrl + Alt + J | Alt+J is native TK_ToggleLODSlider (defaultinputmap.mdl:22). |
| CycleView | M | V is native TK_ToggleMouse (defaultinputmap.mdl:104). |
| ToggleGroupFire | Ctrl + ` | '`' is native TK_QuickChatMenu (defaultinputmap.mdl:18). |
| ToggleCommand | Shift + F3 | Shift+F6 is native TK_ConModeMiniTeam (defaultinputmap.mdl:101); Shift+F3 is TK_ToggleCommand (mdl:63). |

### Binding table (all 291 catalogue actions)

`Old port default` = the pre-P941 key (the "Port layout" preset). `Native-default binding` = what a fresh
profile gets. 118 rows marked *native leaves it unbound; port keeps it* are the actions native does not bind at
all (TK_PitchUp/Yaw…, TK_TargetSelf, the swap/mount commands, the port-only panes) — the port keeps its key so
no command disappears.

| Action | Old port default | Native-default binding | Native source |
|---|---|---|---|
| ThrustForward | W | Tab | keyCommandMap line 21 (TK_ThrustForward) |
| ThrustBackward | S | S | keyCommandMap line 22 (TK_ThrustBackward) |
| ThrustLeft | Q | A | keyCommandMap line 17 (TK_ThrustLeft) |
| ThrustRight | E | D | keyCommandMap line 18 (TK_ThrustRight) |
| ThrustUp | Insert | W | keyCommandMap line 19 (TK_ThrustUp) |
| ThrustDown | X | X | keyCommandMap line 20 (TK_ThrustDown) |
| LockVector | L | L | keyCommandMap line 28 (TK_LockVector) |
| PitchUp | Up | Up (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_PitchUp |
| PitchDown | Down | Down (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_PitchDown |
| YawLeft | Left | Left (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_YawLeft |
| YawRight | Right | Right (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_YawRight |
| RollLeft | A | Q | keyCommandMap line 9 (TK_RollLeft) |
| RollRight | D | E | keyCommandMap line 10 (TK_RollRight) |
| RollModifier | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| FireBooster | LeftShift | Space | keyCommandMap line 23 (TK_FireBooster) |
| ThrottleUp | = | = / Wheel Up | mouseButtonCommandMap line 113 (button 9, TK_58) plus keyCommandMap line 29 (TK_58) |
| ThrottleDown | - | - / Wheel Down | mouseButtonCommandMap line 114 (button 8, TK_59) plus keyCommandMap line 30 (TK_59) |
| ThrottleZero | Backspace | Backspace | keyCommandMap line 31 (TK_ThrottleZero) |
| Throttle33 | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| Throttle66 | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| PlayerPings | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| ThrottleFull | \ | \ | keyCommandMap line 32 (TK_ThrottleFull) |
| MatchSpeed | / | Alt + / (relocated) | '/' is native TK_ConModeGameState (defaultinputmap.mdl:16). |
| MouseWheel | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| FireWeapon | Space | Mouse Left | mouseButtonCommandMap line 111 (button 0, TK_55); no keyboard chord in keyCommandMap |
| FireMissile | LeftControl | Mouse Middle | mouseButtonCommandMap line 112 (button 2, TK_56); no keyboard chord in keyCommandMap |
| ToggleGroupFire | ` | Ctrl + ` (relocated) | '`' is native TK_QuickChatMenu (defaultinputmap.mdl:18). |
| PrevWeapon | [ | Home (relocated) | [ and ] are native TK_AcceptCommand/TK_RejectCommand (defaultinputmap.mdl:12,14). |
| NextWeapon | ] | End (relocated) | [ and ] are native TK_AcceptCommand/TK_RejectCommand (defaultinputmap.mdl:12,14). |
| ToggleWeapon1 | D1 | D1 | keyCommandMap line 73 (TK_ToggleWeapon1) |
| ToggleWeapon2 | D2 | D2 | keyCommandMap line 74 (TK_ToggleWeapon2) |
| ToggleWeapon3 | D3 | D3 | keyCommandMap line 75 (TK_ToggleWeapon3) |
| ToggleWeapon4 | D4 | D4 | keyCommandMap line 76 (TK_ToggleWeapon4) |
| ToggleBooster | Shift + B | Ctrl + Shift + B (relocated) | Shift+B is native TK_TargetFriendlyBaseNearest (defaultinputmap.mdl:51). |
| DropChaff | C | Z | keyCommandMap line 60 (TK_DropChaff) |
| ToggleCloak | F | G | keyCommandMap line 55 (TK_ToggleCloak) |
| Ripcord | Ctrl + R | Ctrl + R | keyCommandMap line 57 (TK_Ripcord) |
| DropMine | N | C | keyCommandMap line 56 (TK_DropMine) |
| Suicide | Alt + K | Ctrl + Shift + D | keyCommandMap line 53 (TK_Suicide) |
| PromoteTurret1 | Ctrl + D1 | Ctrl + Shift + D1 (relocated) | Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65). |
| PromoteTurret2 | Ctrl + D2 | Ctrl + Shift + D2 (relocated) | Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65). |
| PromoteTurret3 | Ctrl + D3 | Ctrl + Shift + D3 (relocated) | Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65). |
| PromoteTurret4 | Ctrl + D4 | Ctrl + Shift + D4 (relocated) | Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65). |
| SwapWeapon1 | unbound | Ctrl + D1 | keyCommandMap |
| SwapWeapon2 | unbound | Ctrl + D2 | keyCommandMap |
| SwapWeapon3 | unbound | Ctrl + D3 | keyCommandMap |
| SwapWeapon4 | unbound | Ctrl + D4 | keyCommandMap |
| SwapTurret1 | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| SwapTurret2 | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| SwapTurret3 | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| SwapTurret4 | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| SwapMissile | unbound | Ctrl + D5 | keyCommandMap |
| SwapChaff | unbound | Ctrl + D6 | keyCommandMap |
| SwapShield | unbound | Ctrl + D7 | keyCommandMap |
| SwapCloak | unbound | Ctrl + D8 | keyCommandMap |
| SwapAfterburner | unbound | Ctrl + D9 | keyCommandMap |
| SwapMine | unbound | Ctrl + D0 | keyCommandMap |
| MountPart | Shift + M | Shift + M (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_MountPart |
| UnmountPart | Ctrl + U | Alt + U (relocated) | Ctrl+U is native TK_TargetEnemyBasePrev (defaultinputmap.mdl:49). |
| ReplacePart | Shift + U | Ctrl + Shift + U (relocated) | Shift+U is native TK_TargetEnemyBaseNearest (defaultinputmap.mdl:48). |
| DropPart | Ctrl + Delete | Ctrl + Delete (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_DropPart |
| NextCargo | , | , | keyCommandMap line 72 (TK_NextCargo) |
| DropCargo | Ctrl + , | Ctrl + , | keyCommandMap line 77 (TK_DropCargo) |
| Reload | Shift + R | R | keyCommandMap line 78 (TK_Reload) |
| PrevPart | Shift + . | Shift + . (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_PrevPart |
| NextPart | . | Alt + . (relocated) | '.' is native TK_CycleRadar (defaultinputmap.mdl:79). |
| Target | T | Alt + T (relocated) | T is native TK_TargetFriendly (defaultinputmap.mdl:41). |
| ToggleAutopilot | G | P | keyCommandMap line 16 (TK_ToggleAutoPilot) |
| TargetEnemyNearest | U | Shift + F | keyCommandMap line 45 (TK_TargetEnemyNearest) |
| TargetFriendlyNearest | Shift + U | Shift + T | keyCommandMap line 42 (TK_TargetFriendlyNearest) |
| TargetEnemyBaseNearest | Z | Shift + U | keyCommandMap line 48 (TK_TargetEnemyBaseNearest) |
| TargetFriendlyBaseNearest | Shift + Z | Shift + B | keyCommandMap line 51 (TK_TargetFriendlyBaseNearest) |
| TargetNothing | ; | ; (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_TargetNothing |
| TargetCenter | ' | Mouse Right | mouseButtonCommandMap line 115 (button 1, TK_90); no keyboard chord in keyCommandMap |
| TargetEnemyBomber | O | O | keyCommandMap line 80 (TK_TargetEnemyBomber) |
| TargetEnemyBomberNearest | Shift + O | Shift + O | keyCommandMap line 85 (TK_TargetEnemyBomberNearest) |
| TargetEnemyBomberPrev | Ctrl + O | Ctrl + O | keyCommandMap line 90 (TK_TargetEnemyBomberPrev) |
| TargetEnemyFighter | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemyFighterNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemyFighterPrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemyConstructor | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemyConstructorNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemyConstructorPrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemyMiner | I | I | keyCommandMap line 81 (TK_TargetEnemyMiner) |
| TargetEnemyMinerNearest | Shift + I | Shift + I | keyCommandMap line 86 (TK_TargetEnemyMinerNearest) |
| TargetEnemyMinerPrev | Ctrl + I | Ctrl + I | keyCommandMap line 91 (TK_TargetEnemyMinerPrev) |
| TargetEnemyMajorBase | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemyMajorBaseNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemyMajorBasePrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyBomber | K | K | keyCommandMap line 82 (TK_TargetFriendlyBomber) |
| TargetFriendlyBomberNearest | Shift + K | Shift + K | keyCommandMap line 87 (TK_TargetFriendlyBomberNearest) |
| TargetFriendlyBomberPrev | Ctrl + K | Ctrl + K | keyCommandMap line 92 (TK_TargetFriendlyBomberPrev) |
| TargetFriendlyFighter | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyFighterNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyFighterPrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyConstructor | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyConstructorNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyConstructorPrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyMiner | H | H | keyCommandMap line 83 (TK_TargetFriendlyMiner) |
| TargetFriendlyMinerNearest | Shift + H | Shift + H | keyCommandMap line 88 (TK_TargetFriendlyMinerNearest) |
| TargetFriendlyMinerPrev | Ctrl + H | Ctrl + H | keyCommandMap line 93 (TK_TargetFriendlyMinerPrev) |
| TargetFriendlyLifepod | J | J | keyCommandMap line 84 (TK_TargetFriendlyLifepod) |
| TargetFriendlyLifepodNearest | Shift + J | Shift + J | keyCommandMap line 89 (TK_TargetFriendlyLifepodNearest) |
| TargetFriendlyLifepodPrev | Ctrl + J | Ctrl + J | keyCommandMap line 94 (TK_TargetFriendlyLifepodPrev) |
| TargetFriendlyMajorBase | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyMajorBaseNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFriendlyMajorBasePrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetHostile | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| ExecuteCommand | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetCommand | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| AcceptCommand | Home | [ | keyCommandMap line 12 (TK_AcceptCommand) |
| RejectCommand | End | ] | keyCommandMap line 14 (TK_RejectCommand) |
| ClearCommand | Delete | Shift + Delete (relocated) | Delete is native TK_RejectCommand (defaultinputmap.mdl:15). |
| CycleCommandee | Shift + PageDown | Shift + PageDown (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| IssueBuild | Alt + I | Alt + I (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LeaderCommand | Ctrl + X | Ctrl + X (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| VoteYes | Y | Y | keyCommandMap line 105 (TK_VoteYes) |
| VoteNo | Ctrl + N | N | keyCommandMap line 106 (TK_VoteNo) |
| TargetNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetPrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetEnemy | unbound | F | keyCommandMap |
| TargetEnemyPrev | Ctrl + F | Ctrl + F | keyCommandMap line 46 (TK_TargetEnemyPrev) |
| TargetFriendly | unbound | T | keyCommandMap |
| TargetFriendlyPrev | unbound | Ctrl + T | keyCommandMap |
| TargetEnemyBase | unbound | U | keyCommandMap |
| TargetEnemyBasePrev | unbound | Ctrl + U | keyCommandMap |
| TargetFriendlyBase | unbound | B | keyCommandMap |
| TargetFriendlyBasePrev | Ctrl + B | Ctrl + B | keyCommandMap line 52 (TK_TargetFriendlyBasePrev) |
| TargetWarp | Alt + W | Alt + W (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_TargetWarp |
| TargetWarpNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetWarpPrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFlag | Alt + G | Alt + G (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_TargetFlag |
| TargetFlagNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetFlagPrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetArtifact | Alt + A | Alt + A (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_TargetArtifact |
| TargetArtifactNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetArtifactPrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetAlliedBase | Alt + Z | Alt + Z (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_TargetAlliedBase |
| TargetAlliedBaseNearest | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetAlliedBasePrev | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| TargetSelf | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| ConModeCombat | Alt + F2 | F2 | keyCommandMap line 35 (TK_ConModeCombat) |
| ConModeGameState | Alt + F7 | / | keyCommandMap line 24 (TK_ConModeGameState) |
| ConModeNav | Shift + F7 | F7 | keyCommandMap line 37 (TK_ConModeNav) |
| ConModeInventory | Shift + F4 | F4 | keyCommandMap line 38 (TK_ConModeInventory) |
| ConModeMiniTeam | Alt + F6 | Shift + F6 | keyCommandMap line 101 (TK_ConModeMiniTeam) |
| ViewExternal | Alt + V | Alt + V (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewExternal |
| ToggleGrid | Ctrl + . | Ctrl + . | keyCommandMap line 54 (TK_ToggleGrid) |
| OccupyNextTurret | Alt + , | Alt + , (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_OccupyNextTurret |
| MainMenu | Esc | Esc | keyCommandMap line 11 (TK_MainMenu) |
| CycleView | V | M (relocated) | V is native TK_ToggleMouse (defaultinputmap.mdl:104). |
| ViewFrontLeft | Alt + D1 | Alt + D1 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewFrontLeft |
| ViewFrontRight | Alt + D2 | Alt + D2 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewFrontRight |
| ViewLeft | Alt + D3 | Alt + D3 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewLeft |
| ViewRight | Alt + D4 | Alt + D4 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewRight |
| ViewRearLeft | D6 | D6 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewRearLeft |
| ViewRear | D7 | D7 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewRear |
| ViewRearRight | D8 | D8 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewRearRight |
| CycleRadar | . | . | keyCommandMap line 100 (TK_CycleRadar) |
| CycleHudLevel | D0 | D0 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ViewMissile | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| ViewChase | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| ViewTurret | unbound | unbound | no keyCommandMap entry for this command and no port binding |
| ViewExternalOrbit | F4 | Shift + F4 (relocated) | F4 is native TK_ConModeInventory (defaultinputmap.mdl:38). |
| ViewFlyby | F5 | Shift + F5 (relocated) | F5 is native TK_ConModeInvest (defaultinputmap.mdl:39). |
| ViewTarget | F7 | Shift + F7 (relocated) | F7 is native TK_ConModeNav (defaultinputmap.mdl:37). |
| ViewBase | F10 | F10 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewBase |
| ViewExternalStation | F11 | F11 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_ViewExternalStation |
| ToggleLODSlider | unbound | Alt + J | keyCommandMap |
| LODUp | Alt + [ | Alt + [ | keyCommandMap line 33 (TK_LODUp) |
| LODDown | Alt + ] | Alt + ] | keyCommandMap line 34 (TK_LODDown) |
| FreeCam | F8 | F8 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| OrbitCam | F9 | F9 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| Director | F2 | Ctrl + F2 (relocated) | F2 is native TK_ConModeCombat (defaultinputmap.mdl:35). |
| FollowCam | Ctrl + F3 | Ctrl + F3 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ViewCommand | F3 | F3 | keyCommandMap |
| ToggleCommand | Shift + F6 | Shift + F3 (relocated) | Shift+F6 is native TK_ConModeMiniTeam (defaultinputmap.mdl:101); Shift+F3 is TK_ToggleCommand (mdl:63). |
| TrackCommandView | Ctrl + F7 | Ctrl + F7 (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_TrackCommandView |
| DroneFollow | Ctrl + F8 | Ctrl + F8 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| DroneCockpit | Ctrl + F9 | Ctrl + F9 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| CommandSelectionAdd | LeftShift | LeftShift (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| SectorHighlightModifier | LeftControl | LeftControl (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ZoomOut | - | - | keyCommandMap line 5 (TK_ZoomOut) |
| ZoomIn | + | + | keyCommandMap line 7 (TK_ZoomIn) |
| ToggleHud | Ctrl + F1 | Ctrl + F1 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ToggleMouse | M | V | keyCommandMap line 104 (TK_ToggleMouse) |
| InvertMouse | Ctrl + M | Ctrl + M (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ToggleStarmap | Tab | Insert (relocated) | Tab is native TK_ThrustForward (defaultinputmap.mdl:21); Insert is free because native TK_ThrustUp takes W (mdl:19). |
| SelectSectorPrev | , | , (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| SelectSectorNext | D9 | D9 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MouseDeadzone | Ctrl + D5 | Ctrl + Shift + D5 (relocated) | Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69). |
| MouseResponseCurve | Ctrl + D6 | Ctrl + Shift + D6 (relocated) | Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69). |
| MouseSensitivityDown | Ctrl + D7 | Ctrl + Shift + D7 (relocated) | Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69). |
| MouseSensitivityUp | Ctrl + D8 | Ctrl + Shift + D8 (relocated) | Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69). |
| Scoreboard | B | Alt + B (relocated) | B is native TK_TargetFriendlyBase (defaultinputmap.mdl:50). |
| PilotInfo | P | Ctrl + Shift + P (relocated) | P is native TK_ToggleAutoPilot (defaultinputmap.mdl:13). |
| PilotProfileEdit | Ctrl + P | Ctrl + P (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| TeamPane | Ctrl + T | F6 | keyCommandMap line 40 (TK_ConModeTeam) |
| TeamPaneNextPilot | PageDown | PageDown (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| TeamPaneAct | Y | Y (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| TeamPaneSort | Ctrl + S | Ctrl + S (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| Help | F1 | F1 | keyCommandMap |
| OpenGraphicsSettings | Ctrl + F6 | Ctrl + F6 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| OpenKeyBindings | F6 | F6 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ScreenShot | PrintScreen | PrintScreen | keyCommandMap line 107 (TK_ScrnShot) |
| Quit | Alt + F4 | Alt + F4 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ToggleMusic | Alt + J | Ctrl + Alt + J (relocated) | Alt+J is native TK_ToggleLODSlider (defaultinputmap.mdl:22). |
| MusicVolumeUp | Ctrl + = | Ctrl + = (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MusicVolumeDown | Ctrl + - | Ctrl + - (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| StartChat | Enter | Enter | keyCommandMap line 27 (TK_StartChat) |
| ChatDelete | Backspace | Backspace (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| CycleChatTarget | Tab | Tab (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| QuickChat | R | ` | keyCommandMap line 25 (TK_QuickChatMenu) |
| CancelChat | Esc | Esc | keyCommandMap |
| PrevCommMsg | Ctrl + PageUp | Ctrl + PageUp (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_PrevCommMsg |
| NextCommMsg | Ctrl + PageDown | Ctrl + PageDown (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_NextCommMsg |
| ChatPageUp | PageUp | PageUp | keyCommandMap line 58 (TK_ChatPageUp) |
| ChatPageDown | PageDown | PageDown | keyCommandMap line 59 (TK_ChatPageDown) |
| ChatPaneFilter | Alt + C | Alt + C (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyUp | Up | Up (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyDown | Down | Down (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyJoin | Enter | Enter (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyFilter | F | F (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyRefresh | R | R (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyClearFilter | Esc | Esc (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyHeaderLeft | Left | Left (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyHeaderRight | Right | Right (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyHeaderClick | Space | Space (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyResetFilters | C | C (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyFindPlayer | P | P (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyDetails | I | I (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LobbyGameFilters | L | L (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ReadyToggle | Y | Y (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ForceReady | O | O (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| AutoAcceptToggle | U | U (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LockLobbyToggle | N | N (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| LockSidesToggle | H | H (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| CivPicker | C | C (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| TeamNameEdit | T | T (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MissionSettings | G | G (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| AcceptJoinRequest | A | A (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MenuUp | Up | Up (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MenuDown | Down | Down (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MenuLeft | Left | Left (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MenuRight | Right | Right (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MenuSelect | Space | Space (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MenuConfirm | Enter | Enter (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| MenuBack | B | B (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ShopTabSwitch | Tab | Tab (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ShopCycleHullPrev | Q | Q (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ShopCycleHullNext | E | E (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ShopLaunch | L | L (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ShopUndock | U | U (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| ToggleTeleportPane | T | F8 | keyCommandMap line 103 (TK_ConModeTeleport) |
| ToggleInvestmentPane | I | F5 | keyCommandMap line 39 (TK_ConModeInvest) |
| InvestPreviousTab | [ | [ (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestNextTab | ] | ] (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestNextPilot | Tab | Tab (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestDonate | D | D (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestDonateLarge | E | E (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestAutoDonate | A | A (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestKeepIncome | C | C (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestToggleDonate | X | X (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestPartial | R | R (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestShowAll | S | S (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| InvestHideCompleted | H | H (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| RandomizeTeams | R | R (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| FreeCamForward | W | W (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| FreeCamBack | S | S (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| FreeCamLeft | A | A (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| FreeCamRight | D | D (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| FreeCamUp | Q | Q (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| FreeCamDown | E | E (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| FreeCamSpeedUp | LeftShift | LeftShift (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| FreeCamSpeedSlow | LeftControl | LeftControl (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| SkirmishJoinAlpha | Ctrl + D1 | Ctrl + D1 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| SkirmishJoinBravo | Ctrl + D2 | Ctrl + D2 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| SkirmishSpectate | Ctrl + D0 | Ctrl + D0 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| SkirmishCamera | V | V (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| SkirmishPrevious | PageDown | PageDown (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| OrbitSubjectNext | PageUp | PageUp (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| PauseTM | Ctrl + Pause | Ctrl + Pause | keyCommandMap line 79 (TK_PauseTM) |
| FinishTM | unbound | End | keyCommandMap |
| SkipTMGoal | Decimal | Decimal (native leaves it unbound; port keeps it) | no keyCommandMap entry for TK_SkipTMGoal |
| IncreaseTMClockSpeed | Ctrl + F11 | Ctrl + F11 | keyCommandMap line 96 (TK_IncreaseTMClockSpeed) |
| DecreaseTMClockSpeed | Ctrl + F10 | Ctrl + F10 | keyCommandMap line 97 (TK_DecreaseTMClockSpeed) |
| ResetTMClockSpeed | Ctrl + F12 | Ctrl + F12 | keyCommandMap line 98 (TK_ResetTMClockSpeed) |
| GameOverBack | Backspace | Backspace (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| Credits | F12 | F12 (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| CreditsScrollUp | PageUp | PageUp (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |
| CreditsScrollDown | PageDown | PageDown (native leaves it unbound; port keeps it) | no keyCommandMap entry for this command |

## 2. Prompt/key audit

44 training prompts name an input. The audit walks the port's mission scripts (the goal graph each
`Mission2/3/4/5/6/8/10` builds, including the joystick branch of 2/3), resolves the action each prompt asks for,
and checks the key against the native-default profile: `TrainingPromptAudit` (table, in
`src/Allegiance.ClientCore/Training/TrainingPromptAudit.cs`) and the tests
`P941PromptAuditTests` (graph half) + `P941InputLayoutTests.Audited_training_prompt_keys_are_bound_...`
(binding half). Voice contents are Claude's Whisper transcriptions of `tm_*.ogg`
(a local Whisper transcription of the shipped `tm_*.ogg` files, not committed), spot-checked against the native
comment above each `PlaySoundAction`.

| Mission | Goal | Native line | On-screen text | Action | Key named | Voice line (Whisper transcript) | Too-long reminder |
|---|---|---|---|---|---|---|---|
| 2 | 2.3 | `mission2.cpp:352-354` | Press the SPACEBAR when you are ready to proceed. | FireBooster (Keyboard, Space) | Space | `tm_2_11r`: When you are comfortable with the operation of your craft, press the spacebar to proceed. | - |
| 2 | 2.4 | `mission2.cpp:438-440` | Press BACKSLASH (\) or set your joystick throttle to maximum for full thrust. | ThrottleFull (Keyboard, \) | \ | `tm_2_16r`: Adjust throttle control full forward on your joystick or press the back flash key. | - |
| 2 | 2.5 | `mission2.cpp:478-480` | Use your scroll wheel or joystick throttle to adjust thrust. | ThrottleUp (Wheel, Wheel Up) | Wheel Up | `tm_2_18r`: Try adjusting the throttle to see how this gauge changes. | - |
| 2 | 2.6 | `mission2.cpp:631-633` | Press the SPACEBAR when you are ready to proceed. | FireBooster (Keyboard, Space) | Space | `tm_2_24r`: When you are comfortable with the operation of your ship, press the spacebar to proceed. | - |
| 2 | 2.7 | `mission2.cpp:737-739` | Press and hold button #3 on your joystick to fire the booster. |  (Joystick, ) |  | `tm_2_32`: Activate the booster with your joystick by pressing and holding button 3. | - |
| 2 | 2.7 | `mission2.cpp:746-748` | Press and hold the SPACE key to fire the booster. | FireBooster (Keyboard, Space) | Space | **MUTED by native** - On the keyboard, press and hold the tab key to fire your boosters. | native muted tm_2_33 (:746) but reminded with it (:747) - P941 repeats the text |
| 3 | 3.3 | `mission3.cpp:450-453` | Press the right mouse button to target the objects in the center of your display. | TargetCenter (Mouse button, Mouse Right) | Mouse Right | **MUTED by native** - Press the C key to target objects nearest to the center of your display. | native removed tm_3_11r (:451) but reminded with tm_3_11 (:453) - P941 repeats the text |
| 3 | 3.3 | `mission3.cpp:481-484` | Press the SPACEBAR when you are ready to proceed. | FireBooster (Keyboard, Space) | Space | `tm_3_12r`: When you're comfortable with targeting, press the spacebar to proceed. | - |
| 3 | 3.4 | `mission3.cpp:534-536` | Press the SPACEBAR when you are ready to proceed. | FireBooster (Keyboard, Space) | Space | `tm_3_15`: Press the spacebar when you are ready to proceed. | - |
| 3 | 3.5 | `mission3.cpp:634-637` | Fire the main weapon by pressing the trigger button on your joystick, or left click if using a mouse | FireWeapon (Mouse button, Mouse Left) | Mouse Left | `tm_3_18`: Press the trigger button on your joystick to fire your main weapon. | - |
| 3 | 3.5 | `mission3.cpp:643-646` | Left click to fire your main weapon. | FireWeapon (Mouse button, Mouse Left) | Mouse Left | **MUTED by native** - Press the spacebar on the keyboard to fire your main weapon. | native muted tm_3_19 (:643) but reminded with it (:645) - P941 repeats the text |
| 3 | 3.7 | `mission3.cpp:773-776` | Target an enemy using the F key. Target a friendly using the T key. | TargetEnemy (Keyboard, F) | F | **MUTED by native** - Select a new target now using the E key. | native muted tm_3_27 and its reminder (:775-776) - no reminder at all |
| 3 | 3.7 | `mission3.cpp:773-776` | Target an enemy using the F key. Target a friendly using the T key. | TargetFriendly (Keyboard, T) | T | **MUTED by native** - Select a new target now using the E key. | native muted tm_3_27 and its reminder (:775-776) - no reminder at all |
| 3 | 3.8 | `mission3.cpp:843-845` | Fire the main weapon by pressing the trigger button on your joystick. |  (Joystick, ) |  | `tm_3_31r`: Go ahead and fire. | - |
| 3 | 3.8 | `mission3.cpp:843-845` | Left click to fire your main weapon. | FireWeapon (Mouse button, Mouse Left) | Mouse Left | `tm_3_31r`: Go ahead and fire. | - |
| 3 | 3.9 | `mission3.cpp:932-935` | Fire missiles by pressing the #2 button on your joystick. |  (Joystick, ) |  | `tm_3_37`: Fire your missiles by pressing button 2 on your joystick. | - |
| 3 | 3.9 | `mission3.cpp:941-945` | Press the middle mouse button to fire your secondary weapon. | FireMissile (Mouse button, Mouse Middle) | Mouse Middle | **MUTED by native** - Fire your missiles by pressing control plus space bar on the keyboard. | native muted tm_3_38 (:942) but reminded with it (:945) - P941 repeats the text |
| 3 | 3.10 | `mission3.cpp:1026-1030` | Press the SPACE key when you are comfortable with the Inventory Display. | FireBooster (Keyboard, Space) | Space | `tm_3_15`: Press the spacebar when you are ready to proceed. | - |
| 3 | 3.11 | `mission3.cpp:1090-1094` | Press the SPACE key when you are ready to go home. | FireBooster (Keyboard, Space) | Space | `tm_3_46`: When you are ready to go home, press the spacebar to proceed. | - |
| 3 | 3.12 | `mission3.cpp:1120-1124` | Press the B key to target the outpost. | TargetFriendlyBase (Keyboard, B) | B | `tm_3_48`: Press the B key to target your nearest base. | - |
| 4 | 4.2 | `mission4.cpp:240-244` | Press the ENTER key to begin a chat. | StartChat (Keyboard, Enter) | Enter | `tm_4_03r`: Press the enter key to begin. Use the tab key to select the chat target, in this case all. Then type your message and press enter again to send it. | - |
| 4 | 4.2 | `mission4.cpp:241` | Press the TAB key to select 'All'. | CycleChatTarget (Keyboard, Tab) | Tab | `tm_4_03r`: Press the enter key to begin. Use the tab key to select the chat target, in this case all. Then type your message and press enter again to send it. | - |
| 4 | 4.2 | `mission4.cpp:242` | Press the ENTER key again to send the chat. | StartChat (Keyboard, Enter) | Enter | `tm_4_03r`: Press the enter key to begin. Use the tab key to select the chat target, in this case all. Then type your message and press enter again to send it. | - |
| 4 | 4.2 | `mission4.cpp:265-269` | Press the SPACEBAR when you are ready to proceed. | FireBooster (Keyboard, Space) | Space | `tm_4_04r`: Press the space bar when you are ready to proceed. | - |
| 4 | 4.3 | `mission4.cpp:290-302` | Press the TILDE (~) key to activate the quickcomm menu. | QuickChat (Keyboard, `) | ` | `tm_4_05`: Good. The second method of communication is to use a quick calm message. You can send a quick calm by pressing the tilde key and then choosing the calm you want to send from the list. This will play a sound and send a chat message to your last chat recipient. | - |
| 4 | 4.3 | `mission4.cpp:301-302` | Press the SPACEBAR when you are ready to proceed. | FireBooster (Keyboard, Space) | Space | `tm_4_06r`: Press the spacebar when you're comfortable with quick comms. | - |
| 4 | 4.4 | `mission4.cpp:347-351` | Press the INSERT or '[' key to accept the command. | AcceptCommand (Keyboard, [) | [ | `tm_4_08r`: Press the insert key to accept this objective. | - |
| 4 | 4.5 | `mission4.cpp:448-452` | Press the INSERT or '[' key to accept the command. | AcceptCommand (Keyboard, [) | [ | `tm_4_15`: Let's get going. Press the insert key to accept your orders. | - |
| 4 | 4.6 | `mission4.cpp:720-725` | Press the G key to activate your cloak. | ToggleCloak (Keyboard, G) | G | `tm_4_26r`: Activate your cloak. | - |
| 4 | 4.7 | `mission4.cpp:796-801` | Target an enemy miner with the F key, and destroy it if you can. | TargetEnemy (Keyboard, F) | F | `tm_4_29`: Target one of the enemy miners and try to sneak up on it. Remember not to fire or use your boosters until you are within range. Destroy the enemy craft if you can. | - |
| 4 | 4.8 | `mission4.cpp:900-906` | Press the INSERT key to accept the command. | AcceptCommand (Keyboard, [) | [ | `tm_4_32`: I'm moving your ship around to protect the constructor. Stay near it in case an enemy fighter comes in. Constructors are very expensive and we don't want to lose it. | - |
| 4 | 4.9 | `mission4.cpp:930-934` | Press the INSERT key to accept the command. | AcceptCommand (Keyboard, [) | [ | `tm_4_33`: Enemy scout detected. Intercepted. | - |
| 4 | 4.10 | `mission4.cpp:989-991` | Press the INSERT key to accept the command. | AcceptCommand (Keyboard, [) | [ | - | - |
| 5 | 5.1 | `mission5.cpp:273-277` | Press the SPACEBAR when you are ready to proceed. | FireBooster (Keyboard, Space) | Space | `tm_5_02`: Practice manipulating your view and then press the spacebar when you're ready to proceed. | - |
| 5 | 5.2 | `mission5.cpp:295-299` | Left click on a Tower Layer to select it. |  (Pointer click, ) |  | `tm_5_03r`: First, left-click a tower layer. | - |
| 5 | 5.2 | `mission5.cpp:310-313` | Left click on 'Build' command. |  (Pointer click, ) |  | - | - |
| 5 | 5.2 | `mission5.cpp:324-328` | Right click in space to deploy tower. |  (Pointer click, ) |  | `tm_5_04r`: Right click in space and drag to indicate where you want the tower to go. | - |
| 5 | 5.3 | `mission5.cpp:372-377` | Left click on 'Mars' in the Sector Map to change view. |  (Pointer click, ) |  | `tm_5_06r`: When you are ready, left click sector Mars in the map display to change the sector you are viewing. | - |
| 5 | 5.4 | `mission5.cpp:391-394` | Left click on a Miner to select it. |  (Pointer click, ) |  | `tm_5_07r`: Left click on a miner. | - |
| 5 | 5.4 | `mission5.cpp:414-419` | Left click on 'Mine' command, and right click on the He3 asteroid to begin mining. |  (Pointer click, ) |  | `tm_5_08r`: Right click on the Helium-3 asteroid you want to mine. | - |
| 6 | 6.1 | `mission6.cpp:407` | Hint: Press F1 to see an overview of your ship's controls. | Help (Keyboard, F1) | F1 | - | - |
| 10 | 10.1 | `mission10.cpp:401` | Hint: Press F1 to see an overview of your ship's controls. | Help (Keyboard, F1) | F1 | - | - |
| 8 | 8.2 | `mission8.cpp:182` | F4 toggles the inventory. You will want it to be open. | ConModeInventory (Keyboard, F4) | F4 | - | - |
| 8 | 8.3 | `mission8.cpp:194-195` | Swap the Nanite Repair System into slot 1 by pressing Ctrl+1. | SwapWeapon1 (Keyboard, Ctrl + 1) | Ctrl + 1 | `tm_8_03`: You need to mount the Nanite Repair System in slot 1, which currently contains a Gatling Gun. Remember pressing the control key along with the slot number will swap the item currently in that slot with the next weapon at the same time. | - |

Notes:

- `Pointer click` rows are the command-view/sector-map picks: no action-map chord is involved (native
  `console.cpp:1912-1990`); they work because the command camera always picks with the pointer.
- `Joystick` rows are native's joystick branch (`GetJoystickCount() > 0`, `mission2.cpp:403`, `mission3.cpp:459`);
  the port ships the keyboard branch, so there is no binding to check.
- The audit's coverage test fails if a mission gains a new prompt that names an input without being added to the
  table, so this table cannot silently drift.

## 3. The too-long reminder bug (deliberate, documented native-bug fix)

Native muted four voice-overs because they name the old keymap but kept passing the same sound id to
`CreateTooLongCondition` (`trainingmission.cpp:927-932`), so a hesitating player heard the wrong key 30 s later:

| Mission | Native line | Muted voice line | Native reminder | P941 behaviour |
|---|---|---|---|---|
| 2.7 booster | `mission2.cpp:746-748` | `tm_2_33` "press and hold the tab key to fire your boosters" (muted at :746) | `CreateTooLongCondition(30.0f, tm_2_33Sound)` (:747) | repeats "Press and hold the SPACE key to fire the booster." |
| 3.3 target centre | `mission3.cpp:450-453` | `tm_3_11r` "Press the C key to target objects nearest to the center…" (removed at :451) | `CreateTooLongCondition(30.0f, tm_3_11Sound)` (:453, the goal's narration) | repeats "Press the right mouse button to target the objects in the center of your display." |
| 3.5 fire weapon | `mission3.cpp:643-646` | `tm_3_19` "Press the spacebar on the keyboard to fire your main weapon" (muted at :643) | `CreateTooLongCondition(30.0f, tm_3_19Sound)` (:645) | repeats "Left click to fire your main weapon." |
| 3.9 missiles | `mission3.cpp:941-945` | `tm_3_38` "Fire your missiles by pressing control plus space bar…" (muted at :942) | `CreateTooLongCondition(30.0f, tm_3_38Sound)` (:945) | repeats the corrected missile prompt (section 4) |

Implementation: `TrainingMission.CreateTooLongMessageCondition(delay, MessageAction)` — the same native periodic
trigger (`PeriodicCondition(NTimesCondition(True, 2, sticky), delay)`) with the prompt's own `MessageAction`
instead of a `PlaySoundAction`. Native offers no matching recording for these four prompts, so no voice line is
played; the on-screen text the player is already reading repeats.

Tests: `P941PromptAuditTests.No_too_long_reminder_replays_a_muted_voice_line` fails without the change (it walks
every `ConditionalAction`'s inner `PlaySoundAction` and rejects the muted stems `tm_2_33`, `tm_3_11`,
`tm_3_19`, `tm_3_38`), and `Fixed_prompts_repeat_their_on_screen_text_when_the_player_dawdles` fails without it
too (it requires a text-repeating reminder for each of the four).

## 4. On-screen text corrections

Only one on-screen prompt disagreed with native's own keymap:

| Mission/goal | Native line | Native text | Native keymap | Corrected port text |
|---|---|---|---|---|
| 3.9 missiles | `training/mission3.cpp:943` | "Press 5 or Middle click to fire your secondary weapon." | `keyCommandMap` binds no key to `5` for missiles; TK_FireMissile is the MIDDLE mouse button (`mouseButtonCommandMap` (2,56), `defaultinputmap.mdl:112`) | "Press the middle mouse button to fire your secondary weapon." |

No audio file was edited or re-recorded. The existing mission-3 test (`TR3TrainingMissions34Tests`) matched the
old text to drive the mission; its matcher was updated to the corrected sentence (it is a prompt-text matcher,
not a layout pin).

## 5. F1 overview in both HUD styles

- Native's hint is "Hint: Press F1 to see an overview of your ship's controls." (`mission6.cpp:407`,
  `mission10.cpp:401`).
- The F1 overlay is the live `ActionMap` table (`KeyBindingTable`), so it now lists the native layout: `Q`/`E`
  roll, `A`/`D` lateral thrust, `Space` booster, `Mouse Left` / `Mouse Middle` / `Mouse Right` for fire/missile/
  target centre, `V` mouse flight, `[`/`]` orders, etc. Mouse-only commands are included with their button label.
- The manual's FLIGHT / COMBAT / COMMAND pages are generated from the live bindings (`HelpManualModel.BuildTopics`),
  which removes the stale port prose ("W/S throttle up/down; Q/E roll; A/D lateral thrust", "Space fires the
  selected weapon") and names the real keys for whichever preset is selected.
- The help pane and the Controls popup are shared model/renderer code (the Modern HUD re-composes gauges and
  reticles, not the help pane); the pair was captured in both styles (section 7) and pinned by
  `P941InputLayoutTests.F1_overview_is_shared_by_both_hud_styles`.

## 6. Tests

New (all three times alone, section 8):

- `tests/Allegiance.ClientCore.MonoGame.Tests/P941InputLayoutTests.cs` — native keyboard defaults, the mouse/wheel
  map against `DefaultInputMap.ParseMouseMap`, no-loss-of-binding, no duplicate chords inside the flight groups,
  the port preset, preset switching, JSON round-trip, legacy-file migration, `KeyBindingStore` layout selection,
  the F6 preset switch and the F1 overview for both styles.
- `tests/Allegiance.ClientCore.Tests/P941PromptAuditTests.cs` — the graph half of the prompt audit (44 prompts
  present in the built mission graphs, coverage guard, reminder fix, muted-recording ban).

Existing tests that pinned the port layout were switched to select the **Port layout** preset explicitly
(`new ActionMap(InputActionCatalog.Definitions, InputLayout.Port)`), not weakened:

- `HelpOverlayTests` — reads a Port-layout map's table (its group/duplicate/cell invariants pin the old key set).
- `P625KeyMapTests`, `P625bActionMapBehaviorTests` — the P625 rebind mechanics were written against the port keys.
- `P624FlightKeysPackTests`, `P626InFlightUtilityPackTests` — pin the P624/P626 port chords.
- `P720EquipmentSwapTests` — pins the swap commands' intentional "unbound in F6" port state.
- `P675CommandActionTests`, `P640CommandCameraTests` — pin the port order/camera chords.
- `P757KeyBindOkCancelTests` — its store tests keep the port keys; its no-file reload assertion now pins the
  documented native fallback (FireWeapon = mouse left) instead of the old Space.
- `P881aTrainingMissionsTests`, `P912TrainingUniverseTests` — pin that training accepted the native keys *on top
  of* the port map.
- `P523SelfTargetAndBracketJitterTests` — the table/Y regression reads the Port map; the shared pre-flight edge
  primes now check both presets (`ComputeSharedEdgePrimes(ks, map)` overload).
- `TR3TrainingMissions34Tests` — text matcher for the corrected missile prompt (section 4).
- The scripted training-proof pilot (`Game1.TrainingProof*`, launched by any run with `ALLEGIANCE_TRAIN_PROOF=1`,
  including `tools/p912-training-proof`) presses the pre-P941 raw keys (`SPACE` to fire, `W`/`Backslash`/
  `LeftShift` to fly), so `InitKeyBindingUi` pins the **Port layout** for those runs (persisted, so the Controls
  popup's Cancel does not flip it) and `TrainingProofPilot.CreateInputMap` starts from the explicit Port preset.

No behaviour was weakened: every change picks the preset the test was written for, or pins the newly documented
rule (native fallback on a missing keymap).

## 7. Screenshots (real presented window)

Sequence driven by `ALLEGIANCE_P941_PROOF=1` inside the real client (`scripts/live-proof.sh`, local server,
windowed 1600x900 client, `--hud-settings` per style): F6 on the fresh native layout → `P` to the Port layout →
`P` back and OK → F1 overview CONTROLS page → F1 overview FLIGHT page.

All eight frames are real presented-window backbuffer captures (`1600x900`, then losslessly downscaled to
`1280x720` 192-colour PNGs to stay inside the evidence budget) under `docs/reports/P941_assets/`. They were
opened and read back; what is visible is described in each row.

| Style | Frame | File | What is visible |
|---|---|---|---|
| Classic | F6 Controls, fresh native layout | `P941_assets/p941_controls_native_classic.png` | panel title `CONTROLS - pick a command, press Enter to rebind (Esc closes) [NATIVE LAYOUT]`, footer `P = layout preset`; MOVEMENT rows `RollLeft Q`, `ThrustLeft A`, `ThrustRight D`, `FireBooster Space`; WEAPONS rows `FireWeapon Mouse Left`, `FireMissile Mouse Middle`; behind it the Classic cockpit HUD (Omega, roll gauge, star map, `YOU: Omega`) |
| Classic | F6 Controls, `P` -> Port layout | `P941_assets/p941_controls_port_classic.png` | header `[PORT LAYOUT]`; `RollLeft A`, `ThrustLeft Q`, `FireBooster Shift`, `FireWeapon Space` (the pre-P941 keys, unchanged) |
| Classic | F1 overview, CONTROLS page | `P941_assets/p941_help_controls_classic.png` | live key table over the cockpit HUD; WEAPONS `Mouse Left - Fire weapons (hold)`, `Mouse Middle - Fire missile (hold)`; TARGETING `Mouse Right - Target center`; CAMERA/VIEW rows use the relocated chords (`Shift + F4/F5/F7`) |
| Classic | F1 overview, FLIGHT page | `P941_assets/p941_help_flight_classic.png` | generated prose over the cockpit HUD: `Tab/S throttle up/down; A/D lateral thrust.`, `W/X vertical thrust; Q/E roll the craft.`, `Space fires the booster`, `V toggles mouse flight (virtual joystick) ...`, `T targets a friendly; F an enemy; P engages the autopilot.` |
| Modern | F6 Controls, fresh native layout | `P941_assets/p941_controls_native_modern.png` | same `[NATIVE LAYOUT]` panel and rows (`RollLeft Q`, `ThrustLeft A`, `FireBooster Space`, `FireWeapon Mouse Left`) over the Modern HUD (the `HUD Tactical` level marker identifies the style) |
| Modern | F6 Controls, `P` -> Port layout | `P941_assets/p941_controls_port_modern.png` | `[PORT LAYOUT]` with the old keys |
| Modern | F1 overview, CONTROLS page | `P941_assets/p941_help_controls_modern.png` | the same live table (shared renderer) over the Modern HUD |
| Modern | F1 overview, FLIGHT page | `P941_assets/p941_help_flight_modern.png` | the same generated FLIGHT prose over the Modern HUD |

Reproduction: `ALLEGIANCE_P941_PROOF=1 scripts/live-proof.sh --hud-settings <classic|modern>.json --client-extra '--auto-launch 285'`
(local loopback server, `--autoshot-dir`). The frames are new media and still need Domingo's review before the push.

## 8. Gate

Run by Claude after Hermes's session ran out of context (the report and code were complete; this section was not):

- `dotnet build AllegianceCore.slnx` (Debug): **0 warnings, 0 errors**.
- New tests (`P941InputLayoutTests`, `P941PromptAuditTests`), alone: **22/22 × 3**, 0 skipped.
- Tests switched to the Port preset (HelpOverlay, P523, P624, P625/P625b, P626, P640, P675, P720, P757, P881a,
  P912TrainingUniverse, TR2/TR3, TrainingMission2, P880a, P885): **426/427** in one pass. The one failure,
  `P881aTrainingMissionsTests.Mission4_completes_with_real_transit_guns_constructor_and_landing`, was task-caused:
  its `Trainee` harness map still used the new native default, so the scripted pilot's SPACE fired the booster and
  the scout at goal 8.7 was never shot (master passes the same test). Fixed by pinning the harness to the Port
  preset; passes.
- Claude's review change: a pre-P941 keymap file now migrates to the native layout with the player's own rebinds
  on top (section 1); `P625KeyMapTests.Overrides_IgnoreUnknownActionsAndBadCodes` updated for that, 58/58 × 3 with
  the P941 tests.
- ONE `tools/run-tests.sh --tier fast`: 13,241 tests, **FAIL** with 4 failures — the P625 expectation above (fixed
  since) and three Core tests P941 does not touch: `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`
  (passes 3/3 alone), `NpcWingmanAttackLiveTests` and `NpcWingmanDefendLiveTests` (already ledgered). Ledgered in
  `docs/TEST_FLAKES.md`.
- `tools/run-training-tests.sh --repeat 1`: **not run on the workstation** (Codex's P940 training gate was using
  it); it runs on the mini runner after the merge.

## Native-intent footer

- **Design (matches native):** the default keyboard table and the mouse/wheel map are native's own
  `defaultinputmap.mdl` (`keyCommandMap:3-107`, `mouseButtonCommandMap:109-116`), decoded by the existing
  `DefaultInputMap`; the native layout reproduces it action by action. The F6 preset switch mirrors native's
  `LoadMap` rebuild (`trekinput.cpp:484-598`) and its OK/Cancel persistence (`:2436-2458`, `:2376-2384`).
- **Era constraint (deliberate port policy):** the port has many commands native does not bind at all (the
  pane/menu/shop/free-cam verbs and the unbound TK tail). They keep their port chords, so the native layout is
  native's map *plus* the port's reachability rule; 28 port-only chords that native's map claims were moved to
  free chords in the same family. The port also keeps its pointer mouse as the **Port layout** preset and the
  F6 popup on `F6` (where native shows the team pane), because F6 is the port's only rebinding surface.
- **Bug (native, deliberately fixed):** four muted wrong-key voice-overs were still used as the 30-second
  too-long reminders (`mission2.cpp:747`, `mission3.cpp:453`, `:645`, `:944`). P941 repeats the on-screen prompt
  instead and never plays those recordings; the fix is documented above, with tests that fail without it.
- **Text correction:** `mission3.cpp:943`'s "Press 5" names a key native's keymap does not bind; corrected to the
  middle mouse button the map actually has.
- **Modern approach:** the data-driven preset model (one catalogue, two layouts, persisted marker, live help)
  is better than native's single hard-coded map: it lets a new player start on native's controls while an
  existing player keeps the port's, with no per-key migration code. The reminder fix is a plain bug fix that a
  modern implementation would not have (a reminder carries the prompt object, not a sound id).
