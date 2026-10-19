# P745B — native finale deadline and human last-base objective

Implemented on `codex/p745b`. Changes remain in the working tree; no commit or push.

The finale deadline now calls `Mission.EvaluateCountdown(now, deadlineReached: true)` and passes its winner (or `GameOver.NA` for a draw) to the existing `DeclareGameOver` path. The override bypasses only the match-length guard; it still requires a started mission and never changes `CountdownDuration` or `CountdownStart`. The requesting side is retained as `ArmedBy` for diagnostics and authorization, without contributing to the result. Ordinary GameLengthSeconds retains P739's strict `elapsed > duration` boundary; the port finale fires at `now >= deadline`.

The shared game-over path finalizes scores, sends GAME_OVER and GAME_OVER_PLAYERS, and rotates/reseats the mission. The native references for that sequence are `~/Development/Allegiance/src/FedSrv/fsmission.cpp:3225-3263,3427-3454`; the existing port rotation collapses the native reset/rebuild phases (`Igc/missionigc.cpp:3804-3830`). No bot targeting, siege behavior, research rates, or loadout selection changed.

## Native ranking evidence

All native paths below are relative to `~/Development/Allegiance/src/` and were read locally.

| Priority | Rule | Native source |
| --- | --- | --- |
| 1 | Most flag-capable stations, when conquest is enabled | `FedSrv/fsmission.cpp:2346-2370` |
| 2 | Most territory sectors, when enabled | `FedSrv/fsmission.cpp:2373-2393` |
| 3 | Most team-money development time invested, when prosperity is enabled | `FedSrv/fsmission.cpp:2396-2420` |
| 4 | Most artifacts, when enabled | `FedSrv/fsmission.cpp:2423-2443` |
| 5 | Most flags, when enabled | `FedSrv/fsmission.cpp:2446-2465` |
| 6 | Most kills, then fewer deaths among equal kills | `FedSrv/fsmission.cpp:2468-2490` |
| 7 | Unresolved tie is a draw | `FedSrv/fsmission.cpp:2494-2503` |

Each criterion starts again with **all** sides; tied leaders do not become a restricted candidate set. The native clue for the kills/deaths comparison is `kills`, including a win decided by fewer deaths (`fsmission.cpp:2470,2480-2488`). Logging preserves that clue and includes both counters. A draw logs `by=draw` and the final kills/deaths values. There is no money-balance tiebreaker in the native implementation.

## Human objective

`Mission.LastRestartBases` identifies each active side with exactly one living restart-capable station. Additional non-restart stations do not prevent the objective. Removing that sole restart station is the existing elimination condition (`FedSrv/fsmission.cpp:3698-3713`); deactivation and victory checking remain the existing native path (`fsmission.cpp:5318-5378`). Multiple endangered sides each produce an objective; a side with two restart stations does not.

A complete, binary port-extension notice, FMID **995**, supplies only those objective stations' side/station/sector IDs, position, name, and the finale seconds remaining (`-1` means unarmed). Its size is `10 + 50 × objectives` bytes, at most 310 bytes for six sides. The server sends changed snapshots and independently initializes new peers, including drop-ins. Countdown changes are rounded up to seconds. Empty snapshots clear objectives when a replacement restart station is built. Client kill/capture/side-inactive/game-over/reset handlers also remove obsolete objectives immediately.

Client-only counting would be wrong under fog of war: newly constructed enemy stations are exported only when discovered (`Server.Research.cs:ExportDiscoveredStations`; native predictable-object discovery `FedSrv/FedSrv.CPP:10750-10778,10945-10949`). A client can know one base while another restart station remains unseen. This deliberately scoped extension reveals the objective, without exporting other hidden stations or changing scanner state.

Seated humans see “Enemy's last Garrison: destroy it to win” and “Our last Garrison: defend it”, with `Finale m:ss` only while armed. Allied sides are excluded from hostile objectives. HUD-1/HudModel is absent in this checkout, so `LastBaseObjective` is the headless presentation model. The renderer reuses `DrawTargetSelectionBracket` and `DrawTargetBearingArrow` without changing the player's target or autopilot. Attack/defend arrows sit above the minimap. The same text warning remains visible over hangar and investment panes. For a station in another sector, the marker guides through the existing real next-hop aleph router, then points at the station upon arrival; it never projects foreign-sector coordinates into the current sector.

Native `voFoundEnemyBase` fits the attacking objective (`Igc/sounds.h:199`); `Content/sounddef.mdl:439,1530` maps it to `vo_player_foundenemybase`. It plays once per newly appearing hostile objective, using the existing serialized voice path, without replaying on timer refresh. Defenders receive the matching text warning. Spectator worlds have no own ship and receive no objective/cue; a seated spectate camera is also explicitly suppressed. SP-5's existing spectator score overlay remains in use. Multiplayer and offline Skirmish share the same server/session path.

## Real-client proof

[1440p objective, station bracket, and attack/defend arrows](P745B_assets/objective-1440p.png).

