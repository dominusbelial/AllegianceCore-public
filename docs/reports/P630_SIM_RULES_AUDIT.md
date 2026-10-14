# P630 — Simulation rules, constants, warnings, feedback and site audit

Date: 2026-10-14. Branch: `codex/analysis`. Audit only. Deliverables: `docs/parity/sim-rules-matrix.csv` (**473 rows**) and this report. P617 itself is unchanged.

The highest-impact findings are explosion target coverage, destination-side warp bombs, out-of-bounds damage, core-driven balance values, active-part signatures, miner arbitration enforcement and damage-driven drone retreat. Crowded-sector damage is still absent in this snapshot and **P629 is already queued**, as stated in `P630_BRIEF.md:17` and `P630_BRIEF.md:55`.

## 1. Source and classification method

Native paths below are relative to `/home/domingo/Development/Allegiance/src/`; port paths are relative to this worktree. The native float IDs are **constants, not an enum**, declared at `Igc/igc.h:59–107`. The layout is `floatConstants[40]` followed by the damage matrix (`Igc/missionigc.h:21–24`); the port decodes all 40 slots (`src/Allegiance.Core/Data/DataConstantsIGC.cs:25,45–46`). Decoding is recorded separately from a gameplay read.

The native working tree has unrelated pre-existing modifications, including `WinTrek/trekapp.cpp`; citations refer to the **files read**, not an assumption of a pristine checkout. Port HEAD: `4c4f47cba07fab6762ec05f2641f354be84eec7b`. Native HEAD: `052f51791e0980519a1d95441e4ac01f5840ab02`. Source fingerprints (SHA-256 of sorted relative path + NUL + each file's SHA-256):

- Native Igc/FedSrv/WinTrek/clintlib `.h`/`.cpp` inputs: `cb2403f66c0459a51c5d7d817db8026e4fa79acc70d963fa38e31f123f989edc`.
- Port `src/**/*.cs` inputs: `c1270155068ef58eeceb497f95b479704a34e007442574c08e825c645d167d18`.
- Existing P617 CSV: `bfa5a6a31afaf57a9e4b9473396f31fed61eae446869b9258b09ed5b8009a646`.

I enumerated declarations and **executable bodies/call expressions**, excluding comments, pure declarations and definitions of the sound methods themselves. Inline header bodies are included. Both `ExecuteShipMove` overloads, `ExecuteTurretMove`, empty type updates and assert-only adapters are accounted for. I read full update bodies, then followed important helper work such as afterburner `IncrementalUpdate`, construction `AsteroidUpdate`, part activation signatures and undock resupply (`Igc/afterburnerIGC.cpp:103`, `Igc/buildingeffectigc.h:313`, `Igc/weaponIGC.h:103`, `Igc/stationIGC.cpp:449`). FedSrv's outer loop, `SendShipUpdates`, `MoveShips`, `UpdateMissions`, `DoTick`, lobby/ballot work and periodic network work are included (`FedSrv/FedSrv.CPP:3662,4238,7942,8157,9361`; `FedSrv/fsmission.cpp:2328,5606,5817`).

Statuses mean:

- **implemented**: the behavior stated in that row has an executable port counterpart. A narrow equation or expiry row does not certify the whole owning subsystem.
- **partial**: an executable counterpart exists, with a traced difference or an explicitly identified untraced portion.
- **missing**: no equivalent producer/consumer was found, or the inspected port body directly omits the rule.
- **n/a**: native no-op/dead code, dispatch plumbing, reserved slot, or an explicit architectural/platform exclusion. Difficulty is not grounds for n/a.

`evidence=traced` means the cited code establishes the stated equation/path/difference. `evidence=inferred` marks negative search conclusions or incompletely traced equivalents, especially UI/audio and optional callback behavior. **200 rows are traced; 273 are inferred.** Missing inferred rows are backlog candidates, not claims of a runtime reproduction. In sound rows, the native statement and nearby source context remain available even when a complete enclosing UI predicate was not reconstructed. This is a family-complete source inventory with conservative classification, not an exhaustive dynamic equivalence proof.

CSV columns include stable review IDs, family, native item/citation/body, rule/condition, status, port citation or reason, explicit production site binding, 1–5 veteran weight, native behavior class, evidence, core slot/read flag, queue state and notes. Rows preserve separate native call sites even when they share a wave. No malformed summary/footer lines are appended to the CSV; footer classifications are in `native_class` and §8.

## 2. Family completeness and counts

| Native family | Native inventory | Classified coverage | Source / accounting |
|---|---|---|---|
| Named float constants | 39 gameplay IDs, slots 0–38 | 39/39 | Igc/igc.h:59–104; every GetFloatConstant reader listed in its CSV row |
| Float layout metadata | 1 unnamed slot 39 + c_fcidMax sentinel | 2/2 supplemental rows | Igc/igc.h:104–107; Igc/missionigc.h:23; DataConstantsIGC.cs:25,45 |
| IGC executable bodies | 31 Update bodies + 4 movement bodies + 1 turret body = 36 | 36/36 represented; body manifest in §10 | Igc files, including inline headers and both ExecuteShipMove overloads |
| FedSrv periodic inventory | 3 extracted fsmission bodies + 8 other outer/helper units | 11/11 represented | §10; includes timer blocks inside OnAppMessage/server loop and SetShipUpdate warp handshake |
| Extra IGC helper units | AsteroidUpdate; two activation helpers grouped in one unit; RepairAndRefuel | 3 supplemental units | Igc/buildingeffectigc.h:313; Igc/weaponIGC.h:103; Igc/afterburnerIGC.h:72; Igc/stationIGC.cpp:449 |
| WarningMask bits | 2 | 2/2 | Igc/igc.h:636–637; setters shipIGC.cpp:386,416 |
| Sound/complaint expressions | 234 executable calls; 7 are forwarding helpers | 234/234, plus 1 dynamic selector row = 235 rows | 117 PlaySoundEffect;29 PlayNotificationSound;36 StartSound;20 PlaySoundIf;5 Complain;8 SendChatf;7 SendChat;12 ReceiveChat |
| Active sal sound references | 47 occurrences / 31 distinct symbols | 47/47 accounted for | 45 inside inventoried calls; remaining2 in clintlib/clintlib.cpp:4006 selector, consumed at :4009 |
| IIgcSite contract | 77 virtual signatures including overloads + SendChatf helper | 78/78 | Igc/igc.h:4757–4924; constructors/factories and overloads remain separate |

The sound inventory has **228 trigger/selector rows after excluding seven forwarding rows**. These are source call sites, not 228 distinct audio assets or independent game features. Empty updates are explicitly classified n/a rather than dropped. `native_body` lets a reviewer recover every body-to-rule subdivision from the CSV.

| Family | rows | implemented | partial | missing | n/a | done% unweighted | done% veteran-weighted |
|---|---|---|---|---|---|---|---|
| constants | 41 | 19 | 13 | 7 | 2 | 48.7% | 33.3% |
| updates | 97 | 21 | 49 | 17 | 10 | 24.1% | 24.9% |
| periodic | 20 | 1 | 11 | 3 | 5 | 6.7% | 6.4% |
| warnings | 2 | 0 | 0 | 2 | 0 | 0.0% | 0.0% |
| sounds | 235 | 14 | 62 | 152 | 7 | 6.1% | 9.4% |
| callbacks | 78 | 22 | 42 | 2 | 12 | 33.3% | 33.3% |
| all matrix surfaces | 473 | 77 | 177 | 183 | 36 | 17.6% | 19.3% |
| tick/periodic rules only | 117 | 22 | 60 | 20 | 15 | 21.6% | 22.7% |

## 3. P617 scorecard extension

This table follows P617's formula (`docs/reports/P617_FEATURE_PARITY_MATRIX.md:40`): done% counts only fully implemented rows over non-n/a rows; weighted done% is the sum of implemented weights divided by the sum of eligible weights. Partial rows receive zero done credit. Weights: 5 for combat/economy/detection outcomes; 4 for frequent pilot/commander decisions; 3 for routine feedback/navigation; 2 for occasional UI/scoring; 1 for platform/transport plumbing. Existing P617 rows retain their original weights.

Three existing P617 CSV rows (physical lines344,350,355: scoreboard, character-info and team-pane) contain unquoted commas in their reasons. For this calculation only, I joined the extra reason fields and used the final numeric field, weight4, preserving each row's status. The original CSV was left unchanged.

The interface subtotal is recalculated from the **current** `docs/parity/parity-matrix.csv`, rather than copying its older report totals. `done` maps to `implemented` and `na` to `n/a` for the extension. The new sim-rules row includes all five requested audit families; updates and periodic work are subdivisions of one family.

| Area | rows | done | partial | missing | na | done% unweighted | done% weighted |
|---|---|---|---|---|---|---|---|
| commands | 203 | 123 | 21 | 46 | 13 | 64.7% | 71.9% |
| fedmsg | 136 | 92 | 33 | 0 | 11 | 73.6% | 74.4% |
| parts | 40 | 31 | 9 | 0 | 0 | 77.5% | 82.4% |
| screens | 27 | 19 | 4 | 1 | 3 | 79.2% | 86.8% |
| commander | 20 | 18 | 1 | 0 | 1 | 94.7% | 96.3% |
| lobby | 12 | 9 | 3 | 0 | 0 | 75.0% | 76.5% |
| P617 interfaces subtotal | 438 | 292 | 71 | 47 | 28 | 71.2% | 76.2% |
| **sim-rules (all five audit families)** | 473 | 77 | 177 | 183 | 36 | 17.6% | 19.3% |
| **Extended TOTAL** | 911 | 369 | 248 | 230 | 64 | 43.6% | 49.3% |

The extended percentage measures audited surfaces. Constants, warning bits, sound call sites and callbacks can describe the same mechanic several times, and the old interfaces also overlap some new rows. It is not a percentage of independent features or a forecast of player satisfaction. The **tick/periodic-only** score is **22/102 = 21.6%**, veteran-weighted **90/396 = 22.7%** (117 rows including 15 n/a). The CSV exposes the denominator so later tasks can update it consistently. Strict native rule parity is substantially weaker than interface availability.

## 4. Constants that the port never reads

**22/39 named slots have an executable port read; 17/39 do not.** Of the 22 read slots, 19 constant rows are implemented and three are partial: lens/radius have other missing consumers, and mount rate is read for only some parts. Point-score reads are real loaded-core reads (`src/Allegiance.Server/PlayerScore.cs:368–380`; finalization receives the core array at `src/Allegiance.Server/Server.Lobby.cs:773`). This does not certify all upstream score counters.

The strongest general finding is that constructor defaults were mistaken for runtime overrides. Native initializes defaults in `CstaticIGC::CstaticIGC` (`Igc/missionigc.cpp:2734–2784`), then core import calls `SetConstants` (`Igc/missionigc.cpp:3281–3283`), which **memcpy replaces the entire constants table** (`Igc/missionigc.h:171–173`). Native `GetFloatConstant` reads that table (`Igc/missionigc.h:133–138`). Port comments saying core values are necessarily overridden do not establish equivalence; e.g. `src/Allegiance.Core/Model/Mission.cs:294–300`, `src/Allegiance.Server/ServerHost.cs:975–986`, `src/Allegiance.Core/Sim/Ship.cs:4502`.

| Row | slot | constant | status | Port evidence | Rule implication |
|---|---|---|---|---|---|
| SIM-0003 | 2 | c_fcidOutOfBounds | missing | src/Allegiance.Core/Sim/Ship.cs:7207; src/Allegiance.Core/Model/Sector.cs:654 | No fc[2] consumer or per-tick ellipsoidal out-of-bounds damage |
| SIM-0004 | 3 | c_fcidExitWarpSpeed | partial | src/Allegiance.Server/ServerHost.cs:986; src/Allegiance.Server/ServerHost.cs:1061 | Entry-speed addition exists but hardcodes 15 instead of loaded slot 3 |
| SIM-0005 | 4 | c_fcidExitStationSpeed | partial | src/Allegiance.Server/Server.cs:569; src/Allegiance.Server/Server.Docking.cs:1839 | Station/carrier/ripcord launch uses fixed 50 instead of loaded slot 4 |
| SIM-0006 | 5 | c_fcidDownedShield | partial | src/Allegiance.Core/Sim/Ship.cs:4502; src/Allegiance.Core/Model/Sector.cs:1570 | Downed-shield absorption/capture use fixed 0.1; no loaded slot 5 |
| SIM-0007 | 6 | c_fcidSecondsBetweenPaydays | partial | src/Allegiance.Core/Model/Mission.cs:300; src/Allegiance.Core/Model/Mission.cs:645 | Payday exists but interval fixed at 60; native imports custom core values |
| SIM-0012 | 11 | c_fcidStartingMoney | partial | src/Allegiance.Core/Model/Mission.cs:321; src/Allegiance.Core/Model/Mission.cs:726 | Starting treasury seed exists but base 16000 and multiplier fixed |
| SIM-0013 | 12 | c_fcidWinTheGameMoney | partial | src/Allegiance.Core/Model/Bucket.cs:542; src/Allegiance.Core/Model/Mission.cs:1070 | Victory sees completed team-money bucket, but bucket price uses dev.Price instead of fGoalTeamMoney*slot12 |
| SIM-0014 | 13 | c_fcidRipcordTime | partial | src/Allegiance.Core/Sim/Ship.cs:1234 | Normal hull ripcord-speed field consumed; native slot13 reader belongs to generated custom hull path, not normal countdown multiplication |
| SIM-0015 | 14 | c_fcidHe3Regeneration | partial | src/Allegiance.Core/Model/Sector.cs:1584; src/Allegiance.Core/Model/Sector.cs:3916; src/Allegiance.Core/Sim/Asteroid.cs:191 | Regrowth equation exists, coefficient fixed 0.025 instead of slot14 |
| SIM-0031 | 30 | c_fcidIncome | partial | src/Allegiance.Core/Model/Mission.cs:310; src/Allegiance.Core/Model/Mission.cs:650 | Civilization and station income exist; baseline fixed 600 instead of slot30 |
| SIM-0033 | 32 | c_fcidWarpBombDelay | partial | src/Allegiance.Core/Model/Sector.cs:1476 | Warp-bomb scheduler exists; fixed 15-second delay instead of slot32 |
| SIM-0034 | 33 | c_fcidLifepodCost | missing | src/Allegiance.Core/Model/Sector.cs:654; src/Allegiance.Core/Sim/Ship.cs:7207 | Crowding cost family never read; neither sector cost nor crowding damage exists; P629 already queued |
| SIM-0035 | 34 | c_fcidTurretCost | missing | src/Allegiance.Core/Model/Sector.cs:654; src/Allegiance.Core/Sim/Ship.cs:7207 | Crowding cost family never read; neither sector cost nor crowding damage exists; P629 already queued |
| SIM-0036 | 35 | c_fcidDroneCost | missing | src/Allegiance.Core/Model/Sector.cs:654; src/Allegiance.Core/Sim/Ship.cs:7207 | Crowding cost family never read; neither sector cost nor crowding damage exists; P629 already queued |
| SIM-0037 | 36 | c_fcidPlayerCost | missing | src/Allegiance.Core/Model/Sector.cs:654; src/Allegiance.Core/Sim/Ship.cs:7207 | Crowding cost family never read; neither sector cost nor crowding damage exists; P629 already queued |
| SIM-0038 | 37 | c_fcidBaseClusterCost | missing | src/Allegiance.Core/Model/Sector.cs:654; src/Allegiance.Core/Sim/Ship.cs:7207 | Crowding cost family never read; neither sector cost nor crowding damage exists; P629 already queued |
| SIM-0039 | 38 | c_fcidClusterDivisor | missing | src/Allegiance.Core/Model/Sector.cs:654; src/Allegiance.Core/Sim/Ship.cs:7207 | Crowding cost family never read; neither sector cost nor crowding damage exists; P629 already queued |

Slot 13 deserves a qualification: native's slot read multiplies **generated custom hull** ripcord speed (`FedSrv/FedSrv.CPP:1676`); ordinary ship countdowns use the hull's ripcord-speed field divided by side GA (`Igc/shipIGC.cpp:4664–4666`). The port already follows the latter (`src/Allegiance.Core/Sim/Ship.cs:1234`). Do not multiply every normal hull countdown by slot13 as a proposed fix.

For He3, the native default is `0.01/60` (`Igc/missionigc.cpp:2702`), whereas the port uses `0.025` (`src/Allegiance.Core/Model/Sector.cs:1584`). The latter is **150 times the native default coefficient**. This compares source defaults, not the actual value of a live loaded core. The underlying deficit-proportional equation is present (`Igc/asteroidigc.h:52–55`; `src/Allegiance.Core/Sim/Asteroid.cs:191–196`). Reading slot14 is the required parity step.

## 5. Traced gameplay gaps and trigger findings

### Combat and flight

- **Explosion semantics:** native applies radius-adjusted quadratic falloff to ships, asteroids, stations, missiles and probes, and separately to mines (`Igc/clusterIGC.cpp:372–455`). The port's resolver processes **ships and mines only**, excludes the ship launcher/same-side ships, and measures mines from their centers (`src/Allegiance.Core/Model/Sector.cs:3242–3297`). Native also extrapolates target position to explosion time and sets NoWarn/NoDebris flags (`Igc/clusterIGC.cpp:417–433`); those behaviors are absent from this resolver. This changes splash damage, repair and mine-clearing behavior.
- **Warp bombs:** native always creates the destination explosion and creates the source explosion only for `c_eabmWarpBombDual` (`Igc/warpIGC.h:69–90`). Port schedules `wpos` only in the source sector, with no partner-sector schedule or dual test (`src/Allegiance.Core/Model/Sector.cs:3523–3524,3578–3613`). Its delay is fixed15s (`:1476`) instead of loaded slot32 (`FedSrv/FedSrv.CPP:12551`).
- **Boundaries:** native subtracts hull fraction using the lens-adjusted radius, raises damage/death and sets OutOfBounds (`Igc/shipIGC.cpp:369–403`). The inspected port ship update has no equivalent (`src/Allegiance.Core/Sim/Ship.cs:7207–7479`). Its existing lens/radius read serves flag respawn (`src/Allegiance.Server/Server.Combat.cs:1897–1906`), which is a different consumer.
- **Crowding, already queued P629:** sector cost and per-player damage are absent (`Igc/clusterIGC.cpp:162–198`, `Igc/shipIGC.cpp:406–425`; port `src/Allegiance.Core/Model/Sector.cs:654–798`, `src/Allegiance.Core/Sim/Ship.cs:7207`). The queue applies to the whole cost/damage/warning/voice family, not a new duplicate task.
- **Missile firing:** native fires on the fire bit with the current lock, scales by maxLock, resets lock and arms loadTime (`Igc/magazineigc.cpp:105–118`). Port requires full lock for nonzero-maxLock missiles and has no load-time gate in `TryFireMissile` (`src/Allegiance.Core/Sim/Ship.cs:3783–3797`). Zero-lock types can therefore use a different firing cadence. Native permits lock against appropriate damageable models or warp-bomb warps (`Igc/magazineigc.cpp:123–130`); port lock calculation requires a Ship target (`src/Allegiance.Core/Sim/Ship.cs:3688`), so this is a concrete target-class gap too.
- **Readiness and firing order:** native fixed guns start at the selected mount in cyclic order, with guns before shield recharge (`Igc/shipIGC.cpp:312–349`). Port walks weapon insertion order after shield update (`src/Allegiance.Core/Sim/Ship.cs:7322,7350–7357`). Native weapon readiness blocks firing (`Igc/weaponIGC.h:34–49`); port's Weapon.Update has no readiness gate (`src/Allegiance.Core/Sim/Weapon.cs:155–169`).
- **Active signatures:** native activation adds weapon/afterburner/shield/magazine/dispenser signature and subtracts it on deactivation (`Igc/weaponIGC.h:109,122`; `Igc/afterburnerIGC.h:78,89`; `Igc/shieldIGC.h:72,76`; `Igc/magazineigc.h:74,78`; `Igc/dispenserigc.h:74`). Port EffectiveSignature includes hull plus active cloak only (`src/Allegiance.Core/Sim/Ship.cs:422–424`); afterburner comments explicitly omit the contribution (`:7098–7118`). Activating equipment can thus change native detection while leaving port detection unchanged.

### Economy, drones and timed world objects

- **Mining arbitration is not enforced at accrual:** native only the least-ore ready friendly contender mines; enemy contention clears mining for all (`Igc/shipIGC.cpp:2092–2203`). Port pointer arbitration exists (`src/Allegiance.Core/Sim/Ship.cs:6812–6912`), but ResolveMining awards ore to any live undocked ship in geometric range and near-zero velocity, without checking miner pilot, mining target, winner or wants-to-mine state (`src/Allegiance.Core/Model/Sector.cs:3930–3967`). Consequently pointer eviction is insufficient to guarantee lockout. This is traced code reasoning; no multi-miner runtime reproduction was executed.
- **Teleport-unload refinery behavior** pays ore near a capable refinery without docking (`Igc/shipIGC.cpp:1972–1994`). Only dock sale was traced in the port (`src/Allegiance.Server/ServerHost.cs:930–960`); the negative conclusion about a teleport-unload producer is inferred from sim/server searches.
- **Damage-driven retreat** has pilot-specific hull/shield thresholds, builder distance exceptions, a repair-station/carrier choice and ten-second reconsideration (`Igc/shipIGC.cpp:1727–1940`). Port NPC autopilot/combat and defend paths are present (`src/Allegiance.Core/Sim/Ship.cs:5031,6551,6663`), but no equivalent running-away state/cooldown producer was found. This absence is inferred. The matrix separates wingman repair, missiles/mines, unseen-target memory and range-band reductions.
- **Construction transforms too late:** native transforms rock to station at the shrink boundary, start+30s, while the remaining fade lasts to40s (`Igc/buildingeffectigc.h:19–22,51–54,313–355`). Port waits for its single40s CompletionTime (`src/Allegiance.Core/Sim/BuildingEffect.cs:61`; `src/Allegiance.Core/Model/Sector.cs:4279–4282`). Its growing translucent station renderer is a reduced visual counterpart (`src/Allegiance.ClientCore.Game/Rendering/BuildingEffectRenderer.cs:8–33,52–53`), not the native phase simulation.
- **Treasure motion** decelerates to a stop analytically and enters the static collision set (`Igc/treasureIGC.cpp:221–262`). Port Treasure.OnUpdate only expires the treasure (`src/Allegiance.Core/Sim/Treasure.cs:150–156`). Ambient treasure accrual/spawning at paydays is missing (`FedSrv/fsmission.cpp:2554–2581`; port `src/Allegiance.Core/Model/Mission.cs:645–656`); neutral-artifact respawn explicitly awaits that generator (`src/Allegiance.Server/Server.Combat.cs:1923–1927`).
- **Station health regeneration** is implemented with armor before shields and side GA (`Igc/stationIGC.h:195–208`; `src/Allegiance.Core/Sim/Station.cs:565–580`). Station resupply on undock is largely implemented, including fuel/ammo/energy refill, but omits native afterburner deactivation (`Igc/stationIGC.cpp:475–477`; `src/Allegiance.Core/Sim/Ship.cs:4364–4410`; `src/Allegiance.Server/Server.Docking.cs:1900`). There is no native passive per-tick fuel refill in the audited ship/afterburner bodies: fuel is consumed in afterburner work and restored through resupply/reload (`Igc/afterburnerIGC.cpp:148–158`; `Igc/shipIGC.h:638–649`; `Igc/stationIGC.cpp:482–488`).

### Warnings and audio

There are **only two `c_wm*` WarningMask bits** (`Igc/igc.h:636–637`). Low fuel/ammo/hull/energy are separate client threshold/attempt sounds, not additional warning bits. Native fires hull/ammo/fuel alerts on crossing15%, energy on crossing5%, and no-ammo/no-fuel on the first failed firing/boost attempt (`WinTrek/trekigc.cpp:1263–1275`). The corresponding threshold/attempt audio paths were not found in the port audio driver; this absence is inferred (`src/Allegiance.ClientCore.Game/Game1.Audio.cs:174`; `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:532`). Existing HUD resource readouts do not establish sound coverage.

Boundary/crowding VO use mask rising edges (`WinTrek/WinTrek.cpp:8659–8666`) and share a mask-driven alert loop (`WinTrek/trekigc.cpp:1285–1286`). Neither the mask producer nor this client warning family exists in the inspected port update/audio bodies (`src/Allegiance.Core/Sim/Ship.cs:7207`; `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:532`). Sector threat warnings are a **different implemented family**, driven by `ClusterWarnings` and client cue drains (`src/Allegiance.Core/Sim/ClusterWarnings.cs:179`; `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:1953–1954`).

Native recurring complaints start at30s and multiply their interval by random1.5–2.5 until at least600s, resetting when a target resumes (`Igc/shipIGC.cpp:25,2351`; `Igc/shipIGC.h:1723–1733`). Port stand-by/arbitration paths log rather than send those periodic complaints (`src/Allegiance.Core/Sim/Ship.cs:4898,6855`). Spawn-time miner/layer/constructor requests are sent separately (`src/Allegiance.Server/Server.Economy.cs:807,968–977`). The client voices only recognized station alerts or quickchat entries whose **caption equals the message exactly** (`src/Allegiance.ClientCore.Game/Game1.Session.cs:1066–1077`). Therefore server-generated text with a valid native voice ID is not sufficient to establish that its sound plays. The matrix distinguishes absent producers from this consumer restriction.

A definition in `sounddef.mdl` is also insufficient asset evidence. Many missing sounds have neither a local OGG nor a Content.mgcb build entry, despite retaining a definition; e.g. `salShieldsOfflineSound`, boundary/crowding and drone-too-crowded definitions (`src/Allegiance.ClientCore.MonoGame/Content/sounddef.mdl:736,791,803–804`). CSV notes record definition line, wave stem, local OGG presence and MGCB inclusion for literal symbols. By contrast countdown1–10 and affirmative/negative voices are shipped and compiled (`src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb:725–783,1211–1215,2135–2139`). This prevents treating asset acquisition as a trivial wiring change.

### IIgcSite production bindings

Only **12 of the78 native signature/helper entries have a concrete ServerHost override**;55 remain default interface implementations and11 are not declared in the port seam. This is a contract-binding count, not a gameplay completion score. The new `port_site_binding` CSV column records each case and its definition line. The12 direct overrides are asteroid kill (object overload), asteroid mined, ship kill, station damage/kill/dock/capture, warp bomb, warp hit, payday, NPC ripcord request and loadout change (`src/Allegiance.Server/ServerHost.cs:609,816,828,674,642,852,708,580,1034,226,1125,409`). Their server subscriptions are visible at `src/Allegiance.Server/Server.Lifecycle.cs:743–776`.

Several native callbacks have a real **renamed replacement** despite their original interface member remaining a no-op: probe/missile/mine/treasure destruction routes through specific destroyed-event hooks (`src/Allegiance.Server/ServerHost.cs:537,317,476,557`; `src/Allegiance.Server/Server.Lifecycle.cs:754,761,764,765`), and typed bucket completion routes through `BucketCompleted` and economy dispatch (`src/Allegiance.Server/ServerHost.cs:248`; `src/Allegiance.Server/Server.Economy.cs:683,771,923,1007`). Those replacements receive semantic credit; merely declaring the default native-named method does not. ClientCore.Game has no production IIgcSite implementation: feedback is supplied by world-message/state consumers and Game1 audio planners (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:25`; `src/Allegiance.ClientCore.Game/Game1.Feedback.cs:51`). This architectural separation explains both working equivalents and missing sim-originated notifications.

## 6. Ranked candidate delegated tasks

These are task candidates for the coordinator; no agents were started and no implementation was made. Effort: S = a small focused wiring/equation change; M = several connected sim/site/wire/client changes; L = a subsystem with several interacting rule branches. Visibility describes who notices the changed behavior. Ranking prioritizes combat/economy impact and frequency over small UI cues. Row IDs refer to this CSV snapshot.

| Rank | Candidate task / row | Native citations | Effort | Player visibility | Acceptance boundary |
|---|---|---|---|---|---|
| 1 | Restore explosion damage for all native target types and native self/friendly/repair policy (SIM-0066) | Igc/clusterIGC.cpp:372–455 | L | 5 — pilots and base attackers; splash cannot hit several native target classes | Implement explosion-time positions and NoWarn/NoDebris behavior; include mine-radius falloff |
| 2 | Restore destination-side and dual warp-bomb scheduling (SIM-0130) | Igc/warpIGC.h:69–90; FedSrv/FedSrv.CPP:12551 | M | 5 — bomber/aleph defense outcomes | Schedule destination blast always, source only dual; consume loaded delay |
| 3 | Restore ellipsoidal boundary hull damage and client warning edges (SIM-0094) | Igc/shipIGC.cpp:369–403; WinTrek/WinTrek.cpp:8659–8666 | M | 5 — fleeing/out-of-bounds ships and pursuit fairness | Use slots0/1/2 and native direct-hull damage; implement bit/VO/loop |
| 4 | Complete crowded-sector penalty family — **P629 already queued** (SIM-0061) | Igc/clusterIGC.cpp:162–198; Igc/shipIGC.cpp:406–425 | M | 5 — crowded team sectors and fleet fights | Coordinate with existing P629; include six core costs, player-only damage, warning and VO |
| 5 | Enforce miner winner/enemy lockout in the ore-accrual pass (SIM-0106) | Igc/shipIGC.cpp:2092–2203 | M | 5 — commanders see contested rocks continue producing | Use physically-ready miner state; award only authorized winner; honor friendly/allied rules |
| 6 | Restore loaded-core balance constants as one family (SIM-0015) | Igc/missionigc.h:133–138,171–173; Igc/missionigc.cpp:3281–3283 | M | 5 — core authors, miners, custom-core game balance | Audit all17 unused slots; read economy/launch/shield/regen/warp delay; slot13 has generated-hull caveat |
| 7 | Add active-part signature contributions to detection (SIM-0153) | Igc/weaponIGC.h:109,122; Igc/afterburnerIGC.h:78,89; Igc/shieldIGC.h:72,76 | M | 5 — scouts, cloak users, detection range and ambushes | Include shield/magazine/dispenser too; remove contributions at deactivation/unmount |
| 8 | Restore magazine load timers, partial-lock firing and target classes (SIM-0073) | Igc/magazineigc.cpp:85–178 | M | 5 — missile firing cadence and warp-bomb/station targeting | Add loaded/mounted gates; native current-lock fire; permit appropriate native damageable targets |
| 9 | Complete readiness gates for weapon/shield/magazine/dispenser and selected-gun order (SIM-0131) | Igc/weaponIGC.h:34–49; Igc/shieldIGC.cpp:72–88; Igc/shipIGC.cpp:312–349 | M | 5 — cargo swaps and scarce-energy multi-gun ships | Use slot10 across the part family; emit ready text/cue; do not assume presence means readiness |
| 10 | Restore pilot-specific drone retreat and repair recheck (SIM-0098) | Igc/shipIGC.cpp:1727–1940 | L | 5 — miners/constructors survive attacks differently | Thresholds, builder exception, carrier exception, repair target and10s reconsideration |
| 11 | Restore native afterburner power timing and side-GA thrust (SIM-0125) | Igc/shipIGC.cpp:2939–2959; Igc/afterburnerIGC.h:113–116 | M | 5 — boost acceleration and thrust research | Use current-step ramp before thrust, GA-scaled maxThrust; verify scarce fuel/release tails |
| 12 | Restore constructor phase timing and selective old-rock fog removal (SIM-0139) | Igc/buildingeffectigc.h:51–54,313–355; Igc/clusterIGC.cpp:141–157 | M | 4 — base appears ten seconds late and hidden rock information leaks | Transform at30s shrink boundary; retain visual fade; track per-side consumed-rock discovery |
| 13 | Restore non-docking refinery teleport unload (SIM-0103) | Igc/shipIGC.cpp:1972–1994 | M | 4 — miners/refineries and economic throughput | Honor SabmTeleportUnload, radius+100, own-side yield, clear ore and replan |
| 14 | Restore ambient treasure generation and neutral-artifact respawn (SIM-0135) | FedSrv/fsmission.cpp:2554–2581; Igc/missionigc.cpp:4125 | L | 4 — scouts, salvage economy and artifact games | Pending fractional rates, treasure sets, home/neutral policy, artifact drop/respawn |
| 15 | Restore drifting treasure motion and static transition (SIM-0129) | Igc/treasureIGC.cpp:221–262 | M | 4 — players chase dropped parts/flags instead of static loot | Analytical deceleration; correct swept pose/velocity and pickup geometry |
| 16 | Restore native timeout winner ordering and economic victory pricing (SIM-0133) | FedSrv/fsmission.cpp:2335–2497; Igc/missionigc.h:790 | M | 4 — countdown/prosperity games end incorrectly or never end by clock | Conquest/territory/bucket-time/artifact/flag/kill-death precedence; unresolved draw; loaded victory price |
| 17 | Complete probe first-shot reveal, target eligibility and emission timing (SIM-0083) | Igc/probeigc.cpp:388–521 | M | 4 — towers reveal differently and ally/AA combat differs | First-fire scanner/allied reveal; native emissionPt/nextFire and launcher fallback |
| 18 | Restore wingman nan-repair, missile/mine behavior flags and unseen-target memory (SIM-0116) | Igc/shipIGC.cpp:2410–2461,2578–2699 | L | 4 — escorts/support drones and hiding targets | Repair lead/stop-at-full-hull; missile evasion gate; mine policy;3s/5s visibility memory |
| 19 | Complete wingman range bands, defend cooldowns and cross-sector chase (SIM-0114) | Igc/shipIGC.cpp:2374–2407,2483–2595,2707–2800 | L | 4 — AI combat/escort routes and pursuit feel | Preserve temporal gates and aggression flags; native distance/aim/range decisions |
| 20 | Restore recurring drone team complaints and generic native voice-ID consumer (SIM-0110) | Igc/shipIGC.h:1723–1733; Igc/shipIGC.cpp:2199,2308–2331 | M | 4 — commander misses idle/contested/retreat voice requests | Site SendChat producer, native backoff, generic sound-ID mapping; include required absent assets |
| 21 | Restore resource-threshold, dry-fire, shield-offline and cloak feedback family (SIM-0089) | WinTrek/trekigc.cpp:1263–1275; Igc/shieldIGC.cpp:93–94,185–186; Igc/shipIGC.cpp:1608 | M | 4 — low resources and shield/cloak state audible to pilots | Separate crossings from failed-attempt edges; import/compile absent waves; preserve own-source filtering |
| 22 | Wire ripcord digit callouts to existing countdown audio (SIM-0155) | WinTrek/trekigc.cpp:1018–1073 | S | 3 — pilots hear remaining ripcord seconds | Use existing player timer and loaded countdown1..10 assets; quick win Q1 |
| 23 | Restore chat-budget refill and gated chat/money requests (SIM-0145) | FedSrv/FedSrv.CPP:8282–8291; FedSrv/FedSrv.CPP:5370 | M | 3 — comms floods and money-request behavior | Dedicated native chat budget is absent; generic transport intake budgets are a separate mechanism |
| 24 | Complete docking/carrier/reload/cargo/command-accept audio triggers (SIM-0389) | WinTrek/trekigc.cpp:2859–2860,4237; Igc/shipIGC.h:616–647; WinTrek/WinTrek.cpp:10658 | M | 3 — routine flight/hangar actions lack native feedback | Audit both producer and compiled wave availability; several cues need assets, not one call |
| 25 | Resolve expiry boundary and mission/collision scheduling drift (SIM-0074) | Igc/mineigc.cpp:150–156; Igc/missileigc.cpp:354–421; Igc/projectileIGC.cpp:120–144; Igc/clusterIGC.cpp:170–251 | M | 3 — narrow tick boundaries and collision/shot order | Strict > vs >=, next-substep burnout, global preplot/plot/execute and collision-time ordering |

## 7. Quick wins — exact proposed changes, no changes made

These can be delegated separately without treating them as completed rows. Q1 is an audio wiring gap with assets already present. Missing resource/drone/docking wave assets are excluded from the trivial-wiring group. Some small rule corrections below still need focused behavior checks when implemented.

| ID | Gap / effort | Native evidence | Exact proposed change / current port location |
|---|---|---|---|
| Q1 | Ripcord digit voice, S | WinTrek/trekigc.cpp:1018–1073 | In Game1 audio update, derive remaining time from world.RipcordActiveSinceLocalMs and RipcordDurationSeconds(world), the same timer used at src/Allegiance.ClientCore.Game/Game1.Rendering.cs:2455–2467. Track edge of 1+(int)remaining, play _sound.PlayCountdownSecond for1..10 once per changed digit, reset on abort/inactive/reconnect. Existing method: Audio/SoundManager.cs:723; compiled assets Content/Content.mgcb:725–783. Local timer is the current HUD approximation, not authoritative native timeLeft. |
| Q2 | Persist completed afterburner mount fraction, S | Igc/afterburnerIGC.cpp:111–125 | At src/Allegiance.Core/Sim/Ship.cs:7158–7163, when frac>=1, replace the mounted part record with WithFraction(part,255) using the same _parts index replacement as :7167–7169; current completed branch only changes a local float. Ready text/audio remains a separate missing notification task. |
| Q3 | Clear afterburner during station resupply, S | Igc/stationIGC.cpp:475–477 | Inside Ship.RepairAndRefuel at src/Allegiance.Core/Sim/Ship.cs:4364, after shield refill call existing DeactivateAfterburner (:7112) so active power cannot survive resupply; undock invokes this at src/Allegiance.Server/Server.Docking.cs:1900. |
| Q4 | Loaded He3 regeneration coefficient, S | Igc/asteroidigc.h:52–55 | Thread mission.Core.Constants.FloatConstants[14] into Sector.ResolveMining instead of Sector.He3Regeneration=0.025 (src/Allegiance.Core/Model/Sector.cs:1584,3916). Keep Asteroid.Regenerate equation (:191) and use native constructor default only when a usable loaded table is unavailable. |
| Q5 | Selected fixed-gun energy priority, S | Igc/shipIGC.cpp:312–343 | Replace insertion-order fixed-gun iteration at src/Allegiance.Core/Sim/Ship.cs:7350 with native i=maxFixed..1 visiting (i+selectedWeapon)%maxFixed, then turret mounts. Move shield recharge after those weapons to match :346–349. Preserve existing gunner orientation update; check two energy-starved guns with changing selected mount. |

An affirmative/negative automatic acknowledgement is not classified as pure wiring: native addresses the original order sender (`WinTrek/WinTrek.cpp:10596,10615,10705`), but port queued state currently preserves only command/target (`src/Allegiance.ClientCore/State/ClientWorld.cs:2409`). The compiled voices exist, but sender metadata and the directed reply path must be restored before claiming the same trigger.

## 8. Native behavior footer: design / era constraint / bug

The CSV's `native_class` labels the native behavior or implementation artifact. A **design** label describes a game rule; it does not imply a missing port feature was deliberately omitted. Where a source comment explicitly documents a port simplification, the row notes it. Intent is not inferred from absence.

| Native behavior | Native citation | Class | Port treatment / implication |
|---|---|---|---|
| Crowding penalty and ellipsoidal boundary damage | Igc/clusterIGC.cpp:162–198; Igc/shipIGC.cpp:369–425 | design | Missing in this snapshot; gameplay parity task, crowding already P629 |
| Deficit-based ore regrowth and loaded-core tuning | Igc/asteroidigc.h:52–55; Igc/missionigc.h:171–173 | design | Equation present; fixed coefficient differs (Sector.cs:1584,3916) |
| Part activity adds detection signature | Igc/weaponIGC.h:109; Igc/afterburnerIGC.h:78 | design | Missing except cloak in Ship.cs:422 |
| Every-other-frame / 1600bytes/s ship-update budget | FedSrv/FedSrv.CPP:3664–3666,3836 | era constraint | Density-aware managed broadcast at Server.Broadcast.cs:609; no requirement to restore modem cap |
| Fixed500-entry stack arrays for explosion targets | Igc/clusterIGC.cpp:366–386 | era constraint | Managed snapshots replace storage cap (Sector.cs:3244); missing target types are separately gameplay gaps |
| DirectPlay lobby roll call/session object cleanup and Win32 force feedback | FedSrv/FedSrv.CPP:7871,7942–8154; Igc/igc.h:4833,4835 | era constraint | HTTP lobby/TCP and managed UI replace these seams; platform exclusion is explicit |
| Cloak first update forces readiness after ramp increment | Igc/cloakigc.cpp:68–78 | bug | Observed code oddity: unconditionally sets fraction1; port assumes ready (Ship.cs:6971). Do not copy accidentally while implementing general readiness. |
| Intended .75s station update timer reset every outer tick | FedSrv/FedSrv.CPP:8161,8253,8302 | bug | Native implementation can starve station updates at regular sub-.75s cadence; port broadcasts station health by its own cadence (Server.Broadcast.cs:2240). Preserve intended feedback, not starvation. |

The optional xorgy/multiport cross-check was skipped because this workspace has restricted network access. No external-port facts or code are used. All behavior findings above come from the local native and port sources.

## 9. Verification

- CSV round-trips through Python csv.DictReader/DictWriter; all473 IDs are unique, every row has a native citation, a status, a port citation/reason and a weight1–5. All native/port citation paths exist and referenced line numbers are within file bounds.
- Native declaration/body/call inventories reconcile with the matrix:39 named constants,36 IGC bodies,11 FedSrv periodic/helper units,2 warnings,234 call expressions plus the dynamic leave selector, and78 site contract entries. The inventories include commented-code exclusions, both movement overloads and the otherwise easy-to-miss inline asteroid/side/bucket/build-effect/warp/weapon updates.
- Recomputed scorecards use CSV data and exclude only n/a from denominators. Row counts/status totals reconcile in §2; the all-family weighted numerator/denominator is257/1333, tick-only is90/396.
- No build, test suite or runtime proof was run: both requested deliverables are documentation, and the verification here is source tracing plus artifact/inventory validation. No gameplay fixes were attempted.

The required ten implemented-row spot checks read executable port code:

| Row | Native rule | Port code read | Verified behavior |
|---|---|---|---|
| SIM-0008 | c_fcidCapacityHe3 | src/Allegiance.Core/Sim/Ship.cs:923–924 | Loaded slot7 selected, not just decoded |
| SIM-0009 | c_fcidValueHe3 | src/Allegiance.Core/Sim/Ship.cs:927–929 | Loaded slot8 selected; dock sale uses effective yield at ServerHost.cs:951 |
| SIM-0049 | chaff_growth_and_expiry | src/Allegiance.Core/Sim/Chaff.cs:132–149 | Exact dtLeft expiry and max(1,radius*(1-dtLeft/lifespan)) |
| SIM-0057 | cloak_energy_and_strength | src/Allegiance.Core/Sim/Ship.cs:6991–7022 | Drain, insufficient-energy r squared and on/off ramps |
| SIM-0090 | ship_energy_recharge | src/Allegiance.Core/Sim/Ship.cs:7297–7305 | Parent/station gates, recharge addition, GA max ceiling, zero floor |
| SIM-0096 | turret_slew | src/Allegiance.Core/Sim/Ship.cs:4684–4707 | Stick-vector normalization and7.5..75deg/s throttle law |
| SIM-0127 | station_armor_then_shield | src/Allegiance.Core/Sim/Station.cs:565–580 | Armor-first if/else; shield only when armor already full |
| SIM-0126 | side_bucket_tick | src/Allegiance.Core/Model/Side.cs:513–518 | All side buckets Update once per side pass |
| SIM-0077 | mission_stage_and_substeps | src/Allegiance.Core/Model/Mission.cs:602–624,661–691 | Stage/clock gates; n=1+floor(delta/.25) equal substeps |
| SIM-0128 | treasure_expiry | src/Allegiance.Core/Sim/Treasure.cs:150–155 | >= expiry removes treasure and raises destruction event |

These checks certify the precise row scopes, not whole subsystems. In particular Treasure expiry is implemented while Treasure motion is missing; shield regeneration is implemented while shield readiness and activation feedback are missing.

## 10. Body manifest and reproducibility

Each exact native body is listed below with the matrix subdivisions sharing its `native_body` key. All36 extracted IGC bodies are accounted for, including seven empty/assert-only bodies and the generic template forwarder. One row can reference additional helpers in its native citations.

| Native body range | Method | Rule rows | Matrix IDs |
|---|---|---|---|
| Igc/afterburnerIGC.cpp:60–63 | CafterburnerIGC::Update | 2 | SIM-0042, SIM-0154 |
| Igc/asteroidigc.h:47–92 | Update | 3 | SIM-0043, SIM-0044, SIM-0045 |
| Igc/bucketigc.h:40–74 | Update | 2 | SIM-0046, SIM-0047 |
| Igc/buildingeffectigc.h:160–225 | Update | 1 | SIM-0048 |
| Igc/chaffigc.cpp:75–86 | CchaffIGC::Update | 1 | SIM-0049 |
| Igc/chafftypeigc.h:39–41 | Update | 1 | SIM-0050 |
| Igc/cloakigc.cpp:59–123 | CcloakIGC::Update | 2 | SIM-0057, SIM-0058 |
| Igc/clusterIGC.cpp:97–485 | CclusterIGC::Update | 10 | SIM-0059, SIM-0060, SIM-0061, SIM-0062, SIM-0063, SIM-0064, SIM-0065, SIM-0066, SIM-0067, SIM-0068 |
| Igc/dispenserigc.cpp:84–126 | CdispenserIGC::Update | 1 | SIM-0069 |
| Igc/igc.h:2952–2952 | Update | 1 | SIM-0051 |
| Igc/igc.h:5081–5091 | Update | 1 | SIM-0070 |
| Igc/magazineigc.cpp:77–181 | CmagazineIGC::Update | 3 | SIM-0071, SIM-0072, SIM-0073 |
| Igc/mineigc.cpp:150–156 | CmineIGC::Update | 1 | SIM-0074 |
| Igc/minetypeigc.h:35–37 | Update | 1 | SIM-0052 |
| Igc/missileigc.cpp:354–421 | CmissileIGC::Update | 2 | SIM-0075, SIM-0076 |
| Igc/missiletypeigc.h:35–37 | Update | 1 | SIM-0053 |
| Igc/missionigc.cpp:3127–3185 | CmissionIGC::Update | 1 | SIM-0077 |
| Igc/modelIGC.cpp:76–103 | CmodelIGC::Update | 1 | SIM-0078 |
| Igc/modelIGC.h:426–429 | Update | 1 | SIM-0079 |
| Igc/probeigc.cpp:333–533 | CprobeIGC::Update | 6 | SIM-0080, SIM-0081, SIM-0082, SIM-0083, SIM-0084, SIM-0085 |
| Igc/probetypeigc.h:65–67 | Update | 1 | SIM-0054 |
| Igc/projectileIGC.cpp:120–144 | CprojectileIGC::Update | 1 | SIM-0086 |
| Igc/shieldIGC.cpp:59–96 | CshieldIGC::Update | 3 | SIM-0087, SIM-0088, SIM-0089 |
| Igc/shipIGC.cpp:264–431 | CshipIGC::Update | 8 | SIM-0090, SIM-0091, SIM-0092, SIM-0093, SIM-0094, SIM-0095, SIM-0140, SIM-0155 |
| Igc/shipIGC.cpp:1618–1702 | CshipIGC::ExecuteTurretMove | 2 | SIM-0096, SIM-0097 |
| Igc/shipIGC.cpp:1704–2001 | CshipIGC::PreplotShipMove | 6 | SIM-0098, SIM-0099, SIM-0100, SIM-0101, SIM-0102, SIM-0103 |
| Igc/shipIGC.cpp:2059–2839 | CshipIGC::PlotShipMove | 16 | SIM-0104, SIM-0105, SIM-0106, SIM-0107, SIM-0108, SIM-0109, SIM-0110, SIM-0111, SIM-0112, SIM-0113, SIM-0114, SIM-0115, SIM-0116, SIM-0117, SIM-0118, SIM-0119 |
| Igc/shipIGC.cpp:2841–2850 | CshipIGC::ExecuteShipMove | 1 | SIM-0120 |
| Igc/shipIGC.cpp:2852–3054 | CshipIGC::ExecuteShipMove | 5 | SIM-0121, SIM-0122, SIM-0123, SIM-0124, SIM-0125 |
| Igc/shipIGC.cpp:4455–4458 | MyHullType::Update | 1 | SIM-0055 |
| Igc/sideigc.h:41–52 | Update | 1 | SIM-0126 |
| Igc/stationIGC.cpp:517–520 | MyStationType::Update | 1 | SIM-0056 |
| Igc/stationIGC.h:189–213 | Update | 1 | SIM-0127 |
| Igc/treasureIGC.cpp:215–266 | CtreasureIGC::Update | 2 | SIM-0128, SIM-0129 |
| Igc/warpIGC.h:54–93 | Update | 1 | SIM-0130 |
| Igc/weaponIGC.h:30–63 | Update | 2 | SIM-0131, SIM-0132 |
| FedSrv/fsmission.cpp:2328–2593 | CFSMission::DoTick | 4 | SIM-0133, SIM-0134, SIM-0135, SIM-0136 |
| FedSrv/fsmission.cpp:5606–5622 | CFSMission::UpdateLobby | 1 | SIM-0137 |
| FedSrv/fsmission.cpp:5817–5833 | Ballot::Update | 1 | SIM-0138 |

Supplemental helper/outer-timer units:

| Native unit | Rows | Matrix IDs |
|---|---|---|
| FedSrv/FedSrv.CPP:3662 SendShipUpdates | 4 | SIM-0469, SIM-0470, SIM-0471, SIM-0472 |
| FedSrv/FedSrv.CPP:4238 MoveShips | 1 | SIM-0141 |
| FedSrv/FedSrv.CPP:4797 OnAppMessage | 1 | SIM-0149 |
| FedSrv/FedSrv.CPP:7871 CheckForNoShows | 1 | SIM-0146 |
| FedSrv/FedSrv.CPP:7942 ProcessNetworkMessages | 1 | SIM-0147 |
| FedSrv/FedSrv.CPP:8157 UpdateMissions | 4 | SIM-0142, SIM-0143, SIM-0144, SIM-0145 |
| FedSrv/FedSrv.CPP:9340 outer server loop | 1 | SIM-0148 |
| FedSrv/fsship.cpp:1173 CFSPlayer::SetShipUpdate | 1 | SIM-0150 |
| Igc/buildingeffectigc.h:313 AsteroidUpdate | 1 | SIM-0139 |
| Igc/stationIGC.cpp:449 RepairAndRefuel | 1 | SIM-0473 |
| Igc/weaponIGC.h:103 Activate; Igc/afterburnerIGC.h:72 Activate | 1 | SIM-0153 |

To reconstruct the declaration/search boundaries, run read-only searches from the worktree:

```bash
rg -n 'const\s+FloatConstantID\s+c_fcid' ~/Development/Allegiance/src/Igc/igc.h
rg -n 'GetFloatConstant\s*\(' ~/Development/Allegiance/src
rg -n '\b(Update|PreplotShipMove|PlotShipMove|ExecuteShipMove|ExecuteTurretMove)\s*\(' ~/Development/Allegiance/src/Igc
rg -n 'c_wm\w+' ~/Development/Allegiance/src
rg -n 'sal\w*Sound|Complain\(|PlaySoundEffect\(|PlayNotificationSound\(|StartSound\(|PlaySoundIf\(' ~/Development/Allegiance/src/Igc ~/Development/Allegiance/src/WinTrek ~/Development/Allegiance/src/clintlib
rg -n 'FloatConstants|\bfc\[|FloatConst\(' src --glob '*.cs'
```

Raw rg counts include declarations and comments; they must be filtered before comparing with executable-body/call counts. The audit scanner removed comments while preserving line numbers, matched declarations, balanced braces/parentheses around method bodies and call expressions, excluded sound definitions/declarations, and retained separate overload/call-site keys. Every uncovered active sal occurrence was checked: the two remaining occurrences are the allied/enemy leave selector at `clintlib/clintlib.cpp:4006`.

For a repeatable CSV integrity/score check:

```python
import csv
from collections import Counter
rows = list(csv.DictReader(open("docs/parity/sim-rules-matrix.csv")))
assert len(rows) == 473
assert len({r["row_id"] for r in rows}) == len(rows)
assert all(r["native_cite"] and r["port_cite_or_reason"] for r in rows)
assert all(r["port_status"] in {"implemented", "partial", "missing", "n/a"} for r in rows)
assert all(1 <= int(r["weight"]) <= 5 for r in rows)
for area in sorted({r["area"] for r in rows}):
    group = [r for r in rows if r["area"] == area]
    eligible = [r for r in group if r["port_status"] != "n/a"]
    done = [r for r in eligible if r["port_status"] == "implemented"]
    print(area, Counter(r["port_status"] for r in group),
          100 * len(done) / len(eligible),
          100 * sum(int(r["weight"]) for r in done)
              / sum(int(r["weight"]) for r in eligible))
```

No commit or push was made. Source code and the existing P617 report/matrix were not modified. The pre-existing untracked brief and hostile-input audit document were left untouched.
