# P486 — MCP commander tools: invest in drones, list drones, order drones (native commander model)

## Why this task exists

Domingo asked whether MCP could drive mining. The decision: **the server's
native drone AI keeps flying, and MCP acts as the commander**. That's
native's own model. A commander invests in drones and orders them; the
drones fly themselves (native drones are server-side, pilot type below
player). It isn't the server driving its AI through MCP: that would put an
out-of-process control plane on the game's critical path. And a wire-bot
can't fly a miner hull anyway, because P470 made drone hulls unbuyable for
players (group id −1) and P473's harness deliberately doesn't bypass that
gate (`src/Allegiance.Server/Server.ShipCommands.cs:~2350`).

**Everything needed already exists on the real wire, as an ordinary
player:**
- `ConnectionSession.SendBucketDonate` (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:280`):
  funds a real investment. P458 spawns constructors from station-type
  investments, and P459 spawns miners, wingmen, layers and carriers from
  drone-type ones.
- `SendOrderChange` (`:398-425`): real orders. P469 made drones obey commands
  (native `CommandToDrone`), and P457 made quick-chat orders carry a
  command and target.
- Observation: drones arrive as ordinary ships on the wire (P459 gave them
  their real hulls). Money and payouts arrive as PAYDAY/MONEY_CHANGE
  (P442/P443), and investments as BUCKET_STATUS, if the client handles it
  (check).

None of this is exposed in `tools/allegiance-mcp/McpTools.cs` today.

## Scope

Add three tools that go through a **harness commander bot**, a seated
`mcp-` player on the chosen side using the P473 grant for money. No server
changes.

1. **`invest`** `{side, bucket: <name or id>, amount?}`: resolve a real
   investment from the core, using the same bucket ids the server
   creates, in native order (P458). Donate through `SendBucketDonate`, and
   return the observed progress or completion, plus the drone that appeared
   on the wire.
2. **`list_drones`** `{side, type?}`: the side's drones as observed on the
   wire: id, drone type or hull, sector, position, state or command, and
   cargo/ore where observable. Say plainly in the tool output which fields
   aren't observable from a player's view.
3. **`order_drone`** `{droneId, command, target}`: issue a real order on the
   P469/P457 path, using native's command set. Return the drone's observed
   reaction ("Affirmative." reply and state change).
4. **A mining demonstration** through these tools, recorded in the report:
   - invest in a miner;
   - list it;
   - order it to a specific He3 asteroid in a reachable sector (multi-hop
     if P484 landed);
   - observe at least one real mining trip and the resulting side or
     wallet income.

## Explicitly out of scope

- Any server change, and any change to drone AI.
- An LLM commander loop. These are the tools, not an agent.
- vizfight changes.

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471's
  sequential runner), 3 consecutive runs. Cite `docs/TEST_FLAKES.md` for any
  remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P486_MCP_COMMANDER_TOOLS.md`.
