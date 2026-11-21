# Browser battle host and visible ships — P1065

Started **2026-11-20 21:09**, lane local time (UTC−5). Finished **22:00**, lane local time. Six-hour cap: 2026-11-21 03:09. No commits, pushes, package installations or public-host access. The brief is retained only in ignored scratch.

## Browser availability — checked first

**Node exists (v22.23.5). Chromium is unavailable.** The P1051 harness's `.scratch/p1051/npm/node_modules/playwright` and `@sparticuz/chromium` directories do not exist, and the standard system Chromium/Chrome binaries are absent. No packages were installed. Browser runs, WASM heap measurements, browser screenshots and their visual review are **for Claude**, using the commands below. Desktop/server and managed tests are identified separately throughout this report.

| Brief item | Result |
|---|---|
| 0 — Browser check | Done; absent modules/binary, Node available. |
| 1 — Battle host | Implemented and exercised: authenticated capped bots, adjacent battle sector, reserved pilot seat, exact owned-process cleanup. |
| 2 — Visible ships | Implemented: contact before mesh, radius-sized stand-in, verified on-demand hull closures, shared visibility gate. All 179 hull models independently decoded. Browser rendering proof is for Claude. |
| 3 — Per-frame cost | Implemented: keyed hulls, wire-maintained sector membership, reused contact/name/snapshot buffers, rock culling and constant-time spin-clock update. Managed microbenchmark below. |
| 4 — Proof and gates | Unit/integration proofs, publication, builds and suites below. Final Fast completed with zero failed assertions and a timing-only red result. Chromium battle/pacing/heap/captures are for Claude under the brief's explicit unavailable-browser path. |

## Ships, visibility and delivery

`src/Allegiance.ClientCore.Game.Browser/BattleShipScene.cs:23` gathers only `ClientWorld.ShipsInSector`. Membership is maintained when wire application changes `ShipEntity.SectorId`, adds/removes a ship, or clears the table (`src/Allegiance.ClientCore/State/SharedBotWorld.cs`, browser-only branches; `BrowserSectorShips.cs`). The desktop compiles its original property/table behavior. The scene uses the existing `ShipEntity.CanDrawInSector`: no dead/gone/unknown ship, docked state, parented gunner, missing pose, or strategic sector unconfirmed by in-view telemetry reaches either drawing, contacts or hull requests. A strategic SHIP_STATUS sector alone is insufficient. The nearby-ship camera uses the same gate.

Desktop reference: `src/Allegiance.ClientCore.Game/Game1.Rendering.cs:4481` excludes hidden/unknown/parented or other-sector ships from radar; `Game1.FlightInput.cs:2882` uses `CanDrawInSector` for hull/effect gathering. Native intent: `clintlib/appmsg.cpp:475-488` moves departed ships into the null cluster; `WinTrek/trekigc.cpp:2167-2169` maintains scanner visibility. No new simulation, detection or wire rule is added. Tracking: [parity matrix](../parity/p1065-browser-battle-ships.csv).

`Game1.Flight.cs:384` creates each HUD contact before mesh resolution. Its lists and `HudSnapshotBuffers` are reused (`:113`, `:458`), and both Classic and Modern consume this same model. An unavailable or undecodable mesh draws a side-colored cube through the existing procedural box renderer. Its side is `hull.Length / sqrt(3)`, so the corner radius is exactly `hull.Length / 2` (`BattleShipScene.cs:40`), matching native `Igc/shipIGC.cpp:1419`; unknown hulls use a small defensive fallback. Mesh bytes becoming resident invalidate the readiness cache through `StagedAssetSource.ContentRevision`. Original meshes replace the stand-in on the next successful renderer lookup. An asset failure never removes the contact.

`tools/p1065-battle-packs.py` reads the resident core's hull/missile IDs, model names and texture overrides, follows original MDL namespaces/image imports, and writes original bytes at `packs/<sha256>`. It neither changes nor expands the join manifest. `BattleManifest.cs` validates names, complete closures, consistent hashes/metadata, visual-only file kinds and the existing file/pack bounds. `Assets/BattlePackStreamer.cs:31` deduplicates requests; `:52` stages a complete closure and `:54` merges it atomically. Size, content type and SHA-256 are checked on both cache hits and downloads through the existing `AssetStager`; a failed/stale/corrupt fetch leaves the stand-in and retries with bounded exponential backoff. Cancellation drains the worker before teardown. Hull requests begin only for the visible roster during flight; there is no hull prefetch in the join path.

