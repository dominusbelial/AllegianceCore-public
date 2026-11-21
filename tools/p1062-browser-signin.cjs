// P1062 — Chromium proof of the two-way browser offer on the production-shaped
// loopback origin (lobby at /play + /api + /identity.html, game WSS at /game):
//
//   A. lobby home page -> player sign-in -> identity page (signed in) ->
//      "Play in browser" -> the client opens the online panel by itself ->
//      join -> sides -> launch -> flight.
//   B. reverse: start at the client (/play/menu.html) signed out -> Play online ->
//      Sign in -> same-tab identity page -> passkey -> back at the client already
//      signed in (no manual refresh) -> join -> flight.
//   C. lobby home page server list -> per-game "Play in browser" -> the client
//      opens with that listing entry preselected -> join -> flight.
//
// Args: npm-directory STATE_DIR EVIDENCE_DIR GENERATION_DIR EXPECTED_GAME_ID
// Every request stays on localhost/127.0.0.1; the harness asserts it.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules');
const stateDir = path.resolve(process.argv[3]);
const output = path.resolve(process.argv[4]);
const generation = path.resolve(process.argv[5]);
const expectedServerId = process.argv[6] || '';
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const origin = 'https://localhost:8443';
const home = origin + '/';
const identity = origin + '/identity.html';
const menu = origin + '/play/menu.html';
const delay = ms => new Promise(r => setTimeout(r, ms));
const readState = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
const gamePassword = fs.readFileSync(path.join(stateDir, 'game-password'), 'utf8').trim();
const minimumVersion = fs.readFileSync(path.join(stateDir, 'client-version'), 'utf8').trim();
const publishedVersion = JSON.parse(fs.readFileSync(path.join(generation, 'client-version.json'), 'utf8')).version;
assert.equal(publishedVersion, minimumVersion, 'the generation reports the version the proof server enforces');
async function serverWait(predicate, label, timeout = 20000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { const state = readState(); if (predicate(state)) return state; await delay(50); }
  throw Error('Server proof timed out: ' + label);
}
const captured = {};
async function screenshot(page, name) {
  await page.screenshot({ path: path.join(output, name + '.png') });
  // The visible text of the same frame, so each cited image can be described
  // from the page itself as well as looked at.
  captured[name] = await page.locator('body').innerText().catch(() => '');
}

