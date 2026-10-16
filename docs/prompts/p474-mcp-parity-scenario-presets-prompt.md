# P474 — allegiance-mcp scenario presets for live-verifying the parity features (bomber run + capture)

## Why this task exists

The parity chain landed signature combat mechanics that unit tests can't
make you *feel*:
- **P450**: capture requires downed shields (`shipIGC.cpp:849-851`).
- **P451**: base-under-attack alerts.
- **P455 / P455b**: elimination when a side loses all its restart stations.

Live-verifying them today means hand-writing vizfight code each time.
allegiance-mcp is the intended **sole bot-driver** (it's the vizfight/MCP
architecture decision). It already has `spawn_bot` with the `combat`
behaviour (P429, a port of vizfight's P420 choreography,
`BotManager.cs` ~547+), `spawn_scenario` size presets (P430), order
acceptance (P431) and real launcher mounting (P433). P473 made bots buy
through the real economy with a harness grant and surface refusals.

Design rule, and don't break it: **MCP sets intent; C# behaviour code
flies at tick rate.** The model never steers per tick.

Check that P450, P451, P455 and P473 commits exist. For any missing one,
skip the preset that depends on it and say so.

## Scope

Add a `scenario` argument, or a new `run_scenario` tool (choose one and
justify it), with two presets. Each preset:
- spawns its bots through the existing spawn paths;
- runs its choreography as a C# behaviour;
- returns a **structured observed-outcome report**, meaning the real wire
  events the bots saw, with timestamps, not a narrative.

1. **`bomber_run`.** Side A spawns an escorted bomber group. The bomber
   hull must carry a real anti-base weapon: resolve it from the core
   through the existing part catalogue, don't hard-code ids. The group
   flies to side B's garrison sector and attacks the station.
   - Side B gets a defender contingent.
   - Report: station shield and hull fractions over time, the
     base-under-attack alert(s) side B's bots received (P451), and whether
     the station died, with its `KILL`/destroy broadcast.
   - If side B had no other restart station, also report the P455 events:
     `SIDE_INACTIVE`, the "is no more." chat and `GAME_OVER`.
2. **`capture_attempt`.** A side-A boarding-capable hull (`c_habmBoard`)
   tries to dock with side B's station **twice**: first at full shields,
   where the report must show a refusal, then after an escort knocks the
   shields below the downed-shield constant, where it must show a
   capture, with the station's side change as the bots observed it.

Both presets:
- take optional size and sector arguments with sensible defaults;
- clean up their bots when done, or on a timeout argument;
- have `list_active_bots` reflect them while running.

## Explicitly out of scope

- Minefield and elimination-only presets. Those are a named follow-up,
  **P474b**; note it in the report.
- New server features. If a preset exposes a real server bug, report it
  with evidence and a named follow-up, and don't fix it here.
- vizfight changes or retirement.

## Verification

- Run each preset live against a local server with P473's harness grant
  on, and paste the structured outcome into the report.
  - `capture_attempt` must show refuse-then-capture.
  - `bomber_run` must show real shield/hull decline and the alert.
- Unit tests for any pure planning or resolution logic you add, such as
  resolving the anti-base weapon and the boarding hull from the core.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full `/home/domingo/.dotnet/dotnet test`, 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P474_MCP_PARITY_SCENARIOS.md`. Cover the tool
   shape decision, both live outcome reports, any server bugs found, and
   the P474b scope.
