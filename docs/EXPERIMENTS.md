# Experiments — fly them one at a time, record every flight

Every experiment below is **off by default** and changes nothing until switched on. Keep or drop is decided per
experiment: by how it feels in play for the client-visible ones, and by a 200v200 two-match A/B on the rig for the
scale-only ones. Fly **one at a time** so any difference has one cause.

## One command per flight

```
scripts/fly-experiment.sh <experiment> [--bots N] [--map <file> | --map-gen <TYPE> --map-seed <N>] [--video] [--no-build]
```

It starts a local server + lobby + MCP bots with exactly one experiment on (the server's `[boot]` banner names it),
launches the client, and waits until you close the client. Then it **records the flight** in
`.scratch/experiments/<UTC time>-<experiment>/` (git-ignored, local only):

| file | what |
|---|---|
| `run.json`, `server.json` | the experiment, its flags, the git commit, bots, map/seed — the exact setup |
| `session-performance.json` | the client's P789 session recorder (frame-time histogram, FPS, 1 % lows, server tick p95, bottleneck) — saved at match end, so finish a match |
| `experiment-telemetry.log` | the server's `[boot]`, `[update-age]`, `[reliable]`, `[reconnect]`, `[gunner]` lines |
| `stack-logs/` | full server, lobby and bot logs |
| `client.out` | the client log |
| `flight.mp4` | game-window recording, with `--video` (`scripts/record-window.py`: portal window capture, GPU HEVC; no desktop) |
| `notes.md` | your impressions and verdict |

and appends a row to [`docs/experiments/FLIGHT_LOG.md`](experiments/FLIGHT_LOG.md) (verdict `_pending_` until filled in).
Fly `baseline` too: it is the control to compare against.

## The experiments

| name | task | switch | scope | what to look for |
|---|---|---|---|---|
| `smoothing` | P813b | client `--remoteHermite` | your client | smoother distant ships; does aiming at the hull feel off? (the drawn hull trails the true pose by speed × one update interval; lead/aim/hits use the true pose) |
| `reconnect` | P813c | server `reconnect.enabled` + `graceSeconds`; client/bots `ALLEGIANCE_RECONNECT_GRACE_SECONDS` | server + client | pull the network for 2–3 s mid-fight: you come back in the same ship; a voluntary quit is still immediate |
| `tiers` | P813a | server `broadcast.interestTiers` (+ `promotionEpsilon`, default 200) | server | far ships still look right; manoeuvring far ships snap back fast; `[update-age]` shows bytes per tier |
| `reliable` | P813d | server `transport.reliableOneShot` (env `ALLEGIANCE_TRANSPORT_RELIABLE_ONESHOT=1`) | server | kills/docks/chat under heavy load; mostly visible at 200v200 |
| `gunners` | P814b | bot host `ALLEGIANCE_GUNNERS=on` | bots (skirmish/showcase) | bots board turret bombers/gunships and fire the turrets; a gunship slot in the fleet |
| — | P788c | `sim.nativeBroadcastOrder` | server | in development |

Bot-side experiments (`gunners`) only show in skirmish/showcase games. Suggested order: `smoothing`,
`reconnect`, `gunners`, then `tiers` and `reliable` (those two are judged on the 221 rig).
