# P473 — test-harness bots survive the real economy (money/tech grant + loud purchase failures)

Date: 2026-10-01 · Branch: `master` (committed, not pushed)

## Summary

After P441/P443/P444/P468 made purchasing real (wallet debit, funds gate, no
debug bypass, native starting money, tighter tech gating), the wire-level test
harnesses (`allegiance-mcp` spawn/matrix bots and `vizfight` bots) were written
for the old free, ungated shop. Nothing checked what they actually got:

- `Allegiance.Bot` did not decode `BuyLoadoutAck` at all (only `ConnectionSession`
  did), so a Bot-based tool could not even see a refusal.
- `BotManager.SpawnBot` sent `SendBuyLoadout(...)` fire-and-forget and relied on
  "the probe streams the ship" as proof — which is exactly what a REFUSED
  purchase also does (the already-seated ship keeps streaming with its unchanged
  default hull). So the matrix silently flew default ships.

P473 adds a **server-side, explicit, off-by-default, prefix-scoped harness
grant** (a port extension — native has no money/tech cheat) and makes every
harness path **see and report** the real purchase result (granted loadout,
refusal = error, partial grant = warning). Never a silent success.

## Native reality (why this is a port extension)

Native has **no money or tech cheat**: `fCanCheat` only affects rank limits and
win checks (`FedSrv/fsship.h:337-339`, `fsmission.cpp:1877,3810`). So granting a
test harness money / tech is a deliberate port extension, and it is treated as
such: explicit in config, off by default, and gated to character names that only
a harness uses.

## 1. Config shape — `liveSettings.testHarness`

Added `TestHarnessConfig` under the existing `LiveSettingsConfig` (P378's "one
growable home" for runtime/testing knobs):

```jsonc
"liveSettings": {
  "debugMode": true,
  "testHarness": {
    "enabled": true,            // off by default; absent == no grant (byte-identical to P441)
    "characterNamePrefix": "mcp-",   // ONLY a char name starting with this is eligible
    "money": 1000000,           // wallet granted absolutely at seat (overrides P443 seed)
    "ignoreTechGate": true      // skip ONLY the BUY_LOADOUT tech check; still pays real prices
  }
}
```

- Registered in `ServerJsonContext` (`[JsonSerializable(typeof(TestHarnessConfig))]`)
  so it survives Native AOT config binding.
- `characterNamePrefix` defaults to `"mcp-"` — the prefix every MCP and vizfight
  bot character name now uses (Section 4), so the committed debug configs work
  out of the box.
- Production / example configs (`docker/compose.server.json`,
  `docker/compose.override.debugserver.yml`) are untouched — no grant there.

## 2. Server-side grant

`Server.HarnessGrantFor(ConnectionId)` (`Server.cs`) resolves the applicable
`TestHarnessConfig` for a connection: it returns `null` (no grant, byte-identical
to P441) unless the section is present+enabled AND the connection's remembered
character name (`_peerNames`, populated at seat) starts with the prefix. A
non-prefixed (human) connection ALWAYS gets `null`.

Three effects:

1. **Boot banner** (`Server.Lifecycle.cs`) — loudly prints
   `*** TEST-HARNESS ECONOMY GRANT ACTIVE ... — applies ONLY to matching char
   names; real players NEVER affected ***` so it is never silently on.
2. **Seat-time wallet grant** (`Server.Lobby.cs` `CompleteJoinAndLaunch`) — set
   `live.Wallet.Money = money` for an eligible connection immediately after
   `_peerNames` is recorded and before the join-time `SET_MONEY` sync (so the
   client learns the granted wallet, not the P443 seed), and log one
   `[testharness] ... GRANTED wallet $N at seat` line per granted connection.
3. **BUY_LOADOUT tech-gate skip** (`Server.ShipCommands.cs` `ApplyLoadout`) —
   when an eligible connection has `ignoreTechGate`, skip ONLY the tech checks:
   the hull gate's `StationSellsHull` term and the part gate's
   `RequiredTechsBuyable` term. Everything else is untouched: the **funds gate**,
   the **real debit path** (ShipValue delta + side-wide MONEY_CHANGE), the
   **drone-hull** (`GroupId < 0`) gate, and the **hardpoint-compatibility** gate.
   So the harness pays the real price for everything it buys — pricing stays
   exercised.

### Proof humans are unaffected

- **Prefix gating is the only lever.** A non-prefixed name always resolves to
  `null` in `HarnessGrantFor`, so a human on the same server is byte-identical
  to pre-P473. The live test proves it (Section 6): the `plain-buyer` bot (not
  `mcp-`) is refused a tech-gated hull while the `mcp-buyer` on the same server
  buys it.
- **Config off by default.** Absent section ⇒ no money, no bypass, for anyone —
  a test proved the `mcp-` bot is refused when the grant is disabled.
- **Real-player code paths untouched.** No change to P441's rules for normal
  players, no in-game admin/cheat command, no rank/win-cheat wiring.
- **Production and example configs** contain no `testHarness` section.

## 3. Bots see and report purchase results

- **`Allegiance.Bot` now decodes `BuyLoadoutAck`** (mirrors `ConnectionSession`)
  into a locked `BuyLoadoutAcks` accumulator + `ResetBuyLoadoutAcks()`.
- **`ConnectionSession` gained the same raw accumulator** (alongside the world
  apply) so the combat/spawn paths have a uniform ack signal.
- **`BotManager` spawn paths now wait for the ack and compare.**
  `WaitForAck` + `EvaluateGrant`: a purchased hull whose ack echoes a different
  (unchanged) hull is a **refusal → specific thrown error**; a granted hull with
  some requested parts not mounted is a **partial grant → warning**. The ack
  accumulator is reset immediately before `SendBuyLoadout` so the seat's
  join-burst default-hull ack is never mistaken for the purchase's ack (found
  and fixed during the live run — see Section 6).
