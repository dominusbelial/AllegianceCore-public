# P624 — Parity wave 1: flight keys pack (match speed, vector lock, vertical strafe, look-around cameras, radar mode cycle)

Date: 2026-10-13 · Branch: `master` (not pushed) · Commits: <initial commit>, amended with suite numbers

## Scope

Closed the P617 items 4–7 and 20 that veterans press and "nothing happens":

- `TK_MatchSpeed` (heading+speed match)
- `TK_LockVector` (heading+speed lock)
- `TK_ThrustUp` / `TK_ThrustDown` (vertical strafe; the port had Q/E lateral only)
- rear/side look cameras (`TK_ViewRear`, `TK_ViewFrontLeft/Right`, `TK_ViewLeft/Right`, `TK_ViewRearLeft/Right`)
- `TK_CycleRadar` (radar mode cycle)

Plus the two carried-over leftovers:
- P625b: `Game1.FlightInput.cs` `ThrottleFullDown` still OR'd a hard-coded `XnaKeys.OemPipe` with the `ThrottleFull` action — folded into the action map as a **native alternate** binding.
- P623 review: the help-overlay header `"KEYBINDINGS (H TOGGLE - F6 CONTROLS / REBIND)"` was hard-coded and went stale after Help moved to F1 — now generated from the **live** `Help` + `OpenKeyBindings` bindings.

---

## 1. Native semantics (file:line) and what each port does

### Match speed — `TK_MatchSpeed`
- Native: read as a **held** key (`IsTrekKeyDown(TK_MatchSpeed)`, `WinTrek.cpp:10881`); while held it overwrites the persistent throttle with `GetThrottle(currentTarget)` (`WinTrek.cpp:10966-10974`). `GetThrottle` (`WinTrek.cpp:122-134`) maps the target's velocity length to the cruise throttle `[-1,1]`: `speed ≥ myMaxSpeed ? 1.0 : 2·speed/myMaxSpeed − 1`.
- Port: `FlightInput.MatchSpeedThrottle` (pure, testable) implements `GetThrottle`. `Game1.TryMatchSpeedThrottle` resolves the current `_autoTarget`'s live velocity (a station/asteroid/warp is stationary → throttle −1, i.e. stop) and the own hull's `MaxSpeed` (`OwnShipSimulator.MaxSpeed`, new). While the `MatchSpeed` key is held the persistent `_throttle` is forced to that value each frame, sent verbatim as `ControlData.Throttle`, so the cruise branch (ShipKinematics) and the server agree.
- **Interaction:** match speed drives the *cruise* branch (no movement button held). Holding W/S (movement buttons) overrides it, exactly native's manual-vs-coast precedence. `_throttle` is the P445 persistent axis, so releasing match-speed leaves the matched speed set (native's `trekThrottle` semantics).

