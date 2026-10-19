# Collector deployment handoff (Claude / Domingo)

BS-1 supplies code and local tests only. No login, cloud database, secret or deployment has been
performed. Start with a clean review of the private-data configuration below.

1. Install Node.js supported by the pinned Wrangler lockfile and .NET 10 for the client. From the
   repository root, build and test locally (no account required):

   ```sh
   cd tools/bench-collector
   npm ci
   npm run build
   npm run typecheck
   npm test
   npm run migrate:local
   ```

2. Log in and provision D1 in the intended Cloudflare account:

   ```sh
   npx wrangler login
   npx wrangler d1 create benchmark-stats
   ```

   Copy the returned `database_id` into `wrangler.toml`, replacing
   `REPLACE_WITH_D1_DATABASE_ID`. Choose unused positive rate-limit namespace numbers in the
   target account and replace the sample 1001/1002 if needed. No account ID is committed. Keep
   the provisioned database ID in the deployment configuration according to the operator's policy.
   Never commit API credentials, account IDs, `.dev.vars` or tokens.

3. Apply the migration to the new database, and enter a strong random admin token at Wrangler's
   interactive prompt (do not put it in a command argument, source file or issue):

   ```sh
   npx wrangler d1 migrations apply benchmark-stats --remote
   npx wrangler secret put ADMIN_TOKEN
   npx wrangler deploy
   ```

   Keep `[observability] enabled = false`. Do not enable Workers invocation logs, request logs,
   Logpush, tracing that captures headers/URLs/bodies, or a dashboard integration that stores client
   IP addresses. The application stores only validated payloads in D1, and uses the IP transiently
   for rate limiting. Review provider-level handling separately; do not claim that the provider
   never processes IPs. Check that the daily `17 3 * * *` cron is installed for 24-month retention.
   Use HTTPS; loopback HTTP is accepted only for local development.

4. Verify the deployed write-only API using synthetic data, then remove the synthetic installation.
   Use a freshly generated pair of lowercase UUIDv4 IDs in
   `docs/benchmark/example-result.json`; POST with `Content-Type: application/json` to
   `/v1/results`, then DELETE `/v1/install/<installId>`. Verify an unauthenticated
   `/v1/admin/export` returns 401. With the secret supplied securely to your HTTP client, test:

   ```text
   GET /v1/admin/export?view=results&format=json&limit=1000
   GET /v1/admin/export?view=kept&format=csv
   GET /v1/admin/export?view=aggregate&format=json
   Authorization: Bearer <ADMIN_TOKEN>
   ```

   Follow JSON's `next` cursor with `after=<cursor>` until null; for CSV, use `X-Next-Cursor` until
   empty. Do not publish exports. Delete removes raw rows and follow-ups for the install, while
   irreversible anonymous histograms remain. Backups/exports must follow the same 24-month/deletion
   policy; manual exports are the administrator's responsibility. Daily retention is implemented
   in the scheduled handler and exercised locally by the tests.

5. After successful deployment, set the client default **in code**:

   ```text
   src/Allegiance.SinglePlayer/Benchmark/BenchmarkSharing.cs
   public const string DefaultCollectorUrl = "https://<deployed-worker-origin>";
   ```

   This remains empty in BS-1. A per-user override lives in
   `ApplicationData/AllegianceCore/player.json` (`PlayerConfig.DirectoryPath`):

   ```json
   {
     "benchmark": {
       "collectorUrl": "https://<deployed-worker-origin>"
     }
   }
   ```

   Merge the `benchmark` property into existing player preferences. Omitting it uses the build
   default; explicitly setting `collectorUrl` to `""` disables requests and hides sharing controls.
   Invalid/insecure external HTTP URLs are treated as disabled. For local work use
   `http://127.0.0.1:8787`. The queue remembers each request's destination; changing collector
   requires fresh consent for uploads to that destination and rotates the install ID. Selecting
   Yes after such a change queues deletion at the old destination before new uploads.

6. Rebuild the whole solution and exercise the rendered benchmark: confirm No is the initial
   choice, inspect V's JSON, choose Yes, restart to confirm consent persistence, change to No in
   Options → Settings, and exercise deletion while offline and after reconnection. Client tests
   can be run with networking disabled on a host that permits network namespaces:

   ```sh
   unshare -rn dotnet test tests/Allegiance.ClientCore.Tests --no-build \
     --filter 'FullyQualifiedName~BS1BenchmarkSharingTests&FullyQualifiedName!~Real_loopback'
   ```

   Network namespaces are prohibited in the BS-1 container; see the task report. Loopback HTTP
   and real unreachable-endpoint tests run without relying on an external service. Headless command
   benchmarks remain local diagnostics and do not establish consent or upload; use the rendered
   client to manage sharing and its follow-up sessions.

## Local development and schema changes

Run `npm run dev` after local migrations. To test authenticated exports locally, create the ignored
`tools/bench-collector/.dev.vars` with a development-only `ADMIN_TOKEN`; never reuse production's
secret. The D1 placeholder works with local Wrangler and no Cloudflare login. No public read page
or dashboard is provided.

Contract changes require a new schema version when incompatible. The collector validates against
`docs/benchmark/result.schema.json` via precompiled AJV validators; `npm run build` regenerates
those validators before bundling. To check the live .NET serialization against the collector:

```sh
# Repository root:
BS1_PAYLOAD_PATH=/tmp/bs1-client-payload.json dotnet test tests/Allegiance.ClientCore.Tests \
  --filter FullyQualifiedName~Schema_round_trip_preserves_P729_metrics_and_excludes_identity
cd tools/bench-collector
BS1_PAYLOAD_PATH=/tmp/bs1-client-payload.json npm test
```

The private aggregate is a weighted median per CPU vendor/thread-count class, GPU vendor/VRAM
class and preset, combining live raw measurements with retained 0.1-FPS histogram bins. It is a
calibration aid; BS-1 does not modify recommendation policy. Cloudflare's rate-limit binding is
approximate and scoped to a point of presence, so it is not a strict global abuse quota. See
[Cloudflare's rate-limit binding documentation](https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/).
