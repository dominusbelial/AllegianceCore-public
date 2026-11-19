// Local Chromium proof: load timings/layouts, pacing under CDP throttling and
// standard Gamepad API input through the production shared flight controller.
// Args: local npm dir, host state dir, output dir, before|after.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules');
const stateDir = path.resolve(process.argv[3]), output = path.resolve(process.argv[4]), phase = process.argv[5];
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const origin = 'http://localhost:8048';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const state = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
async function until(predicate, label, timeout = 30000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (predicate()) return; await delay(50); }
  throw Error('Timed out: ' + label);
}
async function main() {
  fs.mkdirSync(output, { recursive: true });
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const browser = await playwright.launch({ executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content'].includes(a)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost'] });
  let page;
  const results = [], errors = [];
  try {
    const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
    page = await context.newPage(); page.setDefaultTimeout(60000);
    page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
    page.on('request', request => assert.ok(['localhost', '127.0.0.1'].includes(new URL(request.url()).hostname)));
    const cdp = await context.newCDPSession(page); await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal',
      hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
    const name = 'Polish1053' + Date.now().toString().slice(-6);
    // This lane's headless Chromium build delivers keydown but not text insertion
    // (fill and Input.insertText both leave the field empty); set it through the DOM.
    await page.goto(origin + '/identity.html'); await page.locator('#callsign').evaluate((input,value)=>{input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}))},name); await page.locator('#register').click();
    await page.locator('#account').waitFor({ state: 'visible' });
    const profiles = [[0,'Modern',1920,1080],[1,'Modern',1920,1080],[2,'Classic',800,480],[3,'Modern',800,480],[4,'Classic',1920,1080]];
    for (const [index, style, width, height] of (process.argv[6] === '--timing-only' ? profiles.slice(0,2) : profiles)) {
      console.log('LOAD', phase, index, style, width, height);
      await page.setViewportSize({ width, height });
      await page.goto(origin + '/play/preview/menu.html');
      await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null || document.getElementById('runtime-error'), null, { timeout: 120000 });
      assert.equal(await page.locator('#runtime-error').count(), 0, 'runtime boot succeeds');
      const timing = await page.evaluate(() => ({ shellMs: performance.getEntriesByName('first-contentful-paint')[0]?.startTime,
        menuMs: preview.measurements.firstFrameMs }));
      await page.getByRole('button', { name: style, exact: true }).click();
      await page.locator('#online-open').click(); await page.locator('#online-panel[data-state="Servers"]').waitFor();
      await page.locator('.online-join').first().click(); await page.locator('#online-panel[data-state="Sides"]').waitFor();
      await page.locator('.online-side').first().click(); await page.locator('#online-panel[data-state="Hangar"]').waitFor();
      if (index !== 1) await page.screenshot({ path: path.join(output, `${phase}-docked-${style}-${width}x${height}.png`) });
      await page.locator('#online-launch').click(); await page.locator('#online-panel[data-state="Flight"]').waitFor();
      await page.waitForFunction(() => gameMenu.metrics().flight.frames > 0);
      timing.flightMs = await page.evaluate(() => performance.now());
      const shipId = await page.evaluate(() => gameMenu.metrics().online.shipId);
      const ship = () => state().ships.find(s => s.Id === shipId);
      await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 100 });
      await until(() => ship()?.throttle === -1, 'stop accepted');
      if (index !== 1) await page.screenshot({ path: path.join(output, `${phase}-flight-${style}-${width}x${height}.png`) });
      console.log('FIRST_FRAME_TIMING', JSON.stringify(timing));
      if (phase === 'after' && index === 0) {
        results.push(await exercise(page, cdp, ship));
      }
      results.push({ phase, cache: index === 0 ? 'cold' : 'warm', style, width, height, ...timing });
      console.log('TIMING', JSON.stringify(results.at(-1)));
      await page.locator('#online-close').click(); await page.locator('#online-panel').waitFor({ state: 'detached' });
      await until(() => !state().players.includes(name), 'clean logoff');
    }
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, phase + '-polish.json'), JSON.stringify({ passed: true, headlessGpuRepresentative: false, results }, null, 2));
  } catch (error) {
    if (page && !page.isClosed()) {
      fs.writeFileSync(path.join(output, phase + '-failure.txt'), String(error) + '\n' + await page.locator('body').innerText());
      fs.writeFileSync(path.join(output, phase + '-metrics.json'), JSON.stringify(await page.evaluate(() => ({ game: window.gameMenu?.metrics(), pacing: window.preview?.pacing?.() })), null, 2));
    }
    throw error;
  } finally { await browser.close(); }
}
async function exercise(page, cdp, ship) {
  // The injected object implements only the standard browser Gamepad shape;
  // production poll/bindings/dead zones and actual FedPackets remain untouched.
  await page.evaluate(() => {
    window.fakePad = { index: 0, connected: true, mapping: 'standard', axes: [0,0,0,0], buttons: Array.from({length:17}, () => ({pressed:false,value:0})) };
    window.fakePadReads = 0; navigator.getGamepads = () => { fakePadReads++; return [fakePad]; };
    window.proofAudio = new AudioContext();
  });
  await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 100 });
  await page.waitForTimeout(200);
  const pollsStart = await page.evaluate(() => ({reads:fakePadReads,frames:gameMenu.metrics().flight.frames}));
  await page.waitForFunction(n => gameMenu.metrics().flight.frames >= n + 10, pollsStart.frames);
  const pollsEnd = await page.evaluate(() => ({reads:fakePadReads,frames:gameMenu.metrics().flight.frames}));
  assert.equal(pollsEnd.reads-pollsStart.reads, pollsEnd.frames-pollsStart.frames, 'exactly one Gamepad API poll per flight frame');
  await page.evaluate(() => { fakePad.axes = [0,-1,-1,-1]; fakePad.buttons[0] = {pressed:true,value:1}; });
  await until(() => ship()?.yaw > .99 && ship()?.pitch > .99 && ship()?.throttle > .99 && (ship().StateM & 49152) !== 0, 'pad turn/throttle/fire');
  const pad = {...ship()};
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await until(() => ship()?.yaw === 0 && ship()?.pitch === 0 && ship()?.throttle === -1 && ship()?.StateM === 0, 'blur held pad neutral');
  await page.waitForTimeout(400); assert.equal(ship().yaw, 0, 'held pad cannot reassert while blurred');
  await page.evaluate(() => { fakePad.axes = [0,0,0,0]; fakePad.buttons[0] = {pressed:false,value:0}; window.dispatchEvent(new Event('focus')); });
  await page.locator('#theCanvas').focus(); await page.keyboard.down('ArrowLeft'); await page.keyboard.down('ArrowUp'); await page.keyboard.press('Backslash', {delay:100}); await page.keyboard.down('Space');
  await until(() => ship()?.yaw > .99 && ship()?.pitch > .99 && ship()?.throttle > .99 && (ship().StateM & 49152) !== 0, 'equivalent keys');
  const keyboard = {...ship()};
  for (const axis of ['yaw','pitch','throttle']) assert.equal(pad[axis], keyboard[axis]);
  await page.keyboard.up('ArrowLeft'); await page.keyboard.up('ArrowUp'); await page.keyboard.up('Space');
  await page.keyboard.press('Backspace', {delay:100});
  await page.evaluate(() => { fakePad.axes = [0,0,-1,0]; fakePad.buttons[0] = {pressed:true,value:1}; });
  await until(() => ship()?.yaw > .99 && (ship().StateM & 49152) !== 0, 'held stick and fire before disconnect');
  await page.evaluate(() => { fakePad.connected = false; window.dispatchEvent(Object.assign(new Event('gamepaddisconnected'), {gamepad:fakePad})); });
  await until(() => ship()?.yaw === 0 && ship()?.throttle === -1 && ship()?.StateM === 0, 'disconnect neutral');
  // Audio has already been activated by a genuine keyboard gesture. Observe
  // the real RAF gaps caused by browser CPU throttling, with no synthetic load.
  await page.keyboard.down('ArrowRight');
  const transitions = [];
  await page.evaluate(() => {
    window.pacingObservations = []; window.observePacing = true;
    const observe = () => {
      if (!observePacing) return;
      const p = preview.pacing();
      if (pacingObservations.at(-1)?.scale !== p.scale) pacingObservations.push({...p, viewport:gameMenu.metrics().flight.viewport,audio:gameFlight.audioStates()});
      requestAnimationFrame(observe);
    };
    requestAnimationFrame(observe);
  });
  for (const rate of [20, 1]) {
    await cdp.send('Emulation.setCPUThrottlingRate', {rate});
    await page.waitForFunction(expected => {
      const p = preview.pacing(); if (document.getElementById('runtime-error')) throw Error('Runtime failed during pacing proof'); return expected === 20 ? p.scale === .5 && p.slow && gameFlight.audioStates().every(s=>s==='suspended') : p.scale === 1 && !p.slow && gameFlight.audioStates().every(s=>s==='running');
    }, rate, {timeout:120000});
    transitions.push(await page.evaluate(() => ({...preview.pacing(), audio: gameFlight.audioStates(), viewport:gameMenu.metrics().flight.viewport})));
    assert.equal(transitions.at(-1).viewport.width, transitions.at(-1).bufferWidth);
    assert.equal(transitions.at(-1).viewport.height, transitions.at(-1).bufferHeight);
    await page.keyboard.up('ArrowRight'); await page.keyboard.down('ArrowLeft');
    await until(() => ship()?.yaw > .99, 'fresh left input at throttle rate ' + rate, 60000);
    await page.keyboard.up('ArrowLeft'); await page.keyboard.down('ArrowRight');
    await until(() => ship()?.yaw < -.99, 'fresh right input at throttle rate ' + rate, 60000);
    transitions.at(-1).freshControlReversalsAccepted = true;
  }
  await page.keyboard.up('ArrowRight');
  const observedSteps = await page.evaluate(() => { observePacing=false; return pacingObservations; });
  assert.deepEqual(observedSteps.map(p=>p.scale), [1,.75,.5,.75,1]);
  for (const p of observedSteps) { assert.equal(p.viewport.width,p.bufferWidth); assert.equal(p.viewport.height,p.bufferHeight); }
  await page.evaluate(() => { window.proofDprDescriptor = Object.getOwnPropertyDescriptor(window, 'devicePixelRatio'); Object.defineProperty(window, 'devicePixelRatio', {configurable:true,value:3}); });
  await page.waitForFunction(() => preview.pacing().dpr === 2 && preview.pacing().bufferWidth === document.getElementById('canvasHolder').clientWidth * 2);
  const dprCap = await page.evaluate(() => preview.pacing());
  await page.evaluate(() => { if (window.proofDprDescriptor) Object.defineProperty(window, 'devicePixelRatio', proofDprDescriptor); else delete window.devicePixelRatio; });
  return { dprCap, observedSteps, gamepad: { pad: {yaw:pad.yaw,pitch:pad.pitch,throttle:pad.throttle,stateM:pad.StateM}, keyboard: {yaw:keyboard.yaw,pitch:keyboard.pitch,throttle:keyboard.throttle,stateM:keyboard.StateM}, blurNeutral:true, disconnectNeutral:true, pollsPerFrame:1 }, pacing:transitions };
}
main().catch(error => {console.error(error);process.exitCode=1});
