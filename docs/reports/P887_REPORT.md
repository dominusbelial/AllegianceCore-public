# P887 — the rig's winning performance settings become the server defaults

Date: 2026-10-29 · Worktree of master · Task brief: `P887_BRIEF.md`

The 200v200 rig A/B batches picked the configuration that is now **the performance
baseline**: every future change is measured on top of it. Three parts were already code
defaults (`broadcast.immutableSnapshot` + `encodeOnce` — `ServerConfig.cs:758,763`;
`sim.nativeGotoPlan` — `SimConfig.cs:10`; server GC —
`Allegiance.Server.csproj:23`). This task flips the remaining three and updates the
docs. Nothing else about the code paths changes; config and env overrides keep working
exactly as before (env wins; an explicit `"port"`, `false` or `"thread-per-peer"` still
selects the old path).

## 1. Defaults flipped (file:line, re-grepped on this worktree)

| Setting | Old default | New default | Resolution site |
| --- | --- | --- | --- |
| `broadcast.parallelBuild` | `false` | **`true`** | `ServerConfig.cs:799` (property default); `Server.Lifecycle.cs:1602` (`bc?.ParallelBuild ?? true`) |
| `broadcast.shipUpdateModel` | `"port"` | **`"native"`** | `Server.Lifecycle.cs:1771` (fallback `"native"`); `Server.cs:290` (field placeholder) |
| `transport.mode` | thread-per-peer | **`"pool"`** | `Server.Lifecycle.cs:1641` (fallback `"pool"`); `Server.cs:311` (field placeholder) |

Also moved with them:

- The `"default"` alias in `ParseShipUpdateModel` / `ParseTransportMode`
  (`Server.Lifecycle.cs:1795-1806`) now maps to `"native"` / `"pool"` (it previously
  mapped to the old path, which would have contradicted the new default).
- The `Server` field placeholders (`Server.cs:267,283,290,311`) match the new defaults;
  they are always overwritten by the boot resolvers before use (`Server.Lifecycle.cs:168-176`
  resolve order is unchanged).
- `transport.maxConnectionsPerIp` stays **64** (`Server.Lifecycle.cs:1677`) — untouched,
  per the brief; only the rig raises it.

Every doc comment that said "the default is …" was updated: `ServerConfig.cs`
(`BroadcastConfig` class doc, `ParallelBuild`, `ShipUpdateModel`, `TransportConfig`
class doc + `Mode`, `ServerConfig.Broadcast`/`ServerConfig.Transport` property docs),
`Server.cs` (all four field docs), `Server.Lifecycle.cs` (both call-site comment blocks
and the three resolver summaries), and `TcpTransport.cs` (`TransportMode` enum doc; the
`DefaultMode` test-seam comment now clarifies it is a transport-test fallback,
independent of the server default). `[boot]` lines: none of them said "(default)"; the
serial/port branches of the two printouts were reworded to say "selected explicitly".

## 2. Tests touched (and why)

Fixed because the default change made them run a different code path than they assert:

