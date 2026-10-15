# P629 — native crowded-sector and boundary penalties

Classification: **design**. Changes remain in the working tree; no commit or push.

## Result and configuration

Sector crowding now uses the loaded core's constants, damages player-class ships
through `Ship.ReceiveDamage(0, ...)`, and sets the native warning bit. The existing
P650 boundary hull-damage implementation now sets its warning bit too. Both reach
the owning client, appear as adjacent HUD indicators beneath the gauges, play
their native voice on a rising edge, and share the native boundary-alarm loop.

The actual JSON setting is:

```json
"sim": { "crowdingPenalty": "native" }
```

`native` is the default, including an absent section or `"sim": {}`. Use `off`
for crowded stress tests and showcases. Boot logs the resolved mode; invalid
values fail before artwork loading or transport binding. Each assembled mission,
including a rotation/restart, inherits the setting. `off` suppresses crowding cost,
crowding damage and its warning; **boundary damage and its warning remain active**.
The server example includes the setting. A source-generated JSON default-loss bug
was caught by the new config test and fixed by using settable defaulted properties.

## Native rules and citations

References below are to `~/Development/Allegiance/src/`, inspected locally.

| Native rule | Native reference | Port |
|---|---|---|
| Start with loaded base cost; accumulate in ship-list order, drone before parent before lifepod before other player | `Igc/clusterIGC.cpp:163-193` | `Sector.Update`, slots 37, 35, 34, 33, 36. Docked ships are excluded because native cluster ships exclude docked ships. |
| Multiply only a positive total by cluster dt / loaded divisor | `Igc/clusterIGC.cpp:195-198` | Slot 38; no second dt multiplication in the ship damage call. Negative costs remain unscaled. |
| Reset cost outside the started stage | `Igc/clusterIGC.cpp:103-104,229-231` | Stage gate independent of the later native `activeF` collision gate. The stress override also resets cost to zero. |
| Reset warnings each advancing root-ship update; boarded occupants do not take this damage | `Igc/shipIGC.cpp:271-275` | `Ship.WarningMask`; parent/docked guards. Turrets still contribute their native cost. |
| For pilot type >= player, set crowding warning and apply collision damage = cost × (effective hull maximum + installed shield maximum) | `Igc/shipIGC.cpp:408-423` | `Ship.OnUpdate`, including side global attributes and installed shield capacity. Existing damage, kill-credit, score and ejection paths receive the event. |
| Lens-adjusted boundary r² = x² + y² + z²/lens²; boundary² = loaded OOB × radius²; excess damage is dt × (r² − boundary²)/(50 × boundary²) | `Igc/shipIGC.cpp:368-390` | Retains P650's loaded slots 0/1/2 and adds `OutOfBounds`. Synthetic cores without valid spatial constants retain their existing disabled-boundary behavior. |
| Boundary damage subtracts hull fraction directly, bypassing shields/armor, with damage/death callbacks | `Igc/shipIGC.cpp:393-405` | Existing P650 path retained and verified, including a fully charged shield test. |
| Warning bits 0x01 / 0x02 | `Igc/igc.h:635-637` | `ShipWarningMask.OutOfBounds` / `CrowdedSector`. |
| Boundary and crowding voices fire on their respective rising bits | `WinTrek/WinTrek.cpp:8654-8670` | `ShipWarningState` accumulates pending edges across receive updates; the render frame drains them once. |
| Shared alarm loop while either bit is active | `WinTrek/trekigc.cpp:1285-1286` | `SoundManager.UpdateShipWarnings`, also called outside flight so dock/disconnect cannot strand the loop. |

Native computes the warnings inside its client simulation and does not put them
in `SINGLE_SHIP_UPDATE`. This renderer client lacks the full local population
simulation. Owner-only extension `SHIP_WARNING_UPDATE(218)` therefore delivers
the authoritative ship id and mask in 8 bytes, refreshed each tick. Existing
native message layouts are untouched. Other ship ids are ignored; sector changes,
ship deletion, disconnect and mission rotation clear the client warning state.
Docked, boarded and dead owners receive a cleared mask.

The shipped waves are copied from the artwork corpus, with MGCB entries:
`boundsalert.ogg`, `vo_sal_boundryexceeded.ogg`, `vo_sal_sectorcrowded.ogg`.
Their native mappings are `sounddef.mdl:345,803-804`.

## Loaded constants, threshold and 195v195 rate

PCore001 actually loads the following values, rather than the C++ initialization
defaults (`Igc/missionigc.cpp:2728-2733,2779-2784`):

| Slot | Meaning | Loaded value |
|---:|---|---:|
| 33 / 34 / 35 | Lifepod / turret / drone cost | 0.5 each |
| 36 | Other player cost | 1 |
| 37 | Base cluster cost | **−72** |
| 38 | Cluster divisor | 400 |
| 0 / 1 / 2 | Lens / universe radius / OOB factor | 0.2 / 5000 / **25** |

For player ships alone, **72 is safe; the 73rd starts damage**. Mixed fleets cross
the threshold when `players + 0.5 × (pods + turrets + drones) > 72`. Cost classes
are exclusive, with the precedence in the native table above. All-drone sectors
contribute cost but crowding damages only player-class ships.

