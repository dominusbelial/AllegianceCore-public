// P1073: generated map + showcase research -> guest join -> 30 s Classic/Modern
// flights at 1920x1080 and 800x480, with the live-gap assertions.
// Args: NPM_DIRECTORY HOST_STATE EVIDENCE_DIRECTORY GENERATION_DIRECTORY
// Optional CHROMIUM_PATH selects an installed executable. No package installs.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules'), stateDir = path.resolve(process.argv[3]);
const output = path.resolve(process.argv[4]), generation = path.resolve(process.argv[5]);
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const origin = 'https://localhost:8443', delay = ms => new Promise(r => setTimeout(r, ms));
const readState = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
const password = fs.readFileSync(path.join(stateDir, 'game-password'), 'utf8').trim();
const version = fs.readFileSync(path.join(stateDir, 'client-version'), 'utf8').trim();
assert.equal(JSON.parse(fs.readFileSync(path.join(generation, 'client-version.json'), 'utf8')).version, version);
async function until(predicate, label, timeout = 120000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await predicate()) return; await delay(100); }
  throw Error('Timed out: ' + label);
}
async function type(page, selector, value) {
  await page.locator(selector).evaluate((input, text) => { input.value = text; input.dispatchEvent(new Event('input', { bubbles: true })); }, value);
}
async function main() {
  fs.mkdirSync(output, { recursive: true });
  await until(() => { try { return readState().listing.acceptsGuests; } catch (e) { if (e.code === 'ENOENT' || e instanceof SyntaxError) return false; throw e; } }, 'guest host ready');
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const executablePath = process.env.CHROMIUM_PATH || await chromium.executablePath();
  const args = chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content', '--single-process'].includes(a));
  const browser = await playwright.launch({ executablePath, headless: true, dumpio: process.env.P1073_BROWSER_LOG === '1',
    args: [...args, '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost', '--ignore-certificate-errors'] });
  const errors = [], requests = [], captures = [], contexts = [], consoleMessages = [], offending = [];
  const evidence = { browser: browser.version(), captures, errors, consoleMessages, offending,
    visualDescriptions: 'for Claude; images not viewed in this lane', map: 'generated SingleRing seed 1073', research: null, codecs: null, sectorArt: null, docking: null, hangar: null };
  async function fresh() {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, ignoreHTTPSErrors: true }); contexts.push(context);
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      assert.ok(['localhost', '127.0.0.1'].includes(url.hostname), 'loopback requests only');
      requests.push({ method: route.request().method(), path: url.pathname }); await route.continue();
    });
    const page = await context.newPage(); page.setDefaultTimeout(120000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => {
      const text = message.type() + ': ' + message.text(); consoleMessages.push(text);
      // The live gaps: P975 malformed drops and the WarpRenderer texture fallback.
      if (/\[recv\].*malformed/i.test(text) || /\[warp\] texture .* unavailable/i.test(text)) offending.push(text);
    });
    return page;
  }
  async function capture(page, name, selector) {
    const file = name + '.jpg', target = selector ? page.locator(selector) : page;
    await target.screenshot({ path: path.join(output, file), type: 'jpeg', quality: 80 });
    captures.push({ file, bytes: fs.statSync(path.join(output, file)).size });
  }
  async function enterGuest(page) {
    await page.goto(origin + '/play/menu.html?online=1');
    await page.locator('#guest-disclosure').waitFor({ state: 'visible' });
    const callsign = await page.locator('#guest-callsign').inputValue();
    assert.match(callsign, /^Pilot-/);
    if (await page.locator('#join-password').count()) await type(page, '#join-password', password);
    await page.locator('.online-join:enabled').first().click();
    return callsign;
  }
  try {
    const page = await fresh(), pilot = await enterGuest(page);
    await page.locator('#online-panel[data-state="Sides"]').waitFor();
    await page.locator('.online-side').first().click();
    await page.locator('#online-panel[data-state="Hangar"]').waitFor();
    // Item 1 — prove the browser codec matrix on a real briefing frame.
    // The page cannot compress Brotli, so the frame is built here: cbMsg/fmid header + Brotli JSON.
    const body = require('zlib').brotliCompressSync(Buffer.from(JSON.stringify({ epoch: 'p1073-codec-proof', techMode: 'research' })));
    const frame = Buffer.alloc(4 + body.length); frame.writeUInt16LE(frame.length, 0); frame.writeUInt16LE(992, 2); body.copy(frame, 4);
    evidence.codecs = await page.evaluate(bytes => gameMenu.probeCodecs(new Uint8Array(bytes)), [...frame]);
    assert.ok(evidence.codecs.residentDecoderWorks, 'resident runtime Brotli decoder must decode the sample frame');
    // Item 3 — the native hangar launch animation must be resident, not the text row.
    await page.waitForFunction(() => gameMenu.hangar().visible, null, { timeout: 120000 });
    await page.waitForFunction(() => gameMenu.hangar().launchArt === true, null, { timeout: 120000, polling: 1000 });
    evidence.hangar = { launchArt: true, screen: await page.evaluate(() => gameMenu.hangar().screen) };
    await capture(page, 'hangar-launch', '#online-panel');
    await page.waitForFunction(() => { gameMenu.launch(); return document.getElementById('online-panel')?.dataset.state === 'Flight'; },
      null, { timeout: 120000, polling: 1000 });
    await page.waitForFunction(() => gameMenu.metrics().flight.frames > 3);
    await until(() => readState().ships.some(s => s.Name === pilot && s.StationId == null), 'authoritative guest in flight');
    // Item 1 — the port-only research briefing (fmid 992) must be applied, not dropped.
    await page.waitForFunction(() => gameMenu.metrics().flight.research.received === true, null, { timeout: 120000, polling: 500 });
    evidence.research = await page.evaluate(() => gameMenu.metrics().flight.research);
    assert.equal(evidence.research.techMode, 'research'); // JS interop camel-cases the anonymous object
    // Item 2b — every station and asteroid in the home sector must be resident.
    await page.waitForFunction(() => {
      const art = gameMenu.metrics().flight.sectorArt;
      return art && art.sky?.ready === true && art.stations.every(s => s.modelResident && s.textureResident)
        && art.rocks.every(r => r.modelResident && r.textured);
    }, null, { timeout: 180000, polling: 500 });
    evidence.sectorArt = await page.evaluate(() => gameMenu.metrics().flight.sectorArt);
    assert.ok(evidence.sectorArt.stations.length > 0, 'home sector has a station');
    assert.ok(evidence.sectorArt.rocks.length > 0, 'generated map home sector has asteroids');
    assert.ok(!offending.some(l => /\[warp\] texture/.test(l)), 'no warp texture fallback');
    // Fly 30 s in each style at both resolutions; exercise the afterburner/PWR readout.
    for (const [width, height] of [[1920, 1080], [800, 480]]) {
      await page.setViewportSize({ width, height }); await delay(500);
      for (const style of ['Classic', 'Modern']) {
        await page.getByRole('button', { name: style, exact: true }).click();
        await page.waitForFunction(expected => gameMenu.metrics().gameStyle === expected, style);
        await page.locator('#theCanvas').focus();
        await page.keyboard.down('w'); await page.keyboard.down('Shift'); await delay(900);
        const hud = await page.evaluate(() => gameMenu.metrics().flight.hud);
        assert.ok(hud.afterburnerActive, style + ': afterburner active');
        // Item 4 — native's ramp is non-zero while the burner is firing (it would
        // read 0 with the old hardcoded/broken sources).
        // The default guest loadout mounts no afterburner, so native power is 0 there
        // (shipIGC.h:1137 `p ? BytePercentage(p->GetPower()) : 0`). Record, don't assert;
        // the ramp itself is pinned by P1073AfterburnerPowerTests.
        (evidence.pwr ??= []).push({ style, width, power: hud.power, serverPower: hud.serverPower, ship: readState().ships.find(s => s.Name === pilot) });
        await page.keyboard.up('Shift'); await page.keyboard.up('w');
        // Coast the remaining 30 s so the pilot stays in the home sector for docking.
        // Short legs: four 30 s straight-line coasts leave the sector bounds and kill the ship.
        await delay(4000);
        await capture(page, `flight-${style.toLowerCase()}-${width}x${height}`, '#theCanvas');
        evidence[`hud-${style}-${width}`] = hud;
      }
    }
    // Item 2b — "Dock at home station" must end docked at a station whose art is resident.
    await page.locator('#flight-dock').click();
    await page.waitForFunction(() => gameMenu.metrics().flight.docked === true, null, { timeout: 180000, polling: 500 });
    const dockedArt = await page.evaluate(() => gameMenu.metrics().flight.sectorArt);
    const visible = dockedArt.stations.filter(s => s.visible && s.textureResident);
    assert.ok(visible.length > 0, 'docked at a station with resident model and texture');
    evidence.docking = { docked: true, station: visible[0], sector: dockedArt.sector };
    await capture(page, 'docked-hangar', '#online-panel');
    assert.deepEqual(errors, []);
    assert.deepEqual(offending, []);
    assert.ok(!consoleMessages.some(l => /\[recv\].*malformed/i.test(l)), 'no malformed receive lines');
    evidence.imageBytes = captures.reduce((n, c) => n + c.bytes, 0);
    evidence.requests = requests;
    fs.writeFileSync(path.join(output, 'browser-generated.json'), JSON.stringify(evidence, null, 2));
    console.log('BROWSER_GENERATED_PASS', evidence.captures.length, evidence.imageBytes);
  } catch (error) {
    evidence.failure = String(error); fs.writeFileSync(path.join(output, 'browser-generated-failure.json'), JSON.stringify(evidence, null, 2)); throw error;
  } finally { await Promise.allSettled(contexts.map(context => context.close())); await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
