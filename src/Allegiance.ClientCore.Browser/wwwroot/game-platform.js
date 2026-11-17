// Browser-only platform boundary. Cache bytes are immutable; metadata and settings use IndexedDB.
const limit = 64 * 1024 * 1024;
const hashPattern = /^[0-9a-f]{64}$/;
const settingsNames = new Set(['player.json', 'hud.json', 'keymap.json', 'graphics.json']);
function safeSettings(snapshot) {
  for (const [name, text] of Object.entries(snapshot)) {
    if (!settingsNames.has(name) || typeof text !== 'string' || text.length > 128 * 1024) throw new Error('Invalid browser preference');
    const inspect = value => {
      if (!value || typeof value !== 'object') return;
      for (const [key, child] of Object.entries(value)) {
        if (/password|token|secret|credential|privatekey/i.test(key.replace(/[_-]/g, ''))) throw new Error('Secrets cannot be stored');
        inspect(child);
      }
    };
    inspect(JSON.parse(text));
  }
  return snapshot;
}
export { safeSettings };

let database;
function db() {
  database ??= new Promise((resolve, reject) => {
    const request = indexedDB.open('allegiance-menu-v2', 1);
    request.onupgradeneeded = () => {
      for (const name of ['entries', 'manifests', 'settings']) request.result.createObjectStore(name);
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
    request.onblocked = () => reject(new Error('Browser storage is blocked'));
  });
  return database;
}
async function operation(store, mode, perform) {
  const database = await db();
  return new Promise((resolve, reject) => {
    const transaction = database.transaction(store, mode);
    let result;
    const request = perform(transaction.objectStore(store));
    request.onsuccess = () => { result = request.result; };
    transaction.oncomplete = () => resolve(result);
    transaction.onabort = transaction.onerror = () => reject(transaction.error || new Error('Browser storage aborted'));
  });
}
const metadata = {
  all: () => operation('entries', 'readonly', s => s.getAll()),
  put: entry => operation('entries', 'readwrite', s => s.put(entry, entry.hash)),
  remove: hash => operation('entries', 'readwrite', s => s.delete(hash)),
  manifest: value => operation('manifests', 'readwrite', s => s.put(value, 'current'))
};

async function boundedBytes(response, maximum) {
  if (Number(response.headers.get('Content-Length')) > maximum) return null;
  const reader = response.body?.getReader();
  if (!reader) return null;
  const chunks = []; let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.length;
    if (total > maximum) { void reader.cancel().catch(() => {}); return null; }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  return bytes;
}

// Dependency injection keeps quota/eviction/recovery tests on the exact production algorithm.
export class BoundedCache {
  constructor(cache, metadata, estimate, maximum = limit, maximumEntries = 1024) {
    this.cache = cache; this.metadata = metadata; this.estimate = estimate; this.maximum = maximum; this.maximumEntries = maximumEntries;
    this.ready = false; this.queue = Promise.resolve();
  }
  serial(action) {
    const next = this.queue.then(action);
    this.queue = next.catch(() => {});
    return next;
  }
  key(hash) { if (!hashPattern.test(hash)) throw new Error('Invalid cache hash'); return new URL('__immutable/' + hash, document.baseURI).href; }
  async recover() {
    if (this.ready) return;
    const entries = await this.metadata.all();
    const known = new Map(entries.map(e => [this.key(e.hash), e]));
    for (const request of await this.cache.keys()) {
      if (!known.has(request.url)) await this.cache.delete(request);
      else known.delete(request.url);
    }
    for (const entry of known.values()) await this.metadata.remove(entry.hash);
    this.ready = true;
  }
  async removeEntry(entry) {
    await this.cache.delete(this.key(entry.hash));
    await this.metadata.remove(entry.hash);
  }
  read(hash) { return this.serial(async () => {
    await this.recover();
    const response = await this.cache.match(this.key(hash));
    if (!response) { await this.metadata.remove(hash); return null; }
    const known = (await this.metadata.all()).some(e => e.hash === hash);
    const bytes = known ? await boundedBytes(response, Math.min(this.maximum, 16 * 1024 * 1024)) : null;
    if (!bytes) { await this.removeEntry({ hash }); return null; }
    await this.metadata.put({ hash, size: bytes.length, used: Date.now() });
    return bytes;
  }); }
  remove(hash) { return this.serial(() => this.removeEntry({ hash })); }
  put(hash, bytes, contentType) { return this.serial(async () => {
    await this.recover();
    const key = this.key(hash);
    if (bytes.length > this.maximum) return false;
    let entries = await this.metadata.all();
    const old = entries.find(e => e.hash === hash);
    if (old) await this.removeEntry(old);
    entries = entries.filter(e => e.hash !== hash).sort((a, b) => a.used - b.used || a.hash.localeCompare(b.hash));
    let current = entries.reduce((sum, entry) => sum + entry.size, 0);
    const quota = await this.estimate();
    // Do not consume all origin quota; leave at least 2 MiB for runtime/settings.
    let available = Number.isFinite(quota.quota) && Number.isFinite(quota.usage)
      ? Math.max(0, quota.quota - quota.usage - 2 * 1024 * 1024) : this.maximum;
    while (entries.length && (entries.length >= this.maximumEntries || current + bytes.length > this.maximum || bytes.length > available)) {
      const entry = entries.shift(); await this.removeEntry(entry);
      current -= entry.size; available += entry.size;
    }
    if (current + bytes.length > this.maximum || bytes.length > available) return false;
    try {
      // Cache.put publishes a complete response atomically; IDB records it only afterwards.
      await this.cache.put(key, new Response(bytes, { headers: { 'Content-Type': contentType, 'Content-Length': String(bytes.length) } }));
      await this.metadata.put({ hash, size: bytes.length, used: Date.now() });
      return true;
    } catch {
      await this.cache.delete(key);
      await this.metadata.remove(hash);
      return false;
    }
  }); }
  commit(identity, hashes) { return this.serial(async () => {
    await this.recover();
    const available = new Set((await this.metadata.all()).map(e => e.hash));
    await this.metadata.manifest({ identity, hashes: hashes.filter(h => available.has(h)), updated: Date.now() });
  }); }
}
let cachePromise;
function cache() {
  cachePromise ??= caches.open('allegiance-immutable-v2').then(c => new BoundedCache(c, metadata, () => navigator.storage?.estimate?.() ?? Promise.resolve({})));
  return cachePromise;
}
export async function cacheRead(hash) { try { return await (await cache()).read(hash); } catch { return null; } }
export async function cacheRemove(hash) { try { await (await cache()).remove(hash); } catch { /* Private mode: residency still works. */ } }
export async function cachePut(hash, bytes, contentType) { try { return await (await cache()).put(hash, bytes, contentType); } catch { return false; } }
export async function cacheCommit(identity, hashes) { try { await (await cache()).commit(identity, hashes); } catch { /* Persistence is optional. */ } }
export async function settingsRead() {
  try { return safeSettings(await operation('settings', 'readonly', s => s.get('preferences')) || {}); }
  catch { return {}; }
}
export async function settingsWrite(snapshot) {
  safeSettings(snapshot);
  try { await operation('settings', 'readwrite', s => s.put(snapshot, 'preferences')); return true; }
  catch { return false; }
}
export function navigate(url) {
  const target = new URL(url, location.href);
  if (!['https:', 'http:'].includes(target.protocol)) throw new Error('Unsupported navigation');
  window.open(target.href, '_blank', 'noopener,noreferrer');
}
export function download(name, bytes, mime) {
  const url = URL.createObjectURL(new Blob([bytes], { type: mime }));
  const link = document.createElement('a'); link.href = url; link.download = name; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export async function screenshot() {
  const canvas = document.getElementById('theCanvas');
  const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
  if (blob) download('allegiance-menu.png', new Uint8Array(await blob.arrayBuffer()), 'image/png');
}
export function installText(instance) {
  const handler = event => {
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    if (event.key === 'Backspace' || event.key.length === 1) {
      event.preventDefault(); instance.invokeMethod('TextInput', event.key);
    }
  };
  window.addEventListener('keydown', handler);
  return { dispose: () => window.removeEventListener('keydown', handler) };
}