At **195v195 = 390 full player ships**, the excess is 318 and the damage rate is
`318 / 400 = 0.795 × (maximum hull HP + maximum shield strength) per simulation
second`, per ship. At 20 Hz the cost is 0.03975 per tick, i.e. 3.975% of that
maximum pool per tick. With the loaded collision multipliers of 1 for ship
defenses and ignoring regeneration/population changes, a full pool lasts about
**1.258 simulation seconds** (26 ticks at 20 Hz). Shield down-threshold leakage,
regeneration, ejections, departures and tick ordering affect the exact lifetime.
If all 390 become pods and remain, their half weights reduce the rate to
`(195 − 72) / 400 = 0.3075` of maximum pod hull per simulation second, still fatal
in about 3.25 seconds for fresh pods. Server time dilation stretches wall time,
not these simulation-time rates.

An independent scan of the available 100 core constant records found 80 with base
−72, 19 with base −36 and OOB 25/30/35 across the corpus, and one base −50 family
(the full distribution is 80 at −72/OOB25; 17 at −36/OOB25; one each at
−36/OOB35, −36/OOB30, −50/OOB25). Thus other loaded cores can start damage at
37 or 51 plain players; neither the threshold nor the OOB factor is hardcoded.
PCore001's boundary has XY semiaxes 25,000 and Z semiaxis 5,000. At twice a
boundary semiaxis, OOB removes 6% of full hull per simulation second.

## Local live and real-window evidence

The TCP live test places 390 player-class ships in a single local sector,
including one real owning `ConnectionSession`. In `native`, the owner takes
damage, receives the warning and enters a living lifepod through the normal
server ejection path; environmental damage awards no spurious side kill. In
`off`, the owner remains intact with the 390-ship population for 100 additional
ticks (5 simulation seconds).
The native ejection was observed around 1.3 seconds after the fleet was placed.
The other 389 ships are domain models, not 389 network connections; this proves
the local scale rule and owner ejection, not rig networking performance.

A separate live TCP test moves the owner beyond the loaded boundary with
crowding off: hull falls, the OOB bit reaches the client, its rising edge is
available for the voice, and returning inside clears the loop state.

The mandatory bot check runs **production MCP BotManager** with crowding off,
74 stationary player models already at Alderan (1052), and a traveling bot from
each side. **2/2 bots reached the target**, making 76 ships there, with zero
crowding cost. This passed three exact-method isolated repetitions.

`tools/p629-proof` opens a real DesktopGL window using a real server/TCP session,
production `HudRenderer` and production `SoundManager`. The 75-player sector
first triggers crowding; the owner then moves beyond the loaded boundary;
finally it returns inside and the background population is removed. The runtime
OpenAL loop state was **Playing → Playing → Stopped**, with audio loaded in all
three phases. Hull fell from approximately 1.0 to 0.25. The actual window was
captured through X11, rather than an offscreen render target:

- [Crowding warning](p629/phase-0.png)
- [Both adjacent HUD warnings](p629/phase-1.png)
- [Warnings cleared](p629/phase-2.png)
- [Recorded output audio](p629/live-warning-audio.ogg) (crowding voice, boundary voice and alarm)
- [Runtime window evidence](p629/window-proof.txt)

Reproduce from the repository root after building:

```sh
dotnet run --project tools/p629-proof --no-build -- src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content
```

The proof uses the configured external artwork path. No rig/221/200v200 run was
performed. P621 collision rules and bot behavior are unchanged; future P622 work
may teach bots to avoid crowded sectors.

## Verification

Failing-first: the 73-player threshold case failed against the original tree
(`expected hull 0.99975, actual 1`), while the 71/72-player cases passed. Evidence:
[failing-first output](p629/failing-first.txt) (raw log: `/tmp/p629-red.log`). Final solution build
`dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings, 0 errors**,
including the new proof tool.

All **31 new test cases** passed three repetitions by exact method filter,
including each theory's data cases and all live tests. The 15 affected existing
test classes also passed three class-isolated repetitions: P650 simulation,
P651 equipment, sector membership/ghost membership, serial/parallel broadcast
identity, client reset/eject/death/rotation/sector cues/new-contact cues, lifepod
HUD, HUD formatting and weapon readiness. [Isolation ledger](p629/isolation-ledger.json)
contains the 93 valid runs and per-run counters. No isolation failed.

Full suites were run sequentially with `dotnet test tests/<assembly> --no-build`.

| Assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Core | 4208 | 2 | 0 |
| ClientCore | 886 | 0 | 0 |
| ClientCore.MonoGame | 1974 | 0 | 3 |

The three skips are the existing optional Meshy content-pack checks.
[Suite counters and failure names](p629/suite-results.json) preserve the original
outcomes. Raw TRX and logs are under `/tmp/p629-suites/`; focused raw runs are
under `/tmp/p629-isolation/`.

After all three assemblies finished, each Core failure was rerun by exact
method filter three times, alone. **Both passed 3/3**, so they are load flakes
under the brief's rule, not unresolved P629 regressions:

| Test | Full-suite symptom | Exact-method isolation |
|---|---|---|
| `Transport.InboundFloodSafetyLiveTests.Oversized_packet_is_rejected_not_dispatched_others_keep_ticking` | Healthy bot had not yet received an update in the short packet-rejection window | 3/3 pass |
| `LiveWinConditionTests.Board_refused_once_mission_over` | Started-stage control boarding did not complete; already tagged `Lane=LiveTiming` | 3/3 pass |

[Failure rerun ledger](p629/failure-reruns.json); raw logs and TRX are in
`/tmp/p629-failures/`. These observations are also recorded in `docs/TEST_FLAKES.md`.
No tests were retagged and no timing-only product changes were made.

`git diff --check` is clean. No commits or pushes were made.
