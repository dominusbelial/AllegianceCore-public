# BS-1 — optional anonymous benchmark sharing

Status: BS-1 implementation complete. **The full light gate is not green: one unchanged siege test remains unqualified.** No commits, pushes, Cloudflare login,
cloud resources, secrets or deployment. Changes remain on `codex/bs1` at dispatch base
`c2d1d22a55ea3d809027cfd976cef48e95e04490`.

## Delivered

- [Schema contract](../benchmark/RESULT_SCHEMA.md), [JSON Schema v1](../benchmark/result.schema.json),
  and a synthetic [source-generated client example](../benchmark/example-result.json). Hardware
  summary, P729's unchanged run/median/auto-tune metrics, used/suggested settings, build version/
  commit, UUIDv4 install/event IDs, and settings-retention follow-ups. Identity-bearing local P729
  diagnostics are never uploaded: named bots become numeric CPU values; hostname, worker PIDs,
  paths, explanations and other free-form diagnostics are excluded.
- `BenchmarkSharePayload.cs` / `BenchmarkSharing.cs`: source-generated serialization, consent
  defaulting to No, install ID created only on opt-in, local result archive, durable ordered queue,
  atomic state replacement, two-second HTTP timeout, exponential retry/backoff, opt-out cancellation,
  offline deletion/reset and endpoint pinning. Hardware probing and all disk/network operations run
  off the game thread. A changed endpoint requires fresh consent and a new install ID; deletion
  at the previous collector precedes new uploads.
- Rendered client: exact requested sharing prompt on results; S opens controls, Y/N remembers
  consent; V displays the exact queued JSON with scrolling and Left/Right selection of results and
  follow-ups; D deletes shared raw data, resets the ID and turns sharing off. Options now opens
  Settings with existing graphics/key-binding access and sharing controls. Empty/invalid URL hides
  sharing and disables all requests. `player.json` accepts `benchmark.collectorUrl`; the build
  default remains empty for the deployment owner to set after deployment. Ordinary player-preference
  saves preserve the collector configuration; malformed optional sharing values disable sharing
  while preserving ordinary player preferences.
- Follow-up observation after three subsequent play sessions or seven elapsed days. Sessions count
  once per client launch after a successful real join/local spectator game. Benchmark joins and
  reconnects do not count. Current settings are compared with suggested settings; only changed
  key names are shared. Observation pauses while benchmark trials temporarily change settings.
- [Cloudflare collector](../../tools/bench-collector/README.md): TypeScript Worker, real D1 migration,
  precompiled strict AJV validators, streamed 32 KB limit, native per-IP/per-install rate limits,
  idempotent ingestion, same-install follow-up validation, deletion, private bearer-authenticated
  paginated CSV/JSON exports, and CPU/GPU-class median FPS aggregates. There is no public read page.
- Daily retention atomically folds expired rendered FPS samples into anonymous weighted histogram
  bins and deletes raw results/follow-ups after 24 months. Aggregate bins retain no install/event
  IDs or individual hardware model names. IP-derived keys are transient rate-limit inputs, never
  D1 rows. Worker invocation observability is disabled and the code logs no request metadata.
- [Privacy notice](../benchmark/PRIVACY.md), player README notice, and
  [exact deployment handoff](../benchmark/DEPLOY.md), including login, database provisioning,
  migrations, admin secret, deployment, post-deploy verification, client URL and logging policy.

P729's recommendation policy, timing definitions, native simulation rules, server and Core sources
are unchanged. This task adds new client/collector behavior rather than inferring a native rule;
there are no new native parity claims requiring C++ file/line citations. The AOT gate is conditional
on Server/Core edits, so it does not apply.

## Verification

| Gate | Result |
| --- | --- |
| Core — one full sweep | 4,879 passed / 14 failed / 0 skipped; all 14 then qualified 3/3 exact-case. Earlier ship-budget recheck failures retained. |
| ClientCore — one full sweep | 1,478 passed / 1 failed / 1 default opt-in skip. The skipped local showcase rotation passed 3/3 when enabled separately. |
| MonoGame — one full sweep | 2,498 passed / 0 failed / 3 unavailable private Meshy-pack skips. |
| New C# tests | **17 new methods / 51 isolated runs / 0 failures**; each new method passed 3× alone. Affected P729, projection, player-config and preference methods also qualified 3× alone. |
| Collector | 10/10 tests passed, and each passed 3× alone with Miniflare/workerd/D1. Build and TypeScript typecheck passed. |
| Showcase never-parks | 5/5 passed. No bot/server/Core/simulation implementation changed. |

The ClientCore failure is the unchanged
`P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station` at line 131: a station was
destroyed, but the match ended on the clock before its side-elimination assertion. **This case
is not qualified under the 3/3 light gate.** Its isolated attempts include passes, repeated
assertion failures and quota-aborted test hosts/processes. The final attempted batch was pass,
assertion failure, host abort. No assertions, deadlines, quarantine or unrelated simulation code
were changed to force a pass.

