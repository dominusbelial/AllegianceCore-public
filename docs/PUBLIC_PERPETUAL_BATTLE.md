# Public perpetual battle

One two-team battle runs continuously. Players join through the browser or desktop client, choose a side, fit a ship and launch. External MCP clients fill vacant seats. Bot processes run on a separate bot host, never on the game server.

This change supplies files and tools only. The operator reviews and authorizes any deployment separately; no public host, rig or bot host is changed by this task.

| Setting | Public example |
| --- | --- |
| Teams / baseline seats | 2 / 5 per side, including commander/economy seats |
| Civilizations | `[-1, -1]`, fresh legal random selections per assembly |
| Civilization replay | Each `[matchup]` prints its seed, IDs and civilization names; set `games[0].civSeed` to replay the first tuple |
| Map cycle | SingleRing → PinWheel → DoubleRing; rotation seed 927 |
| Research | `research`; income multiplier 5000; build-time scale 8; plans do not loop |
| Developments | Enabled, including shipyard, expansion, tactical and supremacy paths |
| Initial miners | 1 per team |
| Deadline | Match cap 3600 simulation seconds; armed research finale timeout 900 seconds |
| Winner at deadline | Existing goals, then enemy station destructions, then ship kills; ties may draw |
| Between matches | Automatic next match after 30 simulation seconds |
| Side selection | Auto-seat after 30 simulation seconds; `maxTeamImbalance: 1` |
| Team capacity | 32 players per team, allowing humans beyond baseline seats |
| Connections per source | 64 (the default); bots share the operator's source with the operator's own clients |
| Guests | `identity.allowAnonymous: true` is required for browser guests |
| Play log | On in this example; off by default elsewhere |

DiamondRing from the three-team rig is invalid with two teams (`Igc/mapmakerigc.cpp:1930-1937`). The public preset uses DoubleRing as its legal third entry; it does not alter native map validation.

The imbalance gate counts **all seated player clients**, including bots. Native low-rank players may exceed the numeric gate by two seats; bot authentication also has its existing exception. Automatic selection chooses an eligible side, but the gate does not force every explicitly selected human seat to the smaller team. This preserves `FedSrv/fsmission.cpp:4612-4642`. Backfill withdraws a combat bot after a human seats, normally within five wall-clock seconds. It does not move humans or add an admission rule.

Use `docker/public/server.perpetual.example.json` as the starting point for the private server configuration. Fill the existing public-origin, enrollment and trust placeholders as described in `docker/README.md` and `docs/BROWSER_HOSTING.md`. Its empty game password allows drop-in play; set a private password if desired and supply the same password to external bot clients through the existing configuration. Keep guest admission enabled. The named `server-identity` volume is already mounted at `/server-identity` in `docker/public/docker-compose.yml` and the root compose file. Logs use `/server-identity/plays/plays-YYYY-MM.jsonl`; enrollment remains in its existing file. No new volume, public port or bot service is needed. The brief's `docker/public/compose.yml` name refers to the actual `docker/public/docker-compose.yml` file.

After separate deployment approval, install the private configuration as `server.json` beside the public compose file and use the existing public stack lifecycle:

```sh
docker compose -f docker/public/docker-compose.yml up -d server
docker compose -f docker/public/docker-compose.yml stop server
```

Build the external driver on the bot workstation, then run its wrapper there. `SERVER` and `PORT` are operator-supplied arguments, not repository values. Artwork must be available on the bot host through the existing artwork resolver. The wrapper uses the Release assembly; it does not build or install packages:

```sh
dotnet build tools/allegiance-mcp/allegiance-mcp.csproj -c Release --disable-build-servers
tools/allegiance-mcp/scripts/bots-spawn-perpetual.sh SERVER PORT CLIENT_VERSION
```

Stop the foreground driver with Ctrl-C or send SIGTERM to its recorded PID. Its owned client connections are disposed. Stop only that driver process. For local verification, use a loopback server; no remote run is part of P1071.

The wrapper selects `--backfill-seats 5`. Supply the version of the bot release you built/installed as its third argument (`--client-version` on the driver); the public example enforces the existing minimum-version gate. Omission reports unknown `0.0.0`, which that gate refuses rather than inventing a release identity. Backfill sends no legacy dummy ticket: its ordinary anonymous LOGONREQ is admitted only when `identity.allowAnonymous` permits it. Verified identity fixtures remain loopback-only. The driver discovers the roster's sides rather than assuming two. Each side's total includes humans, its protected commander/economy client and combat clients. The manager's probe doubles as one side's commander in this mode. Human departures start a **30 wall-clock second** refill grace; reconnecting before expiry cancels the deficit. A new mission settles its wire roster before refilling, so staying humans retain their seats. The initial combat fleet uses the existing small-battle meeting-engagement option and showcase finale controller, including its pending discovery and rotation re-arm. A replacement inherits the departed combat slot in that controller, preserving its timer and discovery state. The server match cap remains the hard bound when a finale cannot arm. Each action prints `[backfill] side … humans … bots … → …` on the external driver.