| Test | Change | Why |
| --- | --- | --- |
| `NativeShipUpdateModelTests.Ship_update_model_defaults_to_port` → `Ship_update_model_defaults_to_native` | renamed; now asserts `_nativeShipUpdates == true` / `_shipUpdateModel == "native"` on a default boot | the contract test pins the default; the default changed |
| `ParallelBroadcastByteIdentityTests.Parallel_flag_defaults_off_and_config_binds` → `Parallel_flag_defaults_on_and_config_binds` | renamed; asserts `_broadcastParallelBuild == true` on a default boot | same, for `parallelBuild`; the byte-identity assertions themselves are mode-pinned via the test seam and unchanged |
| `TcpTransportSendWorkerPoolTests.Default_server_boot_stays_thread_per_peer` → `Default_server_boots_in_pool_mode` (+ new `Explicit_thread_per_peer_config_boot_stays_thread_per_peer`) | default boot now asserts pool mode with `ProcessorCount` workers; the old thread-per-peer assertion moved to an explicit-`"thread-per-peer"` boot test | the old test pinned thread-per-peer as the default; the coverage (explicit override selects the old path) is preserved, not weakened |
| `TcpTransportSendWorkerPoolTests.Boot_line_prints_the_active_transport_mode` | default-boot assertion now expects `[boot] transport mode = pool (N workers)`; thread-per-peer line asserted on an explicit override boot | same |
| `P884RotationRetentionTests` (ClientCore) | pins `Broadcast = new() { ShipUpdateModel = "port" }` | genuinely exercises the port teammate latch (per-tick posBatch retention across game-over rotation); the native model suppresses the posBatch stream |
| `TeammatePositionBroadcastTests` (2 tests) | pins `ShipUpdateModel = "port"` | asserts the per-tick teammate `SINGLE_SHIP_UPDATE(78)` posBatch stream |
| `OwnShipCoalesceKeyTests` (2 tests) | pins `ShipUpdateModel = "port"` | asserts the teammate posBatch coalesced frame + own-ship key distinctness against it |
| `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live` | pins `ShipUpdateModel = "port"` | scenario 7 asserts the P209 orientation dirty-gate on the posBatch `SINGLE_SHIP_UPDATE` stream |
| `ShipUpdateBudgetLiveTests` (3 tests) | pins `ShipUpdateModel = "port"` | P219 asserts the LEGACY folded budget (exactly 4 HEAVY entries incl. the folded LIGHT tier); the native ranked-pose path keeps native's separate HEAVY + LIGHT messages (2 + 2 per tick) |
| `P803TurretGunnerLoopbackLiveTests` (MonoGame) | pins `ShipUpdateModel = "port"` | asserts the gunner's HUD model inputs from the **docked** parent's pose. Observation: under the native ranked-pose budget a docked ship is not a cluster candidate (`Sector.cs:740` — "docked ships are not physically in the cluster") and the posBatch stream is suppressed, so a docked parent's pose genuinely does not reach the boarded gunner's client. Flagged below; not changed here (it would alter the rig-baseline wire behaviour) |
| Doc-comment-only: `TcpTransportGreetTimeoutTests`, `P813dReliableTierTests`, `TcpTransportSendHardeningTests`, `TcpTransportLivenessPolicyTests`, `TcpTransportSendDropSafetyTests`, `TcpTransportCoalesceAndReasonTests`, `TcpTransportPriorityLaneTests` | dropped the stale "(default)" wording on the thread-per-peer parameterized suites (they construct transports with an explicit mode) | no functional change |

Pinned tests were each run **3× alone**: all green (29/29 ×3 for the Core set; P803 3×;
P884 3×).

## 3. Configs / docs changed

- `src/Allegiance.Server/server.example*.json`, `server.lobby-example.json`, `docker/*.json`,
  `docker/*.yml`: verified clean — none of them set `parallelBuild`, `shipUpdateModel`
  or `transport.mode`, so nothing silently selects the old path. No changes needed.
- `README.md` — "Recommended settings" rewritten: the 200v200 block now only needs
  `maxConnectionsPerIp` (+ `maxConnections`) and the map; new **"Performance settings
  (the P887 baseline)"** table (setting / default / what it does / rig evidence:
  immutableSnapshot+encodeOnce b42 vs b39 — clash-peak tick 44 vs 71–80 ms, overruns
  ~1k vs 22–40k; parallelBuild P603 — byte-identical, clamps to cores−1;
  shipUpdateModel native P604 — native parity, ~1/5 bandwidth at 400 players;
  nativeGotoPlan b52/b55 — perf neutral 16.2 ms, more station kills; pool P605 —
  workers ≈ core count; baseline b55 — median tick 16.2 ms, window p95 ≈ 20 ms,
  0 overrun windows, worst GC pause 36 ms at 394 connections; P863 b50 vs b49 —
  35.8 → 16.3 ms, TiDi windows 5.6 % → 0.6 %). States that new work is A/B'd against
  this baseline. Then the options that stay **off**: `broadcast.interestTiers`
  (+40 % bandwidth, +6–18 ms peak tick for halved update age, b39–b43),
  `sim.nativeBroadcastOrder` (SIM-0141; +7 ms/tick median on b51, re-test after
  P863b, adopt if < ~2 ms), `receive.decouple` (local A/B only, P673, never A/B'd
  on the rig).
