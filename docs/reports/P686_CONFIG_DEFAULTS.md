# P686 — preserve JSON configuration defaults

Base: `integ7` (`6df4e252e0cbbb9ebc22f6d2587098a66bd1d9cc`). Changes remain uncommitted in the working tree; no commit or push was made.

## Fix and audit

Changed all 37 init-only properties in `ServerConfig.cs` (GameConfig, MapGenConfig, LobbyConfig, and ServerConfig) and all seven properties in `TreasureParameters` to ordinary public getters/setters. The property names, types, initializer values, JSON naming/options, nullable opt-ins, and object-initializer usage stay compatible. Existing settable configuration classes already preserve their initializers. No source-generator option or reflection fallback was introduced into production.

Repository-wide searches for `JsonSerializable`, `JsonSerializerContext`, JSON deserialization, and initialized init-only properties found **one production source-generated context**, `ServerJsonContext`, with 11 attributed roots. Its entire transitive graph is guarded, including the implicitly generated contracts for SimConfig, ReceiveConfig, AutoNextMatchConfig, ProfilerConfig, BroadcastConfig, TransportConfig, TreasureParameters, nested collections, and the lobby/egress wire records.

Other JSON boundaries were audited:

- Lobby: LobbySettings uses setters and ASP.NET configuration binding; RegisterRequest uses setters. Positional response/listing records have no initialized defaults to lose.
- Client settings: ConnectConfigValues and ConnectProfile use setters. SavedServer, ServerListing, inspector JSONL records, and CommandGridPreference.Settings are positional records without initialized property defaults. Keymaps/art overrides deserialize dictionaries. GiantObjectConfig and ShipCatalogue parse JsonDocument explicitly and supply their defaults themselves.
- MCP: options come from CLI/environment and tool arguments are read from JsonElement/dictionaries with explicit fallbacks. Incoming worker JSON uses WorkerReply, WorkerState, CoordinationState, and CoordinationDecisions and their nested positional records; no initialized init-only defaults were found there. The active-bot snapshot's `State = "active"` belongs to an output-only record, consumed as raw JsonElement on the supervisor.
- PerPeerRateLimiter is constructed in code and is absent from every JSON graph. Its 5000 ms / 5 / 100 init defaults remain immutable after construction. MapGenParams is also constructed explicitly by ToCoreParams, rather than deserialized. Simulation entities, wire-codec objects, InboundTrace, and rendering resources are outside configuration JSON contracts.

## Permanent regression guard

`tests/_shared/JsonDefaultsGuard.cs` discovers contexts and their `[JsonSerializable]` constructor arguments by reflection, then traverses the **production JsonTypeInfo graph**, rather than a copied list of server types. For each object contract it compares deserialization of `{}` with a newly constructed instance. Public properties are compared recursively, sequences by contents, and dictionaries by keys/values. Positional records use their actual constructor with default arguments, honoring optional parameter defaults.

For every object/collection section, the guard also serializes the parent's default siblings, omits that section, and compares the entire parent with a fresh instance. It separately materializes `{}`, `[{}]`, or a dictionary entry to exercise optional/null children and empty collections; their element contracts are visited even when the constructor's collection is empty. A missing raw RPC JsonElement stays absent because its CLR `Undefined` default has no JSON representation.

Tests cover every server context; lobby JSON plus empty/absent ASP.NET configuration sections; client seed/profile/grid settings using their production options; and all incoming MCP worker graphs with reflection serializer defaults. Existing treasure tests now compare every treasure default and the game admission/development defaults. Explicit false, zero, -1, nullable map-generation overrides, and the 32767 imbalance sentinel are independently checked, so legitimate explicit values are not replaced by defaults.

Regression proof: temporarily restored only the two production definition files from HEAD and rebuilt Core.Tests. The new/strengthened regression group produced **4 failures / 3 passes**: the graph guard caught `ServerConfig.Games = null`, the treasure cases caught a zero rate/false developments, and the rig boot caught false developments. Both production files were restored and the final solution rebuilt before final verification. Mutation evidence: `/tmp/p686-tests/mutation.trx` and `/tmp/p686-mutation-test.log`.

## Effective behavior and native references

Native citations below refer to `~/Development/Allegiance/src/` on this machine.