This is an unmodified 2560×1440 MonoGame backbuffer capture from a real loopback client, with normal logon, side selection, readiness, purchase/launch, station rendering, objective notice, and armed 900-second finale. For a readable in-view bracket, the fixture places the enemy's actual Garrison 6,000 units ahead in the player's sector before joining. It does not simulate a siege or claim a rig match. The source fixture and raw capture log are under `/tmp/allegiance-evidence/P745B/proof/`. [A second 1440p capture](P745B_assets/objective-route-1440p.png) leaves the native map untouched and verifies the cross-sector objective and visible routing arrow. [A seated free-camera capture](P745B_assets/spectator-1440p.png) verifies that the same armed match shows neither objective text nor objective markers while spectating.

## Validation

Whole-solution Release build: **0 warnings / 0 errors**. The exact required linux-x64 self-contained Native AOT publish succeeded with no warnings, including no IL2026/IL3050. New/affected methods passed **36 isolated invocations / 78 cases**, and the owned Skirmish drop-in and opt-in local 15v15 showcase rotation each passed **3/3** additional isolated invocations. Final presentation corrections (total-minute formatting at `3600` seconds → `60:00`, seated F8/F9 spectator-camera suppression, and unseated-side suppression) passed additional three-run isolated qualifications. The ClientCore sweep had already started, so those small final corrections are qualified separately; the MonoGame sweep used the final build. Never-parks passed **5/5 changed and 5/5 dispatch base** (`906d032e`), with no regression in the observed failure rate.

Single full sweeps (no second full sweep):

| Assembly | Passed | Failed | Skipped | Status |
| --- | ---: | ---: | ---: | --- |
| Core | 4,883 | 10 | 0 | Complete; all ten failures subsequently pass 3/3 isolated |
| ClientCore | 1,458 | 3 | 1 | Complete; all three failures subsequently pass 3/3 isolated |
| MonoGame | 2,471 | 0 | 3 | Complete; unavailable private Meshy-pack prerequisites |

Across the three sweeps: **8,812 passed / 13 failed / 4 skipped**. All thirteen failed methods subsequently passed three fresh isolated runs; no second full sweep was run. The opt-in local showcase skip was covered separately, and the three remaining skips require the unavailable private Meshy pack.

The ten Core failures are existing socket/scheduling assumptions, listed with every failed attempt and successful recheck in [the verification ledger](P745B_assets/verification.json) and [TEST_FLAKES](../TEST_FLAKES.md). The ordered-world-data transport test also failed two of three isolated observations on the untouched dispatch base, then qualified 3/3 on the changed tree. No transport code, assertion, timeout, or quarantine was changed. The ClientCore failures were the death/respawn native-pose rejection check and the two client warp-transit checks; each passed three fresh isolated invocations. Its only skip was the default-gated P660 local showcase rotation, already passed 3/3 with the opt-in enabled. These are load-flake classifications, not underlying fixes. Raw logs/TRX/native publish and the dispatch-base comparison checkout live under `/tmp/allegiance-evidence/P745B/`. The sandbox permits writes only to this workspace and `/tmp`, so `~/allegiance-evidence/P745B/` is unavailable in this lane. Only screenshots and compact JSON evidence are retained here; total task assets stay below 2 MB.

The new tests cover the complete native ranking, earlier-criterion ties that must reopen consideration to a third side, fewer deaths, draw, unchanged normal clock, authoritative unseen objectives, two-to-one restart transitions, replacement construction, capture, kill, inactive side, game over, reset, spectator suppression, and one-shot cue behavior. Live tests deliver the native winner/draw to both peers through the real game-over rotation path. An owned Skirmish host test starts as a spectator, drops into a side, readies normally, and receives both attack and defense objectives over loopback without external services. The single-sweep existing siege scenario also destroyed an initial base at 290.3 wall seconds and ended by native side elimination at roughly 571 seconds; its assertions were preserved. Raw siege evidence is `siege-from-sweep.log` in the external evidence directory.

## Claude rig check

Expected lines (side IDs/values below are examples; `epoch` and `armedBy` follow the shown fields):

```text
[finale] deadline winner side=1 by=conquest values=0:1,1:3 epoch=... armedBy=0
[finale] deadline winner side=1 by=territory values=0:2,1:4 epoch=... armedBy=0
[finale] deadline winner side=1 by=prosperity values=0:10000,1:20000 epoch=... armedBy=0
[finale] deadline winner side=1 by=artifacts values=0:0,1:1 epoch=... armedBy=0
[finale] deadline winner side=1 by=flags values=0:0,1:1 epoch=... armedBy=0
[finale] deadline winner side=1 by=kills values=0:kills=10/deaths=7,1:kills=10/deaths=5 epoch=... armedBy=0
[finale] deadline winner side=-1 by=draw values=0:kills=10/deaths=5,1:kills=10/deaths=5 epoch=... armedBy=0
```

Expect one deadline-result line per expired armed epoch, then the usual `[scoring]` / `[gameover]` / rotation logs. A base-kill victory before the deadline uses the existing elimination path and emits no deadline-result line. Conquest/territory/etc. participate only when enabled, as in P739/native. Rig acceptance: the winner must follow the logged values and native precedence, independently of `armedBy`; repeated wins by one side are valid when its native scores justify them. Confirm that the human's last-base objective clears on the real final kill and that spectator-only matches end fairly. Rig/221/200v200 execution is left to Claude as requested.
