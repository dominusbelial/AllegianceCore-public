// Loopback-only Chromium qualification. Dependencies are pinned in the report and installed under task scratch.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const zlib = require('node:zlib');
const { chromium: playwright, firefox } = require('../.scratch/p1047/npm/node_modules/playwright');
const base = 'http://127.0.0.1:8047/play/preview/';
const output = path.resolve('docs/reports/BROWSER_SEAMS_P1047_assets');
const percentile = (a, p) => [...a].sort((x, y) => x - y)[Math.floor((a.length - 1) * p)];
const timing = a => ({ median: percentile(a, .5), p95: percentile(a, .95), p99: percentile(a, .99) });
let chromium;
function pngFirstRgb(filename) {
  const bytes = fs.readFileSync(filename); const chunks = []; let color;
  for (let offset = 8; offset < bytes.length;) {
    const length = bytes.readUInt32BE(offset); const type = bytes.toString('ascii', offset + 4, offset + 8);
    if (type === 'IHDR') color = bytes[offset + 17];
    if (type === 'IDAT') chunks.push(bytes.subarray(offset + 8, offset + 8 + length));
    offset += length + 12;
  }
  assert.ok([2, 6].includes(color), 'Expected RGB/RGBA canvas PNG');
  // All PNG row filters use zero for the missing left/up neighbors of pixel (0,0).
  return [...zlib.inflateSync(Buffer.concat(chunks)).subarray(1, 4)];
}
async function launch() {
  return playwright.launch({ executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content'].includes(a)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost'] });
}
function diagnostics(page) {
  const errors = [], logs = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { logs.push(m.text()); if (m.type() === 'error') errors.push(m.text()); });
  return { errors, logs };
}
async function drawn(page) {
  await page.waitForFunction(() => window.preview?.measurements.firstFrameMs !== null && window.preview?.measurements.firstFrameMs !== undefined, null, { timeout: 30000 });
  assert.equal(await page.locator('#load-panel').isVisible(), false);
}
async function press(page, key, count = 1) {
  for (let i = 0; i < count; i++) {
    await page.keyboard.down(key); await page.waitForTimeout(55);
    await page.keyboard.up(key); await page.waitForTimeout(55);
  }
}
async function options(page, game = false) {
  await press(page, 'ArrowDown', 4); await press(page, 'Enter');
  assert.equal((await page.evaluate(() => gameMenu.metrics())).screen, 'Settings');
  assert.equal((await page.evaluate(() => gameMenu.metrics())).optionsPage, 'Root');
  if (game) {
    await press(page, 'ArrowDown', 2); await press(page, 'Enter');
    assert.equal((await page.evaluate(() => gameMenu.metrics())).optionsPage, 'Game');
  }
}
async function profile(page) {
  return page.evaluate
(async () => {
    const request = indexedDB.open('allegiance-menu-v2', 1);
    const database = await new Promise((resolve, reject) => { request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
    const snapshot = await new Promise((resolve, reject) => {
      const t = database.transaction('settings'); const r = t.objectStore('settings').get('preferences');
      r.onsuccess = () => resolve(r.result || {}); r.onerror = () => reject(r.error);
    });
    database.close(); return snapshot;
  });
}
async function configuredPreference(style) {
  if (style === 'Modern') return undefined;
  const browser = await launch();
  try {
    const context = await browser.newContext({ viewport: { width: 800, height: 480 } });
    const page = await context.newPage(); await page.goto(base + 'menu.html'); await drawn(page);
    await page.getByRole('button', { name: style, exact: true }).click();
    await page.waitForFunction(() => !gameMenu.metrics().settingsPending);
    const state = await context.storageState({ indexedDB: true });
    // Restore only real saved settings into a fresh browser; immutable bytes and
    // residency metadata stay cold. No framework/artwork/menu output is seeded.
    for (const origin of state.origins)
      for (const database of origin.indexedDB || [])
        for (const store of database.stores)
          if (store.name !== 'settings') store.records = [];
    return state;
  } finally { await browser.close(); }
}
async function measure(viewport, style) {
  const storageState = await configuredPreference(style);
  const browser = await launch();
  try {
    const context = await browser.newContext({ viewport, deviceScaleFactor: 1, acceptDownloads: true, storageState });
    const page = await context.newPage(); const diagnostic = diagnostics(page);
    const cdp = await context.newCDPSession(page); await cdp.send('Network.enable');
    let bytes = 0, requests = 0, cached = 0, encodings = {}, urls = [];
    cdp.on('Network.requestWillBeSent', e => { requests++; urls.push(e.request.url); });
    cdp.on('Network.loadingFinished', e => bytes += e.encodedDataLength);
    cdp.on('Network.responseReceived', e => {
      if (e.response.fromDiskCache) cached++;
      const value = e.response.headers['Content-Encoding'] || e.response.headers['content-encoding'] || 'identity';
      encodings[value] = (encodings[value] || 0) + 1;
    });
    await page.goto(base + 'menu.html'); await drawn(page);
    await page.waitForFunction(() => preview.measurements.cpu.length >= 660);
    const cold = await page.evaluate(() => ({ firstFrameMs: preview.measurements.firstFrameMs,
      cpuMs: preview.measurements.cpu.slice(60, 660), rafMs: preview.measurements.interval.slice(60, 660),
      managed: gameMenu.metrics(), resources: performance.getEntriesByType('resource').map(r => ({ name: new URL(r.name).pathname, transferSize: r.transferSize, encodedBodySize: r.encodedBodySize })),
      renderer: (() => { const gl = document.getElementById('theCanvas').getContext('webgl2') || document.getElementById('theCanvas').getContext('webgl'); const debug = gl.getExtension('WEBGL_debug_renderer_info'); return debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER); })() }));
    cold.cpuMs = timing(cold.cpuMs); cold.rafMs = timing(cold.rafMs);
    Object.assign(cold, { transferredBytes: bytes, requests, cachedResponses: cached, encodings: { ...encodings } });
    assert.equal(cold.managed.style, style); assert.equal(cold.managed.gameStyle, style); assert.equal(cold.managed.screen, 'Main');
    assert.ok(bytes <= 8 * 1024 * 1024, 'First-frame/cold transfer budget exceeded');
    assert.equal(diagnostic.errors.length, 0, diagnostic.errors.join('\n'));
    const stem = `${style.toLowerCase()}-${viewport.width}x${viewport.height}`;
    await page.screenshot({ path: path.join(output, `intro-${stem}.png`) });
    bytes = requests = cached = 0; encodings = {}; urls = [];
    await page.reload(); await drawn(page); await page.waitForTimeout(100);
    const warm = { transferredBytes: bytes, requests, cachedResponses: cached, encodings: { ...encodings },
      firstFrameMs: await page.evaluate(() => preview.measurements.firstFrameMs), assetRequests: urls.filter(u => u.includes('/packs/')).length };
    assert.equal(warm.assetRequests, 0, 'Warm boot should use the verified persistent cache');
    await options(page); await page.screenshot({ path: path.join(output, `options-${stem}.png`) });
    await press(page, 'ArrowDown', 2); await press(page, 'Enter');
    assert.equal((await page.evaluate(() => gameMenu.metrics())).invertMouseY, false);
    await press(page, 'y');
    await page.waitForFunction(() => gameMenu.metrics().invertMouseY === true && !gameMenu.metrics().settingsPending);
    const saved = await profile(page);
    assert.equal(JSON.parse(saved['player.json']).InvertMouseY, true);
    assert.ok(!/password|access.?token|refresh.?token|secret|private.?key/i.test(JSON.stringify(saved)));
    // Exercise the real style button. Both the style and the Options toggle must survive a reload.
    await page.getByRole('button', { name: style, exact: true }).click();
    await page.waitForFunction(() => !gameMenu.metrics().settingsPending);
    await page.reload(); await drawn(page);
    assert.equal((await page.evaluate(() => gameMenu.metrics())).invertMouseY, true);
    assert.equal((await page.evaluate(() => gameMenu.metrics())).style, style);
    await options(page, true);
    await page.screenshot({ path: path.join(output, `persisted-${stem}.png`) });
    if (style === 'Modern') {
      await page.reload(); await drawn(page);
      await press(page, 'Enter'); assert.equal((await page.evaluate(() => gameMenu.metrics())).screen, 'SinglePlayer');
      await press(page, 'ArrowDown'); await press(page, 'Enter');
      assert.equal((await page.evaluate(() => gameMenu.metrics())).screen, 'Unavailable');
      await page.screenshot({ path: path.join(output, `skirmish-unavailable-${viewport.width}x${viewport.height}.png`) });
      await page.reload(); await drawn(page);
      await page.getByRole('button', { name: 'Enable menu sound', exact: true }).click();
      assert.equal((await page.evaluate(() => gameMenu.metrics())).menuSoundEnabled, true);
      const download = page.waitForEvent('download');
      await page.getByRole('button', { name: 'Download screenshot', exact: true }).click();
      const file = await download; const filename = path.resolve(`.scratch/p1047/download-${viewport.width}x${viewport.height}.png`);
      await file.saveAs(filename);
      assert.deepEqual(pngFirstRgb(filename), [5, 12, 20], 'Download must contain the drawn menu, not a discarded black buffer');
    }
    assert.equal(diagnostic.errors.length, 0, diagnostic.errors.join('\n'));
    return { viewport, style, browser: browser.version(), cold, warm, logs: diagnostic.logs, errors: diagnostic.errors };
  } finally { await browser.close(); }
}
async function states(viewport) {
  const loadingBrowser = await launch();
  try {
    const page = await loadingBrowser.newPage({ viewport });
    let release; const hold = new Promise(resolve => release = resolve); let held = false;
    await page.route('**/packs/*', async route => { if (!held) { held = true; await hold; } await route.continue(); });
    await page.goto(base + 'menu.html');
    await page.waitForFunction(() => document.querySelector('#load-panel')?.textContent.includes('Loading content/'));
    await page.screenshot({ path: path.join(output, `loading-${viewport.width}x${viewport.height}.png`) });
    release(); await drawn(page);
  } finally { await loadingBrowser.close(); }
  const failureBrowser = await launch();
  try {
    const retry = await failureBrowser.newPage({ viewport }); let corrupt = true, corrupted = false;
    await retry.route('**/packs/*', async route => {
      if (!corrupt || corrupted) return route.continue();
      corrupted = true;
      const response = await route.fetch(); const body = Buffer.from(await response.body()); body[body.length - 1] ^= 1;
      const headers = { ...response.headers(), 'content-type': 'application/octet-stream' }; delete headers['content-encoding']; delete headers['content-length'];
      await route.fulfill({ status: 200, headers, body });
    });
    await retry.goto(base + 'menu.html'); await retry.getByRole('button', { name: 'Retry', exact: true }).waitFor();
    const errorText = await retry.locator('#load-panel').innerText();
    assert.match(errorText, /verification failed/i);
    await retry.screenshot({ path: path.join(output, `asset-failure-${viewport.width}x${viewport.height}.png`) });
    corrupt = false; await retry.getByRole('button', { name: 'Retry', exact: true }).click(); await drawn(retry);
    return { viewport, loading: true, corruptRejected: true, retryRecovered: true, errorText };
  } finally { await failureBrowser.close(); }
}
async function previewSmoke() {
  const browser = await launch();
  try {
    const page = await browser.newPage({ viewport: { width: 800, height: 480 } });
    const diagnostic = diagnostics(page);
    await page.goto(base); await drawn(page);
    assert.equal(await page.getByRole('heading', { level: 1 }).textContent(), 'Allegiance browser preview — not the game yet');
    await page.getByRole('button', { name: 'Rotate right' }).click();
    await page.getByRole('button', { name: 'Zoom in' }).click();
    assert.equal(diagnostic.errors.length, 0, diagnostic.errors.join('\n'));
    return { unchangedHeader: true, originalScoutDrawn: true, controlsResponded: true, errors: diagnostic.errors };
  } finally { await browser.close(); }
}
async function pilotText() {
  const browser = await launch();
  try {
    const page = await browser.newPage({ viewport: { width: 800, height: 480 } });
    const diagnostic = diagnostics(page);
    const waitPilot = async expected => {
      for (let attempt = 0; attempt < 120; attempt++) {
        const saved = await profile(page);
        if (saved['player.json'] && JSON.parse(saved['player.json']).Pilot === expected) return;
        await page.waitForTimeout(25);
      }
      assert.fail('Pilot edit did not persist');
    };
    await page.goto(base + 'menu.html'); await drawn(page);
    assert.equal((await page.evaluate(() => gameMenu.metrics())).style, 'Modern');
    await page.locator('canvas').click({ position: { x: 10, y: 200 } });
    await page.keyboard.type('x'); await waitPilot('pilotx');
    await press(page, 'Tab'); await page.keyboard.type('z'); await page.waitForTimeout(100);
    assert.equal(JSON.parse((await profile(page))['player.json']).Pilot, 'pilotx');
    await press(page, 'Tab'); await press(page, 'Backspace'); await waitPilot('pilot');
    const maximumName = 'pilot' + 'a'.repeat(19);
    await page.keyboard.type('a'.repeat(30)); await waitPilot(maximumName);
    await page.reload(); await drawn(page);
    assert.equal((await page.evaluate(() => gameMenu.metrics())).style, 'Modern', 'Creating player preferences must retain the initial HUD choice');
    await page.keyboard.type('b'); await page.waitForTimeout(100);
    assert.equal(JSON.parse((await profile(page))['player.json']).Pilot, maximumName);
    await page.locator('canvas').focus();
    await page.keyboard.down('Shift'); await press(page, 'Tab'); await page.keyboard.up('Shift');
    assert.equal(await page.evaluate(() => document.activeElement.tagName), 'BUTTON');
    await press(page, 'Enter');
    assert.equal((await page.evaluate(() => gameMenu.metrics())).screen, 'Main', 'Toolbar Enter must not also activate the game menu');
    await press(page, 'Tab');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'theCanvas');
    await press(page, 'Backspace'); await page.keyboard.type('b');
    await waitPilot(maximumName.slice(0, -1) + 'b');
    assert.deepEqual(diagnostic.errors, []);
    return { typedAndSaved: true, unfocusedInputIgnored: true, backspace: true,
      maximumPersistedLength: maximumName.length, reloadPreserved: true, initialHudChoicePreserved: true,
      toolbarKeyboardRoundTrip: true, toolbarEnterKeptMenu: true, inputResumedAfterToolbar: true, errors: diagnostic.errors };
  } finally { await browser.close(); }
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  chromium = (await import('../.scratch/p1047/npm/node_modules/@sparticuz/chromium/build/index.js')).default;
  if (process.argv.includes('--text-only')) { console.log(JSON.stringify(await pilotText())); return; }
  const results = { measurements: [], states: [], textInput: null, preview: null, firefox: null };
  try {
    for (const viewport of [{ width: 1920, height: 1080 }, { width: 800, height: 480 }]) {
      for (const style of ['Classic', 'Modern']) results.measurements.push(await measure(viewport, style));
      results.states.push(await states(viewport));
    }
    results.textInput = await pilotText();
    results.preview = await previewSmoke();
    try { const browser = await firefox.launch({ headless: true }); await browser.close(); results.firefox = 'Executable available; no Firefox rendering measurements collected'; }
    catch { results.firefox = 'Skipped: Firefox executable unavailable; no browser-CDN downloads attempted'; }
  } finally { fs.writeFileSync(path.join(output, 'browser-metrics.json'), JSON.stringify(results, null, 2) + '\n'); }
})().catch(error => { console.error(error); process.exitCode = 1; });
