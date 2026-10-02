# P486 — MCP commander tools: invest in drones, list drones, order drones (native commander model)

Status: landed on `master` (not pushed).
Scope: the **tools**, not an agent loop, no server change, no drone-AI change.
Concept (per the brief): the server's native drone AI keeps flying the drones;
MCP acts as the commander through a **harness commander bot** — a seated
`mcp-` player on the chosen side using the P473 grant money. A wire-bot can't
fly a miner hull anyway (P470 made drone hulls group id −1; P473's harness
deliberately does not bypass that gate, `Server.ShipCommands.cs:~2350`), so
everything rides the real wire as an ordinary player.

## What was added

Four files in `tools/allegiance-mcp/` + one test file:

- `tools/allegiance-mcp/DroneCommands.cs` (new) — pure, dependency-free
  translation: command name → native `OrderChange.Cid*` (igc.h:897-912) and
  target descriptor → `(IgcObjectType, id)`. Unit-tested with no server.
- `tools/allegiance-mcp/BotManager.cs` — a per-side cached **commander peer**
  (`EnsureCommander(side)`, `~:390`): a `ConnectionSession` that logs on,
  seats on the side, readies (P485), and — because it is an `mcp-` connection —
  is granted the P473 money at seat (`Server.Lifecycle.cs:80`). It does NOT buy
  a loadout (only a seated `mcp-` peer with a ship id is needed). Three tool
  methods: `Invest`, `ListDrones`, `OrderDrone`.
- `tools/allegiance-mcp/McpTools.cs` — the three MCP `Tool` definitions +
  dispatch + arg parsing.
- `tests/Allegiance.ClientCore.Tests/DroneCommandsTests.cs` (new) — 17 pure
  tests of the command/target translation (fail-first proven, see below).

### `invest` `{side, bucket, amount?, observe_seconds?}`

