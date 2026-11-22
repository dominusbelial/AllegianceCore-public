# P1071 — Perpetual public battle: play log, random-civ rotation, even-sides bot backfill

Lane: codex. Base: master after the P1068/P1069/P1070 merge. Five-hour cap from your start time.
Report: `docs/reports/PERPETUAL_BATTLE_P1071.md`, written before implementation and kept current.

## Why

The lobby VPS (1 vCPU, ~4 GB) now serves the browser client with guest play (P1068: a random,
browser-local `Pilot-XXXXXXXXXX` callsign, no ticket). Domingo wants to leave one automated
battle running there that anyone can drop into, and to **see whether real people actually play**.
Three pieces are missing:

1. **No play log.** The server prints `[logon] …` console lines (`Server.Dispatch.cs:236` and the
   ack path at `:351-371`) but keeps nothing durable. Nobody can answer "how many different
   people played this week, for how long, and did they come back?"
2. **No public perpetual-battle configuration.** The rig already runs the rules we want
   (`autoNextMatch`, showcase research mode, finale/match caps, map rotation), but with fixed
   civs (`civIds [18,4,5]`, `civRotation: alternate`). The server already supports random civs
   per assembly: `GameConfig.CivIds` with `-1` = random at each assembly plus a logged,
   reproducible `CivSeed` (`ServerConfig.cs:231-238`). Nothing combines these for a public host.
3. **Bots and humans don't share a battle.** Bots are external MCP clients
   (`tools/allegiance-mcp`, run from MS-A2 lane-bots `~/allegiance-bots/bots-spawn-sides.sh`,
   target in `~/allegiance-bots/SERVER`). They never leave to make room for a human, so a human
   joining makes a side bigger than the others, and the side's seat count is not held.

Standing rules: **bots never run on the VPS** (they connect from lane-bots over WAN); a VPS deploy
or config change is Claude's job after asking Domingo — this task changes no deployment.

## Rig rules to reuse (live on rig 219, `~/allegiance-test/docker/compose.server.json`)

```
liveSettings.autoNextMatch       { enabled: true, intermissionSeconds: 30 }
showcase.techMode                "research"
showcase.incomeMultiplier        5000
showcase.researchTimeScale       8
showcase.loopResearchPlans       false
showcase.finaleTimeoutSeconds    900
showcase.stationKillsDecideDeadline true
showcase.matchCapSeconds         3600
showcase.mapRotation             { mode: "cycle", seed: 927, entries: [SingleRing, PinWheel, DiamondRing] }
sideSelectAutoSeatSeconds        30
games[0]: allowDevelopments + all four paths true, initialMinersPerTeam 1, homeSwap "off"
```

Differences for the public battle: **2 teams**, **`civIds: [-1, -1]`** (random each match; the
seed is logged and the matchup must be logged per match — add it if the existing log line lacks
the civ names), **`maxTeamImbalance: 1`** (native gate `Server.SideIntegrity.cs:73-83`; the rig
uses 32767 = off), `maxPlayersPerTeam` large enough for humans beyond the bot seats.

## Items

### 1 — Play log (server, default off)

- New config section, e.g. `playLog { enabled: false, path: "data/plays.jsonl", botNames: ["mcp-*"] }`
  (pattern match case-insensitive, like `adminCheats.allowNames`). Validate it like the other sections.
- One JSON line per **completed play session** (logon → logoff/disconnect/kick/mission end), appended
  and flushed off the tick thread (bounded queue; a full queue drops and counts, never blocks the sim):
  `{ start, end, seconds, callsign, kind: "guest"|"account", client: "browser"|"desktop"|"unknown",
     matchId, civs, sideCiv, kills, deaths, ejects, launched: bool }`.
  - `kind`: account when the logon carried a verified identity ticket (P808 path), else guest.
  - `client`: browser when the session came through the WebSocket listener, desktop for the native
    transport. If the transport is not knowable at that layer, say so and use `unknown` — don't guess.
  - Per-session counters come from the existing per-player score/stat records; cite them.
- **Privacy, required:** no IP addresses, no ports, no user agents, no ticket contents, no
  account ids beyond the public callsign. Guests are identified only by their random callsign
  (P1068: re-rolled/forgotten names count as new people — state that limitation in the report).
- Bot sessions (name matches `botNames`) are **not written**; count them in a separate per-match
  summary line `{ type: "match", matchId, start, end, civs, map, humans, bots, winner }` so the
  log also shows whether humans played alongside bots.
- Rotation: one file per UTC month (`plays-2026-11.jsonl`), keep the last 12, size-capped. The
  path lives under the existing server data volume (check `docker/public/compose.yml`'s volume
  for the server and use it; say which).
