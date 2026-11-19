'use strict';
// P1059 — the browser developer/proof switch (no browser needed).
// Run: node --test tools/p1059-dev-switch-tests.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const dev = require(path.join(__dirname, '..', 'src', 'Allegiance.ClientCore.Browser', 'wwwroot', 'dev-switch.js'));

function storage(value) {
  const writes = [];
  return {
    storage: {
      getItem: key => (key === 'allegiance.dev' ? value ?? null : null),
      setItem: (key, next) => writes.push([key, next]),
    },
    writes,
  };
}

test('default is off, with no URL parameter and no stored choice', () => {
  assert.equal(dev.devRequested({ search: '' }, storage(null).storage), false);
  assert.equal(dev.devRequested({ search: '?x=1' }, storage(null).storage), false);
  assert.equal(dev.STORAGE_KEY, 'allegiance.dev');
});

test('?dev=1 turns the switch on and ?dev=0 turns it off', () => {
  assert.equal(dev.devRequested({ search: '?dev=1' }, storage(null).storage), true);
  assert.equal(dev.devRequested({ search: '?dev=true' }, storage(null).storage), true);
  assert.equal(dev.devRequested({ search: '?dev=on' }, storage(null).storage), true);
  assert.equal(dev.devRequested({ search: '?dev=0' }, storage('1').storage), false);
  assert.equal(dev.devRequested({ search: '?dev=false' }, storage('1').storage), false);
});

test('an unknown URL value falls through to the stored choice', () => {
  assert.equal(dev.devRequested({ search: '?dev=maybe' }, storage('1').storage), true);
  assert.equal(dev.devRequested({ search: '?dev=' }, storage('1').storage), true);
  assert.equal(dev.devRequested({ search: '?dev=maybe' }, storage(null).storage), false);
});

test('storage is the fallback switch', () => {
  assert.equal(dev.devRequested({ search: '' }, storage('1').storage), true);
  assert.equal(dev.devRequested({ search: '' }, storage('0').storage), false);
  assert.equal(dev.devRequested({ search: '' }, storage('verbose').storage), false);
  assert.equal(dev.devRequested({ search: '' }, { getItem: () => { throw new Error('refused'); } }), false);
});

test('install exposes the read the page and Blazor use, off by default', () => {
  const window = { localStorage: storage(null).storage };
  const api = dev.install(window, false);
  assert.equal(window.allegianceDevEnabled(), false);
  assert.equal(window.allegianceDev.enabled(), false);
  assert.equal(window.allegianceDev, api);
});

test('setEnabled switches live and remembers the choice', () => {
  const { storage: store, writes } = storage(null);
  const window = { localStorage: store };
  const api = dev.install(window, true);
  assert.equal(api.enabled(), true);
  assert.equal(api.setEnabled(false), false);
  assert.equal(window.allegianceDevEnabled(), false);
  assert.deepEqual(writes, [['allegiance.dev', '0']]);
  api.setEnabled(true);
  assert.deepEqual(writes, [['allegiance.dev', '0'], ['allegiance.dev', '1']]);
});
