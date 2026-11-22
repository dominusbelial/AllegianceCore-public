# WebSocket bot load tests (P1072)

`allegiance-mcp` can drive the same binary FedPackets as browser players over
WebSocket. Its default remains TCP. These instructions prepare an experiment;
rig, lane-bots and VPS runs require the separate operator approval described in
the P1072 brief. This lane performs loopback runs only.

## Driver options

| CLI | Bot-host environment | Default |
|---|---|---|
| `--transport tcp\|udp\|ws` | `ALLEGIANCE_BOT_TRANSPORT` | `tcp` |
| `--ws-url URL` | `ALLEGIANCE_BOT_WS_URL` | required for WS |
| `--ws-origin ORIGIN` | `ALLEGIANCE_BOT_WS_ORIGIN` | required for WS |
| `--ws-fraction 0..1` | `ALLEGIANCE_BOT_WS_FRACTION` | 1 with `ws`; 0 otherwise |
| `--ws-ca-file PEM` | `ALLEGIANCE_BOT_WS_CA_FILE` | system certificate trust |

CLI values override the environment. When the bot-specific transport is absent,
the existing `ALLEGIANCE_TRANSPORT_PROTOCOL` setting remains a fallback; with
no transport settings the default is TCP. `WebSocketEndpoint.FromConfiguration`
validates the route: WSS is accepted for configured hosts; plaintext WS is
accepted only on loopback. Origin is canonical HTTPS, or HTTP on loopback for
local development. Server Origin, identity, password, version and admission
checks still apply. No server bot exemption is added.

An explicit fraction selects a balanced deterministic sequence by stable bot
index: the first N indices contain `floor(N*fraction)` WS choices; the rest use
TCP. Probe and commander slots have reserved indices. Worker indices stripe the
same sequence; scheduling and random callsigns do not select the route. A
replacement commander keeps its slot, and a refilled bot gets a new bot index.
The P1071 backfill fleet's seat count includes its probe and commanders. A
10-per-side single-driver backfill therefore has exactly 20 connections and,
with fraction 0.5, ten on each route. Worker fleets can have extra control peers;
count actual connections instead of assuming scenario combat counts equal totals.

`[transport] NAME ws URL (origin ORIGIN) index=N` identifies each initial route.
`list_active_bots` includes `transport`, and `get_perf_stats` includes all
ConnectionSession peers under `transports`. Active protocol is `ws` or `wss`
according to the selected URI scheme. Reconnects retain the frozen route,
Origin and CA callback, even if public configuration properties later change.
There is no WS-to-TCP fallback. UDP retains its existing bounded TCP fallback.

The optional CA file contains one CA certificate, not a private key. The bot's
callback builds an X509 chain using only that root, disables automatic certificate
downloads, requires server-authentication usage, and preserves TLS hostname,
validity and signature checks. Local experiment CA revocation lookup is disabled;
no system trust store is modified. Without this option, system validation applies.
Do not use an accept-any callback or a global certificate-validation override.

## Loopback regression and lane comparison

Use the existing artwork configuration in `local.env` or
`ALLEGIANCE_ARTWORK_PATH`. Build with the installed SDK:

```sh
export NUGET_HTTP_CACHE_PATH="${TMPDIR:-/tmp}/p1072-nuget"
"$HOME/.dotnet/dotnet" build AllegianceCore.slnx --disable-build-servers
"$HOME/.dotnet/dotnet" test tests/Allegiance.ClientCore.Tests --no-build --filter FullyQualifiedName~P1072 --disable-build-servers
"$HOME/.dotnet/dotnet" test tests/Allegiance.Core.Tests --no-build --filter FullyQualifiedName~P1072 --disable-build-servers
python3 -m unittest discover -s tools -p 'test_ws_*.py'
```

`P1072WebSocketBotsLiveTests` boots a real loopback server with P1071 backfill,
showcase, two sides and auto-next. It checks the 2v2 all-WS battle, a 10v10
exact split, repeated TCP/WS resumes and frozen configuration. The CA proof
creates two ephemeral roots and a loopback TLS forwarding proxy, connects the
real manager with its configured CA, and refuses another CA and untrusted
system-default certificates. No keys are saved. Admission tests exercise real
upgrades and the source-attempt ledger; shipped defaults remain unchanged.

To reproduce five-minute comparisons, run the theory separately for TCP and
then WS (the default regression duration is five seconds after combat warm-up):

