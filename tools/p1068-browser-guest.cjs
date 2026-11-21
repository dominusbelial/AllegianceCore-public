// P1068: real lobby -> Play now -> disclosure -> guest LOGONREQ -> launch -> flight.
// No WebAuthn/virtual authenticator, account cookie injection or fabricated server listing.
// Args: NPM_DIRECTORY HOST_STATE EVIDENCE_DIRECTORY GENERATION_DIRECTORY
// Optional CHROMIUM_PATH selects an already-installed executable. No package installs.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules'), stateDir = path.resolve(process.argv[3]);
const output = path.resolve(process.argv[4]), generation = path.resolve(process.argv[5]);
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const origin = 'https://localhost:8443', delay = ms => new Promise(r => setTimeout(r, ms));
const readState = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
const password = fs.readFileSync(path.join(stateDir, 'game-password'), 'utf8').trim();
const version = fs.readFileSync(path.join(stateDir, 'client-version'), 'utf8').trim();
assert.equal(JSON.parse(fs.readFileSync(path.join(generation, 'client-version.json'), 'utf8')).version, version);
async function until(predicate, label, timeout = 60000) {
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
  const browser = await playwright.launch({ executablePath, headless: true, dumpio: process.env.P1068_BROWSER_LOG === "1",
    args: [...args, '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost', '--ignore-certificate-errors'] });
  const errors = [], requests = [], captures = [], contexts = [], consoleMessages = [];
  const evidence = { browser: browser.version(), captures, errors, consoleMessages, guests: [], refusals: {}, visualDescriptions: 'for Claude; images not viewed in this lane' };
  async function fresh() {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, ignoreHTTPSErrors: true }); contexts.push(context);
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      assert.ok(['localhost', '127.0.0.1'].includes(url.hostname), 'loopback requests only');
      requests.push({ method: route.request().method(), path: url.pathname }); await route.continue();
    });
    const page = await context.newPage(); page.setDefaultTimeout(120000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => consoleMessages.push(message.type() + ': ' + message.text())); return page;
  }
  async function capture(page, name, selector) {
    const file = name + '.jpg', target = selector ? page.locator(selector) : page;
    await target.screenshot({ path: path.join(output, file), type: 'jpeg', quality: 80 });
    captures.push({ file, bytes: fs.statSync(path.join(output, file)).size });
  }
  async function enterGuest(page, entry) {
    const opened = Date.now(); await page.goto(origin + entry);
    await page.locator('#play-now').waitFor({ state: 'visible' });
    assert.equal(new URL(await page.locator('#play-now').getAttribute('href'), origin).pathname, '/play/menu.html');
    await page.locator('#play-now').click();
    await page.locator('#guest-disclosure').waitFor({ state: 'visible' });
    const disclosure = await page.locator('#guest-disclosure').innerText();
    for (const claim of ['guest', 'only in this browser', 'Clearing site data', 'not reserved', 'career or ladder', 'squad membership', 'Signing in is optional'])
      assert.ok(disclosure.includes(claim), 'disclosure: ' + claim);
    assert.ok(await page.locator('#online-identity a').isVisible());
    const callsign = await page.locator('#guest-callsign').inputValue();
    assert.match(callsign, /^Pilot-[0-9A-HJKMNP-TV-Z]{10}$/);
    assert.equal(await page.evaluate(() => localStorage.getItem('allegiance.guest.callsign')), callsign);
    evidence.guests.push({ callsign, entry }); return { callsign, opened };
  }
  async function join(page) {
    if (await page.locator('#join-password').count()) await type(page, '#join-password', password);
    await page.locator('.online-join:enabled').first().click();
  }
  try {
    const first = await fresh(), pilot = await enterGuest(first, '/');
    await capture(first, 'guest-disclosure', '#guest-disclosure');
    await join(first); await first.locator('#online-panel[data-state="Sides"]').waitFor();
    await first.locator('.online-side').first().click(); await first.locator('#online-panel[data-state="Hangar"]').waitFor();
    assert.ok(await first.locator('#online-identity').isVisible(), 'guest label in hangar');
    await first.locator('#online-launch').click(); await first.locator('#online-panel[data-state="Flight"]').waitFor();
    await first.waitForFunction(() => gameMenu.metrics().flight.frames > 3);
    evidence.pageOpenToFlyingMs = Date.now() - pilot.opened;
    await until(() => readState().ships.some(s => s.Name === pilot.callsign && s.StationId == null), 'authoritative guest in flight');
    const before = readState().ships.find(s => s.Name === pilot.callsign).position;
    await first.locator('#theCanvas').focus(); await first.keyboard.down('w'); await delay(900);
    await first.keyboard.down('ArrowRight'); await delay(300); await first.keyboard.up('ArrowRight'); await first.keyboard.up('w');
    await until(() => readState().ships.some(s => s.Name === pilot.callsign && s.position !== before), 'guest flew with manual inputs');
    evidence.manualFlight = { before, after: readState().ships.find(s => s.Name === pilot.callsign).position };
    for (const style of ['Classic', 'Modern']) {
      await first.getByRole('button', { name: style, exact: true }).click();
      await first.waitForFunction(expected => gameMenu.metrics().gameStyle === expected, style);
      assert.ok(await first.locator('#online-identity').isVisible(), style + ': persistent flight label');
      assert.ok((await first.locator('#online-identity').innerText()).includes('Guest · ' + pilot.callsign));
      await first.locator('#flight-fullscreen').click();
      await first.waitForFunction(() => document.fullscreenElement === document.documentElement);
      assert.ok(await first.locator('#online-identity').isVisible(), style + ': guest label in fullscreen');
      assert.ok(await first.locator('#online-identity a').isVisible(), style + ': sign-in link in fullscreen');
      await capture(first, 'guest-flight-' + style.toLowerCase());
      await first.locator('#flight-fullscreen').click();
      await first.waitForFunction(() => document.fullscreenElement === null);
    }
    // A fresh context proves independent storage; the identity page also offers Play now.
    const second = await fresh(), other = await enterGuest(second, '/identity.html');
    assert.notEqual(other.callsign, pilot.callsign, 'fresh context gets a different random identity');
    async function attempt(name, expected) {
      await type(second, '#guest-callsign', name); await second.locator('#guest-save').click();
      await until(async () => (await second.locator('#online-identity').innerText()).includes(name), 'edited callsign applied');
      await join(second); await second.locator('#guest-try-another').waitFor({ state: 'visible' });
      const message = await second.locator('[role="alert"]').innerText(); assert.ok(message.includes(expected), message); return message;
    }
    evidence.refusals.duplicate = await attempt(pilot.callsign.toLowerCase(), 'already in use');
    evidence.refusals.reserved = await attempt('proofoperator', 'registered');
    // The conflict button must reroll AND retry in one click, preserving the password.
    await second.locator('#guest-try-another').click(); await second.locator('#online-panel[data-state="Sides"]').waitFor();
    const rerolled = await second.evaluate(() => localStorage.getItem('allegiance.guest.callsign'));
    assert.match(rerolled, /^Pilot-/); assert.notEqual(rerolled, other.callsign);
    evidence.conflictRetry = { callsign: rerolled, state: 'Sides' };
    await second.locator('#online-close').click();
    await first.locator('#online-close').click();
    await until(() => !readState().players.includes(pilot.callsign), 'explicit guest logoff');
    // Same browser returns to its persisted guest name; Forget removes it and resets choice.
    await first.goto(origin + '/play/menu.html?online=1');
    await first.locator('#guest-disclosure').waitFor(); assert.equal(await first.locator('#guest-callsign').inputValue(), pilot.callsign);
    await first.locator('#guest-forget').click(); await first.locator('#online-play-now').waitFor();
    assert.equal(await first.evaluate(() => localStorage.getItem('allegiance.guest.callsign')), null);
    await first.goto(origin + '/play/menu.html');
    await first.locator('#online-open').click(); await first.locator('#online-play-now').waitFor({ state: 'visible' });
    assert.ok(await first.locator('#online-signin').isVisible(), 'ordinary menu offers both choices');
    assert.equal(await first.evaluate(() => localStorage.getItem('allegiance.guest.callsign')), null, 'ordinary menu does not generate identity before guest choice');
    await first.locator('#online-play-now').click(); await first.locator('#guest-disclosure').waitFor();
    evidence.ordinaryMenuChoice = true;
    // Disabled builds never offer Play now at any of the three entry points.
    await first.context().route('**/api/browser', route => route.fulfill({ json: { enabled: false } }));
    for (const entry of ['/', '/identity.html']) {
      await Promise.all([first.waitForResponse(response => new URL(response.url()).pathname === '/api/browser'), first.goto(origin + entry)]);
      assert.ok(await first.locator('#play-now').isHidden(), entry + ': disabled offer hidden');
    }
    await first.goto(origin + '/play/menu.html?online=1');
    await first.locator('#online-signin').waitFor({ state: 'visible' });
    await first.waitForFunction(() => document.querySelector('#online-refresh')?.disabled === false);
    assert.equal(await first.locator('#online-play-now').count(), 0, 'disabled offer hidden in online panel');
    evidence.disabledOffers = ['/', '/identity.html', '/play/menu.html?online=1'];
    assert.ok(!requests.some(r => r.path === '/api/identity/join-token'), 'guest never requests a ticket');
    assert.ok(!requests.some(r => r.path.includes('/passkeys/')), 'no identity registration or authentication');
    assert.deepEqual(errors, []);
    evidence.imageBytes = captures.reduce((n, c) => n + c.bytes, 0); assert.ok(evidence.imageBytes <= 2 * 1024 * 1024, 'JPEG q80 set <=2 MiB');
    evidence.requests = requests; fs.writeFileSync(path.join(output, 'browser-guest.json'), JSON.stringify(evidence, null, 2));
    console.log('BROWSER_GUEST_PASS', evidence.pageOpenToFlyingMs, evidence.imageBytes);
  } catch (error) {
    evidence.failure = String(error); fs.writeFileSync(path.join(output, 'browser-guest-failure.json'), JSON.stringify(evidence, null, 2)); throw error;
  } finally { await Promise.allSettled(contexts.map(context => context.close())); await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
