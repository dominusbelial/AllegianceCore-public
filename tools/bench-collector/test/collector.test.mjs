import assert from 'node:assert/strict';
import { test, before, after } from 'node:test';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { Miniflare, Log, LogLevel } from 'miniflare';
import { build } from 'esbuild';
import Ajv from 'ajv';
import addFormats from 'ajv-formats';

const schema = JSON.parse(await readFile('../../docs/benchmark/result.schema.json', 'utf8'));
const sample = JSON.parse(await readFile('../../docs/benchmark/example-result.json', 'utf8'));
const ajv = new Ajv({ strict: true }); addFormats(ajv); const validate = ajv.compile(schema);
const secret = 'local-test-secret-only';
let script;
before(async () => {
  const built = await build({ stdin: { contents: `
    import worker from './src/index.ts';
    export default { async fetch(req,env,ctx) {
      if (new URL(req.url).pathname === '/__test_retention') {
        await worker.scheduled({},env); return new Response('ok');
      }
      return worker.fetch(req,env,ctx);
    }};`, resolveDir: process.cwd() }, bundle: true, format: 'esm', platform: 'browser', write: false });
  script = built.outputFiles[0].text;
});
const instances = [];
after(async () => { await Promise.all(instances.map(m => m.dispose())); });
async function setup({ ipLimit = 1000, installLimit = 1000, token = secret } = {}) {
  const mf = new Miniflare({ modules: true, script, compatibilityDate: '2026-07-01',
    d1Databases: ['DB'], bindings: { ADMIN_TOKEN: token }, log: new Log(LogLevel.ERROR),
    ratelimits: {
      IP_LIMIT: { namespace_id: '1001', simple: { limit: ipLimit, period: 60 } },
      INSTALL_LIMIT: { namespace_id: '1002', simple: { limit: installLimit, period: 60 } }
    }
  });
  instances.push(mf); const db = await mf.getD1Database('DB');
  // Execute the real migration, statement by statement (D1 exec expects one line per statement).
  const migration = await readFile('migrations/0001_stats.sql', 'utf8');
  for (const sql of migration.replace(/^--.*$/gm, '').split(';').map(s => s.trim()).filter(Boolean)) await db.prepare(sql).run();
  const event = structuredClone(sample); event.eventId = randomUUID(); event.installId = randomUUID();
  const call = (path, method = 'GET', body, headers = {}) => mf.dispatchFetch(`http://collector.test${path}`, {
    method, headers: { 'Content-Type': 'application/json', 'CF-Connecting-IP': '192.0.2.11', ...headers },
    ...(body !== undefined ? { body: typeof body === 'string' ? body : JSON.stringify(body) } : {})
  });
  const admin = (query = '') => call('/v1/admin/export' + query, 'GET', undefined, { Authorization: `Bearer ${secret}` });
  return { mf, db, event, call, admin };
}

test('schema validates source-generated client JSON and nullable headless metrics', async () => {
  assert.equal(validate(sample), true, JSON.stringify(validate.errors));
  if (process.env.BS1_PAYLOAD_PATH) {
    const actual = JSON.parse(await readFile(process.env.BS1_PAYLOAD_PATH, 'utf8'));
    assert.equal(validate(actual), true, JSON.stringify(validate.errors));
  }
  const event = structuredClone(sample); event.result.median.gpuMs = null;
  event.result.median.fpsAverage = null; event.result.settingsUsed = null;
  assert.equal(validate(event), true, JSON.stringify(validate.errors));
  for (const value of [Infinity, -1]) { event.result.median.gpuMs = value; assert.equal(validate(event), false); }
});

test('real D1 ingestion is idempotent and the exact P729 metrics survive export', async () => {
  const { event, call, admin, db } = await setup();
  assert.equal((await call('/v1/results', 'POST', event)).status, 201);
  assert.equal((await call('/v1/results', 'POST', event)).status, 201);
  const data = await (await admin()).json(); assert.equal(data.rows.length, 1);
  assert.deepEqual(data.rows[0].payload, event);
  const columns = (await db.prepare('PRAGMA table_info(results)').all()).results.map(r => r.name);
  assert.ok(!columns.some(c => /ip|hostname|name|path/.test(c)));
  const csv = await admin('?format=csv'); assert.equal(csv.status, 200); assert.match(await csv.text(), /fpsOnePercentLow/);
});

test('validation rejects unknown fields, invalid types, UUID aliases, kept mismatch and malformed JSON', async () => {
  const { event, call } = await setup();
  for (const mutate of [e => { e.machine = 'private'; }, e => { e.hardware.serial = 'private'; },
    e => { e.result.median.player = 'private'; }, e => { e.schema = 2; }, e => { e.hardware.cpuThreads = '8'; },
    e => { e.installId = e.installId.toUpperCase(); }, e => { e.result.settingsSuggested.battleSize = 999; }]) {
    const invalid = structuredClone(event); mutate(invalid);
    assert.equal((await call('/v1/results', 'POST', invalid)).status, 422);
  }
  assert.equal((await call('/v1/results', 'POST', '{broken')).status, 400);
  assert.equal((await call('/v1/results', 'POST', event, { 'Content-Type': 'text/plain' })).status, 400);
  const badKept = { schema: 1, eventId: randomUUID(), installId: event.installId, resultId: event.eventId,
    timestamp: new Date().toISOString(), kept: true, changedSettings: ['bloom'] };
  assert.equal((await call('/v1/kept', 'POST', badKept)).status, 422);
});