### Vector lock — `TK_LockVector`
- Native: toggles the `coastButtonIGC` state bit on press (XOR), only when not parented, with the vector-lock sound (`WinTrek.cpp:9458-9464`). The coast bit makes the sim **kill lateral velocity / hold heading+speed** (the coast branch).
- Port: `LockVector` action, bound to **L** (native's default, `defaultinputmap.mdl` line 39). Edge-triggered **latched toggle** (like cloak): `_coastToggled` folds into the frame's `stateM` as `ShipControlState.CoastButton` (8) in `FlightInput.Translate`. The sim's coast branch (`ShipKinematics.cs:167-171`) already implements the native coast semantics. Because the bit crosses on the wire `stateM`, own-ship prediction and the server replay agree.
- **Release:** toggled off by pressing L again, by dock/respawn (per-launch reset), or by holding any movement button (native manual-overrides-coast).

### Vertical strafe — `TK_ThrustUp` / `TK_ThrustDown`
- Native: `ThrustUp=W`, `ThrustDown=X`, `ThrustLeft=A`, `ThrustRight=D`, `ThrustForward=Tab`, `ThrustBackward=S` (`defaultinputmap.mdl` lines 30–33); all feed the movement-button model (side/vertical/fore-aft thrust).
- Port: the sim **already** implements vertical strafe — `ShipKinematics.cs:157-162` sums Left/Right/Up/Down/Forward/Backward into the manual engine vector, the exact lateral model Q/E uses. P624 just wires `ThrustUp`/`ThrustDown` into `FlightInputState.StrafeUp/StrafeDown` → `ShipControlState.UpButton`/`DownButton` (256/512). Keys: **Insert** (up) and **X** (down, native's own X after LeaderCommand relocated). Movement-scheme divergence from native is classified deliberate (below).
- **Thrust model:** identical to lateral — the same `MoveButtonsMask` manual branch; no new sim behaviour.

### Look-around cameras — `TK_ViewRear`, `TK_ViewFrontLeft/Right`, `TK_ViewLeft/Right`, `TK_ViewRearLeft/Right`
- Native: `SetHeadOrientation(absYaw)` rotates the camera's look direction about the ship's up axis, only in `cmCockpit`/`cmExternalChase` (`WinTrek.cpp:9733-9791`); the look yaw combines as `headOrientation * shipOrientation` (`WinTrek.cpp:6982`). The keys are **HELD** — with no look key pressed the head snaps back to forward (`SetHeadOrientation(js.hat)` hat-centred = 0, `WinTrek.cpp:8445-8462`); `SetViewToCombatMode` also resets to 0 (`WinTrek.cpp:9365-9367`).
- Port: new pure `Rendering/HeadLook.cs` holds the native angles (positive = look right, clockwise from above) and the yaw rotation (`CreateRotationY(−yaw) * shipRotation` — sign verified so `π/2` looks +X). Applied only in Cockpit and Chase via an optional `headYawRadians` on `CockpitCamera.Matrices` / `ChaseCamera.Matrices` (P612b camera-relative rule preserved: views stay `CreateLookAt(Zero,…)`, draws subtract the eye). `Game1._headLookYaw` is set by whichever look key is **held** and reset to 0 when none is held or the camera mode changes (native's reset-on-view-change).
- Keys: the number-row **D1–D8 compass ring** (native leaves these unbound in `defaultinputmap.mdl` — keyboard look reached them via the joystick-hat patch — so the port claims the free digit row; see report §3). Note P624b's camera-relative rule: no new world-draw; the head yaw only rotates the view matrix.

### Radar mode cycle — `TK_CycleRadar`
- Native: cycles `m_radarCockpit = (m_radarCockpit+1) % (c_rlMax+1)` over All→Default→Target→Minimal and posts the mode string (`WinTrek.cpp:9955-9975`; enum `radarimage.h:42-52`, strings `radarimage.cpp:2-5`). The LOD switch (`radarimage.cpp:700-980`) controls which contacts get icons/labels/stats.
- Port: new pure `Rendering/RadarLod.cs` (`RadarMode.All/Default/Target/Minimal`, `Next`, `Label`) and a per-contact filter applied in `DrawRadar` (`Game1.Rendering.cs`): All/Default show everything; Target/Minimal hide friendly ships (`ShowShip(isEnemy…)`) and keep only the selected target among stations/warps (`ShowTargetable`). `CycleRadar` is bound to **`.`** (native's default, `defaultinputmap.mdl` line 111) and edge-cycles `_radarMode`.

---

## 2. Key bindings — native wins over port-only bindings (moved)

Native default keys (`defaultinputmap.mdl`) honored; the port-only bindings that occupied them were relocated:

| Action | New key | Native default | Notes |
|---|---|---|---|
| **ThrustUp** | `Insert` | `W` | W kept as port's deliberate thrust-forward (see §4) |
| **ThrustDown** | `X` | `X` | native wins; LeaderCommand → Ctrl+X |
| **LockVector** | `L` | `L` | native wins; Ripcord → Ctrl+R (its native default) |
| **MatchSpeed** | `/` | *(unbound in mdl)* | free key |
| **CycleRadar** | `.` | `.` | native wins; SelectSectorNext → D9 |
| **ViewFrontLeft…ViewRearRight** | `D1–D8` ring | *(unbound)* | compass ring |
| **Ripcord** *(moved)* | `Ctrl+R` | `Ctrl+R` | restored to native |
| **LeaderCommand** *(moved)* | `Ctrl+X` | — | freed X for ThrustDown |
| **PromoteTurret1-4** *(moved)* | `Ctrl+1..4` | `Ctrl+1..4` | restored to native (`defaultinputmap.mdl` lines 73–76) |
| **IssueBuild** *(moved)* | `Alt+I` | — | freed Insert for ThrustUp |
| **MouseDeadzone/Curve/Sens** *(moved)* | `Ctrl+5..8` | — | freed the digit row |

Every new command is registered in `InputActionCatalog.cs` (the source `KeyBindingTable`/help reads), appears in the help overlay (`ToHelpGroups`), and its description carries the native TK name from `trekinput.cpp:1459`'s command-name table.

---

## 3. "Native behaviour classified"

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Movement-key layout (W=fwd, S=back, A/D=roll, Q/E=lateral) | `defaultinputmap.mdl` 30–33 vs port | **era/design divergence** | Kept the documented deliberate port scheme (P625); vertical strafe gets Insert/X instead of native W/X; documented |
| Look cameras held, reset to forward | WinTrek.cpp:8445-8462 | design | Held keys; head snaps back on release / mode change |
| Radar LOD icon/label tiers | radarimage.cpp:700-980 | era complexity | Condensed to a per-contact visibility filter (All/Default/Target/Minimal) |
| `GetThrottle` match-speed law | WinTrek.cpp:122-134 | design | Ported verbatim |
| Coast/vector-lock state bit | WinTrek.cpp:9458-9464 | design | Latched toggle → CoastButton (8) on the wire |
| Look key defaults absent from mdl | radarimage / trekinput | era (joystick hat) | Compass ring on D1–D8, rebindable via F6 |

## 4. Modern alternative considered

- **Action map with alternates** (`InputBindings.cs`): native's single VK often maps to two MonoGame `Keys` (hardware-layout scancodes, e.g. the backslash). Instead of a hard-coded raw `OemPipe` OR, `InputActionDef.Alternate` + `ActionMap.IsDown` let an action own multiple chords data-driven — the modern "input-action + bindings" pattern (Unity/Unreal). Chosen; the old raw-key allow-list entry was removed.
- **Radar modes**: native uses an intricate LOD label/stats tree from the 2000s. A modern radar would use LOD tiers tied to range/significance. Chosen: a clean per-contact filter keeping native's 4 modes' *meaning*, sized for today (no label-tier complexity the port's bracket radar doesn't use).
- **Look-around**: implemented as a head-yaw on the existing camera-relative view (no new rig), matching native's intent without disturbing P612's camera system.

---

## 5. Verification

### Unit tests (failing-first)
New `tests/…/P624FlightKeysPackTests.cs` (all pure): vertical strafe Up/Down → `UpButton`/`DownButton` bits; vector lock latched → `CoastButton` bit; `MatchSpeedThrottle` law (stationary→−1, half→0, ≥max→1, monotone); `HeadLook` directions (Right→+X, Rear→+Z, Left→−X, FrontLeft diagonal, forward at 0); `AngleForAction` maps all 7 commands; `RadarLod.Next` order + `ShowShip`/`ShowTargetable`; `HelpHeader.Line` from live bindings; `ThrottleFull` resolves both OemBackslash and OemPipe via the action-map alternate; catalogue registers all 12 new commands with native TK names; native-wins relocations pinned; new commands appear in the help overlay.

Existing P625 conflict-controller and help-overlay tests that asserted the old catalogue were updated to the intended new one (X now = ThrustDown; IssueBuild on Alt+I). The **P625b guard test is green** — the OemPipe raw-key allow-list entry was removed (folded into the map).

Full MonoGame assembly: **1922 passed, 0 failed**.

### Build
Whole-solution build **0 warnings, 0 errors**.

### Live check — no SHIP_RESET storms from the new commands
Ran the real client against a **local** server (never 221) in my dev worktree, with `ALLEGIANCE_FLIGHTKEYS_PROOF=1` driving vector lock + match speed + vertical strafe through the real send path, plus a controlled **baseline** run (same build, no flight keys):

| Run | duration | server `[ctrlak]` rejected | mean-reject-dist | client `[sim] SHIP_RESET` | rate |
|---|---|---|---|---|---|
| baseline (no flight keys) | 55 s | 46 | 31–77 m | 47 | ~0.85/s |
| flight-keys proof (L+strafe+match engaged) | 75 s | 59 | 38–42 m | 60 | ~0.80/s |

The own-ship prediction shows a **pre-existing** ~40 m disagreement in this auto-fly proof scenario (present from the first frame, before any P624 command, and identical on the no-flight-keys baseline). **The flight-keys engagement adds zero rejections** (rate statistically identical, ~0.8/s both). This pre-existing auto-fly artifact is unrelated to P624 (it predates the new commands and reproduces without them); it warrants a separate own-ship-prediction follow-up (P508/P502-class), not a flight-keys regression.

### Real-window frames (real backbuffer, 2560×1440, `scripts/live-proof.sh`)
`docs/reports/P624_assets/*.png` — 13 distinct real-window frames, one per proof step (each verified distinct by hash):
- `look_baseline_forward.png`, `look_right.png`, `look_rear.png`, `look_left.png`, `look_frontleft.png`, `look_frontright.png`, `look_rearleft.png`, `look_rearright.png` — the seven look directions.
- `radar_all.png`, `radar_default.png`, `radar_target.png`, `radar_minimal.png` — the four radar modes.
- `vectorlock_strafe_match.png` — the flight-command exercise phase.

### Suite
Three consecutive `scripts/run-tests.sh` runs on the final code (each measured as-it-happened):

| run | Lobby | MonoGame (all P624 tests) | ClientCore | Core | verdict |
|---|---|---|---|---|---|
| 1 | 12/12 | 1922/1922 | 672/672 | 4008/4009 (1) | 1 ledgered flake |
| 2 | 12/12 | 1922/1922 | 672/672 | 4007/4009 (2) | 2 ledgered flakes |
| 3 | 12/12 | 1922/1922 | 671/672 (1) | 4003/4009 (6) | 7 ledgered load-flakes (high-load run) |

Every failure is a **known ledgered live-network load-flake** (docs/TEST_FLAKES.md lines 216, 268–272, 432 — `LiveChatSectorWireTests`, `AsteroidMinedLiveTests`, `GameOverPlayersWireTests…Win_by_elimination`, plus the run-3 `SessionLossLiveTests`, `ShipUpdateBudgetLiveTests`, `ShipsUpdateLiveTests`, `LiveWinConditionTests`, `LiveTeamLobbyTests`, `LiveRipcordTests`, `AddPartTests` — fixed-sleep live-wire receipt under full-suite load). None involve P624: the P624-bearing assembly (`Allegiance.ClientCore.MonoGame.Tests`) was **clean 1922/1922 in all three runs**, and P624 touches client input/camera/radar/help only. Whole-solution build: **0 warnings, 0 errors** (both Debug and Release).

---

## 6. Parity-scorecard refresh

Updated 12 command rows in `docs/parity/parity-matrix.csv` (missing→done, with port cites). Re-measured from the CSV:

| Area | rows | done | partial | missing | na | done% (unwtd) | done% (weighted) |
|---|---|---|---|---|---|---|---|
| commands | 203 | 112 | 26 | 52 | 13 | 58.9% | 67.3% |
| fedmsg | 136 | 83 | 42 | 0 | 11 | 66.4% | 67.4% |
| parts | 40 | 31 | 9 | 0 | 0 | 77.5% | 82.4% |
| screens | 27 | 16 | 7 | 1 | 3 | 66.7% | 73.6% |
| commander | 20 | 18 | 1 | 0 | 1 | 94.7% | 96.3% |
| lobby | 12 | 9 | 3 | 0 | 0 | 75.0% | 76.5% |
| TOTAL | 438 | 269 | 88 | 53 | 28 | 65.6% | 71.2% |

P624's contribution: **12 command rows closed** (missing→done). The current total (438) reflects command/screen rows accrued by intermediate waves (P623 targeting family and P624) beyond P617's original 436.

## 7. Files changed

New: `Rendering/HeadLook.cs`, `Rendering/RadarLod.cs`, `Rendering/HelpHeader.cs`, `Game1.FlightProof.cs`, `tests/…/P624FlightKeysPackTests.cs`, `docs/reports/P624_assets/*.png`.
Modified: `Rendering/InputActionCatalog.cs`, `Rendering/InputBindings.cs` (alternates), `Flight/FlightInput.cs`, `Game1.Director.cs`, `Game1.FlightInput.cs`, `Game1.Rendering.cs`, `ClientCore/Flight/OwnShipSimulator.cs`, `Rendering/CockpitCamera.cs`, `Rendering/ChaseCamera.cs`, `Rendering/HelpOverlayRenderer.cs`, `docs/parity/parity-matrix.csv`, tests (`P625bActionMapGuardTests`, `P625KeyMapTests`, `HelpOverlayTests`).
