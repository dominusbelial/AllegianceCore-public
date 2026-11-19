'use strict';
// P1058 — the browser console router (no browser needed).
// Run: node --test tools/p1058-console-log-tests.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const router = require(path.join(__dirname, '..', 'src', 'Allegiance.ClientCore.Browser', 'wwwroot', 'console-log.js'));

function fakeWindow(search, stored) {
  const calls = [];
  const consoleLike = {
    log: (...args) => calls.push(['log', args.join(' ')]),
    debug: (...args) => calls.push(['debug', args.join(' ')]),
    warn: (...args) => calls.push(['warn', args.join(' ')]),
    error: (...args) => calls.push(['error', args.join(' ')]),
  };
  const writes = [];
  const window = {
    location: { search },
    localStorage: {
      getItem: key => (key === 'allegiance.log' ? stored ?? null : null),
      setItem: (key, value) => writes.push([key, value]),
    },
    console: consoleLike,
  };
  return { window, calls, writes };
}

test('per-asset information lines are info level', () => {
  for (const line of [
    "[monogame] status art 'hud': 64x64 'content/hud.png'",
    "[monogame] hud-10 art 'hudshield': 128x128 'content/shield.png'",
    "[monogame] target-pane 'panetarget': 256x256 'art/panetargetbmp.mdl'",
    '[sim] freeze at tick 1200',
    '[sim] resume at tick 1260',
  ]) assert.equal(router.classify(line), 'info', line);
});

test('a real problem inside a log line is promoted, never hidden', () => {
  for (const line of [
    "[monogame] status art 'hud' missing (content/hud.png) — text fallback",
    "[monogame] target-pane 'panetarget' failed to decode: bad header",
    '[browser] Screenshot download failed: no frame',
    'unhandled exception in TickDotNet',
    'The server connection was lost.',
  ]) assert.notEqual(router.classify(line), 'info', line);
  assert.equal(router.classify('[monogame] hud-10 art failed: bad surface — fallback'), 'error');
  assert.equal(router.classify("[monogame] status art 'x' missing — fallback"), 'warning');
});

test('quiet default keeps info off the visible levels but keeps warnings and errors', () => {
  const { window, calls } = fakeWindow('', null);
  router.install(window, false);
  window.console.log("[monogame] status art 'hud': 64x64");
  window.console.log('[browser] Screenshot download failed: no frame');
  window.console.warn('surface warning');
  window.console.error('boom');
  assert.deepEqual(calls.map(call => call[0]), ['debug', 'error', 'warn', 'error']);
  assert.equal(window.allegianceLog.verbose(), false);
});

test('verbose restores the full stream as plain info lines', () => {
  const { window, calls } = fakeWindow('?log=verbose', null);
  router.install(window, true);
  window.console.log("[monogame] hud-10 art 'hudshield': 128x128");
  assert.deepEqual(calls, [['log', "[monogame] hud-10 art 'hudshield': 128x128"]]);
  assert.equal(window.allegianceLog.verbose(), true);
});

test('the switch is read from the URL first and from storage otherwise', () => {
  const storage = { getItem: () => 'verbose' };
  assert.equal(router.verboseRequested({ search: '?log=verbose' }, storage), true);
  assert.equal(router.verboseRequested({ search: '?log=quiet' }, storage), false);
  assert.equal(router.verboseRequested({ search: '' }, storage), true);
  assert.equal(router.verboseRequested({ search: '' }, { getItem: () => null }), false);
  assert.equal(router.verboseRequested({ search: '' }, { getItem: () => { throw new Error('refused'); } }), false);
});

test('setVerbose switches live and remembers the choice', () => {
  const { window, calls, writes } = fakeWindow('', null);
  router.install(window, false);
  window.console.log('info line');
  window.allegianceLog.setVerbose(true);
  window.console.log('info line');
  assert.deepEqual(calls.map(call => call[0]), ['debug', 'log']);
  assert.deepEqual(writes, [['allegiance.log', 'verbose']]);
});
