import { validateResult, validateKept } from '../generated/validators.cjs';

interface Limit { limit(options: { key: string }): Promise<{ success: boolean }> }
export interface Env { DB: D1Database; ADMIN_TOKEN?: string; IP_LIMIT: Limit; INSTALL_LIMIT: Limit }
interface Event {
  eventId: string; installId: string; resultId?: string; kept?: boolean; changedSettings?: string[];
  hardware: { cpuModel: string | null; cpuThreads: number; gpuModel: string | null; gpuVramGb: number | null };
  result: { preset: string; median: { fpsAverage: number | null; rendered: boolean } };
}
const LIMIT = 32768;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
function reply(status: number, value?: unknown) {
  return new Response(value === undefined ? null : JSON.stringify(value), {
    status, headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }
  });
}
async function boundedJson(request: Request): Promise<unknown> {
  if (Number(request.headers.get('Content-Length')) > LIMIT) throw new RangeError();
  if (!request.headers.get('Content-Type')?.toLowerCase().startsWith('application/json')) throw new TypeError();
  const reader = request.body?.getReader();
  if (!reader) throw new TypeError();
  const chunks: Uint8Array[] = []; let length = 0;
  while (true) {
    const { value, done } = await reader.read(); if (done) break;
    length += value.byteLength;
    if (length > LIMIT) { await reader.cancel(); throw new RangeError(); }
    chunks.push(value);
  }
  const bytes = new Uint8Array(length); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}