- **Surfaced in the tool returns:**
  - `spawn_bot` → `granted: {hull, parts[], partCount}` + `warning`.
  - `spawn_scenario` → per-bot `grants` array (side/name/grantedHull/grantedParts/
    warning) + `warnings` count; refusals appear as named per-bot failures.
  - `run_hull_matrix` → per-combination `grantedHulls[]` + `partialGrants`;
    refusals become explicit `status:"error"` rows with the refusal message.
- **vizfight** logs the same mismatch to **stderr** (log-only, never throws —
  it is a spectator tool) via `LogLoadoutGrant`, attached to the proof, roster,
  showcase, and attach/fight launch paths.

## 4. Bot naming — the grant applies to the harness, never a human

- `allegiance-mcp`: `NextName()` now yields `mcp-bNNN-XXXX` and the startup
  probe is `mcp-probe` (was `MCPB…`/`MCP-PROBE`), matching the default prefix.
- `vizfight`: every BOT character name is prefixed `mcp-` via `BotChar(...)`
  (SHOOTER/TARGET/STRESSBOT/PROOFGUNNER/PROOFVICTIM/ROSTER/SC/VZB/VZA). The
  spectator (`ATTACH_CAM`) deliberately stays unprefixed so it models an
  ordinary client that never receives the grant.

## 5. Committed configs

`docker/compose.server.debugmode.json` and
`docker/compose.server.debugmode.mapgen.json` now enable `testHarness`
(prefix `mcp-`, money `1000000`, `ignoreTechGate: true`) — these are the
live-testing setups. `docker/compose.server.json` (production) and
`docker/compose.override.debugserver.yml` are unchanged.

## 6. Verification

### Unit / live tests (all pass)

