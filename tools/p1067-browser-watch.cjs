// P1067 — browser "Watch" (desktop director camera over a browser battle).
// For a workstation with the already-installed P1051 Chromium modules. No npm install.
// Args: NPM_DIRECTORY HOST_STATE OUTPUT GENERATION [SAMPLE_SECONDS=60].
// Run once per fresh host --bots 5. Every browser process is closed here; the
// calling shell owns the proof-host PID and its stop file.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules'), stateDir = path.resolve(process.argv[3]);
const out = path.resolve(process.argv[4]), generation = path.resolve(process.argv[5]);
const sampleSeconds = Number(process.argv[6] || 60);
assert.ok(Number.isFinite(sampleSeconds) && sampleSeconds >= 10 && sampleSeconds <= 300);
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const origin = 'https://localhost:8443', menu = origin + '/play/menu.html?dev=1&director-seed=1067';
const state = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json'))), delay = ms => new Promise(r => setTimeout(r, ms));
const percentile = (a, p) => [...a].sort((x, y) => x - y)[Math.min(a.length - 1, Math.floor(a.length * p))];
async function until(test, label, timeout = 180000) { const end = Date.now() + timeout; while (Date.now() < end) { if (await test()) return; await delay(100); } throw Error('Timed out: ' + label); }
async function type(page, selector, value) { await page.locator(selector).evaluate((input, text) => { input.value = text; input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new Event('change', { bubbles: true })); }, String(value)); }
// One rAF interval collector started from the page; returns the intervals when stopped.
async function startFrames(page) {
  await page.evaluate(() => { window.p1067Frames = { intervals: [], active: true }; let previous; const tick = now => { if (previous !== undefined) p1067Frames.intervals.push(now - previous); previous = now; if (p1067Frames.active) requestAnimationFrame(tick); }; requestAnimationFrame(tick); });
}
async function stopFrames(page, label) {
  const intervals = await page.evaluate(() => { p1067Frames.active = false; return p1067Frames.intervals; });
  assert.ok(intervals.length > 0, 'rAF intervals were collected for ' + label);
  return { label, frames: intervals.length, p50Ms: percentile(intervals, .5), p95Ms: percentile(intervals, .95), p99Ms: percentile(intervals, .99) };
}
async function main() {
  fs.mkdirSync(out, { recursive: true });
  // The host writes state.json only after it boots; a missing or mid-swap file is not ready yet.
  await until(() => { try { return state().battle.ready; } catch (e) { if (e.code === 'ENOENT' || e instanceof SyntaxError) return false; throw e; } }, 'capped bots authenticated and pilot slot reserved', 600000);
  const size = state().battle.size; assert.equal(size, 5, 'this harness is the brief\'s 5 v 5 run');
  const target = state().chart.find(w => w.ClusterId === 1053 && w.Name === state().battle.targetSector)?.destinationSectorId;
  assert.ok(target != null && target !== 1053, 'Ixion is an adjacent authored sector');
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const browser = await playwright.launch({ executablePath: await chromium.executablePath(), headless: true, args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content'].includes(a)), '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost', '--ignore-certificate-errors'] });
  const errors = [], samples = [], captures = [], pacing = [];
  let page;
  try {
    const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, ignoreHTTPSErrors: true });
    page = await context.newPage(); page.setDefaultTimeout(120000);
    await context.route('**/*', route => { const u = new URL(route.request().url()); if (!['localhost', '127.0.0.1'].includes(u.hostname)) throw Error('Non-loopback request: ' + u.origin); return route.continue(); });
    page.on('pageerror', error => errors.push(error.message));
    const cdp = await context.newCDPSession(page);
    await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
    await page.goto(origin + '/identity.html');
    await type(page, '#callsign', 'Browser1067' + Date.now().toString().slice(-7));
    await page.locator('#register').click(); await page.locator('#account').waitFor({ state: 'visible' });
    await page.goto(menu); await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null);
    await page.locator('#online-open').click(); await page.locator('#join-password').waitFor();
    await type(page, '#join-password', fs.readFileSync(path.join(stateDir, 'game-password'), 'utf8').trim());
    await page.locator('.online-join').first().click(); await page.locator('.online-side').first().click();
    await page.locator('#online-panel[data-state="Hangar"]').waitFor(); await page.locator('#online-launch').click();
    await page.locator('#online-panel[data-state="Flight"]').waitFor();
    await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 100 });
    const start = await page.evaluate(() => gameMenu.metrics()); const shipId = start.online.shipId;
    // Ordinary waypoint / aleph autopilot into Ixion — never a position override.
    const chart = state().chart.find(w => w.ClusterId === start.flight.sectorId && w.destinationSectorId === target); assert.ok(chart);
    const option = `#flight-warp option[value="${chart.WarpId}"]`;
    if (!await page.locator(option).count()) {
      await page.locator('#flight-waypoint').click();
      for (const [i, axis] of ['x', 'y', 'z'].entries()) await type(page, '#waypoint-' + axis, chart.Position[i] * .85);
      await page.locator('#waypoint-go').click(); await page.locator(option).waitFor({ state: 'attached', timeout: 240000 });
    }
    await page.locator('#flight-warp').selectOption(String(chart.WarpId));
    await page.waitForFunction(id => gameMenu.metrics().flight.sectorId === id, target, { timeout: 240000 });
    await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 100 });
    // Park away from the aleph and wait for the bots to engage in view.
    await page.locator('#flight-waypoint').click();
    for (const axis of ['x', 'y', 'z']) await type(page, '#waypoint-' + axis, axis === 'y' ? 550 : 0);
    await page.locator('#waypoint-go').click();
    await until(async () => { const m = await page.evaluate(() => gameMenu.metrics()); return m.flight.battle.contacts >= Math.min(3, size * 2 - 1); }, 'several real visible contacts in the battle', 240000);
    await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 100 });

    // F2 is the desktop's Director binding; the browser routes it through the
    // same action map. Prove one on/off round trip before the sampled profiles.
    await page.locator('#theCanvas').focus(); await page.keyboard.press('F2', { delay: 100 });
    await until(async () => (await page.evaluate(() => gameMenu.metrics())).flight.watch.active, 'F2 starts Watch', 15000);
    const f2on = await page.evaluate(() => gameMenu.metrics().flight.watch);
    await page.keyboard.press('F2', { delay: 100 });
    await until(async () => !(await page.evaluate(() => gameMenu.metrics())).flight.watch.active, 'F2 stops Watch', 15000);

    for (const [style, width, height] of [['Classic', 1920, 1080], ['Modern', 1920, 1080], ['Classic', 800, 480], ['Modern', 800, 480]]) {
      await page.setViewportSize({ width, height });
      await page.getByRole('button', { name: style, exact: true }).click(); await delay(1000);
      // Chase baseline pacing (no Watch).
      await startFrames(page); await delay(Math.max(5000, sampleSeconds * 1000 / 6)); pacing.push(await stopFrames(page, `${style} ${width}x${height} chase`));
      // Watch on through the toolbar button.
      await page.locator('#flight-watch').click();
      await until(async () => (await page.evaluate(() => gameMenu.metrics())).flight.watch.active, 'Watch starts from the toolbar', 15000);
      await page.waitForFunction(() => document.querySelector('#flight-watch')?.textContent.trim() === 'Stop watching');
      await startFrames(page);
      const end = Date.now() + sampleSeconds * 1000;
      let shotKinds = new Set(), subjectsFramed = new Set(), maxSubjects = 0;
      while (Date.now() < end) {
        const m = await page.evaluate(() => gameMenu.metrics());
        const b = m.flight.battle, w = m.flight.watch;
        // A pilot loss during the window is a failed run — never relabelled.
        assert.ok(m.flight.flying, 'pilot still flying while watching');
        assert.equal(m.flight.sectorId, target, 'pilot still in the battle sector while watching');
        assert.ok(w.active, 'Watch still active');
        if (w.subjectIndex >= 0) for (const category of Object.keys(w.categories)) subjectsFramed.add(category);
        maxSubjects = Math.max(maxSubjects, w.subjects);
        shotKinds.add(w.kind);
        samples.push({ style, width, height, sampledAt: Date.now(), sectorId: m.flight.sectorId, flying: m.flight.flying, contacts: b.contacts, meshes: b.meshes, standIns: b.standIns, sectorShips: b.sectorShips, watch: w });
        await delay(250);
      }
      pacing.push(await stopFrames(page, `${style} ${width}x${height} watch`));
      const latest = await page.evaluate(() => gameMenu.metrics());
      assert.ok(latest.flight.watch.shotChanges >= 3, 'shot changes over the sampled window (4-8 s dwell)');
      assert.ok(latest.flight.watch.subjects > 0, 'the director has subjects in sector');
      assert.ok(latest.flight.battle.contacts >= Math.min(3, size * 2 - 1), 'real visible contacts at capture');
      const file = `watch-${style.toLowerCase()}-${width}x${height}.jpg`;
      await page.screenshot({ path: path.join(out, file), type: 'jpeg', quality: 80 });
      captures.push({ file, style, width, height, sampledAt: Date.now(), kind: latest.flight.watch.kind, subjectIndex: latest.flight.watch.subjectIndex,
        categories: latest.flight.watch.categories, contacts: latest.flight.battle.contacts, meshes: latest.flight.battle.meshes,
        standIns: latest.flight.battle.standIns, hullIds: latest.flight.battle.hullIds, shotChanges: latest.flight.watch.shotChanges, seed: latest.flight.watch.seed,
        shotKinds: [...shotKinds], subjectKindsFramed: [...subjectsFramed], maxSubjects });
      // Back to chase: the same toggle, and any flight input cancels too.
      await page.locator('#flight-watch').click();
      await until(async () => !(await page.evaluate(() => gameMenu.metrics())).flight.watch.active, 'Watch stops', 15000);
      await page.locator('#theCanvas').focus(); await page.keyboard.press('F2', { delay: 100 });
      await until(async () => (await page.evaluate(() => gameMenu.metrics())).flight.watch.active, 'F2 starts Watch again', 15000);
      await page.keyboard.down('w');
      await until(async () => !(await page.evaluate(() => gameMenu.metrics())).flight.watch.active, 'a flight input cancels Watch', 5000);
      await page.keyboard.up('w');
      pacing.push({ label: `${style} ${width}x${height} cancel-on-input`, cancelledByFlightInput: true });
    }
    assert.ok(samples.every(s => s.sectorId === target && s.flying), 'no pilot loss in any sample');
    assert.ok(captures.length === 4, 'Classic and Modern at 1920x1080 and 800x480');
    const imageBytes = captures.reduce((n, c) => n + fs.statSync(path.join(out, c.file)).size, 0);
    assert.ok(imageBytes <= 2 * 1024 * 1024, 'capture set <= 2 MiB');
    assert.deepEqual(errors, []);
    // Compact evidence (samples + pacing + capture rows) so one assets directory
    // stays under the 2 MiB per-task evidence cap together with the captures.
    const report = { size, browserVersion: browser.version(), headlessGpuRepresentative: false, seed: 1067,
      f2KeyRoute: { started: f2on.active, seed: f2on.seed },
      chaseVersusWatchPacing: pacing, samples, captures, imageBytes,
      manifest: JSON.parse(fs.readFileSync(path.join(generation, 'battle-receipt.json'))) };
    fs.writeFileSync(path.join(out, 'browser-watch.json'), JSON.stringify(report, null, 2));
    await page.locator('#online-close').click(); await page.locator('#online-panel').waitFor({ state: 'detached' });
    console.log('BROWSER_WATCH_PASS', size, imageBytes);
  } catch (error) {
    if (page) fs.writeFileSync(path.join(out, 'failure.json'), JSON.stringify({ error: String(error), errors, samples, lastMetrics: await page.evaluate(() => gameMenu.metrics()).catch(e => String(e)) }, null, 2));
    throw error;
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
