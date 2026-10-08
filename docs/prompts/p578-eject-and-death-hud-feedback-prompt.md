# P578 — eject/death feedback like native: red critical message + sound ("You ejected" / "You were killed" / "You were forced to eject by X"), the eject-pod HUD panel, the camera swing to your killer, and the death banner

## Why this task exists

**Domingo:** "wasn't there some kind of warning banner when you ejected in your
pod?" Yes. Claude traced native (2026-10-07). **The port shows none of it**
(no `CriticalTip`, `ejectpodbmp`, `deathbanner` or eject-pod display anywhere
in `src/`).

**Native:**
1. **Critical message.** `clintlib/appmsg.cpp:2797-2844` (`FM_S_KILL_SHIP`
   handling): `PostText(true, bDeathCredit ? "You were killed" : "You ejected")`
   for yourself; "You were forced to eject by %s" / "You forced %s to eject" /
   "%s was forced to eject by %s"; non-critical lines for others ("%s was
   killed" / "%s ejected"). `PostText(true,…)` → `PostPlainText`
   (`WinTrek/trekigc.cpp:3067-3083`): critical plays `newCriticalMsgSound`
   and sets `ConsoleData::SetCriticalTipText`; non-critical plays
   `newNonCriticalMsgSound` and `SetTipText`. HUD (`Artwork/dialog.mdl`):
   `CriticalTip0Image` (centred, 620 wide, medBoldVerdana, **red**,
   `CriticalTipText0`), tip lines `TipTextImage/1/2` in the side colour
   (`:2006-2060`, composed at `:~2082`). Check how long tips stay (ConsoleData)
   and whether the port already has a tip/critical line to reuse.
2. **Eject-pod HUD panel.** `dialog.mdl:1926-1960, 1984-1996`:
   `EjectPodImage = SwitchImage(IsEjectPod(Me), …)`, placed at `SideCenter,
   Point(-86,-87)`, `ofInFlight` (`:5435-5436`). Classic style
   (`DefaultEjectPodDisplayOn`): the `conejectpoddisplaybmp` panel + a gauge
   (`softgaugebmp`) of `GetEndurance(Me)`; Glass style
   (`GHEjectPodDisplayOn`): "You have ejected." + an "O :" readout (read the
   rest of the block for its value). Also `IsNotEjected` (`:208`) zeroes the
   ammo/energy/fuel gauges while in a pod (`:435, 470, 507`). Find what
   `GetEndurance` means for a lifepod in `consoledata.h`/IGC (oxygen/time
   left?) and port the value.
3. **Camera swing to the killer.** `WinTrekClient::EjectPlayer(pcredit)` →
   `GetWindow()->OverrideCamera(trekClient.m_now, pcredit, true)`
   (`trekigc.cpp:3101-3104`). Find where it's called and how long the
   override lasts (`OverrideCamera` in `WinTrek.cpp`), then mirror it
   (temporary look at the launcher, then back to the normal camera).
4. **Death banner.** `DeathBannerPane = SwitchPane(IsGhost, deathbannerbmp)`,
   `SideCenter, Point(-105,15)`, `ofAlways` (`:5438-5439`).

Art on disk: `~/Development/Artwork/ejectpodbmp.mdl`, `deathbannerbmp.mdl`,
`conejectpoddisplaybmp`, `softgaugebmp` (check names), sounds via
`sounddef.mdl` (`newCriticalMsgSound`, `newNonCriticalMsgSound`).

## Scope

1. Critical + non-critical tip lines on the HUD (if the port has none, add a
   minimal native-faithful one), with native's eject/kill texts for self and
   others, sounds per native.
2. The eject-pod panel (match the port's current HUD style; implement the
   style the port uses, and the other one if cheap), with the endurance gauge
   and the zeroed gauges in a pod.
3. The camera override to the killer on eject, per native duration.
4. The death banner when a ghost.
5. Works with P540/P571 eject autopilot and P575 docking (don't break the pod
   flying home).

## ➕ Rescue messages + pod endurance (Domingo, 2026-10-07)

- **Rescue messages:** native handles `FM_S_PLAYER_RESCUED`
  (`clintlib/appmsg.cpp:311-340`): the rescuer gets the non-critical "You
  rescued %s." (`:324`); the rescued pilot gets the **critical** "%s rescued
  you." (`:338`). The server already sends `PLAYER_RESCUED(16)`
  (`Server.Broadcast.cs:113-124`, `PerformRescue`), but the client ignores it.
  Port both lines through the same tip/critical-tip path as item 1, with
  native sounds.
- **Pod endurance:** `c_fLifepodEndurance = 300.0f` (`Igc/missionigc.cpp`,
  near `c_pointsRescues`): the pod's oxygen/time budget, which is what the
  eject-pod panel's `GetEndurance(Me)` gauge shows. Check how native spends it
  (and what happens at 0) and drive the gauge from the port's equivalent.
  If the port has no endurance, port it as part of this task only if it's a
  pure client display of a server value; otherwise report the gap.

## Proof

- Failing-first tests: the text per (self/other, killed/ejected/forced),
  critical vs non-critical routing, the pod-panel visibility switch, gauge
  values while ejected.
- **Real window (P572 tooling):** get forced to eject by a bot (MCP scenario)
  → screenshot of the red critical line + pod panel; a frame showing the camera
  on the killer; the death banner. Audio events logged.
- Report `docs/reports/P578_EJECT_DEATH_FEEDBACK.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
