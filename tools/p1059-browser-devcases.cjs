// P1059 — the browser developer/proof switch, proved in real Chromium.
//
// With the switch off (?dev absent, default) the developer/proof controls must be
// ABSENT (not rendered) and every player control present; with ?dev=1 the same
// page renders them, exactly as before P1059. Both HUD styles at 1920×1080 and
// 800×480. It also proves the documented stickiness: ?dev=1 keeps the switch on
// for later visits and ?dev=0 turns it off again.
//
// Args: preinstalled npm directory, proof host state directory, output directory.
// Every request stays on loopback; no production rule, wire message or limit is
// touched by this harness.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules');
const stateDir = path.resolve(process.argv[3]);
const output = path.resolve(process.argv[4]);
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const origin = 'http://localhost:8048', menu = origin + '/play/preview/menu.html';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const readState = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
async function until(predicate, label, timeout = 60000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await predicate()) return; await delay(100); }
  throw Error('Timed out: ' + label);
}

// Only these three are developer/proof (see docs/reports/BROWSER_DEV_CONTROLS_P1059.md).
const DEV = ['#flight-rock', '#flight-waypoint', '#online-pose'];
const WAYPOINT_FIELDS = ['#waypoint-x', '#waypoint-y', '#waypoint-z', '#waypoint-go'];
// Player controls that must exist in the Flight panel in both switch states.
const PLAYER = ['#flight-contact', '#flight-dock', '#flight-starmap', '#flight-fullscreen',
  '#flight-warp', '#online-briefing', '#online-close'];

async function counts(page, selectors) {
  const result = {};
  for (const selector of selectors) result[selector] = await page.locator(selector).count();
  return result;
}

