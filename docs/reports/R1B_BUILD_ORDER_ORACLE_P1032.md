# P1032 — R1b F6: constructor build orders leaked unseen asteroid data; drone chat bypassed the leader check

Owner finding: **F6** of `R1B_CARBON_REPORT.md`. Report name: `R1B_BUILD_ORDER_ORACLE_P1032`.
No commit or push is made by this lane; the changes stay in the working tree.

## 1. Verdict on the finding

| Claim | Real? | Action |
|---|---|---|
| `RefuseBuild` sends server diagnostic strings (asteroid ids, `AabmCapabilities` masks, sector names, "already under construction") to the player | **Yes** — reproduced over the wire on both the order path and the native drone-chat path | Fixed: server-log only; the player gets native's response, which is **nothing** |
| `StartBuildIfLegal` resolves the target asteroid across every sector without `SeenBySide`, so the leader can scan the whole map | **Yes** | Fixed by a fog-of-war gate: only a rock the side has seen is a legal target (port hardening, not native) |
| A rock an enemy station replaced is reported as "not found in any sector" | **Yes** | Fixed with native's Imago decoy (buoy/travel objective + `"Affirmative."`) |
| The direct NPC order path requires `IsSideLeader` while the native drone-chat path needs no leader | **Yes, as an internal inconsistency** — but native has **no** leader rule on either path (`CommandToDrone` gates only command id + same side; `FM_CS_ORDER_CHANGE` resolves any same-side ship) | Fixed by matching native: the port-added `IsSideLeader` gate (P653 H-18) is removed; both paths now give every same-side teammate the same authority |
| Native's `CHAT_INDIVIDUAL_ECHO` to the Command/Charlie/Delta/Echo wings is missing | **Yes** (P469 had it as a named gap) | Implemented — cheap, in `CommandToDrone` |
| Cross-sector NPC constructor orders are refused although `SetBuilderBuild` is travel-based | **Yes, a pre-existing parity gap** | **Not changed.** It is the P422/P425 same-sector simplification, not an oracle; making NPC builders warp would change legitimate play (bots/showcase). The refusal is now silent, so it cannot be probed. Named gap in `docs/parity/p1032-build-order-oracle.csv` |

## 2. Root causes and fixes

Line numbers for root causes are the pre-change tree; fix locations are post-change.

