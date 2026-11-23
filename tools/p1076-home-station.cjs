// NPM_DIRECTORY HOST_STATE EVIDENCE_DIRECTORY GENERATION [baseline]
// Fresh guest contexts; real keyboard turns; loopback host only, no installs.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const [npm, stateDir, out, generation] = process.argv.slice(2, 6).map(x => path.resolve(x));
const baseline = process.argv[6] === 'baseline';
const { chromium: playwright } = require(path.join(npm, 'node_modules/playwright'));
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const state = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json')));
const manifest = JSON.parse(fs.readFileSync(path.join(generation, 'battle-manifest.json')));
const resident = JSON.parse(fs.readFileSync(path.join(generation, 'join-manifest.json')));
const assets = new Map([...resident.assets, ...manifest.assets].map(a => [a.source, a]));
const families = new Map();
function family(key) {
  if (key.startsWith('station-')) return 'stations';
  if (key.startsWith('hull-')) return 'hulls';
  if (key.includes('-rock-')) return 'rocks';
  if (key.includes('-planet-')) return 'planets';
  if (key.includes('-globe-')) return 'globes';
  return 'scene';
}
for (const pack of manifest.packs) for (const source of pack.sources) {
  const asset = assets.get(source);
  if (asset && !families.has(asset.sha256)) families.set(asset.sha256,
    /^bgrnd/.test(source) ? 'rocks' : /^nebplnt/.test(source) ? 'planets' :
    /^(?:environment|globe)/.test(source) ? 'globes' : family(pack.key));
}
async function until(test, label, timeout = 120000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await test()) return; await wait(50); }
  throw Error('Timeout: ' + label);
}
async function main() {
  fs.mkdirSync(out, { recursive: true });
  const chromium = (await import(require('node:url').pathToFileURL(path.join(npm, 'node_modules/@sparticuz/chromium/build/index.js')).href)).default;
  const browser = await playwright.launch({ executablePath: process.env.CHROMIUM_PATH || await chromium.executablePath(), headless: true,
    args: [...chromium.args.filter(a => !['--disable-web-security', '--allow-running-insecure-content', '--single-process'].includes(a)),
      '--ignore-certificate-errors', '--proxy-server=http://127.0.0.1:9', '--proxy-bypass-list=localhost;127.0.0.1'] });
  const evidence = { baseline, browser: browser.version(), map: 'SingleRing seed 1073', civIds: state().civIds,
    postLaunchPackHoldMs: 5500, runs: [] };
  try {
    for (const side of [0, 1]) for (const style of ['Classic', 'Modern']) {
      const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, ignoreHTTPSErrors: true });
      await context.addInitScript(require('./p1075-frame-probe.cjs'));
      const page = await context.newPage(); page.setDefaultTimeout(120000);
      const requests = [], errors = [], consoleLines = [];
      let dockAt = 0, launchAt = 0;
      const run = { side, civilization: state().civIds[side], style, requests, errors, consoleLines };
      evidence.runs.push(run);
      await context.route('**/*', async route => {
        const url = new URL(route.request().url()); assert.ok(['localhost', '127.0.0.1'].includes(url.hostname));
        const hash = url.pathname.split('/').pop();
        if (dockAt && /^[a-f0-9]{64}$/.test(hash)) {
          const started = Date.now();
          // Model a slow first-flight network: dock-prefetched art must support
          // the look-back before any flight-only pack can arrive. Then let all
          // normal promotions cross sampled frames. P1075 separately exercises
          // its original 150 ms promotion delay without this focused hold.
          if (launchAt) await wait(Math.max(150, launchAt + 5500 - Date.now()));
          const response = await route.fetch(), body = await response.body();
          requests.push({ hash, family: families.get(hash) || 'other', started, completed: Date.now(),
            decodedBytes: body.length, networkBytes: Number(response.headers()['content-length']) || body.length,
            encoding: response.headers()['content-encoding'] || 'identity' });
          await route.fulfill({ response, body }); return;
        }
        await route.continue();
      });
      page.on('pageerror', error => errors.push(error.message));
      page.on('console', message => { consoleLines.push(message.type() + ': ' + message.text()); if (consoleLines.length > 200) consoleLines.shift(); });
      try {
        console.log(style, 'side', side, 'joining');
        await page.goto('https://localhost:8443/play/menu.html?online=1&dev=1');
        await page.locator('#guest-disclosure').waitFor();
        await page.locator('#join-password').fill(fs.readFileSync(path.join(stateDir, 'game-password'), 'utf8').trim());
        await page.locator('.online-join:enabled').first().click();
        await page.locator('#online-panel[data-state="Sides"]').waitFor();
        dockAt = Date.now(); await page.locator('.online-side').nth(side).click();
        await page.locator('#online-panel[data-state="Hangar"]').waitFor();
        await page.waitForFunction(() => gameMenu.hangar().visible && gameMenu.hangar().launchArt);
        await page.getByRole('button', { name: style, exact: true }).click();
        const id = (await page.evaluate(() => gameMenu.metrics())).online.shipId;
        await until(() => state().ships.some(s => s.Id === id && s.StationId != null), 'authoritative dock');
        const own = () => state().ships.find(s => s.Id === id);
        run.homeId = own().StationId; run.docked = { stationId: run.homeId, side: own().SideId };
        // Ten actual seconds in the hangar, including fitting/animation time.
        await wait(10000);
        run.hangarArt = await page.evaluate(() => gameMenu.metrics().flight.sectorArt);
        const home = run.hangarArt.stations.find(s => s.id === run.homeId);
        assert.ok(home, 'actual dock station appears in SectorArtMetrics');
        const pack = manifest.packs.find(p => p.key === 'station-' + home.typeId);
        run.garrison = { ...home, pack: pack.key, sources: pack.sources.map(source => {
          const asset = assets.get(source), file = path.join(generation, asset.path);
          return { source, hash: asset.sha256, raw: asset.byteSize, encoded: fs.statSync(file + '.gz').size,
            joinResident: resident.assets.some(a => a.sha256 === asset.sha256) };
        }) };
        run.hangarMs = Date.now() - dockAt;
        await page.evaluate(() => p1075Frames.enabled = true);
        // Hold a real turn key while leaving the bay, like a pilot turning as
        // soon as flight begins. Capture the server's initial exit orientation
        // independently of slow first-frame GPU uploads in software Chromium.
        await page.locator('#theCanvas').focus();
        await page.keyboard.down('ArrowLeft');
        launchAt = Date.now();
        const launched = page.waitForFunction(() => { gameMenu.launch(); return document.getElementById('online-panel')?.dataset.state === 'Flight'; }, null, { polling: 100 });
        await until(() => own().StationId == null, 'authoritative launch');
        run.forwardBefore = own().forward;
        await launched;
        run.atLaunch = await page.evaluate(id => gameMenu.metrics().flight.sectorArt.stations.find(s => s.id === id), run.homeId);
        if (!baseline) {
          await page.waitForFunction(id => gameMenu.metrics().flight.sectorArt.stations.find(s => s.id === id)?.textureResident,
            run.homeId, { timeout: Math.max(1, 5000 - (Date.now() - launchAt)), polling: 50 });
        }
        run.residentMs = Date.now() - launchAt;
        await page.locator('#theCanvas').focus(); await page.keyboard.press('Backspace', { delay: 80 });
        run.beforeTurn = await page.evaluate(id => gameMenu.metrics().flight.sectorArt.stations.find(s => s.id === id), run.homeId);
        // Turn the actual ship; no proofView, pose injection or camera override.
        try {
          // Reverse most of the launch heading with actual yaw input. The
          // screenshot records the textured station geometry exposed by it.
          await until(() => own().forward.reduce((dot, v, i) => dot + v * run.forwardBefore[i], 0) < -.75,
            'authoritative ship turns back toward home', baseline ? 15000 : Math.max(1, 4700 - (Date.now() - launchAt)));
        } finally { await page.keyboard.up('ArrowLeft'); }
        await page.waitForFunction(({ id, before }) => gameMenu.metrics().flight.sectorArt.stations.find(s => s.id === id)?.drawFrames > before,
          { id: run.homeId, before: run.beforeTurn.drawFrames }, { timeout: 1000, polling: 25 });
        run.lookBack = await page.evaluate(id => gameMenu.metrics().flight.sectorArt.stations.find(s => s.id === id), run.homeId);
        run.forwardAfter = own().forward;
        assert.ok(run.forwardBefore.some((v, i) => Math.abs(v - run.forwardAfter[i]) > .01), 'real yaw changes authoritative orientation');
        const capture = `home-${side}-${style.toLowerCase()}.jpg`;
        await page.locator('#theCanvas').screenshot({ path: path.join(out, capture), type: 'jpeg', quality: 90 });
        run.capture = capture; run.captureMs = Date.now() - launchAt;
        if (!baseline) assert.ok(run.captureMs <= 5000, 'look-back screenshot within five seconds of launch');
        console.log(style, 'side', side, 'look-back', run.captureMs, 'ms');
        await wait(Math.max(0, 30000 - (Date.now() - launchAt)));
        run.frames = await page.evaluate(() => p1075Frames);
        run.metrics30 = await page.evaluate(() => gameMenu.metrics().flight.sectorArt);
        run.hangarBytes = {}, run.first30Bytes = {};
        for (const category of ['stations', 'hulls', 'rocks', 'planets', 'globes', 'scene', 'other']) {
          const sum = (start, end) => requests.filter(r => r.family === category && r.completed >= start && r.completed < end).reduce((n, r) => n + r.networkBytes, 0);
          run.hangarBytes[category] = sum(dockAt, launchAt);
          run.first30Bytes[category] = sum(launchAt, launchAt + 30000);
        }
        for (const request of requests) { request.startedMs = request.started - launchAt; request.completedMs = request.completed - launchAt; delete request.started; delete request.completed; }
        assert.ok(run.frames.sampled > 30, 'frame probe sampled real flight');
        assert.deepEqual(run.frames.discarded, [], 'zero purple discard frames'); assert.deepEqual(run.frames.errors, []); assert.deepEqual(errors, []);
        fs.writeFileSync(path.join(out, 'home-station.json'), JSON.stringify(evidence, null, 2));
        await page.locator('#online-close').click(); await page.locator('#online-panel').waitFor({ state: 'detached' });
      } finally {
        await page.keyboard.up('ArrowLeft').catch(() => {});
        await context.unrouteAll({ behavior: 'ignoreErrors' });
        await context.close();
      }
    }
    console.log('P1076_HOME_STATION_PASS', evidence.runs.length);
  } catch (error) { evidence.failure = String(error); fs.writeFileSync(path.join(out, 'failure.json'), JSON.stringify(evidence, null, 2)); throw error; }
  finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
