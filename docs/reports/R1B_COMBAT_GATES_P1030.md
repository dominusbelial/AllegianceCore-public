# P1030 — R1b F5: fire-expendable / fire-missile / treasure-ack bypass mount, cooldown, ripcord and lock rules

Owner finding: **F5** of `R1B_CARBON_REPORT.md`. Report name: `R1B_COMBAT_GATES_P1030`.
All three sub-findings are real and are fixed here. No commit or push is made by
this lane; the changes stay in the working tree.

## Root causes and fixes

Line numbers for the root causes are the pre-change tree; fix locations are the
post-change tree.

1. **`FM_C_FIRE_EXPENDABLE(82)` skipped every readiness rule.**
   `HandleFireExpendable` (`src/Allegiance.Server/Server.ShipCommands.cs:768`)
   called `Ship.FireDispenser` (`src/Allegiance.Core/Sim/Ship.Equipment.cs:122`)
   directly, and `FireDispenser` only checked mission/sector/docked/boarded/part
   membership/amount. The held-key leg `UpdateDispenser`
   (`Ship.Equipment.cs:94`) enforced `MountedFraction >= 1`, `now > TimeLoaded`
   and "no ripcord unless chaff", but the wire leg did not, and neither checked
   `Dead` or a real side. `FireDispenser` then armed the deadline only *after*
   the deployment, so two requests inside one tick both fired. Simply sending one
   UDP packet of 30 `FireExpendableRequest` frames drained the whole reserve:
   the reproduction measured **8 mines from a 30-copy packet** (the entire
   8-round load) before the fix.
   *Fix:* the readiness rules now live in `FireDispenser` itself, so the wire
   handler and the held path share one gate: alive, `SideId >= 0`, fully mounted
   (`MountedFraction >= 1`), loaded (`now + tolerance > TimeLoaded`), and
   `RipcordModel` only tolerated for a chaff launcher. The deadline is armed
   *before* the deployment as `Math.Max(now, TimeLoaded) + loadTime`.
   `UpdateDispenser` no longer arms it before the callback; it only spends the
   cooldown if the callback body refused (`Ship.Equipment.cs:94-105`), which keeps
   native's "arm before the callback" protection without double-advancing the
   live path. `FireMountedExpendable` logs a refused request
   (`Server.ShipCommands.cs:817-828`).

2. **`FM_CS_FIRE_MISSILE(70)` bypassed mount readiness, shortened the deadline and
   trusted the client's lock.** `Ship.TryFireMissile` (`Ship.cs:4918`) gated
   `MountedFraction < 1` only when `clientRequest == false`; a player request
   while the rack was mounting created a **live, fully locked missile** (the
   reproduction measured `LockFraction = 1`), consumed a round and overwrote
   `TimeLoaded = now + LoadTime`, which is *shorter* than the mount deadline
   (`LastUpdate + 1/MountRate + LoadTime`) — so the next shot fired armed before
   the rack finished mounting. It also used the client's `req.Lock` verbatim and
   had no `Dead`/`SideId` gate.
   *Fix:* a dead ship or one without a real side never fires; a player request
   while `MountedFraction < 1` is a **dud** (still consumes the round, as native's
   early-shot rule does) and the deadline is armed from
   `Math.Max(now, TimeLoaded) + LoadTime`, so a dud can never shorten a pending
   deadline. The reported lock is bounded by the server's own `MissileLock`
   (`Ship.cs:4918-4975`); the inbound handler logs a clamp
   (`Server.ShipCommands.cs:913-919`).

   **Does the server track lock for human ships every tick?** Yes.
   `UpdateMountedMissileLock` (`Ship.cs:4899`) runs from `Ship.OnUpdate` for every
   ship in the mission — not only NPCs — building `MissileLock` from the server's
   own `CommandTarget`, geometry, range, visibility and side checks
   (`UpdateMissileLock`, `Ship.cs:4829-4878`; native `Igc/magazineigc.cpp:104-176`).
   The value then reaches the owner every tick as `MISSILE_LOCK_UPDATE(204)`
   (`Server.Broadcast.cs:3600-3617`; proven by P230
   `tests/Allegiance.ClientCore.Tests/MissileLockLiveTests.cs`), and the honest
   client sends exactly that value back (`ConnectionSession.SendFireMissile` reads
   `World.MissileLock.LockFraction`, `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1281-1295`).
   Bounding is therefore safe: for an honest client `min(requested, tracked)` is an
   identity, which the live control test below asserts over the real transport.
   No proposal is needed — the tracking exists.

