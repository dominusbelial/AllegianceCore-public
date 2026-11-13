# P1006 — Command screen input and duplicate menus

The input fixes are implemented for Classic and Modern. Visual qualification is
currently blocked: the supplied Debian container has no OpenGL/EGL runtime, and
`Game1.Run()` fails before its first rendered frame. No screenshots were produced. Gate results and counts are recorded below.

## Findings and native intent

| Finding | Classification | Native evidence |
| --- | --- | --- |
| Invisible hangar buttons accept clicks in docked command view; clicking Loadout switches away from the map. | Port bug | `WinTrek/WinTrek.cpp:6669-6685` clears capture/held buttons; `:2180-2216` includes a screen's input pane in its image; `:6706-6740` replaces hangar/loadout with command/combat and releases the screen. `WinTrek/hangarscreen.cpp:181-198` supplies explicit command/loadout transitions. |
| Hidden loadout keys can reach the shop in drone follow/cockpit command modes. | Port bug | The same exclusive screen replacement, `WinTrek/WinTrek.cpp:6728-6740`; loadout and hangar have separate screens at `:6706-6726`. |
| Explicit inventory, investment, team, chat and Escape actions remain available in command view. | Native design | `WinTrek/WinTrek.cpp:10001-10021` opens loadout only when docked, otherwise toggles inventory; `:10025-10034` permits investment; `:10038-10081` permits team panes; `:9441-9446` starts chat; `:9304-9320` permits MainMenu outside override/composition/lockdown. These actions are retained. |
| Splash-skip Escape can open MainMenu before advancing to intro. Intro subview Back can also open another menu; with a popup open, both menu cursors/text fields receive input. | Port bug | `WinTrek/WinTrek.cpp:9281` excludes splash from global keys; `WinTrek/splashscreen.cpp:76-81` owns its skip. Native popup consumes all key/character input in `engine/popup.cpp:391-411`, installed before screen focus at `WinTrek/WinTrek.cpp:2963-2969`. |
| The first-run training suggestion and an intentional Escape popup on the root intro are valid native surfaces. | Native design | `WinTrek/introscreen.cpp:1305-1324` creates the first-run suggestion; `WinTrek/WinTrek.cpp:9304-9320,4229` permits/opens the Escape popup. The port's subview Back keys are a keyboard adaptation of native's mouse-driven intro, `WinTrek/introscreen.cpp:1212-1228`. |
| A briefing survives a late join to a started game when there is no countdown to expire. | Port bug | `WinTrek/teamscreen.cpp:1673-1704` opens it only in STAGE_STARTING and closes it in other stages. |
| An obsolete HANGAR text menu is still submitted alongside the native artwork; hangar is painted after other panes. An investment early return can also leave the hangar active after authoritative launch. | Port bug | Native paints console above backdrop (`WinTrek/WinTrek.cpp:3224-3229`); station departure requests the launch camera (`WinTrek/trekigc.cpp:2917-2918`), then returns to combat (`WinTrek/WinTrek.cpp:6544-6546`). |

No era constraint requires any of these input leaks or duplicate port menus.
Native citations were rechecked against the mounted source, not copied from an
older report's line numbers.

## Root causes and changes

Source line references below describe the final working tree unless marked
**base**, which refers to the task's original `a87b00f3` sources.

- **Base** `Game1.Panes.cs:211-213` calls `UpdateHangarControls` before checking
  command view. **Base** `Game1.Hangar.cs:193` only tests composition and three
  overlays; it does not test whether its own screen is visible. Shared
  `_nativeHangarScreen` / `HangarControlsActive` in
  `src/Allegiance.ClientCore.Game/Game1.Hangar.cs:32` now gate draw and pointer
  input. Hidden frames still observe button state, preventing a held press from
  becoming a new click on return. `DockedLoadoutView` shares the Escape/rendering state, so retained loadout
  cannot consume Escape in a drone camera. `Game1.Panes.cs:213` rejects hidden shop keys
  in every command camera mode. `Game1.ConsoleModes.cs:71` makes explicit F4
  enter loadout from command regardless of the previous docked screen.
- **Base** `Game1.FlightInput.cs:517-584` handles global Escape before the
  splash/intro dispatch at `:753-756`; **base** `Game1.Rendering.cs:383,2175`
  draws both intro and MainMenu. `Game1.IntroScreen.cs:185` now routes splash
  and subview Back before global input. `:204` gives an intentional root popup
  sole key focus and observes held navigation keys through its closing frame.
  `Game1.Session.cs:1640` similarly gates text input. Rendering shows one menu
  while retaining the underlying intro state.
