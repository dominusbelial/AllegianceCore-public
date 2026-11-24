# AFK seats — P1078

Status: implementation and loopback proof complete. Qualification has known full-Fast
failures and two required browser checks awaiting workstation reruns (details below). This report
was created before implementation and updated during the work. Changes remain in
the working tree; no commits, pushes, package installs, remote runs or deployments.

## 1. Audit before implementation

| Native path | Desktop before P1078 | Browser before P1078 | Shared server / orchestration before P1078 |
|---|---|---|---|
| Automatic AFK, `WinTrek/WinTrek.cpp:8182-8214` | No inactivity timer. | No page inactivity timer. | `PLAYER_READY` from pending side-select peers was ignored. |
| Manual AFK, `WinTrek/teamscreen.cpp:2758-2768`; `WinTrek.cpp:8189,8203` | Ready-check Y/gamepad/tap toggle existed, without the suppression latch. | Explicit launch sent ready; no manual AFK control. | `_playerReady` already stored individual flags by connection. |
| Game-over leader AFK, `WinTrek/gameoverscreen.cpp:410-420` | Game-over screen and sound existed; no leader AFK send. | No leader AFK send. | Correctly preserved individual readiness during rotation. |
| Preserve AFK, `FedSrv/fsmission.cpp:649,1144,3468-3481`; `clintlib/clintlib.cpp:3903,4050,4110` | Local readiness was cleared at rotation and game-over Back; the ready-check also had a stale rotation re-ready path. | Launch explicitly readied; no persistent activity controller. | Migration already preserved `_playerReady`. |
| Automatic distribution, `FedSrv/fsmission.cpp:5137-5242` | Shared roster/ready consumers existed. | Shared roster/ready consumers existed. | Native randomize already filtered individual readiness. Timed auto-seat and carry-forward seating did not. |
| AFK roster, `WinTrek/teamscreen.cpp:452-477,497-505` | Shared `[AFK]` rendering already worked in both styles. | Shares the roster model; no new team-pane renderer added here. | Backfill ignored readiness. Live relays reached seated mission peers; join snapshots sent only ready=true teammates. |

## 2. Client activity

`PlayerActivity` is one shared state machine compiled into both client hosts.
It applies the native strict `>90s` on NOAT and `>180s` on a real side, updates
activity from mouse/pointer movement, keys and buttons, and sends only transitions
(`WinTrek/WinTrek.cpp:8182-8214`). Initial logged-on activity matches native's
`g_bActivity=true` (`WinTrek.cpp:44`). Wall time is independent of simulation speed.

Desktop samples input in the outer Game1 update, including all menus and early
returns. Captured relative mouse motion is observed without consuming flight
input; capture flushes do not manufacture activity. Input recorded while logon
is pending is consumed on the next logged-on update, as native's `g_bActivity`
(`WinTrek.cpp:807,8183-8188`), rather than being lost with the join frame. Browser capture listeners
cover the page, including HTML fields and controls. A separate one-second timer
checks inactivity even when RAF stops. Hidden-page and lifecycle events never
manufacture input; listeners and the timer retire with the page adapter.

The desktop ready key/tap and browser AFK button own native manual suppression
(`teamscreen.cpp:2758-2768`; `WinTrek.cpp:8189,8203`). Explicit Join and commander
Team Ready release it (`teamscreen.cpp:2783-2790,2915-2916`). A ready team leader
is marked AFK once for the real game-over event (`gameoverscreen.cpp:410-420`).
The receive model captures outgoing leadership before rotation retires the old
roster; the host drains that signal even if the full next-match burst and scoreboard
clearing happened before its next frame/timer. A newly elected leader is not
mistaken for the outgoing leader. Ordinary side,
Back and rotation paths preserve AFK rather than resetting it (citations in the audit).

## 3. Automatic seating — port adaptation

`AdvanceSideSelection` skips a pending peer whose last `PLAYER_READY` is false.
An explicit `POSITIONREQ`, including pending auto-pick, still seats immediately.
Ready=true makes an expired timed pending seat eligible again. With automatic
seating disabled, active and AFK pending peers both continue waiting for a choice.

The same opt-in filter applies to automatic carry-forward assignment of an
already-unseated peer. With the option off, its existing carry-forward policy is
preserved; this is covered for both null and zero, as well as active/AFK opt-in cases.

The filter reads the player's own `_playerReady`, following native distribution's
`GetReady()` (`FedSrv/fsmission.cpp:5228-5241`), rather than team-ready or force-ready.
The timer itself remains a port adaptation. Unknown readiness retains the existing
legacy-client timer behavior; only an explicit false blocks it.

The ready handler now accepts authenticated pending mission members, corrects the
ship id to the sender's reserved/live identity, stores the flag and relays it to
seated and pending members of the same mission. Native relays to the whole mission
including NOAT (`FedSrv/FedSrv.CPP:6990-7026`). Existing admission, relay budget and
explicit position-request checks remain authoritative.

