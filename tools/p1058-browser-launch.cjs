// Local Chromium proof: "human pace" launch clicks after side select (P1058).
// Args: npm dir, host state dir, evidence dir, case (0|100|500|2000|resume),
//       optional host stdout log path, optional --style Classic|Modern,
//       optional --verbose (navigates with ?log=verbose).
// Every request stays on loopback; no production rule is changed and no
// server/wire message is added.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2] || '.scratch/p1058/npm', 'node_modules');
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const stateDir = path.resolve(process.argv[3]);
const output = path.resolve(process.argv[4]);
const testCase = process.argv[5] || '0';
const hostLog = path.resolve(process.argv[6] || path.join(stateDir, 'host.log'));
const style = process.argv.includes('--style') ? process.argv[process.argv.indexOf('--style') + 1] : 'Modern';
const verbose = process.argv.includes('--verbose');
// P1059: dev switch on, so the toolbar renders exactly as before P1059.
const origin = 'http://localhost:8048', menu = origin + '/play/preview/menu.html?dev=1' + (verbose ? '&log=verbose' : '');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const readState = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
const readyLines = () => fs.existsSync(hostLog)
  ? fs.readFileSync(hostLog, 'utf8').split('\n').filter(line => /^\[ready\] .*fReady=True/.test(line)) : [];