- **Base** `Game1.ConsoleModes.cs:269-273` retains the old HANGAR text menu;
  it is removed. Hangar art is now drawn as a backdrop before popups and
  console panes (`Game1.Rendering.cs:1957`). Docked loadout/teleport/board text
  also require the Flying screen and exclude every command camera.
- **Base** `Game1.FlightInput.cs:1423` can return for investment before the
  docked cleanup at `Game1.Panes.cs:342`. `ClearUndockedHangar`
  (`Game1.Panes.cs:349`), invoked at `Game1.FlightInput.cs:394`, retires the
  shop and teleport as soon as the authoritative station ID clears, before
  modal input can return. Investment remains available in space.

**Base** `Game1.Panes.cs:1146-1160` also lets briefing text outlive the starting
stage when no local countdown exists. The final `IsBriefingVisible` stage gate
closes that late-join/launch overlap; the new two-style regression retains text
while changing stage to prove it cannot linger.

All changes are shared between HUD styles; no style-specific input branch was
added. Matching MainMenu, ViewCommand, inventory, hangar, intro and splash rows
are updated in `docs/parity/parity-matrix.csv`, with the screen ownership entry
in `GLOSSARY.md`.

## Reproduction and coverage

`tests/Allegiance.ClientCore.MonoGame.Tests/P1006ScreenInputTests.cs` drives the
production pointer, docked keyboard, console switch, intro dispatcher and text
handlers without a graphics device. Its **58 cases** cover both HUD styles:

- All ten hidden hangar buttons while docked in command and while airborne.
- Down/Enter/Space/Tab/E/L/U/0 against the hidden shop in command, drone follow
  and drone cockpit; explicit F4 still opens loadout, including command entered
  from loadout.
- Loadout/covering panes disable hangar hit testing; hidden mouse edges and
  releases are observed.
- Splash Escape and intro subview Back are consumed before global menus.
- Popup and closing-frame navigation/activation/text do not alter the intro.
- Authoritative launch retires hangar state even with investment open.
- Briefing exists only in the starting stage, including late joins without a
  countdown; started/not-started/over and missing mission data hide it.

The final 58-case fixture passed **58/0/0** in each of three isolated runs
(**174 passed case executions**). The final affected MonoGame Fast project
passed **5,185/0/17**, including the two added briefing-stage cases. Its longest
case was **3.013 s**.

The real-client proof `tools/p1006-ui-proof/` additionally scripts fresh-profile
first start, skipping splash, dismissing the suggestion, the root Escape popup,
hangar, docked command/hidden clicks, explicit loadout, launch, and command in
space, using the real backbuffer and a loopback server. Each run has a 180-second
watchdog and joins its simulation thread on exit. `--baseline` records pre-fix
failures for comparison. Usage is in the tool's README.

### Full-suite qualification

The complete Fast sweep ran **14,205 cases in 544.0 s: 14,181/4/20**, with
**9/0/0 infrastructure checks**. Its gate is **FAIL**, including five executions
over the calibrated **11.804 s** limit. All 56 P1006 cases present at that point passed in that sweep.
The four assertions were missile-lock partial-tip delivery, miner sell-cycle
completion, roster accumulated time (8.05000019 against [4,8]), and an asteroid
capacity receipt. Four additional passing methods exceeded the timing limit;
the failed missile-lock method was also slow.

All eight distinct methods were run three times alone. Missile lock failed
**0/1/0** each time. The other seven methods passed all repeats: gunner preference
**1/0/0**, support selection **2/0/0**, P999 and P1000 civilization profile methods
**10/0/0** each, and roster/miner/capacity methods **1/0/0** each, per repetition.
Their longest isolated cases were under 11.804 s. Full per-method counts and
measurements are in `docs/TEST_FLAKES.md` (P1006 entry). Repeats do not erase the
failed sweep. No unrelated fixture, receipt or timing threshold was changed.

### Archived visual review

I viewed the existing [Classic hangar](P996_assets/hangar-after-pod-death-classic.png)
and [Modern hangar](P996_assets/hangar-after-pod-death-modern.png) captures. Both
show the mission briefing painted beneath the hangar artwork, with text and
panel fragments visible at its sides. I also viewed the P946 Classic/Modern
intro captures; the idle main intro contains one menu. The duplicate intro
input path is triggered by Escape during splash/subview transitions.