A focused baseline reproducer copied the exact dispatch fixture and P733 configuration from
`c2d1d22a55ea3d809027cfd976cef48e95e04490` and referenced runtime assemblies whose Core, Server,
Bot, ClientCore and MCP sources are unchanged against that commit. Its first run passed; a
second aborted with an explicit disk-quota exception while writing its scenario log. This is a
focused comparison, not a full baseline checkout/sweep, and does not establish a clean 3/3 rate.
All attempts, including aborted runs and the empty aborted TRX, are retained. See
[the flake ledger](../TEST_FLAKES.md) and [verification summary](BS1_assets/verification.json).

Disk quota caused native-test logging crashes and temporarily prevented new sandbox commands.
The incomplete throwaway snapshot and this task's npm download cache were removed. Closed
scenario logs and full TRX files were gzip-archived outside the repository, with decompressed
bytes verified against their originals. Two installed dependency files temporarily removed to
recover command startup were restored from their pinned, integrity-verified npm tarballs;
TypeScript typechecking then passed again. No source/test data was discarded.

The last commit-metadata restriction accepts only a hexadecimal revision or `unknown`, so custom
informational-version suffixes cannot disclose local paths or branch/user names. It and schema
round-trip passed 3× alone on the final source; the updated .NET payload also passed the collector
contract check. The real offline HTTP fixture now uses `collector.invalid` with proxies disabled,
so it does not need a configured loopback interface; it passed 3× alone. Inspector Yes/No/Delete
keys work while viewing JSON and were checked on the actual client. These small final changes
were qualified separately after the single sweeps; no second full sweep was run.

Raw evidence is kept at `/tmp/allegiance-evidence/BS1/`, outside the repository. The brief's preferred
`~/allegiance-evidence/BS1/` is outside the writable sandbox roots; `/tmp` is the permitted fallback.
Only screenshots and the small final verification summary are retained under `BS1_assets/` (below the 2 MB ceiling).

[Default No](BS1_assets/consent.png) · [JSON viewer](BS1_assets/payload.png) ·
[Deletion/reset](BS1_assets/deleted.png). These show the actual client on a private Xvfb display,
using synthetic measurements. The UI sent the result to local Wrangler/D1; stored data equalled
its preview. Restart preserved Yes. Deletion removed the row and reset the ID/consent. The viewer
uses actual font spacing and wraps long display lines without changing the payload string. Yes and
Delete were also exercised directly from the inspector; deletion reset consent/ID and drained the
local delete queue.

Already verified: whole-solution build with 0 warnings / 0 errors; all 10 collector tests in
Miniflare/workerd/D1; local Wrangler migrations without login; `wrangler dev --local` HTTP smoke
(unauthenticated admin 401, result POST 201, install DELETE 204, public result GET 404). All five never-parks runs passed, and the default-gated local
showcase-rotation test was enabled separately and passed 3/3.

The requested `unshare -rn dotnet test ...` was executed and the container rejected namespace
creation with `Operation not permitted` before starting .NET. No permission escalation is available.
Real HTTP loopback, real unreachable-endpoint, cancellation/timeout and durable-offline-retry tests
provide the available offline evidence. This does not claim a successful network-namespace run.

## Operational notes and limits

- No deployed endpoint exists in this lane. Sharing is hidden by default until Claude/Domingo
  deploys and configures the URL. No public dashboard was added.
- Hardware values unavailable from platform/driver APIs are null. In particular, VRAM capacity is
  available only from suitable Linux DRM drivers; unified-memory/NVIDIA/other drivers can report
  null. Windows/macOS probe code compiles here but is not executed on this Linux host.
- Before first consent, the local JSON draft has `installId: null`; this draft cannot be uploaded.
  Yes creates the random ID. Subsequent inspection displays the exact queued/uploaded string.
  Follow-up events are visible while queued. Existing P729 local diagnostics retain their prior
  format and may contain machine/bot/process diagnostics excluded from sharing.
- The durable queue is polled every 30 seconds while the client runs. Seven-day follow-ups and
  offline deletion resume when the game next runs. Delete removes raw rows/follow-ups for the
  current ID; anonymous aggregates remain. A lost/corrupted ID cannot identify previously shared
  rows. An empty endpoint pauses even pending deletions until configuration is restored.
- Headless command benchmarks remain local P729 diagnostics and do not opt in or upload. Sharing
  consent and follow-up sessions belong to the rendered client.
- Cloudflare's rate-limit binding is approximate and scoped to its point of presence. No IP or
  hash key is written to D1/application logs. Operators must also keep request logging/Logpush
  disabled and apply retention/deletion to any manual exports/backups. Provider infrastructure
  necessarily processes IP addresses for network delivery; the notice says so explicitly.