- `tools/play-report.py` (stdlib only): reads the JSONL files and prints per day and per ISO week
  — unique callsigns (guest / account split), sessions, median and p90 session minutes, returning
  players (seen on ≥ 2 distinct days), browser/desktop split, sessions that launched vs only
  joined, and matches with ≥ 1 human. `--json` for machine output. Unit-test it with a fixture.

### 2 — Perpetual battle configuration (files only)

- `docker/public/server.perpetual.example.json`: the public example (`docker/public/server.example.json`)
  plus the rig rules above with the public differences. Keep `identity.allowAnonymous: true` with a
  comment that guests need it. `playLog.enabled: true`. Comment the per-IP cap: all bots arrive from
  one public IP, so `transport.maxConnectionsPerIp` must be ≥ bot seats + headroom (say 32).
- A config-load test that boots the server from this file headless (artwork-gated like the other
  live tests) and proves: random civs resolve to real legal civs on two consecutive matches with a
  logged seed, the match cap/auto-next produce a second match, and `maxTeamImbalance` is 1.
- `docs/PUBLIC_PERPETUAL_BATTLE.md`: what the battle is, the rules table, how to start/stop it, how
  to read the play report. No IPs, hostnames or secrets in the repo.

### 3 — Even-sides bot backfill (bot driver, default off)

Native has no bot backfill; this is port orchestration behind a flag, in `tools/allegiance-mcp`
(the spawner that already launches bots per side), not in the server.

- Mode `--backfill-seats N` (per side). **Start value for the VPS: 5 → a 5v5.**
- The driver reads the authoritative roster it already receives (players per side, human vs bot by
  the same name rule as item 1) and keeps **every side at exactly N seats** (humans + bots):
  - a human seats on side S → one bot on S quits within ~5 s, so the sides stay even;
  - the bot that leaves is a **combat role** — never the side's commander/research bot or the
    bot that buys miners/constructors (cite where the MCP assigns those roles). If only
    protected bots are left, the side runs over N rather than drop the economy;
  - a human leaves S → after a 30 s grace (reconnects don't churn) a bot rejoins S;
  - a side that is all humans has no bots; more humans than seats is fine — the server's
    `maxTeamImbalance: 1` gate already steers a new human to the smaller side, since bots and humans
    both count (`Server.SideIntegrity.cs:63-83`). Verify and say so; don't add a server rule.
  - a new match (auto-next) starts with N bots per side again; humans who stay keep their seat
    and the matching number of bots is not spawned.
- Every backfill decision is logged on the driver side (`[backfill] side 1 humans 1 bots 4 → quit mcp-…`).
- `bots-spawn-perpetual.sh` (new, beside the existing spawn scripts in the repo's lane-bots
  deploy files — find them via `tmp/deploy-ab-lane.sh` references in docs, or put it in
  `tools/allegiance-mcp/scripts/`): starts the backfill driver for 2 sides × 5 seats against a
  server address given as an argument. **Do not run it against any non-loopback host.**
- Tests: a loopback live test (artwork-gated) with the real server, the backfill driver at
  N = 3, and scripted "human" logons (non-`mcp-` names through the ordinary logon path):
  human joins side 0 → bots on 0 drop to 2 within the deadline, sides stay 3/3; a second human
  is steered to side 1 by the imbalance gate; a human leaves → after the grace a bot returns;
  the commander/economy bot is never the one dropped; a next match keeps the counts.
  Unit tests for the seat arithmetic and the drop-order choice.

## Gate

Light gate: solution build 0/0; `tools/build-tools.sh`; every new test 3× alone; one full Fast
sweep at the end; `tools/play-report.py` tests; showcase never-parks 5× is **not** required (no sim
change) unless you touch sim/bot strategy code — then run it and say so. Flakes go in
`docs/TEST_FLAKES.md` with 3× isolated reruns. Use `$HOME/.dotnet/dotnet` and the usual
`NUGET_HTTP_CACHE_PATH` / `--disable-build-servers` flags.

## Not in scope

- Any VPS, rig or lane-bots change, deploy or run (Claude does that after asking Domingo).
- Lobby-side web analytics, page-view counters, cookies, fingerprinting, IP logging of any kind.
- Server-side bot spawning or a server rule for backfill; changing native admission rules.
- Keeping a guest callsign across a later sign-up; carrying guest stats to an account.
- Capacity tuning beyond 5v5 (the VPS ramp is a separate, already-approved test).
- 3+ side battles (the driver must iterate sides, never hard-wire two, but the config is 2 teams).

## Common rules

- No commits, pushes, package installs or deploys; leave changes in the working tree.
- Never `git stash`; never `pkill -f`; stop only processes you started, by PID.
- Native first: cite native file:line for every native-behaviour claim; everything new is port
  orchestration and must say so ("Native intent / Modern approach" footer, both sections).
- No home paths, hostnames, IPs or secrets in committed files.
- Report: what was done per item, tests with counts, anything unfinished and why.
