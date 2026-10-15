# P473 — test-harness bots survive the real economy (money/tech grant + loud purchase failures)

## Why this task exists

The parity chain made purchasing real, as native does:
- **P441** debits the wallet, refuses a purchase when funds are short, and
  runs a server-side tech check on `BUY_LOADOUT`, with **no debug-mode
  bypass**.
- **P443** adds native starting money (`c_fStartingMoney = 16000`).
- **P444** and **P468** tighten tech gating further.

Check that those commits exist (`git log --oneline | grep -E "P441|P443"`).
If they don't, **stop and report**.

The wire-level test and playtest bots were written for the old free,
ungated shop, and nothing checks what they actually got:

- **allegiance-mcp.** `BotManager.SpawnBot` sends
  `SendBuyLoadout(hullId, fLaunch: true, parts)` fire-and-forget
  (`tools/allegiance-mcp/BotManager.cs:247` and `:302`), and never checks
  the result. `run_hull_matrix` (`McpTools.cs` ~90, `MatrixPlanner.cs`) is
  meant to test **every** hull. Under native tech gating most hulls aren't
  buyable at a fresh side's garrison, so the matrix silently tests default
  ships.
- **vizfight.** Its launch paths all use `SendBuyLoadout`
  (`tools/vizfight/Program.cs:1373-1374`, `:1486-1489`, and the showcase
  roster ~429-438).
- **`Allegiance.Bot` doesn't decode `BuyLoadoutAck` at all.** Only
  `ConnectionSession` does (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:641-642`).
  A `Bot`-based tool therefore can't even see a refusal.

The result is that after P441, bots quietly fly the wrong ships, with no
error anywhere. That's the same class of blind spot as P437, where bots never
exercised the real shop numbering.

**Native has no money or tech cheat.** `fCanCheat` only affects rank limits
and win checks (`FedSrv/fsship.h:337-339`, `fsmission.cpp:1877,3810`). So a
harness grant is a **port extension**. It must be explicit, off by default,
and loudly logged, and it must never touch real players.

## Scope

1. **A server-side harness grant**, configured in `liveSettings`
   (`src/Allegiance.Server/ServerConfig.cs`, `LiveSettingsConfig`, P378's
   "one growable home" for these knobs):
   - a new sub-object, for example `testHarness { enabled: false,
     characterNamePrefix: "mcp-", money: <int>, ignoreTechGate: true }`;
   - it applies only to connections whose character name starts with the
     prefix;
   - on seat, set their wallet to `money`;
   - on `BUY_LOADOUT`, when `ignoreTechGate` is set, skip **only** the tech
     check for those connections. They still **pay** through the real debit
     path, so pricing keeps being exercised.
   - Log a boot banner and one line per granted connection. With the
     section absent or disabled, behaviour is byte-identical to P441.
2. **Bots see and report purchase results.**
   - `Allegiance.Bot` decodes `BuyLoadoutAck`, mirroring `ConnectionSession`.
   - allegiance-mcp's spawn paths wait for the ack and compare the granted
     hull and parts with the requested ones.
   - `spawn_bot`, `spawn_scenario` and `run_hull_matrix` return the real
     granted loadout, and a clear per-bot error or warning on refusal or
     partial grant. Never a silent success.
   - vizfight logs the same mismatch to stderr.
3. **Bot naming.** Make sure MCP and vizfight bot character names use the
   harness prefix, or make the prefix configurable on their side, so the
   grant applies to them and never to a human.
4. Update the committed `docker/compose.server.debugmode*.json` configs so
   the harness grant is enabled there, since those are the live-testing
   setups. Production and example configs stay without it.

## Explicitly out of scope

- Any change to P441's real purchase rules for normal players.
- MCP scenario presets (**P474**, next).
- Retiring vizfight's bot code (the graduation criterion is unchanged).
- An in-game admin/cheat command.

## Verification

- Server tests that fail on unfixed code first:
  - with the grant enabled, a `mcp-` connection gets the money and buys a
    tech-gated hull, and its wallet is debited;
  - a non-prefixed connection is refused the same hull;
  - with the grant disabled, the `mcp-` connection is refused too.
- A test that `Allegiance.Bot` exposes the decoded `BuyLoadoutAck`.
- A live run of allegiance-mcp (FIFO stdio pattern: see the
  `allegiance-port-status` wiki page, or `tools/allegiance-mcp/verify_stdio.py`)
  against a local server with the grant on:
  - `run_hull_matrix` reports the real granted hulls;
  - with the grant off, it reports the refusals explicitly.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full `/home/domingo/.dotnet/dotnet test`, 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` (P471) for any remaining
  flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P473_BOT_HARNESS_ECONOMY_GRANT.md`. It covers
   the config shape, the proof that humans are unaffected, and live MCP
   output with the grant on and off.