`tests/Allegiance.Core.Tests/TestHarnessEconomyGrantLiveTests.cs` (3 tests over
a real in-process server + real TCP bots, mirroring P441's harness):

- `GrantOn_McpBuysTechGatedHull_PaidRealDebit_PlainRefused` — grant ON: the
  `mcp-` bot is granted the wallet at seat; docked at a fresh garrison it buys a
  tech-gated hull through the real debit path (wallet falls by the hull's real
  price); the NON-prefixed `plain-buyer` is refused the SAME hull.
- `GrantOff_McpBotAlsoRefusedTechGatedHull` — grant absent: the same `mcp-` bot
  is refused (the grant's only effect is the explicit config).
- `Bot_Decodes_BuyLoadoutAck` — `Allegiance.Bot` exposes the decoded ack.

**Must-fail proof** (task requirement): temporarily neutralizing the server grant
(`HarnessGrantFor` → `null`) makes `GrantOn_McpBuysTechGatedHull…` FAIL at the
seat-wallet assertion (mcp wallet = 0, not 1,000,000), proving the test
discriminates the grant. Reverted; no `TEMP` markers remain.

Existing `RunHullMatrixLiveTests` (P432) now enables the harness grant in its
config so the ack-verified matrix can buy requested hulls (previously fire-and-
forget against a no-grant server); it passes again.

### Build

`/home/domingo/.dotnet/dotnet build` (whole solution): **0 warnings, 0 errors**.
`tools/vizfight` (not in the `.slnx`, modified here) also builds **0/0**.

### Full suite, 3 consecutive runs

`/home/domingo/.dotnet/dotnet test`, 3 runs. All P473 tests + RunHullMatrix
passed in every run. The only failures are pre-existing documented flakes /
the known production bug (cite `docs/TEST_FLAKES.md`, P471):

- Run 1: `ShipDeleteGoneLiveTests.DockOut…` (3/3 — documented **production bug**,
  P472 follow-up #1), `CrossSideCombatVisibilityLiveTests…warp_return`,
  `LiveStationRescueTests…docked_without_player_rescued`.
- Run 2: `DockOut…`, `LiveRipcordTests.First_activation…`.
- Run 3: `DockOut…`, `Transport.InboundFloodSafety…`, `FullSessionKill…CrossSideKill`,
  `LiveChatSectorWireTests…`, `BotFlightTests.Abrupt_disconnect…`.

`DockOut…` is the sole 3/3 failure (known production bug, correctly left
unmasked). The rest are the documented rotating live-wire socket flakes
(`docs/TEST_FLAKES.md` follow-up #4 family). None are caused by P473.

### Live MCP run — grant ON vs OFF (FIFO stdio pattern)

Standalone `Allegiance.Server` (real `PCore001.igc`, `XXV_simple.igc`, teams 2,
debugMode) at 6075 (grant ON) / 6076 (grant OFF), driven by the built
`allegiance-mcp` over newline-delimited MCP JSON-RPC (the `verify_stdio.py`
pattern). `run_hull_matrix` over a representative hull set, sizes [1],
observe_seconds 1:

**Grant ON** — `run_hull_matrix` reports the REAL granted hulls:

```
  OK    hull=Adv Fighter   grantedHulls=[15, 15] partialGrants=2
  OK    hull=Adv Scout     grantedHulls=[13, 13] partialGrants=0
  OK    hull=Fighter       grantedHulls=[55, 55] partialGrants=2
  OK    hull=Gunship       grantedHulls=[22, 22] partialGrants=2
  OK    hull=Scout         grantedHulls=[14, 14] partialGrants=2
  (3 planner "weapon does not mount" errors — not grant refusals)
```

Server log confirms each prefixed bot was granted: `[testharness] … 'mcp-b…'
GRANTED wallet $1000000 at seat` and the buys completed (e.g.
`swapping base hull 13 ('Adv Scout') -> 22 ('Gunship')`, `debited -1 (wallet now
999999)`).

**Grant OFF** — `run_hull_matrix` reports the refusals explicitly:

```
  OK    hull=Adv Fighter   grantedHulls=[15, 15]   (free sellable starter)
  OK    hull=Adv Scout     grantedHulls=[13, 13]   (the spawn hull — no-op)
  OK    hull=Fighter       grantedHulls=[55, 55]   (free sellable starter)
  OK    hull=Scout         grantedHulls=[14, 14]   (free sellable starter)
  ERROR hull=Gunship       run_hull_matrix: combo Gunship+PW Gat Gun 1 size 1
      failed to spawn fully … spawn_bot: bot 'mcp-b007-1265' was REFUSED hull 22 —
      the server granted the unchanged hull 13 instead (fbought=false) …
```

The grant-OFF server has **zero** `[testharness]` lines, and its log shows the
Gunship refusal server-side: `cost 1 > wallet 0 — REFUSED (ship unchanged; ack
fbought=false fLaunch=false)`.

**The discriminator:** Gunship (hull 22) is `grantedHulls=[22,22]` with the grant
ON and an explicit REFUSED error with it OFF — exactly "run_hull_matrix reports
the real granted hulls; with the grant off, it reports the refusals explicitly."

During the live run a real bug was found and fixed: the bot also receives a
**join-burst** `BUY_LOADOUT_ACK` for its default hull at seat, so a naive
"any ack arrived" check read that stale ack and mis-reported a refusal. Fixed by
resetting the accumulator before the send and waiting for an ack reflecting the
requested hull.

## Files changed

- `src/Allegiance.Server/ServerConfig.cs`, `ServerJsonContext.cs`, `Server.cs`,
  `Server.Lifecycle.cs`, `Server.Lobby.cs`, `Server.ShipCommands.cs`
- `src/Allegiance.Bot/Bot.cs`
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs`
- `tools/allegiance-mcp/BotManager.cs`, `McpTools.cs`
- `tools/vizfight/Program.cs`
- `docker/compose.server.debugmode.json`, `docker/compose.server.debugmode.mapgen.json`
- `tests/Allegiance.Core.Tests/TestHarnessEconomyGrantLiveTests.cs` (new)
- `tests/Allegiance.ClientCore.Tests/RunHullMatrixLiveTests.cs` (grant enabled)

## Out of scope (honoured)

- No change to P441's real purchase rules for normal players.
- MCP scenario presets — **P474**, next.
- Retiring vizfight's bot code (graduation criterion unchanged).
- No in-game admin/cheat command.