## 4. Never-launched seated players — decision and port adaptation

With timed auto-seat enabled, an explicitly AFK player who is still docked and
has never launched in the outgoing match returns to `SIDE_TEAMLOBBY` (-2) at the
next match boundary. The player stays connected and unready and may choose a side
again. This uses the existing never-reset `LastLaunchTime` sentinel and current
station state; a previously launched player, including one who docked again, and
a currently flying player retain their side. No new tracking store is needed.

This is a port adaptation of the native automatic-distribution intent to ignore
AFK (`FedSrv/fsmission.cpp:5228-5241`), using the native NOAT destination
(`clintlib/clintlib.h:571`). Native does not have this timer/migration policy.
It is deliberately limited to opt-in timed seating so the default remains unchanged.
The desktop's existing NOAT screen handles the return. Browser presentation now
returns to the side picker, releases flight input, and opens the hangar when an
explicit or timed seat arrives; neither navigation path changes readiness.

## 5. Active-human backfill — port-only orchestration

`BackfillPilot.Ready` comes from the probe's current `TeamRoster`, keyed by the
native ship identity. `BackfillSeats.Decide` counts all bots plus seated humans
without an explicit false flag. A human becoming AFK uses the existing departure
grace before refill; an active seated human uses the existing combat-bot withdrawal
order. Commander/economy/external protection and bot counts are unchanged.

Late probes now receive current false **and** true ready flags across the mission,
not only ready teammates, using `PLAYER_READY` alongside the existing roster.
Native carries current readiness in `PLAYERINFO.fReady` (`FedSrv/FedSrv.CPP:2114`)
and broadcasts changes (`FedSrv.CPP:7018-7025`). The port uses explicit ready
snapshot messages rather than changing its established PLAYERINFO application.

## 6. Proof

The real `BotManager` / `BackfillDriver` loopback uses auto-seat 30 seconds and
five seats per side. It proved: pending false survives the deadline and rotation
with five bots per side; returning activity plus a side request seats the human
and withdraws one combat bot; later seated AFK refills after the real 30-second
grace; the next boundary returns that never-launched player to NOAT with five bots.
The test injects deterministic 90.001 / 180.001-second client clock samples; it
uses real TCP, the real server simulation deadline and real backfill grace.

Timing distinction: an initially active guest can be seated by the 30-second
timer before reaching NOAT's 90-second threshold. Native then uses **180 seconds**
on the real side. The implementation preserves those rules. It does not shorten
the native seated timeout to meet an impossible 90-second combined schedule.

Actual Game1 windows received the idle peer's real `PLAYER_READY(false)` and rendered
`IdlePilot [AFK]` through the existing shared roster. Both captures were opened and
visually checked; no private artwork or identity content was introduced.

- [Classic roster](AFK_SEATS_P1078_assets/afk-classic.png)
- [Modern roster](AFK_SEATS_P1078_assets/afk-modern.png)

Reproduction: `tools/p1078-afk-proof/Program.cs` (actual desktop windows),
`tools/p1078-page-activity-tests.cjs` (production page adapter lifecycle/input),
`P1078AfkSeatsLiveTests` (real server and backfill). Direct Chromium timer/input
proof via `tools/p1078-browser-afk.cjs` passed **three times** against the final
published build: Headless Chromium 153 marked the guest AFK after **91,010 / 90,952 /
90,953 ms**, measured from the first ready observation. Focus alone did not wake
it, actual pointer movement did, and manual AFK suppressed actual pointer and
keyboard input until explicitly released. Every run had zero page runtime
exceptions. The one-second page timer explains the observation granularity; the
policy's strict >90s boundary is separately pinned by unit tests.

The headless shell reported `document.hidden=false` after activating a second
target, so this capture proves real page inactivity, not a hidden-tab lifecycle.
Hidden-page timer/input behavior is covered by the production-adapter tests.
[Browser timing/input receipt](AFK_SEATS_P1078_assets/browser-afk.json).

## Validation

- Solution build: **0 warnings, 0 errors**.
- Local Release browser generation: published successfully with unchanged verified artwork packaging.
- Native parity CSV: **10 rows validated**; glossary link check: **1,145 paths valid**.
- C# new/affected methods: **54 methods × 3 isolated hosts = 162 invocations,
  270/270 theory/fact cases passed, zero skipped**. Covers activity on both
  compiled hosts, AFK roster, seating/ready relays, backfill, native rotation and
  existing reconnect/departure backfill behavior.
- JavaScript new/affected cases: **30 cases × 3 isolated Node processes = 90/90
  passed**, including the existing pointer/audio/gamepad and P1074 keyboard tests.
  Fake browser windows now supply the timer API; the new adapter tests verify
  timer retirement and distinguish real input from visibility/focus changes.