async function allowed(request: Request, env: Env, installId?: string) {
  if (installId && !(await env.INSTALL_LIMIT.limit({ key: installId })).success) return false;
  // IP participates only in a short-lived rate-limit key. No address/header is written to D1 or logs.
  const ip = request.headers.get('CF-Connecting-IP') ?? 'local';
  const bytes = new TextEncoder().encode(`${Math.floor(Date.now() / 60000)}:${ip}`);
  const hash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(v => v.toString(16).padStart(2, '0')).join('');
  return (await env.IP_LIMIT.limit({ key: hash })).success;
}
function cpuClass(h: Event['hardware']) {
  const vendor = /amd/i.test(h.cpuModel ?? '') ? 'AMD' : /intel/i.test(h.cpuModel ?? '') ? 'Intel' : /apple/i.test(h.cpuModel ?? '') ? 'Apple' : 'Other';
  return `${vendor}/${h.cpuThreads <= 4 ? '1-4' : h.cpuThreads <= 8 ? '5-8' : h.cpuThreads <= 16 ? '9-16' : '17+'} threads`;
}
function gpuClass(h: Event['hardware']) {
  const vendor = /nvidia|geforce|quadro/i.test(h.gpuModel ?? '') ? 'NVIDIA' : /amd|radeon/i.test(h.gpuModel ?? '') ? 'AMD' : /intel/i.test(h.gpuModel ?? '') ? 'Intel' : /apple/i.test(h.gpuModel ?? '') ? 'Apple' : 'Other';
  return `${vendor}/${h.gpuVramGb == null ? 'unknown' : h.gpuVramGb <= 4 ? '<=4' : h.gpuVramGb <= 8 ? '4-8' : '>8'} GB`;
}
async function secureEqual(a: string, b: string) {
  const enc = new TextEncoder();
  const [left, right] = await Promise.all([crypto.subtle.digest('SHA-256', enc.encode(a)), crypto.subtle.digest('SHA-256', enc.encode(b))]);
  const x = new Uint8Array(left), y = new Uint8Array(right); let diff = 0;
  for (let i = 0; i < x.length; i++) diff |= x[i] ^ y[i];
  return diff === 0;
}
export async function retain(env: Env) {
  // The same D1 batch atomically folds expired samples into non-identifying bins and removes raw data.
  await env.DB.batch([
    env.DB.prepare(`INSERT INTO aggregate_bins(cpu_class,gpu_class,preset,fps_tenth,samples)
      SELECT cpu_class,gpu_class,preset,CAST(round(fps*10) AS INTEGER),count(*) FROM results
      WHERE received_at < strftime('%Y-%m-%dT%H:%M:%fZ','now','-24 months') AND fps IS NOT NULL
      GROUP BY cpu_class,gpu_class,preset,CAST(round(fps*10) AS INTEGER)
      ON CONFLICT(cpu_class,gpu_class,preset,fps_tenth) DO UPDATE SET samples=samples+excluded.samples`),
    env.DB.prepare("DELETE FROM kept WHERE received_at < strftime('%Y-%m-%dT%H:%M:%fZ','now','-24 months') OR result_id IN (SELECT event_id FROM results WHERE received_at < strftime('%Y-%m-%dT%H:%M:%fZ','now','-24 months'))"),
    env.DB.prepare("DELETE FROM results WHERE received_at < strftime('%Y-%m-%dT%H:%M:%fZ','now','-24 months')")
  ]);
}
async function aggregate(env: Env) {
  // Combine live raw samples and retained histograms; median is weighted by sample count.
  const rows = await env.DB.prepare(`SELECT cpu_class,gpu_class,preset,fps_tenth,sum(samples) AS samples FROM (
    SELECT cpu_class,gpu_class,preset,CAST(round(fps*10) AS INTEGER) AS fps_tenth,1 AS samples FROM results WHERE fps IS NOT NULL
    UNION ALL SELECT cpu_class,gpu_class,preset,fps_tenth,samples FROM aggregate_bins)
    GROUP BY cpu_class,gpu_class,preset,fps_tenth ORDER BY cpu_class,gpu_class,preset,fps_tenth`).all<{
      cpu_class: string; gpu_class: string; preset: string; fps_tenth: number; samples: number;
    }>();
  const groups = new Map<string, typeof rows.results>();
  for (const row of rows.results) {
    const key = JSON.stringify([row.cpu_class, row.gpu_class, row.preset]);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key)!.push(row);
  }
  return [...groups.values()].map(bins => {
    const count = bins.reduce((n, b) => n + b.samples, 0);
    const at = (rank: number) => { let n = 0; for (const b of bins) { n += b.samples; if (n >= rank) return b.fps_tenth / 10; } return 0; };
    return { cpuClass: bins[0].cpu_class, gpuClass: bins[0].gpu_class, preset: bins[0].preset, samples: count,
      medianFps: (at(Math.floor((count + 1) / 2)) + at(Math.ceil((count + 1) / 2))) / 2 };
  });
}
function csvCell(value: unknown) {
  let s = typeof value === 'string' ? value : JSON.stringify(value ?? '');
  if (/^[=+@\-\t\r]/.test(s)) s = "'" + s;
  return '"' + s.replaceAll('"', '""') + '"';
}
async function exportData(url: URL, env: Env) {
  const view = url.searchParams.get('view') ?? 'results';
  const format = url.searchParams.get('format') ?? 'json';
  if (!['results', 'kept', 'aggregate'].includes(view) || !['json', 'csv'].includes(format)) return reply(400, { error: 'invalid export options' });
  const after = url.searchParams.get('after') ?? '';
  const limit = Math.min(1000, Math.max(1, Number(url.searchParams.get('limit') ?? 1000) || 1000));
  const rows: Record<string, unknown>[] = view === 'aggregate' ? await aggregate(env) :
    (await env.DB.prepare(`SELECT event_id AS eventId, received_at AS receivedAt, payload FROM ${view} WHERE event_id > ? ORDER BY event_id LIMIT ?`).bind(after, limit).all<{eventId: string; receivedAt: string; payload: string}>()).results
      .map(row => ({ eventId: row.eventId, receivedAt: row.receivedAt, payload: JSON.parse(row.payload) }));
  if (format === 'json') return reply(200, { rows, next: view !== 'aggregate' && rows.length === limit ? rows.at(-1)!.eventId : null });
  const keys = view === 'aggregate' ? ['cpuClass', 'gpuClass', 'preset', 'samples', 'medianFps'] : ['eventId', 'receivedAt', 'payload'];
  return new Response([keys.join(','), ...rows.map(r => keys.map(k => csvCell(r[k])).join(','))].join('\r\n'), {
    headers: { 'Content-Type': 'text/csv; charset=utf-8', 'Cache-Control': 'no-store',
      'X-Next-Cursor': view !== 'aggregate' && rows.length === limit ? String(rows.at(-1)!.eventId) : '' }
  });
}
export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const url = new URL(request.url);
      if (url.pathname.startsWith('/v1/admin/')) {
        if (!env.ADMIN_TOKEN || !(await secureEqual(request.headers.get('Authorization') ?? '', `Bearer ${env.ADMIN_TOKEN}`))) return reply(401, { error: 'unauthorized' });
        if (request.method === 'GET' && url.pathname === '/v1/admin/export') return await exportData(url, env);
        return reply(404);
      }
      const deletion = /^\/v1\/install\/([^/]+)$/.exec(url.pathname);
      if (request.method === 'DELETE' && deletion) {
        const id = deletion[1]; if (!UUID.test(id)) return reply(400, { error: 'invalid install ID' });
        if (!(await allowed(request, env, id))) return reply(429, { error: 'rate limit' });
        await env.DB.batch([env.DB.prepare('DELETE FROM kept WHERE install_id = ?').bind(id), env.DB.prepare('DELETE FROM results WHERE install_id = ?').bind(id)]);
        return reply(204);
      }
      if (request.method !== 'POST' || !['/v1/results', '/v1/kept'].includes(url.pathname)) return reply(404);
      if (!(await allowed(request, env))) return reply(429, { error: 'rate limit' });
      let value: unknown;
      try { value = await boundedJson(request); }
      catch (error) { return reply(error instanceof RangeError ? 413 : 400, { error: 'invalid body' }); }
      const isResult = url.pathname === '/v1/results';
      if (!(isResult ? validateResult(value) : validateKept(value))) return reply(422, { error: 'schema mismatch' });
      const event = value as Event;
      if (!UUID.test(event.installId) || !UUID.test(event.eventId) || (!isResult && (!UUID.test(event.resultId ?? '') || event.kept !== (event.changedSettings?.length === 0)))) return reply(422, { error: 'invalid event' });
      if (!(await env.INSTALL_LIMIT.limit({ key: event.installId })).success) return reply(429, { error: 'rate limit' });
      const payload = JSON.stringify(value);
      if (isResult) {
        await env.DB.prepare('INSERT INTO results(event_id,install_id,cpu_class,gpu_class,preset,fps,payload) VALUES(?,?,?,?,?,?,?) ON CONFLICT(event_id) DO NOTHING')
          .bind(event.eventId, event.installId, cpuClass(event.hardware), gpuClass(event.hardware), event.result.preset,
            event.result.median.rendered ? event.result.median.fpsAverage : null, payload).run();
      } else {
        // INSERT..SELECT also protects against deletion racing a follow-up.
        const result = await env.DB.prepare(`INSERT INTO kept(event_id,install_id,result_id,payload)
          SELECT ?,?,?,? WHERE EXISTS(SELECT 1 FROM results WHERE event_id=? AND install_id=?) ON CONFLICT(event_id) DO NOTHING`)
          .bind(event.eventId, event.installId, event.resultId!, payload, event.resultId!, event.installId).run();
        if (!result.meta.changes) {
          const existing = await env.DB.prepare('SELECT event_id FROM kept WHERE event_id=? AND install_id=?').bind(event.eventId, event.installId).first();
          if (!existing) return reply(422, { error: 'unknown result' });
        }
      }
      return reply(201, { accepted: true });
    } catch { return reply(503, { error: 'temporarily unavailable' }); } // Never log headers, URLs, payloads or IPs.
  },
  async scheduled(_event: ScheduledController, env: Env) { await retain(env); }
};
