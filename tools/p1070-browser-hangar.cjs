// P1070 — the browser hangar and the full ship loadout, driven in the game window.
// For a workstation with the already-installed P1051 Chromium modules. No npm install.
//
// Args: NPM_DIRECTORY HOST_STATE OUTPUT GENERATION.
// Run once per fresh `p1051-browser-host --bots 1`. Every browser process is closed here;
// the calling shell owns the proof-host PID and its stop file.
//
// What it proves, in one run per HUD style and window size:
//   1. docking shows the native hangar INSIDE the canvas (gameMenu.hangar()),
//      with the server's own offer list and no browser-side hull list;
//   2. the hangar's Loadout pane opens the loadout, and a different hull plus a part
//      can be picked by clicking the drawn rows (gameMenu.hangar().loadout.elements);
//   3. Buy sends the desktop's BUY_LOADOUT and the server's ACK lands (money/state move);
//   4. Launch from the game window leaves the hangar and the pilot flies;
//   5. docking again brings the hangar back.
// A second context loads the page WITHOUT ?dev=1 and proves the HTML launch controls are
// absent from the Hangar state (`#online-launch`, `#launch-status`, `#launch-reason`), that
// the canvas hangar is still there, and that `gameMenu.launch()` launches through it.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const modules = path.resolve(process.argv[2], 'node_modules'), stateDir = path.resolve(process.argv[3]);
const out = path.resolve(process.argv[4]), generation = path.resolve(process.argv[5]);
assert.ok(fs.existsSync(generation), 'the published browser generation exists');
const { chromium: playwright } = require(path.join(modules, 'playwright'));
const origin = 'https://localhost:8443';
const menu = query => origin + '/play/menu.html' + query;
const state = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json'))), delay = ms => new Promise(r => setTimeout(r, ms));
async function until(test, label, timeout = 180000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await test()) return; await delay(100); }
  throw Error('Timed out: ' + label);
}
async function type(page, selector, value) {
  await page.locator(selector).evaluate((input, text) => {
    input.value = text;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }, String(value));
}
const hangar = page => page.evaluate(() => gameMenu.hangar());

function centre(bounds) { return { x: Math.round(bounds.x + bounds.width / 2), y: Math.round(bounds.y + bounds.height / 2) }; }

