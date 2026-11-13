# P1007 — Skirmish garrison alerts

**Finding: reproduced a port bug at five minutes.** In the unchanged seeded
Small/Normal skirmish, an enemy Scout's PW Gat Gun 1 shots triggered two garrison
warnings at match **328.55 s (5:28.55)** and **342.50 s (5:42.50)**. Incoming
power was 3.2, damage type 2. Both native-core matrix entries against the
starting garrison are **zero** (shield defense 10, armor defense 7). Shield and
armor remained 1.0. These were harmless enemy rounds, not a bomber strike or a
base taking damage. The existing repeated-alert throttle worked: the reports
were 13.95 simulation seconds apart.

Changes remain uncommitted; no pushes, stash, remote machines or public hosts
are used. This report was written before the final gate. All four observation
windows and validation are complete. **Builds and 144 task isolation cases pass;
the original full Fast gate is FAIL (14,140/5/20), retained below.**

## Native trigger and delivery

Citations were re-grepped in the mounted native source on 2026-11-13.

- `Igc/stationIGC.cpp:126-133`: pedestal/invulnerable-station immunity and
  non-repair friendly/allied immunity when friendly fire is off precede damage
  processing. `:142-145` applies the attacking ship's experience multiplier.
  `:147-156` repairs hull without reporting an attack. `:159-169` raises
  `DamageStationEvent` only in the nonnegative-damage branch with a nonzero
  shield matrix entry and nonzero shield capacity, **before** health mutation.
  Depleted shields still qualify if the type has capacity; armor-only damage
  can flow through the overflow path. Shieldless or immune bases do not alert.
- `FedSrv/FedSrv.CPP:12183-12190`: require scaled amount >= 1, a launcher
  with a side, no `c_dmgidNoWarn`, and time since the station's last report
  **strictly greater than 10 seconds**. `:12231` stamps only passing reports;
  rejected hits do not slide the window. `Igc/stationIGC.cpp:83` seeds the
  timestamp with creation time, including stations created later in a match.
- `FedSrv/FedSrv.CPP:12192-12212`: cross-side damage reports to the defending
  side, with CHAT_ALLIES when that side has an alliance group, otherwise
  CHAT_TEAM. Allied attackers still produce an alert with an `(Ally)` prefix
  when friendly fire permits damage. The pre-hit hull fraction < .30 selects
  the station type's CriticalSound and critical text; otherwise UnderAttackSound.
  `:12214-12228` privately warns allied/same-side player launchers to watch their
  fire. There is no enemy-only, miner-only, fog, range, wave, or match-age test
  in this server callback. Same-side damage never generates a team attack alert.
- `Igc/igc.h:558-564`, `Igc/shipIGC.cpp:807-821,977-991`: collision damage
  carries NoWarn, NoDebris and NoFlare (0xe0). Damage lookup masks to row 0
  (`Igc/missionigc.h:148-155`); event consumers must retain the flags. Native
  blast damage also adds NoWarn (`Igc/clusterIGC.cpp:431`), so a splash-only
  event does not start another warning.
  The existing port handler accepts Ship launchers; SIM-0649 documents native’s
  unsafe cast of other model types. This task does not broaden that handler.
- `WinTrek/trekigc.cpp:4525-4536`: a visible combat chat with a direct sound ID
  plays that sound; `:4810-4819` appends/logs the chat and invokes BaseClient.
  The alert does not require a separate client cooldown.

The port's production chain is `Station.ReceiveDamage` ->
`ServerHost.DamageStationEvent` (`src/Allegiance.Server/ServerHost.cs:977`) ->
`BroadcastStationDamaged` (`src/Allegiance.Server/Server.Combat.cs:1167`) ->
CHATMESSAGE. `ConnectionSession.cs:1742-1743` decodes it;
`ClientWorld.Callbacks.cs:249-290` records chat and its resolved voice;
`Game1.Audio.cs:568-569` plays that voice; `Game1.Session.cs:1763,1917`
draws chat. The garrison sound ID is 820 (`vo_attack_garrison`). Both HUD
styles consume this same path. There is no HUD, layout or audio change here.

## Root causes and fix

All three discrepancies are **port bugs**, not era constraints:

1. Dispatch `src/Allegiance.Core/Sim/Station.cs:504-517` raised the callback
   immediately after the pedestal gate, before friendly immunity, experience
   scaling or the shield matrix/pool test. P451's historical report explicitly
   described this broader notification as a deliberate divergence. It allowed
   the reproduced harmless scout rounds to sound like garrison damage.
   The callback now sits inside the eligible shield branch at `Station.cs:543`,
   using the station's own mission site and retaining pre-hit hull selection.
2. `src/Allegiance.Core/Model/Sector.cs:2930` reduced collision damage to matrix
   row 0 before event delivery, losing native NoWarn. It now passes 0xe0;
   the existing recipient damage lookups perform masking. This also preserves
   the flags through both the regular and experimental collision dispatch.
3. Dispatch `Station.cs:449` initialized LastDamageReport to negative infinity,
   skipping native's initial ten-second quiet period. Placement now initializes
   creation time (`Mission.cs:1327-1339`); construction passes its actual substep
   time (`Sector.BuildStation`). Repeated-hit throttling already existed and
   retains its strict, non-sliding, independent-per-station behavior.

`docs/parity/sim-rules-matrix.csv` updates SIM-0201 and SIM-0649; the glossary
indexes the rule and the production-host proof. No bots, fleet allocations,
research costs, difficulty knobs, strike settings or default fog are retuned.

## Local skirmish method and observations

`tools/p1007-skirmish-proof/Program.cs` starts the same `SkirmishHost.StartAsync`
used by `Game1.Skirmish.cs`, with the real external supervisor/workers and an
ordinary side-zero `ConnectionSession`: LOGON, POSITIONREQ, ready, legal launch
and flight/fire controls. It runs 600 simulation seconds after Started, at the
normal production cadence, with native economy and research, no reveal edits,
no grants, no shortened countdown and no finale overrides. It brackets the
real station callback without replacing the server handler, records every
report's station, attacker, ship kind/side, time, incoming amount, health and
matrix, and records received player chat separately from miner warnings.
Snapshots retain per-side research, treasury, kills, fleet health and idle
counters. This is a headless reproduction of the client's local-host path,
not a rendered-window playthrough or a claim to manually test audio hardware.

Default Small is five seats per side (four friendly bots plus the player,
five enemy bots), Iron Coalition vs Ga'Taraan, m2xspiderw, Normal speed and
difficulty, AI commanders, research tech. Object fog stays enabled. The
shipped Small/Medium default reveals **warp topology only** (ShowMap=true);
that is not an object reveal. A supplementary Large pilot run uses its default
ShowMap=false for fully undiscovered topology and objects. Seeds pin the
host's supported seed/map resource generation; bot scheduling and generated
names remain nondeterministic. Baseline assemblies are copied before edits;
the second baseline runs from that immutable copy.

Every window reached **600.05 seconds after Started**, with a real player on
side zero and all bot workers alive. Results (station alert counts include both
sides; the player receives only side-zero reports):

| Build / seed | Size / warp reveal | Station alerts | Player station chats | Shots / ship hits | Max idle-with-target |
| --- | --- | ---: | ---: | ---: | ---: |
| Baseline / 1007 | Small / default on | 2 false | 2 | 2,720 / 1,941 | 0 |
| Baseline / 1008 | Small / default on | 0 | 0 | 2,356 / 1,261 | 0 |
| Fixed / 1007 | Small / default on | 0 | 0 | 3,732 / 1,601 | 0 |
| Fixed / 1009 | Large / off | 0 | 0 | 26,033 / 11,862 | 0 |

The **complete station-alert ledger**, including attacker name/ID/side/kind,
incoming power, both matrix multipliers and health, is
[P1007_assets/alerts.csv](P1007_assets/alerts.csv); the per-run summaries and
side/research observations are [P1007_assets/skirmishes.json](P1007_assets/skirmishes.json).
There were **no eligible enemy-damage alerts, own-side/neutral/miner/collision/
splash misreports, wrong-side recipients, or cooldown violations** among these
recorded station reports; the two false reports were exclusively zero-matrix
scout bullets. Native collisions and splash are additionally pinned by the
regressions. Zero live warnings after the fix alone is not a deterministic
counterfactual: the exact 3.2/type-2/garrison regression independently proves
the ignored-hit behavior, and real damaging missile tests prove alerts remain.