test('body size limit rejects declared and streamed bodies above 32 KB', async () => {
  const { call } = await setup();
  assert.equal((await call('/v1/results', 'POST', ' '.repeat(32769))).status, 413);
  // No Content-Length: Miniflare forwards the stream through the actual workerd request reader.
  const { mf } = await setup();
  const stream = new ReadableStream({ start(c) { c.enqueue(new Uint8Array(20000)); c.enqueue(new Uint8Array(20000)); c.close(); } });
  const result = await mf.dispatchFetch('http://collector.test/v1/results', { method: 'POST', body: stream, duplex: 'half', headers: { 'Content-Type': 'application/json' } });
  assert.equal(result.status, 413);
});

test('per-install rate limiting applies across distinct IPs', async () => {
  const { event, call } = await setup({ installLimit: 2 });
  for (let n = 0; n < 2; n++) { event.eventId = randomUUID(); assert.equal((await call('/v1/results', 'POST', event, { 'CF-Connecting-IP': `192.0.2.${n + 1}` })).status, 201); }
  event.eventId = randomUUID(); assert.equal((await call('/v1/results', 'POST', event, { 'CF-Connecting-IP': '192.0.2.99' })).status, 429);
});

test('per-IP rate limiting applies across distinct install IDs', async () => {
  const { event, call } = await setup({ ipLimit: 2 });
  for (let n = 0; n < 2; n++) { event.eventId = randomUUID(); event.installId = randomUUID(); assert.equal((await call('/v1/results', 'POST', event)).status, 201); }
  event.installId = randomUUID(); assert.equal((await call('/v1/results', 'POST', event)).status, 429);
});

test('kept links only to same install; delete removes raw data and followups, leaving other installs', async () => {
  const { event, call, admin } = await setup();
  await call('/v1/results', 'POST', event);
  const kept = { schema: 1, eventId: randomUUID(), installId: randomUUID(), resultId: event.eventId,
    timestamp: new Date().toISOString(), kept: false, changedSettings: ['bloom'] };
  assert.equal((await call('/v1/kept', 'POST', kept)).status, 422);
  kept.installId = event.installId; assert.equal((await call('/v1/kept', 'POST', kept)).status, 201);
  assert.equal((await call('/v1/kept', 'POST', kept)).status, 201);
  const other = structuredClone(event); other.eventId = randomUUID(); other.installId = randomUUID();
  await call('/v1/results', 'POST', other);
  assert.equal((await call('/v1/install/' + event.installId, 'DELETE')).status, 204);
  assert.equal((await call('/v1/install/' + event.installId, 'DELETE')).status, 204);
  assert.equal((await (await admin('?view=kept')).json()).rows.length, 0);
  assert.deepEqual((await (await admin()).json()).rows.map(r => r.payload.installId), [other.installId]);
  assert.equal((await call('/v1/install/not-an-id', 'DELETE')).status, 400);
});

test('admin requires a configured bearer secret for all views and formats; no public read route', async () => {
  const { call, admin } = await setup();
  for (const path of ['/v1/admin/export', '/v1/admin/export?view=aggregate&format=csv', '/v1/admin/unknown']) {
    assert.equal((await call(path)).status, 401);
    assert.equal((await call(path, 'GET', undefined, { Authorization: 'Bearer wrong' })).status, 401);
  }
  assert.equal((await admin()).status, 200);
  assert.equal((await admin('?view=anything')).status, 400);
  assert.equal((await call('/v1/results')).status, 404);
  const unconfigured = await setup({ token: '' });
  assert.equal((await unconfigured.call('/v1/admin/export', 'GET', undefined, { Authorization: 'Bearer ' })).status, 401);
});

test('aggregate median is weighted, retained privately, and raw retention is 24 months', async () => {
  const { event, call, admin, db } = await setup();
  for (const fps of [40, 50, 60, 100]) {
    event.eventId = randomUUID(); event.result.median.fpsAverage = fps;
    assert.equal((await call('/v1/results', 'POST', event)).status, 201);
  }
  let aggregate = (await (await admin('?view=aggregate')).json()).rows;
  assert.equal(aggregate[0].medianFps, 55); assert.equal(aggregate[0].samples, 4);
  const id = (await (await admin()).json()).rows[0].eventId;
  const kept = { schema: 1, eventId: randomUUID(), installId: event.installId, resultId: id,
    timestamp: new Date().toISOString(), kept: true, changedSettings: [] };
  await call('/v1/kept', 'POST', kept);
  await db.prepare("UPDATE results SET received_at='2000-01-01T00:00:00Z'").run();
  assert.equal((await call('/__test_retention')).status, 200);
  assert.equal((await (await admin()).json()).rows.length, 0);
  assert.equal((await (await admin('?view=kept')).json()).rows.length, 0);
  aggregate = (await (await admin('?view=aggregate')).json()).rows;
  assert.equal(aggregate[0].medianFps, 55); assert.equal(aggregate[0].samples, 4);
  await call('/__test_retention'); // No double counting on repeated scheduled runs.
  assert.equal((await (await admin('?view=aggregate')).json()).rows[0].samples, 4);
  assert.ok(!JSON.stringify(aggregate).includes(event.installId));
  assert.match(await (await admin('?view=aggregate&format=csv')).text(), /medianFps/);
});

test('private export pagination returns each raw event once', async () => {
  const { event, call, admin } = await setup();
  for (let i = 0; i < 3; i++) { event.eventId = randomUUID(); await call('/v1/results', 'POST', event); }
  const first = await (await admin('?limit=2')).json(); assert.equal(first.rows.length, 2); assert.ok(first.next);
  const second = await (await admin('?limit=2&after=' + first.next)).json(); assert.equal(second.rows.length, 1); assert.equal(second.next, null);
  assert.equal(new Set([...first.rows, ...second.rows].map(r => r.eventId)).size, 3);
});
