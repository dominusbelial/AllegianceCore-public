# Private benchmark collector

TypeScript Cloudflare Worker + D1; code only. No deployment or account is needed for tests.

```sh
cd tools/bench-collector
npm ci
npm run build
npm run typecheck
npm test
npm run migrate:local
npm run dev
```

The tests execute the real Worker bundle, D1 migration, rate-limit bindings and scheduled
retention through Miniflare/workerd. AJV validators are generated during build so no runtime
eval/reflection is needed. Generated validators, bundles, dependencies and local D1 files are ignored.

`POST /v1/results`, `POST /v1/kept`, `DELETE /v1/install/:id` are write-only. All other reads require
`ADMIN_TOKEN` at `GET /v1/admin/export`. Use `?view=results|kept|aggregate&format=json|csv`.
Raw exports paginate with `limit` (maximum 1000) and `after`; JSON returns `next`, CSV returns
`X-Next-Cursor`. CSV's payload column contains exact JSON with standard CSV escaping. Aggregate
exports contain CPU/GPU class, preset, sample count and median FPS. Headless rows never contribute
a fabricated FPS. Retained histogram samples use 0.1-FPS bins; medians weight sample counts.

The configured rate limits are 60 requests per IP/minute and 10 per install/minute; Cloudflare's
binding is approximate and local to its point of presence, not a global strict quota. The IP key
is a transient minute-scoped hash. D1 contains neither IPs nor rate-limit keys. No application logs
are emitted. Keep invocation logs, request logs and Logpush disabled when provisioning.

See [deployment steps](../../docs/benchmark/DEPLOY.md), [privacy](../../docs/benchmark/PRIVACY.md),
and [wire schema](../../docs/benchmark/RESULT_SCHEMA.md).
