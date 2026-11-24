// Page input integration without DOM packages: exercise the production adapter.
const test = require('node:test'), assert = require('node:assert/strict');
const vm = require('node:vm'), fs = require('node:fs'), path = require('node:path');

function fixture() {
  class Target {
    listeners = new Map();
    addEventListener(name, fn) { const list = this.listeners.get(name) || []; list.push(fn); this.listeners.set(name, list); }
    removeEventListener(name, fn) { this.listeners.set(name, this.listeners.get(name).filter(f => f !== fn)); }
    fire(name, extra = {}) { for (const fn of this.listeners.get(name) || []) fn({ type: name, isTrusted: true, preventDefault() {}, ...extra }); }
  }
  const window = new Target(), document = new Target(), canvas = new Target(), calls = [], timers = new Map();
  let timerId = 0;
  document.hidden = false;
  document.getElementById = id => id === 'theCanvas' ? canvas : null;
  window.setInterval = (fn, ms) => { assert.equal(ms, 1000); timers.set(++timerId, fn); return timerId; };
  window.clearInterval = id => timers.delete(id);
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Allegiance.ClientCore.Browser/wwwroot/flight-input.js'), 'utf8'), { window, document, console, navigator: {} });
  const detach = window.installFlightInput({ invokeMethod: (...args) => { calls.push(args); } });
  return { window, document, calls, timers, detach, activity: () => calls.filter(c => c[0] === 'PlayerActivityTick') };
}

test('page pointer, keys and buttons count in lobby and HTML controls; synthetic input does not', () => {
  const f = fixture();
  for (const name of ['pointermove', 'pointerdown', 'pointerup', 'keydown', 'keyup', 'wheel'])
    f.window.fire(name, { movementX: 2 });
  assert.equal(f.activity().length, 6);
  f.window.fire('pointermove', { movementX: 0, movementY: 0 });
  f.window.fire('keydown', { isTrusted: false });
  assert.equal(f.activity().length, 6);
  assert.ok(f.activity().every(c => c[1] === true));
  f.detach();
});

test('hidden page keeps checking elapsed time while visibility and frame callbacks never invent input', () => {
  const f = fixture();
  f.document.hidden = true;
  f.document.fire('visibilitychange');
  f.window.fire('keydown');
  f.window.fire('pointermove', { movementX: 9 });
  f.window.gameFlight.frame();
  for (const tick of f.timers.values()) tick();
  assert.deepEqual(f.activity().map(c => c[1]), [false]);
  f.document.hidden = false;
  f.document.fire('visibilitychange');
  f.window.fire('focus');
  assert.equal(f.activity().length, 1);
  f.window.fire('pointermove', { movementX: 1 });
  assert.equal(f.activity().at(-1)[1], true);
  f.detach();
});

test('detaching retires the timer and every activity listener', () => {
  const f = fixture();
  f.detach();
  assert.equal(f.timers.size, 0);
  f.window.fire('keydown');
  f.window.fire('pointermove', { movementX: 1 });
  assert.equal(f.activity().length, 0);
});
