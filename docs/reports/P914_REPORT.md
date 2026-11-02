# P914 — preset deadline station kills and result display

Implemented P896 Part 4 A+D as an opt-in preset rule. No native parity rows changed. No commits, pushes, remote hosts, or rig configuration changes.

## Setting and scope

The flag is declared at `src/Allegiance.Server/ServerConfig.cs:19`. The exact rig JSON setting is:

```json
"showcase": {
  "techMode": "research",
  "stationKillsDecideDeadline": true
}
```

Merge `stationKillsDecideDeadline` into the existing showcase object; retain its other winning baseline settings. The new boolean defaults to **false**. The existing source-generated `ServerJsonContext` reads and writes it; absent objects/properties preserve false, and nonboolean values fail JSON validation. No new environment variable or wire format.

`src/Allegiance.Server/Server.Lifecycle.cs:1370` stamps `Mission.IsShowcasePreset` for research showcase, configured goal-rotation showcase, and offline `GameConfig.SkirmishPreset` worlds. `SkirmishOptions.CreateConfig` sets the last marker, including unlocked-tech skirmishes, and forwards its default-off `StationKillsDecideDeadline` option. A normal dedicated game's default config remains ordinary even with the new flag true. Lobby-created worlds and `RequireLanListeners` hosts are excluded; training never uses this criterion. The marker is separate from the rule switch so D displays with A disabled.

Both production deadline callers pass the switch to `Mission.EvaluateCountdown`: the bounded research finale (`src/Allegiance.Server/Server.Showcase.cs:38`) and the real match countdown (`src/Allegiance.Server/Server.Periodic.cs:47`). The mission method also requires preset scope. Generic callers omit the new optional argument and retain native ranking. Goal rotation continues using its configured native goals and never arms the synthetic research finale; its actual countdown can use A if configured.

The new `station-kills` criterion (`src/Allegiance.Core/Model/Mission.Countdown.cs:61`) follows conquest, territory, prosperity, artifacts and flags, before ship kills/deaths. It reuses `UniqueMaximum` across **all** sides. A nonzero unique maximum wins; zero or a tie falls through to native ship kills, then fewer deaths, then draw. The first reason line and `[finale] deadline winner ... by=station-kills values=0:2,1:0` identify the criterion. `CountdownValues` handles that new name; all existing criterion strings and values are unchanged.

Native source rechecked locally: `FedSrv/fsmission.cpp:2339-2342` (strict clock expiry), `2348-2466` (goals), `2468-2491` (kills/deaths), `2494-2503` (reason/draw). A is an approved design extension to that order, not a native behavior claim.

## What counts

The optional `SkillTelemetry.StationKills` is insufficient: its write in `Station.ReceiveDamage` is under the ship-launcher branch and `Mission.Skills` can be null. There is also an existing native `Side.BaseKills` counter (`src/Allegiance.Server/Server.Combat.cs:994`), but it depends on the host station-death callback; the direct ordnance path routes that callback only through a ship launcher with a mission (`src/Allegiance.Core/Model/Sector.cs:2634`). It therefore cannot authoritatively cover every sim destruction. That native score path stays intact.

The new mission-owned integer ledger lives in `src/Allegiance.Core/Model/Mission.StationKills.cs:10` (credit policy at `:21`) and is updated at the lethal damage transition in `src/Allegiance.Core/Sim/Station.cs:594`, before host callbacks. Direct rounds, missile impact/splash, ship collisions and blasts converge on that transition. Each station instance has a one-time latch; repeat damage and the server's station-death callback cannot double count. Repair/re-killing the same retired instance cannot count twice either.

* A real hostile-side launcher earns one station kill for **any destroyed station type**, including garrisons. Capture and partial hull damage earn none. Friendly/allied demolition earns no competitive credit.
* Ships, probes, mines and station wrappers resolve through `EquipmentTarget.SideOf`; missiles resolve through their retained launcher reference. A ship removed from the live registry still provides its side through that reference; membership in the attacker's registry and telemetry are irrelevant. Tests fly a real missile from a retired launcher into a station with both direct power and splash and assert exactly one kill.
* A genuinely null/side-less launcher, an ownerless missile, or friendly destruction increments `UncreditedStationDestructions`. Thus launcherless collapse is visible, without inventing a winning side. A station removed for capture, reset or administrative cleanup without lethal damage is not a destruction.
* Ownership is the victim station's mission. Neither the host's native `Side.BaseKills` callback nor optional telemetry supplies or changes this ledger. Ordinary missions retain their existing base-kill scoring/wire behavior.
* Rotation constructs a new mission and fresh station instances, clearing both counts. The final summary and packet are frozen before retirement; clients retain the old tally in the result while the next match starts at zero.