async function main() {
  fs.mkdirSync(output, { recursive: true });
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const browser = await playwright.launch({ executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content'].includes(a)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost'] });
  const results = [];
  let page;
  try {
    const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
    page = await context.newPage(); page.setDefaultTimeout(120000);
    const errors = [];
    page.on('pageerror', error => { errors.push(error.message); console.error('PAGEERROR', error.message); });
    page.on('request', request => assert.ok(['localhost', '127.0.0.1'].includes(new URL(request.url()).hostname), 'loopback only'));
    const cdp = await context.newCDPSession(page); await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal',
      hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
    const name = 'DevSwitch1059' + Date.now().toString().slice(-6);
    await page.goto(origin + '/identity.html'); await page.locator('#login').waitFor({ state: 'visible' });
    // This lane's headless Chromium build delivers keydown but not text insertion;
    // set the callsign through the DOM (same as the other browser harnesses).
    await page.locator('#callsign').evaluate((input, value) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); }, name);
    await page.locator('#register').click(); await page.locator('#account').waitFor({ state: 'visible' });

    const stored = () => page.evaluate(() => { try { return localStorage.getItem('allegiance.dev'); } catch { return null; } });
    const disconnect = async () => {
      await page.evaluate(() => document.getElementById('online-close')?.click());
      await page.locator('#online-panel').waitFor({ state: 'detached' });
      await until(() => !readState().players.includes(name), 'clean logoff');
    };

    // Load the menu with (or without) the switch, then fly to the Flight panel.
    const fly = async (style, width, height, query) => {
      await page.setViewportSize({ width, height });
      await page.goto(menu + query);
      await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
      await page.getByRole('button', { name: style, exact: true }).click();
      await page.waitForFunction(expected => gameMenu.metrics().gameStyle === expected, style);
      await page.locator('#online-open').click();
      await page.locator('#online-panel[data-state="Servers"]').waitFor();
      await page.locator('.online-join').first().click();
      await page.locator('#online-panel[data-state="Sides"]').waitFor({ timeout: 120000 });
      await page.locator('.online-side').first().click();
      await page.locator('#online-panel[data-state="Hangar"]').waitFor();
      await page.locator('#online-launch').click();
      await page.locator('#online-panel[data-state="Flight"]').waitFor({ timeout: 120000 });
      await page.waitForFunction(() => gameMenu.metrics().flight.frames > 0, null, { timeout: 120000 });
      return await page.evaluate(() => gameMenu.metrics().dev);
    };

    // Both switch states on one page: off (absent dev controls, present players),
    // then ?dev=1 (dev controls render exactly as before P1059).
    const inspect = async (style, width, height, label, capture) => {
      // A fresh profile has no stored choice: prove the default is off for every
      // combination, not only the first one (?dev=1 is sticky by design).
      await page.evaluate(() => { try { localStorage.removeItem('allegiance.dev'); } catch { /* refused storage */ } });
      const devOff = await fly(style, width, height, '');
      const off = await counts(page, [...DEV, ...PLAYER, '#map-sector']);
      assert.equal(devOff, false, 'default switch is off');
      for (const selector of DEV) assert.equal(off[selector], 0, selector + ' must not render with the switch off');
      for (const selector of PLAYER) assert.equal(off[selector], 1, selector + ' must render with the switch off');
      // The starmap selector stays a player control (native SelectCluster).
      await page.locator('#flight-starmap').click();
      await page.locator('#map-sector').waitFor({ state: 'attached' });
      const offStarmap = await counts(page, ['#map-sector', '#flight-rock', '#flight-waypoint', '#online-pose']);
      await page.locator('#flight-starmap').click();
      assert.equal(offStarmap['#map-sector'], 1, '#map-sector is player-visible (native starmap selection)');
      assert.equal(offStarmap['#flight-rock'] + offStarmap['#flight-waypoint'] + offStarmap['#online-pose'], 0);
      if (capture) {
        await page.locator('#online-panel').screenshot({ path: path.join(output, 'toolbar-' + style.toLowerCase() + '-off.png') });
        await page.screenshot({ path: path.join(output, 'flight-' + style.toLowerCase() + '-off.png') });
      }
      await disconnect();

      const devOn = await fly(style, width, height, '?dev=1');
      const on = await counts(page, [...DEV, ...PLAYER]);
      assert.equal(devOn, true, '?dev=1 turns the switch on');
      for (const selector of DEV) assert.equal(on[selector], 1, selector + ' must render with the switch on');
      for (const selector of PLAYER) assert.equal(on[selector], 1, selector + ' must still render with the switch on');
      await page.locator('#flight-waypoint').click();
      const fields = await counts(page, WAYPOINT_FIELDS);
      for (const selector of WAYPOINT_FIELDS) assert.equal(fields[selector], 1, selector + ' renders for the harness');
      await page.locator('#flight-waypoint').click();
      if (capture) {
        await page.locator('#online-panel').screenshot({ path: path.join(output, 'toolbar-' + style.toLowerCase() + '-on.png') });
        await page.screenshot({ path: path.join(output, 'flight-' + style.toLowerCase() + '-on.png') });
      }
      await disconnect();
      results.push({ style, width, height, label, devOff, devOn, off, offStarmap, on, waypointFields: fields,
        storedAfterUrlSwitch: await stored(), dev: await page.evaluate(() => gameMenu.metrics().dev) });
    };

    const combos = [['Classic', 1920, 1080, false], ['Modern', 1920, 1080, false],
      ['Classic', 800, 480, true], ['Modern', 800, 480, true]];
    for (const [style, width, height, capture] of combos) {
      console.log('CASE', style, width, height);
      await inspect(style, width, height, style.toLowerCase() + '-' + width + 'x' + height, capture);
      results.at(-1).consoleErrors = [...errors];
      assert.deepEqual(errors, [], 'no page errors');
      console.log('PASS', style, width, height, JSON.stringify({ devOff: results.at(-1).devOff, devOn: results.at(-1).devOn }));
    }

    // Documented stickiness, proved once on the real page: ?dev=1 keeps the switch
    // on for a later plain visit, and ?dev=0 turns it off again.
    const plain = async () => {
      await page.goto(menu);
      await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
      return { dev: await page.evaluate(() => gameMenu.metrics().dev), stored: await stored() };
    };
    const sticky = {};
    await page.goto(menu + '?dev=1');
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
    sticky.urlOn = { dev: await page.evaluate(() => gameMenu.metrics().dev), stored: await stored() };
    sticky.plainAfterOn = await plain();
    await page.goto(menu + '?dev=0');
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
    sticky.urlOff = { dev: await page.evaluate(() => gameMenu.metrics().dev), stored: await stored() };
    sticky.plainAfterOff = await plain();
    assert.equal(sticky.urlOn.dev, true); assert.equal(sticky.urlOn.stored, '1');
    assert.equal(sticky.plainAfterOn.dev, true, '?dev=1 stays on for later visits');
    assert.equal(sticky.urlOff.dev, false); assert.equal(sticky.urlOff.stored, '0');
    assert.equal(sticky.plainAfterOff.dev, false, '?dev=0 turns it off again');

    const local = await page.evaluate(() => typeof window.allegianceDevEnabled === 'function' && window.allegianceDev.enabled() === false);
    assert.equal(local, true, 'the page exposes the documented switch');
    const proof = { passed: true, browserVersion: browser.version(), headlessGpuRepresentative: false,
      devControls: DEV, playerControls: PLAYER, results, sticky, consoleErrors: errors };
    fs.writeFileSync(path.join(output, 'dev-controls-proof.json'), JSON.stringify(proof, null, 2));
    const pngBytes = fs.readdirSync(output).filter(n => n.endsWith('.png')).reduce((n, f) => n + fs.statSync(path.join(output, f)).size, 0);
    console.log(JSON.stringify({ passed: true, combos: results.length, sticky: true, pngBytes }));
  } catch (error) {
    if (page && !page.isClosed()) {
      fs.writeFileSync(path.join(output, 'failure.txt'), String(error) + '\n' + await page.locator('body').innerText().catch(() => ''));
      fs.writeFileSync(path.join(output, 'failure-metrics.json'), JSON.stringify(await page.evaluate(() => window.gameMenu?.metrics() ?? null).catch(() => null), null, 2));
      await page.screenshot({ path: path.join(output, 'failure.png') }).catch(() => {});
      fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify(results, null, 2));
    }
    throw error;
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