3. **`FM_C_TREASURE_ACK(163)` armed a pickup instantly and lost cargo stacks.**
   `HandleTreasureAck` (`Server.ShipCommands.cs:2556`) passed
   `ExpandedPartData.FullyMounted` to `Ship.MountPart`, so a part salvaged in space
   arrived armed (instant shield/weapon/cloak/launcher), and the merge lookup
   scanned only `ship.Parts`, so a stackable pickup acknowledged into an occupied
   cargo slot (`Mount -1..-5`) missed `_cargo`, was refused by `MountPart`'s
   occupied-cargo guard, and the already-consumed offer was destroyed.
   *Fix:* a fresh mount uses `fractionMounted: 0` (native `CreateAndAddPart`
   leaves `m_mountedFraction = 0`, only the dock `BuyLoadout` arms a part) and a
   negative mount resolves through `Ship.CargoHold` and merges with
   `SetCargoPartAmount` (`Server.ShipCommands.cs:2582-2618`).

Both dispenser fire bits (`MineFire = 0x20000`, `ChaffFire = 0x40000`,
`src/Allegiance.Core/Sim/ShipControlState.cs:14-16`) live in the high word that the
16-bit wire `stateM` cannot carry, exactly like `missileFireIGC`, so a player's
dispenser only fires through the wire handler while the held-key leg runs for NPC /
local state. Both now share one gate, which is why an honest player's single key
press behaves as before and a packet burst cannot.

## Native evidence and parity verdict

Re-read from the native corpus for this task (`/srv/corpus/Allegiance/src`):

