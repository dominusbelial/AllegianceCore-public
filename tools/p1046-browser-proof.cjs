// Local-only proof. Install playwright and @sparticuz/chromium in .scratch/p1046/npm.
// Start the lobby on loopback with its published preview enabled, then run:
// FONTCONFIG_PATH=/tmp/fonts LD_LIBRARY_PATH=/tmp/al2023/lib node tools/p1046-browser-proof.cjs
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const http = require('node:http');
const { chromium: playwright, firefox } = require('../.scratch/p1046/npm/node_modules/playwright');
const url = 'http://127.0.0.1:8046/play/preview/';
const output = path.resolve('docs/reports/BROWSER_PREVIEW_P1046_assets');
fs.mkdirSync(output, { recursive: true });
const percentile = (values, p) => [...values].sort((a, b) => a - b)[Math.floor((values.length - 1) * p)];
const timing = values => ({ median: percentile(values, .5), p95: percentile(values, .95), p99: percentile(values, .99) });
let chromium;
async function launch() {
  return playwright.launch({ executablePath: await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content'].includes(a)),
      '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=127.0.0.1;localhost'] });
}
async function drawn(page) {
  await page.waitForFunction(() => window.preview?.measurements.firstFrameMs != null, null, { timeout: 30000 });
}
function diagnostics(page) {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  return errors;
}
async function measure(viewport) {
  const browser = await launch();
  try {
    const context = await browser.newContext({ viewport, deviceScaleFactor: 1 });
    const page = await context.newPage(); const errors = diagnostics(page);
    const cdp = await context.newCDPSession(page); await cdp.send('Network.enable');
    let bytes = 0, requests = 0, cached = 0, encodings = {};
    cdp.on('Network.requestWillBeSent', () => requests++);
    cdp.on('Network.loadingFinished', event => bytes += event.encodedDataLength);
    cdp.on('Network.responseReceived', event => {
      if (event.response.fromDiskCache) cached++;
      const encoding = event.response.headers['Content-Encoding'] || event.response.headers['content-encoding'] || 'identity';
      encodings[encoding] = (encodings[encoding] || 0) + 1;
    });
    await page.goto(url); await drawn(page);
    await page.waitForFunction(() => preview.measurements.cpu.length >= 660);
    const cold = await page.evaluate(() => ({
      firstFrameMs: preview.measurements.firstFrameMs,
      cpu: preview.measurements.cpu.slice(60, 660), interval: preview.measurements.interval.slice(60, 660),
      resources: performance.getEntriesByType('resource').map(r => ({ name: new URL(r.name).pathname, transferSize: r.transferSize, encodedBodySize: r.encodedBodySize })),
      renderer: (() => { const gl = document.getElementById('theCanvas').getContext('webgl'); const debug = gl.getExtension('WEBGL_debug_renderer_info'); return debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER); })()
    }));
    assert.equal(errors.length, 0, errors.join('\n'));
    cold.cpuMs = timing(cold.cpu); cold.rafMs = timing(cold.interval); delete cold.cpu; delete cold.interval;
    cold.transferredBytes = bytes; cold.requests = requests; cold.cachedResponses = cached; cold.encodings = encodings;
    await page.screenshot({ path: path.join(output, `chromium-loaded-${viewport.width}x${viewport.height}.png`) });
    const before = await page.locator('canvas').screenshot();
    await page.getByRole('button', { name: 'Rotate right' }).click();
    await page.getByRole('button', { name: 'Zoom in' }).click();
    const after = await page.locator('canvas').screenshot();
    assert.notDeepEqual(before, after);
    bytes = requests = cached = 0; encodings = {};
    await page.reload(); await drawn(page); await page.waitForFunction(() => preview.measurements.cpu.length >= 10);
    const warm = { transferredBytes: bytes, requests, cachedResponses: cached, encodings,
      firstFrameMs: await page.evaluate(() => preview.measurements.firstFrameMs) };
    assert.equal(errors.length, 0, errors.join('\n'));
    console.log(`Measured ${viewport.width}x${viewport.height}: cold=${cold.transferredBytes}, warm=${warm.transferredBytes}`);
    return { browser: browser.version(), viewport, cold, warm, controlsChangedCanvas: true, errors };
  } finally { await browser.close(); }
}
async function states(viewport) {
  const browser = await launch();
  try {
    // Hold an actual runtime request so the real shell remains in its loading state.
    const loading = await browser.newPage({ viewport }); let release;
    const held = new Promise(resolve => { release = resolve; });
    await loading.route('**/_framework/blazor.webassembly.js', async route => { await held; await route.continue(); });
    await loading.goto(url, { waitUntil: 'domcontentloaded' });
    await loading.getByText('Downloading C# / WebAssembly…').waitFor();
    await loading.screenshot({ path: path.join(output, `chromium-loading-${viewport.width}x${viewport.height}.png`) });
    release(); await drawn(loading);
    // Missing capabilities must not trigger a framework or artwork request.
    const fallback = await browser.newPage({ viewport }); const downloaded = [];
    fallback.on('request', request => downloaded.push(request.url()));
    await fallback.addInitScript(() => { Object.defineProperty(window, 'WebAssembly', { value: undefined }); });
    await fallback.goto(url); await fallback.locator('#fallback').waitFor();
    assert(!downloaded.some(u => /\/_framework\/|\/_content\/|\/art\//.test(u)));
    await fallback.screenshot({ path: path.join(output, `chromium-fallback-${viewport.width}x${viewport.height}.png`) });
    // Corrupt one same-length response in the browser, then prove Retry recovers.
    const retry = await browser.newPage({ viewport }); let corrupt = true;
    await retry.route('**/art/fig05.*.mdl', async route => {
      if (!corrupt) return route.continue();
      const response = await route.fetch(); const bytes = Buffer.from(await response.body()); bytes[0] ^= 1;
      await route.fulfill({ response, body: bytes, headers: { ...response.headers(), 'content-encoding': 'identity', 'content-length': String(bytes.length) } });
    });
    await retry.goto(url); await retry.getByRole('button', { name: 'Retry', exact: true }).waitFor();
    assert((await retry.locator('#load-panel').innerText()).includes('verification failed'));
    await retry.screenshot({ path: path.join(output, `chromium-error-${viewport.width}x${viewport.height}.png`) });
    corrupt = false; await retry.getByRole('button', { name: 'Retry', exact: true }).click(); await drawn(retry);
    return { viewport, capabilityRequests: downloaded.length, noBundleWithoutWasm: true, hashErrorAndRetryRecovered: true };
  } finally { await browser.close(); }
}
async function securityProof() {
  const traversalPaths = ['/play/preview/../../api/servers', '/Play/Preview/../../api/servers',
    '/play/preview/art/../secret.mdl', '/play/preview/art/%2e%2e%2fsecret.mdl', '/play/preview/%2f..%2f..%2fapi/servers'];
  for (const rawPath of traversalPaths) {
    const status = await new Promise((resolve, reject) => {
      const request = http.get({ hostname: '127.0.0.1', port: 8046, path: rawPath }, response => {
        response.resume(); response.on('end', () => resolve(response.statusCode));
      });
      request.on('error', reject);
    });
    assert.equal(status, 404, rawPath);
  }
  const browser = await launch();
  try {
    const page = await browser.newPage(); const downloaded = [];
    await page.addInitScript(() => {
      const original = HTMLCanvasElement.prototype.getContext;
      HTMLCanvasElement.prototype.getContext = function (type, ...args) { return type.startsWith('webgl') ? null : original.call(this, type, ...args); };
    });
    page.on('request', request => downloaded.push(request.url()));
    await page.goto(url); await page.locator('#fallback').waitFor();
    assert(!downloaded.some(u => /\/_framework\/|\/_content\/|\/art\//.test(u)));
    const blocked = await browser.newPage(); const errors = diagnostics(blocked);
    await blocked.route(url, async route => {
      const response = await route.fetch(); const headers = response.headers();
      headers['content-security-policy'] = headers['content-security-policy'].replace(" 'wasm-unsafe-eval'", '');
      await route.fulfill({ response, headers });
    });
    await blocked.goto(url); await blocked.locator('#runtime-error').waitFor();
    assert(errors.some(error => /wasm-unsafe-eval|WebAssembly.*Content Security Policy/.test(error)));
    return { noBundleWithoutWebGL: true, compilationBlockedWithoutWasmUnsafeEval: true, rawTraversalPathsRejected: traversalPaths };
  } finally { await browser.close(); }
}
(async () => {
  ({ default: chromium } = await import('../.scratch/p1046/npm/node_modules/@sparticuz/chromium/build/index.js'));
  const results = { measurements: [], states: [], security: null, firefox: null };
  for (const viewport of [{ width: 1920, height: 1080 }, { width: 800, height: 480 }]) {
    results.measurements.push(await measure(viewport)); results.states.push(await states(viewport));
    fs.writeFileSync(path.join(output, 'browser-metrics.json'), JSON.stringify(results, null, 2) + '\n');
  }
  results.security = await securityProof();
  let browser;
  try { browser = await firefox.launch({ headless: true }); results.firefox = { available: true, version: browser.version() }; }
  catch { results.firefox = { available: false, reason: 'Playwright Firefox executable absent. Browser-CDN downloads are outside the lane NuGet/npm/docs network allowlist.' }; }
  finally { if (browser) await browser.close(); }
  fs.writeFileSync(path.join(output, 'browser-metrics.json'), JSON.stringify(results, null, 2) + '\n');
  console.log('Browser proof passed; all browsers closed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
