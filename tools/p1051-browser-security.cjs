// Optional Slice-4 admission hardening. Real browser UI, signer and validator;
// only the negative ticket response is substituted. No production policy changes.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules');
const stateDir = path.resolve(process.argv[3]), output = path.resolve(process.argv[4]);
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const origin = 'http://localhost:8048';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, label, timeout = 15000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (predicate()) return; await delay(50); }
  throw Error('Timed out: ' + label);
}
async function main() {
  fs.mkdirSync(output, { recursive: true });
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const browser = await playwright.launch({ executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(arg => !['--disable-web-security', '--allow-running-insecure-content'].includes(arg)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost'] });
  const secrets = [], logs = [], urls = [], cases = [], ticketReads = [], ticketReadErrors = [];
  let page, freshTicket;
  try {
    const context = await browser.newContext({ viewport: { width: 800, height: 480 } });
    page = await context.newPage(); page.setDefaultTimeout(30000);
    page.on('console', message => logs.push(message.text()));
    page.on('request', request => {
      const url = new URL(request.url()); assert.ok(['localhost', '127.0.0.1'].includes(url.hostname)); urls.push(request.url());
    });
    page.on('response', response => {
      if (new URL(response.url()).pathname === '/api/identity/join-token' && response.ok()) {
        ticketReads.push(response.json().then(({ token }) => { freshTicket = token; secrets.push(token); }).catch(error => ticketReadErrors.push(String(error))));
      }
    });
    const flushTicketReads = async () => { await Promise.all(ticketReads); assert.deepEqual(ticketReadErrors, []); };
    const cdp = await context.newCDPSession(page); await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true,
      hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
    const name = 'Hardening1051' + Date.now().toString().slice(-6);
    const state = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
    const clean = () => until(() => !state().players.includes(name) && !state().ships.some(ship => ship.Name === name), 'no zombie');
    await page.goto(origin + '/identity.html'); await page.locator('#callsign').fill(name); await page.locator('#register').click();
    await page.locator('#account').waitFor({ state: 'visible' });
    const account = await page.evaluate(() => fetch('/api/identity/me').then(response => response.json()));
    secrets.push(...(await context.cookies()).map(cookie => cookie.value));
    await page.goto(origin + '/play/preview/menu.html');
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 90000 });
    const open = async () => { await page.locator('#online-open').click(); await page.locator('#online-panel[data-state="Servers"]').waitFor(); };
    const close = async () => { await flushTicketReads(); await page.locator('#online-close').click(); await page.locator('#online-panel').waitFor({ state: 'detached' }); await clean(); };
    const refused = async label => {
      await page.locator('.online-join').first().click(); await page.locator('#online-panel [role="alert"]').waitFor({timeout:240000});
      const message = await page.locator('#online-panel [role="alert"]').innerText(); assert.match(message, /invalid|expired|token|refused/i);
      await clean(); cases.push({ name: label, refused: true, clearMessage: message, noZombie: true });
    };
    for (const [routeUrl, expected] of [['ws://10.1.2.3/game', /no web route/i], ['wss://game.example.com/game', /Public joining is not available in the browser\./]]) {
      let attempts = 0;
      const seen = () => attempts++;
      page.on('websocket', seen);
      await page.route('**/api/servers', async route => {
        const response = await route.fetch(), body = await response.json();
        for (const server of body) server.webSocketUrl = routeUrl;
        await route.fulfill({ response, json: body });
      });
      await open();
      const message = await page.locator('.online-server').first().innerText();
      assert.match(message, expected); assert.equal(await page.locator('.online-join').count(), 0);
      await page.waitForTimeout(300); assert.equal(attempts, 0);
      cases.push({ name: routeUrl.startsWith('ws:') ? 'LAN plaintext listing has no web route' : 'public WSS refused by loopback join gate', passed: true, message, webSocketAttempts: attempts });
      await close(); await page.unroute('**/api/servers'); page.off('websocket', seen);
    }
    await page.route('**/api/servers', route => route.fulfill({ status: 503, body: 'temporary list failure' }));
    await page.locator('#online-open').click(); await page.locator('#online-panel [role="alert"]').waitFor({timeout:240000});
    await close(); await page.unroute('**/api/servers'); await open();
    cases.push({ name: 'list failure returns and retries', passed: true });
    if (process.argv[5] === '--deferred-cancel') {
      const cancelDelayed = async (pattern, match, label) => {
        let release; const gate = new Promise(resolve => { release = resolve; });
        let attempts = 0; const seen = () => attempts++;
        const ticketsBefore = ticketReads.length;
        page.on('websocket', seen);
        await page.route(pattern, async route => { await gate; await route.continue(); });
        try {
          const requested = page.waitForRequest(request => match.test(new URL(request.url()).pathname), {timeout:90000});
          await page.locator('.online-join').first().click(); await requested;
          assert.ok((await page.evaluate(() => gameMenu.metrics())).online.pendingJoins > 0);
          await close(); release();
          await page.waitForFunction(() => gameMenu.metrics().online.pendingJoins === 0, null, {timeout:90000});
          await flushTicketReads(); await clean();
          assert.equal(attempts, 0); assert.equal(ticketReads.length, ticketsBefore);
          cases.push({name:label,passed:true,webSocketAttempts:attempts,ticketsMinted:0,noZombie:true});
        } finally { release(); await page.unroute(pattern); page.off('websocket', seen); }
        await open();
      };
      await cancelDelayed('**/join-manifest.json', /\/join-manifest\.json$/, 'cancel during deferred join-manifest read');
      await cancelDelayed('**/brotli*.js', /\/brotli(?:\.[0-9a-f]+)?\.js$/, 'cancel during deferred decoder preparation');
    }
    for (const kind of ['wrong-audience', 'expired']) {
      const responsePath = path.join(stateDir, 'ticket-response.json'); fs.rmSync(responsePath, { force: true });
      const requestPath = path.join(stateDir, 'ticket-request.json');
      fs.writeFileSync(requestPath + '.tmp', JSON.stringify({ accountId: account.id, kind })); fs.renameSync(requestPath + '.tmp', requestPath);
      await until(() => fs.existsSync(responsePath), 'production signer fixture');
      const { token } = JSON.parse(fs.readFileSync(responsePath)); fs.unlinkSync(responsePath); secrets.push(token);
      const claims = JSON.parse(Buffer.from(token.split('.')[1], 'base64url'));
      if (kind === 'expired') assert.ok(claims.exp < Date.now() / 1000 - 60);
      else assert.equal(claims.aud, 'p1051-other-server');
      await page.route('**/api/identity/join-token', route => route.fulfill({ json: { token } }));
      await refused(kind); await close(); await page.unroute('**/api/identity/join-token'); await open();
    }
    await page.route('**/api/identity/join-token', route => route.fulfill({ status: 503, body: 'temporary ticket failure' }));
    await page.locator('.online-join').first().click(); await page.locator('#join-retry').waitFor();
    await clean(); await page.unroute('**/api/identity/join-token'); await page.locator('#join-retry').click();
    await page.locator('#online-panel[data-state="Sides"]').waitFor(); await flushTicketReads(); const replay = freshTicket;
    assert.ok(replay); assert.ok(JSON.parse(Buffer.from(replay.split('.')[1], 'base64url')).exp > Date.now() / 1000);
    cases.push({ name: 'ticket failure retries through real fresh-ticket join', passed: true });
    await close(); await open();
    await page.route('**/api/identity/join-token', route => route.fulfill({ json: { token: replay } }));
    await refused('replayed unexpired ticket'); await close(); await page.unroute('**/api/identity/join-token');
    // Inspect actual Cache API bytes/URLs and all of our IndexedDB stores.
    const inspection = await page.evaluate(async forbidden => {
      const inspect = text => { for (const secret of forbidden) if (secret && text.includes(secret)) throw Error('Secret in asset storage'); };
      let files = 0, bytes = 0;
      for (const name of await caches.keys()) {
        const cache = await caches.open(name);
        for (const request of await cache.keys()) {
          inspect(request.url); const data = new Uint8Array(await (await cache.match(request)).arrayBuffer());
          inspect(new TextDecoder().decode(data)); files++; bytes += data.length;
        }
      }
      const db = await new Promise((resolve, reject) => { const request = indexedDB.open('allegiance-menu-v2'); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
      for (const name of db.objectStoreNames) {
        const values = await new Promise((resolve, reject) => { const request = db.transaction(name).objectStore(name).getAll(); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
        inspect(JSON.stringify(values));
      }
      db.close(); return { files, bytes, noSecrets: true };
    }, secrets);
    const shipIdsBeforeStale = state().ships.map(ship => ship.Id).sort((a, b) => a - b);
    await page.route('**/api/servers', async route => {
      const response = await route.fetch(), body = await response.json();
      for (const server of body) for (const mission of server.missions || []) mission.cookie = 2147483646;
      await route.fulfill({ response, json: body });
    });
    await open(); await refused('wrong/stale mission cookie'); await close();
    assert.deepEqual(state().ships.map(ship => ship.Id).sort((a, b) => a - b), shipIdsBeforeStale);
    cases.at(-1).serverShipRegistryUnchanged = true;
    await page.unroute('**/api/servers');
    await open(); await page.locator('.online-join').first().click(); await page.locator('#online-panel[data-state="Sides"]').waitFor();
    await flushTicketReads(); await page.goBack(); await page.locator('#account').waitFor({ state: 'visible' }); await clean();
    cases.push({ name: 'browser back-navigation retires the join', passed: true });
    for (const secret of secrets) { assert.ok(!logs.join('\n').includes(secret)); assert.ok(!urls.join('\n').includes(secret)); }
    const result = { passed: true, browserVersion: browser.version(), cases, inspection, noSecretsInLogsOrUrls: true };
    fs.writeFileSync(path.join(output, 'security-proof.json'), JSON.stringify(result, null, 2)); console.log(JSON.stringify(result));
  } catch (error) {
    if (page && !page.isClosed()) fs.writeFileSync(path.join(output, 'security-failure.txt'), String(error) + '\n' + await page.locator('body').innerText());
    throw error;
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