| Rule | Native source | Verdict |
| --- | --- | --- |
| A dispenser fires only fully mounted, loaded, alive and not mid-ripcord (chaff exempt) | `Igc/dispenserigc.cpp:88-94,103,117` | Native parity on behaviour; **port hardening on authority** (native enforced it on the client only) |
| Firing arms `m_timeLoaded = now + LoadTime` before the callback | `Igc/dispenserigc.cpp:119` | Native parity on the arming rule; port hardening on the accepted request rate |
| A magazine fires only after `m_mountedFraction == 1` | `Igc/magazineigc.cpp:85-91,102` | Native parity on behaviour; port hardening on authority |
| An early player shot is a dud (`timeFired + 0.5 < timeLoaded`) and consumes the round | `FedSrv/FedSrv.CPP:5433-5434,5468,5507-5510` | Native parity; the port additionally never shortens a pending deadline (server clock, not the client's `timeFired`) |
| The server copies the client's `lock` and reserves no lock of its own | `FedSrv/FedSrv.CPP:5444`, `Igc/magazineigc.cpp:104-176` | **Deliberate port hardening on authority**; the port computes the lock every tick (`UpdateMountedMissileLock`) and bounds the request by it — native parity on behaviour for an honest client (it echoes the broadcast lock) |
| `CreateAndAddPart` leaves `m_mountedFraction = 0`; dock `BuyLoadout` arms parts | `Igc/shipIGC.h:1490-1512`; `FedSrv/FedSrv.CPP:6099-6119` | Native parity |
| `GetMountedPart` reads `m_mountedCargos` for a negative mount and the handler merges the amount | `Igc/shipIGC.cpp:1516-1549`; `FedSrv/FedSrv.CPP:6101-6103` | Native parity |
| `FIRE_EXPENDABLE` server handler itself had no mount/load/cooldown check; `FIRE_MISSILE` trusted `pfmFireMissile->lock`; native had no server-side `TryAutoReloadLauncher` | `FedSrv/FedSrv.CPP:5615-5780`, `:5408-5510` | Confirms the finding's framing: the port runs those rules server-side, so they are deliberate hardening |
| `CanMount(ppt, mountID)` accepts every cargo mount `>= -c_maxCargo` | `Igc/hullTypeIGC.h:199-213`; `Igc/shipIGC.cpp:1516-1549` | Existing port rule (`DataHullTypeIGC.CanMountPart(part, mount)`) reused unchanged |

`docs/parity/p1030-combat-gates.csv` records these six contracts (100 % weighted
credit, 21/21; validated by `scripts/parity-scorecard.py`). No parity row that
describes a native behaviour changes: the native behaviours are unchanged and the
new rows state the authority split explicitly.

## Reproduction and focused coverage

`tests/Allegiance.Core.Tests/P1030CombatGatesTests.cs` drives the production inbound
dispatcher (`Server.TestProcessDatagram`) with the encoded frames a remote peer
would send, against a real static core (`PCore001.igc`), mission and sector — the
`R1ReviewTests` world pattern. Cases:

1. `One_packet_of_30_fire_expendable_requests_deploys_one_round` — 30 copies in one
   packet deploy one mine and spend one round (device `FireExpendable(87)` relay
   present once).
2. `Fire_expendable_is_refused_while_the_dispenser_is_still_mounting`.
3. `Fire_expendable_obeys_the_load_deadline_and_rearms` — refused 1 s early, fires
   0.4 s early (inside the tolerance), the armed deadline then refuses and re-fires
   exactly at the deadline (legit fire rate unchanged).
4. `Ripcord_refuses_mines_but_allows_chaff_over_the_wire`.
5. `Dead_or_lobbied_ships_cannot_fire_expendables` (theory: dead / team lobby).
6. `Fire_missile_while_the_rack_is_mounting_is_a_dud_and_keeps_the_mount_deadline`.
7. `Fire_missile_lock_is_bounded_by_the_server_tracked_lock` — a claimed 1.0 with no
   server tracking fires at zero lock; after `UpdateMissileLock` the same request
   yields a full lock.
8. `Dead_or_lobbied_ships_cannot_fire_missiles` (theory: dead / team lobby).
9. `A_part_accepted_into_an_active_mount_starts_unmounted`.
10. `A_stackable_pickup_into_an_occupied_cargo_slot_merges_the_cargo_stack`.

`tests/Allegiance.Core.Tests/P1030NormalClientFireLiveTests.cs` is the
"honest client is never refused" control over the real transport: a real
`ConnectionSession` drops chaff and launches a missile from a normally armed,
undocked ship; both deploy and both spend one round.

Run unchanged before and after the product fix (test-file SHA-256 in the evidence
directory): **before, 12 of 12 gate cases failed** and the honest-client control
passed; after, **13 of 13 pass**. Pre-fix observations include 8 mines from the
30-copy packet and a live `LockFraction = 1` missile fired from a mounting rack.

Three existing fixtures were corrected because they encoded the bypass itself
(assertions and product behaviour unchanged):

* `tests/Allegiance.Core.Tests/P651EquipmentRulesTests.Early_player_fire_consumes_a_round_and_is_a_dud_outside_half_second_tolerance`
  reported a 0.4 lock without any server-tracked lock; it now builds the tracked
  lock first (`UpdateMissileLock`) — the dud and `LockFraction = 0.3` assertions
  are untouched.
* `tests/Allegiance.Core.Tests/P655GameStateIntegrityTests.H12_Docked_missile_and_mine_requests_preserve_ammo_flying_requests_fire`
  seeded only the ship clocks (`World.Now(10)`) while the handlers use
  `Mission.LastUpdate` (0); it now advances the mission to the same instant so the
  mount deadlines have genuinely passed.
* `tests/Allegiance.ClientCore.Tests/P697GhostBattleLiveTests.Cloak_hidden_chaff_is_not_relayed_and_decloak_bootstraps_the_source`
  dropped chaff twice in the same tick; it now waits for the real 3 s chaff reload
  (`dispenserigc.cpp:119`) between the two drops. The relay/pose assertions are
  unchanged.

## Configuration and limits

Two named constants, no new setting: `Ship.FireLoadToleranceSeconds = 0.5`
(`src/Allegiance.Core/Sim/Ship.cs:4885`, the native 0.5 s early-shot rule,
`FedSrv.CPP:5433`) is shared by the magazine and dispenser request paths, and the
dispenser's acceptance window is `Math.Min(FireLoadToleranceSeconds, loadTime)` so
the window can never equal or exceed the cooldown. That is what guarantees **at
most one round per launcher per tick**: with the corpus expendables (chaff 3 s,
mine 2 s, probe 1 s — measured from `PCore001.igc`) the second copy of a request in
the same packet always sees the armed deadline. No `server.json` knob is exposed:
these are native correctness rules, not operator policy. No new service, store,
port, environment variable or media; Docker is unavailable in the lane and no
deployment configuration changes, so Compose validation is unaffected.

## Validation

Builds and tests use `--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`. Raw logs/TRX are kept outside the repo in
`~/allegiance-evidence/P1030/` (the lane's evidence directory).

| Check | Passed | Failed | Skipped / not executed |
| --- | ---: | ---: | ---: |
| Before fix: P1030 gate cases (unchanged tests) | 1 | 12 | 0 |
| After fix: P1030 gate class + honest-client control (first green run) | 13 | 0 | 0 |
| P1030 isolated repeat 1 / 2 / 3 | 13 / 13 / 13 | 0 | 0 |
| Existing Core combat/equipment classes (263 cases) | 263 | 0 | 0 |
| Existing ClientCore combat classes (43 cases) | 43 | 0 | 0 |
| Existing MonoGame dispenser classes (3 cases) | 3 | 0 | 0 |
| Existing bot combat/duty classes (19 cases) | 19 | 0 | 0 |
| Showcase never-parks (5 isolated runs) | 5 | 0 | 0 |
| One full Fast suite (14,726 cases, 576.5 s) | 14,713 | 6 | 7 |
| Solution build | 0 warnings, 0 errors | — | — |
| `tools/build-tools.sh` (140 projects) | 140 built, 0 warnings | 0 | — |
| AOT publish (`linux-x64`, `PublishAot=true`) | succeeded | 0 | no IL2026/IL3050 |

The 263 Core cases are `P692EquipmentLifecycleTests`, `P787aLauncherReloadTests`,
`P651EquipmentRulesTests`, `MissileFireTests`, `MissileLockTests`, `ChaffTests`,
`P653EconomyLoadoutIntegrityTests`, `P867DispatchCertificationTests`,
`AddPartTests`, `Wire/TreasureWireTests`, `P754CallbackTests`,
`P655GameStateIntegrityTests`, `ArmedProbeLiveTests`, `P780PodRescueProbeTests`
and `LiveTeleportProbeRipcordTests`. The ClientCore 43 are
`P651EquipmentLiveTests`, `MissileFireLiveTests`, `MissileLockLiveTests`,
`MissileTrackingLiveTests`, `CrossSideCombatVisibilityLiveTests`,
`ChaffDropLiveTests`, `P779b1MedevacLiveTests`, `P697GhostBattleLiveTests` and
`P740FlightInterfaceTests`; the MonoGame 3 are `DispenserEquipFireLiveTests` and
`ShopLauncherMountNumberingLiveTests`; the bot 19 are `P837BotEngagementScaleTests`,
`P832fKillBotLiveTests`, `P832dArtifactBotLiveTests`, `BotAcceptsAndEngagesOrderLiveTests`,
`BotBaseDefenseTests`, `P926BotObjectiveTests` and `P832cFlagBotTests`. The showcase
5× covered `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
in five separate processes.

One full Fast sweep ran (`tools/run-tests.sh --tier fast --no-build`, 120-minute
bound, 576.5 s): **14,713 passed / 6 failed / 7 not executed**, calibrated limit
11.545 s (P744 factor 1.155), infrastructure checks 9/0/0. The six failures are the
already-ledgered families — `P739TimerLiveTests` warp-bomb float bound (delay 7 and
10), `NpcDodgeLiveTests` dock-line deviation, `MinerDecisionCycleLiveTests` (also
30.2 s over the limit), `GameOverPlayersWireTests` roster-clock range and
`AsteroidMinedLiveTests` capacity receipt — none of which runs through the dispenser,
magazine or treasure-ack paths. Each was repeated **3× alone**: NpcDodge 1/0/0 ×3,
MinerDecisionCycle 2/0/0 ×3, GameOverPlayers 1/0/0 ×3, AsteroidMined 1/0/0 ×3, and
P739 passed all three rows in run 3 while showing one ULP-bound row failure in runs 1
and 2 (the ledgered deterministic float signature). The six passing over-limit
executions (P814, P796, SC2, P968, P999, P1000) were also repeated 3× alone and passed
well under the limit (P814 3 s; P796 3–5 s; SC2 2–4 s; P968 ≤ 1 s per row; P999 ≤ 4 s;
P1000 ≤ 1 s per row). The sweep is therefore **FAIL on the six ledgered families only**;
`docs/TEST_FLAKES.md` carries this task's observations. Full gate verdict: build/tools,
AOT publish, task tests and live regressions **PASS**; full Fast **FAIL with the
pre-existing ledgered families above**.

Hygiene: `git diff --check` clean, no `\r` in the new CSV, glossary link check
**PASS** (886 distinct paths), parity scorecard **PASS** (6 rows, 21/21), no new
media, no personal paths/IPs/hosts, and the brief file is not part of the change set.
All lane-started processes have exited. No commit or push was made.


## Native intent

Combat readiness belongs to the ship's launcher state: a dispenser or magazine
fires only once it is mounted, reloaded, on a live sided ship, outside a ripcord
(other than chaff), and a magazine's lock is the cone/range accumulation the player
earned. A salvaged part arrives unmounted, and a stackable pickup joins the stack
that is already there — cargo included.

## Modern approach

Keep the authoritative simulation as the single decision point: the untrusted wire
handlers and the local/NPC held-input leg call one shared gate in
`Ship.FireDispenser` / `Ship.TryFireMissile`, the server's own per-tick
`MissileLock` bounds the lock a client may claim, and named constants document the
limits, so honest play (clients, bots, showcase presets) behaves exactly as native
while a modified client gains nothing.