The initial seed-1007 baseline had exactly the two
false station alerts described above, 36 station callbacks and no station
health changes. Four additional received under-attack chats concerned a miner
(SidSender=8096, voice 1339), not a station. The proof now labels these
separately; the initial raw summary's pilotChats=6 includes both kinds.
All 36 baseline station callbacks were type-2, power-3.2 rounds; neither
station's health changed. The first seed-1008 attempt exited during its first
snapshot because the proof serialized not-yet-received (Undefined) stats. The
proof now records null until health arrives; that aborted attempt is excluded
and its full successful rerun is the baseline 1008 row above.

Reproduce with configured corpus paths and built tools:

```sh
dotnet run --no-build --project tools/p1007-skirmish-proof -- --seed 1007 --output .scratch/p1007/repro
# Full fog while occupying an ordinary player seat:
dotnet run --no-build --project tools/p1007-skirmish-proof -- --seed 1009 --size Large --output .scratch/p1007/repro-large
```

Raw events, server/worker logs, baseline binaries, build logs and TRX remain
under ignored `.scratch/p1007/`. The small public evidence files contain no
private absolute paths or artwork.

## Real attacks and five-minute timing

Native does not provide this port's showcase/skirmish player-bot wave scheduler
or a five-minute base safety rule. Native armed wingmen/cheat players execute
an attack target as soon as visibility/weapon/steering conditions permit
(`Igc/shipIGC.cpp:2353-2356,2409-2469`). A genuine qualifying hit at five
minutes should therefore alert immediately, subject to the station cooldown;
it is not evidence by itself of a native timing violation. Exact five-minute
AI wave parity cannot be claimed for this modern preset.

The reproduced attacker is a player bot (pilot type 10), ship 8103, side 1,
hull 114 Scout, initially a Fighter slot legally falling back to Scout.
Its worker log records recon and ordinary ship attack orders, then a friendly
miner/constructor support call at about 341 s. The station warnings came from
those harmless gun rounds reaching the garrison during combat, not a released
bomber wing. P778/P953's intended reconnaissance, miner raids and strike policy
remain intact.

`SkirmishDriver.cs:24-43` requests the showcase preset and small-fleet meeting
engagement. `BotManager.Exploration.cs:846-911` allows observed-enemy combat and
miner raids while scouting. `McpTools.cs:1503-1514` defaults
`finale_after_minutes` to **30**, `finale_early_pressure_minutes` to **0** (off),
and pre-arm to 0. The default skirmish does not override them. A research
finale can also wait for station discovery/registration and maturity. These
are port preset controls, not native damage rules or a five-minute timer.
No setting is changed in P1007.

## Other “iffy” observations — evidence only

Initial seed 1007, last snapshot at simulation 602.25 s (match 586.95 s):

- No sampled bot with a target exceeded the existing five-second stationary
  threshold (maximum currentCount and botsLastMinute both 0). Combat remained
  active: 2,720 shots and 1,941 ship hits. The worker stayed alive. No stuck
  combat fleet or completely idle side was established in this window.
- Both AI commanders were only at authored plan step 4, Retro Booster, waiting
  for money at Normal x1 income. Side 0 progress was 5.83%, side 1 93.33% on
  that step; both still owned their garrison. This is slow observed progression,
  not proof of a broken economy. The current scope leaves it unchanged.
- The combat snapshot reported three lingering pods, five docked and two
  restarted. This is a follow-up navigation/recovery observation; the sample
  does not establish that those three pods were permanently stuck.
- The worker's fallback finale registration logged “no buyable bomber strike
  loadout” and “no owned station type resolves for the current dock/home” for
  side 1, while ordinary research listed Garrison. It also logged the expected
  waiting requirement age >=1800 s, so this was not a five-minute strike.
  The shop/discovery fallback deserves a separate investigation; it is not
  changed here.

The other runs corroborate the limited scope: no sampled stationary targeted
bot in any run; Small final samples remain at research step 4, while the fully
fogged 30-seat-per-side Large run is still funding step 2 (.TowerAC), with
16 lingering pods and active combat on both sides. Slow early research is a
concrete usability observation for follow-up; no broken-economy root cause or
permanently stuck pod is established by these bounded samples.

These observations distinguish native x1 economy/preset design from confirmed
alert bugs. No additional era constraint was needed to explain the warning.

## Validation and gate

The first relevant regression run before the fix was 10 Passed / 8 Failed /
0 Skipped; its raw diagnostics include an initial immunity-fixture indexing
error and an asynchronous chat-drain assumption, which were corrected. The
real collision regression failed for both dispatch paths before the flag fix
(0/2/0). Final diagnostic after the implementation and fixture corrections:
**24 Passed / 0 Failed / 0 Skipped**.