```sh
mkdir -p .scratch/p1072-evidence
export P1072_EVIDENCE_DIR="$PWD/.scratch/p1072-evidence"
export P1072_COMPARE_SECONDS=300 ALLEGIANCE_BENCHMARK_METRICS=1
export ALLEGIANCE_TICK_PROFILER=1 ALLEGIANCE_TICK_PROFILER_INTERVAL=5
"$HOME/.dotnet/dotnet" test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~Comparison_10v10&DisplayName~False' --disable-build-servers
"$HOME/.dotnet/dotnet" test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~Comparison_10v10&DisplayName~True' --disable-build-servers
python3 tools/ws-load-report.py --window-only .scratch/p1072-evidence/tcp.log
python3 tools/ws-load-report.py --window-only --json .scratch/p1072-evidence/ws.log
```

The comparison uses seed 1072, a two-side Brawl and ordinary combat peers, with
50 ms tick cadence and identical settings for both routes. It marks the measured
window after combat begins, flushes profiler windows at both boundaries and
captures both server and bot diagnostics. These are lane numbers: server and
bots share one process/machine; the raw-WS comparison contains no TLS hop. The
separate CA test proves TLS correctness, not TLS throughput.

## Admission budgets for 100 WS clients from one IP

The default is 64 live connections per IP and 128 handshake attempts per IP per
60-second window. Thus a 100-client simultaneous fleet stops at 64. Disconnecting
clients releases live permits, but does not erase attempt history: 100 initial
attempts plus 100 quick reconnects exceed 128. Use an **experiment config** with:

```json
{
  "maxConnectionsPerIp": 128,
  "handshakeAttemptsPerIpPerWindow": 256
}
```

These values admit 100 clients plus control peers and one complete reconnect
wave with some headroom. Global defaults (1024 live, 256 pending, 2048 attempts)
still apply. Larger fleets need a separately calculated budget. `knownProxies`
exempts only physical per-IP budgets for that exact proxy address; forwarded
source budgets, global budgets and pending budgets remain. It is not a bot
exemption. Native TCP has its own per-IP limits; the experiment file sets its
live cap to 256 so the TCP comparison is admitted too.

## Rig preparation (files only on this lane)

The separate `docker/rig/compose.websocket-experiment.yml` project follows the
one-hop route in `docker/public/Caddyfile`: Caddy terminates TLS, overwrites
forwarding headers and forwards `/game` via HTTP/1.1 to the private server
listener. It uses an internal CA. The exact trusted proxy is `192.0.2.2`, on an
experiment network in the documentation range; the server is `192.0.2.3`.
No WS port is published directly. The default host bindings are loopback.

Preserve the rig's existing private override. Do not replace it with these
files. The standalone experiment uses a copied server config derived from
`docker/public/server.perpetual.example.json`; it retains the legal random-civ
preset, maxTeamImbalance and auto-next rules, but has no lobby/identity issuer.
Its benchmark and civilization seeds are fixed at 1072 for reproducible A/B starts.
It is intended for isolated load testing, not as a public production config.

After approval, the operator supplies artwork, an existing server image, an
experiment DNS name that resolves to the rig and a private copied config:

```sh
export ALLEGIANCE_ARTWORK_PATH=/path/to/artwork  # existing rig artwork, replace locally
export P1072_WS_HOST=load.example.invalid  # replace on the rig
export P1072_SERVER_IMAGE=allegiance-server:local
export P1072_SERVER_CONFIG="$PWD/.scratch/p1072-server.json"
cp docker/rig/server.websocket.example.json "$P1072_SERVER_CONFIG"
python3 - <<'PY'
import json, os
from pathlib import Path
path = Path(os.environ['P1072_SERVER_CONFIG'])
config = json.loads(path.read_text())
config['webSocket']['allowedOrigins'] = ['https://' + os.environ['P1072_WS_HOST'] + ':8443']
path.write_text(json.dumps(config, indent=2) + '\n')
PY
# Set only after approving exposure to the separate bot host.
export P1072_BIND_ADDRESS=198.51.100.10  # replace with the approved rig interface
docker compose -f docker/rig/compose.websocket-experiment.yml config --quiet
docker compose -f docker/rig/compose.websocket-experiment.yml up -d
# Export the certificate, never root.key. Share it with the bot host privately.
docker compose -f docker/rig/compose.websocket-experiment.yml cp ws-proxy:/data/caddy/pki/authorities/local/root.crt ./ws-load-ca.pem
```

The image and Caddy sidecar must already be available to the operator. This
lane does not install packages, build/deploy remote images or run these steps.
The sidecar CA's data volume must persist across runs so pinned clients see the
same root. A new volume requires exporting and distributing its new certificate.

## Rig A/B: TCP, WS, then 50/50