// The per-asset chatter the brief names; it must not reach the visible levels.
const PER_ASSET = /\[monogame\]|\[sim\]|hud-10|target-pane/;
async function until(predicate, label, timeout = 30000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (predicate()) return true; await delay(50); }
  throw Error('Timed out: ' + label);
}
const snapshot = () => ({
  state: gameMenu.metrics().online.state, guard: gameMenu.metrics().launch.guard,
  readySends: gameMenu.metrics().launch.readySends, shipId: gameMenu.metrics().online.shipId,
  joinedShipId: gameMenu.metrics().launch.joinedShipId, stage: gameMenu.metrics().launch.stage,
  inGame: gameMenu.metrics().launch.inGame, busy: gameMenu.metrics().launch.busy,
  resumeSuccesses: gameMenu.metrics().online.resumeSuccesses,
  resumeAttempts: gameMenu.metrics().online.resumeAttempts, reconnecting: gameMenu.metrics().online.reconnecting,
  disabled: document.getElementById('online-launch')?.disabled ?? null,
  reason: document.getElementById('launch-reason')?.textContent ?? null,
  status: document.querySelector('#online-panel [role="status"]')?.textContent ?? null,
  alert: document.querySelector('#online-panel [role="alert"]')?.textContent ?? null,
});
async function main() {
  fs.mkdirSync(output, { recursive: true });
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const browser = await playwright.launch({ executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content'].includes(a)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost'] });
  let page;
  const errors = [], logs = [], responses = [];
  const record = { case: testCase, style, verbose, browserVersion: null, headlessGpuRepresentative: false };
  try {
    const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
    page = await context.newPage(); page.setDefaultTimeout(120000);
    page.on('pageerror', error => { errors.push(error.message); console.error('PAGEERROR', error.message); });
    page.on('console', message => logs.push({ type: message.type(), text: message.text() }));
    page.on('request', request => assert.ok(['localhost', '127.0.0.1'].includes(new URL(request.url()).hostname), 'loopback only'));
    page.on('response', response => {
      const url = new URL(response.url());
      if (url.hostname === 'localhost') responses.push({ path: url.pathname, status: response.status() });
    });
    const cdp = await context.newCDPSession(page); await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal',
      hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
    record.browserVersion = browser.version();
    await page.goto(origin + '/identity.html'); await page.locator('#login').waitFor({ state: 'visible' });
    const name = 'Launch1058' + Date.now().toString().slice(-6);
    record.callsign = name;
    // This lane's headless Chromium build delivers keydown but not text insertion
    // (keystrokes and Input.insertText both leave an input empty); set the field
    // through the DOM and assert it landed before the passkey request reads it.
    await page.locator('#callsign').evaluate((input, value) => {
      input.value = value; input.dispatchEvent(new Event('input', { bubbles: true }));
    }, name);
    assert.equal(await page.locator('#callsign').inputValue(), name);
    await page.locator('#register').click();
    await page.locator('#account').waitFor({ state: 'visible' });
    await page.locator('#signout').click(); await page.locator('#login').waitFor({ state: 'visible' });
    await page.locator('#signin').click(); await page.locator('#account').waitFor({ state: 'visible' });

    await page.goto(menu);
    responses.length = 0;   // the game page's own network outcome (the reported red line)
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
    record.iconLink = await page.evaluate(() => document.querySelector('link[rel="icon"]')?.getAttribute('href') ?? null);
    await page.getByRole('button', { name: style, exact: true }).click();
    await page.waitForFunction(expected => gameMenu.metrics().gameStyle === expected, style);
    await page.locator('#online-open').click();
    await page.locator('#online-panel[data-state="Servers"]').waitFor();
    await page.locator('.online-join').first().click();
    await page.locator('#online-panel[data-state="Sides"]').waitFor({ timeout: 120000 });
    const readyBefore = readyLines().length;
    await page.locator('.online-side').first().click();
    await page.locator('#online-panel[data-state="Hangar"]').waitFor();
    record.atHangar = await page.evaluate(snapshot);

    // Record what the DOM click did and the world values the guard saw.
    await page.evaluate(() => {
      window.__launchProbe = { domClicks: 0 };
      document.getElementById('online-launch').addEventListener('click', () => { window.__launchProbe.domClicks++; });
    });

    let firstClick;
    if (testCase === 'resume') {
      // The ordinary auto-reconnect resume: between sockets the session re-dials
      // and the join burst re-declares the own player, so the world briefly has
      // no launch pose. Click in that window if the page still offers launch.
      fs.writeFileSync(path.join(stateDir, 'drop-ws'), '');
      firstClick = await page.evaluate(async () => {
        const probe = { sawNull: false, clickedAtNull: false, samples: [], atNull: null };
        const started = performance.now();
        while (performance.now() - started < 20000) {
          const metrics = gameMenu.metrics();
          const button = document.getElementById('online-launch');
          probe.samples.push({ ms: Math.round(performance.now() - started), state: metrics.online.state,
            shipId: metrics.online.shipId, joinedShipId: metrics.launch.joinedShipId,
            resumeSuccesses: metrics.online.resumeSuccesses, disabled: button?.disabled ?? null,
            reason: document.getElementById('launch-reason')?.textContent ?? null });
          if (!probe.sawNull && metrics.online.state === 'Hangar' && metrics.launch.shipId === null) {
            probe.sawNull = true;
            probe.atNull = { launch: metrics.launch, disabled: button?.disabled ?? null,
              reason: document.getElementById('launch-reason')?.textContent ?? null,
              status: document.querySelector('#online-panel [role="status"]')?.textContent ?? null };
            // Click it whether or not the page still offers launch: a disabled
            // button is force-enabled for this one click so the guard's own
            // message is measured too (the page control is restored at once).
            if (button) {
              const wasDisabled = button.disabled;
              button.disabled = false; button.click(); button.disabled = wasDisabled;
              probe.clickedAtNull = true; probe.forced = wasDisabled;
            }
            break;
          }
          await new Promise(resolve => setTimeout(resolve, 10));
        }
        await new Promise(resolve => setTimeout(resolve, 500));
        probe.after = { metrics: gameMenu.metrics(), domClicks: window.__launchProbe.domClicks };
        return probe;
      });
      await page.screenshot({ path: path.join(output, 'blocked-' + style.toLowerCase() + '-resume.png') });
    } else if (testCase === 'hold') {
      // Refuse the upgrade for a moment so the client's real resume retry keeps
      // the session between sockets: the launch affordance must be disabled with
      // a visible reason, and enable again as soon as the resume completes.
      fs.writeFileSync(path.join(stateDir, 'hold-ws'), '');
      fs.writeFileSync(path.join(stateDir, 'drop-ws'), '');
      const blocked = await page.evaluate(async () => {
        const started = performance.now();
        while (performance.now() - started < 3000) {
          const metrics = gameMenu.metrics();
          const button = document.getElementById('online-launch');
          const reason = document.getElementById('launch-reason')?.textContent ?? null;
          if (metrics.online.state === 'Hangar' && button?.disabled && reason)
            return { disabled: button.disabled, reason, shipId: metrics.online.shipId,
              reconnecting: metrics.online.reconnecting, resumeAttempts: metrics.online.resumeAttempts,
              waitedMs: Math.round(performance.now() - started) };
          await new Promise(resolve => setTimeout(resolve, 20));
        }
        return null;
      });
      record.blockedAffordance = blocked;
      await page.screenshot({ path: path.join(output, 'blocked-' + style.toLowerCase() + '-hold.png') });
      fs.unlinkSync(path.join(stateDir, 'hold-ws'));
      await page.waitForFunction(() => {
        const button = document.getElementById('online-launch');
        return button && !button.disabled && gameMenu.metrics().online.resumeSuccesses > 0;
      }, null, { timeout: 30000 });
      record.enabledAfterResume = await page.evaluate(() => ({
        disabled: document.getElementById('online-launch')?.disabled ?? null,
        reason: document.getElementById('launch-reason')?.textContent ?? null,
        resumeSuccesses: gameMenu.metrics().online.resumeSuccesses,
        resumeAttempts: gameMenu.metrics().online.resumeAttempts,
      }));
      firstClick = await page.evaluate(() => ({ atClick: gameMenu.metrics(),
        disabled: document.getElementById('online-launch')?.disabled ?? null,
        reason: document.getElementById('launch-reason')?.textContent ?? null,
        status: document.querySelector('#online-panel [role="status"]')?.textContent ?? null }));
      await page.evaluate(() => document.getElementById('online-launch').click());
    } else {
      await delay(Number(testCase));
      firstClick = await page.evaluate(() => ({
        atClick: gameMenu.metrics(), disabled: document.getElementById('online-launch')?.disabled ?? null,
        reason: document.getElementById('launch-reason')?.textContent ?? null,
        status: document.querySelector('#online-panel [role="status"]')?.textContent ?? null,
      }));
      if (!firstClick.disabled) await page.evaluate(() => document.getElementById('online-launch').click());
    }

    // Watch the click for 20 s.
    const trail = [];
    for (let index = 0; index < 80; index++) {
      const state = await page.evaluate(snapshot);
      trail.push(state);
      if (state.state === 'Flight' || (state.alert && index >= 8)) break;
      await delay(250);
    }
    record.firstClick = firstClick;
    record.trail = trail;
    record.reachedFlight = trail.some(state => state.state === 'Flight');

    // "Never silent": a click that fires must either send the ready or show why not.
    const visibleReason = Boolean(firstClick.reason || firstClick.atNull?.reason || firstClick.status ||
      trail.slice(0, 8).some(state => state.reason || state.status || state.alert || state.disabled));
    record.blockedFeedback = { atNull: firstClick.atNull ?? null,
      alert: trail.slice(0, 8).map(state => state.alert).filter(Boolean).at(-1) ?? null };
    record.clickSent = (trail[0]?.readySends ?? 0) > 0;
    record.silentDrop = await page.evaluate(() => window.__launchProbe.domClicks) > 0 && !record.clickSent && !visibleReason;

    // A later click on the same session (no reload), the way the reported session recovered.
    if (!record.reachedFlight) {
      await delay(2000);
      const before = await page.evaluate(() => gameMenu.metrics().launch.readySends);
      const disabled = await page.evaluate(() => document.getElementById('online-launch')?.disabled ?? null);
      await page.evaluate(() => document.getElementById('online-launch')?.click());
      const later = [];
      for (let index = 0; index < 80; index++) {
        const state = await page.evaluate(snapshot);
        later.push(state);
        if (state.state === 'Flight' || (state.alert && index >= 8)) break;
        await delay(250);
      }
      record.laterClick = { readySendsBefore: before, disabled, trail: later,
        sent: (later.at(-1)?.readySends ?? before) > before, reachedFlight: later.some(state => state.state === 'Flight') };
    }

    record.serverReadyLines = readyLines().slice(readyBefore);
    record.intendedLaunches = (record.clickSent ? 1 : 0) + (record.laterClick?.sent ? 1 : 0);
    record.console = {
      byType: logs.reduce((counts, line) => ({ ...counts, [line.type]: (counts[line.type] ?? 0) + 1 }), {}),
      perAssetVisible: logs.filter(line => PER_ASSET.test(line.text) && (line.type === 'log' || line.type === 'info')).length,
      perAssetDebug: logs.filter(line => PER_ASSET.test(line.text) && line.type === 'debug').length,
      warningLines: logs.filter(line => line.type === 'warning' || line.type === 'error').length,
    };
    record.responses = responses;
    record.notFound = responses.filter(response => response.status === 404);
    record.iconResponses = responses.filter(response => response.path.includes('favicon'));
    record.errors = errors;

    // Leave cleanly so the next case can reuse the host.
    await page.evaluate(() => document.getElementById('online-close')?.click());
    await page.locator('#online-panel').waitFor({ state: 'detached' });
    await until(() => !readState().players.includes(name), 'clean logoff');
    record.logoffNoZombie = true;

    // Assertions: one server [ready] per intended launch, no silent click, no 404.
    assert.equal(record.silentDrop, false, 'a click must never be dropped without feedback');
    assert.equal(record.serverReadyLines.length, record.intendedLaunches, 'one server [ready] per intended launch');
    assert.deepEqual(record.notFound, [], 'no 404 responses');
    assert.ok(record.iconLink && record.iconLink.length > 0, 'the page declares a favicon');
    if (verbose) assert.ok(record.console.perAssetVisible > 0, 'verbose restores the per-asset stream');
    else {
      assert.equal(record.console.perAssetVisible, 0, 'quiet default hides per-asset info lines');
      assert.ok(record.console.perAssetDebug > 0, 'quiet default still emits them at debug level');
    }
    if (testCase === 'hold') {
      assert.ok(record.blockedAffordance, 'the blocked affordance was rendered');
      assert.ok(record.blockedAffordance.reason.length > 0, 'the blocked affordance names the reason');
      assert.equal(record.enabledAfterResume.disabled, false, 'the button enables as soon as it can launch');
      assert.equal(record.reachedFlight, true, 'the launch completes after the resume');
    }
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'launch-' + testCase + (style === 'Modern' ? '' : '-' + style.toLowerCase()) + (verbose ? '-verbose' : '') + '.json'), JSON.stringify(record, null, 2));
    fs.writeFileSync(path.join(output, 'console-' + testCase + (style === 'Modern' ? '' : '-' + style.toLowerCase()) + (verbose ? '-verbose' : '') + '.txt'),
      logs.map(line => line.type + ': ' + line.text).join('\n'));
    console.log('CASE_RESULT', testCase, style, verbose ? 'verbose' : 'quiet', JSON.stringify({
      reachedFlight: record.reachedFlight, clickSent: record.clickSent, silentDrop: record.silentDrop,
      readySends: record.trail.at(-1)?.readySends, serverReadyLines: record.serverReadyLines.length,
      guard: record.trail.at(0)?.guard, reason: record.firstClick.reason ?? record.firstClick.atNull?.reason ?? null,
      laterWorked: record.laterClick?.reachedFlight ?? null, perAssetVisible: record.console.perAssetVisible,
      perAssetDebug: record.console.perAssetDebug, notFound: record.notFound.length, icon: record.iconLink }));
  } catch (error) {
    record.failure = String(error);
    if (page && !page.isClosed()) {
      record.body = await page.locator('body').innerText().catch(() => null);
      record.failureMetrics = await page.evaluate(() => window.gameMenu?.metrics() ?? null).catch(() => null);
      await page.screenshot({ path: path.join(output, 'failure-' + testCase + '.png') }).catch(() => {});
    }
    fs.writeFileSync(path.join(output, 'launch-' + testCase + (verbose ? '-verbose' : '') + '.json'), JSON.stringify(record, null, 2));
    throw error;
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