async function main() {
  fs.mkdirSync(output, { recursive: true });
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const launch = async () => playwright.launch({
    executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content'].includes(a)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost', '--ignore-certificate-errors'],
  });
  let browser = await launch();
  const page = await (await browser.newContext({ viewport: { width: 1280, height: 800 }, ignoreHTTPSErrors: true })).newPage();
  page.setDefaultTimeout(60000);
  const errors = [], logs = [], urls = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { logs.push(m.type() + ': ' + m.text()); if (m.type() === 'error') console.error(m.text()); });
  page.on('request', r => { const u = new URL(r.url()); assert.ok(['localhost', '127.0.0.1'].includes(u.hostname), 'loopback only: ' + u.hostname); urls.push(r.url()); });
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
  const callsign = 'Browser1062' + Date.now().toString().slice(-6);
  const proof = { callsign, directions: {} };
  const metrics = () => page.evaluate(() => gameMenu.metrics());
  const enterPassword = () => page.locator('#join-password').waitFor().then(() =>
    page.locator('#join-password').evaluate((input, text) => { input.value = text; input.dispatchEvent(new Event('input', { bubbles: true })); }, gamePassword));
  // One join -> sides -> launch -> flight chain, asserted against the server state.
  async function joinAndFly(label, expectPreselected = null) {
    const state = await page.locator('#online-panel').getAttribute('data-state');
    assert.equal(state, 'Servers', label + ': the server list is open');
    if (expectPreselected) assert.equal((await metrics()).entry.preselected, expectPreselected, label + ': preselected listing id');
    await page.getByRole('button', { name: 'Classic', exact: true }).click();
    await page.waitForFunction(() => gameMenu.metrics().gameStyle === 'Classic');
    if (await page.locator('#join-password').count()) await enterPassword();
    await page.locator('.online-join').first().click();
    await page.locator('#online-panel[data-state="Sides"]').waitFor({ timeout: 90000 });
    await page.locator('.online-side').first().click();
    await page.locator('#online-panel[data-state="Hangar"]').waitFor({ timeout: 60000 });
    // P1070 — the HTML launch button is a dev-only control now (no ?dev=1 here), so
    // launch through the game window's own hangar Launch verb.
    assert.equal(await page.locator('#online-launch').count(), 0, label + ': the hangar has no HTML launch control for a player');
    await page.evaluate(() => gameMenu.launch());
    await page.locator('#online-panel[data-state="Flight"]').waitFor({ timeout: 120000 });
    await page.waitForFunction(() => gameMenu.metrics().flight.frames > 3, null, { timeout: 60000 });
    const shipId = (await metrics()).online.shipId;
    const flight = await serverWait(s => s.players.includes(callsign) && s.ships.some(x => x.Name === callsign && x.StationId === null),
      label + ': the server has the browser ship in flight');
    await screenshot(page, 'flight-' + label);
    await page.locator('#online-close').click();
    await page.locator('#online-panel').waitFor({ state: 'detached' });
    await serverWait(s => !s.players.includes(callsign) && !s.ships.some(x => x.Id === shipId), label + ': clean logoff');
    return { shipId, ships: flight.ships.filter(x => x.Name === callsign).map(x => ({ id: x.Id, stationId: x.StationId, sectorId: x.sectorId })) };
  }

  try {
    // A — the lobby home page first.
    await page.goto(home);
    const browserLink = page.locator('a', { hasText: 'Play in browser' }).first();
    await browserLink.waitFor();
    const row = page.locator('#servers tbody tr').filter({ has: browserLink });
    assert.equal(await row.count(), 1, 'exactly one listed game offers the browser');
    const desktopHref = await row.locator('a[href^="allegiance://join"]').first().getAttribute('href');
    const browserHref = await browserLink.getAttribute('href');
    const desktopId = decodeURIComponent(desktopHref.split('&id=')[1].split('&')[0]);
    assert.equal(desktopId, expectedServerId, 'the browser link names the same listing entry as the desktop Join link');
    assert.match(browserHref, /^\/play\/menu\.html\?online=1&server=/);
    assert.equal(decodeURIComponent(browserHref.split('&server=')[1]), desktopId);
    await screenshot(page, 'server-list');
    await page.getByRole('link', { name: /Player sign-in/ }).click();
    await page.locator('#login').waitFor();
    assert.equal(await page.locator('#play-browser').isHidden(), true, 'the browser action is hidden while signed out');
    await screenshot(page, 'identity-signed-out');
    await page.locator('#callsign').evaluate((input, value) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); }, callsign);
    await page.locator('#register').click();
    await page.locator('#account').waitFor();
    assert.equal(await page.locator('#play-browser').isVisible(), true, 'the browser action appears once signed in');
    const identityHref = await page.locator('#play-browser').getAttribute('href');
    assert.equal(identityHref, '/play/menu.html?online=1');
    await screenshot(page, 'identity-signed-in');
    await page.locator('#play-browser').click();
    await page.waitForURL(u => u.pathname === '/play/menu.html' && u.search.includes('online=1'));
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
    // No #online-open click: the client picks the signed-in identity up itself.
    await page.locator('#online-panel[data-state="Servers"]').waitFor({ timeout: 120000 });
    assert.match(await page.locator('#online-panel').innerText(), new RegExp('Signed in as ' + callsign));
    proof.directions.identityOffer = { autoOnline: (await metrics()).entry.autoOnline, state: (await metrics()).online.state };
    proof.directions.identityOffer.flight = await joinAndFly('from-identity');

    // B — the reverse path: the client starts signed out and comes back signed in.
    await page.goto(identity);
    await page.locator('#account').waitFor();
    await page.locator('#signout').click();
    await page.locator('#login').waitFor();
    await page.goto(menu);
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
    await page.locator('#online-open').click();
    await page.locator('#online-panel[data-state="Login"]').waitFor();
    const signInHref = await page.locator('#online-signin').getAttribute('href');
    assert.match(signInHref, /^\/identity\.html\?return=/);
    assert.match(decodeURIComponent(signInHref), /^\/identity\.html\?return=\/play\/menu\.html\?online=1$/);
    await page.locator('#online-signin').click();
    await page.locator('#login').waitFor();
    assert.ok(page.url().startsWith(identity + '?return='), 'same-tab sign-in page');
    await page.locator('#signin').click();
    await page.waitForURL(u => u.pathname === '/play/menu.html' && u.search.includes('online=1'), { timeout: 60000 });
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
    await page.locator('#online-panel[data-state="Servers"]').waitFor({ timeout: 120000 });
    assert.match(await page.locator('#online-panel').innerText(), new RegExp('Signed in as ' + callsign));
    proof.directions.roundTrip = { autoOnline: (await metrics()).entry.autoOnline, noManualRefresh: true };
    proof.directions.roundTrip.flight = await joinAndFly('round-trip');

    // C — the lobby server list's per-game link preselects that listing entry.
    await page.goto(home);
    await page.locator('a', { hasText: 'Play in browser' }).first().waitFor();
    await page.locator('a', { hasText: 'Play in browser' }).first().click();
    await page.waitForURL(u => u.pathname === '/play/menu.html' && u.search.includes('server=' + expectedServerId));
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 120000 });
    const preselected = page.locator('#online-panel[data-state="Servers"] article[data-preselected="true"]');
    await preselected.waitFor({ timeout: 120000 });
    assert.match(await preselected.innerText(), /Opened from the server list/);
    assert.equal((await metrics()).entry.preselected, expectedServerId);
    await screenshot(page, 'client-preselected');
    proof.directions.serverList = { preselected: expectedServerId };
    proof.directions.serverList.flight = await joinAndFly('from-server-list', expectedServerId);

    assert.deepEqual(errors, []);
    // The signed-out identity probe answers 401 by design; any other failed load is not.
    const failedLoads = logs.filter(line => /^error:/.test(line) && /Failed to load resource/.test(line) && !/ 401 /.test(line));
    assert.deepEqual(failedLoads, [], 'no unexpected failed asset load');
    fs.writeFileSync(path.join(output, 'signin-proof.json'), JSON.stringify({ passed: true, browser: browser.version(), callsign, directions: proof.directions, visibleText: captured }, null, 2));
    const pngs = fs.readdirSync(output).filter(n => n.endsWith('.png'));
    const bytes = pngs.reduce((n, f) => n + fs.statSync(path.join(output, f)).size, 0);
    assert.ok(bytes <= 2 * 1024 * 1024, 'PNG evidence <= 2 MiB');
    console.log(JSON.stringify({ passed: true, directions: Object.keys(proof.directions), pngs, pngBytes: bytes }));
  } catch (error) {
    if (!page.isClosed()) {
      fs.writeFileSync(path.join(output, 'failure-console.txt'), logs.join('\n'));
      fs.writeFileSync(path.join(output, 'failure.txt'), String(error) + '\n' + await page.locator('body').innerText().catch(() => ''));
      await page.screenshot({ path: path.join(output, 'failure.png') }).catch(() => {});
    }
    throw error;
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1 });