Build the bot tool in Release on the bot host. Use identical seeds, mission
settings, fleet sizes, warm-up and measured duration. Run these as separate
experiments, stopping only PIDs started for that run and resetting the server
between runs. Capture server stdout and every worker stderr file. Set
`ALLEGIANCE_BENCHMARK_METRICS=1` on both server and bot process; set
`ALLEGIANCE_TICK_PROFILER=1` on the server for tick windows.

```sh
export ALLEGIANCE_BENCHMARK_METRICS=1
bot_script=tools/allegiance-mcp/scripts/bots-spawn-load.sh
# This example names a documentation-only host. Replace it after approval.
bash "$bot_script" load.example.invalid 6073 --transport tcp --i-mean-it-host load.example.invalid
bash "$bot_script" load.example.invalid 6073 --transport ws --ws-url wss://load.example.invalid:8443/game --ws-origin https://load.example.invalid:8443 --ws-ca-file ./ws-load-ca.pem --ws-fraction 1 --i-mean-it-host load.example.invalid
bash "$bot_script" load.example.invalid 6073 --transport ws --ws-url wss://load.example.invalid:8443/game --ws-origin https://load.example.invalid:8443 --ws-ca-file ./ws-load-ca.pem --ws-fraction 0.5 --i-mean-it-host load.example.invalid
```

`bots-spawn-load.sh` defaults to ten total seats per side; append `--seats 50`
for 100 total connections. `bots-spawn-perpetual.sh` keeps its original five-seat
default and optional positional installed client version, with the same transport
flags. Both scripts refuse every non-loopback TCP or WS target unless the exact
host is acknowledged with `--i-mean-it-host HOST` (repeat it for different TCP
and WS hosts). The guard also checks environment-supplied WS URLs. No DNS lookup
is used to bless a remote hostname as loopback. Workers (`--workers N` / internal
`--worker i/N`) are supported by the MCP driver; P1071 backfill itself retains its
single-driver restriction. Child and recovered worker processes receive the same
explicit transport options.

Against the VPS later, use the existing same-origin `wss://<public-origin>/game`
route and its exact HTTPS Origin. Omit `--ws-ca-file` for the normal public CA;
keep the installed release's client version and all ordinary server admission
checks. Never change the public server defaults just to admit a fleet without
separate operator approval.

## Reading results and limits

```sh
python3 tools/ws-load-report.py server.log bot.log err.worker-*.log
python3 tools/ws-load-report.py --json server.log bot.log err.worker-*.log > comparison.json
```

Choose logs for one experiment and the same measured interval; do not pass the
same worker log twice. The script does not deduplicate overlapping log captures.
`--window-only` is for lane comparison files with explicit start/end markers;
it retains initial logon measurements outside those markers. Tick percentiles
merge the profiler's one-microsecond histogram bins across windows; they are
not averages of window percentiles. Tick timing belongs to the whole server,
so a mixed experiment has one tick distribution, not separate TCP/WS tick costs.

Sends/drops count carrier frames, and bytes include TCP or WS carrier headers
(socket acceptance, not network delivery). Coalescing is distinct from dropping.
TLS/proxy handshake and ciphertext overhead are outside these backend byte counters.
Queue high-water marks are the largest individual peer backlog observed at
enqueue, including in-flight data, not the sum across the fleet. Disconnect
causes are recorded independently of the pre-existing disconnect-summary drain.
Idle and fragment timeout attribution preserves existing disconnect behavior.
Logon time spans connection start through the validated ACK observed by the
manager's short polling loop. Bot update age is the gap since the local arrival
of the flying own-ship pose (`ageSource=ownPose`); docked control peers report
no pose-age sample. Plain idle/weave clients instead
report the last ship-update frame (`ageSource=shipUpdate`). It is not one-way latency or browser render lag. Quiet peers can have older poses while their connection is healthy. The
separate read-age field helps distinguish this. Reconnect metrics count attempts.

This test does **not** measure browser-side WASM frame time, input handling,
rendering cost or browser memory pressure. Those require a Playwright harness
and real browsers, a handful at a time. Performance ideas discovered here belong
in a later default-off rig A/B; P1072 makes no transport performance change.

## Native intent

Native uses DirectPlay client joining (`clintlib/clintlib.cpp:1317-1320`) and
server hosting (`FedSrv/AdminServer.cpp:463-470,525-532`). It has neither a
WebSocket transport nor an MCP bot driver. These peers stand in for ordinary
native connections and reuse existing wire messages and game rules.

## Modern approach

Explicit browser-compatible routes, strict per-bot opt-in CA trust, deterministic
mixed fleets and existing profiler/egress records make the browser-facing server
path measurable without a new security exception or a new game rule.