- Final-source tool build: **all 154 projects built, zero warnings/errors**.
- Initial full Fast sweep: **16,105 cases in 563.4 s: 16,094 passed / 4 failed /
  7 not executed**, plus four calibrated timing overruns. Two failures were
  constructor-bypassing UI fixtures missing the new controller. Both are corrected
  and pass three isolated runs; the commander test also verifies manual AFK release.
- Second full sweep: **16,109 cases in 571.0 s: 16,099 passed / 3 failed /
  7 not executed**, plus three timing overruns. Both corrected UI tests and all
  P1078 tests passed. Remaining failures are existing spectator/mining stability
  families.
- Final-source full Fast sweep: **16,109 cases in 565.1 s: 16,100 passed / 2 failed /
  7 not executed**, plus three calibrated timing overruns. All P1078 tests and
  every browser, desktop UI, lobby and updater project passed. The remaining failures
  were `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
  (no deletion receipt within 900 ticks) and
  `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`
  (observer held old-sector status only). The calibrated limit remained unchanged
  (11.541 s for this run). **The full Fast gate remains red.**
- All ten existing failed/slow methods across the three sweeps ran **3× alone
  against final source: 87/87 cases passed in 30 fresh hosts**. An earlier mining
  rerun was pass/fail/fail; those failures remain recorded. No unrelated simulation
  fix, quarantine or threshold change is claimed. See the
  [flake ledger](../TEST_FLAKES.md#p1078--afk-seats-qualification-2026-11-23).
- Required P1068/P1074 browser scripts: both attempted; neither can start because
  Playwright is absent. No packages were installed. Workstation reruns remain
  required by the brief. The successful direct Chromium/DevTools proof is
  additional evidence and is not represented as either required script.

Raw build and isolated TRX logs remain locally in `.scratch/p1078/`; sanitized
browser receipts and reviewed captures are in the report assets directory.
[Sanitized validation receipt](AFK_SEATS_P1078_assets/validation.json) includes
all sweep counts and the final isolated outcomes. The only unfinished execution
is the two named Playwright checks; they require an environment with Playwright.
No further implementation item remains open. Work stopped within the four-hour cap.

No training, server configuration defaults, services, ports or environment variables
were added or changed. No remote environment was accessed.

## Claude review additions (2026-11-24)

Two live findings from Domingo flying the public 5v5 during review were fixed in the same merge.

1. **Second Launch while waiting for the next match.** The browser hangar's Launch before
   `STAGE_STARTED` hands the page a ready → start → dock request and sets `LaunchPending`.
   A second click was not blocked by `LaunchPending`, so the page answered it as busy
   ("The server is still completing the previous launch request.") and cleared the
   hangar's wait line, although the first launch then completed. Now:
   - `BrowserHangarModel.Launch` ignores clicks while a launch is pending;
   - the page ignores a game-window request that races a running launch;
   - the wait line shows the start countdown (`HangarLaunchText.Waiting`).

   Test: `P1070HangarTests.A_second_launch_while_waiting_for_the_mission_start_is_ignored`.
2. **Undocking as a pod after a match rotation.** A carried-forward player receives the next
   match's MISSIONDEF but no fresh SIDE_ROSTER. The client ignored MISSIONDEF's `rgCivID`, so
   the side kept the previous match's civilization. The lifepod resolver then returned the
   wrong civ's pod (Rixian instead of Dreghklar hull 33). As a result:
   - `OwnShipDockedInLifepod` was false;
   - the P996 restore of the last real loadout never ran;
   - the loadout screen showed the pod;
   - Launch sent `BUY_LOADOUT` for hull 33 (server log: `hull 33 nParts 0 fbought False fLaunch True`).

   Native re-applies each side's civilization from every MISSIONDEF (`clintlib/appmsg.cpp:2410-2424`
   → `Igc/missionigc.cpp:3751-3757`). `ClientWorld` now mirrors that civilization identity (the random
   flag is kept), and `ResolveLifepodHull` prefers it over a side-select roster. Desktop and
   browser share both. Tests: `P1078MissionDefCivilizationTests` (2 cases).

Review validation on the workstation:
- build 0/0;
- the new tests 3× alone;
- browser game 320/320; browser head 52/52;
- P1078 ClientCore 21/21; affected MonoGame 37/37;
- page-activity, P1051 and P1074 Node tests;
- **`tools/p1074-browser-keyboard.cjs` and `tools/p1068-browser-guest.cjs` passed** on a fresh generation
  (the two Chromium checks the lane could not run);
- one full Fast sweep (see the flake ledger).

## Native intent

Preserve native activity thresholds, manual suppression, individual ready state
and explicit team choices. Keep AFK across ordinary transitions; native automatic
team distribution checks the player's own ready flag and ignores AFK.

## Modern approach

Share the policy between desktop and browser, feed host input and monotonic time,
reuse mission-wide ready messages and current rosters, and keep timed seat release
and active-human bot orchestration explicitly documented as port adaptations.
