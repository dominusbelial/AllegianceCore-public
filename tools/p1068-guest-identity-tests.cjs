// Dependency-free tests of the actual browser script, including blocked storage and crypto provenance.
const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const { webcrypto } = require('node:crypto');
const source = fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/guest-identity.js', 'utf8');
function browser(storage = new Map(), crypto = webcrypto) {
  const context = { window: {}, crypto, Uint8Array, localStorage: {
    getItem: k => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k),
  }, Math: Object.freeze({ random: () => { throw Error('Math.random is forbidden'); } }) };
  vm.runInNewContext(source, context); return context.window.guestIdentity;
}
const storage = new Map(); let calls = 0;
const guest = browser(storage, { getRandomValues(bytes) { calls++; assert.equal(bytes.length, 10); return webcrypto.getRandomValues(bytes); } });
const first = guest.load(); assert.match(first, /^Pilot-[0-9A-HJKMNP-TV-Z]{10}$/); assert.equal(first.length, 16);
assert.equal(calls, 1); assert.equal(guest.load(), first); assert.equal(calls, 1); assert.equal(guest.persistent(), true);
assert.equal(browser(storage).load(), first); assert.notEqual(browser().load(), first);
const names = new Set([first]);
for (let i = 0; i < 1000; i++) { const name = guest.roll(); assert.match(name, /^Pilot-[0-9A-HJKMNP-TV-Z]{10}$/); assert.ok(!names.has(name)); names.add(name); }
assert.equal(calls, 1001);
for (const name of ['A', 'Pilot-1234', 'A B', 'é'.repeat(24), '<pilot>']) { assert.equal(guest.valid(name), true); assert.equal(guest.save(name), name); assert.equal(browser(storage).load(), name); }
for (const name of ['', 'x'.repeat(25), ' x', 'x ', 'a\n', 'a\t', 'a\0', 'a\x7f', 'a\x85', 'a\xa0', 'a\xad', '🚀', null, 123]) {
  assert.equal(guest.valid(name), false); assert.throws(() => guest.save(name));
}
// Test every Latin-1 character against the shared rule's exact accepted window.
for (let n = 0; n < 256; n++) assert.equal(guest.valid('A' + String.fromCharCode(n) + 'B'),
  n >= 32 && !(n >= 127 && n <= 160) && n !== 173, 'Latin-1 ' + n);
assert.equal(guest.forget(), true); assert.equal(storage.size, 0); assert.equal(guest.persistent(), false); assert.notEqual(guest.load(), first);
storage.set('allegiance.guest.callsign', '\x00bad'); assert.match(browser(storage).load(), /^Pilot-/);
for (const mode of ['read', 'write', 'silent']) {
  const blocked = { get() { if (mode === 'read') throw Error('blocked'); return null; },
    set() { if (mode !== 'silent') throw Error('quota'); }, delete() { throw Error('blocked'); } };
  const session = browser(blocked), name = session.load();
  assert.equal(session.persistent(), false); assert.equal(session.load(), name); assert.notEqual(session.roll(), name);
  assert.equal(session.forget(), false); assert.equal(session.persistent(), false); assert.notEqual(session.load(), name);
}
// Storage getter itself can throw under privacy policies.
const blockedContext = { window: {}, crypto: webcrypto, Uint8Array };
Object.defineProperty(blockedContext, 'localStorage', { get() { throw Error('SecurityError'); } });
vm.runInNewContext(source, blockedContext);
assert.match(blockedContext.window.guestIdentity.load(), /^Pilot-/);
assert.equal(blockedContext.window.guestIdentity.persistent(), false);
console.log('P1068_GUEST_IDENTITY_PASS');