These existing captures identify the layering problem; they are historical
context, not newly generated P1006 before/after qualification. The native
briefing exists only in `STAGE_STARTING` and closes in other stages
(`WinTrek/teamscreen.cpp:1673-1704`). The port previously relied only on a local
countdown expiring, so a late join to an already-started match could retain the
briefing indefinitely. The final fix adds the authoritative stage gate as well
as correcting the backdrop order. Both styles have a regression for starting,
started, not-started and over stages, including no local countdown.

### Capture limitation

The baseline client built with **0 warnings / 0 errors**. Its Classic intro
capture attempt failed with
`NoSuitableGraphicsDeviceException -> NullReferenceException at
MonoGame.OpenGL.GL.GetString`, before rendering. The container has neither
`libGL`/`libEGL` nor Mesa drivers, Xvfb, or cached rendering packages. The brief
forbids contacting public hosts, so no external packages were downloaded.
The pre-fix binaries are preserved under task scratch for later comparison.
There are **no newly captured P1006 before/after screenshots to inspect**. A local OpenGL runtime is still needed to complete those fresh captures.

## Gate

The report was written before starting the gate, as requested. Counts are
Passed / Failed / Skipped. The complete Fast sweep ran once; after the final
Escape/rendering consistency and briefing-stage refinements, the affected UI
project and the 58-case fixture were rerun on the final code. Unchanged core/server failures remain in the full
sweep result. The additional training gate ran both client projects serially
across Fast and Live three times, with artwork. Nanite embedded-host completion
passed in all three repetitions. Each repetition passed **126/0/0** in
ClientCore and **247/0/47** in MonoGame, for **1,119/0/141** in aggregate. Each
MonoGame run skipped 44 graphics cases, two opt-in M4 branch proofs and one
audio-device case. These skips leave the visual requirement unqualified.
Training ran before the final briefing visibility guard; its final shared UI behavior is covered by the
affected UI Fast rerun and 58-case fixture. No training host/mission logic changed.

Machine-readable counts and isolated-method receipts:
[P1006 qualification](P1006_assets/qualification.json).

| Check | Passed / Failed / Skipped or result |
| --- | --- |
| Solution build | PASS: 0 warnings / 0 errors (final build 13.22 s) |
| `tools/build-tools.sh --drop-bins` | PASS: all 136 tool projects; 0 warnings / 0 errors |
| New tests, alone ×3 | PASS: 58/0/0 each repetition |
| One full fast suite, three-hour outer bound | FAIL: 14,181/4/20 in 544.0 s; 9 infrastructure checks PASS; 5 timing overruns |
| Final affected UI project Fast rerun | PASS: 5,185/0/17; longest case 3.013 s |
| Training Fast + Live, both client projects ×3 | PASS with skips: 126/0/0 + 247/0/47 each repetition; total 1,119/0/141 |
| Glossary links / parity / diff hygiene | PASS: 799 glossary paths; parity validation and diff/public hygiene scans |
| Classic/Modern real-client captures, before and after | Blocked: missing local OpenGL runtime |

The initial default restore failed because the user-level NuGet HTTP cache is
read-only. Subsequent builds use task-local CLI/HTTP caches, the preinstalled
package files, and a loopback feed containing the supplied official NuGet
vulnerability database (dated 2026-11-12). NuGet audit remains enabled; the
vulnerability content is unchanged. No public package server is required.
The report, parity data and glossary checks pass; the full Fast sweep remains
failed as qualified above, and fresh rendering/captures remain blocked. The
brief is preserved in ignored task scratch. All product changes are left in
the working tree; no commits or pushes were made. Every verification process
was stopped, including the task-local audit server.

The first tools sweep hit a local `mgcb` resolver entry referring to the
redundant cache removed to conserve disk. The entry was repaired and the full
136-project tools build was rerun successfully; no product change was needed.
Docker is absent, as specified by the brief; this task adds no service, store,
port or environment variable to the product.

Native intent: one active screen owns its controls; native console shortcuts
and intentional popups remain reachable. First-run suggestion is native design;
leaked input and stale/doubled port menus are port bugs.

Modern approach: use shared screen ownership, consume startup/Back input before
global menus, retire docked state independently of modal input, and verify both
HUD styles through production handlers and a real-client capture harness.
