# SC-1 — showcase skill coverage

SC-1 adds measurement only: 32 catalogue skills, per-match/per-side native-event counters, a bounded console snapshot query, final game-over coverage, and a log-to-table tool. No bot behavior, equipment, balance, or research rules were changed. Changes remain in the working tree; no commit or push was made. Base revision: `3b37af4e`.

The catalogue is [SKILLS.csv](../showcase/SKILLS.csv); operation and counting limits are in [the telemetry guide](../showcase/README.md). All native paths below are relative to `~/Development/Allegiance/src/`. Coverage demonstrates aggregate actions, with the documented server-side proxies for flight/autopilot/scouting; it does not measure player competence or intentional use.

## Local match

The proof runs civ 25 (Iron Coalition) vs civ 3 (Ga'Taraan), research mode, research ×16, income multiplier 5000, SingleRing with four sectors, and 15 combat peers per side, plus research commanders and native economy NPCs. It uses ordinary legal purchases, starts at home, stages in neutral sector 1, and uses the existing research finale. A 600-simulation-second countdown bounds the match, with a 180-second finale deadline after the normal finale trigger. These are smaller/shorter local proof conditions, not the 107v107/45-minute qualification. Minimums are deliberately unscaled, so a low percentage includes exposure/size shortfalls.

Native training coverage includes M2 flight/autopilot (`training/mission2.cpp:748,812`), M3 targeting/guns/missiles (`training/mission3.cpp:269,875,928`), M4 cloak/eyeball/chat/warp (`training/mission4.cpp:236,497,719,832`), M5 build/goto/attack commands (`training/mission5.cpp:323,512,556`), M6 repeated combat/respawn (`training/mission6.cpp:518`), M8 station/ship nanites (`training/mission8.cpp:221,278`; `Igc/shipIGC.cpp:1244`), and M10 support/repair orders (`training/mission10.cpp:706`). The remaining catalogue entries cite native multiplayer player/event rules rather than claiming that every item is taught by those missions.

| Match | Side | State | Covered | Coverage |
|---:|---:|---|---:|---:|
| 1 | 0 | final | 12/32 | 37.5% |
| 1 | 1 | final | 15/32 | 46.9% |

Match 1, side 0 — zero/under minimum: flight 22/50, missiles 23/100, chaff 0/10, booster 11/20, cloak 0/5, probes 0/10, mines 0/5, station-damage 0/20, station-kills 0/1, nanite-ship 0/10, nanite-station 0/10, order-goto 0/5, order-attack 0/5, order-repair 0/2, drone-build 1/2, drone-mine 3/10, eject 8/10, pod-rescue 0/2, dock 3/20, ship-kills 18/20

Match 1, side 1 — zero/under minimum: flight 22/50, missiles 36/100, chaff 0/10, cloak 0/5, probes 0/10, mines 0/5, station-damage 0/20, station-kills 0/1, nanite-ship 0/10, nanite-station 0/10, order-goto 0/5, order-attack 0/5, order-repair 0/2, drone-mine 3/10, pod-rescue 0/2, dock 7/20, ship-kills 12/20

| Skill | Minimum per side | Side 0 | Side 1 |
|---|---:|---:|---:|
| flight | 50 | 22 | 22 |
| warp | 20 | 26 | 55 |
| autopilot | 5 | 7 | 7 |
| targeting | 50 | 191 | 83 |
| guns | 1000 | 5115 | 2744 |
| missiles | 100 | 23 | 36 |
| chaff | 10 | 0 | 0 |
| booster | 20 | 11 | 121 |
| cloak | 5 | 0 | 0 |
| probes | 10 | 0 | 0 |
| scouting | 10 | 205 | 316 |
| mines | 5 | 0 | 0 |
| ship-damage | 100 | 2174 | 1332 |
| station-damage | 20 | 0 | 0 |
| station-kills | 1 | 0 | 0 |
| nanite-ship | 10 | 0 | 0 |
| nanite-station | 10 | 0 | 0 |
| order-goto | 5 | 0 | 0 |
| order-attack | 5 | 0 | 0 |
| order-build | 2 | 20 | 19 |
| order-repair | 2 | 0 | 0 |
| drone-build | 2 | 1 | 2 |
| drone-mine | 10 | 3 | 3 |
| eject | 10 | 8 | 14 |
| pod-rescue | 2 | 0 | 0 |
| dock | 20 | 3 | 7 |
| relaunch | 20 | 23 | 27 |
| purchase | 20 | 21 | 25 |
| research | 5 | 12 | 12 |
| chat | 5 | 35 | 37 |
| voice | 5 | 31 | 30 |
| ship-kills | 20 | 18 | 12 |

The existing research-finale deadline (armed at deadline `303.65` simulation seconds) declared side 1 the winner. It did **not** destroy or damage a station. Each side emitted exactly one final marker and coverage line; periodic snapshots were also captured and the parser selected the finals without adding snapshots together. Structured counts are in [skills.json](SC1_assets/skills.json). The live finale log reports no legal bomber hull passing the side/station tech gate at that point. This run proves instrumentation and exposes gaps; it does not certify all training skills or a native siege win.



The existing P745 siege regression, executed during the single ClientCore sweep, supplies a separate positive live check at its own ×64 research settings: side 0 recorded **3 station-damage hits / 1 station kill / 7 pod rescues**, and side 1 recorded **50 station-damage hits / 7 station kills / 10 pod rescues**. Its native station-elimination result occurred after about 577 wall seconds. These observations confirm the native event counters beyond the short ×16 proof; they are not mixed into its table. See [supplementary P745 counts](SC1_assets/p745-skills.json); raw log `/tmp/allegiance-evidence/SC1/p745-siege-full.log`.

## Gap diagnoses

**flight — side 0 22/50, side 1 22/50.** This is an exposure shortfall: a 15v15 proof cannot establish a floor designed for 107 combat ships per side. The counter records the first moving update per ship identity, including native economy/support entities and pods (`src/Allegiance.Core/Sim/Ship.cs:7784`). Both fleets demonstrably fly; no missing flight rule is inferred. Native reference: `training/mission2.cpp:351`; `Igc/shipIGC.cpp:2939`.

**missiles — side 0 23/100, side 1 36/100.** Bots do fire missiles, but only with a legal equipped magazine, the UseMissiles behavior, adequate aim, full server lock, and ready-time pacing (`tools/allegiance-mcp/BotManager.cs:3894`). The shorter/smaller run and early legal loadouts explain limited exposure; this is not evidence of an absent launch rule. Native reference: `training/mission3.cpp:928`; `Igc/shipIGC.cpp:2584`.

**chaff — side 0 0/10, side 1 0/10.** Every initial fleet group carried Counter 1 ([fleet summary](SC1_assets/fleet-summary.json)), and the server can create real chaff with a verified counter, but the showcase combat loop calls missile and mine helpers without a chaff-defense helper (`tools/allegiance-mcp/BotManager.cs:3156`). Equipping a chaff launcher in `tools/allegiance-mcp/TechLoadoutSelector.cs:91` does not deploy it. This is a missing showcase trigger/policy, not a missing server decoy rule. Native reference: `FedSrv/FedSrv.CPP:5672-5711`.

**booster — side 0 11/20, side 1 121/20.** Both sides activate real afterburner emitters; side 0 falls below the provisional floor. The combat steering law chooses boost based on engagement geometry (`tools/allegiance-mcp/BotManager.cs:3095,4068`), and the counter is on the inactive-to-active transition (`src/Allegiance.Core/Sim/Ship.cs:7648`), not every boosted tick. This is a side-asymmetric trigger/exposure shortfall, not absent equipment simulation. Native reference: `training/mission2.cpp:748`; `Igc/afterburnerIGC.h:72`.

**cloak — side 0 0/5, side 1 0/5.** The server cloak state machine exists (`src/Allegiance.Core/Sim/Ship.cs:7498`), and the focused test proves that a mounted cloak with the control bit activates the counter once while unmounted input does not. None of the 46 observed loadout selections contained a cloak ([loadout summary](SC1_assets/loadout-summary.json)); the showcase loop also builds movement/fire state without setting CloakActive; `LastCloakAt` is only declared/reset (`tools/allegiance-mcp/BotManager.cs:5813`, `BotManager.Lifecycle.cs:94`). Legal cloak selection can exist without a use policy. Native reference: `training/mission4.cpp:719`; `Igc/cloakigc.cpp:80`.

**probes — side 0 0/10, side 1 0/10.** Initial scouts on both sides carried EWS Probe 1 ([fleet summary](SC1_assets/fleet-summary.json)), and the focused test proves probe creation increments, but the showcase dispenser helper is specifically a mine helper (`tools/allegiance-mcp/BotManager.cs:3955`). The combat Scout role does not currently supply an explicit probe-deployment policy. Automatic scanner contacts should not be mistaken for probe use. Native reference: `FedSrv/FedSrv.CPP:13801-13818`; scanner/eyeball training `training/mission4.cpp:832`.

**mines — side 0 0/5, side 1 0/5.** Mine bot logic exists, but it requires an equipped mine dispenser (`tools/allegiance-mcp/BotManager.cs:3955-3977`). Across 46 observed combat loadout selections, the only selected dispensers were Rescue (part 19) and EWS Probe 1 (81); decoding PCore001 confirms both are ProbeLauncher types, with no MineLauncher selected ([loadout summary](SC1_assets/loadout-summary.json)). The equipment gate therefore remained closed in the observed selections; no scenario minefield/layer was requested either. This is a legal loadout/policy coverage gap, not a missing server mine rule; the focused test proves actual mine creation counts. Native reference: `Igc/shipIGC.cpp:2651-2665`.

**station-damage — side 0 0/20, side 1 0/20.** No effective station hit occurred. The final MCP log repeatedly reports that no bomber hull passed the current side/station tech gate (`/tmp/allegiance-evidence/SC1/final-local/mcp-stderr.log`, finale diagnostics). The selector preserves legal station-damage requirements (`tools/allegiance-mcp/TechLoadoutSelector.cs:185,261`), while the existing deadline can finish a short research match before those weapons become available (`src/Allegiance.Server/Server.Showcase.cs:25`). This is a tech/timeline limitation of this proof; the focused test verifies real station damage, so no missing damage rule is established. Native reference: `Igc/stationIGC.cpp:159-200`.

**station-kills — side 0 0/1, side 1 0/1.** No station was killed, consistent with zero station-damage hits. The match result came from the existing bounded research finale, which is intentionally not counted as a station kill. The lethal hull-crossing counter in `src/Allegiance.Core/Sim/Station.cs:587` is separately verified by the focused damage test. A longer native siege is required for live coverage. Native reference: `FedSrv/FedSrv.CPP:12237`; `Igc/stationIGC.cpp:189-193`.

**nanite-ship — side 0 0/10, side 1 0/10.** The server already repairs hulls from negative-power rounds and clips over-repair (`src/Allegiance.Core/Sim/Ship.cs:4510`); focused tests prove an effective repair increments and a full-health shot does not. Native NPC repair orders also exist (`Ship.cs:6958`), but the showcase legal combat selector rejects nonpositive-power guns (`tools/allegiance-mcp/TechLoadoutSelector.cs:123,218`) and has no friendly support role. SC-2 should add legal support loadout/target policy, not rebuild the damage rule. Native reference: `training/mission8.cpp:278`; `Igc/shipIGC.cpp:1244-1267,2608`.

**nanite-station — side 0 0/10, side 1 0/10.** Negative damage can already restore station hull and bypass friendly positive-damage immunity (`src/Allegiance.Core/Sim/Station.cs:516,526`), and the focused test verifies effective and ineffective station repairs. The showcase has no nanite loadout/repair-station decision policy; research/finale bots choose positive-power combat loadouts (`tools/allegiance-mcp/TechLoadoutSelector.cs:123,218`). The old comment claiming there is no repair source is stale; it is not a missing server repair rule. Native reference: `training/mission8.cpp:221`; `Igc/stationIGC.cpp:147-156`.

**order-goto — side 0 0/5, side 1 0/5.** The research commander can send goto before build when a constructor and its selected rock are in different sectors (`tools/allegiance-mcp/ResearchCommander.cs:204-217`; `BotManager.Research.cs:102`). This generated map supplies suitable home-sector resources, so the observed constructors can receive build directly. Combat peers' own travel orders are deliberately outside this NPC commander counter. The focused command test proves a legal drone goto increments and side attribution is correct. Native reference: `training/mission5.cpp:512`; `FedSrv/FedSrv.CPP:3252`.

**order-attack — side 0 0/5, side 1 0/5.** Combat peers acquire and attack targets themselves (`tools/allegiance-mcp/BotManager.cs:2940`); the research commander emits goto/build/chat, not a tactical NPC attack stream (`BotManager.Research.cs:99-105`). The native command arm exists and the focused test confirms legal enemy attack acceptance (`src/Allegiance.Server/Server.ShipCommands.cs:1877`). This is missing showcase commander policy, not missing server command support. Native reference: `training/mission5.cpp:556`; `FedSrv/FedSrv.CPP:3252`.

**order-repair — side 0 0/2, side 1 0/2.** The server accepts repair orders for friendly ships/stations and maps them to `SetRepairOrder` (`src/Allegiance.Server/Server.ShipCommands.cs:1885`), with the real repair combat arm in `Ship.cs:6958`. Both friendly acceptance and enemy rejection are tested. The showcase research commander emits goto/build/chat only, so no tactical repair command is generated. A repair-equipped support drone and commander policy are the missing showcase pieces. Native reference: `training/mission10.cpp:706`; `Igc/shipIGC.cpp:2608-2678`.

**drone-build — side 0 1/2, side 1 2/2.** Construction is live: both sides completed stations, but side 0 completed only one against a floor of two. Research plans serialize funding, constructor travel, drilling, and branch prerequisites (`tools/allegiance-mcp/ResearchCommander.cs:149-222`); the short finale deadline interrupts that progression. The counter is on completed station construction, not order retries or bucket purchases (`src/Allegiance.Core/Model/Sector.cs:4572`). No absent build rule is inferred. Native reference: `training/mission5.cpp:323`; `Igc/buildingeffectigc.h:348`.

**drone-mine — side 0 3/10, side 1 3/10.** Both sides started effective mining, but only three episodes each against a ten-episode floor. This counter measures positive-ore starts, not every mining tick, and one long productive mining episode does not satisfy repeated-start coverage (`src/Allegiance.Core/Model/Sector.cs:4165`). The 15v15 economy and short runtime do not establish the 107v107 expectation. Native reference: `Igc/shipIGC.cpp:2211-2241`.

**eject — side 0 8/10, side 1 14/10.** Both sides produced real pods, but side 0 had fewer than the proposed minimum. The counter records actual `EjectSurvivor` conversion (`src/Allegiance.Server/Server.Combat.cs:2104`), not all deaths; native NPC destruction and killed pods need not eject. This is combat exposure, not evidence of missing ejection support. Native reference: `FedSrv/FedSrv.CPP:12008`; combat/respawn training `training/mission6.cpp:518`.

**pod-rescue — side 0 0/2, side 1 0/2.** Bots actively fly pods home (`tools/allegiance-mcp/BotManager.cs:3773`), but there is no showcase policy assigning a rescue-capable ship to pick up another pod. Native ship pickup and station rescue callbacks exist (`src/Allegiance.Server/Server.Docking.cs:706,808`); none executed in this run. A pod docking itself is recorded as docking rather than falsely credited as another ship's rescue. Native reference: `FedSrv/FedSrv.CPP:13033-13076`.

**dock — side 0 3/20, side 1 7/20.** Real docking occurred on both sides but below a floor intended for a longer/larger match. Pod return uses home-station selection and dock approach (`tools/allegiance-mcp/BotManager.cs:3773,3828`); only actual collision docks increment (`src/Allegiance.Server/ServerHost.cs:1143`). Initial seating, purchases, and direct rescue placement do not inflate this count. No missing docking rule follows from this short proof. Native reference: `FedSrv/FedSrv.CPP:13087`; `Igc/shipIGC.h:441`.

**ship-kills — side 0 18/20, side 1 12/20.** Real lethal hull crossings were credited on both sides, but neither reached the 45-minute floor in this short match. The counter uses native damage-track credit before the death callback (`src/Allegiance.Core/Sim/Ship.cs:4612`), excludes oxygen expiration and consumed layers, and should not be equated with ejections or shots. Native reference: `training/mission6.cpp:518`; `Igc/shipIGC.cpp:1317-1331`.

## Proposed follow-ups

- **SC-2: nanite support bots.** Add a legal support loadout policy and friendly damaged ship/station targeting. Prove effective negative damage on both target kinds and survival/relaunch of the support ship. The server already implements the mechanics; preserve its friendly/enemy repair and clamping rules (`Igc/shipIGC.cpp:1244-1267`, `Igc/stationIGC.cpp:147-156`).
- **SC-3: cloak usage.** Connect a deliberate combat/scouting cloak policy to the existing client control bit, then qualify actual emitter activations under legal hull/part/energy gates (`training/mission4.cpp:719`; `Igc/cloakigc.cpp:80`).
- **SC-4: dispensers and scouting.** Add chaff defense and deliberate probe deployment, distinguish scanner contact acquisition from exploration, and diagnose legal mine availability/range gates without spawning artificial mines (`FedSrv/FedSrv.CPP:5672,13801`; `Igc/shipIGC.cpp:2651`).
- **SC-5: commander tactics.** Exercise native NPC goto/attack/repair and construction orders beyond the research plan's goto/build loop. Confirm actual repair rather than just affirmative speech (`FedSrv/FedSrv.CPP:3252-3257`; `Igc/shipIGC.cpp:2608-2678`).
- **SC-6: longer coverage qualification.** Run the rig's normal 107v107/45-minute profile and investigate low station/docking/rescue/build activity against its actual tech and travel timeline. Replace provisional floors only with measured multi-match expectations. Rig work is deferred to the Claude lane as requested.

## Validation and evidence

- Whole-solution build: **0 warnings / 0 errors**. Linux-x64 self-contained Native AOT publish succeeded with **no IL2026/IL3050**; the compiled native server also answered the real stdin `skills` query with both side snapshots and exited cleanly.
- Six focused counter tests passed **3/3 individual invocations each (18 passes, zero skips)**: real fire/deploy sites, effective ship/station nanites and typed damage, terminal station hits, mounted cloak activation, accepted/rejected commander orders, side attribution, snapshot/reset isolation, and one final block across duplicate game-over calls. The final additional cloak/terminal assertions were qualified separately; the full sweeps were not repeated.
- Four parser/schema tests passed **3/3 individually**. They cover catalogue/minimum parity and LF, side/match separation, final-over-snapshot selection without summing, missing counters, zero/under-minimum reporting, and malformed pairs.
- Never-parks gate: **unchanged base 5/5; changed code 5/5**. Longest stopped-with-target interval: base **2.052 s**, changed **2.017 s**, against the unchanged 5-second assertion.

| Single full sweep | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Core | 4874 | 15 | 0 |
| ClientCore | 1453 | 3 | 1 |
| MonoGame | 2471 | 0 | 3 |

**16 failed sweep cases** qualified with three fresh exact-case passes; [isolated.json](SC1_assets/isolated.json) preserves all attempted windows, including the intermittent ripcord case's first 2/3 window and its final recovery window. Three optional private Meshy-pack cases and one explicitly opt-in rig case were skipped by the test runner.

**The performance gate is not wholly green:** 2 existing performance cases also reproduce on unchanged base `3b37af4e`.

- `Allegiance.Core.Tests.P694MassEventTickTests.Launch_point_mass_events(count: 150, parallel: True, stationDeath: True, coincident: False, nativeLaunch: False)`: changed **3/3 failures**, base **3/3 failures**.
- `Allegiance.Core.Tests.P694MassEventTickTests.Launch_point_mass_events(count: 195, parallel: False, stationDeath: True, coincident: False, nativeLaunch: False)`: changed **3/3 failures**, base **2/3 failures**.

These are retained as base/load performance limitations, not claimed as passing checks or fixed regressions. The same P694 rows have prior base failures documented in [P745](P745_FINALE_SIEGE.md).

See [validation.json](SC1_assets/validation.json) for TRX counters, failure messages, parking samples, and AOT evidence. Preliminary overlapping helper output was discarded from qualification; authoritative isolated output is in `isolated-exact/` and `isolated-base-exact/`. The temporary generated runner hook used to stop those helpers was restored. No second full sweep or rig/221/200v200 run was made.

Raw logs, TRX, configuration, failed setup diagnostics, base comparison binaries, and parser output are outside the repository under [the external evidence directory](/tmp/allegiance-evidence/SC1/) (base binaries `/tmp/sc1-base/`, native publish `/tmp/sc1-aot/`). This sandbox permits writes only to the workspace and `/tmp`, so the requested `~/allegiance-evidence/SC1/` location is unavailable; no raw evidence was put in the repository. Small review evidence is under [SC1_assets](SC1_assets/).

The initial local attempt requested the file-map name Alderan on a generated map and was rejected before spawning a fleet; its diagnostics are in `local-invalid-sector/`. A preliminary instrumented run is in `local/`. The reported proof is `final-local/` and uses the final production changes. The first build encountered NuGet's read-only default HTTP cache; all subsequent restore/build/publish operations used `NUGET_HTTP_CACHE_PATH=/tmp/sc1-nuget-cache` with ordinary package auditing intact.
