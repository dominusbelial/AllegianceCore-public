// Actual Chromium page input + native 90-second NOAT timer, without npm packages.
// Start p1051-browser-host with --bots 0 --guests 1, then:
// node tools/p1078-browser-afk.cjs CHROMIUM HOST_STATE HOST_LOG OUTPUT
// The separate P1078AfkSeatsLiveTests covers the opt-in 30s / five-bot policy.
const fs = require('node:fs'), path = require('node:path'), os = require('node:os');
const { spawn } = require('node:child_process'), assert = require('node:assert/strict');
const [executable, stateDir, hostLog, output] = process.argv.slice(2).map(p => path.resolve(p));
const delay = ms => new Promise(r => setTimeout(r, ms));
async function until(fn, label, seconds = 120) {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) { if (await fn()) return; await delay(100); }
  throw Error('Timed out: ' + label);
}
async function connect(url) {
  const socket = new WebSocket(url), pending = new Map(), events = [];
  let next = 0;
  socket.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const entry = pending.get(message.id); pending.delete(message.id);
      if (message.error) entry.reject(Error(JSON.stringify(message.error))); else entry.resolve(message.result);
    } else events.push(message);
  };
  socket.onclose = () => { for (const entry of pending.values()) entry.reject(Error('Chromium debugger closed')); pending.clear(); };
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  return { events, close: () => socket.close(), send: (method, params = {}, sessionId) =>
    new Promise((resolve, reject) => { const id = ++next; pending.set(id, { resolve, reject }); socket.send(JSON.stringify({ id, method, params, sessionId })); }) };
}
async function main() {
  fs.mkdirSync(output, { recursive: true });
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'p1078-chrome-'));
  const log = fs.openSync(path.join(output, 'chromium.log'), 'w');
  const chrome = spawn(executable, ['--headless=new', '--no-sandbox', '--disable-dev-shm-usage',
    '--disable-background-networking', '--disable-gpu-sandbox', '--use-gl=angle', '--use-angle=swiftshader',
    '--enable-unsafe-swiftshader', '--ignore-certificate-errors', '--remote-debugging-port=0',
    '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=localhost;127.0.0.1', '--window-size=1600,1000',
    '--user-data-dir=' + profile, 'about:blank'], { stdio: ['ignore', log, log] });
  let cdp;
  try {
    await until(() => fs.existsSync(path.join(profile, 'DevToolsActivePort')), 'Chromium debugger', 30);
    const [port, route] = fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').trim().split('\n');
    cdp = await connect('ws://127.0.0.1:' + port + route);
    const version = await cdp.send('Browser.getVersion');
    const target = await cdp.send('Target.createTarget', { url: 'about:blank' });
    const { sessionId } = await cdp.send('Target.attachToTarget', { targetId: target.targetId, flatten: true });
    const send = (method, params) => cdp.send(method, params, sessionId);
    await send('Runtime.enable'); await send('Page.enable');
    const evaluate = async expression => {
      const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
      if (result.exceptionDetails) throw Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
      return result.result.value;
    };
    const click = async selector => {
      const box = await evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
      await send('Input.dispatchMouseEvent', { type: 'mouseMoved', ...box });
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...box });
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...box });
    };
    await send('Page.navigate', { url: 'https://localhost:8443/play/menu.html?online=1&dev=1' });
    await until(() => evaluate('!!document.querySelector("#guest-disclosure")'), 'guest disclosure');
    const callsign = await evaluate('document.querySelector("#guest-callsign").value');
    const password = fs.readFileSync(path.join(stateDir, 'game-password'), 'utf8').trim();
    await evaluate(`(()=>{const e=document.querySelector('#join-password');if(e){e.value=${JSON.stringify(password)};e.dispatchEvent(new Event('input',{bubbles:true}))}})()`);
    await until(() => evaluate('!!document.querySelector(".online-join:enabled")'), 'Join button');
    await click('.online-join:enabled');
    await until(() => evaluate('document.querySelector("#online-panel")?.dataset.state==="Sides"'), 'side picker');
    let ship;
    await until(() => { ship = fs.readFileSync(hostLog, 'utf8').split('\n').find(l => l.includes("'" + callsign + "' pending side-select"))?.match(/ship id (\d+)/)?.[1]; return ship; }, 'reserved own ship');
    const flags = () => [...fs.readFileSync(hostLog, 'utf8').matchAll(new RegExp('\\[ready\\].*ship ' + ship + ' fReady=(True|False)', 'g'))].map(m => m[1] === 'True');
    await until(() => flags().at(-1) === true, 'initial ready');
    const readyAt = Date.now();
    const background = await cdp.send('Target.createTarget', { url: 'about:blank' });
    await cdp.send('Target.activateTarget', { targetId: background.targetId });
    const hidden = await evaluate('document.hidden');
    console.log('P1078_BROWSER_IDLE_STARTED hidden=' + hidden);
    await until(() => flags().at(-1) === false, 'native AFK from no page input', 115);
    const idleMs = Date.now() - readyAt;
    assert.ok(idleMs >= 89000 && idleMs < 110000, 'native 90-second wall deadline: ' + idleMs);
    await send('Page.bringToFront');
    await delay(1200);
    assert.equal(flags().at(-1), false, 'focus alone never wakes AFK');
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 900, y: 600 });
    await until(() => flags().at(-1) === true, 'real pointer movement wakes AFK', 10);
    await click('.online-side');
    await until(() => evaluate('document.querySelector("#online-panel")?.dataset.state==="Hangar"'), 'explicit side seats active guest');
    await click('#online-afk');
    await until(() => flags().at(-1) === false, 'manual AFK');
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 950, y: 650 });
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Shift', code: 'ShiftLeft', windowsVirtualKeyCode: 16 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Shift', code: 'ShiftLeft', windowsVirtualKeyCode: 16 });
    await delay(1500);
    assert.equal(flags().at(-1), false, 'manual suppression survives pointer and keyboard input');
    await click('#online-afk');
    await until(() => flags().at(-1) === true, 'explicit manual return');
    const errors = cdp.events.filter(e => e.method === 'Runtime.exceptionThrown');
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'browser-afk.json'), JSON.stringify({ browser: version.product, hidden,
      idleMs, readyFlags: flags(), focusWoke: false, pointerWoke: true, manualSuppressedInput: true, errors }, null, 2) + '\n');
    console.log('P1078_BROWSER_AFK_PASS idleMs=' + idleMs + ' pointerWake=true manualSuppression=true');
  } finally {
    if (cdp) { try { await cdp.send('Browser.close'); } catch {} cdp.close(); }
    if (chrome.exitCode === null) { chrome.kill('SIGTERM'); await new Promise(resolve => chrome.once('exit', resolve)); }
    fs.closeSync(log); fs.rmSync(profile, { recursive: true, force: true });
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