1. **`RefuseBuild` turned one order into a whole-map oracle.**
   `RefuseBuild` (`src/Allegiance.Server/Server.ShipCommands.cs:2434-2444` before the change)
   built a private `ChatMessage` with `"Negative. " + reason` and sent it to the
   commander. Every reason was a verbatim server diagnostic:
   `BUILD of '<station>' (AabmBuild 0x8) refused at asteroid 7103 (cap 0x100) — capability mismatch`
   (the exact `AabmCapabilities` bitmask), `... is not in builder ship N's sector '<name>'`
   (an unseen sector's name and membership), `... is already under construction`
   (an unseen enemy builder's activity) and `... not found in any sector` (the rock was
   consumed out of sight). `StartBuildIfLegal` (`:2335-2432` before) answered every one
   of those questions because it resolved the rock with `FindAsteroidById` across
   **all sectors** and checked capability *before* any sector test.
   *Fix:* `RefuseBuild` (`Server.ShipCommands.cs:2563`) is now a
   `ConsoleLog.Write` line only. Native sends no chat for an illegal drone order:
   the `SendChat(..., voNegativeSound, "Negative.")` in `CommandToDrone` is
   commented out (`FedSrv/FedSrv.CPP:3286-3291`) and the client-side twin that the
   P422/P425 docs cited (`clintlib/clintlib.cpp:2266-2272`) is inside a comment
   block that opens at `clintlib.cpp:2174` — dead code, as re-verified here.

2. **Fog-of-war gate in the shared legality body.**
   `StartBuildIfLegal` now resolves the rock and then requires
   `rock.SeenBySide(target.SideId)` **before** the capability, sector and
   commitment answers (`Server.ShipCommands.cs:2493-2498`). An unseen rock is
   refused exactly like a nonexistent one, and the refusal is server-side only.
   Native resolves the target with `pMission->GetModel(OT_asteroid, oid)` over the
   whole mission (`FedSrv/FedSrv.CPP:3211`) with no visibility test — so this is a
   **deliberate port hardening** (fog-of-war), not native parity. It changes no
   legitimate client: a client can only target a rock it has been told about, and
   every port flow that legitimately orders a build (client order resolver, bots,
   showcase build presets, bucket-spawned constructors) orders a rock its side has
   already seen. Existing fixture rocks that were placed without a visibility sweep
   now reveal themselves (see §4).

3. **Native's decoy for an asteroid an enemy station replaced.**
   `ArmDecoyBuildAtReplacedRock` (`Server.ShipCommands.cs:1808`) is the port of
   Imago's guard (`FedSrv/FedSrv.CPP:3213-3235`): when the id resolves to no live
   asteroid, scan every station for one whose `ConsumedAsteroid` is that rock and
   whose side is not the drone's own; arm a targetless `GotoOrder` at the station's
   position (native's `c_buoyWaypoint`/`c_buoyCluster` buoy) and accept the order
   anyway (`bBarelyLegal`, `FedSrv/FedSrv.CPP:3252`). It is called from
   `CommandToDrone` before `LegalCommand` (native's own site) and from
   `StartBuildIfLegal` for the port-added direct order path. The commander receives
   `"Affirmative."` — never a hint that the rock is gone — and the constructor flies
   to where the rock used to be.

4. **Authority aligned with native.**
   `HandleDirectNpcBuildOrder` (`Server.ShipCommands.cs:2375`) no longer requires
   `IsSideLeader`. Native's rule for commanding a constructor is the drone-chat
   path's: `CommandToDrone` gates only `commandID != c_cidNone` and
   sender-side == drone-side (`FedSrv/FedSrv.CPP:3190-3191`), and native's
   `FM_CS_ORDER_CHANGE` resolves any same-side ship for the sender
   (`pside->GetShip(pfmOC->shipID)`, `FedSrv/FedSrv.CPP:7271-7278`); there is no
   leader test on either path. The port's leader gate was P653 H-18's "modern
   authorization" on a path native does not have, and it could not hold while the
   native chat path was open to every teammate. This is **native parity**; it was
   called out in the F6 brief ("make both paths match native").

5. **Native wing echo.**
   `EchoDroneCommandToWings` (`Server.ShipCommands.cs:1834`) resends an accepted
   drone order with `ChatTarget.IndividualEcho` to same-side players in wing 0
   (Command) and wings 7/8/9 (Charlie/Delta/Echo), excluding the sender, forwarding
   to peers only (`ForwardChatMessageToWing`, `FedSrv/FedSrv.CPP:3327-3345`, called
   at `:3563-3570`). The P654 255-character relay cap is retained. No new
   configuration.

## 3. Native citations and parity verdicts

| Rule | Native | Verdict |
|---|---|---|
| No refusal text for an illegal drone order | `FedSrv/FedSrv.CPP:3286-3291` (commented), `clintlib/clintlib.cpp:2174-2274` (commented block) | **Native parity** (the port had fabricated text) |
| Ship-side authority for drone commands | `FedSrv/FedSrv.CPP:3190-3191`, `:7271-7278` | **Native parity** (leader gate removed) |
| Whole-map `GetModel` target resolution | `FedSrv/FedSrv.CPP:3211` | **Port hardening** (SeenBySide gate) |
| Replaced-rock decoy | `FedSrv/FedSrv.CPP:3213-3235`, `:3252` | **Native parity** |
| Wing echo of an accepted order | `FedSrv/FedSrv.CPP:3327-3345`, `:3563-3570` | **Native parity** |
| Builder travel before drilling | `Igc/shipIGC.cpp:2810-2827` | unchanged (port already travel-based for NPCs) |
| No cluster test in `c_cidBuild` | `Igc/shipIGC.h:1695-1707` | **partial** — cross-sector order still refused (named gap) |

Parity rows: `docs/parity/p1032-build-order-oracle.csv` (6 rows; validated by
`scripts/parity-scorecard.py`).

## 4. Tests

### New, unskipped (`tests/Allegiance.Core.Tests/P1032BuildOrderOracleTests.cs`, 11)

1. `Leader_build_order_at_an_unseen_wrong_type_rock_reveals_no_capability_mask` — the
   capability oracle (the old reply carried `cap 0x…`).
2. `Leader_build_order_at_a_nonexistent_asteroid_id_reveals_nothing` — the
   "consumed in fog" oracle.
3. `Chat_build_order_at_an_unseen_asteroid_reveals_no_sector_name` — the native chat
   path's sector-name oracle.
4. `Leader_build_order_at_an_unseen_asteroid_reveals_no_sector_name` — the same for
   the direct order path (the unseen gate runs before the sector gate).
5. `Seen_asteroid_build_orders_still_start_construction_from_both_paths` — legitimate
   play: a scouted rock still arms the constructor from both entry points.
6. `Non_leader_teammate_commands_a_constructor_on_both_paths` — native authority.
7. `Replaced_rock_gets_the_native_decoy_affirmative` — decoy reply + travel objective.
8. `Replaced_rock_of_the_drones_own_side_is_not_a_decoy` — native's own-side skip.
9. `Accepted_drone_command_is_echoed_to_the_command_wings` — `CHAT_INDIVIDUAL_ECHO`
   reaches Command and Charlie wings, not the other side, not the sender.
10. `Seen_rock_in_another_sector_is_refused_silently` — the port's same-sector build
    simplification still refuses, but the reply no longer names the sector.
11. `Rock_already_under_construction_refusal_is_silent` — the fourth old diagnostic
    (another constructor's commitment) stays server-side.

**Reproduction (before the fix, same file, unchanged):** 10 of 11 failed; the only
pass was `Seen_asteroid_build_orders_still_start_construction_from_both_paths` (the
legit play it pins was never broken). The failures carried the live leak texts on the
wire, among them `"Negative. BUILD of 'Garrison (Str)' (AabmBuild 0x8) …"` (the
capability mask), `"Negative. BUILD target asteroid 32700 not found in …"`,
`"Negative. BUILD target asteroid 7101 is already un[der construction] …"`,
`"Negative. BUILD target asteroid 7102 is not in bui[lder … sector] …"` (sector name)
and `"Negative. BUILD target asteroid 7104 not found in …"` (the replaced-rock case).
Summary and leak list: `docs/reports/P1032_assets/`. **After the fix:** 11 of 11 pass
(3× alone in the gate below).

### Existing tests updated because they encoded the old behaviour

- `tests/Allegiance.Core.Tests/DirectNpcBuildOrderLiveTests.cs` — the three refusal
  tests asserted the private `"Negative."` chat; they now assert silence (a directed
  chat is absent; ambient team chatter is ignored). One was renamed
  `..._is_refused_silently`.
- `tests/Allegiance.ClientCore.MonoGame.Tests/CommandedDroneBuildKeyLiveTests.cs` —
  `Live_client_illegal_drone_build_refusal_reaches_client_chat` →
  `..._is_silent`: no `"Negative."` on either path.
- `tests/Allegiance.Core.Tests/P653EconomyLoadoutIntegrityTests.cs` —
  `H18_Docked_and_nonleader_orders_fail_and_commander_build_completes` →
  `H18_Docked_orders_fail_and_any_teammate_build_order_completes`: the docked-order
  refusal is unchanged; the non-leader leg now asserts the native authority
  (accepted), and the leader leg still completes the build.
- Fixtures that place a rock without a visibility sweep now reveal it to the side
  (a client could only target a seen rock): `P652ConstructorBuildTests`,
  `P769BuilderCollisionBuildTests` (also covers `P817NativeP769BuilderCollisionBuildTests`),
  the two P653 H-18 tests, `StationBucketSpawnsConstructorLiveTests`, and the
  MonoGame `CommandedDroneBuildKeyLiveTests` boot fixture.

### Existing classes run that cover the touched paths

`DirectNpcBuildOrderLiveTests`, `PlayerBuildCommandLiveTests`, `P652ConstructorBuildTests`,
`P769BuilderCollisionBuildTests` + `P817NativeP769BuilderCollisionBuildTests`,
`P653EconomyLoadoutIntegrityTests`, `StationBucketSpawnsConstructorLiveTests`,
`P761bCommandRelayTests`, `SC1SkillTelemetryTests`, `P753SimUpdateTests`,
`AcceptRejectQueuedOrderLiveTests`, `LiveQuickChatOrderWireTests`,
`LiveQuickChatToDroneCommandTests`, `MultiHopDroneLiveTests`,
`P591LayerDroneLaysTests`, `P591cDroneRoutingTests`, `P725DroneChatterSeamTests`,
`P725DroneChatterWireLiveTests`, `P762DroneRuleTests`, `NpcBuilderDefaultOrderLiveTests`,
`NpcLayerDefaultOrderLiveTests`, `OrderChange*LiveTests`, `LiveChatWireTests`,
`DroneBucketSpawnsDroneLiveTests`, `P366LeaderBotCommandLiveTests`,
`P652EconomyDroneAiTests`, `P868DefaultOrderTests`, `P918CommandSlotsTests`;
MonoGame `CommandedDroneBuildKeyLiveTests`, `CommandOrderResolverTests`,
`P728CommanderTests`; ClientCore `DroneCommandsTests`, `LeaderBotCommandTests`.

### Focused edge cases

- Unseen vs seen vs nonexistent vs replaced: all four rock states are distinguished
  by observable server state (no effect/no target, travel target, decoy goto).
- Own-side replacement station: native's own-side skip is asserted.
- Wing echo: same-side only, sender excluded, wing filter (0 and 7 asserted, 8/9 same
  branch), other side untouched.

### Docs and ledger

- `docs/parity/p1032-build-order-oracle.csv` (new, validated).
- `GLOSSARY.md` — "Constructor build orders / fog-of-war (P1032)" under Server.
- `docs/TEST_FLAKES.md` — P1032 gate observations (three load-only Core failures,
  each 3× green alone; five slow offenders passed).
- `docs/reports/P425_COMMAND_OWNED_DRONE_BUILD_ORDER.md`,
  `docs/reports/P469_QUICKCHAT_ORDERS_TO_DRONES.md` and the P653 H-18 audit row are
  historical records of the earlier behaviour; sections 2 and 4 above supersede them
  (refusal ack removed, wing echo implemented, side-leader gate removed).

## 5. Config and limits

No numeric limit, no `server.json` setting and no environment flag are introduced.
The fog-of-war gate and the silent refusal are correctness rules (an oracle must not
exist in either configuration), so they are unconditional. The wing echo reuses the
existing P654 255-character relay cap.

## 6. Gate

Light gate, per the lane rules (builds with `--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`):

1. **Whole-solution build** — `dotnet build AllegianceCore.slnx -c Debug`: **0 warnings,
   0 errors**.
2. **Tools** — `tools/build-tools.sh`: **all 140 tool projects built**, 0 warnings.
3. **New tests 3× alone** — `P1032BuildOrderOracleTests` (11): **11/11 passed in each of
   three isolated runs**. The affected existing classes (55 tests across
   `DirectNpcBuildOrderLiveTests`, `PlayerBuildCommandLiveTests`,
   `P653EconomyLoadoutIntegrityTests`, `StationBucketSpawnsConstructorLiveTests`,
   `P652ConstructorBuildTests`, `P769BuilderCollisionBuildTests` +
   `P817NativeP769BuilderCollisionBuildTests`) plus the 11 new tests ran
   **66/66 passed** after the last fixture edit. The MonoGame `CommandedDroneBuildKeyLiveTests` + `CommandOrderResolverTests`
   + `P728CommanderTests` ran **29/29**, and the broader drone/chat/order sweep
   (20 classes) **200/200**.
4. **One fast suite** — `tools/run-tests.sh --tier fast`: **14,743 tests, 558.6 s,
   FAIL (environment)**. Every failure is a known load-only flake on a path this task
   does not touch; each passed 3/3 in its own process and is ledgered in
   `docs/TEST_FLAKES.md` (§"P1032 gate observations"). Five executions exceeded the
   10 s Fast guard and all passed. No task test failed.
5. **AOT publish** — `dotnet publish src/Allegiance.Server -c Release -r linux-x64
   --self-contained -p:PublishAot=true`: succeeded, native `Allegiance.Server` binary
   produced (21.9 MB), no IL2026/IL3050 warnings.
6. **Showcase never-parks** (load-sensitive, bots/constructors affected) — 5/5 passed.
7. **Docs checks** — `tools/check-glossary-links.py` OK (891 paths);
   `scripts/parity-scorecard.py docs/parity/p1032-build-order-oracle.csv` validated
   6 rows (90.91 % weighted; the `partial` cross-sector row is intentional).

## 7. Results — test counts

| Set | Passed | Failed | Skipped/NotExecuted |
|---|---|---|---|
| New `P1032BuildOrderOracleTests` (3× alone) | 11, 11, 11 | 0 | 0 |
| Affected build classes + the new tests, one run (66) | 66 | 0 | 0 |
| MonoGame command/build classes | 29 | 0 | 0 |
| Drone/chat/order sweep (20 classes) | 200 | 0 | 0 |
| Reproduced pre-fix run (same 11 tests) | 1 | 10 | 0 |
| Fast suite — Core main | 6,546 | 3 (load flake, 3× green alone) | 3 |
| Fast suite — MonoGame | 5,471 | 0 | 4 |
| Fast suite — ClientCore | 2,449 | 0 | 0 |
| Fast suite — Lobby | 193 | 0 | 0 |

## 8. Unfinished / named gaps

- **Cross-sector NPC build travel** (native `Igc/shipIGC.h:1695-1707` has no cluster
  test; the port keeps its same-sector simplification). It is now a silent refusal, so
  it cannot leak; making constructors warp is a behaviour change out of F6's scope.
- The dry-run/build refusal reasons keep their full detail **in the server log**
  (intended; operators need them).
- No config flag or limit was added; there is nothing for the rig to A/B.

## Native intent

Native's constructor order is a **drone chat** (`CHAT_INDIVIDUAL` with a command id,
`CommandToDrone`) that any same-side player can send; it resolves the target model
across the mission, has no leader check, sends no refusal text, and deliberately
hides a rock an enemy station replaced by flying the constructor at a decoy buoy so
the drone cannot give away fog-of-war information (Imago 7/10, `FedSrv.CPP:3213-3235`).
An accepted order is echoed to the command wings as `CHAT_INDIVIDUAL_ECHO`
(`FedSrv.CPP:3563-3570`).

## Modern approach

The port keeps native's player-visible behaviour (no refusal text, decoy
"Affirmative.", same-side authority, wing echo) and adds one server-side rule
native cannot express: the target asteroid must be `SeenBySide` for the
constructor's side, so a manipulated client cannot use a build order as a
whole-map asteroid-capability oracle. Every refusal reason still exists — in the
server log, where it belongs — and the decoy is the port's existing travel
objective (a targetless `GotoOrder`) instead of native's transient buoy object, so
no station identity ever rides the order.