Only combat clients owned by this driver can yield seats. Research/commander sessions, miner/builder clients, current team leaders and foreign bot clients are protected. If protected clients are the only bots remaining, the side stays above the target and logs that decision. A side already composed of humans needs no additional bots. Protected economy clients already present remain protected even if humans fill every baseline seat. More than five humans is allowed within native admission/capacity rules. Use one backfill owner per battle: independent owners cannot withdraw each other's clients.

Both analytics and backfill classify names with exact or trailing-`*` prefix patterns, case-insensitively. Defaults are `mcp-*` and `research-cmd*`; the second covers older MCP commander names. If customizing `playLog.botNames`, pass the identical comma-separated list to the driver with `--bot-names`. Names classify telemetry only and confer no privileges; a human choosing a bot-pattern name is classified as a bot.

Copy monthly files from the existing data volume using the operator's normal private backup procedure, then run the standard-library report tool locally:

```sh
python3 tools/play-report.py /path/to/copied-plays/
python3 tools/play-report.py /path/to/copied-plays/ --json
python3 -m unittest discover -s tools/tests -p test_play_report.py
```

The report counts completed session segments by UTC start date and ISO week-year. Returning means an active callsign has appeared on at least two distinct dates by the end of the displayed period, within the supplied files; first-visit days do not count as returning. It includes unique callsigns and guest/account counts, sessions, median/nearest-rank p90 minutes, returning callsigns, browser/desktop/unknown session counts, launched/only-joined sessions and matches with humans. Guests are only random public callsigns: cleared storage, rerolled or edited names count as new people, and shared names cannot establish unique physical people. A reconnect is a new session segment; staying across a match boundary also starts another segment. Account classification requires a server-verified join ticket; no account ID is persisted. Bot sessions are excluded; per-match summaries count distinct human and bot callsigns observed during that match, including players who left before the end. Match summaries start at assembly and end at game over, retirement or clean shutdown; they include the pre-game/intermission interval. `winner` is a side ID, or null for a draw or shutdown. Match participation does not prove simultaneous overlap.

Monthly files are selected by completion time in UTC. The writer appends and flushes lines on a background worker and retains the current UTC month plus eleven preceding months. Each file is capped at 64 MiB by default (`playLog.maxFileBytes`); subsequent records that month are dropped. A full bounded queue (`queueCapacity`, default 1024) also drops rather than blocks the simulation. `[playlog]` diagnostics count written/dropped/failed records; the server exposes `PlayLogDropped` for in-process observation. Disk errors drop records and preserve gameplay. Shutdown drains the queue. Abrupt process termination can lose open sessions or queued records, so the data is an estimate of completed play, not billing or identity evidence. Files contain public callsigns and gameplay aggregates only: no addresses, ports, agents, account IDs or ticket contents. Protect these volume files using the operator's existing access policy.

## Operating notes (live host, 2026-11)

- **Bots** run under systemd on the external bot host:
  [`tools/allegiance-mcp/scripts/allegiance-perpetual-bots.service.example`](../tools/allegiance-mcp/scripts/allegiance-perpetual-bots.service.example)
  uses a self-contained published driver, `Restart=always` and a log file. A server redeploy or a lost link
  recovers by itself, and the driver refills seats after reconnecting.
- **Mixed transports:** `--ws-fraction 0.5 --ws-url wss://<host>/game --ws-origin https://<host>` sends every
  second bot over the public WebSocket route, so the bots exercise the same Caddy/WSS path as browser players.
  The live host runs this split since 2026-11-23. The unit restarts the driver itself: after replacing the driver
  or the start script, restart the unit (`systemctl restart allegiance-perpetual-bots`), never start a second copy
  by hand.
- **Do not reuse a rig's bot secret** (`ALLEGIANCE_PRIVILEGE_SECRET`) against a public host. An empty game
  password needs no `ALLEGIANCE_LAN_PASSWORD`: the join gate is skipped when the game has no password.
- **Pass the client version** at or above the server's `minimumClientVersion`. A missing version reports `0.0.0`
  and is refused.
- **Exclude test visits** from the play report. Operators' own browser test guests are ordinary `Pilot-…`
  sessions in the log; note their callsigns when testing.
- **Fly a browser guest on the rotation's generated maps** after any client or art change. See
  `docs/BROWSER_HOSTING.md` ("Lessons from the live host").

## Native intent

Preserve native side counts and admission exceptions (`FedSrv/fsmission.cpp:4612-4642`), creation-time random civilization drawing (`FedSrv/fsmission.cpp:175-198`, `Igc/igc.h:1280-1281`), per-player kills/deaths/ejections (`Igc/igc.h:5940-5943`), voluntary logoff (`FedSrv/FedSrv.CPP:5787-5802`) and result/reset ordering (`FedSrv/fsmission.cpp:3225-3263,3427-3454`). Existing native gates remain authoritative.

## Modern approach

Durable aggregate analytics, retention, perpetual preset pacing and external seat backfill are port orchestration. Logging and backfill default off. Backfill reuses ordinary client logon, side selection, purchases and logoff, plus the existing research/constructor driver; it creates no server bot service or gameplay rule. This work performs no deployment.