- `docs/architecture/SERVER_PIPELINE.md` — the transport summary now names `pool` as
  the default (P887) with thread-per-peer as the explicit override; the Ph.8 rollback
  line no longer claims thread-per-peer is the TCP default.
- `docs/TEST_FLAKES.md` — P887 gate entry added (below).

## 4. Gate results

| Gate | Result |
| --- | --- |
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** (after every change; re-run 3×) |
| `tools/build-tools.sh` | **PASS** — all 94 tool projects built |
| Server Native AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | **clean** → `.scratch/p887/aot/` |
| Touched tests 3× alone | **PASS** — Core set (TeammatePositionBroadcast, OwnShipCoalesceKey, LiveTurretGunnerBinding, ShipUpdateBudget, ParallelBroadcastByteIdentity, NativeShipUpdateModel, TcpTransportSendWorkerPool) 29/29 ×3; P803 3×; P884 3× |
| ONE `tools/run-tests.sh --tier fast` (`tiers-20261029T181742.259466Z`) | 11,726 executions, 556.8 s. **6 non-task failures, all load flakes, each passed 3/3 alone** (ledgered: AsteroidCombat friendly-missile, NpcPilotTypeGate standby, P864d team-lobby relay, DockDocking (P872), P871b create-game, P804ClientLead (ledgered)). `NpcWingmanDefendLiveTests` failed both sweeps **and 3/3 alone** — the pre-existing P872/P882a baseline failure, identical on clean HEAD, not a P887 path. One calibrated-limit offender (P777WarpExitWobble 11.8 s, passed) — pre-existing timing variance. |
| `tools/run-training-tests.sh --no-build` | **PASS** — ClientCore 75/75, MonoGame 58/58 (Fast + Live, incl. Nanite completion) |
| Showcase never-parks 5× (P730 two-match + P700 never-parks) | 9 paired runs: **7 clean 2/2**; runs 2 and 4 failed one of the pair with no detail captured — the already-ledgered host-load class for both tests (9–11 min runs vs the ~1m50s base rate). **3/3 immediate isolated re-runs passed 2/2.** No bot flight/decision code changed. |
| `tools/check-glossary-links.py` equivalent | GLOSSARY not touched (no new system/term/pattern); no doc paths added |

Sweep-1 details (`tiers-20261029T175614.547114Z`, before the test pins): the 11 failures
it reported were the default-change casualties fixed in §2 plus the same load flakes.

## 5. Native-intent footer

- `broadcast.shipUpdateModel: "native"` = **native parity** — remote-ship positions ride
  the native deviation-ranked HEAVY/LIGHT_SHIPS_UPDATE budget
  (FedSrv.CPP:3923-3950,4141-4188; see docs/reports/P604_NATIVE_SHIP_UPDATE_BUDGET.md).
- `broadcast.parallelBuild` and `transport.mode: "pool"` = **modern, behaviour-preserving**
  — per-peer payloads are byte-identical (P603 proof) and per-peer send order is
  preserved (P605); they change how fast bytes are produced and drained, never which
  bytes.

## 6. Open observations (not fixed here)

1. **Docked parent pose under the native model.** With `shipUpdateModel: "native"`, a
   DOCKED parent ship's pose does not reach a boarded gunner's client (docked ships are
   not cluster candidates, `Sector.cs:740`; the posBatch stream that used to carry it is
   suppressed). Native excludes docked ships from `cluster->GetShips()` too, but the
   native client runs a local IGC sim; our renderer client's HUD reads `parent.Latest`
   (P803). The rig has run native since b4x; whether real turret gameplay ever hits the
   docked case needs a rig check. P803 is pinned to `"port"` meanwhile.
2. **Rig confirmation pending.** Claude will confirm on the rig (192.168.0.221) with the
   explicit settings removed from the rig's `server.json` — a fresh default boot must
   show `[boot] broadcast.parallelBuild = true …`, `[boot] broadcast.shipUpdateModel =
   native …`, `[boot] transport mode = pool (N workers)` and behave as b55. **Not claimed
   here** — this task ran no rig (none was contacted, per the brief).