The Blazor owner starts/stops the worker with sector streaming (`Pages/Menu.Sectors.cs:19,58`), retains current-generation battle hashes during cache manifest commits (`Menu.Online.cs:130-132`), and shares the existing global four-download semaphore and 64 MiB/1,024-entry persistent-cache limits. Actual raw asset and decoded renderer residency is separate from that persistent limit: loaded hulls remain resident for the visit. The browser heap qualification is therefore still material and is **for Claude**; no heap limit or measured browser number is inferred from the cache limit.

All **56 core missile-type closures** use this delivery path, requested for current-sector missile telemetry. The two missing effect surfaces have declared, fetchable closures. Their original bytes fit the staging bounds, but effect drawing and GPU upload remain task B: `exp22bmp` is 15,360 pixels wide and needs tiling before upload. No projectile sprites, explosion renderer, engine effects, lighting, targeting or sound were added.

## Pack sizes

Fresh final Release publication, measured from original files and deterministic gzip outputs. [Publication receipt](BROWSER_BATTLE_SHIPS_P1065_assets/publication.json); [independent C# verification and geometry audit](BROWSER_BATTLE_SHIPS_P1065_assets/pack-audit.json).

| Content | Raw bytes | Gzip bytes |
|---|---:|---:|
| Unchanged join assets (601 names / 577 unique hashes) | 32,536,380 | 7,564,835 |
| Browser runtime | — | 6,182,705 |
| **P1054 join + runtime metric** | — | **13,747,540** |
| P1054 budget | — | **14,680,064** |
| All additional battle assets (391 unique files) | 166,834,430 | 49,164,045 |
| `exp22bmp` closure, included above | 15,728,756 | 2,024,216 |
| `fx18bmp` closure, included above | 8,388,720 | 624,251 |

The 237,446-byte mutable battle index declares **447 hull IDs / 179 distinct models**, **56 missile IDs** and **2 effect packs**. Clients fetch individual closures rather than that entire catalog. Hull closures add 0 bytes when already resident; additional compressed closures range from 12,801 to 4,825,156 bytes. Shared files are not fetched again once promoted. The P1054 budget metric counts pack + runtime transfer, as before; mutable manifest/application metadata is separate. The battle builder verifies that the join manifest itself stays byte-identical. The final artwork provenance includes all on-demand files.

The independent C# audit validated the manifest, verified all 391 staged files and parsed **179/179 original hull models to nonzero triangles**, with **zero unsupported geometry nodes** and no missing missile IDs. This proves original geometry availability/decodability, not WebGL rendering.

## Battle host and identity

`tools/p1051-browser-host/BattleOptions.cs` adds `--bots 0..10` and optional `--seconds 1..21600`; the default has no bots. Bad, missing, duplicate or over-cap options fail before starting processes. `BattleBots.cs:49` uses the existing `scripts/mcp_driver.py` / `allegiance-mcp` `spawn_scenario`, sets `target_sector: "Ixion"`, and reserves a friendly pilot seat through `despawn_bot`. There are N−1 friendly bots, N opposing bots and one human pilot, so the flying battle is N v N, including at N=1. The MCP probe stays docked. The host verifies the authored map actually puts Ixion one aleph from Alpha's home before starting the driver. Fleet variety and explicit proof wallet/tech grants use the existing driver/server fixture facilities.

**Decision: enroll the bots; keep anonymous admission disabled.** `Program.cs:83` retains `AllowAnonymous=false`. The existing lobby fixture creates real accounts and its production signer issues ordinary, audience-bound tickets (`:96`). MCP's optional `--logon-file` supplies the build version, game password and local broker directory; the option refuses non-loopback destinations. There is no new production authentication setting or listener. This avoids weakening the production identity rule even for the battle run. The broker and files exist only inside the proof tool/ignored scratch, and `p1061-deploy-check.py` additionally rejects proof flags/broker references in public deployment placeholders. No `docker/public/*` file changed.

The driver atomically exports command results and a ready/PID file, and its SIGTERM/INT `finally` terminates/waits for its exact owned MCP PID. MCP owns its worker PIDs. The host owns the driver and TLS proxy, handles stop files/signals/time limits and waits for exit (`BattleBots.cs:71`, host `Program.cs:342`). No process-name matching or global process kill is used. The first combat run exposed an existing unsynchronized enumeration of the proof desktop's projectile list; the host's navigation and telemetry snapshots now hold `desktop.Gate` (`:223,330`). This is a proof-host fix, with no desktop client source edit.

## Desktop/server battle evidence — no browser renderer

A fresh **240-second** host run completed cleanly with lobby enrollment, required identity, enforced version `2.0.0`, a game password, `/play` and the existing TLS/WS proxy configured. The ordinary TCP headless desktop pilot took the reserved side-0 seat and crossed the real aleph into Ixion using normal controls. No server position override was used. The public-shape listing was verified and advertised `wss://localhost:8443/game`; an old-version TCP logon was refused by the unchanged server gate.

[195 telemetry samples spanning 193.879 seconds](BROWSER_BATTLE_SHIPS_P1065_assets/server-battle.json) show **10 flying combatants on each side**, up to **18 authoritative ships in the viewer's sector**, and up to **18 visibility-eligible client ships**. That peak spans 12 hull IDs / 10 distinct models, including Hvy Scouts (`fig05`, `fig21`, `fig13`), Adv Fighter (`quizfigv2`), stealth fighters and the pilot's `quizscoutv2`. Samples reached **62 live projectiles**, and the client received **3,059 projectile events**. These contact counts use the shared visibility predicate; they are not measured desktop HUD draw rows. Lower visible counts than authoritative sector counts are expected under fog and departures.

| Evidence | Ships / eligible contacts | Drawn meshes / stand-ins |
|---|---|---|
| Managed staging fixture, held/cold/failure | 2 / 2 throughout | 2 stand-in decisions become 2 mesh-byte-ready decisions; GPU drawing not exercised |
| Real headless TCP battle, peak sample | 18 / 18 | Not measured by this headless client |
| Actual browser battle | **for Claude** | **for Claude** |

## Per-frame cost

The old loop scanned all known ships and performed a linear hull search plus closure/list/name-array allocations for every frame. The new gather visits only indexed sector members, uses a hull dictionary, and reuses lists. Readiness checks are cached until asset promotion. HUD contact values stay in reused list/snapshot storage. Counter arrays/names are produced only when diagnostics are requested.

Rock drawing rejects camera-relative bounding spheres by frustum and 10,000-unit far plane before geometry/texture access (`Game1.Flight.cs:425`). Update advances one sector clock (`:296`); `BrowserRockSpins.cs` evaluates rate × elapsed simulation seconds only for drawn rocks, preserving phase while culled and respecting time dilation. New discoveries and mission/session reset are covered. Native spin intent: `Igc/modelIGC.cpp:85-99`.

[Microbenchmark](BROWSER_BATTLE_SHIPS_P1065_assets/benchmark.json): actual browser scene selection compiled as a managed **Release JIT** harness, tiering disabled, 20,000 measured passes after warmup. It measures selection, hull lookup and contact/name collection; excludes projection, prediction, asset preparation, HUD rendering and GPU work. Before assumes meshes exist to isolate the allocation/search cost. These are **not interpreter frame times**.

| Flying ships | Known off-sector ships | Before µs/pass | After µs/pass | Before allocated bytes/pass | After |
|---:|---:|---:|---:|---:|---:|
| 2 | 0 | 0.728 | 0.134 | 1,072 | 0 |
| 10 | 0 | 7.436 | 0.579 | 6,160 | 0 |
| 20 | 0 | 26.138 | 1.097 | 12,800 | 0 |
| 20 | 500 | 30.684 | 1.077 | 24,800 | 0 |

| Browser battle size | RAF p50 / p95 | WASM linear / managed heap | Qualification |
|---|---|---|---|
| 1 v 1 | for Claude | for Claude | Headless correctness; not GPU-representative |
| 5 v 5 | for Claude | for Claude | Headless correctness; not GPU-representative |
| 10 v 10 | for Claude | for Claude | Headless correctness; not GPU-representative |

`FlightMetrics.battle` exposes known sector members, eligible contacts, drawn meshes, stand-ins, hull IDs, culled rocks and ship-pass time. Blazor diagnostics expose completed/retried packs and promoted bytes. The workstation harness instruments actual `WebAssembly.Memory` buffers before runtime startup and also records the existing managed-heap counter; it does not substitute JavaScript heap for WASM memory.

## Gates

| Check | Result |
|---|---|
| Solution build | **0 warnings / 0 errors**, disabled build servers/node reuse/shared compilation |
| `tools/build-tools.sh` | **149 projects built**, 0-warning/error builds |
| New browser battle tests, alone ×3 | **27 passed ×3** |
| New bot identity tests, alone ×3 | **5 passed ×3** |
| Python pack/deploy guard regressions, alone ×3 | **3 passed ×3** |
| Full browser game-model project | **118 passed** |
| Browser preview project | **12 passed** |
| Lobby suite | **287 passed, 3 optional PostgreSQL skips** |
| Deploy checker | **PASS, 45 checks**; Docker unavailable on this lane |
| Release publication + independent original-byte/geometry audit | **PASS** |
| Desktop output filenames vs independently built HEAD archive | **753/753 identical**, no added/removed paths |
| Desktop HUD zero-delta comparison | **1,848/1,848 passed**, 0 pixel/semantic differences, 84 animation-only regions, 0 corrupt goldens |
| Glossary / parity / whitespace | Passed; final recheck before handoff |
| Final Fast tier, run last | **15,726 passed / 0 failed assertions / 7 not executed**; **red for 3 timing offenders** |

[Desktop HUD comparison](BROWSER_BATTLE_SHIPS_P1065_assets/desktop-hud.json), [desktop filename comparison](BROWSER_BATTLE_SHIPS_P1065_assets/desktop-filenames.json) and [baseline source witnesses](BROWSER_BATTLE_SHIPS_P1065_assets/baseline-sources.json) retain hashes. The baseline was independently compiled from `git archive HEAD`, including the unmodified shared client-core files. The HUD harness's embedded `Dirty=true` reflects launching inside the task worktree; it does not mean the baseline used edited source. No file under `src/Allegiance.ClientCore.Game/` or any desktop HUD test was edited.

The final Fast sweep completed **15,733 cases in 557.5 s**, with all nine infrastructure checks passing. Its only errors were passing tests taking 13.925 s (`SC2NaniteSupportTests`, civ 25), 12.411 s (`P814GunnerTests`) and 12.987 s (`P750NativeAsteroidTypesTests`) against the calibrated 11.521 s limit. Each existing timing family then ran in **three fresh isolated processes**: all **12 isolated test rows passed**, with maximum case time 3.974 s. No threshold, assertion, timeout or tier changed. **The Fast gate remains red for timing**, recorded in [the flake ledger](../TEST_FLAKES.md), [sweep receipt](BROWSER_BATTLE_SHIPS_P1065_assets/fast-summary.json) and [three-run isolation receipt](BROWSER_BATTLE_SHIPS_P1065_assets/fast-isolated.json).

[Verification accounting](BROWSER_BATTLE_SHIPS_P1065_assets/verification.json) retains repeated-test TRX hashes, suite counts, publication/desktop checks and process-exit outcomes. The final battle host, driver, workers, proxy, HUD harnesses and test processes have exited. Remaining work is the explicitly deferred Chromium battle/rendering/pacing/heap run and opening/describing its captures; no browser results or images are claimed here.

## Exact workstation browser work — for Claude

No npm/NuGet package installation. Use the workstation's already-installed P1051 modules. Run from the repo root; all network endpoints stay on loopback.

```bash
set -euo pipefail
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1065/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork  # use the workstation's existing artwork path
export BROWSER_CLIENT_VERSION=2.0.0
NPM_DIR=.scratch/p1051/npm                         # existing playwright + @sparticuz/chromium

test -d "$NPM_DIR/node_modules/playwright"
test -d "$NPM_DIR/node_modules/@sparticuz/chromium"
for project in tools/p1051-browser-host tools/allegiance-mcp src/Allegiance.ClientCore.MonoGame src/Allegiance.ClientCore.Browser; do
  "$DOTNET" build "$project" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false \
    -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false
done

# Unpack the checked-in, previously recorded native open witnesses.
mkdir -p .scratch/p1065/traces
python3 - <<'PY'
import gzip, json
from pathlib import Path
traces = json.loads(gzip.decompress(Path('docs/reports/BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz').read_bytes()))
for name, trace in traces.items():
    target = Path('.scratch/p1065/traces') / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(trace))
PY

# Choose a NEW output directory; the publisher refuses to overwrite one.
scripts/publish-browser-flight.sh .scratch/p1065/browser-generation \
  .scratch/p1065/traces/opens/Classic.json .scratch/p1065/traces/opens/Modern.json \
  .scratch/p1065/traces/flight-opens4/Classic.json .scratch/p1065/traces/flight-opens4/Modern.json \
  .scratch/p1065/traces/flight-miners0/Classic.json .scratch/p1065/traces/flight-miners1/Modern.json

for size in 1 5 10; do
  state_dir=$(mktemp -d "$PWD/.scratch/p1065/browser-host-$size.XXXXXX")
  output_dir="$PWD/.scratch/p1065/browser-$size"
  if [[ $size == 10 ]]; then output_dir="$PWD/docs/reports/BROWSER_BATTLE_SHIPS_P1065_assets/browser"; fi
  "$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
    "$ALLEGIANCE_ARTWORK_PATH" .scratch/p1065/browser-generation "$state_dir" \
    --bots "$size" --seconds 1200 > "$state_dir/host.log" 2>&1 &
  host_pid=$!
  cleanup() { touch "$state_dir/stop"; wait "$host_pid" || true; }
  trap cleanup EXIT INT TERM
  result=0
  node tools/p1065-browser-battle.cjs "$NPM_DIR" "$state_dir" "$output_dir" \
    .scratch/p1065/browser-generation 60 || result=$?
  cleanup
  trap - EXIT INT TERM
  [[ $result == 0 ]] || exit "$result"
done
```

The new harness uses the existing `/play/menu.html?dev=1`, passkey registration, typed password, side/launch buttons, ordinary waypoint/aleph autopilot, and the actual production session/renderers. It holds deferred mesh responses to observe contacts/stand-ins, injects one 503, releases delivery and observes successful retries/meshes. It then records four 60-second style/size profiles per battle, server/visible/drawn counts, hull diversity, frame pacing and memory. It closes its browser in `finally`; the shell stops/waits for the exact host PID, which owns the remaining processes. The script is **syntax-checked but not runtime-qualified on this lane**. A small battle still depends on ordinary bots discovering/traversing the route; a timeout or pilot loss must be reported/retried, never relabeled as successful combat evidence.

Captures requested by the brief are **for Claude**: Classic/Modern at 1920×1080 and 800×480. Only the 10 v 10 capture set goes under the report assets; the harness enforces ≤2 MiB for that set. **No requested browser PNG exists on this lane, so none was opened or visually described.** Claude must open every resulting PNG, describe only the visible ships/fight/HUD, and append those descriptions and the measured browser table. The HUD baseline images are internal scratch comparison inputs, not substitute browser captures.

Reproduce available non-browser evidence:

```bash
"$DOTNET" build tools/p1065-battle-proof -c Release --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false \
  -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false
"$DOTNET" tools/p1065-battle-proof/bin/Release/net10.0/p1065-battle-proof.dll \
  --audit .scratch/p1065/browser-generation .scratch/p1065/pack-audit.json
DOTNET_TieredCompilation=0 "$DOTNET" tools/p1065-battle-proof/bin/Release/net10.0/p1065-battle-proof.dll \
  --benchmark "$ALLEGIANCE_ARTWORK_PATH" .scratch/p1065/benchmark.json
python3 tools/test-p1065-battle-packs.py
python3 tools/p1061-deploy-check.py
```

## Native intent / Modern approach

**Native intent:** show only current scanner-visible ships in the viewed sector, preserve their radar eligibility independently of artwork, and size hull presentation from the native half-length radius. Reuse the existing shared visibility and spin rules, with the native citations and parity rows above.

**Modern approach:** keep the join small, deliver verified original visual closures asynchronously, preserve a contact and a simple solid stand-in through failures, and keep frame work proportional to the viewed sector. Desktop rendering/HUD behavior and production authentication stay unchanged. Browser combat rendering, pacing, heap and screenshot review remain explicitly **for Claude** because this lane has no Chromium.