async function run() {
  fs.mkdirSync(out, { recursive: true });
  // The host writes state.json only after it boots; a missing or mid-swap file is not ready.
  await until(() => { try { return state().battle.ready; } catch (e) { if (e.code === 'ENOENT' || e instanceof SyntaxError) return false; throw e; } },
    'capped bots authenticated and pilot slot reserved', 600000);
  assert.equal(state().battle.size, 1, 'this harness is the brief\'s --bots 1 run');
  const chromium = (await import(require('node:url').pathToFileURL(path.join(modules, '@sparticuz/chromium/build/index.js')).href)).default;
  const browser = await playwright.launch({
    executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content'].includes(a)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost', '--ignore-certificate-errors'],
  });
  const errors = [], captures = [], cases = [], noDev = {};
  const password = fs.readFileSync(path.join(stateDir, 'game-password'), 'utf8').trim();
  try {
    const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, ignoreHTTPSErrors: true });
    const page = await context.newPage();
    page.setDefaultTimeout(120000);
    await context.route('**/*', route => {
      const u = new URL(route.request().url());
      if (!['localhost', '127.0.0.1'].includes(u.hostname)) throw Error('Non-loopback request: ' + u.origin);
      return route.continue();
    });
    page.on('pageerror', error => errors.push(error.message));
    page.pointer = (x, y, down) => page.evaluate(([cx, cy, pressed]) => gameMenu.pointer(cx, cy, pressed, 0), [x, y, down]);
    const clickAt = async (bounds, label) => {
      const point = centre(bounds);
      await page.pointer(point.x, point.y, false); await delay(80);
      await page.pointer(point.x, point.y, true); await delay(250);
      await page.pointer(point.x, point.y, false); await delay(250);
      return label;
    };
    const verb = async name => (await hangar(page)).verbs.find(v => v.action === name)?.bounds;
    const element = async (kind, index) => {
      const h = await hangar(page);
      const list = h.loadout?.elements ?? [];
      return list.find(e => e.kind === kind && (index === undefined || e.index === index));
    };

    // Register, sign in, join, pick a side, and land in the Hangar state.
    const cdp = await context.newCDPSession(page);
    await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
    const callsign = 'Browser1070' + Date.now().toString().slice(-7);
    await page.goto(origin + '/identity.html');
    await type(page, '#callsign', callsign);
    await page.locator('#register').click();
    await page.locator('#account').waitFor({ state: 'visible' });
    await page.goto(menu('?dev=1'));
    await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null);
    await page.locator('#online-open').click();
    await page.locator('#join-password').waitFor();
    await type(page, '#join-password', password);
    await page.locator('.online-join').first().click();
    await page.locator('.online-side').first().click();
    await page.locator('#online-panel[data-state="Hangar"]').waitFor({ timeout: 180000 });
    await until(async () => (await hangar(page)).visible, 'the hangar is drawn in the game window', 60000);

    // ── the hangar in the canvas ────────────────────────────────────────────
    const docked = await hangar(page);
    assert.equal(docked.screen, 'Hangar', 'the docked screen is the native hangar');
    assert.ok(docked.offeredHulls > 0, 'the server offers hulls (' + docked.offeredHulls + ')');
    assert.ok(docked.verbs.some(v => v.action === 'Launch'), 'the launch pane has a drawn rectangle');
    assert.ok(docked.verbs.some(v => v.action === 'Loadout'), 'the loadout pane has a drawn rectangle');
    const startHull = docked.hull, startMoney = docked.money;
    // The dev visit keeps the P1058 launch control exactly as it was.
    assert.equal(await page.locator('#online-launch').count(), 1, '?dev=1 still renders #online-launch');
    assert.equal(await page.evaluate(() => gameMenu.metrics().dev), true, 'the dev switch is on');

    // The loadout pane, clicked where it is drawn.
    await clickAt(await verb('Loadout'), 'loadout pane');
    await until(async () => (await hangar(page)).loadoutOpen, 'the loadout screen is open', 30000);
    const loadoutOpened = await hangar(page);
    assert.ok(loadoutOpened.slots > 0, 'the loadout lists the hull\'s slots (' + loadoutOpened.slots + ')');

    // A different hull, then a part in a slot, then a buy — all by drawn row.
    const hullRows = (await hangar(page)).loadout.elements.filter(e => e.kind === 'Hull');
    const otherHull = hullRows.find(e => e.index !== 0) ?? hullRows[hullRows.length - 1];
    assert.ok(otherHull, 'the hull column has more than one row');
    await clickAt(otherHull.bounds, 'hull row ' + otherHull.index);
    await until(async () => (await hangar(page)).hull !== startHull || (await hangar(page)).hullName !== docked.hullName,
      'the chosen hull changed', 30000);
    const chosenHull = await hangar(page);
    const slot = (await hangar(page)).loadout.elements.find(e => e.kind === 'Slot');
    assert.ok(slot, 'the loadout draws at least one slot');
    await clickAt(slot.bounds, 'slot ' + slot.index);
    await until(async () => (await hangar(page)).loadout.openSlot >= 0, 'the slot\'s part list opened', 30000);
    // The last offer row is a real part (row 0 is native's "-Make Slot Empty-").
    const offers = (await hangar(page)).loadout.elements.filter(e => e.kind === 'Offer');
    const offer = offers[offers.length - 1];
    assert.ok(offer, 'the opened slot offers parts (' + offers.length + ' rows)');
    await clickAt(offer.bounds, 'offer row ' + offer.index);
    await until(async () => (await hangar(page)).parts > chosenHull.parts, 'a part was mounted in the pending loadout', 30000);
    const chosen = await hangar(page);
    // Buy: the request goes out through the shared BUY_LOADOUT path; the server's
    // acknowledgement then moves the wallet and the shop follows it.
    const buy = await element('Buy');
    assert.ok(buy, 'the loadout draws its Buy button');
    await clickAt(buy.bounds, 'Buy');
    await until(async () => {
      const h = await hangar(page);
      return h.money !== startMoney || h.lastAction.startsWith('purchase');
    }, 'the buy reached the server', 60000);
    const bought = await hangar(page);
    cases.push({ phase: 'loadout', startHull, chosenHull: chosen.hull, chosenHullName: chosen.hullName, parts: chosen.parts,
      moneyBefore: startMoney, moneyAfter: bought.money, state: bought.state, lastAction: bought.lastAction,
      offeredHulls: bought.offeredHulls, offeredParts: bought.offeredParts });

    // ── captures: hangar and loadout, Classic/Modern, 1920x1080 and 800x480 ──
    for (const [style, width, height] of [['Classic', 1920, 1080], ['Modern', 1920, 1080], ['Classic', 800, 480], ['Modern', 800, 480]]) {
      await page.setViewportSize({ width, height });
      await page.getByRole('button', { name: style, exact: true }).click();
      await page.waitForFunction(expected => gameMenu.metrics().gameStyle === expected, style);
      await delay(1200);
      // Loadout first, then back to the hangar, so both screens are one state apart.
      if (!(await hangar(page)).loadoutOpen) {
        await clickAt(await verb('Loadout'), style + ' loadout pane');
        await until(async () => (await hangar(page)).loadoutOpen, style + ' loadout open', 30000);
      }
      await delay(1200);
      const loadoutFile = `hangar-loadout-${style.toLowerCase()}-${width}x${height}.jpg`;
      await page.screenshot({ path: path.join(out, loadoutFile), type: 'jpeg', quality: 80 });
      const atLoadout = await hangar(page);
      captures.push({ file: loadoutFile, style, width, height, screen: atLoadout.screen, hull: atLoadout.hull,
        hullName: atLoadout.hullName, parts: atLoadout.parts, money: atLoadout.money, cost: atLoadout.cost,
        slots: atLoadout.slots, offers: atLoadout.offers, previewStreamed: atLoadout.previewStreamed,
        status: atLoadout.status, state: atLoadout.state });
      // Back to the hangar (the drawn Back button).
      const back = await element('Back');
      if (back) { await clickAt(back.bounds, style + ' Back'); }
      else { await page.locator('#theCanvas').focus(); await page.keyboard.press('Escape', { delay: 100 }); }
      await until(async () => !(await hangar(page)).loadoutOpen, style + ' back to the hangar', 30000);
      await delay(1200);
      const hangarFile = `hangar-${style.toLowerCase()}-${width}x${height}.jpg`;
      await page.screenshot({ path: path.join(out, hangarFile), type: 'jpeg', quality: 80 });
      const atHangar = await hangar(page);
      captures.push({ file: hangarFile, style, width, height, screen: atHangar.screen, hover: atHangar.hover,
        boardState: atHangar.boardState, developments: atHangar.developments, verbs: atHangar.verbs.length,
        money: atHangar.money, hull: atHangar.hull, parts: atHangar.parts, status: atHangar.status });
    }

    // ── launch from the game window, fly, dock, and see the hangar again ────
    await page.setViewportSize({ width: 1920, height: 1080 });
    await page.getByRole('button', { name: 'Modern', exact: true }).click();
    await delay(800);
    await clickAt(await verb('Launch'), 'launch pane');
    await page.locator('#online-panel[data-state="Flight"]').waitFor({ timeout: 180000 });
    await page.waitForFunction(() => gameMenu.metrics().flight.frames > 3, null, { timeout: 120000 });
    const flying = await page.evaluate(() => gameMenu.metrics());
    assert.ok(flying.flight.flying, 'the pilot launched and is flying');
    assert.equal((await hangar(page)).visible, false, 'the hangar is gone once the pilot launches');
    // Fly with the ordinary keys, then dock at the home station through the toolbar.
    await page.locator('#theCanvas').focus();
    await page.keyboard.press('Equal', { delay: 200 });
    await page.keyboard.press('Equal', { delay: 200 });
    await delay(1000);
    await page.locator('#flight-dock').click();
    await until(async () => (await page.evaluate(() => gameMenu.metrics())).online.state === 'Hangar', 'docked again', 300000);
    await until(async () => (await hangar(page)).visible, 'the hangar came back after docking', 60000);
    const redocked = await hangar(page);
    assert.equal(redocked.screen, 'Hangar', 'docking returns to the hangar, not the loadout');
    assert.ok(redocked.visible, 'the hangar is drawn again');

    // ── the run WITHOUT ?dev=1: no HTML launch controls at all ─────────────
    const plain = await browser.newContext({ viewport: { width: 1920, height: 1080 }, ignoreHTTPSErrors: true });
    const bare = await plain.newPage();
    bare.setDefaultTimeout(120000);
    await plain.route('**/*', route => {
      const u = new URL(route.request().url());
      if (!['localhost', '127.0.0.1'].includes(u.hostname)) throw Error('Non-loopback request: ' + u.origin);
      return route.continue();
    });
    bare.on('pageerror', error => errors.push(error.message));
    await bare.goto(menu(''));
    await bare.waitForFunction(() => window.preview?.measurements.firstFrameMs != null);
    await bare.locator('#online-open').click();
    await bare.locator('#join-password').waitFor();
    await type(bare, '#join-password', password);
    await bare.locator('.online-join').first().click();
    await bare.locator('.online-side').first().click();
    await bare.locator('#online-panel[data-state="Hangar"]').waitFor({ timeout: 180000 });
    await until(async () => (await hangar(bare)).visible, 'the hangar is drawn without ?dev=1', 60000);
    noDev.dev = await bare.evaluate(() => gameMenu.metrics().dev);
    noDev.launch = await bare.locator('#online-launch').count();
    noDev.status = await bare.locator('#launch-status').count();
    noDev.reason = await bare.locator('#launch-reason').count();
    noDev.canvasNote = await bare.locator('#hangar-canvas-note').count();
    noDev.pose = await bare.locator('#online-pose').count();
    noDev.hangar = await hangar(bare);
    assert.equal(noDev.dev, false, 'the dev switch is off on a plain visit');
    assert.equal(noDev.launch, 0, '#online-launch is not rendered without ?dev=1');
    assert.equal(noDev.status, 0, '#launch-status is not rendered in the Hangar state without ?dev=1');
    assert.equal(noDev.reason, 0, '#launch-reason is not rendered without ?dev=1');
    assert.equal(noDev.pose, 0, '#online-pose stays a dev control');
    assert.ok(noDev.hangar.visible, 'the canvas hangar is the interface for a player');
    assert.ok(noDev.hangar.offeredHulls > 0, 'the player still has the server\'s hulls');
    // And it launches without any HTML control at all.
    assert.equal(await bare.evaluate(() => gameMenu.launch()), 'window', 'the hook uses the game window');
    await bare.locator('#online-panel[data-state="Flight"]').waitFor({ timeout: 180000 });
    await bare.waitForFunction(() => gameMenu.metrics().flight.frames > 3, null, { timeout: 120000 });
    noDev.flying = (await bare.evaluate(() => gameMenu.metrics())).flight.flying;
    assert.ok(noDev.flying, 'the game window launched the pilot with no HTML control');
    // Still a player's Flight panel: nothing else was hidden by this task.
    noDev.playerControls = {
      contact: await bare.locator('#flight-contact').count(),
      watch: await bare.locator('#flight-watch').count(),
      dock: await bare.locator('#flight-dock').count(),
      close: await bare.locator('#online-close').count(),
    };
    for (const [name, count] of Object.entries(noDev.playerControls))
      assert.equal(count, 1, '#' + name + ' stays a player control');
    await plain.close();

    const captureBytes = captures.reduce((sum, c) => sum + fs.statSync(path.join(out, c.file)).size, 0);
    assert.ok(captureBytes <= 2 * 1024 * 1024, 'capture set is at most 2 MiB (' + captureBytes + ' bytes)');
    assert.deepEqual(errors, [], 'no page errors');
    fs.writeFileSync(path.join(out, 'browser-hangar.json'), JSON.stringify({
      generatedAt: new Date().toISOString(), callsign, startHull, cases, captures, captureBytes, noDev,
      host: { size: state().battle.size, targetSector: state().battle.targetSector ?? null },
    }, null, 2) + '\n');
    console.log(JSON.stringify({ captures: captures.length, captureBytes, noDev },
      (key, value) => key === 'hangar' ? undefined : value, 2));
  } catch (error) {
    fs.mkdirSync(out, { recursive: true });
    fs.writeFileSync(path.join(out, 'failure.json'), JSON.stringify({
      error: error.message, stack: error.stack, errors, cases, captures, noDev,
    }, null, 2) + '\n');
    throw error;
  } finally {
    await browser.close();
  }
}
run().catch(error => { console.error(error); process.exit(1); });
