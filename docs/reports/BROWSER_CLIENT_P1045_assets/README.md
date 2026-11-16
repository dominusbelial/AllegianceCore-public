# P1045 evidence and reproduction

The two patches recreate throwaway code only under `.scratch/p1045/`. Neither changes a tracked source, solution or compose file. They contain no artwork binaries or credentials. Apply from the repository root in a disposable checkout; reserve the documented loopback ports. No production endpoint is used.

| Evidence | Meaning |
|---|---|
| `spike-render.patch` | KNI browser head, original-parser inventory helper, compile-only shared-source compatibility check, capture and byte-measurement scripts, pinned npm lockfile |
| `spike-network.patch` | Loopback-only WebSocket/TCP bridge, async client transport, real ConnectionSession harness and fresh-stack runner |
| `render-*.png` | Actual Chromium screenshots; both opened and inspected; neither is a complete Game1/HUD capture |
| `render-metrics.json` | Warmup/sample counts, CPU submission/RAF times, first draw submission and browser/renderer |
| `bundle-metrics.json` | Whole publish payload bytes and offline gzip/Brotli sizes; not an AOT build |
| `network-metrics.json` | Three independent fresh-stack proofs and all 90 measured ping samples |
| `artwork-inventory.json` | Candidate file lists/hashes and measured categories, full-corpus/desktop-manifest totals, desktop font bake sizes |
| `verification.json` | Final build/test/cleanup receipt; full fast sweep remains failed |
| `evidence-manifest.json` | SHA-256 and byte sizes for this report and its assets, excluding itself |
| `fast-summary.json` | Full fast-tier gate result, including existing failures/timing offenders |
| `isolated-tests.json` | Three isolated executions of each failed or timing-offender display case |
| `source-citations.txt` | Re-grepped repository/native lines supporting the report |
| `kni-compatibility.txt` | Expected failing compile-only diagnostics, machine paths removed |
| `artwork-LICENSE.txt` | Artwork root's redistribution notice, also applicable to depicted covered art |

The patch files are 40,221 and 14,070 bytes, each below 300 KB. PNGs total 148,190 bytes, below 2 MB. Exact hashes and sizes are in `evidence-manifest.json` (which excludes itself).

## Common setup

Use the repository's .NET 10 SDK and read-only artwork configured in `local.env`. The recorded SDK was 10.0.404. Set package caches to scratch when the normal home cache is read-only:

```bash
source ./local.env
export ALLEGIANCE_ARTWORK_PATH
export DOTNET_ROOT="$HOME/.dotnet"
export NUGET_PACKAGES="$PWD/.scratch/p1045/packages"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1045/nuget-http"
export NUGET_SCRATCH="$PWD/.scratch/p1045/nuget-tmp"
```

The benchmark environment exposed 16 Linux CPUs (AMD Ryzen 9 9955HX), approximately 92 GiB memory, and no cgroup CPU quota. Chromium used SwiftShader, not a hardware GPU. No synthetic load was applied. The source/OS file cache was not flushed. Do not interpret this as a minimum player machine specification.

## Render reproduction

```bash
git apply docs/reports/BROWSER_CLIENT_P1045_assets/spike-render.patch
mkdir -p .scratch/p1045/render/wwwroot/art
cp "$ALLEGIANCE_ARTWORK_PATH/fig05.mdl" "$ALLEGIANCE_ARTWORK_PATH/fig05bmp.mdl" .scratch/p1045/render/wwwroot/art/
~/.dotnet/dotnet publish .scratch/p1045/render/P1045Render.csproj -c Release -o .scratch/p1045/render-publish
npm ci --prefix .scratch/p1045/npm --cache .scratch/p1045/npm-cache
```

Use an installed Chromium if available, or the npm-provided headless binary as in the patch. On the minimal study host, NSS/NSPR were missing; the same npm package includes the required libraries in its `al2023.tar.br`. The following preparation makes no OS-wide changes and downloads no separate browser archive:

```bash
TMPDIR="$PWD/.scratch/p1045" node --input-type=module - <<'JS'
import chromium from './.scratch/p1045/npm/node_modules/@sparticuz/chromium/build/index.js';
import fs from 'node:fs';
import zlib from 'node:zlib';
await chromium.executablePath();
fs.writeFileSync('.scratch/p1045/al2023.tar', zlib.brotliDecompressSync(
  fs.readFileSync('.scratch/p1045/npm/node_modules/@sparticuz/chromium/bin/al2023.tar.br')));
JS
mkdir -p .scratch/p1045/al2023
tar -xf .scratch/p1045/al2023.tar -C .scratch/p1045/al2023
python3 - <<'PY'
from pathlib import Path
p = Path('.scratch/p1045/fonts')
p.mkdir(exist_ok=True)
(p/'fonts.conf').write_text('<?xml version="1.0"?><!DOCTYPE fontconfig SYSTEM "fonts.dtd"><fontconfig><dir>'
    + str((p/'fonts').resolve()) + '</dir><cachedir>'
    + str(Path('.scratch/p1045/fonts-cache').resolve()) + '</cachedir></fontconfig>')
PY
```

Run the static server in one terminal, then capture in another:

```bash
python3 .scratch/p1045/render-serve.py
```

```bash
FONTCONFIG_PATH="$PWD/.scratch/p1045/fonts" \
LD_LIBRARY_PATH="$PWD/.scratch/p1045/al2023/lib" \
TMPDIR="$PWD/.scratch/p1045" node .scratch/p1045/capture.cjs
node .scratch/p1045/measure-bundle.cjs
```

The capture script closes every browser it launches. Stop the static server with Ctrl+C after capture. Chromium's package defaults for disabling web security and allowing insecure mixed content are filtered out. The remaining headless/container/SwiftShader switches are not representative of a hardware-accelerated player's browser. This server intentionally lacks the lobby's production CSP and auth; those remain acceptance work in the report.

To reproduce the candidate artwork inventory:

```bash
~/.dotnet/dotnet run --project .scratch/p1045/inventory -- --assets > .scratch/p1045/art-groups.log
python3 .scratch/p1045/measure-assets.py
```

That inventory script records font source bytes. The final JSON additionally includes 17 actual desktop XNB font bakes measured after the final solution build (sum raw 1,909,690 bytes, per-file gzip-9 197,968 bytes). These are the `Content/Fonts/*.xnb` and `Content/HUD*.xnb` outputs; they need separate KNI validation/rebuilding. Categories are planning samples, not a certified complete first-match pack. `.mdl` namespaces and image-script dependencies are followed, with canonical case resolution; rare assets and other modes still need production traces.

The optional compatibility check is **expected to fail**:

```bash
~/.dotnet/dotnet build .scratch/p1045/compat/compat.csproj
```

It links the existing shared Game sources against KNI packages. Its scratch copy adapts only the Game lifecycle hook signatures; the other 23 errors are the second-pass diagnostics retained here. It is not a ported browser client. To see the initial two hook errors, remove the copied Compile item and the original-file exclusion in that scratch project.

## Network reproduction

The bridge is an experiment, **not a deployable public gateway**. It assumes a fixed loopback upstream, does not preserve external-peer provenance, lacks admission/auth/rate/deadline policies and has no production coalescing. Keep it local.

```bash
git apply docs/reports/BROWSER_CLIENT_P1045_assets/spike-network.patch
~/.dotnet/dotnet build .scratch/p1045/network/network.csproj
~/.dotnet/dotnet build src/Allegiance.Server/Allegiance.Server.csproj -c Release
~/.dotnet/dotnet build src/Allegiance.Lobby/Allegiance.Lobby.csproj -c Release
bash .scratch/p1045/run-network.sh
```

This reserves loopback TCP 6045 (real game server), HTTP 8080 (dev-stack lobby), and WS 8046 (bridge). Discovery is disabled and no lobby heartbeat is configured. Each repetition starts a fresh stack and gateway, then tears both down by their owned PIDs. If interrupted, allow the runner's EXIT trap to finish; the explicit cleanup command is:

```bash
DOTNET="$HOME/.dotnet/dotnet" scripts/dev-stack.sh down --name p1045
```

The harness uses the real `ConnectionSession`, with an internal test-factory seam accessed through reflection. It stops automatic keepalive only for the manually paced RTT measurements, retaining the server's one-ping/second limit. These measurements include a 20 Hz game tick; the browser session/runtime/auth flow is not proved by this native .NET harness.

## Removing recreated spikes

After stopping all processes, remove only the recreated scratch source/projects and their output folders. The study did this before its final solution build. Both patches then passed `git apply --check`. Keep the report/evidence directory; do not commit scratch or local identity keys. No stash, commit or push is needed.
