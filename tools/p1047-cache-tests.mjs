// node --test tools/p1047-cache-tests.mjs; no network, uses the production cache algorithm.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../src/Allegiance.ClientCore.Browser/wwwroot/game-platform.js', import.meta.url), 'utf8');
const { BoundedCache, safeSettings } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
globalThis.document = { baseURI: 'http://127.0.0.1/play/preview/' };
const hash = n => n.toString(16).padStart(64, '0');
function fixture(maximum = 8, quota = {}, maximumEntries = 1024) {
  const files = new Map(), entries = new Map(); let committed;
  const disk = {
    async keys() { return [...files.keys()].map(url => ({ url })); },
    async match(key) { return files.get(key)?.clone(); },
    async delete(key) { return files.delete(typeof key === 'string' ? key : key.url); },
    async put(key, response) { files.set(key, response.clone()); }
  };
  const metadata = {
    async all() { return [...entries.values()]; },
    async put(e) { entries.set(e.hash, e); },
    async remove(h) { entries.delete(h); },
    async manifest(m) { committed = m; }
  };
  return { files, entries, metadata, disk, get committed() { return committed; }, cache: new BoundedCache(disk, metadata, async () => quota, maximum, maximumEntries) };
}
test('bounded cache evicts LRU and records only actually persisted hashes', async () => {
  const f = fixture();
  assert.equal(await f.cache.put(hash(1), new Uint8Array([1, 2, 3, 4]), 'application/octet-stream'), true);
  assert.equal(await f.cache.put(hash(2), new Uint8Array([5, 6, 7, 8]), 'application/octet-stream'), true);
  f.entries.get(hash(1)).used = 0; f.entries.get(hash(2)).used = 1;
  await f.cache.put(hash(3), new Uint8Array([9, 10, 11, 12]), 'application/octet-stream');
  assert.equal(await f.cache.read(hash(1)), null);
  assert.deepEqual(await f.cache.read(hash(2)), new Uint8Array([5, 6, 7, 8]));
  await f.cache.commit('generation', [hash(1), hash(2), hash(3)]);
  assert.deepEqual(f.committed.hashes, [hash(2), hash(3)]);
  assert.equal([...f.entries.values()].reduce((n, e) => n + e.size, 0), 8);
});
test('quota checked before writes; oversized and zero-quota requests stay uncached', async () => {
  const f = fixture(8, { quota: 1, usage: 0 });
  assert.equal(await f.cache.put(hash(1), new Uint8Array([1]), 'application/octet-stream'), false);
  assert.equal(f.files.size, 0);
  assert.equal(await fixture().cache.put(hash(2), new Uint8Array(9), 'application/octet-stream'), false);
});
test('recovery drops orphan bytes and metadata after partial or aborted writes', async () => {
  const f = fixture();
  f.files.set(f.cache.key(hash(1)), new Response(new Uint8Array([1])));
  f.entries.set(hash(2), { hash: hash(2), size: 1, used: 0 });
  await f.cache.recover();
  assert.equal(f.files.size, 0); assert.equal(f.entries.size, 0);
  f.metadata.put = async () => { throw new Error('IndexedDB transaction aborted'); };
  assert.equal(await f.cache.put(hash(3), new Uint8Array([3]), 'application/octet-stream'), false);
  assert.equal(f.files.size, 0); assert.equal(f.entries.size, 0);
});
test('Cache API quota/write failure leaves no partial entry; retry works', async () => {
  const f = fixture(); const put = f.disk.put;
  f.disk.put = async () => { throw new Error('QuotaExceededError'); };
  assert.equal(await f.cache.put(hash(1), new Uint8Array([1]), 'application/octet-stream'), false);
  assert.equal(f.entries.size, 0);
  f.disk.put = put;
  assert.equal(await f.cache.put(hash(1), new Uint8Array([1]), 'application/octet-stream'), true);
  assert.deepEqual(await f.cache.read(hash(1)), new Uint8Array([1]));
});
test('external eviction becomes a miss and removes stale metadata', async () => {
  const f = fixture();
  await f.cache.put(hash(1), new Uint8Array([1]), 'application/octet-stream'); f.files.clear();
  assert.equal(await f.cache.read(hash(1)), null); assert.equal(f.entries.size, 0);
});
test('browser settings round-trip and reject credential paths/fields', () => {
  assert.deepEqual(safeSettings({ 'player.json': '{"Pilot":"pilot"}', 'keymap.json': '{}' }), { 'player.json': '{"Pilot":"pilot"}', 'keymap.json': '{}' });
  assert.throws(() => safeSettings({ 'identity.json': '{}' }));
  assert.throws(() => safeSettings({ 'player.json': '{"nested":[{"access_token":"secret"}]}' }));
});

test('metadata entry count is bounded independently of byte quota', async () => {
  const f = fixture(100, {}, 2);
  await f.cache.put(hash(1), new Uint8Array([1]), 'application/octet-stream');
  await f.cache.put(hash(2), new Uint8Array([2]), 'application/octet-stream');
  await f.cache.put(hash(3), new Uint8Array([3]), 'application/octet-stream');
  assert.equal(f.entries.size, 2); assert.equal(f.files.size, 2);
});

test('oversized cached response is bounded and removed before reuse', async () => {
  const f = fixture();
  await f.cache.put(hash(1), new Uint8Array([1]), 'application/octet-stream');
  f.files.set(f.cache.key(hash(1)), new Response(new Uint8Array(9)));
  assert.equal(await f.cache.read(hash(1)), null);
  assert.equal(f.files.size, 0); assert.equal(f.entries.size, 0);
});

test('sector manifest retention preserves base and streamed originals across a warm base commit', async () => {
  const f = fixture(32);
  const base = hash(1), entryOriginal = hash(2), nextOriginal = hash(3), obsolete = hash(4);
  for (const [index, h] of [base, entryOriginal, nextOriginal, obsolete].entries())
    await f.cache.put(h, new Uint8Array([index + 1]), 'application/octet-stream');
  await f.cache.commit('base-plus-sector-index', [base, entryOriginal, nextOriginal]);
  assert.deepEqual(await f.cache.read(entryOriginal), new Uint8Array([2]));
  assert.deepEqual(await f.cache.read(nextOriginal), new Uint8Array([3]));
  assert.deepEqual(f.committed.hashes, [base, entryOriginal, nextOriginal]);
  assert.equal(await f.cache.read(obsolete), null);
});

test('reused sector hashes consume one cache entry and remain bounded during sector churn', async () => {
  const f = fixture(8, {}, 3);
  await f.cache.put(hash(1), new Uint8Array([1, 2, 3, 4]), 'application/octet-stream');
  await f.cache.put(hash(1), new Uint8Array([1, 2, 3, 4]), 'application/octet-stream');
  assert.equal(f.entries.size, 1);
  for (let i = 2; i < 20; i++) await f.cache.put(hash(i), new Uint8Array([i, i, i, i]), 'application/octet-stream');
  assert.equal(f.files.size, 2);
  assert.equal([...f.entries.values()].reduce((total, entry) => total + entry.size, 0), 8);
  assert.deepEqual(await f.cache.read(hash(19)), new Uint8Array([19, 19, 19, 19]));
});
