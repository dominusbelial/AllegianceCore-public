// P1068: random browser-local identity, never an account or a device fingerprint.
(() => {
  'use strict';
  const key = 'allegiance.guest.callsign';
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  let current = '', persistent = false;
  // Safe subset of the native 24-byte name window (_Utility/Utility.h:60,64),
  // matching JoinTokens.ValidCallsign: printable Latin-1, no controls/invisibles.
  const valid = name => typeof name === 'string' && name.length >= 1 && name.length <= 24 &&
    !name.startsWith(' ') && !name.endsWith(' ') && /^[\x20-\x7e\xa1-\xac\xae-\xff]+$/.test(name);
  function random() {
    const bytes = new Uint8Array(10);
    crypto.getRandomValues(bytes); // 50 independent bits; never Math.random or identifying signals.
    return 'Pilot-' + Array.from(bytes, b => alphabet[b & 31]).join('');
  }
  function save(name) {
    if (!valid(name)) throw new Error('Use 1–24 printable Latin-1 characters, without outer spaces or invisible characters.');
    current = name; persistent = false;
    try { localStorage.setItem(key, name); persistent = localStorage.getItem(key) === name; } catch { /* session-only */ }
    return current;
  }
  window.guestIdentity = Object.freeze({
    load() {
      if (current) return current;
      try { const stored = localStorage.getItem(key); if (valid(stored)) { current = stored; persistent = true; return current; } } catch { /* session-only */ }
      return save(random());
    },
    save,
    roll: () => save(random()),
    forget() {
      let forgotten = false;
      try { localStorage.removeItem(key); forgotten = localStorage.getItem(key) === null; } catch { /* session-only */ }
      current = ''; persistent = false;
      return forgotten;
    },
    persistent: () => persistent,
    valid,
  });
})();
