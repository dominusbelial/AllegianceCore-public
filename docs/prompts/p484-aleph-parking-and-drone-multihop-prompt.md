# P484 — ships park at an aleph instead of transiting (side-dependent); confirm drones cross multi-hop routes

## Why this task exists

Two separate live sessions saw the same stall, and both left it unfixed:

- **P429** (`docs/reports/P429_MCP_COMBAT_BEHAVIOR.md:136-148, 191-196`): a
  bot seated on the probe's own side flies to the aleph and **hovers at the
  identical fixed coordinate without transiting**, while an opposing-side
  bot crosses cleanly at the same moment. The parking coordinate is the same
  whether the bot approaches to 200/450 or 5/120 units, so approach distance
  isn't the cause. P429 attributed it to "server warp export/coordinate
  handling for the probe's side". That's a hypothesis, not a finding.
- **P474** (`docs/reports/P474_MCP_PARITY_SCENARIOS.md` §3): on the debug
  map, bots complete the first hop (Plato → Alderan) and then **park at the
  second aleph** (Alderan → Omega) for the rest of the window, while the
  bot's own diagnostics say
  `cluster 1052 goal 1053 … warpFound=True`. As a result the
  `bomber_run`/`capture_attempt` presets can't reach the enemy garrison, and
  P450/P451/P455 have never been proven live.

**Why this matters beyond test bots.** Server-side drones from P458 (AI
constructors) and P459 (miners, wingmen, layers, carriers) also route through
alephs (P157's next-warp search in `src/Allegiance.Core/Model/Sector.cs`). If
the same stall hits them, the expansion and mining loop breaks on any target
more than one warp away.

**Relevant code:**
- The server's transit trigger is a physics collision,
  `|ship − warp| ≤ warp.Radius + ship.Radius` (`Sector.cs:~1420`, `~3124`).
- There's a 2 s debounce (`WarpCooldownSeconds`, `Sector.cs:1372`) and P406's
  exit clearance (`docs/reports/P406_WARP_EXIT_CLEARANCE.md`).
- Bot routing is client-side, from its own `ClientWorld`:
  `TryBuildWarpDestMap` / `TryFindWarpTo`
  (`tools/allegiance-mcp/BotManager.cs:681-720`), ported from vizfight.

## Scope

1. **Root-cause the parking with evidence, not hypotheses.** Reproduce it
   (a P474 debug-map preset or a minimal 2-hop bot flight). At the parked
   moment, log side by side:
   - the bot's own believed warp position (from its `ClientWorld`);
   - the server's authoritative warp position and radius for that aleph;
   - the bot's authoritative server-side position and distance to the warp;
   - and why the server's trigger isn't firing: distance, debounce, exit
     clearance, wrong warp id, or a cluster mismatch.
   Determine whether the cause is the **client's warp data** (for example a
   side-specific or stale warp EXPORT, or the wrong partner/destination
   mapping after the first hop), the **bot steering**, or the **server
   trigger**. Explain why it's side-dependent.
2. **Fix it at the root.** If it's wire data (EXPORT/cluster info), fix the
   server or client path with a native citation; it would affect human
   players too. If it's only the bot, fix the bot. If it's the server
   trigger, cite native's aleph-collision handling.
3. **Server-side drones on multi-hop routes.** Add a test where a
   server-side NPC drone (for example a P459 miner or a P458 constructor
   ordered via P425) must cross **two** alephs to its target, and assert
   that it arrives. If it parks, fix it under the same root cause, or as a
   separate cause with evidence.

## Explicitly out of scope

- The "freshly seated opposing-side combat bot freezes in place" on
  non-debug maps (P474 §3, first bullet). That's **P485**, next, unless
  step 1 proves it's the same root cause, in which case say so.
- Re-running the P474 presets as proof (P485).
- Warp visuals (P341) and ripcord.

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471's
  sequential runner), 3 consecutive runs. Cite `docs/TEST_FLAKES.md` for any
  remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P484_ALEPH_PARKING_MULTIHOP.md`.