Resolves a real bucket from the side's synthesized catalog
(`BucketCatalog.Describe`, `Bucket.cs:480` — the SAME id numbering the server
baked into its list at `Mission.Assemble`), fed the commander's real
`DevelopmentTechsFor(side)` (CREATE_BUCKETS) and GA. Development buckets are
refused (they complete research, not drones). Donates through
`ConnectionSession.SendBucketDonate` (`ConnectionSession.cs:280`, real
`FM_C_BUCKET_DONATE` 64). Reports observed progress: wallet before/after
(MONEY_CHANGE), invested money (BUCKET_STATUS), and — when the bucket
completes — the new drone that appeared on the wire (a NEW same-side NPC with
the bucket's drone hull).

Observability note surfaced in the result: the client only learns a bucket's
invested **time** from BUCKET_STATUS broadcasts, which the server sends on a
donation, not periodically — so build-time accrual is not visibly ticking
client-side. Completion is therefore signalled by the drone APPEARING (money
full AND server build-time elapsed), not by the observed time reading.

### `list_drones` `{side, type?}`

Every same-side NPC the commander observes on the wire: ship id, drone type
(from its real hull against the core's drone-type roster), hull id, sector,
position, state, character name — where observable. The drone discriminant is
`Player == null` (a real player carries a PlayerInfo/PLAYERINFO 10; an NPC does
not — verified in `Server.Broadcast.cs:600-615` for players vs the NPC export
at `:755`). The tool explicitly says what a player's view CANNOT see: cargo/ore
and the drone's internal order are not on the peer ship wire.

### `order_drone` `{droneId, command, target}`

Issues a real order through the P469/P457 path: an Individual quick-chat
(`FM_CS_CHATMESSAGE` 23, `ConnectionSession.SendChat` with `commandId`,
`oidTarget`, `otTarget`, `:193-216`) naming the drone as recipient — exactly
what the server's `CommandToDrone` consumes (`Server.ShipCommands.cs:1553-1646`).
Returns the drone's observed reaction: the server's "Affirmative." reply
(`SendDroneAffirmative`, `:1906`) in the commander's chat stream, plus any
sector/state change.

## Live demonstration (real server, debug mode + testHarness grant)

Ran the real `Allegiance.Server` (temp config outside the repo) on a spare
port with `liveSettings.debugMode=true` and `testHarness{money:1000000}` on
`m2xspiderw.igc`, then drove the three tools over the MCP stdio protocol.

1. **invest** `.Miner` (drone-type bucket, price 3500, build 90s):
   resolved to `BucketId 298 / BitDroneType`, donated through the commander's
   P473 wallet, observed `WalletBefore 1000000 -> WalletAfter` climbing
   (payday income accrued while the build ran), money full, and after the 90s
   build time elapsed the spawned **Miner 8105** appeared on the wire.
2. **list_drones** side 0 returned the two Miners (8096 initial, 8105 spawned
   by invest), type `Miner`, hull 3, live positions, state, and — over repeated
   calls — **positions changed** (real native AI flight:
   `[-2072,-2643,110] -> [-2486,-3230,160] -> [-2903,-3821,212]` over ~18s).
3. **order_drone** issued real P469 orders. A legal **ship-target GOTO**
   (`order_drone 8096 goto ship:8097`) returned `AffirmativeObserved=true`,
   `DroneReply="Affirmative."` — the P469 ack round-tripped to the commander.
4. **Mining trip + income** (server sim): the invested minerdrones flew from
   the side-0 home `Omega(1053)` to the reachable He3 sector `Alderan(1052)`
   via a real warp (`[warp] ship 8096 crossed from 'Omega' to 'Alderan' via
   warp 2153` — multi-hop reachability, the P484 result), mined He3 asteroids
   (`[mine] ship 8096 began mining asteroid 3199 'He1056'`, ore fraction
   depleting), and sold for real income: `[mine-sell] ship 8096 sold 142.5 ore
   at 7/u = 961.9 credits to side 0`, with the side treasuries and paydays
   accruing (`[payday] side 0 'Alpha': ... balance`). The commander's own
   wallet also rose during the invest window (PAYDAY credits) — an observable
   "wallet income" through the tools.

### Finding (honest): P469 as ported refuses asteroid/station/warp-target orders

The specific sub-step "order the miner to a specific He3 asteroid" is blocked
**server-side**, and fixing it is explicitly out of P486's scope ("no server
change"). Diagnosis, proven live:

- `ResolveChatOrderTarget` returns `model = null` for every NON-ship target
  (Station/Asteroid/Warp), only Ship targets resolve to a real `Model`
  (`Server.ShipCommands.cs:1504-1531`).
- `LegalCommand`'s first gate treats a null model as "target invalid" and
  allows only `CidDoNothing` (`:1660-1668`). So an `order_drone ... mine
  asteroid:<id>` (or goto/build to an asteroid/station/warp) fails
  `LegalCommand` and gets **no** "Affirmative." (the `[drone]` lines use the
  P318 verbosity-gated sink, so the refusal is only visible as the missing
  ack).
- Native resolves these targets to a real `pmodel` (`FedSrv.CPP:3197-3260`),
  so native's `LegalCommand` passes for them; the port's null-model collapse is
  a genuine divergence. A `donothing` chat order is likewise refused, by the
  separate chat command-range gate `[-1, CidMax)` (`:1566`), because
  `c_cidDoNothing = -3` is not a quick-chat command in native either.

The tool faithfully reports this: legal ship-target orders return the
"Affirmative." ack; asteroid-target orders return `AffirmativeObserved=false`
with a specific note (order may be refused / target unresolved). This is the
kind of divergence the brief's "say plainly what isn't observable / verify the
positive claims" asks to surface. Recommended follow-up: a small P-stage to
give `ResolveChatOrderTarget` a real target model for asteroids/stations/warps
(or exempt the null-model gate when `ot` names a non-ship object), so the
specific-asteroid mining order lands. The mining trip + income itself is fully
demonstrated by the native AI once the miner exists, independent of that.

## Verification

- **Fail-first:** `DroneCommandsTests` are red when the translation is wrong —
  proven by corrupting the `mine`→`CidMine` mapping (test went red) and
  reverting.
- **Whole-solution build:** `/home/domingo/.dotnet/dotnet build` →
  `0 Warning(s), 0 Error(s)`.
- **Full suite,** `scripts/run-tests.sh` sequential runner, 3 consecutive runs:
  my tests (17) green every run. The only interruptions are the documented
  live-wire / load flakes in `docs/TEST_FLAKES.md` —
  `FullSessionKillIntegrationTests...CrossSideKill` (1/3), `LiveRipcordTests...
  flying_in_sector` (1/3), `AutoReloadOnEmptyTests...RELOAD129` (1/3),
  `LiveCountdownStartPingTests.StartGame_owner_all_ready...` (1/3). Each
  confirmed **green in isolation** (Ripcord 3/3, AutoReload 1/1, FullSessionKill
  1/1, CountdownStartPing 1/1), i.e. environmental live-network timing, not a
  regression from this stage (all are in assemblies that do not reference the
  changed tool code).

## Deliverables / scope boundary

- Committed on `master`, not pushed (`git branch --show-current` → master).
- Three MCP tools through the harness commander; no server change; no drone-AI
  change; no vizfight change; no LLM commander loop (these are tools).
- This report: `docs/reports/P486_MCP_COMMANDER_TOOLS.md`.