Native damage boundary: `Igc/stationIGC.cpp:184-194`; native launcher-side score: `FedSrv/FedSrv.CPP:12245-12273`. P914 deliberately narrows competitive credit to hostile destruction, independently of the unchanged native score path.

## Feed and score overlay

For presets, every whole-mission result (`src/Allegiance.Server/Server.Lobby.cs:552`) sends two existing `CHATMESSAGE` notices, publishes the same lines on the existing `Notable` spectator feed and logs them with `[finale]`, and appends them to the existing `GAME_OVER` reason string:

```text
Station kills: Alpha=2, Bravo=0
Enemy base shattered: Alpha (2); uncredited=0
```

`Alpha (2)` names the side that destroyed two enemy stations. The first reason line and winner ID remain authoritative; D never chooses or changes them. Zero counts still display. Chat history follows the existing per-match reset; the spectator/log feed and frozen score overlay retain the final result. Ordinary worlds get neither extra notice nor extra reason text.

The preset encoder (`src/Allegiance.Server/Server.Lobby.cs:1662`) uses the existing `SideEndgameInfo.CBaseKills` field to carry the same count into the existing **BK** side-table column. The signed 16-bit field saturates at 32767; the textual summary retains the full integer tally. No codec/layout/message ID changed. Native already sends this field and a variable reason (`FedSrv/fsmission.cpp:3063-3077`).

`src/Allegiance.ClientCore.Game/Rendering/GameOverPanelInfo.cs:84` preserves reason line breaks while sanitizing each line, and the shared `src/Allegiance.ClientCore.Game/Rendering/GameOverScreenRenderer.cs:136` renders the lines separately for Classic and Modern. No native MDL coordinates, group order, StringImage anchoring or y-axis conventions changed.

Real `Game1` + loopback-server proof: `tools/p914-ui-proof`. It destroys two runtime stations through `Sector.ResolveExplosion`, then allows the real periodic deadline to expire with ship kills 901–978. It checks the received winner/tally before capture; it does not seed the result renderer. Both captures were opened and visually inspected: Alpha wins by station-kills, both summary lines are legible, BK is 2 versus 0, and K remains 901 versus 978; the Back button is visible.

* [Classic](P914_assets/deadline-classic.png)
* [Modern](P914_assets/deadline-modern.png)

The proof waits for result transition/font warming before the final capture. These new media files remain local for Domingo's review before any later push.

## Verification

The solution builds with **0 warnings / 0 errors**; all **108** tool projects build, and the server's Release linux-x64 Native AOT publish completes with no IL2026/IL3050 or other warnings. Three filtered repetitions with server GC pass: **60 Core cases ×3**, **5 ClientCore cases ×3**, and **20 existing HUD model cases ×3**, all with **zero skips**. These include all **26 new P914 cases** (21 Core + 5 ClientCore), the existing native deadline/goal-server/result-delivery/damage tests, source-generated config-default guards, and shared game-over presentation models.

Exactly **one Fast sweep** ran: **12,904 passed / 8 failed / 3 skipped**, **12,915 executions in 532.5 s**. All **nine infrastructure checks** passed; all **26 P914 cases** passed. Two unrelated executions exceeded the ten-second timing limit. The skipped count is explicitly **3**, all optional private-pack checks; artwork was available. The sweep remains **FAIL**, not silently relabeled green. All nine failed/slow methods were then run **three times alone**, in fresh processes with the same lane environment: **36 case executions, 14 passed / 22 failed / 0 skipped**. The persistent cases are unrelated to station accounting, deadlines or result presentation:

| Method / family | Three isolated repetitions | Finding |
|---|---|---|
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` | Artwork fails 3/3; SkyPack passes 3/3 | The fixture creates its supposedly external install under `Path.GetTempPath()`. Required scratch lives inside this checkout, so the resolver finds the real repository configuration. |
| `PlayerBundlePathsTests.MissingDirectoryHasNoDefault` | Artwork and NativeSource fail 3/3; SkyPack passes 3/3 | Same temp-directory ancestry conflict. |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | fails 3/3 | Same fixture ancestry conflict; the real checkout wins before the fixture's working-directory config. |
| `PlayerBundlePathsTests.NativeSourceNeverGuessesAnInstallSubdirectory` | fails 3/3 | Same fixture ancestry conflict. No resolver code changed. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fails 3/3 at the default config path; **passes 3/3** with scratch `XDG_CONFIG_HOME` | The sandbox makes the existing user config read-only. No profile code changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fails 3/3, 1.174 / 1.216 / 1.144 s | Existing null `DefendTarget` at line 179, already ledgered in P913/P908/P907/P906/P904. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | pass / fail / pass, 0.580 / 0.580 / 0.571 s | Existing stale dock-status history: station 1051 versus -1; same P913 signature. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | passes 3/3, 3.209 / 3.228 / 3.297 s | Sweep timing only: 10.319 s. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | passes 3/3, 9.539 / 9.979 / 9.918 s | Sweep timing only: 10.464 s; near the calibrated ten-second limit. |

These outcomes are also entered in `docs/TEST_FLAKES.md`. No unrelated production code or fixture was changed to force a green tier.

The required **five serial never-parks runs passed 5/5**, zero skips. All reached 24/30 ships, observed real hits, and passed the fixture's zero simulation-stage-failure assertion:

| Run | Wall seconds | Hits | Longest stopped with target (s) |
|---|---:|---:|---:|
| 1 | 244.063 | 656 | 1.003 |
| 2 | 481.974 | 965 | 1.905 |
| 3 | 252.780 | 1 | 0.000 |
| 4 | 197.342 | 158 | 1.819 |
| 5 | 227.874 | 218 | 0.301 |

This matches P913's saved **5/5** baseline pass rate; the local maximum stopped interval is **1.905 s**, versus its **1.913 s**. This is a regression check, not a performance improvement claim. Boot logs retain the approved snapshot/encode-once, parallel native broadcast, pool transport and native GotoPlan settings; tests explicitly use server GC.

[Compact validation receipts](P914_assets/validation.json) include exact per-case isolation outcomes, timing and starting load averages. [Raw logs and TRX](../../.scratch/p914/) remain local.

A final spectator-feed `Notable` notification and its targeted assertion were added after Fast discovery; the final **60 Core ×3** and **5 ClientCore ×3** repetitions and clean solution/AOT builds cover that addition. Exactly one whole Fast sweep was run.

Raw logs, TRX receipts and reproduction scripts are under `.scratch/p914/` in this worktree (sandbox-writable evidence location). Temporary builds, caches and GUI profiles were removed at handoff. `local.env` was present and sourced; artwork tests actually ran. Scratch temp files and NuGet HTTP cache use that directory because the default cache is read-only in this lane.

## Claude's rig checks / limitations

Enable only the JSON boolean above on the research showcase, retaining immutable snapshots, encode-once, parallel native broadcast, pool transport, native GotoPlan, P863 clash budget and server GC. Reproduce a b59-like deadline: lower ship kills plus a unique station-kill lead must win by `station-kills`; goal criteria must still outrank it. Compare the same seed/roster with the flag false, verify kills/deaths ranking, and check both feed notices and both client skins. Check the next rotated match starts with zero counts. Observe station death/game-over tick and network cost against the approved baseline; no performance benefit or rig A/B is claimed locally.

No remote measurements, rig edits, or skirmish setup-menu toggle were added. Skirmish's programmatic preset option is available and remains false by default; the display is automatic. Existing clients already understand the packets and BK field; rendering multiple reason lines requires the updated client. Long detail text retains the existing per-line clipping at narrow window widths; the BK table carries each side's tally independently.

## Native intent footer

**Design:** native deadline criteria reward configured objectives first and use ship kills/deaths as the universal fallback. P914 preserves that default; station demolition ahead of kills is the approved showcase/skirmish presentation policy.

**Era constraint:** the original fixed side record uses signed 16-bit counters (`Inc/Messages.h:453-465`) and a single variable reason string (`FedSrv/fsmission.cpp:3063-3064`). The current format already carries the required data, so this change reuses it.

**Bug:** relying on optional ship-only telemetry would omit valid destruction and couple winner selection to observability. A mission-owned lethal-transition ledger fixes that prospective error without rewriting native scoring.

**Modern better approach:** a versioned typed result-details payload could distinguish hostile, friendly and environmental destruction, support localization/wrapping, and expose full-width counters. That broader protocol migration is unnecessary for this task.