Exactly **one** full Fast sweep ran within a **7,200-second (two-hour) bound**,
exceeding the brief’s 90-minute minimum allowance. It completed in **582.240 s**:
**14,140 Passed / 5 Failed / 20 Skipped**. Infrastructure **9/0/0**, 0.059 s.
The original sweep result is **FAIL**, including four timing overruns against
the calibrated **14.562 s** limit. No full sweep was repeated or replaced.

The task-related P906 assertion expected stripped collision byte 0. It now
expects native 0xe0, retaining every movement/order/timing assertion. After the
final **0-warning / 0-error** solution build (15.24 s), the corrected whole
P906 class passed **24/0/0 ×3**. Together with alert **19/0/0 ×3** and collision
**5/0/0 ×3**, that is **144 Passed / 0 Failed / 0 Skipped** task case executions.
The complete tools gate passed **136 projects**, zero warnings/errors each.

All remaining failed or slow methods were rechecked three times alone:

| Method | Isolated P/F/S, repeats 1–3 | Longest case seconds, repeats 1–3 |
| --- | --- | --- |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | 1/0/0; 1/0/0; 1/0/0 | 1.334 / 1.343 / 1.303 |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | 1/0/0; 1/0/0; 0/1/0 | 1.508 / 1.545 / 38.537 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | 3/0/0; 3/0/0; 3/0/0 | 5.582 / 5.528 / 5.461 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | 2/0/0; 2/0/0; 2/0/0 | 4.183 / 4.159 / 4.168 |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 0/1/0; 0/1/0; 0/1/0 | 1.204 / 1.052 / 1.036 |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | 3/0/0; 2/1/0; 3/0/0 | 0.971 / 0.936 / 0.918 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | 10/0/0; 10/0/0; 10/0/0 | 5.653 / 4.611 / 4.627 |

The missile-lock and floating deadline failures persisted intermittently.
Seating failed all three isolated runs at 32.150000000000297,
31.000000000000309 and 32.200000000000294 s against the [28,31] bound.
Respawn passed all three; the three passing but slow sweep methods all
rechecked below the calibrated limit. Their
existing signatures and all new results are recorded in `docs/TEST_FLAKES.md`.
No non-task implementation or fixture was changed. The original Fast failure
is not waived. The task is fixed and individually qualified; this report does
not claim a green repository-wide Fast gate.

Counts, original failed/slow test identities, all 20 skipped cases and isolated
results are retained in [P1007_assets/validation.json](P1007_assets/validation.json).

The alert fixture now has a real shield pool; the old fixture's zero-capacity
station could only pass because of the erroneous broad callback. Its second
missile assertion now awaits actual additional shield damage inside the
cooldown, rather than waiting for hull damage that cannot happen. New cases
cover the exact scout/garrison receipt, shieldless/matrix-immune stations,
pedestals, repairs, friendly/allied FF off and on, subthreshold/scaled damage,
NoWarn, null/unseated launchers, exact cooldown boundary, independent stations,
creation suppression, and real rams in both collision dispatch modes.

Builds used the specified build-server workaround and the local package cache
as the restore source; NuGet audit was not disabled. No public package or audit
endpoint was contacted, so fresh online vulnerability data is not claimed.
A task-local Tailscale stub returned no peers during tests. Temporary fixtures
physically lived in ignored scratch via a `/proc` directory alias, keeping
bundle-path tests from discovering the checkout’s ancestor `local.env`.
No UI change required screenshots; no training implementation changed.

Glossary links (**797 paths**), parity CSV validation (**860 rows**), whitespace
and working-tree hygiene pass. No media, secrets or private home paths were
added. The brief was moved into ignored scratch. Each proof host disposed its
supervisor/workers and ticker; all successful observation processes exited 0,
and all build/test commands ended. No commits or pushes were made; HEAD remains
`a87b00f34a111edcd65958fcc22fb0b6b1f38853`.

## Native intent

Only eligible station damage produces an attack report. Preserve native
friendly immunity, damage matrix, experience threshold, collision/blast flags,
creation/repeated-report clocks, defending/allied recipients and station voice.

## Modern approach

Exercise the actual local skirmish host with ordinary pilot traffic, retain a
complete alert ledger and bounded economy/fleet observations, then correct the
shared simulation rule without masking the symptom by delaying bot attacks.