| Omitted JSON value | Before → after | Runtime meaning / reference |
| --- | --- | --- |
| `games[].allowDevelopments` | false → true | Mission development/research and payday income enabled. Native constructor: `Igc/igc.h:1245`; development setup: `FedSrv/fsmission.cpp:1948`; payday gate/accrual: `FedSrv/fsmission.cpp:2520-2550`. |
| `games[].maxPlayersPerTeam` | 0 → 256 | Server.SideIntegrity previously clamped the accidental zero to **one player/team**; normal admission now permits the intended 256. **256 is the port's existing P655 capacity, not a native constant**: native initializes 100 at `Igc/igc.h:1334`, validates 1..200 at `Igc/igc.h:1452-1453`, and checks capacity at `FedSrv/fsmission.cpp:4633-4635`. |
| `games[].maxTeamImbalance` | 0 → 1 | Correct effective config; admission already clamped zero to one, so ordinary balance behavior does not change. Native default: `Igc/igc.h:1303`; started-stage check and 0x7fff bypass: `FedSrv/fsmission.cpp:4614-4626`; authenticated bot bypass: `FedSrv/fsmission.cpp:4637-4642`. Explicit 32767 still binds. |
| `treasure.playerSectorTreasureRate` inside a present object | 0 → 0.2/60 per second | Restores home-sector treasure accrual. Native default: `Igc/igc.h:1321`; accumulation: `FedSrv/fsmission.cpp:2563-2566`. |
| `treasure.neutralSectorTreasureRate` inside a present object | 0 → 0.5/60 per second | Restores neutral-sector treasure accrual. Native default: `Igc/igc.h:1322`; accumulation: `FedSrv/fsmission.cpp:2563-2566`. |
| All four treasure start/regenerate set IDs inside a present object | 0 → 1 | Restores the intended loaded-core treasure sets for initial population and regeneration. Native defaults: `Igc/igc.h:1287-1290`; regeneration selection/use: `FedSrv/fsmission.cpp:2573-2576`. |
| `treasure.allowDevelopments` inside a present object | false → true | Removes the second accidental payday/cash-treasure suppression gate. This port projection shares native's mission development switch, whose default is true (`Igc/igc.h:1245`; payday gate `FedSrv/fsmission.cpp:2520`). |
| `lobby.heartbeatIntervalSeconds` | 0 → 25 | Correct effective config/logging. LobbyHeartbeat already substitutes 25 for nonpositive input, so timer cadence remains 25 seconds. This HTTP heartbeat is a port setting. |
| `games`, `civIds`, `sideNames` | null → empty collections | Restores safe enumeration and intended validation instead of null dereferences for incomplete JSON. |
| Game name/map, MapGen type, lobby URL/displayName/publicAddress | null → empty strings | Restores declared string contracts and existing validation/error/fallback behavior. Lobby payload string fields now keep their intended empty defaults. |

The already-settable GameConfig.Treasure preserves its object initializer when the entire section is absent. The seven nested treasure defaults were lost when the object was present but individual fields were omitted. LiveSettings and Sim were already settable; their automatic next-match/dilation and crowding defaults are covered without changing them.

Call-site review found no dependency on the accidental zeros requiring a production call-site change. Capacity zero is explicitly clamped to one, not unlimited; imbalance zero is already clamped to one; heartbeat nonpositive input already falls back to 25. Explicit treasure rate zero still disables regeneration, and explicit -1 set IDs remain valid input. The lobby wire payload's separate `MaxPlayers = 0` unknown/unbounded advertisement is unrelated and unchanged. Nullable configuration options remain null unless supplied; off-by-default opt-ins retain their existing values.

## Rig-style boot smoke

The test writes the exact brief's rig-style JSON with only `artPath` resolved to the configured local artwork corpus. It calls **Server.Boot(path, [])**, loads the real PCore001.igc and m2xspiderw.igc, binds an ephemeral TCP port with pool transport, and checks the assembled mission and loaded config:

- mission/game developments true; max players 256; imbalance 1;
- every mission TreasureParameters property matches `new TreasureParameters()` and references the game's loaded treasure settings;
- heartbeat 25; omitted lobby display/public address empty;
- pool transport, max connections per IP 512, crowding disabled;
- time dilation auto; auto-next-match enabled with 30-second intermission.

It then starts the mission and advances through its first payday, asserting that every side's treasury increases. This checks the gameplay consequence of both development defaults, beyond just inspecting properties. The server is disposed and the temporary JSON removed.

## Verification

The final build command was `NUGET_HTTP_CACHE_PATH=/tmp/p686-nuget-http dotnet build AllegianceCore.slnx --no-restore --nologo`: **0 warnings / 0 errors**. Restore used the same writable cache; the initial default cache path was read-only in this sandbox. No package/audit checks were disabled.

The focused matrix contains **36 methods / 45 data cases**, each invoked **3× alone** using an exact FullyQualifiedName filter: **135 passing case executions, 0 failed, 0 skipped**. It includes all new/modified tests, existing JSON-loader checks, heartbeat wire contracts, side capacity/balance tests, and the complete P663 treasure/payday class. Ten initial unmatched filters (file/class name differences) are explicitly labeled `unmatched-filter` in the ledger and contribute no passing tests to these totals; their corrected method names each passed 3/3 before full suites started.

Every invocation, exact filter, TRX/log path, raw counters, and failure message is preserved in [P686_RESULTS.json](P686_RESULTS.json).

The three full assemblies ran **unfiltered and sequentially** after the final build, including their live/loopback tests. `ALLEGIANCE_LIVE_P660_SHOWCASE=1` enabled the local 15v15 MCP next-match showcase, which passed in ClientCore. No live rig / 221 / 200v200 run was attempted.

| Assembly | Passed | Failed | Skipped | Wall seconds |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests | 4543 | 0 | 0 | 530.346 |
| Allegiance.ClientCore.Tests | 997 | 0 | 0 | 400.013 |
| Allegiance.ClientCore.MonoGame.Tests | 2186 | 0 | 3 | 266.073 |

The three MonoGame skips are the existing optional Meshy-pack checks; that external pack is unavailable. The real artwork/native corpus was available and the rig-style boot smoke executed.

All full suites completed with **zero failures**. No load-flake classification or TEST_FLAKES.md update was needed. `git diff --check` passed.
